using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Odx.Core;

namespace Odx.Session;

/// <param name="WorkDir">Each round is staged in &lt;WorkDir&gt;/&lt;session&gt;/ until it is uploaded.</param>
/// <param name="AccessKey">The orchestrator's (and odx-merge's) access key; null when they have none.</param>
public sealed record GameServerAgentOptions(Uri Orchestrator, Cs2ServerOptions Cs2, string WorkDir, string? AccessKey = null,
    string Name = "CS2 server");

/// <summary>
/// The game server machine's agent (odx game-server). It connects to the orchestrator like a player's agent but joins no room:
/// every round's Start records the server's side (<see cref="Cs2Server"/>) and samples this machine's clock against the
/// orchestrator's time service; Stop uploads both to odx-merge and reports Done. A cancelled round is dropped.
/// </summary>
public sealed class GameServerAgent(GameServerAgentOptions options, ILogger log)
{
    public const string Id = "game-server";
    private static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(1), NtpTimeout = TimeSpan.FromSeconds(2),
        Retry = TimeSpan.FromSeconds(5);

    private sealed record OpenRound(string Dir, CancellationTokenSource StopSampling, Task Sampling);

    private readonly Cs2Server _cs2 = new(options.Cs2, log);
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { Authorization = options.AccessKey is { } key ? new AuthenticationHeaderValue("Bearer", key) : null },
    };
    private readonly Dictionary<string, OpenRound> _rounds = [];
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private string? _timeServer;
    private Uri? _merge;

    /// <summary>Runs until <paramref name="stop"/>, reconnecting every 5 s; a refused access key ends it.</summary>
    public async Task RunUntilStoppedAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stop);
                if (!stop.IsCancellationRequested) log.LogWarning("orchestrator closed the connection");
            }
            catch (UnauthorizedAccessException e)
            {
                log.LogError("{Error}", e.Message);
                return;
            }
            catch (Exception e) when (e is WebSocketException or HttpRequestException)
            {
                log.LogWarning("{Orchestrator}: {Error}", options.Orchestrator, e.Message);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                await Task.Delay(Retry, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task RunAsync(CancellationToken stop)
    {
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        if (options.AccessKey is { } key) socket.Options.SetRequestHeader("Authorization", $"Bearer {key}");
        var wsUri = new UriBuilder(options.Orchestrator)
        {
            Scheme = options.Orchestrator.Scheme == "https" ? "wss" : "ws",
            Path = "/agents",
        }.Uri;
        try
        {
            await socket.ConnectAsync(wsUri, stop);
        }
        catch (WebSocketException) when (socket.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException($"{options.Orchestrator} refused the access key");
        }
        _socket = socket;
        await SendAsync(new Hello(Id, options.Name, BuildInfo.Version, Role: Hello.GameServerRole), stop);
        log.LogInformation("connected to {Orchestrator}", options.Orchestrator);

        var buffer = new byte[64 * 1024];
        while (!stop.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            if (await AgentClient.ReceiveTextAsync(socket, buffer, stop) is not { } text) break;
            switch (AgentMessage.Parse(text))
            {
                case Welcome w:
                    _timeServer = $"{options.Orchestrator.Host}:{w.TimePort}";
                    _merge = w.MergeUrl is { } merge ? new Uri(merge) : null;
                    if (_merge is null) log.LogWarning("the orchestrator has no odx-merge: rounds will not be uploaded");
                    break;
                case Start s:
                    await StartAsync(s.SessionId);
                    break;
                case Stop s:
                    // Off the receive loop: stopping and uploading take seconds, and the next round may start meanwhile.
                    _ = Task.Run(() => StopAsync(s, stop), CancellationToken.None);
                    break;
            }
        }
    }

    private async Task StartAsync(string sessionId)
    {
        string dir = Path.Combine(options.WorkDir, sessionId);
        Directory.CreateDirectory(Path.Combine(dir, "server"));
        // Sampling first: starting SourceTV waits up to 3 s for its demo, and a short round may end meanwhile.
        var stopSampling = new CancellationTokenSource();
        var sampling = Task.Run(() => SampleClockAsync(Path.Combine(dir, "server", Cs2ServerCapture.ClockSamplesFile), stopSampling.Token));
        lock (_rounds) _rounds[sessionId] = new OpenRound(dir, stopSampling, sampling);
        await _cs2.StartAsync(sessionId, dir);
        log.LogInformation("{Session}: recording", sessionId);
    }

    /// <summary>Every second until <paramref name="stop"/>, and once more then: at least the round's start and end.</summary>
    private async Task SampleClockAsync(string path, CancellationToken stop)
    {
        using var writer = new NtpSampleWriter(new StreamWriter(path));
        while (true)
        {
            try
            {
                if (_timeServer is { } server)
                {
                    writer.Write(SntpClient.Query(server, NtpTimeout));
                    writer.Flush();
                }
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or InvalidDataException)
            {
                log.LogWarning("time service {Server}: {Error}", _timeServer, e.Message);
            }
            if (stop.IsCancellationRequested) return;
            try
            {
                await Task.Delay(SampleEvery, stop);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task StopAsync(Stop s, CancellationToken stop)
    {
        OpenRound? round;
        lock (_rounds) _rounds.Remove(s.SessionId, out round);
        if (round is null)
        {
            if (s.Reason != Stop.Cancelled) await SendAsync(new Done(s.SessionId, false, "the game server agent did not record this round"), stop);
            return;
        }
        await round.StopSampling.CancelAsync();
        await round.Sampling;
        bool keep = s.Reason != Stop.Cancelled;
        await _cs2.StopAsync(s.SessionId, round.Dir, keep);
        string? error = null;
        if (keep)
        {
            try
            {
                await UploadAsync(s.SessionId, Path.Combine(round.Dir, "server"), stop);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
            {
                error = $"upload to odx-merge failed: {e.Message}";
            }
            await SendAsync(new Done(s.SessionId, error is null, error), stop);
            log.LogInformation("{Session}: {Result}", s.SessionId, error ?? "uploaded");
        }
        if (error is null) Directory.Delete(round.Dir, true);
    }

    private async Task UploadAsync(string sessionId, string dir, CancellationToken stop)
    {
        var merge = _merge ?? throw new InvalidOperationException("the orchestrator has no odx-merge");
        foreach (string file in Directory.GetFiles(dir))
        {
            await using var stream = File.OpenRead(file);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var url = new Uri(merge, $"/sessions/{Uri.EscapeDataString(sessionId)}/server/files/{Uri.EscapeDataString(Path.GetFileName(file))}");
            using var response = await _http.PutAsync(url, content, stop);
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task SendAsync(AgentMessage message, CancellationToken stop)
    {
        if (_socket is not { State: WebSocketState.Open } socket) return;
        await _sendLock.WaitAsync(stop);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJson()), WebSocketMessageType.Text, true, stop);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
