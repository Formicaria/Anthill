using System.Text.Json;

namespace Anthill.SDK.Artifacts;

/// <summary>One retrieved source, as a `source_set` payload carries it.</summary>
public sealed record RetrievedSource(string Url, string Title);

/// <summary>
/// READING WHAT THE WEB ANT WROTE. v0.3.8.99.
///
/// WHY THIS TYPE EXISTS, and it is not tidiness. The `source_set` payload is a CONTRACT between one
/// producer and, as of this release, two readers — the builder, which must show a model the urls it
/// may cite, and `CitationIntegrity`, which resolves what it cited. The first draft had each reader
/// parse the payload for itself with `TryGetProperty("url")`, and every one of them was wrong in the
/// same invisible way: `Json.Dumps` sets no naming policy, so `new { src.Title, src.Url }` serialises
/// as `"Title"` and `"Url"`, and `TryGetProperty` is case-SENSITIVE. Both readers found nothing,
/// silently, and reported an answer that cited nothing.
///
/// WHAT MADE IT INVISIBLE is the part worth keeping. The unit tests passed — because their fixtures
/// wrote the payload the way the READERS expected rather than the way the PRODUCER writes it. The
/// test agreed with the code under test and both disagreed with the system; a green suite proved
/// only that two things written together matched each other. One parser, shared by the producer's
/// readers and asserted against the producer's ACTUAL spelling, is what removes the class rather
/// than the instance.
///
/// CASE-INSENSITIVE ON PURPOSE, not as a workaround for the bug above. The payload is written by a
/// component that may reasonably change its serialisation, and a reader that breaks silently when it
/// does is a reader that will break silently again. Matching the FIELD rather than its spelling is
/// the same rule `ResearchBrief` applies to headings: a format check, not a formatting check.
/// </summary>
public static class SourceSetPayload
{
    /// <summary>
    /// Every source a `source_set` payload holds. Returns empty for anything unparseable — a
    /// malformed payload is already reported as a schema non-conformance where the artifact is read,
    /// and throwing here would let a diagnostic fail the mission it describes.
    /// </summary>
    public static IReadOnlyList<RetrievedSource> Read(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return Array.Empty<RetrievedSource>();

        try
        {
            using var document = JsonDocument.Parse(payload!);
            if (!TryField(document.RootElement, "sources", out var sources)
                || sources.ValueKind != JsonValueKind.Array)
                return Array.Empty<RetrievedSource>();

            var found = new List<RetrievedSource>();
            foreach (var source in sources.EnumerateArray())
            {
                var url = TryField(source, "url", out var u) ? u.GetString() ?? "" : "";
                if (url.Length == 0) continue;
                var title = TryField(source, "title", out var t) ? t.GetString() ?? "" : "";
                if (!found.Any(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase)))
                    found.Add(new RetrievedSource(url, title));
            }
            return found;
        }
        catch (JsonException)
        {
            return Array.Empty<RetrievedSource>();
        }
    }

    /// <summary>Every url across a mission's source sets, for resolving what an answer cited.</summary>
    public static IReadOnlySet<string> UrlsFrom(IEnumerable<string?> payloads)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var payload in payloads)
            foreach (var source in Read(payload))
                urls.Add(source.Url);
        return urls;
    }

    /// <summary>
    /// A property by name, whatever case the producer used. `JsonElement.TryGetProperty` is
    /// case-sensitive and has no option to be otherwise, so this enumerates — the payloads are a
    /// handful of fields and the alternative is the silent miss this type was written to end.
    /// </summary>
    private static bool TryField(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) return false;

        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        return false;
    }
}
