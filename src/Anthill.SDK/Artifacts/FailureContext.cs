using System.Text.Json;
using System.Text.Json.Serialization;
using Anthill.SDK.Contracts;

namespace Anthill.SDK.Artifacts;

/// <summary>
/// The typed record of ONE task failure, produced at the failure boundary — structural-repair
/// release §2. Recovery used to reconstruct failure state from task/result prose; the Medic
/// re-inferred a class from keywords and diagnosed whichever failure happened to be newest. This
/// artifact is the fix's foundation: the failure's structured facts, bound to the failed task and
/// the artifacts/workspace it failed over, stored where the diagnosing specialist can load them.
///
/// The SEMANTIC SIGNATURE is the identity a recovery loop is bounded by. Task UUIDs regenerate on
/// every attempt; two attempts at the same defect must hash to the same signature or the loop
/// detector sees an endless parade of "new" failures. Everything ephemeral — ids, timestamps,
/// memory addresses, GUIDs inside error text — is excluded or normalized away.
/// </summary>
public sealed record FailureContext
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("mission_id")] public required string MissionId { get; init; }
    [JsonPropertyName("failed_task_id")] public required string FailedTaskId { get; init; }
    [JsonPropertyName("failed_role")] public required string FailedRole { get; init; }
    [JsonPropertyName("task_type")] public required string TaskType { get; init; }
    [JsonPropertyName("attempt")] public int Attempt { get; init; } = 1;

    /// <summary>The canonical class, in wire form (<see cref="FailureClassNames"/>). "unknown_failure"
    /// means exactly that — a consumer must not diagnose it into something else.</summary>
    [JsonPropertyName("failure_class")] public required string FailureClass { get; init; }
    [JsonPropertyName("failure_code")] public string? FailureCode { get; init; }
    [JsonPropertyName("retryable")] public bool Retryable { get; init; }

    /// <summary>Raw error text, for humans and for signature normalization. Never a control channel.</summary>
    [JsonPropertyName("raw_error")] public string RawError { get; init; } = "";
    /// <summary>The error with volatile tokens (guids, hex ids, numbers, paths' file parts kept but
    /// line numbers dropped) normalized out — the comparable form.</summary>
    [JsonPropertyName("normalized_error")] public string NormalizedError { get; init; } = "";

    [JsonPropertyName("provider")] public string? Provider { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    /// <summary>The failing deterministic check ids, when the failure came from checks.</summary>
    [JsonPropertyName("failing_checks")] public IReadOnlyList<string> FailingChecks { get; init; } = Array.Empty<string>();
    [JsonPropertyName("tool")] public string? Tool { get; init; }

    /// <summary>Artifacts the failed task consumed or produced — what the diagnosis is ABOUT.</summary>
    [JsonPropertyName("source_artifact_ids")] public IReadOnlyList<string> SourceArtifactIds { get; init; } = Array.Empty<string>();
    /// <summary>Kinds of those artifacts (patch_set, ui_map, …) — specialist selection reads TYPES,
    /// never words in prose.</summary>
    [JsonPropertyName("artifact_kinds")] public IReadOnlyList<string> ArtifactKinds { get; init; } = Array.Empty<string>();
    [JsonPropertyName("affected_paths")] public IReadOnlyList<string> AffectedPaths { get; init; } = Array.Empty<string>();

    [JsonPropertyName("patch_set_id")] public string? PatchSetId { get; init; }
    [JsonPropertyName("patch_set_hash")] public string? PatchSetHash { get; init; }
    [JsonPropertyName("base_revision")] public string? BaseRevision { get; init; }
    [JsonPropertyName("tree_hash")] public string? TreeHash { get; init; }
    [JsonPropertyName("workspace_id")] public string? WorkspaceId { get; init; }

    [JsonPropertyName("environment_fingerprint")] public string? EnvironmentFingerprint { get; init; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; init; } = Common.AnthillTime.NowUtc();

    /// <summary>
    /// The SEMANTIC identity of this failure: stable across task UUID regeneration, changed by a
    /// materially different artifact or error. SHA-256 over the stable properties, prefixed for
    /// recognisability. Computed, never stored-and-trusted.
    /// </summary>
    [JsonPropertyName("failure_signature")]
    public string FailureSignature => ComputeSignature(
        FailureClass, NormalizedError, FailingChecks, AffectedPaths,
        PatchSetHash ?? PatchSetId, Provider, Model, EnvironmentFingerprint);

    public string ToJson() => JsonSerializer.Serialize(this);

    public static FailureContext? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<FailureContext>(json!); }
        catch { return null; }
    }

    public static string ComputeSignature(
        string failureClass, string normalizedError, IEnumerable<string>? failingChecks,
        IEnumerable<string>? affectedPaths, string? artifactHash, string? provider, string? model,
        string? environmentFingerprint)
    {
        // v0.3.8.76: the ESCAPE, not a raw 0x1F byte. Identical string, identical signature —
        // but a raw control byte made this file BINARY to grep, ripgrep and git grep, so the
        // typed failure signature at the centre of bounded repair answered "no match" to every
        // search ever run over this repository. See SourceHygieneTests.
        var material = string.Join("\u001f",
            failureClass ?? "",
            normalizedError ?? "",
            string.Join(",", (failingChecks ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal)),
            string.Join(",", (affectedPaths ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal)),
            artifactHash ?? "", provider ?? "", model ?? "", environmentFingerprint ?? "");
        return "fsig:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant()[..32];
    }

    /// <summary>
    /// Strip the volatile parts of an error so two occurrences of the same defect compare equal:
    /// GUIDs, long hex runs, decimal numbers, ISO timestamps. Paths survive (they are identity);
    /// their line/column suffixes do not.
    /// </summary>
    public static string NormalizeError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw!.Length > 2000 ? raw[..2000] : raw;
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "<guid>");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b[0-9a-fA-F]{12,64}\b", "<hex>");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}[0-9:.Z+-]*", "<time>");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[(:]\s*\d+\s*[,:]?\s*\d*\)?", "<loc>");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b\d+(\.\d+)?\b", "<n>");
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
    }
}
