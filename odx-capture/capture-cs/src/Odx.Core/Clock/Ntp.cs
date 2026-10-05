using System.Buffers.Binary;
using System.Net.Sockets;

namespace Odx.Core;

/// <summary>
/// One SNTP exchange (RFC 4330). <c>UtcUnixMs</c> is the server's time at local QPC <c>TQpcMs</c> (the midpoint of the
/// exchange, assuming a symmetric path), so it maps QPC to UTC without going through the Windows clock.
/// <c>SystemOffsetMs</c> is how far the Windows clock was behind the server (negative: ahead).
/// </summary>
public sealed record NtpSample(double TQpcMs, double UtcUnixMs, double RttMs, double SystemOffsetMs, string Server);

public static class SntpClient
{
    private const int PacketSize = 48;
    private const double NtpToUnixSeconds = 2_208_988_800;

    /// <param name="server">"host" (port 123) or "host:port", e.g. the orchestrator's time service.</param>
    /// <exception cref="SocketException">Network error or no answer within the timeout.</exception>
    /// <exception cref="InvalidDataException">The answer is not a usable server reply.</exception>
    public static NtpSample Query(string server, TimeSpan timeout)
    {
        using var socket = new Socket(SocketType.Dgram, ProtocolType.Udp) { ReceiveTimeout = (int)timeout.TotalMilliseconds };
        int colon = server.LastIndexOf(':');
        if (colon > 0 && int.TryParse(server[(colon + 1)..], out int port)) socket.Connect(server[..colon], port);
        else socket.Connect(server, 123);
        var request = new byte[PacketSize];
        request[0] = 0x23;  // LI 0, version 4, mode 3 (client)
        double sentUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ulong transmit = ToNtp(sentUnixMs);
        BinaryPrimitives.WriteUInt64BigEndian(request.AsSpan(40), transmit);  // echoed back as the originate timestamp
        double q1 = QpcClock.NowMs();
        socket.Send(request);
        var response = new byte[PacketSize];
        int n = socket.Receive(response);
        double q4 = QpcClock.NowMs();
        double receivedUnixMs = sentUnixMs + (q4 - q1);
        return Parse(response.AsSpan(0, n), transmit, q1, q4, sentUnixMs, receivedUnixMs, server);
    }

    /// <param name="transmit">The transmit timestamp of our request; a valid reply echoes it as its originate timestamp.</param>
    /// <param name="q1">QPC ms when the request was sent.</param>
    /// <param name="q4">QPC ms when the reply arrived.</param>
    /// <param name="systemSentUnixMs">Windows clock when the request was sent.</param>
    /// <param name="systemReceivedUnixMs">Windows clock when the reply arrived.</param>
    public static NtpSample Parse(ReadOnlySpan<byte> reply, ulong transmit, double q1, double q4, double systemSentUnixMs,
        double systemReceivedUnixMs, string server)
    {
        if (reply.Length < PacketSize) throw new InvalidDataException($"short NTP reply ({reply.Length} bytes)");
        int leap = reply[0] >> 6, mode = reply[0] & 7, stratum = reply[1];
        if (mode != 4) throw new InvalidDataException($"NTP reply mode {mode}, expected 4 (server)");
        if (leap == 3 || stratum is 0 or > 15) throw new InvalidDataException($"NTP server unsynchronized (leap {leap}, stratum {stratum})");
        if (BinaryPrimitives.ReadUInt64BigEndian(reply[24..]) != transmit)
            throw new InvalidDataException("NTP reply does not answer this request");
        double t2 = ToUnixMs(BinaryPrimitives.ReadUInt64BigEndian(reply[32..]));
        double t3 = ToUnixMs(BinaryPrimitives.ReadUInt64BigEndian(reply[40..]));
        double rtt = Math.Max(0, (q4 - q1) - (t3 - t2));
        double serverMid = (t2 + t3) / 2;
        double systemMid = (systemSentUnixMs + systemReceivedUnixMs) / 2;
        return new NtpSample((q1 + q4) / 2, serverMid, rtt, serverMid - systemMid, server);
    }

    public static ulong ToNtp(double unixMs)
    {
        double seconds = unixMs / 1000 + NtpToUnixSeconds;
        ulong whole = (ulong)Math.Floor(seconds);
        ulong fraction = (ulong)((seconds - whole) * 4294967296.0);
        return (whole << 32) | (fraction & 0xFFFFFFFF);
    }

    /// <summary>NTP era 0 covers 1900–2036; timestamps with the top bit clear are in era 1 (from 2036, RFC 4330 §3).</summary>
    public static double ToUnixMs(ulong ntp)
    {
        double seconds = (ntp >> 32) + (ntp & 0xFFFFFFFF) / 4294967296.0;
        if ((ntp >> 63) == 0) seconds += 4294967296.0;
        return (seconds - NtpToUnixSeconds) * 1000;
    }
}

/// <summary>Writes ntp_samples.csv: <c>t_qpc_ms,utc_unix_ms,rtt_ms,system_offset_ms,server</c>; pinned by tests/fixtures/ntp_samples.csv.</summary>
public sealed class NtpSampleWriter(TextWriter writer) : IDisposable
{
    public const string Header = "t_qpc_ms,utc_unix_ms,rtt_ms,system_offset_ms,server";
    private bool _headerWritten;

    public void Write(NtpSample s)
    {
        if (!_headerWritten)
        {
            writer.Write(Header + "\n");
            _headerWritten = true;
        }
        writer.Write(Csv.Line([InputLogWriter.Num(s.TQpcMs), InputLogWriter.Num(s.UtcUnixMs), InputLogWriter.Num(s.RttMs),
            InputLogWriter.Num(s.SystemOffsetMs), s.Server]));
    }

    public void Flush() => writer.Flush();
    public void Dispose() => writer.Dispose();
}

public static class NtpSampleReader
{
    public static List<NtpSample> Read(string path) => Csv.ReadFile(path).Rows.Select(r => new NtpSample(
        Frames.ParseDouble(r["t_qpc_ms"]), Frames.ParseDouble(r["utc_unix_ms"]), Frames.ParseDouble(r["rtt_ms"]),
        Frames.ParseDouble(r["system_offset_ms"]), r["server"])).ToList();
}

/// <summary>QPC → UTC mapping from (QPC ms, UTC unix ms) points.</summary>
public sealed record ClockMap(double QpcRefMs, double UtcAtRefMs, double Slope)
{
    /// <summary>Points spanning less than this give a pure offset; QPC rate error is invisible over short spans.</summary>
    public const double MinFitSpanMs = 60_000;
    /// <summary>Slopes further than this from 1 (1000 ppm) are measurement noise, not a real QPC rate error.</summary>
    public const double MaxSlopeError = 1e-3;

    public double ToUnixMs(double qpcMs) => UtcAtRefMs + (qpcMs - QpcRefMs) * Slope;

    /// <summary>Least-squares line over a long enough span, otherwise the median offset (robust to one bad sample).</summary>
    public static ClockMap Fit(IReadOnlyList<(double Qpc, double Utc)> points)
    {
        if (points.Count == 0) throw new ArgumentException("no clock points", nameof(points));
        double qMean = points.Average(p => p.Qpc), uMean = points.Average(p => p.Utc);
        double span = points.Max(p => p.Qpc) - points.Min(p => p.Qpc);
        if (points.Count >= 3 && span >= MinFitSpanMs)
        {
            double slope = points.Sum(p => (p.Qpc - qMean) * (p.Utc - uMean)) / points.Sum(p => (p.Qpc - qMean) * (p.Qpc - qMean));
            if (Math.Abs(slope - 1) <= MaxSlopeError) return new ClockMap(qMean, uMean, slope);
        }
        var offsets = points.Select(p => p.Utc - p.Qpc).Order().ToList();
        double median = offsets[offsets.Count / 2];
        return new ClockMap(0, median, 1);
    }
}
