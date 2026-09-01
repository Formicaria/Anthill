using Anthill.Core.Memory;
using Anthill.SDK.Artifacts;
using Anthill.SDK.Common;
using Anthill.SDK.Tools;

namespace Anthill.Core.Tools;

/// <summary>
/// READ AN ARTIFACT ANOTHER MISSION PRODUCED, BY ID — IF THAT MISSION WAS VERIFIED. v0.3.8.106.
///
/// WHAT THIS MAKES POSSIBLE, and it is the program's continuity item: work that builds on work. A
/// mission that audited the roster produces a record; a later mission can answer a question about
/// that audit by reading the record rather than by re-running the audit and hoping the two agree.
/// Until now the colony had no way to do that at all — the artifact store's cross-mission reach is
/// <see cref="IArtifactStore.Get"/>, and nothing a mission could dispatch reached it.
///
/// ONLY FROM A VERIFIED MISSION, and this is the gate rather than a precaution. An artifact from a
/// mission that failed, was cancelled, stopped for a repeated failure or is still waiting on an
/// operator is a record of work whose own colony declined to stand behind it. Building on it would
/// launder an ungraded result into an input, and the second mission's answer would inherit a
/// confidence the first never earned — with nothing in the second mission's record to show where
/// it came from. `MissionOutcome.IsPositiveSuccess` is the same predicate auto-apply, promotion and
/// reinforcement already ask, and this asks it for the same reason: it is the ONE place the colony
/// says a result may be built upon.
///
/// A MISSION WITH NO PERSISTED EVALUATION IS REFUSED, not assumed good. That is every mission that
/// predates canonical evaluation and every one still running. Absence of a grade is not a pass —
/// the S3 rule, applied to lineage.
///
/// IT DOES NOT RECORD THE CONSUMPTION ITSELF. That happens at the dispatch chokepoint, which knows
/// the mission and task doing the reading; a tool taking its own consumer identity from an argument
/// would be taking it from the model, and a model that can name the mission it read on behalf of
/// can attribute its reads to a mission that never made them. See `ToolRegistry.RunTool`.
///
/// AND IT RECORDS NO INSPECTION EVIDENCE, deliberately. `ToolEvidence` admits the read-only
/// inspection tools so an audit can show it looked at something, and a cross-mission read is
/// already recorded — in the consumption ledger, by the chokepoint, with the hash as read. Adding
/// an evidence row would give one act two accounts in two stores, which is the arrangement this
/// repository keeps having to unpick. One record per fact.
/// </summary>
public sealed class ReadArtifactTool : ITool
{
    private readonly SqliteMemory _memory;

    public ReadArtifactTool(SqliteMemory memory) => _memory = memory;

    public const string ToolName = "read_artifact";

    public string Name => ToolName;

    public string Description =>
        "Read a typed artifact produced by an EARLIER mission, by its artifact id. Only artifacts "
      + "from missions that reached a verified outcome can be read. Returns the artifact's schema, "
      + "its producing mission and task, and its payload.";

    public string ParametersJson => """
        {"type":"object","properties":{"artifact_id":{"type":"string",
        "description":"The id of the artifact to read, as reported by an earlier mission."}},
        "required":["artifact_id"]}
        """;

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        var artifactId = (args.GetValueOrDefault("artifact_id")?.ToString() ?? "").Trim();
        if (artifactId.Length == 0)
            return new ToolResult(Name, false, "", "artifact_id is required.",
                FailureClass.ValidationFailure);

        Artifact? artifact;
        try
        {
            artifact = ((IArtifactStore)_memory).Get(artifactId);
        }
        catch (Exception error)
        {
            return new ToolResult(Name, false, "",
                $"the artifact store could not be read: {error.Message}", FailureClass.ToolFailure);
        }

        if (artifact is null)
            return new ToolResult(Name, false, "",
                $"no artifact '{artifactId}' exists in this colony.", FailureClass.ValidationFailure);

        // THE LINEAGE GATE. Read from the PERSISTED evaluation, which is the colony's one grade for
        // a mission — never re-derived here, for the reason `MissionEvaluation` exists: a second
        // opinion about whether a mission succeeded is a second answer.
        var evaluation = _memory.LoadMissionEvaluation(artifact.MissionId);

        if (evaluation is null)
            return new ToolResult(Name, false, "",
                $"artifact '{artifactId}' belongs to mission {artifact.MissionId}, which has no "
              + "persisted evaluation — it is still running, or it predates canonical grading. An "
              + "ungraded mission's output is not something later work may be built on.",
                FailureClass.PolicyDenial);

        if (!Outcomes.MissionOutcome.IsPositiveSuccess(evaluation.OutcomeCode))
            return new ToolResult(Name, false, "",
                $"artifact '{artifactId}' belongs to mission {artifact.MissionId}, which graded "
              + $"'{evaluation.OutcomeCode}' rather than a verified success. Building on it would "
              + "give this mission's answer a confidence the producing mission never earned.",
                FailureClass.PolicyDenial);

        return new ToolResult(Name, true, Json.Dumps(new
        {
            artifact_id = artifact.Id,
            schema = artifact.Schema,
            schema_version = artifact.SchemaVersion,
            produced_by_mission = artifact.MissionId,
            produced_by_task = artifact.TaskId,
            producer_role = artifact.ProducerRole,
            content_hash = artifact.ContentHash,
            created_at = artifact.CreatedAt.ToIso(),
            payload = artifact.Payload,
        }, indented: true));
    }
}
