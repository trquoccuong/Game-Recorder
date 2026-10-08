using System.Net;
using Odx.Capture;
using Odx.Core;
using Odx.Merge;
using Odx.Merge.Proto;
using Odx.Protocol;

namespace Odx.Games.Wc3;

/// <summary>
/// "w3gs": TCP streams are put back in order and split into W3GS messages, each decoded (UDP datagrams are one message each);
/// TCP opens/closes are listed too.
/// </summary>
public sealed class W3gsPacketDecoder : IPacketDecoder
{
    public string Protocol => "w3gs";

    /// <summary>
    /// TCP: per direction of each connection, bytes are appended in sequence order (retransmitted bytes skipped; a gap
    /// drops the half-read message and is listed as TCP_GAP) and complete W3GS messages are cut off as they arrive.
    /// </summary>
    public List<PacketRecord> Decode(IReadOnlyList<(double Utc, IpPacket Packet)> packets, IReadOnlySet<IPAddress> local,
        PacketCaptureSpec spec)
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
                if ((p.TcpFlags & 0x10) == 0) records.Add(PacketLog.Record(utc, p, local, "TCP_OPEN", [], ""));
                continue;
            }
            if ((p.TcpFlags & (IpPacket.Fin | IpPacket.Rst)) != 0)
                records.Add(PacketLog.Record(utc, p, local, (p.TcpFlags & IpPacket.Rst) != 0 ? "TCP_RESET" : "TCP_CLOSE", [], ""));
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
                records.Add(PacketLog.Record(utc, p, local, "TCP_GAP", [],
                    new System.Text.Json.Nodes.JsonObject { ["missing_bytes"] = ahead }.ToJsonString()));
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

    private static PacketRecord Decoded(double utc, IpPacket p, IReadOnlySet<IPAddress> local, byte[] message)
    {
        var (name, fields) = W3gs.Decode(message);
        return PacketLog.Record(utc, p, local, name, message, fields.ToJsonString(AgentMessage.Json));
    }
}
