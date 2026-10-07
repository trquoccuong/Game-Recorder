namespace Odx.Core;

public static class UtcText
{
    /// <summary>ISO 8601 UTC to the millisecond, the form of every *_utc field.</summary>
    public static string Iso(double unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(unixMs)).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
}
