using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Conversations;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Outcomes;
using Anthill.SDK.Artifacts;

namespace Anthill.Core.Verification;

/// <summary>Who is asking to promote a patch. It changes ONE question: who satisfies the human.</summary>
public enum PromotionActor
{
    /// <summary>An operator clicked Apply. A human approval row must exist and be approved.</summary>
    Human,
    /// <summary>A Bypass conversation. The human is skipped BY POLICY — nothing else is.</summary>
    Bypass,
    /// <summary>The auto-apply Director. No human at all, so the canonical evaluation stands in.</summary>
    Automation,
}

/// <summary>Which gate said no. Typed so a caller can act on it and a log can name it.</summary>
public enum PromotionRefusal
{
    None,
    PatchUnknown,
    PatchStatusForbids,
    WriteGatesOff,
    RollbackHalted,
    DeterministicBlock,
    SecurityReviewBlocked,
    ReviewIncomplete,
    EvidenceAboutAnotherRevision,
    /// <summary>The live tree is not the one verification read. The evidence is about other bytes.</summary>
    WorkspaceMoved,
    MissionNotVerified,
    HumanApprovalMissing,
}

/// <param name="Layer">The gate that refused, named rather than left to inference.</param>
public sealed record PromotionVerdict(bool Promotable, PromotionRefusal Refusal, string Layer, string Reason)
{
    public static PromotionVerdict Allow(string reason) => new(true, PromotionRefusal.None, "promotion-gate", reason);
    public static PromotionVerdict Refuse(PromotionRefusal refusal, string layer, string reason) =>
        new(false, refusal, layer, reason);
}

/// <summary>
/// THE ONE AUTHORITY ON WHETHER A PROPOSED PATCH MAY BE WRITTEN. v0.3.8.91.
///
/// WHY IT EXISTS. Five code paths could put a proposal's bytes on the operator's tree, and each one
/// carried its own idea of what to check first:
///
///   - `ApplyApprovedPatch` (the Apply button): approval exists, approval approved, action type is a
///     patch proposal, patch exists, patch not already applied/rejected/failed. Five checks, and
///     **no evidence, no review, no revision, no block** — it never asked whether anything had
///     verified the change it was about to write.
///   - `ApplyUnderBypass`: a deterministic block, and the conversation policy. Two checks, then a
///     per-proposal loop through the Apply path — running BEFORE the tester and soldier tasks it had
///     just inserted have executed.
///   - `AutoApplyRunner`: nine, including the canonical evaluation, revision-bound evidence, a
///     whole-set preflight, a policy allowlist and a durable journal.
///   - `PatchVerifyRunner`'s legacy arm: the write gates.
///   - `ApplyPatchTool`: the write gates, path containment, blocked paths and a per-file base hash —
///     which is the right set for a TOOL and knows nothing about approvals or evidence.
///
/// One capability, five answers to "may this happen", and the strictest one was the only one nobody
/// clicks. An external review named this the top architectural target and it is: *no code path
/// writes a proposed patch; every code path asks the gate.*
///
/// WHAT `PromotionActor` DOES AND DOES NOT CHANGE. Exactly one condition — who satisfies the human.
/// Everything else applies to everyone. That is the whole reasoning behind the reviewer's sentence
/// this class exists to enforce: **Skip All Approvals skips the human, not the colony's safety
/// system.** A Bypass conversation is an operator saying "stop asking me", not "stop checking".
///
/// ABSENCE IS NOT PASS, with one deliberate exception, stated rather than hidden. Revision-bound
/// evidence arrived in v0.3.8.57; missions whose evidence predates it carry no identity at all, and
/// refusing every such mission would turn a schema addition into a retroactive freeze on work that
/// was legitimately verified under the rules of its own release. So a mission with NO
/// revision-identified evidence is not refused on that ground — matching `AutoApplyRunner`'s
/// existing rule exactly, rather than inventing a second one. A mission that HAS such evidence must
/// have evidence that judges THIS revision.
///
/// WHAT THIS GATE IS NOT. It is not the write gate, the path guard, or the base-hash check —
/// `ApplyPatchTool` owns those and keeps them, because they are properties of the file operation
/// rather than of the promotion decision. It is not atomicity either: applying a set as one unit is
/// the next commit in this release. This answers one question, for everybody, in one place.
/// </summary>
public static class PatchPromotionGate
{
    /// <summary>The description marker `InsertPolicyReviewTasks` stamps on a review task.</summary>
    private static string ReviewMarker(string role, string patchSetId) => $"policy-review:{role}:{patchSetId}";

    /// <summary>
    /// May this patch be written? Reads persisted state only, so the answer survives a restart and
    /// two callers cannot disagree by holding different in-memory objects.
    /// </summary>
    public static PromotionVerdict Evaluate(
        SqliteMemory memory, IEvidenceStore evidence, string patchId, PromotionActor actor)
    {
        ArgumentNullException.ThrowIfNull(memory);

        var patch = memory.GetPatchProposal(patchId);
        if (patch is null)
            return PromotionVerdict.Refuse(PromotionRefusal.PatchUnknown, "patch-store",
                $"no patch proposal exists with id {patchId}");

        string Field(string key) => patch.GetValueOrDefault(key)?.ToString() ?? "";

        var missionId = Field("mission_id");
        var patchSetId = Field("patch_set_id");
        var producingTaskId = Field("task_id");
        var status = Field("status");

        if (status != PatchStatus.Proposed.Value() && status != PatchStatus.Approved.Value())
            return PromotionVerdict.Refuse(PromotionRefusal.PatchStatusForbids, "patch-store",
                $"patch {patchId} is '{status}'. Only a proposed or approved patch can be promoted — "
              + "an applied one would be written twice and a rejected one was refused by a person.");

        // The write gates. Checked here as well as in the tool because a refusal an operator can act
        // on beats a tool-level failure they have to decode from an error string.
        if (!AnthillRuntime.EnablePatchApplication || !AnthillRuntime.EnableFileWriting)
            return PromotionVerdict.Refuse(PromotionRefusal.WriteGatesOff, "write-gates",
                "patch_application_enabled and file_writing_enabled must both be on before anything "
              + "writes to the operator's tree");

        // A previous rollback that could not complete leaves a durable marker. Until a human has
        // looked, the tree's state is unknown, and writing more into an unknown tree is how a
        // partial rollback becomes an unrecoverable one.
        if (SDK.Common.ApplyTransaction.HasRollbackFailure(AnthillRuntime.AllowedWorkspaceRoot))
            return PromotionVerdict.Refuse(PromotionRefusal.RollbackHalted, "apply-journal",
                "a previous apply left a ROLLBACK_FAILED marker in the apply journal. The workspace "
              + "state is unverified until an operator resolves it.");

        var tasks = missionId.Length > 0 ? memory.GetTasksForMission(missionId) : new();

        string TaskField(Dictionary<string, object?> row, string key) => row.GetValueOrDefault(key)?.ToString() ?? "";

        // The producing task's deterministic block. v0.3.8.91 gave this a column; before that it was
        // in-memory only, so this gate could not have existed.
        var producer = tasks.FirstOrDefault(t => TaskField(t, "id") == producingTaskId);
        if (producer is not null && TaskField(producer, "deterministic_block").Length > 0)
            return PromotionVerdict.Refuse(PromotionRefusal.DeterministicBlock, "deterministic-block",
                $"the task that produced this patch carries a deterministic block: "
              + TaskField(producer, "deterministic_block"));

        // The policy review tasks, when they exist. Absence is NOT treated as completion — but it is
        // also not treated as refusal, because a role can be legitimately absent (its runtime is
        // unavailable, or admission refused it) and those cases log their own events. What is
        // refused is a review that was inserted and has not finished, or has finished and said no.
        if (patchSetId.Length > 0)
        {
            foreach (var role in new[] { "soldier", "tester" })
            {
                var marker = ReviewMarker(role, patchSetId);
                var review = tasks.FirstOrDefault(t =>
                    TaskField(t, "assigned_ant") == role &&
                    TaskField(t, "description").Contains(marker, StringComparison.Ordinal));

                if (review is null) continue;

                var reviewStatus = TaskField(review, "status");
                if (reviewStatus != Domain.TaskStatus.Complete.Value())
                    return PromotionVerdict.Refuse(PromotionRefusal.ReviewIncomplete, $"{role}-review",
                        $"the {role} review for patch set {patchSetId} is '{reviewStatus}'. It was "
                      + "inserted because policy requires it, so promoting before it finishes would "
                      + "make the review advisory.");

                if (role == "soldier" && SoldierBlocked(memory, TaskField(review, "id")))
                    return PromotionVerdict.Refuse(PromotionRefusal.SecurityReviewBlocked, "soldier-review",
                        $"the soldier's review of patch set {patchSetId} recorded a blocking finding");
            }
        }

        // Evidence must judge THIS revision when the mission has revision-identified evidence at all.
        if (missionId.Length > 0 && evidence is not null && patchSetId.Length > 0)
        {
            IReadOnlyList<Evidence> rows;
            try { rows = evidence.ForMission(missionId); }
            catch (Exception error)
            {
                // An evidence store that cannot be read is not an evidence store that said yes.
                return PromotionVerdict.Refuse(PromotionRefusal.EvidenceAboutAnotherRevision, "evidence-store",
                    $"the evidence store could not be read ({error.Message}), so nothing can be shown "
                  + "to have verified this revision");
            }

            var identified = rows.Where(e => e.IdentifiesARevision).ToList();
            if (identified.Count > 0)
            {
                var revisionId = $"rev:{patchSetId}";
                var mine = identified.Where(e =>
                    string.Equals(e.RevisionId, revisionId, StringComparison.Ordinal)).ToList();

                var judged = mine.Count > 0
                    && MissionVerification.EvidenceJudgesRevision(mine, revisionId, mine[0].TreeHash);

                if (!judged)
                    return PromotionVerdict.Refuse(PromotionRefusal.EvidenceAboutAnotherRevision, "evidence",
                        $"this mission has revision-identified evidence and none of it judges "
                      + $"{revisionId} with a deterministic pass. Evidence about a different revision "
                      + "is a true statement about the wrong bytes.");
            }
        }

        // THE LIVE TREE MUST STILL BE THE ONE VERIFICATION READ. v0.3.8.91.
        //
        // Every other binding describes the PATCH — the base revision, the patch-set content hash,
        // and `AppliedTreeHash`, which despite its name covers only the files the patch touched. So
        // the tree could change underneath a verified set and nothing would notice: verification
        // builds and tests a sandbox containing A.cs and B.cs, the patch changes only A.cs, somebody
        // edits B.cs, and the apply still finds A.cs hashing to its recorded base. The build was
        // proven against a tree that no longer exists.
        //
        // NotCaptured is not a refusal — a non-git workspace, or a set from before this existed, was
        // never measured, and refusing every such set would turn a schema addition into a
        // retroactive freeze. Unmeasurable IS a refusal: something WAS captured and cannot be read
        // back now, which is a different statement and one the operator should see.
        var recorded = memory.GetPatchSetBaseFingerprint(patchSetId);
        var freshness = Workspaces.WorkspaceFingerprint.Compare(recorded, AnthillRuntime.AllowedWorkspaceRoot);

        if (freshness is Workspaces.FreshnessVerdict.Moved or Workspaces.FreshnessVerdict.Unmeasurable)
            return PromotionVerdict.Refuse(PromotionRefusal.WorkspaceMoved, "workspace-freshness",
                freshness == Workspaces.FreshnessVerdict.Moved
                    ? "the workspace has changed since this patch set was verified. The evidence "
                    + "describes a tree that no longer exists — re-run verification against the tree "
                    + "as it stands rather than writing into one nothing has checked."
                    : "a workspace fingerprint was recorded for this patch set and cannot be read "
                    + "back now, so whether the tree still matches what verification read is "
                    + "unknown. Unknown is not unchanged.");

        // The human, and the only condition the actor changes.
        switch (actor)
        {
            case PromotionActor.Human:
            {
                var approval = memory.GetApprovalForTarget(patchId);
                var approvalStatus = approval?.GetValueOrDefault("status")?.ToString() ?? "";
                if (approvalStatus != ApprovalStatus.Approved.Value())
                    return PromotionVerdict.Refuse(PromotionRefusal.HumanApprovalMissing, "approval",
                        approval is null
                            ? $"no approval request exists for patch {patchId}"
                            : $"the approval for patch {patchId} is '{approvalStatus}', not approved");
                break;
            }

            case PromotionActor.Bypass:
            {
                var conversation = missionId.Length > 0 ? memory.FindConversationForMission(missionId) : null;
                if (conversation?.EffectivePolicy != EscalationPolicy.Bypass)
                    return PromotionVerdict.Refuse(PromotionRefusal.HumanApprovalMissing, "escalation-policy",
                        "no attributed Bypass policy governs this mission, so the human approval has "
                      + "not been deliberately skipped — it is simply missing. `EffectivePolicy` "
                      + "fails closed to Ask for an unattributed or cancelled conversation.");
                break;
            }

            case PromotionActor.Automation:
            {
                var evaluation = memory.LoadMissionEvaluation(missionId);
                if (evaluation is null || !evaluation.IsPositive)
                    return PromotionVerdict.Refuse(PromotionRefusal.MissionNotVerified, "canonical-evaluation",
                        $"mission {missionId} has no canonical completed_verified evaluation "
                      + $"(outcome: {evaluation?.OutcomeCode ?? "none persisted"}). With no human in "
                      + "the loop, the canonical evaluation is what stands in for one.");
                break;
            }
        }

        return PromotionVerdict.Allow(
            $"promotable for {actor}: status {status}, write gates on, no deterministic block, "
          + "required reviews complete, evidence consistent with this revision");
    }

    /// <summary>
    /// Did the soldier record a BLOCK, read from its structured warnings rather than its prose?
    ///
    /// `SoldierAnt.SoldierBlockMarker` leads the warning list when the review blocks and the rule ids
    /// follow. A missing result is "not recorded", which the caller has already treated as an
    /// incomplete review — it must never read as "no findings".
    /// </summary>
    private static bool SoldierBlocked(SqliteMemory memory, string taskId)
    {
        if (taskId.Length == 0) return false;
        var result = memory.LoadTaskResult(taskId);
        return result is not null
            && result.Warnings.Any(w => string.Equals(w, SoldierAnt.SoldierBlockMarker, StringComparison.Ordinal));
    }
}
