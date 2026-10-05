using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Odx.Core;
using Odx.Session;

namespace Odx.Orchestrator;

/// <summary>A CS2 dedicated server on this machine, with the odx_netlog plugin (cs2-plugin/README.md).</summary>
/// <param name="GameDir">Its game/csgo directory: the plugin logs to odx-netlog/, demos go to replays/.</param>
public sealed record Cs2ServerOptions(string RconHost, int RconPort, string RconPassword, string GameDir);

/// <summary>
/// Records the server's side of each round (spec v3 §3): SourceTV records from the countdown on (tv_record over RCON) until
/// the round ends (tv_stoprecord); the demo and the lines the plugin logged meanwhile are then staged in &lt;staging&gt;/server/
/// for <see cref="Cs2ServerCapture"/>. The server must run with tv_enable 1 and tv_delay 0, or the demo lags the round.
/// </summary>
public sealed class Cs2Server(Cs2ServerOptions options, ILogger log)
{
    private readonly Dictionary<string, Dictionary<string, long>> _logStart = [];

    private string NetLogDir => Path.Combine(options.GameDir, "odx-netlog");

    public async Task StartAsync(string sessionId, string stagingDir)
    {
        // Only what the plugin writes from here on belongs to this round; the logs live as long as the server runs.
        lock (_logStart)
            _logStart[sessionId] = Directory.Exists(NetLogDir)
                ? Directory.GetFiles(NetLogDir, "*.jsonl").ToDictionary(f => f, f => new FileInfo(f).Length)
                : [];
        string server = Path.Combine(stagingDir, "server");
        Directory.CreateDirectory(server);
        double utc = NtpResponder.NowUnixMs();
        await ExecAsync($"tv_record replays/{sessionId}");
        File.WriteAllText(Path.Combine(server, Cs2ServerCapture.RecordingFile), new JsonObject { ["tv_record_utc_ms"] = utc }.ToJsonString());
    }

    /// <param name="keep">False for a cancelled round: stop recording and drop it.</param>
    public async Task StopAsync(string sessionId, string stagingDir, bool keep)
    {
        string reply = await ExecAsync("tv_stoprecord");
        Dictionary<string, long>? logStart;
        lock (_logStart) _logStart.Remove(sessionId, out logStart);
        string demo = Path.Combine(options.GameDir, "replays", $"{sessionId}.dem");
        if (!keep)
        {
            TryDelete(demo);
            return;
        }
        if (!reply.Contains("Completed SourceTV demo", StringComparison.Ordinal))
            log.LogWarning("CS2 server: tv_stoprecord for {Session}: {Reply}", sessionId, reply.Trim());

        await Task.Delay(TimeSpan.FromSeconds(1));  // the plugin flushes its log every 250 ms
        string server = Path.Combine(stagingDir, "server");
        Directory.CreateDirectory(server);
        if (File.Exists(demo)) File.Move(demo, Path.Combine(server, Cs2ServerCapture.DemoFile), true);
        else log.LogWarning("CS2 server: no demo {Demo} for {Session}", demo, sessionId);
        CopyNewLines(logStart ?? [], Path.Combine(server, Cs2ServerCapture.NetLogFile));
    }

    /// <summary>The bytes each log file gained since the round started (whole files that appeared since).</summary>
    private void CopyNewLines(Dictionary<string, long> start, string target)
    {
        if (!Directory.Exists(NetLogDir)) return;
        using var output = File.Create(target);
        foreach (string file in Directory.GetFiles(NetLogDir, "*.jsonl").Order())
        {
            // The plugin keeps its log open for writing.
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long from = start.GetValueOrDefault(file);
            if (from >= input.Length) continue;
            input.Position = from;
            input.CopyTo(output);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException e)
        {
            log.LogWarning("CS2 server: cannot delete {Path}: {Error}", path, e.Message);
        }
    }

    /// <summary>Runs a console command; a server that cannot be reached is logged, not fatal to the round.</summary>
    private async Task<string> ExecAsync(string command)
    {
        try
        {
            using var rcon = await SourceRcon.ConnectAsync(options.RconHost, options.RconPort, options.RconPassword, default);
            return await rcon.ExecAsync(command, default);
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            log.LogWarning("CS2 server: {Command}: {Error}", command, e.Message);
            return "";
        }
    }
}

/// <summary>
/// Source RCON over TCP (Valve's Source RCON protocol): packets of int32 size, int32 id, int32 type, then a NUL-terminated
/// body and an empty string, little-endian. Type 3 authenticates (the reply's id is -1 on a wrong password), type 2 runs a
/// command, whose console output comes back as type 0.
/// </summary>
public sealed class SourceRcon : IDisposable
{
    private const int Auth = 3, ExecCommand = 2, AuthResponse = 2, ResponseValue = 0;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private int _nextId = 1;

    private SourceRcon(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static async Task<SourceRcon> ConnectAsync(string host, int port, string password, CancellationToken stop)
    {
        var client = new TcpClient();
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, timeout.Token);
        }
        var rcon = new SourceRcon(client);
        int id = await rcon.SendAsync(Auth, password, stop);
        while (true)
        {
            var (replyId, type, _) = await rcon.ReceiveAsync(stop);
            if (type != AuthResponse) continue;  // some servers send an empty response value first
            if (replyId == id) return rcon;
            rcon.Dispose();
            throw new UnauthorizedAccessException("RCON password refused");
        }
    }

    public async Task<string> ExecAsync(string command, CancellationToken stop)
    {
        int id = await SendAsync(ExecCommand, command, stop);
        while (true)
        {
            var (replyId, type, body) = await ReceiveAsync(stop);
            if (replyId == id && type == ResponseValue) return body;
        }
    }

    private async Task<int> SendAsync(int type, string body, CancellationToken stop)
    {
        int id = _nextId++;
        byte[] text = Encoding.UTF8.GetBytes(body);
        byte[] packet = new byte[4 + 4 + 4 + text.Length + 2];
        BinaryPrimitives.WriteInt32LittleEndian(packet, packet.Length - 4);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        text.CopyTo(packet, 12);
        await _stream.WriteAsync(packet, stop);
        return id;
    }

    private async Task<(int Id, int Type, string Body)> ReceiveAsync(CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        byte[] size = new byte[4];
        await _stream.ReadExactlyAsync(size, timeout.Token);
        byte[] rest = new byte[BinaryPrimitives.ReadInt32LittleEndian(size)];
        if (rest.Length < 10) throw new IOException("RCON: short packet");
        await _stream.ReadExactlyAsync(rest, timeout.Token);
        return (BinaryPrimitives.ReadInt32LittleEndian(rest), BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(4)),
            Encoding.UTF8.GetString(rest, 8, rest.Length - 10));
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }
}
