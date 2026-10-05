using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Odx.Core;
using Odx.Orchestrator;
using Odx.Session;
using Odx.Session.Proto;
using Xunit;

namespace Odx.Tests;

public sealed class ProtobufTextTests
{
    [Fact]
    public void Text_format_becomes_json()
    {
        var json = ProtobufText.ToJson("CMsg_Test [7]\n{\ntick: 3230\nname: \"a\\\"b\\n\"\nflag: true\nrate: -0.5\nkind: k_EFoo\n" +
                                       "data: \"\\001\\377x\"\nsub {\n  v: -2\n}\nsub {\n  v: 3\n}\nlist: 1\nlist: 2\nbig: 18446744073709551615\n}\n");

        Assert.Equal(3230, json["tick"]!.GetValue<long>());
        Assert.Equal("a\"b\n", json["name"]!.GetValue<string>());
        Assert.True(json["flag"]!.GetValue<bool>());
        Assert.Equal(-0.5, json["rate"]!.GetValue<double>());
        Assert.Equal("k_EFoo", json["kind"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String([1, 255, (byte)'x']), json["data"]!["$bytes"]!.GetValue<string>());
        Assert.Equal([-2L, 3L], json["sub"]!.AsArray().Select(s => s!["v"]!.GetValue<long>()));
        Assert.Equal([1L, 2L], json["list"]!.AsArray().Select(v => v!.GetValue<long>()));
        Assert.Equal(ulong.MaxValue, json["big"]!.GetValue<ulong>());
    }

    [Fact]
    public void Every_message_the_plugin_logged_converts()
    {
        var lines = Cs2ServerCapture.ReadNetLog(Cs2Fixtures.NetLog);
        Assert.Equal(210, lines.Count);
        foreach (var line in lines) Assert.NotNull(ProtobufText.ToJson(line.Text));
        var move = ProtobufText.ToJson(lines.First(l => l.Message.StartsWith("CCLCMsg_Move", StringComparison.Ordinal)).Text);
        Assert.NotNull(move["data"]!["$bytes"]);
    }
}

internal static class Cs2Fixtures
{
    public static string NetLog => Path.Combine(AppContext.BaseDirectory, "fixtures", "cs2_netmessages.jsonl");
    /// <summary>12 s of 20 bots on de_dust2 across a round restart (tv_record mid-map; this one carries the game event list).</summary>
    public static string Demo => Path.Combine(AppContext.BaseDirectory, "fixtures", "cs2_demo.dem");
    /// <summary>The game event list (CMsgSource1LegacyGameEventList) of the build that recorded <see cref="Demo"/> (14188).</summary>
    public static string GameEvents => Path.Combine(AppContext.BaseDirectory, "fixtures", "cs2_gameevents.bin");

    public static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "odx-cs2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public sealed class Cs2ServerCaptureTests
{
    [Fact]
    public void Net_messages_become_server_packets()
    {
        string staging = Cs2Fixtures.TempDir(), session = Cs2Fixtures.TempDir();
        File.Copy(Cs2Fixtures.NetLog, Path.Combine(staging, Cs2ServerCapture.NetLogFile));

        Cs2ServerCapture.Write(staging, session, new FileHeader { SessionId = "s1" });

        var (header, records) = PacketLog.Read(Path.Combine(session, PacketLog.BinFile));
        Assert.Equal(PacketRecord.Descriptor.FullName, header.RecordType);
        Assert.Equal(210, records.Count);
        var tick = records.First(r => r.Message == "CNETMsg_Tick" && r.Direction == PacketRecord.Types.Direction.Out);
        Assert.Equal("76561197960265729", tick.PeerId);
        Assert.Equal("netmessage", tick.Transport);
        Assert.Equal(tick.LocalTimeUs, tick.ServerTimeUs);
        Assert.NotNull(JsonNode.Parse(tick.DecodedJson)!["tick"]);
        Assert.Equal(211, File.ReadAllLines(Path.Combine(session, PacketLog.TextFile)).Length);
        Assert.True(File.Exists(Path.Combine(session, "capture", Cs2ServerCapture.NetLogFile)));
    }

    [Fact]
    public void Server_ticks_go_on_the_clock_by_the_earliest_line_of_each_tick()
    {
        Cs2ServerCapture.NetLogLine Line(long utcUs, int tick) => new(utcUs, tick, "in", 2, "0", "", "CCLCMsg_Move [21]", 21, -1, "");
        var clock = Cs2ServerCapture.TickClock.Fit(
        [
            Line(1_000_000, 100), Line(1_016_000, 101), Line(1_015_000, 101), Line(1_070_000, 104),
            Line(1_060_000, 105),  // earlier than tick 104: dropped, the clock never runs backwards
            Line(2_000_000, -1),   // not written in a tick
        ])!;

        Assert.Equal(1_000_000, clock.UtcUs(100));
        Assert.Equal(1_015_000, clock.UtcUs(101));
        Assert.Equal(1_033_333, clock.UtcUs(102));
        Assert.Equal(1_000_000 - 15_625, clock.UtcUs(99));
        Assert.Equal(1_070_000 + 2 * 15_625, clock.UtcUs(106));
        Assert.Equal(3, clock.Describe()["logged_ticks"]!.GetValue<int>());
        Assert.Null(Cs2ServerCapture.TickClock.Fit([Line(1, -1)]));
    }

    [Fact]
    public void The_demo_becomes_one_state_record_per_tick_from_when_recording_started()
    {
        string staging = Cs2Fixtures.TempDir(), session = Cs2Fixtures.TempDir();
        File.Copy(Cs2Fixtures.Demo, Path.Combine(staging, Cs2ServerCapture.DemoFile));
        const double recordStartMs = 1_791_191_292_846.408;
        File.WriteAllText(Path.Combine(staging, Cs2ServerCapture.RecordingFile), $"{{\"tv_record_utc_ms\": {recordStartMs.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");

        var result = Cs2ServerCapture.Write(staging, session, new FileHeader { SessionId = "s1" });

        using var bin = File.OpenRead(Path.Combine(session, Cs2ServerCapture.StateFile));
        Assert.Equal(ServerStateRecord.Descriptor.FullName, FileHeader.Parser.ParseDelimitedFrom(bin).RecordType);
        var states = new List<ServerStateRecord>();
        while (bin.Position < bin.Length) states.Add(ServerStateRecord.Parser.ParseDelimitedFrom(bin));
        Assert.Equal(states.Count, result.Info["server_state"]!["records"]!.GetValue<int>());
        Assert.InRange(states.Count, 12 * 64 - 64, 12 * 64 + 64);  // about 12 s at 64 ticks/s
        Assert.Equal((long)Math.Round(recordStartMs * 1000), states[0].ServerTimeUs);
        Assert.True(states.Zip(states.Skip(1)).Count(p => p.Second.Tick - p.First.Tick == 1) >= states.Count * 0.95);

        var last = states[^1];
        Assert.Equal(20, last.Players.Count);
        Assert.All(last.Players, p =>
        {
            Assert.Contains(p.Team, new[] { 2, 3 });
            Assert.NotEmpty(p.Name);
            Assert.True(p.Alive);
            Assert.Equal(100, p.Health);
            Assert.NotEqual(0, p.Position.X);
            Assert.NotEmpty(p.ActiveWeapon);
        });
        Assert.Contains(last.Entities, e => e.ClassName == "CC4");
        Assert.True(File.Exists(Path.Combine(session, "capture", Cs2ServerCapture.DemoFile)));
        Assert.StartsWith("271 game events, from the demo", result.Info["game_event_names"]!.GetValue<string>());
        Assert.Contains(result.Events, e => e["name"]!.GetValue<string>() == "bomb_pickup");
    }

    [Fact]
    public void Signon_data_gives_the_demo_its_game_events_and_its_messages_go_to_the_packets()
    {
        string staging = Cs2Fixtures.TempDir(), session = Cs2Fixtures.TempDir();
        File.Copy(Cs2Fixtures.Demo, Path.Combine(staging, Cs2ServerCapture.DemoFile));
        const double recordStartMs = 1_791_191_292_846.408;
        File.WriteAllText(Path.Combine(staging, Cs2ServerCapture.RecordingFile), $"{{\"tv_record_utc_ms\": {recordStartMs.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
        byte[] eventList = File.ReadAllBytes(Cs2Fixtures.GameEvents);
        byte[] tick = new CNETMsg_Tick { Tick = 1234 }.ToByteArray();
        long before = (long)(recordStartMs * 1000) - 60_000_000, during = (long)(recordStartMs * 1000) + 1_000_000;
        File.WriteAllLines(Path.Combine(staging, Cs2ServerCapture.SendDataFile),
        [
            SendDataLine(before, (205, eventList), (4, tick)),  // a player joined before the round
            SendDataLine(during, (4, tick)),
        ]);

        var result = Cs2ServerCapture.Write(staging, session, new FileHeader { SessionId = "s1" });

        Assert.StartsWith("271 game events", result.Info["game_event_names"]!.GetValue<string>());
        Assert.All(result.Events, e => Assert.Equal("game_event", GameEvents.Type(e)));
        var pickup = Assert.Single(result.Events, e => e["name"]!.GetValue<string>() == "bomb_pickup");
        Assert.Equal("demo", pickup["source"]!.GetValue<string>());
        Assert.NotEmpty(pickup["userid_pawn"]!["name"]!.GetValue<string>());  // its only key: the pawn, named after its player
        Assert.InRange(GameEvents.UtcMs(pickup), recordStartMs, recordStartMs + 13_000);
        var spawn = result.Events.First(e => e["name"]!.GetValue<string>() == "player_spawn");
        Assert.NotNull(spawn["userid"]!["slot"]);
        Assert.Null(spawn["userid_pawn"]);  // the same player as userid
        Assert.DoesNotContain(result.Events, e => e["name"]!.GetValue<string>() is "item_equip" or "player_footstep");

        var (_, records) = PacketLog.Read(Path.Combine(session, PacketLog.BinFile));
        var signon = Assert.Single(records);  // only what was sent during the round
        Assert.Equal("CNETMsg_Tick", signon.Message);
        Assert.Equal(during, signon.ServerTimeUs);
        Assert.Equal(tick, signon.Payload.ToByteArray());
        Assert.Equal(1234, JsonNode.Parse(signon.DecodedJson)!["tick"]!.GetValue<int>());
        Assert.True(File.Exists(Path.Combine(session, "capture", Cs2ServerCapture.SendDataFile)));
    }

    /// <summary>A SendData line as the plugin writes it: messages as UBitVar id, varint size, bytes, LSB first.</summary>
    private static string SendDataLine(long utcUs, params (int Id, byte[] Payload)[] messages)
    {
        var bits = new List<bool>();
        void Put(long value, int count)
        {
            for (int i = 0; i < count; i++) bits.Add((value >> i & 1) != 0);
        }
        foreach (var (id, payload) in messages)
        {
            if (id < 16) Put(id, 6);
            else
            {
                Put(id & 15 | 2 << 4, 6);
                Put(id >> 4, 8);
            }
            for (int size = payload.Length; ; size >>= 7)
            {
                Put(size & 0x7F | (size >= 0x80 ? 0x80 : 0), 8);
                if (size < 0x80) break;
            }
            foreach (byte b in payload) Put(b, 8);
        }
        byte[] data = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) data[i >> 3] |= (byte)(1 << (i & 7));
        return new JsonObject
        {
            ["utc_us"] = utcUs, ["tick"] = 1, ["direction"] = "out", ["slot"] = 0, ["steamid"] = "76561197960265729",
            ["address"] = "127.0.0.1:27005", ["message"] = "SendData", ["id"] = -1, ["buf"] = 1, ["text"] = "", ["bits"] = bits.Count,
            ["data"] = Convert.ToBase64String(data),
        }.ToJsonString();
    }
}

/// <summary>A Source RCON server on loopback that answers each command with <see cref="Reply"/>.</summary>
internal sealed class FakeRcon : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _password;

    public FakeRcon(string password)
    {
        _password = password;
        _listener.Start();
        _ = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public List<string> Commands { get; } = [];
    public Func<string, string> Reply { get; set; } = _ => "";

    private async Task AcceptAsync()
    {
        try
        {
            while (true) _ = ServeAsync(await _listener.AcceptTcpClientAsync());
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        try
        {
            while (true)
            {
                byte[] size = new byte[4];
                await stream.ReadExactlyAsync(size);
                byte[] rest = new byte[BinaryPrimitives.ReadInt32LittleEndian(size)];
                await stream.ReadExactlyAsync(rest);
                int id = BinaryPrimitives.ReadInt32LittleEndian(rest), type = BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(4));
                string body = Encoding.UTF8.GetString(rest, 8, rest.Length - 10);
                if (type == 3)
                {
                    await SendAsync(stream, id, 0, "");  // Source servers send an empty response value before the auth response
                    await SendAsync(stream, body == _password ? id : -1, 2, "");
                    continue;
                }
                lock (Commands) Commands.Add(body);
                await SendAsync(stream, id, 0, Reply(body));
            }
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task SendAsync(NetworkStream stream, int id, int type, string body)
    {
        byte[] text = Encoding.UTF8.GetBytes(body);
        byte[] packet = new byte[14 + text.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, packet.Length - 4);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        text.CopyTo(packet, 12);
        await stream.WriteAsync(packet);
    }

    public void Dispose() => _listener.Stop();
}

public sealed class SourceRconTests
{
    [Fact]
    public async Task Runs_commands_after_authenticating()
    {
        using var server = new FakeRcon("pw") { Reply = c => $"ran {c}" };
        using var rcon = await SourceRcon.ConnectAsync("127.0.0.1", server.Port, "pw", default);
        Assert.Equal("ran status", await rcon.ExecAsync("status", default));
        Assert.Equal("ran tv_stoprecord", await rcon.ExecAsync("tv_stoprecord", default));
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        using var server = new FakeRcon("pw");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SourceRcon.ConnectAsync("127.0.0.1", server.Port, "nope", default));
    }
}

public sealed class Cs2ServerTests
{
    [Fact]
    public async Task A_round_stages_its_demo_and_only_the_log_lines_written_during_it()
    {
        string game = Cs2Fixtures.TempDir(), staging = Cs2Fixtures.TempDir();
        string logs = Path.Combine(game, "odx-netlog");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(Path.Combine(game, "replays"));
        File.WriteAllText(Path.Combine(logs, "a.jsonl"), "before\n");
        using var rcon = new FakeRcon("pw");
        rcon.Reply = c =>
        {
            if (c != "tv_stoprecord") return "";
            File.WriteAllText(Path.Combine(game, "replays", "m-r01.dem"), "demo");
            return "Completed SourceTV demo \"replays/m-r01.dem\"";
        };
        var server = new Cs2Server(new Cs2ServerOptions("127.0.0.1", rcon.Port, "pw", game), NullLogger.Instance);

        double before = NtpResponder.NowUnixMs();
        await server.StartAsync("m-r01", staging);
        File.AppendAllText(Path.Combine(logs, "a.jsonl"), "during 1\n");
        File.WriteAllText(Path.Combine(logs, "b.jsonl"), "during 2\n");  // the server restarted mid-round
        await server.StopAsync("m-r01", staging, keep: true);

        Assert.Equal(["tv_record replays/m-r01", "tv_stoprecord"], rcon.Commands);
        string staged = Path.Combine(staging, "server");
        Assert.Equal("demo", File.ReadAllText(Path.Combine(staged, Cs2ServerCapture.DemoFile)));
        Assert.False(File.Exists(Path.Combine(game, "replays", "m-r01.dem")));
        Assert.Equal("during 1\nduring 2\n", File.ReadAllText(Path.Combine(staged, Cs2ServerCapture.NetLogFile)));
        double started = JsonNode.Parse(File.ReadAllText(Path.Combine(staged, Cs2ServerCapture.RecordingFile)))!["tv_record_utc_ms"]!.GetValue<double>();
        Assert.InRange(started, before, NtpResponder.NowUnixMs());
    }

    [Fact]
    public async Task A_cancelled_round_drops_its_demo()
    {
        string game = Cs2Fixtures.TempDir(), staging = Cs2Fixtures.TempDir();
        Directory.CreateDirectory(Path.Combine(game, "replays"));
        using var rcon = new FakeRcon("pw");
        rcon.Reply = c =>
        {
            if (c == "tv_stoprecord") File.WriteAllText(Path.Combine(game, "replays", "m-r02.dem"), "demo");
            return "";
        };
        var server = new Cs2Server(new Cs2ServerOptions("127.0.0.1", rcon.Port, "pw", game), NullLogger.Instance);

        await server.StartAsync("m-r02", staging);
        await server.StopAsync("m-r02", staging, keep: false);

        Assert.False(File.Exists(Path.Combine(game, "replays", "m-r02.dem")));
        Assert.False(File.Exists(Path.Combine(staging, "server", Cs2ServerCapture.DemoFile)));
    }
}
