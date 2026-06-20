using System.Globalization;

namespace Anthill.Core.Common;

/// <summary>UTC clock + id helpers. Mirrors now_utc / timestamp_id from the Python build.</summary>
public static class AnthillTime
{
    public static DateTime NowUtc() => DateTime.UtcNow;

    public static string TimestampId() => NowUtc().ToString("yyyyMMddTHHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>ISO-8601 with a trailing offset, matching Python's datetime.isoformat() on aware UTC values.</summary>
    public static string ToIso(this DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff+00:00", CultureInfo.InvariantCulture);
    }

    public static string? ToIsoOrNull(this DateTime? value) => value?.ToIso();
}
