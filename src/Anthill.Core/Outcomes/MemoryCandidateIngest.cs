using System.Text.Json;
using Anthill.Core.Agents;

namespace Anthill.Core.Outcomes;

/// <summary>
/// v2.20.0: the consumer the archivist's memory candidates never had.
///
/// ArchivistAnt has emitted `memory_candidate` artifacts — and declared them in its execution
/// contract — since Stage D-6, but nothing ingested them: candidates were built, serialised, and
/// dropped. That is the same "tested code with no call site" failure mode as v2.14.12,
/// `SanitizeInto` (v2.14.14), the `/missions/json` payload (v2.18.2), and `HandoffGate.Evaluate`.
///
/// This parses candidates out of an execution result so the Queen can persist each one as a
/// durable, queryable `memory_candidate` event with its provenance. Deliberately narrow: it stores
/// records; it does not certify, promote, or feed planning. Certification stays with the V2.12
/// evaluation pipeline, and candidates carry `auto_promote = false` end to end.
///
/// Parsing fails soft: a malformed artifact yields zero candidates rather than an exception,
/// because archival must never be able to fail a mission that already finished.
/// </summary>
public static class MemoryCandidateIngest
{
    public const string ArtifactKind = "memory_candidate";
    public const string EventType = "memory_candidate";

    /// <summary>The fields a stored candidate event records, in a stable shape.</summary>
    public sealed record Candidate(
        string MemoryClass, string Summary, string SourceMission, string Outcome,
        string Confidence, bool AutoPromote);

    /// <summary>A string property, or "" when absent or not a string — candidates are operator
    /// data, so absent fields must degrade to empty rather than throw.</summary>
    private static string Str(System.Text.Json.JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>Extract every well-formed candidate from the result's memory_candidate artifacts.</summary>
    public static IReadOnlyList<Candidate> Extract(AntExecutionResult? result)
    {
        if (result is null) return Array.Empty<Candidate>();
        var found = new List<Candidate>();
        foreach (var artifact in result.Artifacts.Where(a => a.Kind == ArtifactKind))
        {
            try
            {
                using var doc = JsonDocument.Parse(artifact.Content);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var c in doc.RootElement.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) continue;
                    var memoryClass = Str(c, "memory_class");
                    var summary = Str(c, "summary");
                    if (memoryClass.Length == 0 || summary.Length == 0) continue; // not a candidate
                    found.Add(new Candidate(
                        memoryClass, summary,
                        Str(c, "source_mission"), Str(c, "outcome"), Str(c, "confidence"),
                        AutoPromote: c.TryGetProperty("auto_promote", out var ap) && ap.ValueKind == JsonValueKind.True));
                }
            }
            catch (JsonException)
            {
                // Malformed archival output is a defect worth seeing in the artifact itself, but it
                // must not throw inside mission finalisation. Zero candidates; the raw artifact is
                // still in the execution record.
            }
        }
        return found;
    }

    /// <summary>Event metadata for one stored candidate. `auto_promote` is recorded as read so a
    /// candidate claiming promotability is visible — nothing here acts on it.</summary>
    public static Dictionary<string, object?> EventMetadata(Candidate c) => new()
    {
        ["memory_class"] = c.MemoryClass,
        ["summary"] = c.Summary,
        ["source_mission"] = c.SourceMission,
        ["outcome"] = c.Outcome,
        ["confidence"] = c.Confidence,
        ["auto_promote"] = c.AutoPromote,
        ["ingested_by"] = "memory_candidate_ingest_v1",
    };
}
