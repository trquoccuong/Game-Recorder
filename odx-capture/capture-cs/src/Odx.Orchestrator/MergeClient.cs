using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Odx.Session;

namespace Odx.Orchestrator;

/// <summary>odx-merge's orchestrator side (MergeHost): register a round, have it assembled, or drop it.</summary>
public sealed class MergeClient(Uri merge, string? accessKey)
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = merge,
        // odx-merge assembles one round at a time: a round can wait behind others.
        Timeout = TimeSpan.FromHours(1),
        DefaultRequestHeaders = { Authorization = accessKey is { } key ? new AuthenticationHeaderValue("Bearer", key) : null },
    };

    /// <summary>From here on odx-merge accepts the round's uploads (from its players and the game server agent).</summary>
    public async Task OpenAsync(RoundRecord round, string profileJson)
    {
        await PutAsync(round.SessionId, RoundRecord.FileName, round.ToJson());
        await PutAsync(round.SessionId, RoundRecord.ProfileFileName, profileJson);
    }

    public async Task<AssembledSession> AssembleAsync(RoundRecord round, IEnumerable<JsonObject> events)
    {
        await PutAsync(round.SessionId, RoundRecord.FileName, round.ToJson());
        await PutAsync(round.SessionId, RoundRecord.EventsFileName, string.Concat(events.Select(e => e.ToJsonString(AgentMessage.Json) + "\n")));
        using var response = await _http.PostAsync($"/sessions/{Uri.EscapeDataString(round.SessionId)}/assemble", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AssembledSession>(AgentMessage.Json)
               ?? throw new InvalidDataException("empty answer from odx-merge");
    }

    public async Task DropAsync(string sessionId)
    {
        using var response = await _http.DeleteAsync($"/sessions/{Uri.EscapeDataString(sessionId)}");
        response.EnsureSuccessStatusCode();
    }

    private async Task PutAsync(string sessionId, string fileName, string text)
    {
        using var content = new StringContent(text, Encoding.UTF8, "application/octet-stream");
        using var response = await _http.PutAsync($"/sessions/{Uri.EscapeDataString(sessionId)}/round/{fileName}", content);
        response.EnsureSuccessStatusCode();
    }
}
