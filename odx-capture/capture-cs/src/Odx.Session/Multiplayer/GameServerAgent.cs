using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Odx.Core;

namespace Odx.Session;

/// <param name="WorkDir">Each round is kept in &lt;WorkDir&gt;/&lt;session&gt;/ (server/ and the round's files).</param>
/// <param name="AccessKey">The orchestrator's access key; null when it has none.</param>
/// <param name="Id">Names this game server to the orchestrator; rooms opened for it (POST /rooms?server=…) are its own.</param>
public sealed record GameServerAgentOptions(Uri Orchestrator, Cs2ServerOptions Cs2, string WorkDir, string? AccessKey = null,
    string Name = "CS2 server", string Id = "game-server");

/// <summary>
/// The game server machine's agent (odx game-server). It connects to the orchestrator like a player's agent but joins no room:
/// every round's Start records the server's side (<see cref="Cs2Server"/>) and samples this machine's clock against the
/// orchestrator's time service; Stop keeps both and reports Done.
/// The round's files (<see cref="RoundFiles"/>) join them once everyone is done. A cancelled round is dropped.
/// </summary>
public sealed class GameServerAgent(GameServerAgentOptions options, ILogger log)
{
    private static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(1), NtpTimeout = TimeSpan.FromSeconds(2),
        Retry = TimeSpan.FromSeconds(5);

    private sealed record OpenRound(string Dir, CancellationTokenSource StopSampling, Task Sampling);

    private readonly Cs2Server _cs2 = new(options.Cs2, log);
    private readonly Dictionary<string, OpenRound> _rounds = [];
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private string? _timeServer;

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
        await SendAsync(new Hello(options.Id, options.Name, BuildInfo.Version, Role: Hello.GameServerRole), stop);
        log.LogInformation("connected to {Orchestrator}", options.Orchestrator);

        var buffer = new byte[64 * 1024];
        while (!stop.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            if (await AgentClient.ReceiveTextAsync(socket, buffer, stop) is not { } text) break;
            switch (AgentMessage.Parse(text))
            {
                case Welcome w:
                    _timeServer = $"{options.Orchestrator.Host}:{w.TimePort}";
                    break;
                case RoundFiles files:
                    string kept = Path.Combine(options.WorkDir, files.SessionId);
                    try
                    {
                        if (Directory.Exists(kept)) files.WriteTo(kept);
                    }
                    catch (IOException e)
                    {
                        log.LogError("{Session}: cannot write the round's files: {Error}", files.SessionId, e.Message);
                    }
                    break;
                case Start s:
                    await StartAsync(s.SessionId);
                    break;
                case Stop s:
                    // Off the receive loop: stopping takes seconds, and the next round may start meanwhile.
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
        if (!keep)
        {
            Directory.Delete(round.Dir, true);
            return;
        }
        await SendAsync(new Done(s.SessionId, true), stop);
        log.LogInformation("{Session}: kept in {Dir}", s.SessionId, round.Dir);
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
