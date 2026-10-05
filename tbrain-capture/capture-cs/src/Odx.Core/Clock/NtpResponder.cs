using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Odx.Core;

/// <summary>
/// A minimal SNTP server (RFC 4330 server mode) answering from this machine's clock. The orchestrator runs it next to the
/// game server so every player measures its clock offset and RTT against one reference (spec v3 §4), on a port of its
/// own (no admin rights, no Windows Time service needed).
/// </summary>
public static class NtpResponder
{
    private static readonly long UnixEpochTicks = DateTime.UnixEpoch.Ticks;

    /// <summary>Answers requests until <paramref name="stop"/> is cancelled.</summary>
    public static async Task RunAsync(UdpClient udp, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            UdpReceiveResult request;
            try
            {
                request = await udp.ReceiveAsync(stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;  // e.g. ICMP port unreachable from an earlier reply
            }
            double received = NowUnixMs();
            if (Reply(request.Buffer, received, NowUnixMs()) is { } reply)
                await udp.SendAsync(reply, request.RemoteEndPoint, stop);
        }
    }

    /// <summary>The reply to a client request received at <paramref name="receivedUnixMs"/>; null if it is not one.</summary>
    public static byte[]? Reply(ReadOnlySpan<byte> request, double receivedUnixMs, double sentUnixMs)
    {
        if (request.Length < 48 || (request[0] & 7) != 3) return null;
        int version = (request[0] >> 3) & 7;
        var reply = new byte[48];
        reply[0] = (byte)((version << 3) | 4);  // LI 0, the client's version, mode 4 (server)
        reply[1] = 2;                           // stratum: synchronized from another source (this OS clock)
        reply[2] = request[2];                  // poll interval, echoed
        reply[3] = unchecked((byte)-20);        // precision ~1 µs
        "LOCL"u8.CopyTo(reply.AsSpan(12));      // reference id
        BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(16), SntpClient.ToNtp(receivedUnixMs));  // reference time
        request[40..48].CopyTo(reply.AsSpan(24));                                                     // originate = client transmit
        BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(32), SntpClient.ToNtp(receivedUnixMs));
        BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(40), SntpClient.ToNtp(sentUnixMs));
        return reply;
    }

    /// <summary>UTC with sub-millisecond resolution (DateTime.UtcNow is precise to ~1 µs on Windows 10+ and Linux).</summary>
    public static double NowUnixMs() => (DateTime.UtcNow.Ticks - UnixEpochTicks) / (double)TimeSpan.TicksPerMillisecond;

    /// <summary>Dual-stack, so agents reaching the orchestrator as "localhost" (::1) or by IPv4 address both get answers.</summary>
    public static UdpClient Bind(int port)
    {
        var udp = new UdpClient(AddressFamily.InterNetworkV6);
        udp.Client.DualMode = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        return udp;
    }
}
