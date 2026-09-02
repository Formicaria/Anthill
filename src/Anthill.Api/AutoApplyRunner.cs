using System.Diagnostics;
using System.Text;
using Anthill.Core.Autonomy;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Orchestration;
using Anthill.Core.Outcomes;

namespace Anthill.Api;

/// <summary>
/// Phase 5 gated auto-apply orchestration. After an autonomous mission produces patch proposals,
/// the Director calls <see cref="Run"/>: it filters the proposals through the strict
/// <see cref="AutoApplyPolicy"/>, applies the eligible ones to disk (with per-file backups),
/// runs a verify step (built-in <c>dotnet build</c> + <c>dotnet test</c>, or an operator command),
/// and — this is the whole safety story — <b>keeps the changes only if verify is green, otherwise
/// rolls every one of them back</b> from the pre-apply backups. Nothing here runs unless the
/// master switch and both write gates are on, and an empty path allowlist makes it inert.
///
/// It runs on the single Director thread, after the mission's outcome is recorded, so it never
/// races the colony's own bookkeeping; the verify build blocks that thread (deliberately — we do
/// not want the Director launching more work mid-verify).
/// </summary>
public static class AutoApplyRunner
{
    private const string SystemMissionId = AnthillRuntime.SystemApiMissionId;

    public static void Run(Queen queen, string missionId)
    {
        if (!AnthillRuntime.AutonomyAutoApplyEnabled) return;

        // v0.3.8.94 — THE FOLD THE GATE'S OWN DOCS PROMISED. Three of this method's opening checks
        // — the canonical evaluation, the write gates, and the rollback-failure marker — moved into
        // `PatchPromotionGate.Evaluate`, which is consulted below for every eligible proposal as
        // `PromotionActor.Automation`. They were exact duplicates of the gate's
        // MissionNotVerified / WriteGatesOff / RollbackHalted conditions — two implementations of
        // one rule, this repository's defect class 5, and the gate header itself listed this
        // runner's "nine checks" as the divergence to end. Folding also STRENGTHENS this lane: the
        // gate additionally refuses on a producing task's deterministic block, an incomplete or
        // blocking policy review, and a moved workspace — conditions this runner never checked.
        //
        // What deliberately stays here, because it is about the SET or the ENVIRONMENT rather than
        // one proposal's promotability: the writable-workspace probe, the AutoApplyPolicy
        // allowlist, the set-level evidence CONTENT check (the bytes about to be applied must be
        // the bytes the evidence judged — a per-proposal gate cannot answer that), the whole-set
        // preflight, and the durable transaction.

        // Preflight: if the workspace root isn't writable (e.g. the source tree is read-only under
        // systemd ProtectSystem=strict), every apply would fail one-by-one. Surface it once, clearly.
        if (!WorkspaceWritable(out var wsReason))
        {
            queen.Memory.LogEvent(SystemMissionId, "autonomy_autoapply_skipped",
                $"Auto-apply is enabled but the workspace root ({AnthillRuntime.AllowedWorkspaceRoot}) is not writable — {wsReason}. " +
                "Point agent_workspace_dir at a writable checkout the service owns to let auto-apply land changes.",
                antName: "director", metadata: new() { ["reason"] = "workspace_readonly", ["mission_id"] = missionId, ["workspace"] = AnthillRuntime.AllowedWorkspaceRoot });
            return;
        }

        // Candidate patches: still-proposed proposals from this mission.
        var candidates = queen.Memory.ListPatchProposalsForMission(missionId)
            .Where(p => (p.GetValueOrDefault("status")?.ToString() ?? "") == PatchStatus.Proposed.Value())
            .Select(p => p.GetValueOrDefault("id")?.ToString() ?? "")
            .Where(id => id.Length > 0)
            .ToList();
        if (candidates.Count == 0) return;

        // v0.3.8.57 — the PROPOSAL is carried alongside the ids so preflight can compute the whole
        // set before anything is written, without re-reading every row from the store. The PATCH SET
        // id travels for the same reason: the evidence check below needs to know which set a
        // proposal belongs to (evidence is stamped `rev:{patchSetId}`), and going back to the store
        // for it would be a second source for something already in hand here.
        var eligible = new List<(string PatchId, string? PatchSetId, string? TaskId, PatchProposal Proposal)>();
        foreach (var patchId in candidates)
        {
            var full = queen.Memory.GetPatchProposal(patchId);
            if (full is null) continue;
            var proposal = new PatchProposal
            {
                Id = patchId,
                FilePath = full.GetValueOrDefault("file_path")?.ToString() ?? "",
                ChangeType = EnumExtensions.ParsePatchChangeType(full.GetValueOrDefault("change_type")?.ToString() ?? "modify"),
                OldContent = full.GetValueOrDefault("old_content") as string,
                BaseHash = full.GetValueOrDefault("base_hash") as string,
                DestinationPath = full.GetValueOrDefault("destination_path") as string,
                NewContent = full.GetValueOrDefault("new_content") as string,
            };
            var decision = AutoApplyPolicy.Evaluate(proposal);
            if (decision.Eligible)
                eligible.Add((patchId, full.GetValueOrDefault("patch_set_id")?.ToString(),
                    full.GetValueOrDefault("task_id")?.ToString(), proposal));
            else
                queen.Memory.LogEvent(missionId, "autonomy_autoapply_ineligible",
                    $"Patch not eligible for auto-apply: {proposal.FilePath} — {decision.Reason}", full.GetValueOrDefault("task_id")?.ToString(), "director",
                    metadata: new() { ["patch_id"] = patchId, ["file_path"] = proposal.FilePath, ["reason"] = decision.Reason });
        }
        if (eligible.Count == 0) return;

        // v0.3.8.97 — WHICH TREE ARE THESE SETS FOR. The Director's whole lane — the writable
        // probe above, the verify step, the standalone-branch commit — is built around the
        // colony's OWN checkout. A set whose persisted workspace names another SourceRoot (a
        // project mission's) must therefore be REFUSED here, not applied into the live root the
        // lane happens to hold: project sets promote through the mission lanes (bypass or the
        // operator's Apply), which carry the target through the transaction. Unresolvable targets
        // refuse for the same reason the gate does — nothing writes on a guessed tree.
        var targetRefusals = new List<string>();
        foreach (var setId in eligible.Select(e => e.PatchSetId ?? "").Distinct(StringComparer.Ordinal))
        {
            var target = Anthill.Core.Verification.PatchTargetResolver.Resolve(queen.Memory, setId);
            if (!target.Ok)
                targetRefusals.Add($"set {setId}: {target.Problem}");
            else if (!target.IsLiveTree)
                targetRefusals.Add($"set {setId} targets {target.Root} — a project checkout, not the "
                    + "colony's own tree. Auto-apply serves only the live root; project sets promote "
                    + "through the mission lanes, which carry their target through the transaction.");
        }
        if (targetRefusals.Count > 0)
        {
            queen.Memory.LogEvent(missionId, "autonomy_autoapply_skipped",
                $"Auto-apply refused for mission {missionId}: " + string.Join(" | ", targetRefusals.Take(5)),
                antName: "director",
                metadata: new() { ["mission_id"] = missionId, ["reason"] = "target_not_live_or_unresolvable",
                                  ["refusals"] = targetRefusals });
            return;
        }

        // v0.3.8.94 — THE GATE, AS AUTOMATION, FOR EVERY PROPOSAL. One refusal refuses the set:
        // it applies as a unit, so it is gated as one. The gate's verdict is typed and names its
        // layer, and the halted case keeps its own event — an operator must be able to tell "this
        // run was refused" from "auto-apply is halted until someone resolves the tree".
        var gateRefusals = new List<string>();
        var halted = false;
        foreach (var (patchId, _, _, proposal) in eligible)
        {
            var verdict = Anthill.Core.Verification.PatchPromotionGate.Evaluate(
                queen.Memory, (Anthill.SDK.Artifacts.IEvidenceStore)queen.Memory, patchId,
                Anthill.Core.Verification.PromotionActor.Automation);
            if (verdict.Promotable) continue;
            gateRefusals.Add($"{proposal.FilePath} [{verdict.Layer}]: {verdict.Reason}");
            halted |= verdict.Refusal == Anthill.Core.Verification.PromotionRefusal.RollbackHalted;
        }
        if (gateRefusals.Count > 0)
        {
            if (halted)
                queen.Memory.LogEvent(SystemMissionId, "autonomy_autoapply_halted",
                    "CRITICAL: auto-apply is halted — a previous batch's rollback did not complete "
                  + $"(promotion gate, mission {missionId}): " + string.Join(" | ", gateRefusals.Take(3))
                  + " Inspect the tree, resolve, and delete the marker to re-enable.",
                    antName: "director",
                    metadata: new() { ["mission_id"] = missionId, ["severity"] = "critical",
                                      ["reason"] = "rollback_failed_marker", ["refusals"] = gateRefusals });
            else
                queen.Memory.LogEvent(SystemMissionId, "autonomy_autoapply_skipped",
                    $"Auto-apply refused by the promotion gate for mission {missionId}: "
                  + $"{gateRefusals.Count} of {eligible.Count} proposal(s) were refused, so none were "
                  + "applied. " + string.Join(" | ", gateRefusals.Take(5)),
                    antName: "director",
                    metadata: new() { ["mission_id"] = missionId, ["reason"] = "promotion_gate_refused",
                                      ["refused_count"] = gateRefusals.Count, ["refusals"] = gateRefusals });
            return;
        }

        var verifyCmdConfigured = !string.IsNullOrWhiteSpace(AnthillRuntime.AutonomyAutoApplyVerifyCmd);
        var verifyDescription = verifyCmdConfigured ? AnthillRuntime.AutonomyAutoApplyVerifyCmd
            : (AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify ? "(none — keep without verify)" : "dotnet build && dotnet test");
        var workspace = Directory.Exists(AnthillRuntime.AllowedWorkspaceRoot)
            ? Path.GetFullPath(AnthillRuntime.AllowedWorkspaceRoot) : AnthillRuntime.AllowedWorkspaceRoot;

        // v0.3.8.94: the rollback-failure HALT moved into the promotion gate (RollbackHalted),
        // which every proposal above just passed — the durable-marker rule is unchanged, its
        // checker is now the same one the Apply button and the bypass lane consult, and the
        // dedicated `autonomy_autoapply_halted` event survives on the gate-refusal path above.
        queen.Memory.LogEvent(missionId, "autonomy_autoapply_started",
            $"Director auto-applying {eligible.Count} eligible patch(es), then verifying with: {verifyDescription}.", antName: "director",
            metadata: new() { ["mission_id"] = missionId, ["eligible_count"] = eligible.Count, ["verify_cmd"] = verifyDescription, ["workspace"] = workspace });

        /* v0.3.8.57 — PREFLIGHT THE WHOLE SET BEFORE WRITING ANYTHING.
         *
         * The set is applied as a unit or not at all. Every proposal is computed against the tree
         * first, with no IO, and one refusal aborts the batch before a single byte is written.
         *
         * What this replaces: the loop below applied patches one at a time and, on a failure, logged
         * it and CARRIED ON to the next. A set whose third patch was stale therefore left patches one
         * and two applied and the rest not — a repository in a state no revision ever had, mixing old
         * and new, with rollback reachable only through the verify step further down. If verify was
         * not configured, or the run ended before reaching it, the mixture simply stayed.
         *
         * Preflight cannot be perfect — the tree can change between the check and the write — so it
         * is a gate, not a guarantee, and the transactional rollback below is what covers the gap.
         */
        // v0.3.8.57 — EVIDENCE ABOUT THIS REVISION, or nothing is applied.
        //
        // Preflight below asks whether each patch still fits the tree. This asks the prior question:
        // is there deterministic, PASSING evidence about the exact revision this patch set produced?
        //
        // The task-level pairing in MissionVerification already refuses to call a mission verified
        // when its latest revision has no tester run stamped with it. That is the scheduling claim.
        // This is the promotion claim, and it is the one that matters here, because auto-apply writes
        // to the LIVE TREE: a set applied on the strength of evidence about a different revision is
        // exactly v3.8.22's failure — true statements about the wrong bytes — with a write at the end
        // of it.
        //
        // Missions with no revision-identified evidence at all are UNAFFECTED. That is not a loophole
        // being left open: evidence written before v0.3.8.57 carries no identity, and refusing every
        // such mission would convert a schema addition into a retroactive freeze on work that was
        // legitimately verified under the rules of its own release.
        var evidenceRefusals = RefuseEvidenceAboutAnotherRevision(
            (Anthill.SDK.Artifacts.IEvidenceStore)queen.Memory, missionId, eligible);
        if (evidenceRefusals.Count > 0)
        {
            queen.Memory.LogEvent(missionId, "autonomy_autoapply_stale_evidence",
                "Auto-apply refused the whole set: its evidence does not judge the revision these "
                + "patches produced. " + string.Join(" | ", evidenceRefusals.Take(5)), antName: "director",
                metadata: new()
                {
                    ["mission_id"] = missionId, ["eligible_count"] = eligible.Count,
                    ["refusals"] = evidenceRefusals,
                });
            return;
        }

        var refusals = Preflight(eligible);
        if (refusals.Count > 0)
        {
            queen.Memory.LogEvent(missionId, "autonomy_autoapply_preflight_refused",
                $"Auto-apply refused the whole set: {refusals.Count} of {eligible.Count} patch(es) "
                + "cannot be applied to the tree as it stands, so none were. "
                + string.Join(" | ", refusals.Take(5)), antName: "director",
                metadata: new()
                {
                    ["mission_id"] = missionId, ["eligible_count"] = eligible.Count,
                    ["refused_count"] = refusals.Count, ["refusals"] = refusals,
                });
            return;
        }

        // Apply each eligible patch INSIDE A DURABLE TRANSACTION. v0.3.8.62 (S4). The journal is
        // written before the first mutation, each file's pre-state and backup are recorded before
        // its write, and a crash at any instant leaves a journal that startup recovery can replay.
        // The tool still owns path guards and patch semantics; the transaction owns durability.
        var tx = Anthill.SDK.Common.ApplyTransaction.Begin(workspace, note: $"auto-apply {missionId}");
        var txGuard = new Anthill.Core.Security.WorkspacePathGuard(AnthillRuntime.AllowedWorkspaceRoot);
        var applied = new List<Queen.AutoApplyOutcome>();
        foreach (var (patchId, _, taskId, proposal) in eligible)
        {
            Anthill.SDK.Common.ApplyTransaction.Entry? entry = null;
            try
            {
                var target = txGuard.ResolveSafePath(proposal.FilePath);
                var dest = string.IsNullOrWhiteSpace(proposal.DestinationPath)
                    ? null : txGuard.ResolveSafePath(proposal.DestinationPath!);
                entry = tx.StageExternal(target, proposal.ChangeType.ToString().ToLowerInvariant(), dest);
            }
            catch (Exception stageError)
            {
                // Staging could not even record intent — refuse the batch before mutating anything.
                queen.Memory.LogEvent(missionId, "autonomy_autoapply_apply_failed",
                    $"Auto-apply could not stage {proposal.FilePath}: {stageError.Message}", taskId, "director",
                    metadata: new() { ["patch_id"] = patchId, ["error"] = stageError.Message });
                RollBackBatch(queen, tx, applied, missionId, taskId,
                    $"{proposal.FilePath} could not be staged");
                return;
            }

            var outcome = queen.ApplyPatchForAutomation(patchId, missionId, taskId);
            if (outcome.Success)
            {
                tx.MarkApplied(entry, outcome.AppliedHash);
                applied.Add(outcome);
                continue;
            }

            /* A write failed AFTER preflight passed — a race, a permission change, a full disk.
             * The transaction rolls the whole batch back, INCLUDING whatever the failed operation
             * left behind: its entry is journaled, its backup exists, and the hash rule decides
             * per file whether the pre-apply bytes can safely go back. */
            queen.Memory.LogEvent(missionId, "autonomy_autoapply_apply_failed",
                $"Auto-apply could not write patch {outcome.FilePath}: {outcome.Error}", taskId, "director",
                metadata: new() { ["patch_id"] = patchId, ["error"] = outcome.Error,
                                  ["backup_path"] = outcome.BackupPath });
            RollBackBatch(queen, tx, applied, missionId, taskId,
                $"{outcome.FilePath} could not be written");
            return;
        }
        if (applied.Count == 0) { tx.Commit(); return; }

        // v1.8.21 fix: on a deployment with no build toolchain, the default `dotnet build && dotnet test`
        // verify always fails and every applied patch is rolled back — so auto-apply never persists. When
        // the operator has explicitly opted in (autonomy_autoapply_keep_without_verify) AND set no verify
        // command, keep the applied patches instead of running (and failing) the built-in verify.
        if (!verifyCmdConfigured && AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify)
        {
            // v2.26.0 pre-V3 hardening: this is a BREAK-GLASS development option, and using it is
            // recorded as such — a critical event, an explicit "this installation is unqualified"
            // statement, and NO verified-success anywhere: the kept change records no deterministic
            // evidence, so it can never promote a skill or reinforce learning (Promotable
            // intrinsically requires deterministic evidence), and the readiness gate reports the
            // installation NOT QUALIFIED while the flag is on.
            queen.Memory.LogEvent(missionId, "autonomy_autoapply_break_glass",
                "CRITICAL: patches kept WITHOUT verification via the keep_without_verify break-glass. "
                + "This installation is NOT V3-qualifiable while this option is enabled; the kept "
                + "change records no verified success and reinforces no learning.",
                antName: "director",
                metadata: new() { ["mission_id"] = missionId, ["severity"] = "critical", ["kept_count"] = applied.Count });
            tx.Commit();
            KeepApplied(queen, missionId, applied,
                "autonomy_autoapply_kept_unverified",
                $"Kept {applied.Count} auto-applied patch(es) WITHOUT verification " +
                "(autonomy_autoapply_keep_without_verify=true; no verify command configured).");
            return;
        }

        // Verify: the change must still build + test green, or every applied patch is reverted.
        var verify = RunVerify();
        if (verify.Green)
        {
            tx.Commit();
            KeepApplied(queen, missionId, applied,
                "autonomy_autoapply_verified",
                $"Verify passed — kept {applied.Count} auto-applied patch(es).",
                new() { ["verify_exit"] = verify.ExitCode, ["verify_seconds"] = verify.Seconds });
        }
        else
        {
            var reason = verify.TimedOut ? "verify timed out" : $"verify failed (exit {verify.ExitCode})";
            var report = RollBackBatch(queen, tx, applied, missionId, null, reason);
            queen.Memory.LogEvent(missionId, "autonomy_autoapply_reverted",
                $"Verify FAILED ({reason}) — rolled back {report.Restored} of {applied.Count} auto-applied patch(es)"
                + (report.Clean ? ". " : " — ROLLBACK INCOMPLETE, auto-apply is now halted. ") +
                $"Verify ran in {workspace} with: {verifyDescription}. " +
                "If this deployment has no build toolchain, set autonomy_autoapply_verify_cmd to a check it can run, " +
                "or autonomy_autoapply_keep_without_verify=true to keep changes without verifying.", antName: "director",
                metadata: new()
                {
                    ["mission_id"] = missionId, ["reverted_count"] = applied.Count, ["verify_exit"] = verify.ExitCode,
                    ["timed_out"] = verify.TimedOut, ["verify_cmd"] = verifyDescription, ["workspace"] = workspace,
                    ["verify_tail"] = Tail(verify.Output, 1500),
                });
        }
    }

    /// <summary>
    /// Compute every proposal against the tree WITHOUT writing. v0.3.8.57.
    ///
    /// Returns one line per refusal; empty means the whole set can be applied. Reuses
    /// <see cref="PatchApply.Compute"/> — the same function the applier runs — so a preflight that
    /// passes and an apply that then refuses would mean the two disagree, which is exactly the drift
    /// a second hand-written checker would introduce.
    ///
    /// <c>requireBaseHash: true</c> matches the live applier: this batch writes to the operator's
    /// real tree, so a destructive proposal that cannot say what it was built against is refused
    /// here rather than discovered halfway through the set.
    /// </summary>
    /// <summary>
    /// The evidence gate for LIVE auto-apply, and it FAILS CLOSED. v0.3.8.61 (PLAN.md §1b S3).
    ///
    /// Three of this method's former mercies were the P0. An unreadable store returned zero
    /// refusals — a database failure widening into permission to write the operator's tree. A
    /// mission with NO revision-identified evidence sailed through, on the reasoning that old rows
    /// simply predate identity; true of history, and irrelevant to a LIVE write happening now. And
    /// a proposal with no patch-set id was invisible to the whole loop, so the one thing that made
    /// a set checkable also made it optional.
    ///
    /// The rule now: live auto-apply requires a patch-set identity on every proposal; at least one
    /// revision-identified evidence row for each set; evidence that is deterministic AND passing for
    /// the exact revision and tree (<see cref="MissionVerification.EvidenceJudgesRevision"/>); and a
    /// patch-set CONTENT hash that matches what is about to be written — the evidence judged bytes,
    /// so the gate compares bytes, and a set that was filtered down to a subset on the way here
    /// no longer matches the hash and is refused, which is the "applies as a unit" rule enforcing
    /// itself. Legacy unidentified evidence stays readable for history and the manual apply path;
    /// what it can no longer do is authorise an unattended write.
    ///
    /// v0.3.8.94, its relationship to <c>PatchPromotionGate</c>: the gate (consulted above, per
    /// proposal, as Automation) is the FLOOR — its evidence-identity condition was written to match
    /// this method's, deliberately. This method stays because three of its rules are about the SET
    /// AS A UNIT and a per-proposal gate cannot answer them: every proposal must carry a patch-set
    /// identity; a deterministic FAILURE beside a pass refuses (mixed rows need a human — the gate
    /// requires a pass, this additionally forbids a standing objection); and the content hash binds
    /// the exact bytes about to be written to the exact bytes the evidence judged. Deleting this in
    /// favour of the gate would silently widen live auto-apply on all three.
    /// </summary>
    internal static List<string> RefuseEvidenceAboutAnotherRevision(
        Anthill.SDK.Artifacts.IEvidenceStore store, string missionId,
        List<(string PatchId, string? PatchSetId, string? TaskId, PatchProposal Proposal)> eligible)
    {
        var refusals = new List<string>();

        // A proposal that cannot name its set cannot have its evidence checked, and "cannot be
        // checked" must read as "no" at a gate that writes to the live tree.
        foreach (var (patchId, patchSetId, _, proposal) in eligible)
            if (string.IsNullOrWhiteSpace(patchSetId))
                refusals.Add($"patch {patchId} ({proposal.FilePath}): no patch_set_id — live "
                           + "auto-apply requires a patch-set identity so its evidence can be checked");

        List<Anthill.SDK.Artifacts.Evidence> evidence;
        try { evidence = store.ForMission(missionId).ToList(); }
        catch (Exception error)
        {
            // The store failing is not a verdict about the patches — and that is precisely why it
            // refuses. Without evidence this gate knows NOTHING, and a gate that knows nothing and
            // lets a live write proceed is not a gate. The refusal names the outage so the operator
            // fixes the store rather than the mission; nothing here freezes the work forever,
            // because the next auto-apply cycle re-reads.
            Console.Error.WriteLine($"[autoapply] could not read evidence for {missionId}: {error.Message}");
            refusals.Add($"the evidence store could not be read ({error.Message}) — live auto-apply "
                       + "refuses to write without evidence; manual apply remains available");
            return refusals;
        }

        var identified = evidence.Where(e => e.IdentifiesARevision).ToList();
        if (identified.Count == 0)
        {
            refusals.Add("the mission holds no revision-identified evidence — live auto-apply "
                       + "requires evidence that names the revision, content and tree it judged; "
                       + "legacy evidence remains readable and the set remains manually applicable");
            return refusals;
        }

        foreach (var group in eligible.Where(e => !string.IsNullOrWhiteSpace(e.PatchSetId))
                     .GroupBy(e => e.PatchSetId!, StringComparer.Ordinal))
        {
            var patchSetId = group.Key;
            // The identity ExecutionService stamps when it verifies a materialized set.
            var revisionId = $"rev:{patchSetId}";
            var forThisSet = identified.Where(e =>
                string.Equals(e.RevisionId, revisionId, StringComparison.Ordinal)).ToList();

            if (forThisSet.Count == 0)
            {
                refusals.Add($"patch set {patchSetId}: the mission holds {identified.Count} "
                           + "revision-identified evidence row(s) and none of them judge this set");
                continue;
            }

            // Present is not passing, and not every kind promotes. MissionVerification owns that rule
            // so the two cannot disagree about what counts as evidence to act on.
            var treeHash = forThisSet[0].TreeHash;
            if (!MissionVerification.EvidenceJudgesRevision(forThisSet, revisionId, treeHash))
            {
                refusals.Add($"patch set {patchSetId}: its evidence exists but none of it is "
                           + "deterministic AND passing for the tree it produced");
                continue;
            }

            // At least one deterministic pass is necessary and NOT sufficient: a deterministic
            // FAILURE for this same revision is a machine saying no, and a green run beside it does
            // not answer the objection — a build that passed after a test that failed is exactly
            // the state that needs a human. Mixed rows refuse.
            var deterministicFailures = forThisSet.Count(e => e.Deterministic && !e.Passed);
            if (deterministicFailures > 0)
            {
                refusals.Add($"patch set {patchSetId}: {deterministicFailures} deterministic "
                           + "check(s) FAILED for this revision — a deterministic failure cannot be "
                           + "outvoted by a pass; live auto-apply requires no standing objection");
                continue;
            }

            // The CONTENT check. Revision id and tree hash say which verification run this was;
            // the patch-set hash says which BYTES it judged. The same function the materializer
            // used computes the hash of what this runner is about to write, so evidence for a
            // set that has since been altered — or arrived here as a policy-filtered subset of
            // itself — no longer authorises the write.
            var aboutToApply = new PatchSet
            {
                Id = patchSetId,
                MissionId = missionId,
                Proposals = group.Select(e => e.Proposal).ToList(),
            };
            var contentHash = Anthill.Core.Verification.PatchSetMaterializer.HashPatchSet(aboutToApply);
            if (!forThisSet.Any(e => string.Equals(e.PatchSetHash, contentHash, StringComparison.Ordinal)))
                refusals.Add($"patch set {patchSetId}: the content about to be applied does not match "
                           + "the content the evidence judged (hash mismatch — the set was altered, "
                           + "or only part of it is eligible, and evidence judges the whole set)");
        }

        return refusals;
    }

    /// <summary>
    /// Roll the whole batch back through the transaction and TELL THE TRUTH about the result.
    /// v0.3.8.62 (S4): the predecessor iterated per-patch rollbacks, ignored every return value,
    /// and logged the batch as rolled back regardless — so an operator reading the log saw a clean
    /// revert over a tree that still held half a patch set. The transaction's hash-checked report
    /// is authoritative: conflicts (files changed after apply — left alone) and failures are
    /// logged as what they are, and an unclean report has already written the durable
    /// ROLLBACK_FAILED marker that halts the next run.
    /// </summary>
    private static Anthill.SDK.Common.ApplyTransaction.RollbackReport RollBackBatch(
        Queen queen, Anthill.SDK.Common.ApplyTransaction tx,
        List<Queen.AutoApplyOutcome> applied, string missionId, string? taskId, string reason)
    {
        var report = tx.Rollback();

        foreach (var outcome in applied)
            queen.Memory.UpdatePatchStatus(outcome.PatchId, PatchStatus.Failed,
                lastError: $"Auto-apply rolled back: {reason}");

        if (report.Clean)
        {
            queen.Memory.LogEvent(missionId, "autonomy_autoapply_batch_rolled_back",
                $"Rolled back {report.Restored} file change(s) because {reason}. "
                + "The set is applied as a unit or not at all.", taskId, "director",
                metadata: new() { ["mission_id"] = missionId, ["reverted_count"] = report.Restored,
                                  ["transaction"] = tx.Id, ["reason"] = reason });
        }
        else
        {
            queen.Memory.LogEvent(missionId, "autonomy_autoapply_rollback_incomplete",
                $"CRITICAL: rollback INCOMPLETE after {reason} — restored {report.Restored}, "
                + $"conflicts: {report.Conflicts.Count}, failures: {report.Failures.Count}. "
                + "A durable ROLLBACK_FAILED marker now halts auto-apply until an operator resolves it.",
                taskId, "director",
                metadata: new()
                {
                    ["mission_id"] = missionId, ["severity"] = "critical", ["transaction"] = tx.Id,
                    ["restored"] = report.Restored,
                    ["conflicts"] = string.Join(" | ", report.Conflicts),
                    ["failures"] = string.Join(" | ", report.Failures),
                });
        }
        return report;
    }

    private static List<string> Preflight(
        List<(string PatchId, string? PatchSetId, string? TaskId, PatchProposal Proposal)> eligible) =>
        // v0.3.8.91: this body MOVED to `Anthill.Core.Verification.PatchSetApply.Preflight`, and this
        // is now the one line that remains. It was a second implementation of a rule — "compute every
        // proposal against the tree before writing any of them" — that the ordinary apply path did
        // not have at all, which is how a set could be applied file by file on one lane while the
        // other refused the whole batch. Two implementations of one rule is a named defect class
        // here; one of them living in Anthill.Api meant Core could not reach it even to agree.
        Anthill.Core.Verification.PatchSetApply.Preflight(
            eligible.Select(e => (e.PatchId, e.Proposal)));

    /// <summary>
    /// Finalize a set of kept (not rolled-back) auto-applied patches: consume the human approvals that
    /// would otherwise sit in the queue, optionally git-commit locally, and log the outcome. Shared by
    /// the verify-green path and the keep-without-verify path (v1.8.21).
    /// </summary>
    private static void KeepApplied(Queen queen, string missionId, List<Queen.AutoApplyOutcome> applied,
        string eventType, string message, Dictionary<string, object?>? extra = null)
    {
        foreach (var a in applied)
        {
            var approval = queen.Memory.ApprovalForTarget(a.PatchId);
            if (approval is not null)
                queen.Memory.UpdateApprovalStatus(approval.Id,
                    ApprovalStatus.Consumed, "Auto-applied by the Director and kept.");
        }
        var committed = false;
        var gitNote = "";
        if (AnthillRuntime.AutonomyAutoApplyGitCommit)
        {
            committed = GitCommit(applied, out gitNote);
            if (!committed)
                queen.Memory.LogEvent(missionId, "autonomy_autoapply_git_failed",
                    $"Kept the applied patch(es) on disk but the local git commit failed: {gitNote}", antName: "director",
                    metadata: new() { ["mission_id"] = missionId, ["note"] = gitNote });
            else
                // Success path now emits its own event so the Event Log reflects the git step — previously
                // only the failure path logged, so a successful commit/push was invisible in the UI.
                queen.Memory.LogEvent(missionId, "autonomy_autoapply_committed",
                    $"Auto-applied patch(es) committed to the standalone branch — {gitNote}.", antName: "director",
                    metadata: new()
                    {
                        ["mission_id"] = missionId, ["note"] = gitNote,
                        ["pushed"] = AnthillRuntime.AutonomyAutoApplyGitPush && gitNote.Contains("pushed to"),
                        ["branch"] = AnthillRuntime.AutonomyAutoApplyGitBranch,
                        ["files"] = applied.Select(a => a.FilePath).ToList(),
                    });
        }
        var meta = new Dictionary<string, object?>
        {
            ["mission_id"] = missionId, ["kept_count"] = applied.Count, ["git_commit_enabled"] = AnthillRuntime.AutonomyAutoApplyGitCommit,
            ["git_committed"] = committed, ["files"] = applied.Select(a => a.FilePath).ToList(),
        };
        foreach (var kv in extra ?? new()) meta[kv.Key] = kv.Value;
        queen.Memory.LogEvent(missionId, eventType, message, antName: "director", metadata: meta);
    }

    /// <summary>Probes whether the workspace root accepts writes (a temp file create+delete). Cheap; runs only when eligible patches exist.</summary>
    private static bool WorkspaceWritable(out string reason)
    {
        var root = AnthillRuntime.AllowedWorkspaceRoot;
        try
        {
            if (!Directory.Exists(root)) { reason = "directory does not exist"; return false; }
            var probe = Path.Combine(root, $".autoapply_probe_{Guid.NewGuid():N}");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            reason = "";
            return true;
        }
        catch (Exception e) { reason = e.GetType().Name; return false; }
    }

    internal sealed record VerifyResult(bool Green, int ExitCode, bool TimedOut, double Seconds, string Output);

    /// <summary>Runs the verify step in the workspace root: the operator command, or built-in dotnet build+test.
    /// Shared with the operator-triggered patch verification (v1.8.24, <see cref="PatchVerifyRunner"/>).</summary>
    internal static VerifyResult RunVerify(string? workdir = null)
    {
        var cmd = string.IsNullOrWhiteSpace(AnthillRuntime.AutonomyAutoApplyVerifyCmd)
            ? "dotnet build && dotnet test"
            : AnthillRuntime.AutonomyAutoApplyVerifyCmd;
        // v2.10.1: an explicit workdir lets verification run inside a disposable sandbox instead
        // of the live checkout; the default remains the legacy live-root behavior.
        var dir = workdir is not null && Directory.Exists(workdir) ? Path.GetFullPath(workdir)
            : Directory.Exists(AnthillRuntime.AllowedWorkspaceRoot)
                ? Path.GetFullPath(AnthillRuntime.AllowedWorkspaceRoot) : Environment.CurrentDirectory;
        var (exit, output, timedOut, seconds) = RunShell(cmd, dir, AnthillRuntime.AutonomyAutoApplyVerifyTimeout);
        return new VerifyResult(!timedOut && exit == 0, exit, timedOut, seconds, output);
    }

    /// <summary>
    /// Commits the applied files on the standalone auto-apply branch and (optionally) pushes it to the
    /// remote via the configured SSH deploy key. NEVER touches main: it refuses to run on
    /// main/master, only ever commits/pushes the "&lt;username&gt;-anthill" branch, never merges the
    /// branch into main, and never force-pushes. On any git error it leaves the change on disk and
    /// returns false (fail-closed). The SSH key is referenced by PATH via GIT_SSH_COMMAND — no key
    /// material is read or logged. Sync direction is one-way: origin/main is merged INTO the branch.
    /// </summary>
    private static bool GitCommit(List<Queen.AutoApplyOutcome> applied, out string note)
    {
        note = "";
        var dir = Directory.Exists(AnthillRuntime.AllowedWorkspaceRoot)
            ? Path.GetFullPath(AnthillRuntime.AllowedWorkspaceRoot) : Environment.CurrentDirectory;
        // v0.3.8.52: a rename touches TWO paths, and staging only the source would commit the
        // deletion without the arrival. `git add` on a vanished path stages its removal, so the
        // delete change type needs nothing extra here.
        var files = string.Join(" ", applied
            .SelectMany(a => new[] { a.ResolvedPath ?? a.FilePath, a.ResolvedDestination })
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => "\"" + p!.Replace("\"", "") + "\""));
        var msg = $"ANTHILL auto-applied {applied.Count} verified patch(es) [autonomy]";
        var branch = AnthillRuntime.AutonomyAutoApplyGitBranch; // "<username>-anthill" or ""

        var (curExit, curOut, _, _) = RunShell("git rev-parse --abbrev-ref HEAD", dir, 20);
        if (curExit != 0) { note = "not a git working tree: " + Tail(curOut, 200); return false; }
        var current = curOut.Trim();

        // Hard safety: never commit auto-applied changes onto main/master.
        if (current is "main" or "master")
        {
            note = branch.Length == 0
                ? "workspace is on 'main'; set a git username in Auto-Apply settings so commits land on <username>-anthill, never main."
                : $"workspace is on 'main'; check the clone out on '{branch}' first (git checkout {branch}) — ANTHILL never commits to main.";
            return false;
        }
        // If a standalone branch is configured, require the workspace to already be on it — never
        // switch branches with a dirty working tree on the operator's live clone.
        if (branch.Length > 0 && !string.Equals(current, branch, StringComparison.Ordinal))
        {
            note = $"workspace is on '{current}', not the configured branch '{branch}'. Check it out there (git checkout {branch}) so auto-apply commits land on the standalone branch.";
            return false;
        }

        // Set the author/committer identity inline (-c) so a commit never fails with "Please tell me
        // who you are" on a host where the service user has no global git identity configured.
        var (exit, output, timedOut, _) = RunShell(
            $"git add {files} && git -c user.name=\"ANTHILL Auto-Apply\" -c user.email=\"anthill@localhost\" commit -m \"{msg}\"", dir, 60);
        if (timedOut || exit != 0) { note = "commit failed: " + Tail(output, 250); return false; }

        // Capture the commit sha so the success event names the exact commit an operator can inspect.
        var (shExit, shOut, _, _) = RunShell("git rev-parse --short HEAD", dir, 20);
        var sha = shExit == 0 ? shOut.Trim() : "(unknown)";
        var pushMsg = AnthillRuntime.AutonomyAutoApplyGitPush ? "" : "push disabled";
        var warn = "";

        // Optional push (+ one-way sync of origin/main into the branch) via the SSH deploy key.
        // Best-effort: a sync/push failure never undoes the local commit.
        if (AnthillRuntime.AutonomyAutoApplyGitPush && branch.Length > 0)
        {
            var remote = AnthillRuntime.AutonomyAutoApplyGitRemote;
            var key = AnthillRuntime.AutonomyAutoApplyGitSshKeyPath;
            // UserKnownHostsFile=/tmp/... : ssh records the remote host key on first connect. Under the
            // systemd sandbox (ProtectSystem=strict) the service user's ~/.ssh is read-only, so writing
            // known_hosts there fails; /tmp is writable (PrivateTmp) and per-service, so the push works
            // without needing .ssh in ReadWritePaths.
            var env = key.Length > 0
                ? $"GIT_SSH_COMMAND='ssh -i \"{key.Replace("\"", "")}\" -o IdentitiesOnly=yes -o StrictHostKeyChecking=accept-new -o UserKnownHostsFile=/tmp/anthill_known_hosts' "
                : "";
            var (fx, fo, _, _) = RunShell($"{env}git fetch {remote} && git merge {remote}/main --no-edit", dir, 120);
            if (fx != 0) { RunShell("git merge --abort", dir, 20); warn = "sync with main skipped: " + Tail(fo, 150); }
            // Push ONLY the standalone branch (never main); no force.
            var (px, po, _, _) = RunShell($"{env}git push {remote} {branch}", dir, 120);
            pushMsg = px == 0 ? $"pushed to {remote}/{branch}" : "push failed: " + Tail(po, 200);
        }
        note = $"committed {sha} on {current}"
             + (pushMsg.Length > 0 ? $"; {pushMsg}" : "")
             + (warn.Length > 0 ? $" ({warn})" : "");
        return true;
    }

    private static (int Exit, string Output, bool TimedOut, double Seconds) RunShell(string command, string dir, int timeoutSeconds)
    {
        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir,
            CreateNoWindow = true,   // v0.3.8.53: never flash a console from the desktop shell
            StandardOutputEncoding = Encoding.UTF8,   // v0.3.8.55: children emit UTF-8, not the OS codepage
            StandardErrorEncoding = Encoding.UTF8,
        };
        /*
         * WINDOWS TAKES THE RAW STRING; UNIX TAKES THE LIST. v0.3.8.79.
         *
         * THE DEFECT. Both arms used `ArgumentList`, which .NET escapes by C-RUNTIME rules: an
         * argument containing a quote is emitted wrapped, with inner `"` written as `\"`. That is
         * correct for a program using the C runtime to parse its command line. `cmd.exe` does not
         * — it has its own quoting rules and treats `\"` as a literal backslash followed by a
         * quote. So a verify command written as
         *
         *     findstr /C:"aria-label" static\app.js
         *
         * reached findstr as `/C:\"aria-label\"`, matched nothing, and exited 1. Auto-apply then
         * rolled back a correctly applied patch and reported "Verify FAILED" — against a tree where
         * the change was present and correct. Every quoted verify command in the field is affected,
         * and the failure is the worst shape available: the colony reports that verification said
         * no, so an operator debugs their change rather than their configuration.
         *
         * It survived because the only verify command any test used was scenario 3's
         * `type docs\COLONY-NOTE.md`, which has no quotes. The second instance is the auto-commit
         * below, which passes `user.name="ANTHILL Auto-Apply"` and `-m "{msg}"` and which no test
         * exercised at all.
         *
         * `psi.Arguments` hands Windows the string verbatim, so cmd applies its OWN rules to a
         * command an operator wrote for cmd. Unix keeps `ArgumentList`: there is no command-line
         * re-parsing on that side — the list becomes `argv` directly — so `sh -c <command>` already
         * received the command intact, and switching it to a string would introduce the very
         * re-quoting this removes.
         */
        if (isWindows) psi.Arguments = "/c " + command;
        else { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(command); }

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)!;
        var output = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit(timeoutSeconds * 1000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            proc.WaitForExit(3000);
            sw.Stop();
            lock (output) return (-1, output.ToString(), true, Math.Round(sw.Elapsed.TotalSeconds, 1));
        }
        proc.WaitForExit();
        sw.Stop();
        lock (output) return (proc.ExitCode, output.ToString(), false, Math.Round(sw.Elapsed.TotalSeconds, 1));
    }

    internal static string Tail(string s, int max) =>
        s.Length <= max ? s.TrimEnd() : "…" + s[^max..].TrimEnd();
}
