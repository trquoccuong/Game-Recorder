using System.Net;
using Odx.Capture;
using Odx.Core;
using Odx.Merge;
using Odx.Merge.Proto;
using Odx.Protocol;

namespace Odx.Games.Csgo;

/// <summary>"csgo": one decoder per direction of each connection (its reliable streams span datagrams); a record per netmessage.</summary>
public sealed class CsgoPacketDecoder : IPacketDecoder
{
    public string Protocol => "csgo";

    public List<PacketRecord> Decode(IReadOnlyList<(double Utc, IpPacket Packet)> packets, IReadOnlySet<IPAddress> local,
        PacketCaptureSpec spec)
    {
        var serverPorts = spec.UdpPorts.ToHashSet();
        var records = new List<PacketRecord>();
        var decoders = new Dictionary<string, CsgoChannelDecoder>();
        foreach (var (utc, p) in packets.Where(p => p.Packet.Transport == "udp" && p.Packet.Payload.Length > 0))
        {
            string key = $"{p.Source}:{p.SourcePort}>{p.Destination}:{p.DestinationPort}";
            if (!decoders.TryGetValue(key, out var decoder)) decoders[key] = decoder = new CsgoChannelDecoder(spec.Version);
            foreach (var m in decoder.Decode(p.Payload, serverPorts.Contains(p.SourcePort)))
                records.Add(PacketLog.Record(utc, p, local, m.Name, m.Bytes, m.Fields.ToJsonString(AgentMessage.Json)));
        }
        return records;
    }
}
