using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Odx.Core;

namespace Odx.Games.Csgo;

/// <summary>One decoded unit of CS:GO traffic: a netmessage, a connectionless packet, or a note about the datagram.</summary>
/// <param name="Bytes">The message's bytes (a netmessage's protobuf payload; a connectionless packet whole).</param>
public sealed record CsgoMessage(string Name, JsonObject Fields, byte[] Bytes);

/// <summary>
/// Decodes CS:GO (legacy, Source 1) game traffic seen on the wire, one direction of one connection per instance (reliable
/// streams carry state across datagrams). Layers, outermost first:
/// <list type="number">
/// <item>0xFFFFFFFF: connectionless packet (server queries, challenge, connect); 0xFFFFFFFE: one part of a split datagram.</item>
/// <item>The rest is ICE-2 encrypted with a key derived from the game build (<see cref="Key"/>), then holds a padding count,
/// padding, a big-endian length and the packet; 0xFFFFFFFD then means LZSS/Snappy-compressed.</item>
/// <item>Netchannel header: sequence, ack, flags, checksum, reliable state[, choked].</item>
/// <item>With PACKET_FLAG_RELIABLE: a subchannel index and, per stream (messages, files), a possibly fragmented and
/// compressed transfer.</item>
/// <item>Netmessages, reliable then unreliable: varint type, varint size, protobuf payload; names from netmessages.proto.</item>
/// </list>
/// Format per Synacktiv's CS:GO protocol notes and the open Source engine client emulators; payload fields are decoded by
/// protobuf field number (no schema).
/// </summary>
public sealed class CsgoChannelDecoder(string? build)
{
    private const int FragmentBits = 8, FragmentSize = 1 << FragmentBits, MaxFileSizeBits = 26, MaxPayloadBits = 18;
    private const byte FlagReliable = 1, FlagChoked = 1 << 4;

    private sealed class Transfer
    {
        public required byte[] Buffer { get; init; }
        public string? FileName { get; init; }
        public int UncompressedSize { get; init; }
        public required int Fragments { get; init; }
        public HashSet<int> Received { get; } = [];
    }

    private readonly Transfer?[] _streams = new Transfer?[2];
    private readonly Dictionary<int, byte[]?[]> _splits = [];
    private IceKey? _key = build is null ? null : Key(build);
    private string? _build = build;

    /// <summary>The ICE-2 key of a build ("1.38.8.1"): "CSGO" then the build number's bits in three rotations.</summary>
    public static IceKey Key(string build)
    {
        uint n = uint.Parse(build.Replace(".", "", StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture);
        byte[] key = [(byte)'C', (byte)'S', (byte)'G', (byte)'O', .. new[] { 0, 8, 16, 24, 2, 10, 18, 26, 4, 12, 20, 28 }.Select(k => (byte)(n >> k))];
        return new IceKey(2, key);
    }

    /// <param name="fromServer">Datagram sent by the game server (svc_ messages) rather than the client (clc_).</param>
    public List<CsgoMessage> Decode(byte[] datagram, bool fromServer)
    {
        if (datagram.Length < 4) return [];
        int header = BinaryPrimitives.ReadInt32LittleEndian(datagram);
        if (header == -1) return [Connectionless(datagram)];
        if (header == -2) return Split(datagram, fromServer);

        byte[] packet = datagram;
        if (_key is not null)
        {
            packet = (byte[])datagram.Clone();
            _key.Decrypt(packet);
            if (Unpad(packet) is not { } inner)
                return [Note("NET_UNDECODABLE", new JsonObject
                {
                    ["reason"] = $"not an ICE packet for build {_build}: wrong network.version in the game profile, or not CS:GO",
                    ["bytes"] = datagram.Length,
                })];
            packet = inner;
        }
        return Channel(packet, fromServer);
    }

    private List<CsgoMessage> Channel(byte[] packet, bool fromServer)
    {
        if (packet.Length >= 4 && BinaryPrimitives.ReadInt32LittleEndian(packet) == -3)
        {
            if (Decompress(packet.AsSpan(4)) is not { } inflated) return [Note("NET_UNDECODABLE", new JsonObject { ["reason"] = "bad compressed packet" })];
            packet = inflated;
        }
        var r = new SourceBitReader(packet);
        var messages = new List<CsgoMessage>();
        var head = new JsonObject();
        try
        {
            head["seq"] = r.Int32();
            head["ack"] = r.Int32();
            int flags = (int)r.Bits(8);
            head["flags"] = flags;
            r.Bits(16);  // checksum
            head["reliable_state"] = r.Bits(8);
            if ((flags & FlagChoked) != 0) head["choked"] = r.Bits(8);
            if ((flags & FlagReliable) != 0)
            {
                head["subchannel"] = r.Bits(3);
                for (int stream = 0; stream < 2; stream++)
                    if (r.Bit() && ReadTransfer(r, stream) is { } done)
                    {
                        if (done.FileName is { } file)
                            messages.Add(Note("NET_FILE", new JsonObject { ["file"] = file, ["bytes"] = done.Buffer.Length }));
                        else if (Inflate(done) is { } data)
                            messages.AddRange(Messages(new SourceBitReader(data), fromServer, head, reliable: true));
                    }
            }
            messages.AddRange(Messages(r, fromServer, head, reliable: false));
        }
        catch (EndOfStreamException)
        {
            messages.Add(Note("NET_TRUNCATED", (JsonObject)head.DeepClone()));
        }
        if (messages.Count == 0) messages.Add(Note("NET_ACK", head));
        return messages;
    }

    private IEnumerable<CsgoMessage> Messages(SourceBitReader r, bool fromServer, JsonObject head, bool reliable)
    {
        var messages = new List<CsgoMessage>();
        while (r.BitsLeft >= 8)
        {
            uint type;
            int size;
            try
            {
                type = r.VarInt32();
                size = (int)r.VarInt32();
            }
            catch (EndOfStreamException)
            {
                break;  // the zero padding at the end of a datagram
            }
            if (type == 0 && size == 0) continue;
            if (size > r.BitsLeft / 8)
            {
                messages.Add(Note("NET_TRUNCATED", new JsonObject { ["type"] = type, ["size"] = size }));
                break;
            }
            byte[] payload = r.Bytes(size);
            var fields = new JsonObject
            {
                ["seq"] = head["seq"]?.DeepClone(),
                ["reliable"] = reliable,
                ["fields"] = ProtobufWire.Decode(payload),
            };
            messages.Add(new CsgoMessage(MessageName(type, fromServer), fields, payload));
        }
        return messages;
    }

    /// <summary>One subchannel transfer chunk; the transfer once complete, else null.</summary>
    private Transfer? ReadTransfer(SourceBitReader r, int stream)
    {
        bool single = !r.Bit();
        int start = 0, count = 0;
        if (!single)
        {
            start = (int)r.Bits(MaxFileSizeBits - FragmentBits);
            count = (int)r.Bits(3);
        }
        if (start == 0)
        {
            string? file = null;
            if (!single && r.Bit())
            {
                r.Bits(32);  // transfer id
                file = r.String();
                r.Bit();     // replay demo
            }
            int uncompressed = r.Bit() ? (int)r.Bits(single ? MaxPayloadBits : MaxFileSizeBits) : 0;
            int size = (int)r.Bits(single ? MaxPayloadBits : MaxFileSizeBits);
            int fragments = (size + FragmentSize - 1) / FragmentSize;
            _streams[stream] = new Transfer { Buffer = new byte[size], FileName = file, UncompressedSize = uncompressed, Fragments = fragments };
            if (single) count = fragments;
        }
        if (_streams[stream] is not { } transfer)
        {
            // Joined mid-transfer: its first chunk (with the sizes) is not in this capture, so this one cannot be placed.
            r.Bytes(count * FragmentSize);
            return null;
        }
        int offset = start * FragmentSize;
        int length = Math.Min(count * FragmentSize, transfer.Buffer.Length - offset);
        if (length < 0) throw new EndOfStreamException();
        r.Bytes(length).CopyTo(transfer.Buffer, offset);
        for (int i = start; i < start + count; i++) transfer.Received.Add(i);
        if (transfer.Received.Count < transfer.Fragments) return null;
        _streams[stream] = null;
        return transfer;
    }

    private static byte[]? Inflate(Transfer t) => t.UncompressedSize > 0 ? Decompress(t.Buffer) : t.Buffer;

    private List<CsgoMessage> Split(byte[] datagram, bool fromServer)
    {
        if (datagram.Length < 12) return [];
        int sequence = BinaryPrimitives.ReadInt32LittleEndian(datagram.AsSpan(4));
        int id = BinaryPrimitives.ReadUInt16LittleEndian(datagram.AsSpan(8));
        int total = id & 0xff, number = id >> 8;
        if (total == 0 || number >= total) return [];
        if (!_splits.TryGetValue(sequence, out var parts) || parts.Length != total) _splits[sequence] = parts = new byte[]?[total];
        parts[number] = datagram[12..];
        if (parts.Any(p => p is null)) return [];
        _splits.Remove(sequence);
        return Decode(parts.SelectMany(p => p!).ToArray(), fromServer);
    }

    /// <summary>Connectionless packets; a server's S2A_INFO also tells its build, which switches the ICE key to it.</summary>
    private CsgoMessage Connectionless(byte[] datagram)
    {
        var fields = new JsonObject();
        char type = datagram.Length > 4 ? (char)datagram[4] : '\0';
        string name = type switch
        {
            'q' => "A2S_GETCHALLENGE", 'A' => "S2C_CHALLENGE", 'k' => "C2S_CONNECT", 'B' => "S2C_CONNECTION", '9' => "S2C_CONNREJECT",
            'T' => "A2S_INFO", 'I' => "S2A_INFO", 'U' => "A2S_PLAYER", 'D' => "S2A_PLAYER", 'V' => "A2S_RULES", 'E' => "S2A_RULES",
            'i' => "A2A_PING", 'j' => "A2A_ACK", _ => $"CONNECTIONLESS_0x{(int)type:X2}",
        };
        if (type == 'I')
        {
            try
            {
                var r = new SourceBitReader(datagram, 5);
                fields["protocol"] = r.Bits(8);
                fields["server_name"] = r.String();
                fields["map"] = r.String();
                fields["folder"] = r.String();
                fields["game"] = r.String();
                fields["app_id"] = r.Bits(16);
                fields["players"] = r.Bits(8);
                fields["max_players"] = r.Bits(8);
                fields["bots"] = r.Bits(8);
                r.Bits(8 * 4);  // server type, environment, visibility, VAC
                string version = r.String();
                fields["version"] = version;
                if (version.Length > 0 && version != _build && version.All(c => char.IsAsciiDigit(c) || c == '.'))
                {
                    _build = version;
                    _key = Key(version);
                    fields["ice_key_from_server"] = true;
                }
            }
            catch (EndOfStreamException)
            {
                fields["truncated"] = true;
            }
        }
        else if (type == '9' && datagram.Length > 9)
        {
            fields["reason"] = Encoding.UTF8.GetString(datagram, 9, datagram.Length - 9).TrimEnd('\0');
        }
        return new CsgoMessage(name, fields, datagram);
    }

    /// <summary>Encrypted payload: a padding count, that many padding bytes, a big-endian length, the packet.</summary>
    private static byte[]? Unpad(byte[] d)
    {
        int padding = d[0];
        if (padding + 5 > d.Length) return null;
        int length = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(padding + 1));
        return padding + 5 + length == d.Length ? d[(padding + 5)..] : null;
    }

    /// <summary>The engine's buffer compression: "LZSS" + size + LZSS stream, or "SNAP" + a raw Snappy stream.</summary>
    public static byte[]? Decompress(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return null;
        try
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(data) switch
            {
                0x53535A4C => Lzss(data[8..], (int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..])),
                0x50414E53 => Snappy(data[4..]),
                _ => null,
            };
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Source LZSS: a command byte before every 8 items; a set bit is a back-reference (12-bit distance, 4-bit length − 1,
    /// length 1 ends the stream), a clear bit a literal byte.
    /// </summary>
    private static byte[] Lzss(ReadOnlySpan<byte> input, int size)
    {
        var output = new List<byte>(size);
        int o = 0, command = 0;
        for (int item = 0; ; item = (item + 1) & 7)
        {
            if (item == 0) command = input[o++];
            if ((command & 1) != 0)
            {
                int distance = (input[o] << 4) | (input[o + 1] >> 4), count = (input[o + 1] & 0xF) + 1;
                o += 2;
                if (count == 1) break;
                int from = output.Count - 1 - distance;
                if (from < 0) throw new InvalidDataException("LZSS reference before the start");
                for (int i = 0; i < count; i++) output.Add(output[from + i]);
            }
            else output.Add(input[o++]);
            command >>= 1;
        }
        if (output.Count != size) throw new InvalidDataException("LZSS size mismatch");
        return output.ToArray();
    }

    /// <summary>Raw Snappy: varint length, then literals and copies (1-, 2- or 4-byte offsets).</summary>
    private static byte[] Snappy(ReadOnlySpan<byte> input)
    {
        int o = 0, length = 0, shift = 0;
        while (true)
        {
            byte b = input[o++];
            length |= (b & 0x7F) << shift;
            if (b < 0x80) break;
            shift += 7;
        }
        var output = new byte[length];
        int w = 0;
        while (o < input.Length)
        {
            byte tag = input[o++];
            int n, offset;
            switch (tag & 3)
            {
                case 0:
                    n = tag >> 2;
                    if (n >= 60)
                    {
                        int bytes = n - 59;
                        n = 0;
                        for (int i = 0; i < bytes; i++) n |= input[o++] << (8 * i);
                    }
                    input.Slice(o, n + 1).CopyTo(output.AsSpan(w));
                    o += n + 1;
                    w += n + 1;
                    continue;
                case 1:
                    n = 4 + ((tag >> 2) & 7);
                    offset = ((tag >> 5) << 8) | input[o++];
                    break;
                case 2:
                    n = (tag >> 2) + 1;
                    offset = BinaryPrimitives.ReadUInt16LittleEndian(input[o..]);
                    o += 2;
                    break;
                default:
                    n = (tag >> 2) + 1;
                    offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(input[o..]);
                    o += 4;
                    break;
            }
            if (offset == 0 || offset > w) throw new InvalidDataException("Snappy copy before the start");
            for (int i = 0; i < n; i++, w++) output[w] = output[w - offset];
        }
        if (w != length) throw new InvalidDataException("Snappy size mismatch");
        return output;
    }

    /// <summary>netmessages.proto: NET_Messages both ways, then SVC_Messages from the server or CLC_Messages from the client.</summary>
    public static string MessageName(uint type, bool fromServer) => type switch
    {
        0 => "net_NOP", 1 => "net_Disconnect", 2 => "net_File", 3 => "net_SplitScreenUser", 4 => "net_Tick", 5 => "net_StringCmd",
        6 => "net_SetConVar", 7 => "net_SignonState", 100 => "net_PlayerAvatarData",
        _ when fromServer => type switch
        {
            8 => "svc_ServerInfo", 9 => "svc_SendTable", 10 => "svc_ClassInfo", 11 => "svc_SetPause", 12 => "svc_CreateStringTable",
            13 => "svc_UpdateStringTable", 14 => "svc_VoiceInit", 15 => "svc_VoiceData", 16 => "svc_Print", 17 => "svc_Sounds",
            18 => "svc_SetView", 19 => "svc_FixAngle", 20 => "svc_CrosshairAngle", 21 => "svc_BSPDecal", 22 => "svc_SplitScreen",
            23 => "svc_UserMessage", 24 => "svc_EntityMessage", 25 => "svc_GameEvent", 26 => "svc_PacketEntities",
            27 => "svc_TempEntities", 28 => "svc_Prefetch", 29 => "svc_Menu", 30 => "svc_GameEventList", 31 => "svc_GetCvarValue",
            33 => "svc_PaintmapData", 34 => "svc_CmdKeyValues", 35 => "svc_EncryptedData", 36 => "svc_HltvReplay",
            38 => "svc_Broadcast_Command", _ => $"svc_{type}",
        },
        _ => type switch
        {
            8 => "clc_ClientInfo", 9 => "clc_Move", 10 => "clc_VoiceData", 11 => "clc_BaselineAck", 12 => "clc_ListenEvents",
            13 => "clc_RespondCvarValue", 14 => "clc_FileCRCCheck", 15 => "clc_LoadingProgress", 16 => "clc_SplitPlayerConnect",
            17 => "clc_ClientMessage", 18 => "clc_CmdKeyValues", 20 => "clc_HltvReplay", _ => $"clc_{type}",
        },
    };

    private static CsgoMessage Note(string name, JsonObject fields) => new(name, fields, []);
}

/// <summary>Protobuf wire format without a schema: fields by number, nested messages and text recognised where they parse.</summary>
public static class ProtobufWire
{
    public static JsonObject Decode(byte[] data) => TryDecode(data, 0) ?? new JsonObject { ["hex"] = Convert.ToHexString(data) };

    private static JsonObject? TryDecode(byte[] data, int depth)
    {
        var fields = new JsonObject();
        int o = 0;
        while (o < data.Length)
        {
            if (!TryVarint(data, ref o, out ulong tag)) return null;
            int number = (int)(tag >> 3), wire = (int)(tag & 7);
            if (number == 0) return null;
            JsonNode? value;
            switch (wire)
            {
                case 0:
                    if (!TryVarint(data, ref o, out ulong v)) return null;
                    value = JsonValue.Create((long)v);
                    break;
                case 1:
                    if (o + 8 > data.Length) return null;
                    value = JsonValue.Create(BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(o)));
                    o += 8;
                    break;
                case 5:
                    if (o + 4 > data.Length) return null;
                    value = JsonValue.Create(Math.Round(BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(o)), 4));
                    o += 4;
                    break;
                case 2:
                    if (!TryVarint(data, ref o, out ulong length) || o + (long)length > data.Length) return null;
                    byte[] bytes = data[o..(o + (int)length)];
                    o += (int)length;
                    value = Text(bytes) is { } s ? JsonValue.Create(s)
                        : depth < 8 && bytes.Length > 0 && TryDecode(bytes, depth + 1) is { } nested ? nested
                        : JsonValue.Create(Convert.ToHexString(bytes));
                    break;
                default:
                    return null;
            }
            string key = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (fields[key] is JsonArray list) list.Add(value);
            else if (fields.ContainsKey(key))
            {
                var first = fields[key]!.DeepClone();
                fields[key] = new JsonArray(first, value);
            }
            else fields[key] = value;
        }
        return fields;
    }

    private static string? Text(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        if (bytes.Any(b => b < 0x20 && b is not (0x09 or 0x0A or 0x0D))) return null;
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool TryVarint(byte[] data, ref int o, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64 && o < data.Length; shift += 7)
        {
            byte b = data[o++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return true;
        }
        return false;
    }
}
