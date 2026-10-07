using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Odx.Session;

namespace Odx.Merge;

/// <summary>
/// HTTP API of odx-merge:
///   PUT    /sessions/{sid}/round/{name}               orchestrator: round.json (registers the round), server_events.jsonl,
///                                                     game_profile.json
///   PUT    /sessions/{sid}/players/{pid}/files/{name} a player's capture file (AgentClient), once their recording stopped
///   PUT    /sessions/{sid}/server/files/{name}        the game server's side of the round (GameServerAgent)
///   POST   /sessions/{sid}/assemble                   orchestrator: build the v3 session → <see cref="AssembledSession"/>
///   DELETE /sessions/{sid}                            orchestrator: a cancelled round
///   GET    /status                                    staged rounds
/// With an access key, every request needs "Authorization: Bearer &lt;key&gt;" (401 otherwise).
/// </summary>
public static class MergeHost
{
    public static WebApplication Build(MergeOptions options, string bindAddress = "0.0.0.0")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://{bindAddress}:{options.HttpPort}");
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = null);  // video.mp4 uploads
        builder.Services.AddSingleton(sp => new SessionMerger(options, sp.GetRequiredService<ILogger<SessionMerger>>()));
        var app = builder.Build();

        if (options.AccessKey is { Length: > 0 } key)
        {
            byte[] expected = Encoding.UTF8.GetBytes("Bearer " + key);
            app.Use(async (context, next) =>
            {
                byte[] presented = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
                if (!CryptographicOperations.FixedTimeEquals(presented, expected))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                await next();
            });
        }
        app.MapPut("/sessions/{sid}/round/{name}", (string sid, string name, HttpRequest request, SessionMerger merger) =>
            SaveAsync(merger.RoundFilePath(sid, name), request));
        app.MapPut("/sessions/{sid}/players/{pid}/files/{name}", (string sid, string pid, string name, HttpRequest request, SessionMerger merger) =>
            SaveAsync(merger.PlayerFilePath(sid, pid, name), request));
        app.MapPut("/sessions/{sid}/server/files/{name}", (string sid, string name, HttpRequest request, SessionMerger merger) =>
            SaveAsync(merger.ServerFilePath(sid, name), request));
        app.MapPost("/sessions/{sid}/assemble", async (string sid, SessionMerger merger) =>
            await merger.AssembleAsync(sid) is { } assembled
                ? Results.Text(JsonSerializer.Serialize(assembled, AgentMessage.Json), "application/json")
                : Results.NotFound($"no round {sid}"));
        app.MapDelete("/sessions/{sid}", (string sid, SessionMerger merger) =>
        {
            merger.Drop(sid);
            return Results.Ok();
        });
        app.MapGet("/status", (SessionMerger merger) =>
            Results.Text(new JsonObject { ["staged"] = new JsonArray(merger.Staged().Select(s => (JsonNode)s).ToArray()) }.ToJsonString(),
                "application/json"));
        return app;
    }

    private static async Task<IResult> SaveAsync(string? path, HttpRequest request)
    {
        if (path is null) return Results.NotFound();
        await using (var file = File.Create(path)) await request.Body.CopyToAsync(file);
        return Results.Ok();
    }
}
