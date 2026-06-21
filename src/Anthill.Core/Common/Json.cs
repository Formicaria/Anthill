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

    /// <summary>
    /// Extracts the first JSON object from a model response. Tolerates markdown code
    /// fences and surrounding prose, mirroring <c>extract_json_object</c>.
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
        try
        {
            if (JsonNode.Parse(cleaned) is JsonObject obj) return obj;
        }
        catch (JsonException) { /* fall through to brace extraction */ }

        var match = System.Text.RegularExpressions.Regex.Match(cleaned, "\\{.*\\}",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!match.Success) throw new FormatException("No JSON object found.");
        return JsonNode.Parse(match.Value) as JsonObject
               ?? throw new FormatException("No JSON object found.");
    }

    /// <summary>Tolerantly parses a stored JSON object string into a dictionary; empty on null/invalid input.</summary>
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
}
