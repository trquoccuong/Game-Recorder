namespace Odx.Core;

public static class UtcText
{
    /// <summary>ISO 8601 UTC to the millisecond, the form of every *_utc field.</summary>
    public static string Iso(double unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(unixMs)).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>To the microsecond, as the client's multiplayer delivery writes event and first-frame times.</summary>
    public static string IsoUs(double unixMs) =>
        DateTime.UnixEpoch.AddTicks((long)Math.Round(unixMs * 10_000))
            .ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Truncated to the second, as the client's multiplayer delivery writes created_at_utc / ended_at_utc.</summary>
    public static string IsoSeconds(double unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(unixMs)).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
}
