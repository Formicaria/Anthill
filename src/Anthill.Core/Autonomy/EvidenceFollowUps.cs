using Anthill.Core.Domain;
using Anthill.Core.Outcomes;

namespace Anthill.Core.Autonomy;

/// <summary>
/// v2.24.0 Phase C6: follow-up objectives derived from what verification actually FOUND, rather
/// than from what a model suggested.
///
/// Today's follow-ups come from `strategy.FollowUps` — the Strategist's free-form proposal about
/// what might be worth doing next. That is a model's opinion about future work, created on the
/// strength of a mission's success. The verifier, meanwhile, reports "Missing Steps:" — a concrete
/// list of what the mission did NOT do, which nothing reads.
///
/// This extracts those. Three properties the ADR's vocabulary table demands, and this enforces:
///
///  - **A follow-up is not a retry.** A retry re-runs the same task; a follow-up is a NEW objective
///    for work the mission never attempted. They have separate budgets and must never be confused,
///    or a failing mission could re-run itself forever under a different name.
///  - **Evidence-derived, not model-derived.** Only lines the verifier actually recorded as missing
///    become follow-ups. A model cannot invent future work here and have it enqueued.
///  - **Depth-limited.** A follow-up of a follow-up terminates: the parent chain is capped, so
///    verification findings cannot generate an unbounded objective tree.
/// </summary>
public static class EvidenceFollowUps
{
    /// <summary>The verifier's own section header. Its output is the evidence; this reads it.</summary>
    private const string MissingStepsHeader = "Missing Steps:";

    /// <summary>Phrases the verifier emits when there is nothing missing. Never a follow-up.</summary>
    private static readonly string[] NothingMissing =
    {
        "none identified", "none", "n/a", "nothing", "no missing steps",
    };

    /// <summary>
    /// The verifier's other section headers. Its output format is fixed
    /// (Verdict / Reasoning / Missing Steps / Risk Notes), so the end of the findings block is
    /// identifiable rather than guessed.
    /// </summary>
    private static readonly string[] SectionHeaders =
    {
        "Risk Notes:", "Reasoning:", "Verdict:", "Degraded Sections:",
    };

    /// <summary>How deep a follow-up chain may go before findings stop generating objectives.</summary>
    public const int MaxFollowUpDepth = 2;

    /// <summary>
    /// The concrete gaps a verification recorded. Reads the "Missing Steps:" block out of the
    /// verifier's own output — the same text the operator reads, so a follow-up can always be
    /// traced to the sentence that caused it.
    /// </summary>
    public static IReadOnlyList<string> MissingSteps(string? verifierResult)
    {
        var text = verifierResult ?? "";
        var at = text.IndexOf(MissingStepsHeader, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return Array.Empty<string>();

        var after = text[(at + MissingStepsHeader.Length)..];
        var steps = new List<string>();
        foreach (var raw in after.Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '*', '•').Trim();
            if (line.Length == 0) continue;

            // Stop at the next KNOWN section, not merely at "a line with a colon".
            //
            // The verifier writes the clean case inline — "Missing Steps: None identified by static
            // verification." — so the block is empty and the very next line is "Risk Notes: ...".
            // A looser rule read that as a missing step and produced a follow-up objective titled
            // "Risk Notes: none": work invented out of a section header, from a verification that
            // found nothing wrong.
            if (SectionHeaders.Any(h => line.StartsWith(h, StringComparison.OrdinalIgnoreCase))) break;

            if (NothingMissing.Any(n => line.Equals(n, StringComparison.OrdinalIgnoreCase)
                                     || line.StartsWith(n, StringComparison.OrdinalIgnoreCase))) continue;

            steps.Add(line.TrimEnd('.'));
            if (steps.Count >= 3) break;   // a finding list, not a backlog dump
        }
        return steps;
    }

    /// <summary>
    /// Turn a finished mission's verification findings into follow-up objectives.
    ///
    /// Returns empty unless the mission actually verified — an unverified mission's "missing steps"
    /// describe work that may not be missing at all, since the thing that was supposed to check is
    /// what failed.
    /// </summary>
    public static IReadOnlyList<Objective> From(
        IReadOnlyList<Dictionary<string, object?>>? taskRows, string missionId, Objective? parent, string missionOutcome)
    {
        if (taskRows is null || taskRows.Count == 0 || parent is null) return Array.Empty<Objective>();

        // Only a verified mission's findings are trustworthy evidence of a real gap.
        if (!MissionOutcome.IsPositiveSuccess(missionOutcome)) return Array.Empty<Objective>();

        if (DepthOf(parent) >= MaxFollowUpDepth) return Array.Empty<Objective>();

        static string Field(Dictionary<string, object?> r, string k) => r.GetValueOrDefault(k)?.ToString() ?? "";

        // The completed verifier row — the same role rule MissionVerification uses for verdicts,
        // so findings and verdicts always come from the same task.
        var verifier = taskRows.FirstOrDefault(r =>
            string.Equals(Field(r, "assigned_ant"), "verifier", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Field(r, "status"), "complete", StringComparison.OrdinalIgnoreCase));
        if (verifier is null) return Array.Empty<Objective>();

        var verifierId = Field(verifier, "id");
        var followUps = new List<Objective>();
        foreach (var step in MissingSteps(Field(verifier, "result")))
        {
            followUps.Add(new Objective
            {
                Title = Common.TextUtil.Truncate(step, 120),
                Charter = $"Verification of '{Common.TextUtil.Truncate(parent.Title, 80)}' recorded this as "
                        + $"missing: {step}",
                Status = ObjectiveStatus.Pending,
                ParentObjectiveId = parent.Id,
                Priority = parent.Priority,
                // Its OWN budget. A follow-up must never draw on the parent's remaining runs, or
                // an objective could extend itself indefinitely by discovering more work.
                MaxRuns = 1,
                Metadata = new Dictionary<string, object?>
                {
                    ["origin"] = "verification_evidence",
                    ["source_mission_id"] = missionId,
                    ["source_task_id"] = verifierId,
                    ["follow_up_depth"] = DepthOf(parent) + 1,
                },
            });
        }
        return followUps;
    }

    /// <summary>
    /// How deep in a follow-up chain an objective sits. Read from the metadata written when it was
    /// created; a root objective has no marker and is depth 0.
    /// </summary>
    public static int DepthOf(Objective? o)
    {
        if (o?.Metadata is null) return 0;
        if (!o.Metadata.TryGetValue("follow_up_depth", out var raw) || raw is null) return 0;
        return int.TryParse(raw.ToString(), out var d) && d > 0 ? d : 0;
    }
}
