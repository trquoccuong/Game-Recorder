using System.Buffers.Binary;
using System.Net;

namespace Odx.Core;

/// <param name="UtcUnixMs">Capture time on the capturing machine's clock (pktmon: the Windows clock).</param>
public sealed record CapturedFrame(double UtcUnixMs, int LinkType, byte[] Data);

/// <summary>Reads the frames of a pcapng file (Section Header, Interface Description and Enhanced Packet blocks).</summary>
public static class PcapNgReader
{
    private const uint SectionHeader = 0x0A0D0D0A, InterfaceDescription = 1, EnhancedPacket = 6;

    /// <exception cref="InvalidDataException">Not a little-endian pcapng file.</exception>
    public static List<CapturedFrame> Read(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        var interfaces = new List<(int LinkType, double TicksPerMs)>();
        var frames = new List<CapturedFrame>();
        for (int o = 0; o + 12 <= d.Length;)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 4));
            if (length < 12 || o + length > d.Length) throw new InvalidDataException($"{path}: bad block at {o}");
            var body = d.AsSpan(o + 8, length - 12);
            switch (type)
            {
                case SectionHeader when BinaryPrimitives.ReadUInt32LittleEndian(body) != 0x1A2B3C4D:
                    throw new InvalidDataException($"{path}: only little-endian pcapng is supported");
                case SectionHeader:
                    interfaces.Clear();
                    break;
                case InterfaceDescription:
                    interfaces.Add((BinaryPrimitives.ReadUInt16LittleEndian(body), TicksPerMs(body[8..])));
                    break;
                case EnhancedPacket:
                    int iface = (int)BinaryPrimitives.ReadUInt32LittleEndian(body);
                    ulong ts = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(body[8..]);
                    int captured = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[12..]);
                    var (linkType, ticksPerMs) = iface < interfaces.Count ? interfaces[iface] : (1, 1000.0);
                    frames.Add(new CapturedFrame(ts / ticksPerMs, linkType, body.Slice(20, captured).ToArray()));
                    break;
            }
            o += length;
        }
        return frames;
    }

    /// <summary>if_tsresol (option 9): 10^-n or, with the high bit set, 2^-n seconds per tick; microseconds by default.</summary>
    private static double TicksPerMs(ReadOnlySpan<byte> options)
    {
        for (int o = 0; o + 4 <= options.Length;)
        {
            int code = BinaryPrimitives.ReadUInt16LittleEndian(options[o..]), length = BinaryPrimitives.ReadUInt16LittleEndian(options[(o + 2)..]);
            if (code == 0) break;
            if (code == 9 && length == 1)
            {
                byte r = options[o + 4];
                double perSecond = (r & 0x80) != 0 ? Math.Pow(2, r & 0x7F) : Math.Pow(10, r);
                return perSecond / 1000;
            }
            o += 4 + (length + 3) / 4 * 4;
        }
        return 1000;
    }
}

/// <param name="Payload">The TCP or UDP payload.</param>
/// <param name="TcpSequence">Sequence number of the payload's first byte (TCP only).</param>
public sealed record IpPacket(IPAddress Source, IPAddress Destination, string Transport, int SourcePort, int DestinationPort,
    uint TcpSequence, byte TcpFlags, byte[] Payload)
{
    public const byte Syn = 0x02, Fin = 0x01, Rst = 0x04;

    /// <summary>
    /// The TCP/UDP packet in a captured frame: Ethernet (optionally VLAN-tagged), 802.11 data frames with LLC/SNAP (what
    /// pktmon logs for Wi-Fi adapters, even under the Ethernet link type), raw IP or BSD loopback. Null for anything else.
    /// </summary>
    public static IpPacket? Parse(CapturedFrame frame)
    {
        var d = frame.Data;
        int l3 = FindNetworkLayer(d, frame.LinkType);
        if (l3 < 0 || l3 >= d.Length) return null;
        int version = d[l3] >> 4;
        IPAddress source, destination;
        int protocol, l4, end;
        if (version == 4 && d.Length >= l3 + 20)
        {
            int headerLength = (d[l3] & 0x0F) * 4;
            end = Math.Min(d.Length, l3 + BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(l3 + 2)));
            protocol = d[l3 + 9];
            source = new IPAddress(d.AsSpan(l3 + 12, 4));
            destination = new IPAddress(d.AsSpan(l3 + 16, 4));
            l4 = l3 + headerLength;
        }
        else if (version == 6 && d.Length >= l3 + 40)
        {
            end = Math.Min(d.Length, l3 + 40 + BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(l3 + 4)));
            protocol = d[l3 + 6];
            source = new IPAddress(d.AsSpan(l3 + 8, 16));
            destination = new IPAddress(d.AsSpan(l3 + 24, 16));
            l4 = l3 + 40;
        }
        else return null;

        if (protocol == 6 && end >= l4 + 20)
        {
            int dataOffset = (d[l4 + 12] >> 4) * 4;
            return new IpPacket(source, destination, "tcp", BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(l4)),
                BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(l4 + 2)), BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(l4 + 4)),
                d[l4 + 13], d[Math.Min(end, l4 + dataOffset)..end]);
        }
        if (protocol == 17 && end >= l4 + 8)
            return new IpPacket(source, destination, "udp", BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(l4)),
                BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(l4 + 2)), 0, 0, d[(l4 + 8)..end]);
        return null;
    }

    private static int FindNetworkLayer(byte[] d, int linkType)
    {
        switch (linkType)
        {
            case 0 when d.Length > 4:  // BSD loopback: 4-byte address family
                return 4;
            case 101 or 228 or 229:  // raw IP
                return 0;
        }
        if (d.Length >= 14)
        {
            int etherType = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(12));
            if (etherType is 0x0800 or 0x86DD) return 14;
            if (etherType == 0x8100 && d.Length >= 18) return 18;
        }
        // 802.11 data frame: 24-byte header (+6 with four addresses, +2 QoS, +4 HT control), then LLC/SNAP and the EtherType.
        if (d.Length >= 32 && ((d[0] >> 2) & 3) == 2)
        {
            int header = 24 + ((d[1] & 3) == 3 ? 6 : 0);
            if ((d[0] & 0x80) != 0) header += 2 + ((d[1] & 0x80) != 0 ? 4 : 0);
            if (d.Length >= header + 8 && d[header] == 0xAA && d[header + 1] == 0xAA && d[header + 2] == 0x03)
                return header + 8;
        }
        return -1;
    }
}
