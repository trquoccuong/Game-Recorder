using System.Buffers.Binary;
using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class NtpTests
{
    private const double Utc2026 = 1_791_108_000_000;  // 2026-10-04T10:00:00Z

    /// <summary>A server reply: received at t2, sent at t3 (unix ms), answering the request with this transmit stamp.</summary>
    private static byte[] Reply(ulong originate, double t2, double t3, int mode = 4, int stratum = 2, int leap = 0)
    {
        var b = new byte[48];
        b[0] = (byte)((leap << 6) | (4 << 3) | mode);
        b[1] = (byte)stratum;
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(24), originate);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(32), SntpClient.ToNtp(t2));
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(40), SntpClient.ToNtp(t3));
        return b;
    }

    [Fact]
    public void Maps_the_exchange_midpoint_to_server_time_and_measures_rtt_and_windows_offset()
    {
        // Windows clock 44 s behind; 10 ms each way; server holds the request 2 ms.
        ulong transmit = SntpClient.ToNtp(Utc2026);
        var reply = Reply(transmit, Utc2026 + 44_010, Utc2026 + 44_012);
        var s = SntpClient.Parse(reply, transmit, q1: 1000, q4: 1022, Utc2026, Utc2026 + 22, "ntp.test");
        Assert.Equal(1011, s.TQpcMs);
        Assert.Equal(Utc2026 + 44_011, s.UtcUnixMs, 0.01);
        Assert.Equal(20, s.RttMs, 0.01);
        Assert.Equal(44_000, s.SystemOffsetMs, 0.01);
    }

    [Theory]
    [InlineData(3, 2, 0)]   // client mode, not a server reply
    [InlineData(4, 0, 0)]   // stratum 0: kiss-o'-death
    [InlineData(4, 2, 3)]   // leap 3: server unsynchronized
    public void Rejects_unusable_replies(int mode, int stratum, int leap)
    {
        ulong transmit = SntpClient.ToNtp(Utc2026);
        var reply = Reply(transmit, Utc2026, Utc2026, mode, stratum, leap);
        Assert.Throws<InvalidDataException>(() => SntpClient.Parse(reply, transmit, 0, 1, Utc2026, Utc2026, "x"));
    }

    [Fact]
    public void Rejects_a_reply_to_another_request()
    {
        var reply = Reply(SntpClient.ToNtp(Utc2026 - 5000), Utc2026, Utc2026);
        Assert.Throws<InvalidDataException>(() =>
            SntpClient.Parse(reply, SntpClient.ToNtp(Utc2026), 0, 1, Utc2026, Utc2026, "x"));
    }

    [Theory]
    [InlineData(Utc2026)]
    [InlineData(2_190_000_000_000)]  // 2039, NTP era 1
    public void Ntp_timestamps_round_trip(double unixMs) =>
        Assert.Equal(unixMs, SntpClient.ToUnixMs(SntpClient.ToNtp(unixMs)), 0.001);

    [Fact]
    public void Writer_output_matches_shared_fixture_and_reads_back()
    {
        var samples = new[]
        {
            new NtpSample(5000.25, 1791108000000.5, 18.75, -12.5, "time.windows.com"),
            new NtpSample(20000.5, 1791108015000.25, 22, -12.75, "time.windows.com"),
        };
        var sw = new StringWriter();
        using (var w = new NtpSampleWriter(sw))
            foreach (var s in samples) w.Write(s);
        string fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "ntp_samples.csv");
        Assert.Equal(File.ReadAllText(fixture), sw.ToString());
        Assert.Equal(samples, NtpSampleReader.Read(fixture));
    }

    [Fact]
    public void Short_spans_give_the_median_offset()
    {
        var map = ClockMap.Fit([(1000, Utc2026 + 1000), (2000, Utc2026 + 2000 + 300), (3000, Utc2026 + 3000)]);
        Assert.Equal(Utc2026 + 10_000, map.ToUnixMs(10_000), 0.001);  // the 300 ms outlier does not move it
    }

    [Fact]
    public void Long_spans_fit_the_qpc_rate_error()
    {
        const double ppm = 50e-6;  // QPC runs 50 ppm slow: 180 ms per hour
        var points = Enumerable.Range(0, 60).Select(i => (Qpc: i * 60_000.0, Utc: Utc2026 + i * 60_000.0 * (1 + ppm))).ToList();
        var map = ClockMap.Fit(points);
        Assert.Equal(1 + ppm, map.Slope, 1e-9);
        Assert.Equal(Utc2026 + 3_600_000 * (1 + ppm), map.ToUnixMs(3_600_000), 0.01);
    }
}

public class NtpResponderTests
{
    [Fact]
    public async Task Answers_sntp_queries_from_this_clock()
    {
        using var udp = NtpResponder.Bind(0);
        int port = ((System.Net.IPEndPoint)udp.Client.LocalEndPoint!).Port;
        using var stop = new CancellationTokenSource();
        var server = NtpResponder.RunAsync(udp, stop.Token);

        var samples = Enumerable.Range(0, 5).Select(_ => SntpClient.Query($"127.0.0.1:{port}", TimeSpan.FromSeconds(2))).ToList();

        Assert.InRange(samples.Min(s => s.RttMs), 0, 50);  // the first exchange can be slow under parallel test load
        Assert.All(samples, s => Assert.InRange(Math.Abs(s.SystemOffsetMs), 0, s.RttMs / 2 + 2));  // same clock: RTT/2 + whole-ms send stamp
        Assert.All(samples, s => Assert.Equal($"127.0.0.1:{port}", s.Server));
        stop.Cancel();
        await server;
    }

    [Fact]
    public void Ignores_packets_that_are_not_client_requests() =>
        Assert.Null(NtpResponder.Reply(new byte[48] { 0x24, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 0, 0));
}
