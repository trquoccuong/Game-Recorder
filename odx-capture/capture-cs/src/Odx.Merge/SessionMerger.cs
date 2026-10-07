using Odx.Session;

namespace Odx.Merge;

/// <param name="OutRoot">Sessions go to &lt;OutRoot&gt;/&lt;vendor&gt;/…; rounds are staged in <see cref="StagingRoot"/>.</param>
public sealed record MergeOptions(string OutRoot, string Vendor = "odx", int HttpPort = 5190)
{
    public GateTolerances Tolerances { get; init; } = GateTolerances.Default;
    /// <summary>Where rounds are staged until assembled; null: &lt;OutRoot&gt;/.staging/.</summary>
    public string? StagingRoot { get; init; }
    /// <summary>Every request must carry "Authorization: Bearer &lt;key&gt;"; null lets anyone in.</summary>
    public string? AccessKey { get; init; }
}

/// <summary>
/// Staged rounds and their assembly. The orchestrator registers a round (round.json) when it starts, so its players and its
/// game server can upload into &lt;staging&gt;/&lt;session&gt;/; when everyone is done it sends the final round.json and the
/// round's events and asks for the session. Uploads to a round being or already assembled are refused.
/// </summary>
public sealed class SessionMerger(MergeOptions options, ILogger<SessionMerger> log)
{
    // One session at a time: assembling reads the whole demo and every video's frames, and the machine stays responsive to
    // uploads while a queue of rounds waits.
    private readonly SemaphoreSlim _assembly = new(1, 1);
    private readonly HashSet<string> _closed = [];

    private string StagingRoot => options.StagingRoot ?? Path.Combine(options.OutRoot, ".staging");

    /// <summary>A file the orchestrator sends: round.json, server_events.jsonl or game_profile.json.</summary>
    public string? RoundFilePath(string sessionId, string fileName)
    {
        if (!IsPlainName(sessionId) || fileName is not (RoundRecord.FileName or RoundRecord.EventsFileName or RoundRecord.ProfileFileName))
            return null;
        lock (_closed)
            if (_closed.Contains(sessionId)) return null;
        string dir = Path.Combine(StagingRoot, sessionId);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }

    /// <summary>Where a player's upload goes; null when the round is unknown or closed, or the player is not in it.</summary>
    public string? PlayerFilePath(string sessionId, string playerId, string fileName)
    {
        if (!IsPlainName(playerId) || !IsPlainName(fileName) || OpenRound(sessionId) is not { } dir ||
            RoundRecord.Load(dir).Players.All(p => p.PlayerId != playerId))
            return null;
        string playerDir = Path.Combine(dir, "players", playerId);
        Directory.CreateDirectory(playerDir);
        return Path.Combine(playerDir, fileName);
    }

    /// <summary>Where the game server agent's upload goes; null when the round is unknown or closed.</summary>
    public string? ServerFilePath(string sessionId, string fileName)
    {
        if (!IsPlainName(fileName) || OpenRound(sessionId) is not { } dir) return null;
        string serverDir = Path.Combine(dir, "server");
        Directory.CreateDirectory(serverDir);
        return Path.Combine(serverDir, fileName);
    }

    /// <summary>A cancelled round: its staging goes, nothing is kept.</summary>
    public void Drop(string sessionId)
    {
        if (!IsPlainName(sessionId)) return;
        string dir = Path.Combine(StagingRoot, sessionId);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        lock (_closed) _closed.Remove(sessionId);
        log.LogInformation("{Session} dropped", sessionId);
    }

    /// <summary>Null when the round was never registered.</summary>
    public async Task<AssembledSession?> AssembleAsync(string sessionId)
    {
        if (!IsPlainName(sessionId)) return null;
        string staging = Path.Combine(StagingRoot, sessionId);
        if (!File.Exists(Path.Combine(staging, RoundRecord.FileName))) return null;
        lock (_closed) _closed.Add(sessionId);
        await _assembly.WaitAsync();
        try
        {
            var (dir, report) = await Task.Run(() => MultiplayerSession.Assemble(staging, options.OutRoot, options.Vendor, options.Tolerances));
            log.LogInformation("session {Session}: {Result}, {Valid}/{Count} players valid -> {Dir}", report.SessionId,
                report.Pass ? "PASS" : "FAIL", report.ValidPlayers, report.Players.Count, dir);
            foreach (var issue in report.Issues.Concat(report.Players.SelectMany(p => p.Issues)))
                log.LogWarning("{Session}: [{Rule}] {Message}", report.SessionId, issue.Rule, issue.Message);
            return new AssembledSession(dir, report);
        }
        finally
        {
            _assembly.Release();
        }
    }

    public List<string> Staged() =>
        Directory.Exists(StagingRoot) ? Directory.GetDirectories(StagingRoot).Select(d => Path.GetFileName(d)).Order().ToList() : [];

    private string? OpenRound(string sessionId)
    {
        if (!IsPlainName(sessionId)) return null;
        lock (_closed)
            if (_closed.Contains(sessionId)) return null;
        string dir = Path.Combine(StagingRoot, sessionId);
        return File.Exists(Path.Combine(dir, RoundRecord.FileName)) ? dir : null;
    }

    // Uploads come from Windows machines: a backslash is a path separator there whatever this machine thinks.
    private static bool IsPlainName(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(['/', '\\']) < 0 && name == Path.GetFileName(name);
}
