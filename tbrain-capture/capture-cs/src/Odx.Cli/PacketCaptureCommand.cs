// odx packet-capture --pipe <name> --user <SID>
// The elevated half of packet capture (pktmon needs administrator rights): odx-app starts it with a UAC prompt the first
// time a room wants it and keeps it until the app closes, sending start/stop requests over the named pipe, which only
// that user's account may open. Exits, stopping any capture, when the app closes the pipe.
#if WINDOWS
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Odx.Session;

namespace Odx.Cli;

public static class PacketCaptureCommand
{
    public static int Run(string[] args)
    {
        string? pipeName = null, user = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Length: pipeName = args[++i]; break;
                case "--user" when i + 1 < args.Length: user = args[++i]; break;
                default: pipeName = null; i = args.Length; break;
            }
        }
        if (pipeName is null || user is null)
        {
            Console.Error.WriteLine("usage: odx packet-capture --pipe <name> --user <SID>");
            return 2;
        }
        if (!PktmonCapture.IsElevated)
        {
            Console.Error.WriteLine("odx packet-capture needs administrator rights (pktmon)");
            return 1;
        }

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(user), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.None, 0, 0, security);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            try
            {
                pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return 1;
            }
        }

        var capture = new PktmonCapture();
        using var reader = new StreamReader(pipe, new UTF8Encoding(false));
        // Not disposed: disposing flushes, and once the app has closed the pipe that throws (it has nothing left to flush).
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            while (reader.ReadLine() is { } line)
            {
                JsonObject answer;
                try
                {
                    var request = JsonNode.Parse(line)!.AsObject();
                    switch ((string?)request["cmd"])
                    {
                        case "start":
                            capture.StartAsync(request["ports"]!.AsArray().Select(p => (string)p!).ToList(), (string)request["etl"]!,
                                default).GetAwaiter().GetResult();
                            break;
                        case "stop":
                            capture.StopAsync((string)request["etl"]!, (string)request["pcapng"]!, default).GetAwaiter().GetResult();
                            break;
                        default:
                            throw new ArgumentException($"unknown request: {line}");
                    }
                    answer = new JsonObject { ["ok"] = true };
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or
                                              System.Text.Json.JsonException or NullReferenceException)
                {
                    answer = new JsonObject { ["ok"] = false, ["error"] = e.Message };
                }
                writer.WriteLine(answer.ToJsonString());
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            PktmonCapture.StopQuietly();
        }
        return 0;
    }
}
#endif
