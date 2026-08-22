using System.Text.Json;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.SDK.Artifacts;

namespace Anthill.Core.Readiness;

/// <summary>
/// One field a live qualification run must record, and whether it was ACTUALLY measured. v0.3.8.89.
///
/// The pair is the whole design. `QUALIFICATION.md` lists seven fields a run must capture, and a
/// report that printed `0` for a field nothing records would satisfy the table while telling the
/// operator something false — which is the shape this repository has now found in an event
/// vocabulary, a capability register and a readiness gate. `V3Readiness` already states the rule for
/// its own thresholds: *unmeasured is NOT ready.* This carries the same rule into the live record.
/// </summary>
/// <param name="Field">The canonical id, from <see cref="QualificationFields"/>.</param>
/// <param name="Value">What was measured. Null when <paramref name="Measured"/> is false.</param>
/// <param name="Measured">False means nothing in the runtime produces this. Never means "zero".</param>
/// <param name="Note">Why it is unmeasured, or what the value is derived from. Never empty.</param>
public sealed record RecordedField(string Field, string? Value, bool Measured, string Note);

/// <summary>What one role did during the run, assembled from what the colony already persisted.</summary>
public sealed record RoleTelemetry
{
    public required string Role { get; init; }

    /// <summary>
    /// HOW this role came to run — read from the admission event, not inferred from the plan.
    /// `QUALIFICATION.md` asks for this specifically ("proves production triggers, not that the
    /// harness called it"), and a value derived from the task graph would answer the adjacent
    /// question of what the plan intended.
    /// </summary>
    public required string Trigger { get; init; }

    public int ModelCalls { get; init; }

    /// <summary>Null when the provider reported nothing. Absent usage stays absent — a provider that
    /// reports nothing is unknown, not zero, which is the rule `ModelRouter` already applies.</summary>
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }

    public string? Provider { get; init; }
    public string? Model { get; init; }

    /// <summary>Summed from the model-call events, so it is model time rather than task time.</summary>
    public long? ModelDurationMs { get; init; }

    /// <summary>The typed class, never prose — so a live failure joins the same taxonomy.</summary>
    public string? FailureClass { get; init; }

    public IReadOnlyList<string> ProducedArtifactIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ConsumedArtifactIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The canonical field ids, paired with the row each one answers in `QUALIFICATION.md`.
///
/// PAIRED DELIBERATELY. The document's table is the specification for a live run, and a recorder
/// whose fields drifted from it would produce a report that looks complete and answers a different
/// question. <c>LiveQualificationRecordTests</c> reads that table and requires a one-to-one match, so
/// adding a row to the document without a producer fails, and so does producing a field the document
/// never asked for.
/// </summary>
public static class QualificationFields
{
    public const string ProviderAndModel = "provider_and_model";
    public const string Tokens = "tokens";
    public const string Cost = "cost";
    public const string WallTime = "wall_time";
    public const string FailureClass = "failure_class";
    public const string Trigger = "trigger_per_role";
    public const string Artifacts = "artifact_ids";

    /// <summary>Field id → the exact leading text of its row in the document's table.</summary>
    public static IReadOnlyDictionary<string, string> DocumentRows { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProviderAndModel] = "provider and model, with version",
            [Tokens] = "prompt and completion tokens",
            [Cost] = "cost",
            [WallTime] = "wall time, per task and per mission",
            [FailureClass] = "failure class per failure",
            [Trigger] = "which trigger reached each role",
            [Artifacts] = "artifact ids produced and consumed",
        };
}

/// <summary>
/// A live qualification run's record, assembled from the store rather than from notes.
///
/// `QUALIFICATION.md` §3 says it in those words: "A live run should be reconstructable from the store
/// afterwards rather than from notes." This is that reconstruction, and building it BEFORE any live
/// run is deliberate — every field can be proved present and correct against a scripted mission with
/// no provider attached, so the live run is an operator pressing go rather than a live run and an
/// argument about whether its telemetry is complete.
///
/// It computes nothing it cannot source. See <see cref="Unmeasured"/>.
/// </summary>
public sealed record LiveQualificationRecord
{
    public required string MissionId { get; init; }
    public IReadOnlyList<RecordedField> Fields { get; init; } = Array.Empty<RecordedField>();
    public IReadOnlyList<RoleTelemetry> Roles { get; init; } = Array.Empty<RoleTelemetry>();

    /// <summary>Whether `MissionReconstruction` replays this run, and every way it does not.</summary>
    public bool Reconstructs { get; init; }
    public IReadOnlyList<string> ReconstructionGaps { get; init; } = Array.Empty<string>();

    /// <summary>Mission wall time. Null when the mission rows carry no usable timestamps.</summary>
    public long? MissionDurationMs { get; init; }

    /// <summary>
    /// The fields nothing in the runtime produces. NOT an error and NOT hidden: an operator reading
    /// the record has to be able to see which parts of the exit gate were answered by measurement and
    /// which were not, because those are different claims.
    /// </summary>
    public IReadOnlyList<RecordedField> Unmeasured =>
        Fields.Where(f => !f.Measured).ToList();

    /// <summary>
    /// Assemble the record for one mission.
    ///
    /// Pure with respect to the colony: it reads and computes, and writes nothing. A recorder that
    /// logged would appear in the record it produces.
    /// </summary>
    public static LiveQualificationRecord For(
        SqliteMemory memory, IArtifactStore artifacts, IEvidenceStore evidence, string missionId)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(evidence);

        var replay = MissionReconstruction.For(artifacts, evidence, missionId);
        var byRole = replay.Roles.ToDictionary(r => r.Role, StringComparer.OrdinalIgnoreCase);

        var calls = memory.GetRecentEvents(2000, "model_call", missionId);
        var tasks = memory.GetTasksForMission(missionId);
        var triggers = TriggersByRole(memory, missionId, tasks);

        var roles = new SortedDictionary<string, RoleTelemetry>(StringComparer.Ordinal);

        foreach (var role in RolesMentioned(tasks, calls, replay))
        {
            var mine = calls.Where(e => Text(e, "ant_name").Equals(role, StringComparison.OrdinalIgnoreCase)).ToList();
            var prompt = SumNullable(mine, "prompt_tokens");
            var completion = SumNullable(mine, "completion_tokens");
            var duration = SumNullable(mine, "duration_ms");

            var mineTasks = tasks
                .Where(t => Text(t, "assigned_ant").Equals(role, StringComparison.OrdinalIgnoreCase))
                .ToList();

            roles[role] = new RoleTelemetry
            {
                Role = role,
                Trigger = triggers.GetValueOrDefault(role, "planned"),
                ModelCalls = mine.Count,
                PromptTokens = prompt,
                CompletionTokens = completion,
                ModelDurationMs = duration,
                Provider = mine.Select(e => Meta(e, "provider")).FirstOrDefault(v => v is { Length: > 0 }),
                Model = mine.Select(e => Meta(e, "model")).FirstOrDefault(v => v is { Length: > 0 }),
                FailureClass = mineTasks
                    .Select(t => Text(t, "failure_type"))
                    .FirstOrDefault(v => v.Length > 0),
                ProducedArtifactIds = byRole.GetValueOrDefault(role)?.ProducedArtifactIds ?? Array.Empty<string>(),
                ConsumedArtifactIds = byRole.GetValueOrDefault(role)?.ConsumedArtifactIds ?? Array.Empty<string>(),
            };
        }

        var missionMs = MissionWallTime(tasks);
        var anyTokens = roles.Values.Any(r => r.PromptTokens is not null || r.CompletionTokens is not null);
        var anyProvider = roles.Values.Any(r => r.Provider is { Length: > 0 });

        var fields = new List<RecordedField>
        {
            new(QualificationFields.ProviderAndModel,
                anyProvider ? string.Join(", ", roles.Values
                    .Where(r => r.Provider is { Length: > 0 })
                    .Select(r => $"{r.Provider}/{r.Model}")
                    .Distinct(StringComparer.Ordinal)) : null,
                anyProvider,
                anyProvider
                    ? "read from the model_call events, which record the model that actually served "
                    + "the call rather than the configured route"
                    : "no model call was made in this mission, so no provider served it"),

            new(QualificationFields.Tokens,
                anyTokens
                    ? $"prompt {roles.Values.Sum(r => r.PromptTokens ?? 0)}, "
                    + $"completion {roles.Values.Sum(r => r.CompletionTokens ?? 0)}"
                    : null,
                anyTokens,
                anyTokens
                    ? "summed from model_call events; a provider that reports nothing contributes "
                    + "nothing rather than zero"
                    : "no provider in this run reported usage — unknown, not zero"),

            // THE ONE FIELD WITH NO PRODUCER, and it is stated rather than computed.
            //
            // `ModelRouter` records tokens; nothing anywhere records money. Turning tokens into
            // currency needs a per-provider price table, which is operator configuration that does
            // not exist — and inventing a rate here would put a number in front of an operator that
            // no part of this system can stand behind. Recorded as a gap so the exit gate cannot be
            // read as met on this field.
            new(QualificationFields.Cost, null, false,
                "no price table exists. The runtime records prompt and completion tokens per call and "
              + "nothing converts them to currency; a rate assumed here would be a fabricated figure "
              + "in an operator-facing report. Wiring it needs per-provider pricing as configuration."),

            new(QualificationFields.WallTime,
                missionMs is { } ms
                    ? $"mission {ms}ms; model time per role recorded on each RoleTelemetry"
                    : null,
                missionMs is not null,
                missionMs is not null
                    ? "mission span from the task timestamps; per-role model time summed from "
                    + "model_call durations"
                    : "no task in this mission carries usable start and finish timestamps"),

            new(QualificationFields.FailureClass,
                string.Join(", ", roles.Values
                    .Where(r => r.FailureClass is { Length: > 0 })
                    .Select(r => $"{r.Role}:{r.FailureClass}")
                    .DefaultIfEmpty("none")),
                true,
                "read from the persisted task rows as the typed class, never from prose"),

            new(QualificationFields.Trigger,
                string.Join(", ", roles.Values.Select(r => $"{r.Role}:{r.Trigger}")),
                roles.Count > 0,
                roles.Count > 0
                    ? "read from the admission events — handoff, policy insertion, adaptive repair or "
                    + "post-finalization — rather than inferred from the plan"
                    : "no role ran in this mission"),

            new(QualificationFields.Artifacts,
                $"{roles.Values.Sum(r => r.ProducedArtifactIds.Count)} produced, "
              + $"{roles.Values.Sum(r => r.ConsumedArtifactIds.Count)} consumed",
                true,
                "produced from the artifact store; consumed from the consumption ledger, which "
              + "records what each role actually read rather than what its task declared"),
        };

        return new LiveQualificationRecord
        {
            MissionId = missionId,
            Fields = fields,
            Roles = roles.Values.ToList(),
            Reconstructs = replay.IsConsistent,
            ReconstructionGaps = replay.Gaps,
            MissionDurationMs = missionMs,
        };
    }

    /// <summary>
    /// Which role each admission event named, so a trigger is READ rather than assumed.
    ///
    /// Order matters: a role can be admitted once, and the events are scanned from the most specific
    /// trigger to the least. Anything an admission event does not name was planned, which is the only
    /// remaining way a task reaches the queue.
    /// </summary>
    private static Dictionary<string, string> TriggersByRole(
        SqliteMemory memory, string missionId, List<Dictionary<string, object?>> tasks)
    {
        var triggers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Note(string eventType, string trigger)
        {
            foreach (var row in memory.GetRecentEvents(500, eventType, missionId))
            {
                var role = Text(row, "ant_name");
                if (role.Length == 0) role = Meta(row, "destination_role") ?? Meta(row, "role") ?? "";
                if (role.Length > 0 && !triggers.ContainsKey(role)) triggers[role] = trigger;
            }
        }

        Note("handoff_admitted", "handoff");
        Note("policy_review_inserted", "policy_inserted");
        Note("adaptive_repair", "adaptive_repair");
        Note("adaptive_delta_plan", "adaptive_delta_plan");
        Note("archivist_ran", "post_finalization");

        foreach (var t in tasks)
        {
            var role = Text(t, "assigned_ant");
            if (role.Length > 0 && !triggers.ContainsKey(role)) triggers[role] = "planned";
        }

        return triggers;
    }

    private static IEnumerable<string> RolesMentioned(
        List<Dictionary<string, object?>> tasks,
        List<Dictionary<string, object?>> calls,
        MissionReconstruction replay) =>
        tasks.Select(t => Text(t, "assigned_ant"))
            .Concat(calls.Select(e => Text(e, "ant_name")))
            .Concat(replay.Roles.Select(r => r.Role))
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The span from the earliest task start to the latest finish.
    ///
    /// Null rather than zero when no task carries both, because "the mission took no time" and "the
    /// timestamps are missing" are different claims and only one of them is possible.
    /// </summary>
    private static long? MissionWallTime(List<Dictionary<string, object?>> tasks)
    {
        DateTime? first = null, last = null;

        foreach (var t in tasks)
        {
            if (Stamp(t, "started_at") is { } s && (first is null || s < first)) first = s;
            foreach (var column in new[] { "finished_at", "completed_at", "failed_at" })
                if (Stamp(t, column) is { } f && (last is null || f > last)) last = f;
        }

        if (first is null || last is null || last < first) return null;
        return (long)(last.Value - first.Value).TotalMilliseconds;
    }

    private static DateTime? Stamp(Dictionary<string, object?> row, string column) =>
        DateTime.TryParse(Text(row, column), null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed) ? parsed : null;

    private static string Text(Dictionary<string, object?> row, string column) =>
        row.GetValueOrDefault(column)?.ToString() ?? "";

    /// <summary>One metadata value, or null. A malformed payload reads as absent, never as zero.</summary>
    private static string? Meta(Dictionary<string, object?> row, string key)
    {
        var json = Text(row, "metadata_json");
        if (json.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(key, out var value)) return null;
            return value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => value.GetString(),
                _ => value.ToString(),
            };
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Sum a numeric metadata key across events, or null when NOTHING reported it.
    ///
    /// The null is the point. Summing absent values to zero would turn "this provider does not report
    /// usage" into "this provider used no tokens", and the second is a claim about the run.
    /// </summary>
    private static int? SumNullable(List<Dictionary<string, object?>> rows, string key)
    {
        var total = 0;
        var saw = false;

        foreach (var row in rows)
            if (Meta(row, key) is { } raw && int.TryParse(raw, out var value)) { total += value; saw = true; }

        return saw ? total : null;
    }
}
