using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Odx.Games.Wc3;

/// <summary>
/// Decodes Warcraft III's game protocol (W3GS, classic 1.26–1.29: LAN discovery over UDP 6112, the game over TCP 6112).
/// Messages are 0xF7, an id byte, a little-endian uint16 length (header included) and the fields. In-game, the host
/// relays every player's commands each tick (INCOMING_ACTION) and each player sends its own (OUTGOING_ACTION); those
/// command blocks use the same action encoding as replays and are decoded action by action. Field layouts follow
/// BNETDocs and gowarcraft3's w3gs package; anything unknown is kept as hex rather than guessed.
/// </summary>
public static class W3gs
{
    public const byte Signature = 0xF7;

    private static readonly Dictionary<byte, string> Names = new()
    {
        [0x01] = "PING_FROM_HOST", [0x04] = "SLOTINFOJOIN", [0x05] = "REJECTJOIN", [0x06] = "PLAYERINFO", [0x07] = "PLAYERLEFT",
        [0x08] = "PLAYERLOADED", [0x09] = "SLOTINFO", [0x0A] = "COUNTDOWN_START", [0x0B] = "COUNTDOWN_END",
        [0x0C] = "INCOMING_ACTION", [0x0F] = "CHAT_FROM_HOST", [0x10] = "START_LAG", [0x11] = "STOP_LAG", [0x1E] = "REQJOIN",
        [0x21] = "LEAVEGAME", [0x23] = "GAMELOADED_SELF", [0x26] = "OUTGOING_ACTION", [0x27] = "OUTGOING_KEEPALIVE",
        [0x28] = "CHAT_TO_HOST", [0x2F] = "SEARCHGAME", [0x30] = "GAMEINFO", [0x31] = "CREATEGAME", [0x32] = "REFRESHGAME",
        [0x33] = "DECREATEGAME", [0x3D] = "MAPCHECK", [0x3F] = "STARTDOWNLOAD", [0x42] = "MAPSIZE", [0x43] = "MAPPART",
        [0x44] = "MAPPARTOK", [0x45] = "MAPPARTNOTOK", [0x46] = "PONG_TO_HOST", [0x48] = "INCOMING_ACTION2",
    };

    /// <summary>Splits complete messages off the front of a stream buffer; what remains is an incomplete message.</summary>
    /// <returns>The messages, and how many bytes they used. Bytes that cannot start a message are skipped (resync).</returns>
    public static (List<byte[]> Messages, int Consumed) Split(ReadOnlySpan<byte> buffer)
    {
        var messages = new List<byte[]>();
        int o = 0;
        while (o < buffer.Length)
        {
            if (buffer[o] != Signature)
            {
                o++;
                continue;
            }
            if (buffer.Length - o < 4) break;
            int length = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(o + 2)..]);
            if (length < 4)
            {
                o++;
                continue;
            }
            if (buffer.Length - o < length) break;
            messages.Add(buffer.Slice(o, length).ToArray());
            o += length;
        }
        return (messages, o);
    }

    /// <summary>The message's name ("W3GS_" + name) and fields; malformed fields end in a "rest_hex" entry.</summary>
    public static (string Name, JsonObject Fields) Decode(byte[] message)
    {
        byte id = message[1];
        string name = "W3GS_" + (Names.TryGetValue(id, out var n) ? n : $"0x{id:X2}");
        var fields = new JsonObject();
        var r = new Reader(message, 4);
        try
        {
            switch (id)
            {
                case 0x01 or 0x46:
                    fields["tick"] = r.U32();
                    break;
                case 0x04:
                    int slotLength = r.U16();
                    fields["slots"] = SlotInfo(new Reader(r.Bytes(slotLength), 0));
                    fields["player_id"] = r.U8();
                    fields["external_address"] = r.SockAddr();
                    break;
                case 0x05 or 0x21:
                    fields["reason"] = r.U32();
                    break;
                case 0x06:
                    fields["join_counter"] = r.U32();
                    fields["player_id"] = r.U8();
                    fields["name"] = r.CString();
                    break;
                case 0x07:
                    fields["player_id"] = r.U8();
                    fields["reason"] = r.U32();
                    break;
                case 0x08 or 0x3F:
                    if (id == 0x3F) r.U32();
                    fields["player_id"] = r.U8();
                    break;
                case 0x09:
                    fields["slots"] = SlotInfo(new Reader(r.Bytes(r.U16()), 0));
                    break;
                case 0x0C or 0x48:
                    fields["send_interval_ms"] = r.U16();
                    if (r.Left >= 2)
                    {
                        fields["crc16"] = r.U16();
                        var players = new JsonArray();
                        while (r.Left >= 3)
                        {
                            int player = r.U8(), length = r.U16();
                            players.Add(new JsonObject { ["player_id"] = player, ["actions"] = Actions(r.Bytes(length)) });
                        }
                        fields["players"] = players;
                    }
                    break;
                case 0x0F or 0x28:
                    Chat(r, fields);
                    break;
                case 0x10:
                    int lagging = r.U8();
                    var lag = new JsonArray();
                    for (int i = 0; i < lagging; i++) lag.Add(new JsonObject { ["player_id"] = r.U8(), ["lag_ms"] = r.U32() });
                    fields["players"] = lag;
                    break;
                case 0x11:
                    fields["player_id"] = r.U8();
                    fields["lag_ms"] = r.U32();
                    break;
                case 0x1E:
                    fields["host_counter"] = r.U32();
                    fields["entry_key"] = r.U32();
                    r.U8();
                    fields["listen_port"] = r.U16();
                    fields["peer_key"] = r.U32();
                    fields["name"] = r.CString();
                    break;
                case 0x26:
                    fields["crc32"] = r.U32();
                    fields["actions"] = Actions(r.Bytes(r.Left));
                    break;
                case 0x27:
                    r.U8();
                    fields["checksum"] = r.U32();
                    break;
                case 0x2F:
                    fields["product"] = r.FourCC();
                    fields["version"] = r.U32();
                    break;
                case 0x30:
                    fields["product"] = r.FourCC();
                    fields["version"] = r.U32();
                    fields["host_counter"] = r.U32();
                    fields["entry_key"] = r.U32();
                    fields["game_name"] = r.CString();
                    r.CString();  // password, always empty on LAN
                    GameSettings(StatString(r.CStringBytes()), fields);
                    fields["slots_total"] = r.U32();
                    fields["game_flags"] = r.U32();
                    fields["slots_used"] = r.U32();
                    fields["slots_available"] = r.U32();
                    fields["uptime_s"] = r.U32();
                    fields["game_port"] = r.U16();
                    break;
                case 0x31:
                    fields["product"] = r.FourCC();
                    fields["version"] = r.U32();
                    fields["host_counter"] = r.U32();
                    break;
                case 0x32:
                    fields["host_counter"] = r.U32();
                    fields["players_in_game"] = r.U32();
                    fields["player_slots"] = r.U32();
                    break;
                case 0x33:
                    fields["host_counter"] = r.U32();
                    break;
                case 0x3D:
                    r.U32();
                    fields["map_path"] = r.CString();
                    fields["map_size"] = r.U32();
                    fields["map_crc"] = r.U32();
                    fields["map_xoro"] = r.U32();
                    break;
                case 0x42:
                    r.U32();
                    fields["size_flag"] = r.U8();
                    fields["map_size"] = r.U32();
                    break;
                case 0x43 or 0x44:
                    fields["to_player"] = r.U8();
                    fields["from_player"] = r.U8();
                    r.U32();
                    fields["offset"] = r.U32();
                    if (id == 0x43) fields["chunk_bytes"] = r.Left - 4;
                    break;
                case 0x0A or 0x0B or 0x23:
                    break;
                default:
                    fields["rest_hex"] = Convert.ToHexString(message.AsSpan(4));
                    break;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            fields["truncated"] = true;
        }
        return (name, fields);
    }

    private static JsonObject SlotInfo(Reader r)
    {
        int count = r.U8();
        var slots = new JsonArray();
        for (int i = 0; i < count; i++)
            slots.Add(new JsonObject
            {
                ["player_id"] = r.U8(),
                ["download_percent"] = r.U8(),
                ["status"] = r.U8() switch { 0 => "open", 1 => "closed", _ => "occupied" },
                ["computer"] = r.U8() != 0,
                ["team"] = r.U8(),
                ["color"] = r.U8(),
                ["race_flags"] = r.U8(),
                ["computer_type"] = r.U8(),
                ["handicap"] = r.U8(),
            });
        var info = new JsonObject { ["slots"] = slots };
        if (r.Left >= 6)
        {
            info["random_seed"] = r.U32();
            info["layout_style"] = r.U8();
            info["player_slots"] = r.U8();
        }
        return info;
    }

    private static void Chat(Reader r, JsonObject fields)
    {
        int receivers = r.U8();
        var to = new JsonArray();
        for (int i = 0; i < receivers; i++) to.Add(r.U8());
        fields["to"] = to;
        fields["from"] = r.U8();
        int flag = r.U8();
        switch (flag)
        {
            case 0x10:
                fields["message"] = r.CString();
                break;
            case 0x11 or 0x12 or 0x13 or 0x14:
                fields[flag switch { 0x11 => "team", 0x12 => "color", 0x13 => "race", _ => "handicap" }] = r.U8();
                break;
            case 0x20:
                fields["scope"] = r.U32() switch { 0 => "all", 1 => "allies", 2 => "observers", var s => (s - 3).ToString(CultureInfo.InvariantCulture) };
                fields["message"] = r.CString();
                break;
            default:
                fields["flag"] = flag;
                fields["rest_hex"] = Convert.ToHexString(r.Bytes(r.Left));
                break;
        }
    }

    /// <summary>The encoded stat string: every block of 7 bytes follows a mask byte; a clear bit (i+1) means byte i is stored +1.</summary>
    private static byte[] StatString(byte[] encoded)
    {
        var decoded = new List<byte>(encoded.Length);
        for (int o = 0; o < encoded.Length; o += 8)
        {
            byte mask = encoded[o];
            for (int i = 1; i < 8 && o + i < encoded.Length; i++)
                decoded.Add((mask & (1 << i)) != 0 ? encoded[o + i] : (byte)(encoded[o + i] - 1));
        }
        return decoded.ToArray();
    }

    private static void GameSettings(byte[] settings, JsonObject fields)
    {
        var r = new Reader(settings, 0);
        try
        {
            fields["game_settings_flags"] = r.U32();
            r.U8();
            fields["map_width"] = r.U16();
            fields["map_height"] = r.U16();
            fields["map_xoro"] = r.U32();
            fields["map_path"] = r.CString();
            fields["host_name"] = r.CString();
        }
        catch (ArgumentOutOfRangeException)
        {
            fields["game_settings_truncated"] = true;
        }
    }

    /// <summary>Game actions of one command block (replay encoding, 1.13+ field sizes).</summary>
    public static JsonArray Actions(byte[] block)
    {
        var actions = new JsonArray();
        var r = new Reader(block, 0);
        try
        {
            while (r.Left > 0)
            {
                int id = r.U8();
                var a = new JsonObject();
                switch (id)
                {
                    case 0x01: a["action"] = "pause"; break;
                    case 0x02: a["action"] = "resume"; break;
                    case 0x03: a["action"] = "set_game_speed"; a["speed"] = r.U8(); break;
                    case 0x04: a["action"] = "increase_game_speed"; break;
                    case 0x05: a["action"] = "decrease_game_speed"; break;
                    case 0x06: a["action"] = "save_game"; a["name"] = r.CString(); break;
                    case 0x07: a["action"] = "save_game_finished"; r.U32(); break;
                    case >= 0x10 and <= 0x14:
                        a["action"] = id switch
                        {
                            0x10 => "order", 0x11 => "order_point", 0x12 => "order_target", 0x13 => "give_or_drop_item", _ => "order_two_points",
                        };
                        a["flags"] = r.U16();
                        a["order_id"] = r.OrderId();
                        r.Bytes(8);
                        if (id >= 0x11)
                        {
                            a["x"] = r.F32();
                            a["y"] = r.F32();
                        }
                        if (id is 0x12 or 0x13) a["target"] = r.ObjectId();
                        if (id == 0x13) a["item"] = r.ObjectId();
                        if (id == 0x14)
                        {
                            a["order_id_2"] = r.OrderId();
                            r.Bytes(9);
                            a["x2"] = r.F32();
                            a["y2"] = r.F32();
                        }
                        break;
                    case 0x16 or 0x17:
                        a["action"] = id == 0x16 ? "change_selection" : "assign_group";
                        if (id == 0x16) a["mode"] = r.U8() == 1 ? "add" : "remove";
                        else a["group"] = r.U8();
                        int count = r.U16();
                        var units = new JsonArray();
                        for (int i = 0; i < count; i++) units.Add(r.ObjectId());
                        a["units"] = units;
                        break;
                    case 0x18: a["action"] = "select_group"; a["group"] = r.U8(); r.U8(); break;
                    case 0x19: a["action"] = "select_subgroup"; a["unit_type"] = r.OrderId(); a["unit"] = r.ObjectId(); break;
                    case 0x1A: a["action"] = "pre_subselection"; break;
                    case 0x1B: a["action"] = "unknown_1b"; r.Bytes(9); break;
                    case 0x1C: a["action"] = "select_ground_item"; r.U8(); a["item"] = r.ObjectId(); break;
                    case 0x1D: a["action"] = "cancel_hero_revival"; a["hero"] = r.ObjectId(); break;
                    case 0x1E: a["action"] = "remove_from_queue"; a["slot"] = r.U8(); a["unit_type"] = r.OrderId(); break;
                    case 0x21: a["action"] = "unknown_21"; r.Bytes(8); break;
                    case 0x50: a["action"] = "change_ally_options"; a["player_slot"] = r.U8(); a["flags"] = r.U32(); break;
                    case 0x51: a["action"] = "transfer_resources"; a["player_slot"] = r.U8(); a["gold"] = r.U32(); a["lumber"] = r.U32(); break;
                    case 0x60: a["action"] = "trigger_chat"; r.Bytes(8); a["message"] = r.CString(); break;
                    case 0x61: a["action"] = "escape"; break;
                    case 0x62: a["action"] = "scenario_trigger"; r.Bytes(12); break;
                    case 0x66: a["action"] = "hero_skill_menu"; break;
                    case 0x67: a["action"] = "building_menu"; break;
                    case 0x68: a["action"] = "minimap_ping"; a["x"] = r.F32(); a["y"] = r.F32(); r.U32(); break;
                    case 0x69 or 0x6A: a["action"] = "continue_game"; r.Bytes(16); break;
                    case 0x75: a["action"] = "unknown_75"; r.U8(); break;
                    default:
                        a["action"] = $"0x{id:X2}";
                        a["rest_hex"] = Convert.ToHexString(r.Bytes(r.Left));
                        break;
                }
                actions.Add(a);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            actions.Add(new JsonObject { ["truncated"] = true });
        }
        return actions;
    }

    /// <summary>Little-endian field reader; reading past the end throws ArgumentOutOfRangeException.</summary>
    private sealed class Reader(byte[] data, int start)
    {
        private int _o = start;

        public int Left => data.Length - _o;

        public int U8() => Take(1)[0];
        public int U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public double F32() => Math.Round(BinaryPrimitives.ReadSingleLittleEndian(Take(4)), 3);
        public byte[] Bytes(int n) => Take(n).ToArray();

        /// <summary>Products and order ids are four ASCII characters stored reversed ("W3XP" as "PX3W"); numeric otherwise.</summary>
        public string FourCC()
        {
            var b = Take(4);
            return Encoding.ASCII.GetString([b[3], b[2], b[1], b[0]]);
        }

        public JsonNode OrderId()
        {
            var b = Take(4);
            bool text = b.ToArray().All(c => c is >= 0x20 and < 0x7F);
            return text
                ? Encoding.ASCII.GetString([b[3], b[2], b[1], b[0]])
                : JsonValue.Create(BinaryPrimitives.ReadUInt32LittleEndian(b))!;
        }

        /// <summary>Unit/item handles: two uint32s, written as "hhhhhhhh:hhhhhhhh".</summary>
        public string ObjectId() => $"{U32():x8}:{U32():x8}";

        public string SockAddr()
        {
            var b = Take(16);
            int port = BinaryPrimitives.ReadUInt16BigEndian(b[2..]);
            return $"{b[4]}.{b[5]}.{b[6]}.{b[7]}:{port}";
        }

        public byte[] CStringBytes()
        {
            int end = Array.IndexOf(data, (byte)0, _o);
            if (end < 0) throw new ArgumentOutOfRangeException(nameof(data));
            var s = data[_o..end];
            _o = end + 1;
            return s;
        }

        public string CString() => Encoding.UTF8.GetString(CStringBytes());

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || _o + n > data.Length) throw new ArgumentOutOfRangeException(nameof(n));
            var s = data.AsSpan(_o, n);
            _o += n;
            return s;
        }
    }
}
