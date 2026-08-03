using System.Text.Json;
using System.Text.Json.Nodes;

namespace Anthill.Core.Common;

/// <summary>
/// JSON helpers shared across the colony. Wraps System.Text.Json with the lenient,
/// string-fallback behaviour the Python build got for free from <c>json.dumps(default=str)</c>
/// and its tolerant object extraction.
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>Serialises any value, falling back to ToString() for unsupported nodes — like json.dumps(default=str).</summary>
    public static string SafeDumps(object? data)
    {
        try
        {
            return JsonSerializer.Serialize(data, Options);
        }
        catch
        {
            return JsonSerializer.Serialize(data?.ToString() ?? "");
        }
    }

    public static string Dumps(object? data, bool indented = false) =>
        JsonSerializer.Serialize(data, indented ? IndentedOptions : Options);

    // Lenient parse options — small local models routinely emit trailing commas and // comments.
    private static readonly JsonDocumentOptions LenientDoc = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Extracts the first JSON object from a model response. Tolerates markdown code fences,
    /// surrounding prose, trailing commas, comments, and — the big one for small local models —
    /// raw unescaped control characters inside string values (e.g. a literal newline in a patch's
    /// new_content, which strict JSON rejects with "'0x0A' is invalid within a JSON string").
    /// Each parse is retried on a control-char-repaired copy before giving up.
    /// </summary>
    public static JsonObject ExtractJsonObject(string text)
    {
        var cleaned = (text ?? "").Trim();
        if (cleaned.StartsWith("```"))
        {
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "^```(?:json)?", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "```$", "").Trim();
        }

        if (TryParseJsonObject(cleaned, out var obj)) return obj!;

        // Whole-string parse failed — narrow to the outermost {...} and try again (both raw and repaired).
        var match = System.Text.RegularExpressions.Regex.Match(cleaned, "\\{.*\\}",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (match.Success && TryParseJsonObject(match.Value, out obj)) return obj!;

        throw new FormatException("No parseable JSON object found in the model response.");
    }

    /// <summary>Attempts a lenient parse, then a control-char-repaired parse. Returns false if neither yields an object.</summary>
    private static bool TryParseJsonObject(string candidate, out JsonObject? result)
    {
        foreach (var attempt in new[] { candidate, RepairJsonControlChars(candidate) })
        {
            try
            {
                if (JsonNode.Parse(attempt, documentOptions: LenientDoc) is JsonObject obj) { result = obj; return true; }
            }
            catch (JsonException) { /* try the next form */ }
        }
        result = null;
        return false;
    }

    /// <summary>
    /// Escapes raw control characters (newline, tab, etc.) that appear <em>inside JSON string
    /// literals</em> — the single most common reason small models' JSON fails to parse. Characters
    /// outside strings, and already-escaped sequences, are left untouched.
    /// </summary>
    internal static string RepairJsonControlChars(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 16);
        bool inString = false, escaped = false;
        foreach (var c in s)
        {
            if (!inString)
            {
                if (c == '"') inString = true;
                sb.Append(c);
                continue;
            }
            if (escaped) { sb.Append(c); escaped = false; continue; }
            if (c == '\\') { sb.Append(c); escaped = true; continue; }
            if (c == '"') { sb.Append(c); inString = false; continue; }
            if (c < 0x20)
            {
                sb.Append(c switch
                {
                    '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\b' => "\\b", '\f' => "\\f",
                    _ => $"\\u{(int)c:x4}",
                });
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Tolerantly parses a stored JSON object string into a dictionary; empty on null/invalid input.</summary>
    /// <summary>
    /// A JSON string array as a list, tolerating null, empty, malformed input, and non-string
    /// elements. Persisted collections must never be able to throw during a load — a corrupt row
    /// should degrade to an empty list, not prevent the process from starting.
    /// </summary>
    public static List<string> TryParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json, LenientDoc);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new List<string>();
            return doc.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? "")
                .ToList();
        }
        catch (JsonException) { return new List<string>(); }
    }

    /// <summary>
    /// A stored JSON array as a typed list, tolerating null, empty and malformed input.
    ///
    /// Same rule as <see cref="TryParseStringList"/> and for the same reason: a corrupt row must
    /// degrade to an empty list rather than take down a load path. Deserialization failure here is
    /// data loss for one record, not a reason to fail the read that found it.
    /// </summary>
    public static List<T> TryParseList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<T>();
        try { return JsonSerializer.Deserialize<List<T>>(json!) ?? new List<T>(); }
        catch (JsonException) { return new List<T>(); }
        catch (NotSupportedException) { return new List<T>(); }
    }

    /// <summary>A stored JSON object as a typed record; null on null/empty/malformed input.</summary>
    public static T? TryParseTyped<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json!); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public static Dictionary<string, object?> TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ToDictionary(doc.RootElement);
        }
        catch { return new(); }
    }

    public static Dictionary<string, object?> ToDictionary(JsonElement element)
    {
        var result = new Dictionary<string, object?>();
        if (element.ValueKind != JsonValueKind.Object) return result;
        foreach (var prop in element.EnumerateObject())
            result[prop.Name] = ElementToObject(prop.Value);
        return result;
    }

    private static object? ElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => element.EnumerateArray().Select(ElementToObject).ToList(),
        JsonValueKind.Object => ToDictionary(element),
        _ => element.GetRawText(),
    };

    /// <summary>
    /// v3.7.0: read a JSON string array back, never throwing.
    ///
    /// The counterpart to <see cref="SafeDumps"/>, and it exists for the same reason: a malformed
    /// blob in one column must not take out the row that contains it. An empty list is the honest
    /// degradation — the caller loses one field rather than the whole record.
    /// </summary>
    public static IReadOnlyList<string> SafeLoadList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json!) ?? new List<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
