using Anthill.SDK.Common;

namespace Anthill.Core.Memory;

/// <summary>
/// READING ONE VALUE OUT OF A STORED ROW, ONCE. v0.3.8.113, PLAN.md §2b `.113`.
///
/// WHY THIS EXISTS BEFORE THE TYPED ROWS DO. The migration away from
/// <c>Dictionary&lt;string, object?&gt;</c> is fifty public methods and a hundred consumer files, so
/// it happens a slice at a time — and every slice has to turn a row into an object. Without a shared
/// reader, each slice writes its own `Str`/`Int`/`Utc` trio, and this repository's most-named defect
/// class is two implementations of one rule.
///
/// It is not hypothetical: v0.3.8.110 already produced the second copy. `MissionRehydration` carries
/// private `Str`, `Nullable`, `Int`, `Double`, `Utc` and `NullableUtc` helpers written for exactly
/// this job, and the approvals slice needed the same six. They are here now, and that file reads
/// them rather than keeping its own.
///
/// EVERY READER SURVIVES ANY UNDERLYING TYPE, deliberately. SQLite hands back <c>object?</c> —
/// strings, longs, doubles and <c>DBNull</c> — and which one arrives depends on how the value was
/// written rather than on the column's declared affinity. A reader that assumed the declared type
/// would work until somebody stored an integer as text, then return a default that is
/// indistinguishable from an absent value.
///
/// AND ABSENT IS NOT EMPTY. <see cref="TextOrNull"/> answers null for a missing or blank column and
/// <see cref="Text"/> answers a caller-supplied fallback, because "this row has no decision note"
/// and "this row's decision note is the empty string" are different facts — the second is what a
/// bare <c>ToString()</c> turns the first into.
/// </summary>
public static class RowValues
{
    /// <summary>The column as text, or <paramref name="fallback"/> when it is absent or null.</summary>
    public static string Text(IReadOnlyDictionary<string, object?> row, string key, string fallback = "") =>
        row.TryGetValue(key, out var value) && value is not null and not DBNull
            ? value.ToString() ?? fallback
            : fallback;

    /// <summary>The column as text, or null when it is absent, null or blank.</summary>
    public static string? TextOrNull(IReadOnlyDictionary<string, object?> row, string key) =>
        Text(row, key) is { Length: > 0 } text ? text : null;

    public static int Int(IReadOnlyDictionary<string, object?> row, string key, int fallback = 0) =>
        row.TryGetValue(key, out var value)
            ? value switch
            {
                long l => (int)l,
                int i => i,
                double d => (int)d,
                string s when int.TryParse(s, out var parsed) => parsed,
                _ => fallback,
            }
            : fallback;

    public static double? Double(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value)
            ? value switch
            {
                double d => d,
                long l => l,
                int i => i,
                string s when double.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null,
            }
            : null;

    public static bool Bool(IReadOnlyDictionary<string, object?> row, string key, bool fallback = false) =>
        row.TryGetValue(key, out var value)
            ? value switch
            {
                bool b => b,
                long l => l != 0,
                int i => i != 0,
                string s when bool.TryParse(s, out var parsed) => parsed,
                string s when int.TryParse(s, out var number) => number != 0,
                _ => fallback,
            }
            : fallback;

    /// <summary>
    /// The column as a UTC instant, or null when it is absent or unparseable.
    ///
    /// `AssumeUniversal | AdjustToUniversal` rather than the default, because every timestamp this
    /// store writes goes through <c>ToIso()</c> and is UTC — and a parser that assumed local time
    /// would shift them by the operator's offset, silently, in whichever direction they happen to
    /// live. An unparseable value is null rather than <c>DateTime.MinValue</c>: year one is a real
    /// date and would sort, compare and render as one.
    /// </summary>
    public static DateTime? Timestamp(IReadOnlyDictionary<string, object?> row, string key)
    {
        var raw = Text(row, key);
        if (raw.Length == 0) return null;

        return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal
          | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>The column as a UTC instant, or now when it is absent — for a required field whose
    /// absence would otherwise become year one.</summary>
    public static DateTime TimestampOrNow(IReadOnlyDictionary<string, object?> row, string key) =>
        Timestamp(row, key) ?? AnthillTime.NowUtc();
}
