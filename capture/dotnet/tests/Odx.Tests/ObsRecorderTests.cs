using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class ObsRecorderTests
{
    [Fact]
    public void Authentication_matches_the_obs_websocket_5_example()
    {
        // obs-websocket protocol docs, "Creating an authentication string": password "supersecretpassword".
        Assert.Equal("1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=",
            ObsClient.Authentication("supersecretpassword", "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=",
                "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY="));
    }

    [SkippableFact]
    public async Task Obs_recording_is_remuxed_and_put_on_the_qpc_clock()
    {
        Skip.IfNot(SessionFixtures.HasFfmpeg, "ffmpeg/ffprobe not on PATH");
        string dir = Path.Combine(Path.GetTempPath(), $"odx-obs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await using var obs = await FakeObs.StartAsync("secret", Path.Combine(dir, "obs.mkv"));
        try
        {
            using var stop = new CancellationTokenSource();
            var statuses = new List<RecorderStatus>();
            var run = ObsRecorder.RunAsync(new ObsRecordOptions(obs.Address, "secret", Path.Combine(dir, "out"), "ffmpeg"),
                s => { lock (statuses) statuses.Add(s); }, stop.Token);
            await Task.Delay(2500);
            stop.Cancel();

            Assert.Null(await run);
            Assert.Equal([RecorderStatus.Started, RecorderStatus.Stopped],
                statuses.Select(s => s.Event).Where(e => e != RecorderStatus.Progress));
            var info = Mp4Reader.Read(Path.Combine(dir, "out", "video.mp4"));
            var frames = VideoFrameLogReader.Read(Path.Combine(dir, "out", "video_frames.csv"));
            Assert.Equal(obs.Frames, info.Frames);
            Assert.Equal(info.Frames, frames.Count);
            // The file starts at StartRecord; outputDuration, which starts late, must not move t0.
            Assert.InRange(frames[0].TGridMs - obs.StartQpcMs, -5, 5);
            Assert.Equal(1000.0 / 30, frames[1].TGridMs - frames[0].TGridMs, 6);
            var recording = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "out", "recording.json")))!;
            Assert.Equal(30, recording["fps"]!.GetValue<double>());
            Assert.Equal(1920, recording["screen_width"]!.GetValue<int>());
            Assert.Equal(160, recording["width"]!.GetValue<int>());
            Assert.False(File.Exists(Path.Combine(dir, "obs.mkv")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_wrong_password_is_reported()
    {
        await using var obs = await FakeObs.StartAsync("secret", Path.Combine(Path.GetTempPath(), "unused.mkv"));
        var e = await Assert.ThrowsAsync<ObsException>(() => ObsClient.ConnectAsync(obs.Address, "wrong"));
        Assert.Contains("password", e.Message);
    }

    /// <summary>obs-websocket 5 as far as ObsRecorder uses it; StopRecord writes a 160x90, 30 fps MKV with ffmpeg.</summary>
    private sealed class FakeObs : IAsyncDisposable
    {
        private const double Fps = 30, OutputStartupMs = 400;
        private readonly WebApplication _app;
        private readonly string _password, _file;
        private double? _startQpcMs;

        public string Address { get; private set; } = "";
        public double StartQpcMs { get; private set; } = double.NaN;
        public long Frames { get; private set; }

        private FakeObs(WebApplication app, string password, string file) => (_app, _password, _file) = (app, password, file);

        public static async Task<FakeObs> StartAsync(string password, string file)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var obs = new FakeObs(app, password, file);
            app.UseWebSockets();
            app.Run(async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    return;
                }
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await obs.ServeAsync(socket);
            });
            await app.StartAsync();
            obs.Address = new Uri(app.Urls.First()).Authority;
            return obs;
        }

        private async Task ServeAsync(WebSocket socket)
        {
            const string salt = "c2FsdA==", challenge = "Y2hhbGxlbmdl";
            await Send(socket, new JsonObject { ["op"] = 0, ["d"] = new JsonObject
                { ["rpcVersion"] = 1, ["authentication"] = new JsonObject { ["salt"] = salt, ["challenge"] = challenge } } });
            var identify = await Receive(socket);
            if ((string?)identify?["d"]?["authentication"] != ObsClient.Authentication(_password, salt, challenge))
            {
                await socket.CloseAsync((WebSocketCloseStatus)4009, "Authentication failed.", CancellationToken.None);
                return;
            }
            await Send(socket, new JsonObject { ["op"] = 2, ["d"] = new JsonObject { ["negotiatedRpcVersion"] = 1 } });
            while (await Receive(socket) is { } message)
            {
                var d = message["d"]!;
                string type = (string)d["requestType"]!;
                JsonObject data = [];
                switch (type)
                {
                    case "GetVideoSettings":
                        data = new JsonObject { ["fpsNumerator"] = 30, ["fpsDenominator"] = 1, ["baseWidth"] = 1920,
                            ["baseHeight"] = 1080, ["outputWidth"] = 160, ["outputHeight"] = 90 };
                        break;
                    case "StartRecord":
                        StartQpcMs = (_startQpcMs = QpcClock.NowMs()).Value;
                        break;
                    case "GetRecordStatus":
                        // As OBS 32 on Windows: outputDuration starts counting once the output is up (~1.1 s there).
                        long received = _startQpcMs is double s
                            ? (long)Math.Max(0, Math.Floor((QpcClock.NowMs() - s - OutputStartupMs) * Fps / 1000)) : 0;
                        data = new JsonObject { ["outputActive"] = _startQpcMs is not null,
                            ["outputDuration"] = Math.Round(received * 1000 / Fps) };
                        break;
                    case "StopRecord":
                        Frames = (long)Math.Floor((QpcClock.NowMs() - _startQpcMs!.Value) * Fps / 1000);
                        _startQpcMs = null;
                        WriteVideo(Frames);
                        data = new JsonObject { ["outputPath"] = _file };
                        break;
                }
                await Send(socket, new JsonObject { ["op"] = 7, ["d"] = new JsonObject { ["requestType"] = type,
                    ["requestId"] = (string?)d["requestId"], ["requestStatus"] = new JsonObject { ["result"] = true, ["code"] = 100 },
                    ["responseData"] = data } });
                if (type == "StopRecord")
                    await Send(socket, new JsonObject { ["op"] = 5, ["d"] = new JsonObject { ["eventType"] = "RecordStateChanged",
                        ["eventData"] = new JsonObject { ["outputActive"] = false, ["outputState"] = "OBS_WEBSOCKET_OUTPUT_STOPPED",
                            ["outputPath"] = _file } } });
            }
        }

        private void WriteVideo(long frames)
        {
            var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
            foreach (string a in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=30", "-frames:v",
                         frames.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:v", "libx264", "-pix_fmt",
                         "yuv420p", _file })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardError.ReadToEnd();
            p.WaitForExit();
        }

        private static Task Send(WebSocket socket, JsonObject message) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

        private static async Task<JsonObject?> Receive(WebSocket socket)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    return null;
                }
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return null;
                }
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) return JsonNode.Parse(message.ToArray()) as JsonObject;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
