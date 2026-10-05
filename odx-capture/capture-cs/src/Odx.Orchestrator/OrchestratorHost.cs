using System.Net;
using System.Text.Json.Nodes;
using Odx.Core;
using Odx.Session;

namespace Odx.Orchestrator;

/// <summary>
/// HTTP API (AgentClient and the game server plugin are the clients):
///   GET  /agents                                   WebSocket, one per agent (Hello → Welcome, then Arm/Start/Stop ↔ state/done)
///   POST /game/events                              one GameEvents JSON object
///   PUT  /sessions/{sid}/players/{pid}/files/{name} a capture file after the player's recording stopped
///   GET  /status
/// plus the NTP responder on UDP <see cref="OrchestratorOptions.TimePort"/>.
/// </summary>
public static class OrchestratorHost
{
    public static WebApplication Build(OrchestratorOptions options, string bindAddress = "0.0.0.0")
    {
        var udp = NtpResponder.Bind(options.TimePort);
        int timePort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://{bindAddress}:{options.HttpPort}");
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = null);  // video.mp4 uploads
        builder.Services.AddSingleton(sp =>
            new MatchCoordinator(options, timePort, sp.GetRequiredService<ILogger<MatchCoordinator>>()));
        var app = builder.Build();

        app.Lifetime.ApplicationStarted.Register(() => _ = NtpResponder.RunAsync(udp, app.Lifetime.ApplicationStopping));
        app.Lifetime.ApplicationStopped.Register(udp.Dispose);

        app.UseWebSockets();
        app.Map("/agents", async (HttpContext context, MatchCoordinator coordinator) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await coordinator.HandleAgentAsync(socket, context.RequestAborted);
        });
        app.MapPost("/game/events", async (HttpRequest request, MatchCoordinator coordinator) =>
        {
            try
            {
                if (await JsonNode.ParseAsync(request.Body) is not JsonObject e) return Results.BadRequest("expected a JSON object");
                return Results.Text((await coordinator.PostEventAsync(e)).ToJsonString(AgentMessage.Json), "application/json");
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException)
            {
                return Results.BadRequest(e.Message);
            }
        });
        app.MapPut("/sessions/{sid}/players/{pid}/files/{name}",
            async (string sid, string pid, string name, HttpRequest request, MatchCoordinator coordinator) =>
            {
                if (coordinator.UploadPath(sid, pid, name) is not { } path) return Results.NotFound();
                await using (var file = File.Create(path)) await request.Body.CopyToAsync(file);
                return Results.Ok();
            });
        app.MapGet("/status", (MatchCoordinator coordinator) => Results.Text(coordinator.Status().ToJsonString(), "application/json"));
        return app;
    }
}
