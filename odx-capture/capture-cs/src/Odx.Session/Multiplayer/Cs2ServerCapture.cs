using System.Globalization;
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
/// <item><c>recording.json</c>: when tv_record was sent, the fallback for the demo's first tick without a log.</item>
/// </list>
/// The plugin stamps messages with the game server machine's clock; the orchestrator runs on that machine, so its times are
/// already on the orchestrator's clock.
/// </summary>
public static class Cs2ServerCapture
{
    public const string DemoFile = "demo.dem", NetLogFile = "netmessages.jsonl", RecordingFile = "recording.json";
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

        if (lines.Count > 0)
        {
            var records = lines.Select(PacketOf).ToList();
            PacketLog.WriteRecords(records, header.Clone(), Path.Combine(sessionServerDir, PacketLog.BinFile),
                Path.Combine(sessionServerDir, PacketLog.TextFile));
            File.Copy(Path.Combine(stagingServerDir, NetLogFile), Path.Combine(sessionServerDir, "capture", NetLogFile), true);
            info["packets"] = new JsonObject { ["source"] = "odx_netlog plugin", ["records"] = records.Count };
        }

        string demo = Path.Combine(stagingServerDir, DemoFile);
        if (File.Exists(demo))
        {
            clock ??= TickClock.FromRecordingStart(Path.Combine(stagingServerDir, RecordingFile), demo);
            if (clock is not null)
            {
                int states = WriteState(demo, clock, header.Clone(), Path.Combine(sessionServerDir, StateFile), events);
                info["server_state"] = new JsonObject { ["source"] = "SourceTV demo", ["records"] = states };
            }
            File.Copy(demo, Path.Combine(sessionServerDir, "capture", DemoFile), true);
        }
        info["tick_clock"] = clock?.Describe();
        return new Result(events, info);
    }

    /// <summary>One line of the plugin's log (cs2-plugin/src/plugin.cpp writes them).</summary>
    /// <param name="Tick">The server tick the line was written in.</param>
    internal sealed record NetLogLine(long UtcUs, int Tick, string Direction, int Slot, string SteamId, string Address, string Message,
        int Id, int Buf, string Text);

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
                o["id"]!.GetValue<int>(), o["buf"]!.GetValue<int>(), o["text"]!.GetValue<string>()));
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

    /// <summary>"CNETMsg_Tick [4]" → "CNETMsg_Tick".</summary>
    private static string MessageName(string bound) => bound.IndexOf(" [", StringComparison.Ordinal) is var i and > 0 ? bound[..i] : bound;

    private static int WriteState(string demoPath, TickClock clock, FileHeader header, string binPath, List<JsonObject> events)
    {
        var demo = new CsDemoParser();
        using var bin = File.Create(binPath);
        header.RecordType = ServerStateRecord.Descriptor.FullName;
        header.WriteDelimitedTo(bin);
        int count = 0, lastTick = int.MinValue;

        void Event(string type, JsonObject fields)
        {
            int tick = (int)demo.CurrentGameTick.Value;
            fields["type"] = type;
            fields["utc_ms"] = Math.Round(clock.UtcUs(tick) / 1000.0, 3);
            fields["tick"] = tick;
            fields["source"] = "demo";
            events.Add(fields);
        }

        demo.Source1GameEvents.PlayerDeath += e => Event(GameEvents.Kill, new JsonObject
        {
            ["attacker"] = Player(e.Attacker),
            ["victim"] = Player(e.Player),
            ["assister"] = Player(e.Assister),
            ["weapon"] = e.Weapon,
            ["headshot"] = e.Headshot,
        });
        demo.Source1GameEvents.RoundStart += _ => Event("game_round_start", []);
        demo.Source1GameEvents.RoundFreezeEnd += _ => Event("game_round_freeze_end", []);
        demo.Source1GameEvents.RoundEnd += e => Event("game_round_end", new JsonObject
        {
            ["winner"] = Team(e.Winner),
            ["reason"] = e.Reason,
            ["message"] = e.Message,
        });
        demo.Source1GameEvents.BombPlanted += e => Event("bomb_planted", new JsonObject { ["player"] = Player(e.Player), ["site"] = e.Site });
        demo.Source1GameEvents.BombDefused += e => Event("bomb_defused", new JsonObject { ["player"] = Player(e.Player), ["site"] = e.Site });
        demo.Source1GameEvents.BombExploded += e => Event("bomb_exploded", new JsonObject { ["site"] = e.Site });
        demo.Source1GameEvents.PlayerConnectFull += e => Event(GameEvents.PlayerConnect, new JsonObject { ["player"] = Player(e.Player) });
        demo.Source1GameEvents.PlayerDisconnect += e => Event(GameEvents.PlayerDisconnect, new JsonObject
        {
            ["player"] = Player(e.Player),
            ["reason"] = e.Reason,
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
        return count;
    }

    private static Vec3 Vec(Vector v) => new() { X = v.X, Y = v.Y, Z = v.Z };

    private static JsonObject? Player(CCSPlayerController? p) =>
        p is null ? null : new JsonObject { ["steam_id"] = p.SteamID.ToString(CultureInfo.InvariantCulture), ["name"] = p.PlayerName };

    private static string Team(int team) => team switch { 2 => "terrorists", 3 => "counter_terrorists", _ => team.ToString(CultureInfo.InvariantCulture) };

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

        public static TickClock? FromRecordingStart(string recordingPath, string demoPath)
        {
            if (!File.Exists(recordingPath)) return null;
            double startMs = JsonNode.Parse(File.ReadAllText(recordingPath))?["tv_record_utc_ms"]?.GetValue<double>() ?? 0;
            return new TickClock([(FirstTick(demoPath), (long)Math.Round(startMs * 1000))],
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
