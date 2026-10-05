using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Odx.Core;
using Odx.Session.Proto;

namespace Odx.Session;

/// <summary>
/// packets.bin and packets.txt (spec v3 §2.3) from a player's packets.pcapng. "w3gs": TCP streams are put back in order and
/// split into W3GS messages, each decoded (UDP datagrams are one message each); TCP opens/closes are listed too. Any other
/// protocol is kept raw: one record per UDP/TCP payload. Times go onto the orchestrator's clock with the player's measured
/// offset (pcapng holds the Windows clock).
/// </summary>
public static class PacketLog
{
    public const string BinFile = "packets.bin", TextFile = "packets.txt";

    public const string TextHeader =
        "# capture_time_utc\tserver_time_utc\tdirection\ttransport\tlocal_endpoint\tremote_endpoint\tpeer\tbytes\tmessage\tdecoded_json";

    /// <param name="serverMinusLocalMs">Orchestrator clock − this machine's Windows clock (player.json clock_sync.offset_ms).</param>
    /// <returns>How many records were written.</returns>
    public static int Write(string pcapng, IReadOnlyCollection<IPAddress> localAddresses, PacketCaptureSpec spec, double serverMinusLocalMs,
        FileHeader header, string binPath, string textPath)
    {
        var packets = PcapNgReader.Read(pcapng)
            .Select(f => (Frame: f, Packet: IpPacket.Parse(f)))
            .Where(p => p.Packet is not null)
            .OrderBy(p => p.Frame.UtcUnixMs)
            .Select(p => (p.Frame.UtcUnixMs, Packet: p.Packet!))
            .ToList();
        var local = localAddresses.ToHashSet();
        var records = spec.Protocol switch
        {
            "w3gs" => W3gsRecords(packets, local),
            "csgo" => CsgoRecords(packets, local, spec.UdpPorts.ToHashSet(), spec.Version),
            _ => RawRecords(packets, local),
        };
        foreach (var r in records) r.ServerTimeUs = (long)Math.Round((r.LocalTimeUs / 1000.0 + serverMinusLocalMs) * 1000);
        WriteRecords(records, header, binPath, textPath);
        return records.Count;
    }

    /// <summary>packets.bin and packets.txt from records that already carry their server time.</summary>
    public static void WriteRecords(IEnumerable<PacketRecord> records, FileHeader header, string binPath, string textPath)
    {
        using var bin = File.Create(binPath);
        header.RecordType = PacketRecord.Descriptor.FullName;
        header.WriteDelimitedTo(bin);
        using var text = new StreamWriter(textPath, false, new UTF8Encoding(false)) { NewLine = "\n" };
        text.WriteLine(TextHeader);
        foreach (var r in records)
        {
            r.WriteDelimitedTo(bin);
            text.WriteLine(string.Join('\t', Iso(r.LocalTimeUs), Iso(r.ServerTimeUs), r.Direction == PacketRecord.Types.Direction.Out ? "OUT" : r.Direction == PacketRecord.Types.Direction.In ? "IN" : "?",
                r.Transport, r.LocalEndpoint, r.RemoteEndpoint, Peer(r), r.Payload.Length.ToString(CultureInfo.InvariantCulture), r.Message,
                r.DecodedJson));
        }
    }

    /// <summary>The other side as a session player id, else its SteamID64 ("steam:…"); empty on a player's own capture.</summary>
    private static string Peer(PacketRecord r) =>
        r.PeerPlayerId.Length > 0 ? r.PeerPlayerId : r.PeerId.Length > 0 ? "steam:" + r.PeerId : "";

    public static (FileHeader Header, List<PacketRecord> Records) Read(string binPath)
    {
        using var input = File.OpenRead(binPath);
        var header = FileHeader.Parser.ParseDelimitedFrom(input);
        var records = new List<PacketRecord>();
        while (input.Position < input.Length) records.Add(PacketRecord.Parser.ParseDelimitedFrom(input));
        return (header, records);
    }

    private static List<PacketRecord> RawRecords(List<(double Utc, IpPacket Packet)> packets, HashSet<IPAddress> local) =>
        packets.Where(p => p.Packet.Payload.Length > 0)
            .Select(p => Record(p.Utc, p.Packet, local, "", p.Packet.Payload, ""))
            .ToList();

    /// <summary>
    /// TCP: per direction of each connection, bytes are appended in sequence order (retransmitted bytes skipped; a gap
    /// drops the half-read message and is listed as TCP_GAP) and complete W3GS messages are cut off as they arrive.
    /// </summary>
    private static List<PacketRecord> W3gsRecords(List<(double Utc, IpPacket Packet)> packets, HashSet<IPAddress> local)
    {
        var records = new List<PacketRecord>();
        var streams = new Dictionary<string, (uint Next, List<byte> Buffer)>();
        foreach (var (utc, p) in packets)
        {
            if (p.Transport == "udp")
            {
                foreach (byte[] message in W3gs.Split(p.Payload).Messages) records.Add(Decoded(utc, p, local, message));
                continue;
            }
            string key = $"{p.Source}:{p.SourcePort}>{p.Destination}:{p.DestinationPort}";
            if ((p.TcpFlags & IpPacket.Syn) != 0)
            {
                streams[key] = (p.TcpSequence + 1, []);
                if ((p.TcpFlags & 0x10) == 0) records.Add(Record(utc, p, local, "TCP_OPEN", [], ""));
                continue;
            }
            if ((p.TcpFlags & (IpPacket.Fin | IpPacket.Rst)) != 0)
                records.Add(Record(utc, p, local, (p.TcpFlags & IpPacket.Rst) != 0 ? "TCP_RESET" : "TCP_CLOSE", [], ""));
            if (p.Payload.Length == 0) continue;

            if (!streams.TryGetValue(key, out var stream)) stream = (p.TcpSequence, []);  // capture started mid-connection
            byte[] data = p.Payload;
            int ahead = unchecked((int)(p.TcpSequence - stream.Next));
            if (ahead < 0)
            {
                if (-ahead >= data.Length) continue;  // retransmission of bytes already taken
                data = data[-ahead..];
            }
            else if (ahead > 0)
            {
                records.Add(Record(utc, p, local, "TCP_GAP", [], new JsonObject { ["missing_bytes"] = ahead }.ToJsonString()));
                stream.Buffer.Clear();
            }
            stream.Buffer.AddRange(data);
            stream.Next = unchecked(p.TcpSequence + (uint)p.Payload.Length);
            var (messages, consumed) = W3gs.Split(stream.Buffer.ToArray());
            stream.Buffer.RemoveRange(0, consumed);
            streams[key] = stream;
            foreach (byte[] message in messages) records.Add(Decoded(utc, p, local, message));
        }
        return records;
    }

    /// <summary>One decoder per direction of each connection (its reliable streams span datagrams); a record per netmessage.</summary>
    private static List<PacketRecord> CsgoRecords(List<(double Utc, IpPacket Packet)> packets, HashSet<IPAddress> local,
        HashSet<int> serverPorts, string? version)
    {
        var records = new List<PacketRecord>();
        var decoders = new Dictionary<string, CsgoChannelDecoder>();
        foreach (var (utc, p) in packets.Where(p => p.Packet.Transport == "udp" && p.Packet.Payload.Length > 0))
        {
            string key = $"{p.Source}:{p.SourcePort}>{p.Destination}:{p.DestinationPort}";
            if (!decoders.TryGetValue(key, out var decoder)) decoders[key] = decoder = new CsgoChannelDecoder(version);
            foreach (var m in decoder.Decode(p.Payload, serverPorts.Contains(p.SourcePort)))
                records.Add(Record(utc, p, local, m.Name, m.Bytes, m.Fields.ToJsonString(AgentMessage.Json)));
        }
        return records;
    }

    private static PacketRecord Decoded(double utc, IpPacket p, HashSet<IPAddress> local, byte[] message)
    {
        var (name, fields) = W3gs.Decode(message);
        return Record(utc, p, local, name, message, fields.ToJsonString(AgentMessage.Json));
    }

    private static PacketRecord Record(double utc, IpPacket p, HashSet<IPAddress> local, string message, byte[] payload, string decoded)
    {
        bool outgoing = local.Contains(p.Source), incoming = local.Contains(p.Destination);
        return new PacketRecord
        {
            LocalTimeUs = (long)Math.Round(utc * 1000),
            Direction = outgoing ? PacketRecord.Types.Direction.Out : incoming ? PacketRecord.Types.Direction.In : PacketRecord.Types.Direction.Unknown,
            Transport = p.Transport,
            LocalEndpoint = outgoing || !incoming ? Endpoint(p.Source, p.SourcePort) : Endpoint(p.Destination, p.DestinationPort),
            RemoteEndpoint = outgoing || !incoming ? Endpoint(p.Destination, p.DestinationPort) : Endpoint(p.Source, p.SourcePort),
            Message = message,
            Payload = ByteString.CopyFrom(payload),
            DecodedJson = decoded,
        };
    }

    private static string Endpoint(IPAddress address, int port) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";

    private static string Iso(long unixUs) =>
        DateTime.UnixEpoch.AddTicks(unixUs * 10).ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);

    /// <summary>The addresses odx agent recorded with the capture (packets_local.json).</summary>
    public static List<IPAddress> ReadLocalAddresses(string path) =>
        File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path))?["addresses"]?.AsArray()
                .Select(a => IPAddress.TryParse((string?)a, out var ip) ? ip : null).OfType<IPAddress>().ToList() ?? []
            : [];
}
