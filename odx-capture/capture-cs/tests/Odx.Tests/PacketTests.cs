using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Odx.Capture;
using Odx.Core;
using Odx.Games.Wc3;
using Odx.Merge;
using Odx.Merge.Proto;
using Xunit;

namespace Odx.Tests;

/// <summary>Synthetic captures: pcapng blocks, Ethernet / 802.11 frames and W3GS messages built byte by byte.</summary>
public class PacketTests
{
    private static readonly IPAddress Me = IPAddress.Parse("10.147.20.5"), Host = IPAddress.Parse("10.147.20.9");

    /// <summary>A little-endian pcapng with one Ethernet interface (microsecond timestamps).</summary>
    private static string Pcapng(IEnumerable<(double UtcMs, byte[] Frame)> frames)
    {
        var o = new MemoryStream();
        void Block(uint type, byte[] body)
        {
            int length = 12 + (body.Length + 3) / 4 * 4;
            o.Write(BitConverter.GetBytes(type));
            o.Write(BitConverter.GetBytes(length));
            o.Write(body);
            o.Write(new byte[(4 - body.Length % 4) % 4]);
            o.Write(BitConverter.GetBytes(length));
        }
        Block(0x0A0D0D0A, [0x4D, 0x3C, 0x2B, 0x1A, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        Block(1, [1, 0, 0, 0, 0, 0, 0, 0]);
        foreach (var (utc, frame) in frames)
        {
            ulong ts = (ulong)(utc * 1000);
            var body = new byte[20 + frame.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)(ts >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), (uint)ts);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)frame.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), (uint)frame.Length);
            frame.CopyTo(body, 20);
            Block(6, body);
        }
        string path = Path.Combine(Path.GetTempPath(), $"odx-test-{Guid.NewGuid():N}.pcapng");
        File.WriteAllBytes(path, o.ToArray());
        return path;
    }

    private static byte[] Ipv4(IPAddress from, IPAddress to, byte protocol, byte[] l4)
    {
        var ip = new byte[20 + l4.Length];
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)ip.Length);
        ip[8] = 128;
        ip[9] = protocol;
        from.GetAddressBytes().CopyTo(ip, 12);
        to.GetAddressBytes().CopyTo(ip, 16);
        l4.CopyTo(ip, 20);
        return ip;
    }

    private static byte[] Ethernet(byte[] ip) => [.. new byte[12], 0x08, 0x00, .. ip];

    /// <summary>What pktmon logs for a Wi-Fi adapter: an 802.11 data header and LLC/SNAP before the IP packet.</summary>
    private static byte[] Wifi(byte[] ip) => [0x08, 0x01, .. new byte[22], 0xAA, 0xAA, 0x03, 0, 0, 0, 0x08, 0x00, .. ip];

    private static byte[] Tcp(IPAddress from, int fromPort, IPAddress to, int toPort, uint seq, byte flags, byte[] payload)
    {
        var tcp = new byte[20 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(tcp, (ushort)fromPort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(2), (ushort)toPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.AsSpan(4), seq);
        tcp[12] = 5 << 4;
        tcp[13] = flags;
        payload.CopyTo(tcp, 20);
        return Ethernet(Ipv4(from, to, 6, tcp));
    }

    private static byte[] Udp(IPAddress from, int fromPort, IPAddress to, int toPort, byte[] payload)
    {
        var udp = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(udp, (ushort)fromPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(2), (ushort)toPort);
        payload.CopyTo(udp, 8);
        return Ipv4(from, to, 17, udp);
    }

    /// <summary>A W3GS message: 0xF7, id, total length, fields.</summary>
    private static byte[] W3(byte id, params byte[][] fields)
    {
        byte[] body = fields.SelectMany(f => f).ToArray();
        return [0xF7, id, .. BitConverter.GetBytes((ushort)(body.Length + 4)), .. body];
    }

    private static byte[] U8(int v) => [(byte)v];
    private static byte[] U16(int v) => BitConverter.GetBytes((ushort)v);
    private static byte[] U32(uint v) => BitConverter.GetBytes(v);
    private static byte[] F32(float v) => BitConverter.GetBytes(v);
    private static byte[] Str(string s) => [.. Encoding.UTF8.GetBytes(s), 0];
    private static byte[] FourCC(string s) => [(byte)s[3], (byte)s[2], (byte)s[1], (byte)s[0]];

    /// <summary>The stat string encoding: a mask byte before each 7 bytes, with even bytes stored +1 and their bit cleared.</summary>
    private static byte[] EncodeStatString(byte[] data)
    {
        var o = new List<byte>();
        for (int i = 0; i < data.Length; i += 7)
        {
            var chunk = data.Skip(i).Take(7).ToArray();
            byte mask = 1;
            var encoded = new byte[chunk.Length];
            for (int j = 0; j < chunk.Length; j++)
            {
                if (chunk[j] % 2 == 0) encoded[j] = (byte)(chunk[j] + 1);
                else
                {
                    encoded[j] = chunk[j];
                    mask |= (byte)(1 << (j + 1));
                }
            }
            o.Add(mask);
            o.AddRange(encoded);
        }
        return [.. o, 0];
    }

    [Fact]
    public void Reads_ethernet_and_wifi_frames()
    {
        byte[] probe = Encoding.ASCII.GetBytes("ODXPROBE");
        string path = Pcapng([(1000, Ethernet(Udp(Me, 50000, Host, 6112, probe))), (2000.5, Wifi(Udp(Me, 50000, Host, 6112, probe)))]);
        var frames = PcapNgReader.Read(path);
        Assert.Equal([1000, 2000.5], frames.Select(f => f.UtcUnixMs));
        foreach (var packet in frames.Select(IpPacket.Parse))
        {
            Assert.Equal(("udp", 50000, 6112), (packet!.Transport, packet.SourcePort, packet.DestinationPort));
            Assert.Equal(Me, packet.Source);
            Assert.Equal(probe, packet.Payload);
        }
    }

    [Fact]
    public void Decodes_lan_discovery_with_the_encoded_game_settings()
    {
        byte[] settings = [.. U32(0x00004000), 0, .. U16(116), .. U16(84), .. U32(0x12345678), .. Str("Maps\\FrozenThrone\\(2)EchoIsles.w3x"),
            .. Str("Alice"), 0];
        var (name, f) = W3gs.Decode(W3(0x30, FourCC("W3XP"), U32(26), U32(7), U32(0xCAFE), Str("Alice's game"), Str(""),
            EncodeStatString(settings), U32(12), U32(1), U32(1), U32(11), U32(42), U16(6112)));
        Assert.Equal("W3GS_GAMEINFO", name);
        Assert.Equal("W3XP", (string?)f["product"]);
        Assert.Equal("Alice's game", (string?)f["game_name"]);
        Assert.Equal("Maps\\FrozenThrone\\(2)EchoIsles.w3x", (string?)f["map_path"]);
        Assert.Equal("Alice", (string?)f["host_name"]);
        Assert.Equal(116, (int)f["map_width"]!);
        Assert.Equal(6112, (int)f["game_port"]!);
        Assert.Equal("W3GS_SEARCHGAME", W3gs.Decode(W3(0x2F, FourCC("W3XP"), U32(26), U32(0))).Name);
    }

    [Fact]
    public void Decodes_slots_chat_and_lockstep_actions()
    {
        byte[] slot(int pid, int team, int color) => [(byte)pid, 100, 2, 0, (byte)team, (byte)color, 0x01, 1, 100];
        var (join, jf) = W3gs.Decode(W3(0x04, U16(1 + 18 + 6), U8(2), slot(1, 0, 0), slot(2, 1, 1), U32(99), U8(0), U8(2),
            U8(2), [2, 0, 0x17, 0xE0, 10, 147, 20, 5, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Equal("W3GS_SLOTINFOJOIN", join);
        Assert.Equal(2, (int)jf["player_id"]!);
        Assert.Equal(1, (int)jf["slots"]!["slots"]![1]!["team"]!);
        Assert.Equal("10.147.20.5:6112", (string?)jf["external_address"]);

        var (_, chat) = W3gs.Decode(W3(0x28, U8(1), U8(1), U8(2), U8(0x20), U32(0), Str("gl hf")));
        Assert.Equal(("all", "gl hf"), ((string?)chat["scope"], (string?)chat["message"]));

        byte[] move = [0x11, .. U16(0x0040), .. U32(0x000D0012), .. new byte[8], .. F32(-1024.5f), .. F32(512f)];
        byte[] select = [0x16, 1, .. U16(1), .. U32(0x1234), .. U32(0x5678)];
        byte[] block1 = [.. move, .. select], block2 = [0x61];
        var (name, f) = W3gs.Decode(W3(0x0C, U16(100), U16(0xBEEF), U8(1), U16(block1.Length), block1, U8(2), U16(1), block2));
        Assert.Equal("W3GS_INCOMING_ACTION", name);
        var players = f["players"]!.AsArray();
        var order = players[0]!["actions"]![0]!;
        Assert.Equal(("order_point", 0x000D0012u, -1024.5, 512.0),
            ((string?)order["action"], (uint)order["order_id"]!, (double)order["x"]!, (double)order["y"]!));
        Assert.Equal("00001234:00005678", (string?)players[0]!["actions"]![1]!["units"]![0]);
        Assert.Equal("escape", (string?)players[1]!["actions"]![0]!["action"]);

        var (_, unknown) = W3gs.Decode(W3(0x26, U32(7), [0xEE, 1, 2]));
        Assert.Equal(("0xEE", "0102"), ((string?)unknown["actions"]![0]!["action"], (string?)unknown["actions"]![0]!["rest_hex"]));
    }

    [Fact]
    public void Reassembles_tcp_streams_into_messages_with_direction_and_server_time()
    {
        byte[] req = W3(0x1E, U32(7), U32(0xCAFE), U8(0), U16(6112), U32(1), Str("Bob"), U32(0), U16(6112), U32(0));
        byte[] ping = W3(0x01, U32(1234)), pong = W3(0x46, U32(1234));
        uint s = 1000, h = 5000;
        string path = Pcapng([
            (1000, Tcp(Me, 50000, Host, 6112, s, 0x02, [])),
            (1001, Tcp(Host, 6112, Me, 50000, h, 0x12, [])),
            (1002, Tcp(Me, 50000, Host, 6112, s + 1, 0x18, req[..10])),            // REQJOIN split over two segments
            (1003, Tcp(Me, 50000, Host, 6112, s + 11, 0x18, req[10..])),
            (1004, Tcp(Me, 50000, Host, 6112, s + 11, 0x18, req[10..])),           // retransmission: nothing new
            (1005, Tcp(Host, 6112, Me, 50000, h + 1, 0x18, ping)),
            (1006, Tcp(Me, 50000, Host, 6112, s + 1 + (uint)req.Length + 100, 0x18, pong)),  // 100 bytes lost before it
            (1007, Wifi(Udp(Host, 6112, IPAddress.Broadcast, 6112, W3(0x33, U32(7))))),
            (1008, Tcp(Me, 50000, Host, 6112, s + 1 + (uint)req.Length + 100 + (uint)pong.Length, 0x11, [])),
        ]);
        string bin = Path.ChangeExtension(path, ".bin"), text = Path.ChangeExtension(path, ".txt");
        int count = PacketLog.Write(path, [Me], new PacketCaptureSpec("w3gs", ["tcp/6112"]), 250, new FileHeader { SessionId = "s1", PlayerId = "p1" }, bin, text, Odx.Cli.MergeCommand.Games.Packets);

        var (header, records) = PacketLog.Read(bin);
        Assert.Equal("odx.capture.v1.PacketRecord", header.RecordType);
        Assert.Equal(count, records.Count);
        Assert.Equal(["TCP_OPEN", "W3GS_REQJOIN", "W3GS_PING_FROM_HOST", "TCP_GAP", "W3GS_PONG_TO_HOST", "W3GS_DECREATEGAME", "TCP_CLOSE"],
            records.Select(r => r.Message));
        var reqjoin = records[1];
        Assert.Equal((PacketRecord.Types.Direction.Out, "10.147.20.5:50000", "10.147.20.9:6112"),
            (reqjoin.Direction, reqjoin.LocalEndpoint, reqjoin.RemoteEndpoint));
        Assert.Equal(req, reqjoin.Payload.ToByteArray());
        Assert.Equal("Bob", (string?)JsonNode.Parse(reqjoin.DecodedJson)!["name"]);
        Assert.Equal(1003_000, reqjoin.LocalTimeUs);
        Assert.Equal(1003_000 + 250_000, reqjoin.ServerTimeUs);
        Assert.Equal(PacketRecord.Types.Direction.In, records[2].Direction);
        Assert.Equal(100, (int)JsonNode.Parse(records[3].DecodedJson)!["missing_bytes"]!);

        var lines = File.ReadAllLines(text);
        Assert.Equal(PacketLog.TextHeader, lines[0]);
        Assert.Contains("\tOUT\ttcp\t10.147.20.5:50000\t10.147.20.9:6112\t", lines[2]);
        Assert.Contains("W3GS_REQJOIN\t{", lines[2]);
    }

    [Fact]
    public void Other_protocols_are_kept_raw()
    {
        byte[] encrypted = [0x01, 0x02, 0x03, 0x04];
        string path = Pcapng([(1000, Ethernet(Udp(Host, 27015, Me, 51000, encrypted)))]);
        string bin = Path.ChangeExtension(path, ".bin"), text = Path.ChangeExtension(path, ".txt");
        PacketLog.Write(path, [Me], new PacketCaptureSpec("raw", ["udp/27015"]), 0, new FileHeader(), bin, text, Odx.Cli.MergeCommand.Games.Packets);
        var record = PacketLog.Read(bin).Records.Single();
        Assert.Equal(("", PacketRecord.Types.Direction.In, "udp"), (record.Message, record.Direction, record.Transport));
        Assert.Equal(encrypted, record.Payload.ToByteArray());
    }
}
