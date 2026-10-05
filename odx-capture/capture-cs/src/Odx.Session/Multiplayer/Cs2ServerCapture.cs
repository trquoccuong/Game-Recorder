using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DemoFile;
using DemoFile.Game.Cs;
using Google.Protobuf;
using Odx.Core;
using Odx.Session.Proto;
using Vec3 = Odx.Session.Proto.Vec3;

namespace Odx.Session;

/// <summary>
/// The CS2 game server's side of a round (spec v3 §3), staged by the orchestrator in &lt;staging&gt;/server/:
/// <list type="bullet">
/// <item><c>demo.dem</c>: the round's SourceTV demo (tv_record / tv_stoprecord over RCON): server_state.bin, events.</item>
/// <item><c>netmessages.jsonl</c>: what the odx_netlog plugin logged during the round (cs2-plugin/README.md):
/// packets.bin / packets.txt, and the server tick ↔ UTC of each line, which put the demo's ticks on the clock.</item>
/// <item><c>senddata.jsonl</c>: the already serialized buffers the plugin saw sent (each joining client's signon data):
/// split into messages for packets.bin, and the game event list, which a demo recorded mid-map does not carry.</item>
/// <item><c>recording.json</c>: when tv_record was sent, the fallback for the demo's first tick without a log.</item>
/// </list>
/// The plugin stamps messages with the game server machine's clock; the orchestrator runs on that machine, so its times are
/// already on the orchestrator's clock.
/// </summary>
public static class Cs2ServerCapture
{
    public const string DemoFile = "demo.dem", NetLogFile = "netmessages.jsonl", SendDataFile = "senddata.jsonl";
    public const string RecordingFile = "recording.json";
    public const string StateFile = "server_state.bin";

    /// <param name="Events">For events.jsonl, with utc_ms; "source": "demo".</param>
    /// <param name="Info">For session.json "server".</param>
    public sealed record Result(List<JsonObject> Events, JsonObject Info);

    public static bool IsStaged(string stagingServerDir) =>
        File.Exists(Path.Combine(stagingServerDir, DemoFile)) || File.Exists(Path.Combine(stagingServerDir, NetLogFile));

    public static Result Write(string stagingServerDir, string sessionServerDir, FileHeader header)
    {
        Directory.CreateDirectory(Path.Combine(sessionServerDir, "capture"));
        var info = new JsonObject();
        var events = new List<JsonObject>();
        var lines = ReadNetLog(Path.Combine(stagingServerDir, NetLogFile));
        var clock = TickClock.Fit(lines);
        string recordingPath = Path.Combine(stagingServerDir, RecordingFile);
        double? recordingStartMs = File.Exists(recordingPath)
            ? JsonNode.Parse(File.ReadAllText(recordingPath))?["tv_record_utc_ms"]?.GetValue<double>()
            : null;
        // The staged signon data spans the server's run (clients join before rounds); this round's packets keep only what
        // was sent during it, the event list is the newest one.
        var signon = ReadNetLog(Path.Combine(stagingServerDir, SendDataFile))
            .SelectMany(l => SplitSendData(l).Select(m => (Line: l, m.Id, m.Payload))).ToList();
        var eventList = signon.LastOrDefault(m => m.Id == GameEventListId) is { Payload: { } listBytes }
            ? CMsgSource1LegacyGameEventList.Parser.ParseFrom(listBytes)
            : null;

        var records = lines.Select(PacketOf)
            .Concat(signon.Where(m => m.Line.UtcUs >= (recordingStartMs ?? 0) * 1000).Select(m => SignonPacketOf(m.Line, m.Id, m.Payload)))
            .OrderBy(r => r.ServerTimeUs).ToList();
        if (records.Count > 0)
        {
            PacketLog.WriteRecords(records, header.Clone(), Path.Combine(sessionServerDir, PacketLog.BinFile),
                Path.Combine(sessionServerDir, PacketLog.TextFile));
            info["packets"] = new JsonObject { ["source"] = "odx_netlog plugin", ["records"] = records.Count };
        }
        foreach (string file in new[] { NetLogFile, SendDataFile }.Where(f => File.Exists(Path.Combine(stagingServerDir, f))))
            File.Copy(Path.Combine(stagingServerDir, file), Path.Combine(sessionServerDir, "capture", file), true);

        string demo = Path.Combine(stagingServerDir, DemoFile);
        if (File.Exists(demo))
        {
            clock ??= TickClock.FromRecordingStart(recordingStartMs, demo);
            if (clock is not null)
            {
                var (states, eventNames) = WriteState(demo, clock, eventList, header.Clone(), Path.Combine(sessionServerDir, StateFile), events);
                info["server_state"] = new JsonObject { ["source"] = "SourceTV demo", ["records"] = states };
                info["game_event_names"] = eventNames;
            }
            File.Copy(demo, Path.Combine(sessionServerDir, "capture", DemoFile), true);
        }
        info["tick_clock"] = clock?.Describe();
        return new Result(events, info);
    }

    /// <summary>One line of the plugin's log (cs2-plugin/src/plugin.cpp writes them).</summary>
    /// <param name="Tick">The server tick the line was written in.</param>
    /// <param name="Data">SendData lines: the buffer as sent, <paramref name="Bits"/> long.</param>
    internal sealed record NetLogLine(long UtcUs, int Tick, string Direction, int Slot, string SteamId, string Address, string Message,
        int Id, int Buf, string Text, int Bits = 0, byte[]? Data = null);

    internal static List<NetLogLine> ReadNetLog(string path)
    {
        if (!File.Exists(path)) return [];
        var lines = new List<NetLogLine>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            JsonObject o;
            try
            {
                o = JsonNode.Parse(line)!.AsObject();
            }
            catch (JsonException)
            {
                continue;  // the last line when the server stopped mid-write
            }
            lines.Add(new NetLogLine(o["utc_us"]!.GetValue<long>(), o["tick"]?.GetValue<int>() ?? -1, o["direction"]!.GetValue<string>(),
                o["slot"]!.GetValue<int>(),
                o["steamid"]!.GetValue<string>(), o["address"]!.GetValue<string>(), o["message"]!.GetValue<string>(),
                o["id"]!.GetValue<int>(), o["buf"]!.GetValue<int>(), o["text"]!.GetValue<string>(), o["bits"]?.GetValue<int>() ?? 0,
                o["data"] is JsonValue data ? Convert.FromBase64String(data.GetValue<string>()) : null));
        }
        return lines;
    }

    private static PacketRecord PacketOf(NetLogLine l) => new()
    {
        ServerTimeUs = l.UtcUs,
        LocalTimeUs = l.UtcUs,
        Direction = l.Direction == "out" ? PacketRecord.Types.Direction.Out : PacketRecord.Types.Direction.In,
        Transport = "netmessage",
        LocalEndpoint = "server",
        RemoteEndpoint = l.Address,
        Message = MessageName(l.Message),
        DecodedJson = ProtobufText.ToJson(l.Text).ToJsonString(AgentMessage.Json),
        PeerId = l.SteamId,
    };

    /// <summary>A serialized message stream: each message is its id (UBitVar), its size (varint) and its protobuf bytes.</summary>
    internal static List<(int Id, byte[] Payload)> SplitSendData(NetLogLine line)
    {
        var messages = new List<(int, byte[])>();
        if (line.Data is null) return messages;
        var reader = new SourceBitReader(line.Data);
        int padding = line.Data.Length * 8 - line.Bits;
        try
        {
            while (reader.BitsLeft - padding >= 8)
            {
                int id = (int)reader.UBitVar();
                int size = (int)reader.VarInt32();
                messages.Add((id, reader.Bytes(size)));
            }
        }
        catch (EndOfStreamException)
        {
        }
        return messages;
    }

    private static PacketRecord SignonPacketOf(NetLogLine l, int id, byte[] payload)
    {
        var record = new PacketRecord
        {
            ServerTimeUs = l.UtcUs,
            LocalTimeUs = l.UtcUs,
            Direction = PacketRecord.Types.Direction.Out,
            Transport = "netmessage",
            LocalEndpoint = "server",
            RemoteEndpoint = l.Address,
            Message = $"#{id}",
            Payload = ByteString.CopyFrom(payload),
            PeerId = l.SteamId,
        };
        if (Messages.Value.TryGetValue(id, out var type))
        {
            record.Message = type.Name;
            try
            {
                record.DecodedJson = JsonFormatter.Default.Format(type.Parser.ParseFrom(payload));
            }
            catch (InvalidProtocolBufferException)
            {
            }
        }
        return record;
    }

    private static int GameEventListId => Messages.Value.First(m => m.Value.Name == nameof(CMsgSource1LegacyGameEventList)).Key;

    /// <summary>
    /// Net message id → protobuf class, from the id enums DemoFile generates from the game's .proto files: their original
    /// value names (svc_ServerInfo, net_Tick, GE_…, UM_…, CS_UM_…) name the message classes.
    /// </summary>
    private static readonly Lazy<Dictionary<int, (string Name, MessageParser Parser)>> Messages = new(() =>
    {
        var assemblies = new[] { typeof(CsDemoParser).Assembly, typeof(DemoParser<>).Assembly };
        var classes = assemblies.SelectMany(a => a.GetTypes()).Where(t => typeof(IMessage).IsAssignableFrom(t) && !t.IsAbstract)
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        (string Prefix, string Class)[] prefixes =
            [("svc_", "CSVCMsg_"), ("net_", "CNETMsg_"), ("clc_", "CCLCMsg_"), ("CS_UM_", "CCSUsrMsg_"), ("UM_", "CUserMessage"),
             ("GE_", "CMsg"), ("EM_", "CEntityMessage")];
        var map = new Dictionary<int, (string, MessageParser)>();
        foreach (var e in assemblies.SelectMany(a => a.GetTypes()).Where(t => t.IsEnum &&
                     t.Name is "SVC_Messages" or "NET_Messages" or "CLC_Messages" or "EBaseGameEvents" or "EBaseUserMessages" or
                         "ECstrike15UserMessages" or "EBaseEntityMessages"))
            foreach (var field in e.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                string? original = field.GetCustomAttribute<Google.Protobuf.Reflection.OriginalNameAttribute>()?.Name;
                var prefix = prefixes.FirstOrDefault(p => original?.StartsWith(p.Prefix, StringComparison.Ordinal) == true);
                if (prefix.Prefix is null || !classes.TryGetValue(prefix.Class + original![prefix.Prefix.Length..], out var type)) continue;
                if (type.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is MessageParser parser)
                    map.TryAdd(Convert.ToInt32(field.GetValue(null), CultureInfo.InvariantCulture), (type.Name, parser));
            }
        return map;
    });

    /// <summary>"CNETMsg_Tick [4]" → "CNETMsg_Tick".</summary>
    private static string MessageName(string bound) => bound.IndexOf(" [", StringComparison.Ordinal) is var i and > 0 ? bound[..i] : bound;

    /// <param name="eventList">Names the demo's game events; a list in the demo itself replaces it (a demo recorded
    /// mid-map does not always carry one).</param>
    /// <returns>The records written, and where the game event names came from (null: none, so only the round events the
    /// game rules entity tells are in <paramref name="events"/>).</returns>
    private static (int Records, string? EventNames) WriteState(string demoPath, TickClock clock, CMsgSource1LegacyGameEventList? eventList, FileHeader header,
        string binPath, List<JsonObject> events)
    {
        var demo = new CsDemoParser();
        using var bin = File.Create(binPath);
        header.RecordType = ServerStateRecord.Descriptor.FullName;
        header.WriteDelimitedTo(bin);
        int count = 0, lastTick = int.MinValue;

        var descriptors = eventList?.Descriptors.ToDictionary(d => d.Eventid) ?? [];
        string? eventNames = eventList is null ? null : $"{descriptors.Count} game events, from the signon data the plugin logged";
        demo.BaseGameEvents.Source1LegacyGameEventList += list =>
        {
            descriptors = list.Descriptors.ToDictionary(d => d.Eventid);
            eventNames = $"{descriptors.Count} game events, from the demo";
        };
        demo.BaseGameEvents.Source1LegacyGameEvent += e =>
        {
            if (!descriptors.TryGetValue(e.Eventid, out var descriptor) || IsNoise(descriptor.Name)) return;
            int tick = (int)demo.CurrentGameTick.Value;
            var fields = new JsonObject
            {
                ["type"] = "game_event",
                ["name"] = descriptor.Name,
                ["utc_ms"] = Math.Round(clock.UtcUs(tick) / 1000.0, 3),
                ["tick"] = tick,
                ["source"] = "demo",
            };
            // A pawn key ("userid_pawn") repeats the player key next to it ("userid"); alone (bomb_pickup) it names the player.
            var playerKeys = descriptor.Keys.Where(k => k.Type == PlayerSlot).Select(k => k.Name).ToHashSet();
            foreach (var (key, value) in descriptor.Keys.Zip(e.Keys))
            {
                if (key.Type == PawnHandle && key.Name.EndsWith("_pawn", StringComparison.Ordinal) && playerKeys.Contains(key.Name[..^5]))
                    continue;
                fields[key.Name] = Value(demo, key.Type, value);
            }
            events.Add(fields);
        };

        // CS2 does not send round_start / round_freeze_end / round_end as game events to SourceTV; DemoFile tells them from
        // the game rules entity.
        void FromGameRules(string name, JsonObject fields)
        {
            int tick = (int)demo.CurrentGameTick.Value;
            if (events.Any(x => x["name"]?.GetValue<string>() == name && x["tick"]?.GetValue<int>() == tick)) return;
            fields["type"] = "game_event";
            fields["name"] = name;
            fields["utc_ms"] = Math.Round(clock.UtcUs(tick) / 1000.0, 3);
            fields["tick"] = tick;
            fields["source"] = "demo";
            fields["from"] = "game_rules";
            events.Add(fields);
        }
        demo.Source1GameEvents.RoundStart += _ => FromGameRules("round_start", []);
        demo.Source1GameEvents.RoundFreezeEnd += _ => FromGameRules("round_freeze_end", []);
        demo.Source1GameEvents.RoundEnd += e => FromGameRules("round_end", new JsonObject
        {
            ["winner"] = e.Winner,
            ["reason"] = e.Reason,
            ["message"] = e.Message,
        });

        demo.OnCommandFinishPersistent += () =>
        {
            int tick = (int)demo.CurrentGameTick.Value;
            if (tick == lastTick || tick <= 0) return;
            lastTick = tick;
            // demo.GameRules throws until the game rules entity has arrived.
            var rules = demo.Entities.OfType<CCSGameRulesProxy>().FirstOrDefault()?.GameRules;
            var record = new ServerStateRecord
            {
                Tick = tick,
                ServerTimeUs = clock.UtcUs(tick),
                Warmup = rules?.WarmupPeriod ?? false,
                FreezePeriod = rules?.FreezePeriod ?? false,
                RoundsPlayed = rules?.TotalRoundsPlayed ?? 0,
            };
            foreach (var controller in demo.Players)
            {
                if (controller.CSTeamNum is not (CSTeamNumber.Terrorist or CSTeamNumber.CounterTerrorist)) continue;
                var pawn = controller.PlayerPawn;
                var state = new PlayerState
                {
                    SteamId = controller.SteamID,
                    Name = controller.PlayerName,
                    Team = (int)controller.CSTeamNum,
                    Alive = controller.PawnIsAlive,
                    Slot = (int)controller.EntityIndex.Value - 1,
                };
                if (pawn is not null)
                {
                    state.Health = pawn.Health;
                    state.Armor = pawn.ArmorValue;
                    state.Position = Vec(pawn.Origin);
                    state.Pitch = pawn.EyeAngles.Pitch;
                    state.Yaw = pawn.EyeAngles.Yaw;
                    state.Velocity = new Vec3 { X = pawn.Velocity.X, Y = pawn.Velocity.Y, Z = pawn.Velocity.Z };
                    state.ActiveWeapon = pawn.ActiveWeapon?.ServerClass.Name ?? "";
                    state.Ducking = pawn.MovementServices is CCSPlayer_MovementServices m && (m.Ducked || m.Ducking);
                    state.Scoped = pawn.IsScoped;
                    state.Walking = pawn.IsWalking;
                }
                record.Players.Add(state);
            }
            foreach (var entity in demo.Entities)
            {
                string? owner = null;
                string? stateText = null;
                switch (entity)
                {
                    case CBaseCSGrenadeProjectile grenade:
                        owner = (grenade.Thrower as CCSPlayerPawn)?.Controller?.SteamID.ToString(CultureInfo.InvariantCulture);
                        break;
                    case CPlantedC4 planted:
                        stateText = (planted.BombSite == 0 ? "site_a" : "site_b") +
                                    (planted.BombDefused ? ",defused" : planted.BeingDefused ? ",defusing" : planted.BombTicking ? ",ticking" : "");
                        break;
                    case CC4 bomb:
                        owner = (bomb.OwnerEntity as CCSPlayerPawn)?.Controller?.SteamID.ToString(CultureInfo.InvariantCulture);
                        stateText = owner is null ? "dropped" : "carried";
                        break;
                    default:
                        continue;
                }
                record.Entities.Add(new WorldEntity
                {
                    Index = (int)entity.EntityIndex.Value,
                    ClassName = entity.ServerClass.Name,
                    Position = Vec(((CBaseEntity)entity).Origin),
                    OwnerSteamId = owner is null ? 0 : ulong.Parse(owner, CultureInfo.InvariantCulture),
                    State = stateText ?? "",
                });
            }
            record.WriteDelimitedTo(bin);
            count++;
        };

        using var stream = File.OpenRead(demoPath);
        DemoFileReader.Create(demo, stream).ReadAllAsync().AsTask().GetAwaiter().GetResult();
        return (count, eventNames);
    }

    private static Vec3 Vec(Vector v) => new() { X = v.X, Y = v.Y, Z = v.Z };

    // Game event key types (CMsgSource1LegacyGameEventList descriptors).
    private const int PawnHandle = 8, PlayerSlot = 9;

    private static JsonNode? Value(CsDemoParser demo, int type, CMsgSource1LegacyGameEvent.Types.key_t value) => type switch
    {
        1 => value.ValString,
        2 => value.ValFloat,
        3 => value.ValLong,
        4 => value.ValShort,
        5 => value.ValByte,
        6 => value.ValBool,
        7 => value.ValUint64.ToString(CultureInfo.InvariantCulture),
        PlayerSlot => Player(demo, (short)value.ValShort),  // "nobody" is -1, sent as 65535
        PawnHandle => PlayerOfPawn(demo, value.ValLong),
        _ => value.ToString(),
    };

    /// <summary>A player slot as the player: the controller's entity is slot + 1. Null for "nobody" (-1, e.g. no attacker).</summary>
    private static JsonObject? Player(CsDemoParser demo, int slot)
    {
        if (slot < 0) return null;
        var controller = demo.Players.FirstOrDefault(p => p.EntityIndex.Value == slot + 1);
        return new JsonObject
        {
            ["slot"] = slot,
            ["steam_id"] = controller?.SteamID.ToString(CultureInfo.InvariantCulture),
            ["name"] = controller?.PlayerName,
        };
    }

    /// <summary>The player whose pawn an entity handle names (its low 15 bits are the entity index); null for none.</summary>
    private static JsonObject? PlayerOfPawn(CsDemoParser demo, long handle)
    {
        var pawn = demo.Entities.OfType<CCSPlayerPawn>().FirstOrDefault(p => p.EntityIndex.Value == (handle & 0x7FFF));
        return pawn?.Controller is { } controller ? Player(demo, (int)controller.EntityIndex.Value - 1) : null;
    }

    /// <summary>
    /// Events left out of events.jsonl, which holds the events that pace the game (spec v3 §3.2): per-shot, per-step and
    /// UI/SourceTV events. Everything they record is in server_state.bin and the demo.
    /// </summary>
    private static bool IsNoise(string name) =>
        name.StartsWith("hltv_", StringComparison.Ordinal) || name.StartsWith("instructor_", StringComparison.Ordinal) ||
        name.StartsWith("gameinstructor_", StringComparison.Ordinal) || name.StartsWith("buymenu_", StringComparison.Ordinal) ||
        name is "player_footstep" or "player_jump" or "player_sound" or "player_shoot" or "weapon_fire" or "weapon_fire_on_empty"
            or "weapon_reload" or "weapon_zoom" or "weapon_zoom_rifle" or "weaponhud_selection" or "inspect_weapon" or "item_equip"
            or "item_pickup_slerp" or "bullet_impact" or "bullet_damage" or "add_bullet_hit_marker" or "grenade_bounce" or "bomb_beep"
            or "player_ping" or "player_ping_stop" or "player_full_update" or "player_info" or "cs_round_start_beep"
            or "cs_round_final_beep";

    /// <summary>
    /// Server tick → UTC. Each line of the plugin's log carries the server tick it was written in; the earliest line of a tick
    /// marks when that tick ran, and ticks in between are interpolated (the server falls behind and catches up, so a line
    /// through them all would not do). Before the first and after the last logged tick it runs at 64 ticks/s. Without a
    /// log, the demo's first tick is taken as the moment tv_record was sent.
    /// </summary>
    internal sealed class TickClock
    {
        private const double UsPerTick = 1e6 / 64;
        private readonly List<int> _ticks;
        private readonly List<long> _utcUs;
        private readonly string _method;

        private TickClock(List<(int Tick, long UtcUs)> knots, string method)
        {
            _ticks = knots.Select(k => k.Tick).ToList();
            _utcUs = knots.Select(k => k.UtcUs).ToList();
            _method = method;
        }

        public long UtcUs(int tick)
        {
            int i = _ticks.BinarySearch(tick);
            if (i >= 0) return _utcUs[i];
            i = ~i;
            if (i == 0) return _utcUs[0] - (long)Math.Round((_ticks[0] - tick) * UsPerTick);
            if (i == _ticks.Count) return _utcUs[^1] + (long)Math.Round((tick - _ticks[^1]) * UsPerTick);
            double f = (tick - _ticks[i - 1]) / (double)(_ticks[i] - _ticks[i - 1]);
            return (long)Math.Round(_utcUs[i - 1] + f * (_utcUs[i] - _utcUs[i - 1]));
        }

        public JsonObject Describe() => new()
        {
            ["method"] = _method,
            ["logged_ticks"] = _ticks.Count,
            ["first_tick"] = _ticks[0],
            ["last_tick"] = _ticks[^1],
            ["longest_gap_ticks"] = _ticks.Count > 1 ? _ticks.Zip(_ticks.Skip(1), (a, b) => b - a).Max() : null,
        };

        public static TickClock? Fit(List<NetLogLine> lines)
        {
            var knots = new List<(int Tick, long UtcUs)>();
            foreach (var g in lines.Where(l => l.Tick >= 0).GroupBy(l => l.Tick).OrderBy(g => g.Key))
            {
                long utc = g.Min(l => l.UtcUs);
                if (knots.Count == 0 || utc > knots[^1].UtcUs) knots.Add((g.Key, utc));  // keeps the clock monotonic
            }
            return knots.Count == 0 ? null : new TickClock(knots, "earliest plugin log line of each server tick, interpolated between");
        }

        public static TickClock? FromRecordingStart(double? startMs, string demoPath)
        {
            if (startMs is null) return null;
            return new TickClock([(FirstTick(demoPath), (long)Math.Round(startMs.Value * 1000))],
                "first demo tick = when tv_record was sent, then 64 ticks/s (no plugin log)");
        }

        private static int FirstTick(string demoPath)
        {
            var demo = new CsDemoParser();
            int first = 0;
            demo.OnCommandFinishPersistent += () =>
            {
                if (first <= 0) first = (int)demo.CurrentGameTick.Value;
            };
            using var stream = File.OpenRead(demoPath);
            DemoFileReader.Create(demo, stream).ReadAllAsync().AsTask().GetAwaiter().GetResult();
            return first;
        }
    }
}
