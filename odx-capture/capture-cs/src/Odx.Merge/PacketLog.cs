using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Odx.Capture;
using Odx.Core;
using Odx.Merge.Proto;
using Odx.Protocol;

namespace Odx.Merge;

/// <summary>
/// packets.bin and packets.txt (spec v3 §2.3) from a player's packets.pcapng, decoded by the game's <see cref="IPacketDecoder"/>
/// (Warcraft III, CS:GO); without one the traffic is kept raw: one record per UDP/TCP payload. Times go onto the
/// orchestrator's clock with the player's measured offset (pcapng holds the Windows clock).
/// </summary>
public static class PacketLog
{
    public const string BinFile = "packets.bin", TextFile = "packets.txt";

    public const string TextHeader =
        "# capture_time_utc\tserver_time_utc\tdirection\ttransport\tlocal_endpoint\tremote_endpoint\tpeer\tbytes\tmessage\tdecoded_json";

    /// <param name="serverMinusLocalMs">Orchestrator clock − this machine's Windows clock (player.json clock_sync.offset_ms).</param>
    /// <param name="decoders">The games' decoders; the profile's network.protocol picks one, none keeps the payloads raw.</param>
    /// <returns>How many records were written.</returns>
    public static int Write(string pcapng, IReadOnlyCollection<IPAddress> localAddresses, PacketCaptureSpec spec, double serverMinusLocalMs,
        FileHeader header, string binPath, string textPath, IEnumerable<IPacketDecoder> decoders)
    {
        var packets = PcapNgReader.Read(pcapng)
            .Select(f => (Frame: f, Packet: IpPacket.Parse(f)))
            .Where(p => p.Packet is not null)
            .OrderBy(p => p.Frame.UtcUnixMs)
            .Select(p => (p.Frame.UtcUnixMs, Packet: p.Packet!))
            .ToList();
        var local = localAddresses.ToHashSet();
        var records = decoders.FirstOrDefault(d => d.Protocol == spec.Protocol)?.Decode(packets, local, spec) ?? RawRecords(packets, local);
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

    private static List<PacketRecord> RawRecords(List<(double Utc, IpPacket Packet)> packets, IReadOnlySet<IPAddress> local) =>
        packets.Where(p => p.Packet.Payload.Length > 0)
            .Select(p => Record(p.Utc, p.Packet, local, "", p.Packet.Payload, ""))
            .ToList();

    /// <summary>One record for a game's <see cref="IPacketDecoder"/>: its direction and endpoints from this machine's addresses.</summary>
    public static PacketRecord Record(double utc, IpPacket p, IReadOnlySet<IPAddress> local, string message, byte[] payload, string decoded)
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

    /// <summary>The addresses the player's agent recorded with the capture (packets_local.json).</summary>
    public static List<IPAddress> ReadLocalAddresses(string path) =>
        File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path))?["addresses"]?.AsArray()
                .Select(a => IPAddress.TryParse((string?)a, out var ip) ? ip : null).OfType<IPAddress>().ToList() ?? []
            : [];
}
