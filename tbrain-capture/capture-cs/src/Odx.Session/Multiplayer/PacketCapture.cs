using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace Odx.Session;

/// <summary>Records a round's network traffic (spec v3 §2.3) into a pcapng file next to the video.</summary>
public interface IPacketCapture
{
    /// <param name="ports">"tcp/6112", "udp/27015"… (<see cref="PacketCaptureSpec.Ports"/>).</param>
    Task StartAsync(IReadOnlyList<string> ports, string etlPath, CancellationToken stop);

    Task StopAsync(string etlPath, string pcapngPath, CancellationToken stop);
}

/// <summary>
/// pktmon, built into Windows 10 2004+: captures the game's ports on the network adapters only (so no packet is logged twice
/// on its way through the stack) and converts the log to pcapng. It needs administrator rights.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PktmonCapture : IPacketCapture
{
    public static bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public Task StartAsync(IReadOnlyList<string> ports, string etlPath, CancellationToken stop) => Task.Run(() =>
    {
        Run("filter", "remove");
        foreach (string port in ports)
        {
            if (!PacketCaptureSpec.PortPattern().IsMatch(port)) throw new ArgumentException($"not a port spec: {port}");
            string[] parts = port.Split('/');
            Run("filter", "add", "odx", "-t", parts[0].ToUpperInvariant(), "-p", parts[1]);
        }
        // 2 GB circular log: hours of game traffic, and a round never needs more.
        Run("start", "--capture", "--comp", "nics", "--pkt-size", "0", "--file-size", "2048", "--file-name", Checked(etlPath, ".etl"));
    }, stop);

    public Task StopAsync(string etlPath, string pcapngPath, CancellationToken stop) => Task.Run(() =>
    {
        try
        {
            Run("stop");
            Run("etl2pcap", Checked(etlPath, ".etl"), "--out", Checked(pcapngPath, ".pcapng"));
        }
        finally
        {
            Run("filter", "remove");
            File.Delete(etlPath);
        }
    }, stop);

    /// <summary>Stops a capture left running (the helper's app went away).</summary>
    public static void StopQuietly()
    {
        try
        {
            Run("stop");
            Run("filter", "remove");
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string Checked(string path, string extension) =>
        Path.IsPathFullyQualified(path) && path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? path
            : throw new ArgumentException($"expected a full {extension} path: {path}");

    /// <exception cref="InvalidOperationException">pktmon failed.</exception>
    private static void Run(params string[] args)
    {
        var psi = new ProcessStartInfo("pktmon")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start pktmon");
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        // "stop" without a running capture and "filter remove" without filters are fine.
        if (p.ExitCode != 0 && args[0] is not ("stop" or "filter"))
            throw new InvalidOperationException($"pktmon {string.Join(' ', args)}: {output.Trim()}");
    }
}

/// <summary>
/// odx-app runs without administrator rights, so pktmon runs in `odx packet-capture`, started once per room with a UAC
/// prompt and driven over a named pipe only this user can open. One JSON line per request and per answer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PacketCaptureHelper : IPacketCapture, IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private PacketCaptureHelper(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false));
        _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>False once the helper went away (it exits when its pipe breaks).</summary>
    public bool IsConnected => _pipe.IsConnected;

    /// <summary>Starts the elevated helper (UAC prompt); null when the user declines or it does not come up.</summary>
    public static async Task<PacketCaptureHelper?> LaunchAsync(string odxPath)
    {
        string pipeName = $"odx-packets-{Guid.NewGuid():N}";
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        try
        {
            Process.Start(new ProcessStartInfo(odxPath, $"packet-capture --pipe {pipeName} --user {sid}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception)
        {
            return null;  // UAC declined (1223) or odx missing
        }
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await pipe.ConnectAsync(timeout.Token);
            return new PacketCaptureHelper(pipe);
        }
        catch (OperationCanceledException)
        {
            await pipe.DisposeAsync();
            return null;
        }
    }

    public Task StartAsync(IReadOnlyList<string> ports, string etlPath, CancellationToken stop) =>
        RequestAsync(new JsonObject
        {
            ["cmd"] = "start",
            ["ports"] = new JsonArray(ports.Select(p => (JsonNode)p).ToArray()),
            ["etl"] = etlPath,
        }, stop);

    public Task StopAsync(string etlPath, string pcapngPath, CancellationToken stop) =>
        RequestAsync(new JsonObject { ["cmd"] = "stop", ["etl"] = etlPath, ["pcapng"] = pcapngPath }, stop);

    /// <exception cref="InvalidOperationException">The helper reported an error or went away.</exception>
    private async Task RequestAsync(JsonObject request, CancellationToken stop)
    {
        await _lock.WaitAsync(stop);
        try
        {
            await _writer.WriteLineAsync(request.ToJsonString().AsMemory(), stop);
            string? line = await _reader.ReadLineAsync(stop) ?? throw new InvalidOperationException("the packet capture helper exited");
            var answer = JsonNode.Parse(line)!.AsObject();
            if (answer["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException((string?)answer["error"] ?? "packet capture failed");
        }
        catch (IOException e)
        {
            throw new InvalidOperationException($"the packet capture helper went away: {e.Message}", e);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _pipe.Dispose();
}

public static class PacketCaptureFiles
{
    public const string Pcapng = "packets.pcapng", LocalAddresses = "packets_local.json";

    /// <summary>This machine's addresses, so the assembler can tell sent packets from received ones.</summary>
    public static void WriteLocalAddresses(string dir)
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Select(a => (JsonNode)a.Address.ToString())
            .ToArray();
        File.WriteAllText(Path.Combine(dir, LocalAddresses), new JsonObject { ["addresses"] = new JsonArray(addresses) }.ToJsonString());
    }
}
