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

    /// <summary>Parses an ISO-8601 timestamp back to UTC, or null if absent/unparseable.</summary>
    public static DateTime? ParseIsoOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
    }

    /// <summary>Like <see cref="ParseIsoOrNull"/> but falls back to the current UTC time.</summary>
    public static DateTime ParseIsoOrNow(string? value) => ParseIsoOrNull(value) ?? NowUtc();
}
