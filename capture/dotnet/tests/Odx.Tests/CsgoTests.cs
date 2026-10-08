using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Odx.Capture;
using Odx.Core;
using Odx.Games.Csgo;
using Odx.Merge;
using Odx.Merge.Proto;
using Xunit;

namespace Odx.Tests;

/// <summary>CS:GO datagrams built the way the engine sends them: bit-packed netchannel, padded, ICE-2 encrypted.</summary>
public class CsgoTests
{
    private const string Build = "1.38.8.1";

    /// <summary>bf_write: least significant bit of each byte first.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bit;

        public BitWriter Bits(uint value, int count)
        {
            for (int i = 0; i < count; i++, _bit++)
            {
                if ((_bit & 7) == 0) _bytes.Add(0);
                if ((value & (1u << i)) != 0) _bytes[^1] |= (byte)(1 << (_bit & 7));
            }
            return this;
        }

        public BitWriter Bit(bool b) => Bits(b ? 1u : 0, 1);
        public BitWriter Int32(int v) => Bits((uint)v, 32);
        public BitWriter Bytes(byte[] b) => b.Aggregate(this, (w, x) => w.Bits(x, 8));

        public BitWriter VarInt(uint v)
        {
            do
            {
                uint b = v & 0x7F;
                v >>= 7;
                Bits(v != 0 ? b | 0x80 : b, 8);
            } while (v != 0);
            return this;
        }

        public BitWriter Message(uint type, byte[] payload) => VarInt(type).VarInt((uint)payload.Length).Bytes(payload);

        public byte[] ToArray() => _bytes.ToArray();
    }

    private static BitWriter Header(int seq, byte flags = 0) =>
        new BitWriter().Int32(seq).Int32(seq - 1).Bits(flags, 8).Bits(0, 16).Bits(0, 8);

    /// <summary>Padding count, padding (so the whole is a multiple of 8), big-endian length, packet; then ICE-2.</summary>
    private static byte[] Encrypt(byte[] packet, string build = Build)
    {
        int padding = 8 - (1 + 4 + packet.Length) % 8;
        if (padding == 0) padding = 8;
        byte[] d = [(byte)padding, .. new byte[padding], .. BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(packet.Length)), .. packet];
        CsgoChannelDecoder.Key(build).Encrypt(d);
        return d;
    }

    /// <summary>A protobuf message with varint and string fields.</summary>
    private static byte[] Proto(params (int Field, object Value)[] fields)
    {
        var o = new List<byte>();
        void Varint(ulong v)
        {
            do
            {
                byte b = (byte)(v & 0x7F);
                v >>= 7;
                o.Add(v != 0 ? (byte)(b | 0x80) : b);
            } while (v != 0);
        }
        foreach (var (field, value) in fields)
        {
            if (value is string s)
            {
                Varint((ulong)(field << 3 | 2));
                byte[] b = Encoding.UTF8.GetBytes(s);
                Varint((ulong)b.Length);
                o.AddRange(b);
            }
            else
            {
                Varint((ulong)(field << 3));
                Varint(Convert.ToUInt64(value));
            }
        }
        return o.ToArray();
    }

    /// <summary>LZSS with literals only (a zero command byte per 8 literals) and the end marker.</summary>
    private static byte[] Lzss(byte[] data)
    {
        var o = new List<byte> { (byte)'L', (byte)'Z', (byte)'S', (byte)'S' };
        o.AddRange(BitConverter.GetBytes(data.Length));
        int i = 0;
        while (true)
        {
            int literals = Math.Min(8, data.Length - i);
            if (literals == 8)
            {
                o.Add(0);
                o.AddRange(data.Skip(i).Take(8));
                i += 8;
                continue;
            }
            o.Add((byte)(1 << literals));
            o.AddRange(data.Skip(i).Take(literals));
            o.AddRange([0, 0]);  // distance 0, count 0+1: end
            return o.ToArray();
        }
    }

    /// <summary>Snappy with one literal run and one copy of the run's first 4 bytes.</summary>
    private static byte[] Snappy(byte[] literal)
    {
        byte[] expanded = [.. literal, .. literal[..4]];
        return [(byte)'S', (byte)'N', (byte)'A', (byte)'P', (byte)expanded.Length, (byte)((literal.Length - 1) << 2), .. literal,
            1, (byte)literal.Length];  // copy: length 4, offset back to the run's start
    }

    [Fact]
    public void Derives_the_build_key()
    {
        // The key of build 1.37.7.5 as an open CS:GO proxy hard-codes it.
        var expected = new IceKey(2, [0x43, 0x53, 0x47, 0x4F, 0xCF, 0x35, 0x00, 0x00, 0x73, 0x0D, 0x00, 0x00, 0x5C, 0x03, 0x00, 0x00]);
        byte[] block = Convert.FromHexString("0123456789abcdef"), a = new byte[8], b = new byte[8];
        expected.EncryptBlock(block, a);
        CsgoChannelDecoder.Key("1.37.7.5").EncryptBlock(block, b);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Decodes_unreliable_messages_in_both_directions()
    {
        var decoder = new CsgoChannelDecoder(Build);
        var fromServer = decoder.Decode(Encrypt(Header(10).Message(4, Proto((1, 6400), (2, 7))).Message(16, Proto((1, "hello"))).ToArray()), true);
        Assert.Equal(["net_Tick", "svc_Print"], fromServer.Select(m => m.Name));
        Assert.Equal(6400L, (long)fromServer[0].Fields["fields"]!["1"]!);
        Assert.Equal("hello", (string?)fromServer[1].Fields["fields"]!["1"]);
        Assert.Equal(10, (int)fromServer[0].Fields["seq"]!);

        var fromClient = new CsgoChannelDecoder(Build).Decode(Encrypt(Header(5, 1 << 4).Bits(2, 8).Message(9, Proto((1, 0), (2, 1))).ToArray()), false);
        Assert.Equal("clc_Move", fromClient.Single().Name);

        Assert.Equal("NET_ACK", new CsgoChannelDecoder(Build).Decode(Encrypt(Header(6).ToArray()), true).Single().Name);
    }

    [Fact]
    public void Decodes_reliable_single_block_and_fragmented_compressed_transfers()
    {
        byte[] serverInfo = new BitWriter().Message(8, Proto((1, 13881), (5, "de_dust2"))).ToArray();
        var single = Header(1, 1).Bits(3, 3)
            .Bit(true).Bit(false).Bit(false).Bits((uint)serverInfo.Length, 18).Bytes(serverInfo)  // stream 0: single, uncompressed
            .Bit(false)                                                                            // stream 1: nothing
            .Message(4, Proto((1, 1)));
        var decoded = new CsgoChannelDecoder(Build).Decode(Encrypt(single.ToArray()), true);
        Assert.Equal(["svc_ServerInfo", "net_Tick"], decoded.Select(m => m.Name));
        Assert.True((bool)decoded[0].Fields["reliable"]!);
        Assert.Equal("de_dust2", (string?)decoded[0].Fields["fields"]!["5"]);

        // 300 bytes of messages, LZSS-compressed, sent as two fragments of a multi-block transfer.
        byte[] messages = new BitWriter().Message(16, Proto((1, new string('x', 280)))).ToArray();
        byte[] compressed = Lzss(messages);
        var decoder = new CsgoChannelDecoder(Build);
        int fragments = (compressed.Length + 255) / 256;
        Assert.Equal(2, fragments);
        var first = Header(2, 1).Bits(0, 3).Bit(true)
            .Bit(true).Bits(0, 18).Bits(1, 3)                       // multi-block: fragments [0, 1)
            .Bit(false)                                              // not a file
            .Bit(true).Bits((uint)messages.Length, 26)               // compressed, uncompressed size
            .Bits((uint)compressed.Length, 26)
            .Bytes(compressed[..256]).Bit(false);
        Assert.Equal("NET_ACK", decoder.Decode(Encrypt(first.ToArray()), true).Single().Name);
        var second = Header(3, 1).Bits(1, 3).Bit(true)
            .Bit(true).Bits(1, 18).Bits(1, 3)
            .Bytes(compressed[256..]).Bit(false);
        var done = decoder.Decode(Encrypt(second.ToArray()), true);
        Assert.Equal("svc_Print", done.Single().Name);
        Assert.Equal(280, ((string?)done[0].Fields["fields"]!["1"])!.Length);

        Assert.Equal(Encoding.ASCII.GetBytes("abcdefgabcd"), CsgoChannelDecoder.Decompress(Snappy(Encoding.ASCII.GetBytes("abcdefg"))));
    }

    [Fact]
    public void Reassembles_split_datagrams()
    {
        byte[] whole = Encrypt(Header(9).Message(16, Proto((1, new string('y', 100)))).ToArray());
        byte[] Part(int number, byte[] data) =>
            [.. BitConverter.GetBytes(-2), .. BitConverter.GetBytes(77), .. BitConverter.GetBytes((ushort)(number << 8 | 2)),
             .. BitConverter.GetBytes((ushort)1260), .. data];
        var decoder = new CsgoChannelDecoder(Build);
        Assert.Empty(decoder.Decode(Part(1, whole[64..]), true));
        Assert.Equal("svc_Print", decoder.Decode(Part(0, whole[..64]), true).Single().Name);
    }

    [Fact]
    public void A_servers_info_reply_sets_the_key_and_a_wrong_key_is_reported()
    {
        var decoder = new CsgoChannelDecoder(Build);
        byte[] packet = Encrypt(Header(1).Message(4, Proto((1, 1))).ToArray(), "1.37.7.5");
        Assert.Equal("NET_UNDECODABLE", decoder.Decode(packet, true).Single().Name);

        byte[] info = [.. BitConverter.GetBytes(-1), (byte)'I', 17, .. "srv\0"u8, .. "de_mirage\0"u8, .. "csgo\0"u8,
            .. "Counter-Strike: Global Offensive\0"u8, .. BitConverter.GetBytes((ushort)730), 2, 10, 0, (byte)'d', (byte)'w', 1, 0,
            .. "1.37.7.5\0"u8];
        var reply = decoder.Decode(info, true).Single();
        Assert.Equal(("S2A_INFO", "de_mirage", "1.37.7.5"), (reply.Name, (string?)reply.Fields["map"], (string?)reply.Fields["version"]));
        Assert.Equal("net_Tick", decoder.Decode(packet, true).Single().Name);
    }

    [Fact]
    public void Packet_log_names_server_and_client_messages()
    {
        IPAddress me = IPAddress.Parse("10.0.0.5"), server = IPAddress.Parse("10.0.0.9");
        byte[] Udp(IPAddress from, int fromPort, IPAddress to, int toPort, byte[] payload)
        {
            var udp = new byte[8 + payload.Length];
            BinaryPrimitives.WriteUInt16BigEndian(udp, (ushort)fromPort);
            BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(2), (ushort)toPort);
            payload.CopyTo(udp, 8);
            var ip = new byte[20 + udp.Length];
            ip[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)ip.Length);
            ip[9] = 17;
            from.GetAddressBytes().CopyTo(ip, 12);
            to.GetAddressBytes().CopyTo(ip, 16);
            udp.CopyTo(ip, 20);
            return [.. new byte[12], 0x08, 0x00, .. ip];
        }
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
        foreach (var (ms, frame) in new[]
                 {
                     (1000L, Udp(me, 50000, server, 27015, Encrypt(Header(1).Message(9, Proto((2, 1))).ToArray()))),
                     (1010L, Udp(server, 27015, me, 50000, Encrypt(Header(1).Message(26, Proto((1, 3))).ToArray()))),
                 })
        {
            var body = new byte[20 + frame.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), (uint)(ms * 1000));
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)frame.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), (uint)frame.Length);
            frame.CopyTo(body, 20);
            Block(6, body);
        }
        string path = Path.Combine(Path.GetTempPath(), $"odx-csgo-{Guid.NewGuid():N}.pcapng");
        File.WriteAllBytes(path, o.ToArray());
        string bin = Path.ChangeExtension(path, ".bin"), text = Path.ChangeExtension(path, ".txt");
        PacketLog.Write(path, [me], new PacketCaptureSpec("csgo", ["udp/27015"], Build), 0, new FileHeader(), bin, text, Odx.Cli.MergeCommand.Games.Packets);
        var records = PacketLog.Read(bin).Records;
        Assert.Equal(["clc_Move", "svc_PacketEntities"], records.Select(r => r.Message));
        Assert.Equal([PacketRecord.Types.Direction.Out, PacketRecord.Types.Direction.In], records.Select(r => r.Direction));
        Assert.Equal(3, (int)JsonNode.Parse(records[1].DecodedJson)!["fields"]!["1"]!);
    }
}
