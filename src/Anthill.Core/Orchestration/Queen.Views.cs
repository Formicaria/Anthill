using System.Text.Json;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Security;

namespace Anthill.Core.Orchestration;

/// <summary>
/// Queen approval/patch-application flow plus the formatter surface that the CLI and the
/// secured API render. These read from <see cref="SqliteMemory"/> and stay metadata-first
/// by default; full result summaries are only included where a permission explicitly allows.
/// </summary>
public sealed partial class Queen
{
    private const string Divider = "\n--------------------------------------------------\n";

    private static string Str(Dictionary<string, object?> row, string key, string fallback = "") =>
        row.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? fallback : fallback;

    // ---- approvals --------------------------------------------------------

    public string ApproveRequest(string approvalId)
    {
        try { approvalId = Validation.ValidateApprovalId(approvalId); }
        catch (Exception e) { return $"Invalid approval id: {e.Message}"; }
        var approval = Memory.GetApprovalRequest(approvalId);
        if (approval is null) return $"No approval request found with id: {approvalId}";
        if (Str(approval, "status") != ApprovalStatus.Pending.Value())
            return $"Approval request is not pending.\nID: {approvalId}\nCurrent Status: {Str(approval, "status")}";
        var updated = Memory.UpdateApprovalStatus(approvalId, ApprovalStatus.Approved,
            "Approved by user. Patch can only be applied through /apply if write gates are enabled.");
        if (updated is not null)
        {
            // Flip the patch itself to "approved" for a patch_proposal approval, mirroring the reject
            // path. Without this the patch stays "proposed" after approval and the Patch Center's
            // Apply action (gated on patch status "approved") never appears — approved patches were
            // un-appliable through the UI.
            if (Str(updated, "action_type") == ApprovalActionType.PatchProposal.Value())
                Memory.UpdatePatchStatus(Str(updated, "target_id"), PatchStatus.Approved);
            Memory.LogEvent(Str(updated, "mission_id"), "approval_request_approved", $"Approval request approved: {approvalId}",
                Str(updated, "task_id"), "queen",
                new() { ["approval_request_id"] = approvalId, ["action_type"] = Str(updated, "action_type"), ["target_id"] = Str(updated, "target_id"), ["patch_application_enabled"] = AnthillRuntime.EnablePatchApplication, ["file_writing_enabled"] = AnthillRuntime.EnableFileWriting });
        }
        return $"Approval recorded.\nID: {approvalId}\nStatus: approved\n\nNext step: inspect the patch with /patch {Str(updated!, "target_id")}.\n" +
               $"To apply later: /apply {approvalId}\n\nPatch application requires both write gates enabled.";
    }

    public string RejectRequest(string approvalId, string? reason = null)
    {
        try { approvalId = Validation.ValidateApprovalId(approvalId); }
        catch (Exception e) { return $"Invalid approval id: {e.Message}"; }
        var approval = Memory.GetApprovalRequest(approvalId);
        if (approval is null) return $"No approval request found with id: {approvalId}";
        if (Str(approval, "status") != ApprovalStatus.Pending.Value())
            return $"Approval request is not pending.\nID: {approvalId}\nCurrent Status: {Str(approval, "status")}";
        var note = reason ?? "Rejected by user.";
        var updated = Memory.UpdateApprovalStatus(approvalId, ApprovalStatus.Rejected, note);
        if (updated is not null)
        {
            if (Str(updated, "action_type") == ApprovalActionType.PatchProposal.Value())
                Memory.UpdatePatchStatus(Str(updated, "target_id"), PatchStatus.Rejected, lastError: note);
            Memory.LogEvent(Str(updated, "mission_id"), "approval_request_rejected", $"Approval request rejected: {approvalId}",
                Str(updated, "task_id"), "queen",
                new() { ["approval_request_id"] = approvalId, ["action_type"] = Str(updated, "action_type"), ["target_id"] = Str(updated, "target_id"), ["reason"] = note });
        }
        return $"Approval request rejected.\nID: {approvalId}\nStatus: rejected\nReason: {note}";
    }

    /// <summary>
    /// String-returning face, kept for the CLI and the endpoints that render text. v0.3.8.91: it is
    /// now a FORMATTER over <see cref="ApplyApprovedPatchTyped"/> and carries no decision of its own.
    /// Nothing may branch on what it returns; the typed twin exists for that.
    /// </summary>
    public string ApplyApprovedPatch(string approvalId) => ApplyApprovedPatchTyped(approvalId).Message;

    /// <summary>
    /// Apply an approved patch, and say in a TYPE what happened. v0.3.8.91.
    ///
    /// Every refusal below used to be distinguishable only by reading its sentence, and three of
    /// those sentences contain the word "applied" — see <see cref="PatchApplyResult"/> for the
    /// rejected-patch-reports-success defect that came out of it.
    /// </summary>
    public PatchApplyResult ApplyApprovedPatchTyped(string approvalId)
    {
        try { approvalId = Validation.ValidateApprovalId(approvalId); }
        catch (Exception e) { return new(PatchApplyOutcome.RefusedUnknown, $"Invalid approval id: {e.Message}"); }
        var approval = Memory.GetApprovalRequest(approvalId);
        if (approval is null) return new(PatchApplyOutcome.RefusedUnknown, $"No approval request found with id: {approvalId}");
        if (Str(approval, "status") != ApprovalStatus.Approved.Value())
            return new(PatchApplyOutcome.RefusedNotApproved,
                $"Cannot apply patch. Approval request is not approved.\nID: {approvalId}\nCurrent Status: {Str(approval, "status")}");
        if (Str(approval, "action_type") != ApprovalActionType.PatchProposal.Value())
            return new(PatchApplyOutcome.RefusedUnknown,
                $"Cannot apply approval type: {Str(approval, "action_type")}\nOnly patch_proposal approvals can be applied.");
        var patchId = Str(approval, "target_id");
        var patch = Memory.GetPatchProposal(patchId);
        if (patch is null) return new(PatchApplyOutcome.RefusedUnknown, $"No patch proposal found for approval target id: {patchId}");
        if (Str(patch, "status") == PatchStatus.Applied.Value())
            return new(PatchApplyOutcome.RefusedStatus, $"Patch is already applied.\nPatch ID: {patchId}");
        if (Str(patch, "status") is var ps && (ps == PatchStatus.Rejected.Value() || ps == PatchStatus.Failed.Value()))
            return new(PatchApplyOutcome.RefusedStatus, $"Patch cannot be applied because status is {ps}.\nPatch ID: {patchId}");

        // THE PROMOTION GATE. v0.3.8.91.
        //
        // Everything above this line is what the Apply button used to check on its own: an approval
        // row, its status, its type, the patch's own status. Five facts, none of them about whether
        // anything had VERIFIED the change. Meanwhile the auto-apply lane checked nine things — so
        // the strictest path was the one with no human on it and the loosest was the one a person
        // clicks. One capability, five different answers to "may this happen".
        //
        // The gate is now the authority for all of them and this call is what makes that true for
        // the manual path. The checks above are left in place deliberately: they produce the
        // specific, long-standing operator messages, and a duplicated check that AGREES with the
        // authority is harmless where a second, different answer would not be.
        var verdict = Anthill.Core.Verification.PatchPromotionGate.Evaluate(
            Memory, (Anthill.SDK.Artifacts.IEvidenceStore)Memory, patchId,
            Anthill.Core.Verification.PromotionActor.Human);

        if (!verdict.Promotable)
        {
            Memory.LogEvent(Str(approval, "mission_id"), "patch_promotion_refused",
                $"Promotion refused at {verdict.Layer}: {verdict.Reason}",
                Str(approval, "task_id"), "queen",
                new()
                {
                    ["patch_id"] = patchId, ["approval_request_id"] = approvalId,
                    ["refusal"] = verdict.Refusal.ToString(), ["layer"] = verdict.Layer,
                });

            return new(PatchApplyOutcome.RefusedStatus,
                $"Patch cannot be applied.\nPatch ID: {patchId}\nRefused by: {verdict.Layer}\n{verdict.Reason}");
        }

        var result = Tools.RunTool("apply_patch", Str(approval, "mission_id"), Str(approval, "task_id"), "queen",
            new() { ["patch"] = patch });
        if (!result.Success)
        {
            Memory.UpdatePatchStatus(patchId, PatchStatus.Failed, lastError: result.Error);
            Memory.LogEvent(Str(approval, "mission_id"), "patch_apply_failed", $"Patch application failed: {patchId}",
                Str(approval, "task_id"), "queen", new() { ["approval_request_id"] = approvalId, ["patch_id"] = patchId, ["error"] = result.Error });
            return new(PatchApplyOutcome.Failed,
                $"Patch application failed.\nApproval ID: {approvalId}\nPatch ID: {patchId}\nError: {result.Error}");
        }
        string? backupPath = null;
        try { backupPath = JsonDocument.Parse(string.IsNullOrEmpty(result.Output) ? "{}" : result.Output).RootElement.TryGetProperty("backup_path", out var bp) ? bp.GetString() : null; }
        catch { /* tolerate */ }
        Memory.UpdatePatchStatus(patchId, PatchStatus.Applied, AnthillTime.NowUtc().ToIso(), backupPath, null);
        Memory.UpdateApprovalStatus(approvalId, ApprovalStatus.Consumed, "Approval consumed by successful patch application.");
        Memory.LogEvent(Str(approval, "mission_id"), "patch_applied", $"Patch applied successfully: {patchId}",
            Str(approval, "task_id"), "queen",
            new() { ["approval_request_id"] = approvalId, ["patch_id"] = patchId, ["file_path"] = Str(patch, "file_path"), ["change_type"] = Str(patch, "change_type"), ["backup_path"] = backupPath });
        Memory.UpdatePheromoneTrail("capability:controlled_file_writing", "capability", true, 0.03,
            new() { ["approval_request_id"] = approvalId, ["patch_id"] = patchId, ["file_path"] = Str(patch, "file_path") });

        return new(PatchApplyOutcome.Applied,
            $"Patch applied successfully.\nApproval ID: {approvalId}\nPatch ID: {patchId}\nFile: {Str(patch, "file_path")}\nBackup: {backupPath ?? "n/a"}\nApproval Status: consumed\nPatch Status: applied");
    }

    /// <summary>
    /// Structured outcome of an automated patch apply, carrying what rollback needs.
    ///
    /// <paramref name="ResolvedDestination"/> (v0.3.8.52) is where a RENAME put the file, and is null
    /// for every other change type. Rollback needs it as much as it needs the backup: restoring the
    /// backup alone would recreate the source and leave the moved copy in place, turning a failed
    /// rename into a duplicated file rather than an undone one.
    /// </summary>
    public sealed record AutoApplyOutcome(bool Success, string PatchId, string? Error,
        string? ResolvedPath, string? BackupPath, string ChangeType, string FilePath,
        string? ResolvedDestination = null,
        // v0.3.8.62 (S4): what the apply left at the live path. Rollback restores ONLY while the
        // current bytes still match this — anything else is newer work, not rollback's to destroy.
        string? AppliedHash = null);

    /// <summary>
    /// Phase 5: applies a patch directly for the auto-apply runner (no separate approval step) and
    /// returns the resolved path + backup path so a failed verify can be rolled back. Honors the
    /// same write gates and path guard as the human <see cref="ApplyApprovedPatch"/> path — the
    /// only difference is the actor and that no human approval row is consumed. Logs an audit event.
    /// </summary>
    public AutoApplyOutcome ApplyPatchForAutomation(string patchId, string missionId, string? taskId)
    {
        var patch = Memory.GetPatchProposal(patchId);
        if (patch is null) return new AutoApplyOutcome(false, patchId, "patch not found", null, null, "", "");
        var changeType = Str(patch, "change_type");
        var filePath = Str(patch, "file_path");

        var result = Tools.RunTool("apply_patch", missionId, taskId, "director",
            new() { ["patch"] = patch });

        // v0.3.8.62 (S4): parse metadata from SUCCESS AND FAILURE alike. The tool now reports the
        // backup it took and the path it touched even when the mutation threw — a failed write
        // whose backup path dissolved into an exception message was S4's unrecoverable case.
        string? backupPath = null, resolvedPath = null, resolvedDestination = null, appliedHash = null;
        try
        {
            var root = JsonDocument.Parse(string.IsNullOrEmpty(result.Output) ? "{}" : result.Output).RootElement;
            backupPath = root.TryGetProperty("backup_path", out var bp) ? bp.GetString() : null;
            resolvedPath = root.TryGetProperty("file_path", out var fp) ? fp.GetString() : null;
            // v0.3.8.52: present only on a rename. Absent for everything else, which is why the
            // rollback treats a missing one as "nothing was moved" rather than as an error.
            resolvedDestination = root.TryGetProperty("destination_path", out var dp) ? dp.GetString() : null;
            appliedHash = root.TryGetProperty("applied_hash", out var ah) ? ah.GetString() : null;
        }
        catch { /* tolerate — rollback for a modify still needs the backup, handled by caller */ }

        if (!result.Success)
        {
            Memory.UpdatePatchStatus(patchId, PatchStatus.Failed, lastError: result.Error, backupPath: backupPath);
            return new AutoApplyOutcome(false, patchId, result.Error, resolvedPath, backupPath, changeType, filePath, resolvedDestination);
        }

        Memory.UpdatePatchStatus(patchId, PatchStatus.Applied, AnthillTime.NowUtc().ToIso(), backupPath, null, appliedHash);
        Memory.LogEvent(missionId, "autonomy_autoapply_applied", $"Director auto-applied patch: {filePath}", taskId, "director",
            new() { ["patch_id"] = patchId, ["file_path"] = filePath, ["change_type"] = changeType, ["backup_path"] = backupPath, ["destination_path"] = resolvedDestination, ["applied_hash"] = appliedHash, ["verified"] = false });
        return new AutoApplyOutcome(true, patchId, null, resolvedPath, backupPath, changeType, filePath, resolvedDestination, appliedHash);
    }

    // v0.3.8.62 (S4): `RollbackAutoApplied` is GONE. The auto-apply runner rolls back through
    // ApplyTransaction — journaled, hash-checked, honest about incomplete restores — and a
    // per-patch rollback with no journal and no hash gate surviving beside it would have been the
    // second implementation that drifts. The operator's manual path is RevertAppliedPatch below,
    // which now applies the same hash rule.

    /// <summary>
    /// v2.7.0: manually reverts an APPLIED patch on operator request — for an "add" it deletes the
    /// created file, for a "modify" or a "delete" it restores the pre-apply backup, and for a
    /// "rename" (v0.3.8.52) it moves the file back. Marks the patch Reverted and
    /// logs the action. Mirrors the automation rollback (the ApplyTransaction path in AutoApplyRunner) but is
    /// operator-initiated and keyed by patch id, resolving the on-disk path exactly as the apply tool
    /// did (through the same <see cref="WorkspacePathGuard"/>), so a revert can never escape the
    /// sandboxed workspace. Idempotent-ish: only an "applied" patch can be reverted.
    /// </summary>
    public string RevertAppliedPatch(string patchId)
    {
        var patch = Memory.GetPatchProposal(patchId);
        if (patch is null) return $"No patch proposal found with id: {patchId}";
        var status = Str(patch, "status");
        if (status != PatchStatus.Applied.Value())
            return $"Only an applied patch can be reverted. Patch {patchId} is currently: {status}.";

        var changeType = Str(patch, "change_type");
        var filePath = Str(patch, "file_path");
        var backupPath = Str(patch, "backup_path");
        var destinationPath = Str(patch, "destination_path");
        var missionId = Str(patch, "mission_id");
        var taskId = Str(patch, "task_id");

        var guard = new WorkspacePathGuard();
        string resolved;
        try { resolved = guard.ResolveSafePath(filePath); }
        catch (Exception e) { return $"Cannot revert: path '{filePath}' is unsafe or unresolvable ({e.Message})."; }

        // v0.3.8.62 (S4): the same hash gate the automated rollback applies. An applied patch that
        // recorded its post-apply hash is revertable only while the live file still holds those
        // bytes; a file edited since is newer work, and the honest answer is a refusal that names
        // the situation rather than an overwrite that quietly destroys it. Patches applied before
        // this release carry no hash and keep their old behaviour — recorded in the reply.
        var appliedHash = Str(patch, "applied_hash");

        // The destination is resolved through the SAME guard as the source, for the same reason the
        // method comment gives for resolving the source at all: a revert must not be able to move a
        // file out of the sandboxed workspace any more than an apply can.
        string? resolvedDestination = null;
        if (destinationPath.Length > 0)
        {
            try { resolvedDestination = guard.ResolveSafePath(destinationPath); }
            catch (Exception e) { return $"Cannot revert: destination '{destinationPath}' is unsafe or unresolvable ({e.Message})."; }
        }

        if (appliedHash.Length > 0)
        {
            var livePath = changeType.Equals("rename", StringComparison.OrdinalIgnoreCase) && resolvedDestination is { Length: > 0 }
                ? resolvedDestination : resolved;
            var currentHash = Anthill.SDK.Common.ApplyTransaction.HashFile(livePath);
            if (currentHash != appliedHash)
                return $"Revert refused: {filePath} changed after this patch was applied. "
                     + "The current content is newer work; reverting would destroy it. "
                     + "Restore manually from the backup if that is really what you want: "
                     + (backupPath.Length > 0 ? backupPath : "(no backup on record)");
        }

        bool restored; string detail;
        try
        {
            if (changeType.Equals("add", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(resolved)) { File.Delete(resolved); restored = true; detail = "created file deleted"; }
                else { restored = false; detail = "created file was already absent"; }
            }
            else if (changeType.Equals("rename", StringComparison.OrdinalIgnoreCase))
            {
                // Explicit move-back. The generic backup-restore below would recreate the source and
                // leave the moved file at the destination — the tree would hold both copies, which
                // is not what "reverted" means to the operator who clicked it.
                if (resolvedDestination is { Length: > 0 } dest && File.Exists(dest))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
                    File.Move(dest, resolved, overwrite: true);
                    restored = true; detail = "moved back from the rename destination";
                }
                else if (!string.IsNullOrEmpty(backupPath) && File.Exists(backupPath))
                {
                    File.Copy(backupPath, resolved, overwrite: true);
                    restored = true; detail = "destination was already gone — source restored from pre-apply backup";
                }
                else
                {
                    restored = false; detail = "renamed file is no longer at its destination and no pre-apply backup is on record";
                }
            }
            else if (!string.IsNullOrEmpty(backupPath) && File.Exists(backupPath))
            {
                // modify and delete alike: the pre-apply bytes go back where they were. For a delete
                // the parent directory may have been pruned since, so it is recreated first.
                Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
                File.Copy(backupPath, resolved, overwrite: true); restored = true; detail = "restored from pre-apply backup";
            }
            else
            {
                restored = false; detail = "no pre-apply backup on record — prior contents could not be restored";
            }
        }
        catch (Exception e)
        {
            Memory.LogEvent(missionId, "patch_revert_failed", $"Patch revert FAILED for {filePath}: {e.Message}", taskId, "queen",
                new() { ["patch_id"] = patchId, ["file_path"] = filePath, ["error"] = e.Message });
            return $"Patch revert failed.\nPatch ID: {patchId}\nFile: {filePath}\nError: {e.Message}";
        }

        Memory.UpdatePatchStatus(patchId, PatchStatus.Reverted, lastError: $"Reverted by operator ({detail}).");
        Memory.LogEvent(missionId, "patch_reverted", $"Patch reverted by operator ({detail}): {filePath}", taskId, "queen",
            new() { ["patch_id"] = patchId, ["file_path"] = filePath, ["change_type"] = changeType, ["restored"] = restored, ["backup_path"] = backupPath });
        return $"Patch reverted.\nPatch ID: {patchId}\nFile: {filePath}\nChange: {changeType}\nResult: {detail}\nPatch Status: reverted";
    }

    // ---- Patch Center 2.0 operator surface (v1.8.24) ----------------------
    // Some pending patches have no approval_requests row (deduped duplicates whose original was
    // resolved, pre-v1.8.16 history, or a crash between proposal save and approval save). The
    // operator could see them in the Patch Center but had NO way to act on them. These methods
    // stay on the Queen/approval path: they create the missing approval record first, then run
    // the exact same approve/reject transitions as always — never a direct status write.

    /// <summary>Finds the approval record for a patch, creating one if it is missing and the patch is still pending.</summary>
    public (bool Ok, string ApprovalId, string Message) EnsurePatchApproval(string patchId, string requestedBy = "operator")
    {
        var patch = Memory.GetPatchProposal(patchId);
        if (patch is null) return (false, "", $"No patch proposal found with id: {patchId}");
        var existing = Memory.GetApprovalForTarget(patchId);
        if (existing is not null) return (true, Str(existing, "id"), "Existing approval record found.");
        if (Str(patch, "status") != PatchStatus.Proposed.Value())
            return (false, "", $"Patch is not pending (status: {Str(patch, "status")}) and has no approval record to act on.");
        var approval = new ApprovalRequest
        {
            MissionId = Str(patch, "mission_id"), TaskId = Str(patch, "task_id"),
            ActionType = ApprovalActionType.PatchProposal, TargetId = patchId,
            Title = $"Approve patch proposal for {Str(patch, "file_path")}",
            Description = "Approval record created from the Patch Center for a pending patch that had none " +
                          "(deduplicated or pre-Patch-Center proposal). Approval alone does not apply the patch.",
            RequestedBy = requestedBy,
            Metadata = new() { ["patch_proposal_id"] = patchId, ["file_path"] = Str(patch, "file_path"), ["created_from"] = "patch_center_operator" },
        };
        Memory.SaveApprovalRequest(approval);
        Memory.LogEvent(Str(patch, "mission_id"), "approval_request_created",
            $"Operator-requested approval record created from the Patch Center: {Str(patch, "file_path")}",
            Str(patch, "task_id"), "queen",
            new() { ["approval_request_id"] = approval.Id, ["target_id"] = patchId, ["created_from"] = "patch_center_operator" });
        return (true, approval.Id, "Approval record created.");
    }

    /// <summary>Approve a patch by patch id — ensures the approval record exists, then runs the normal approve transition.</summary>
    public string ApprovePatchDirect(string patchId, string requestedBy = "operator")
    {
        var (ok, approvalId, message) = EnsurePatchApproval(patchId, requestedBy);
        return ok ? ApproveRequest(approvalId) : message;
    }

    /// <summary>
    /// v0.3.8.51 (field report): approve AND apply one patch as ONE operator action with a
    /// STRUCTURED result. The chat card ran approve-then-apply against two text/plain endpoints
    /// through a JSON parser — the parse threw, the card said "Approve failed" over an approval
    /// that had actually landed, and the apply never ran. One call, one truth, both step results.
    /// </summary>
    public (bool Ok, string Message) ApproveAndApplyPatch(string patchId, string requestedBy = "operator")
    {
        var (ok, approvalId, message) = EnsurePatchApproval(patchId, requestedBy);
        if (!ok) return (false, message);

        // NO PROSE IS PARSED HERE ANY MORE. v0.3.8.91.
        //
        // This read `approveResult.Contains("approved")` and
        // `applyResult.Contains("applied") && !applyResult.Contains("not applied")`, under a comment
        // asserting that no refusal sentence contains those words. Three do. "Approval request is not
        // pending. Current Status: approved" passed the first check; "Patch cannot be applied because
        // status is rejected" and "Patch is already applied" passed the second — so a REJECTED patch
        // was reported as applied, returned HTTP success, and fired a real `git commit` over a file
        // nothing had written.
        //
        // The approval step is read from the stored STATUS rather than from a returned type, because
        // `ApproveRequest` has several callers and changing its signature is a wider edit than this
        // fix needs; the row is authoritative either way, which the sentence never was. The apply
        // step returns `PatchApplyResult`, whose outcome is the decision.
        var approveMessage = ApproveRequest(approvalId);
        var approvalNow = Memory.GetApprovalRequest(approvalId);
        if (approvalNow is null || Str(approvalNow, "status") != ApprovalStatus.Approved.Value())
            return (false, approveMessage);

        var apply = ApplyApprovedPatchTyped(approvalId);

        // The commit follows the OUTCOME, not the wording. `RefusedStatus` covers "already applied",
        // which used to re-commit a patch this call did not apply.
        if (apply.Applied) CommitAppliedPatch(patchId, requestedBy);
        return (apply.Applied, apply.Message);
    }

    /// <summary>
    /// v0.3.8.51 (field report): "changes applied but nothing hit git commit." Under the
    /// full-control gates (Skip all approvals, Automatically approve) a landed patch is COMMITTED
    /// to the repository that owns its file — git itself is asked which repo that is. Under
    /// Manual approval the tree is deliberately left dirty: the operator approved one apply, not
    /// a commit, and the files pane gives them a Commit button. A plain folder (no repo, or no
    /// git on the machine) degrades to exactly the old behavior. Never throws, never masks the
    /// apply that succeeded.
    /// </summary>
    private void CommitAppliedPatch(string patchId, string requestedBy)
    {
        try
        {
            var patch = Memory.GetPatchProposal(patchId);
            if (patch is null) return;
            var missionId = Str(patch, "mission_id");
            var conversation = Memory.FindConversationForMission(missionId);
            // Fully qualified: the Queen's own `Conversations` property (the runner) shadows the
            // namespace — the same trap `Workspaces` sprang in v0.3.8.50.
            if (conversation?.EffectivePolicy is not (Anthill.Core.Conversations.EscalationPolicy.Bypass
                                                   or Anthill.Core.Conversations.EscalationPolicy.AutoApprove)) return;

            // Resolve the file the way the apply tool did: relative paths are workspace-rooted.
            var filePath = Str(patch, "file_path");
            if (string.IsNullOrWhiteSpace(filePath)) return;
            var full = Path.IsPathRooted(filePath)
                ? Path.GetFullPath(filePath)
                : Path.GetFullPath(Path.Combine(AnthillRuntime.AllowedWorkspaceRoot, filePath));
            var dir = Path.GetDirectoryName(full);
            var repoRoot = dir is null ? null : Anthill.Core.Projects.RepoOps.TopLevel(dir);
            if (repoRoot is null) return;   // plain folder — the operator's git story ends here, honestly
            var rel = Path.GetRelativePath(repoRoot, full).Replace('\\', '/');

            var goal = (Str(Memory.GetMission(missionId) ?? new(), "goal").Split('\n')[0]).Trim();
            var subject = TextUtil.Truncate(string.IsNullOrWhiteSpace(goal) ? $"anthill: apply {rel}" : goal, 72);
            var shortMission = missionId.Length >= 8 ? missionId[..8] : missionId;
            var shortPatch = patchId.Length >= 8 ? patchId[..8] : patchId;
            var message = $"{subject}\n\nanthill mission {shortMission}, patch {shortPatch} — {rel}";

            var (ok, output) = Anthill.Core.Projects.RepoOps.Commit(repoRoot, new[] { rel }, message, requestedBy);
            Memory.LogEvent(missionId, ok ? "patch_committed" : "patch_commit_failed",
                ok ? $"Committed {rel} to {repoRoot}: {TextUtil.Truncate(output.Split('\n')[0], 160)}"
                   : $"Commit of {rel} failed: {TextUtil.Truncate(output, 200)}",
                Str(patch, "task_id"), "queen",
                new() { ["patch_id"] = patchId, ["file"] = rel, ["repo"] = repoRoot, ["ok"] = ok });
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[queen] commit after apply failed for {patchId}: {e.Message}");
        }
    }

    /// <summary>Reject a patch by patch id — ensures the approval record exists, then runs the normal reject transition.</summary>
    public string RejectPatchDirect(string patchId, string? reason = null, string requestedBy = "operator")
    {
        var (ok, approvalId, message) = EnsurePatchApproval(patchId, requestedBy);
        return ok ? RejectRequest(approvalId, reason) : message;
    }

    /// <summary>
    /// v1.8.24: the operator edits a proposal's new content and offers it as an ALTERNATIVE patch.
    /// The alternative is a brand-new proposal (same file, same base old content) that goes through
    /// the standard approval gate like any coder proposal — editing never writes to disk directly.
    /// The original is marked superseded (and its pending approval resolved) unless kept.
    /// </summary>
    public (bool Ok, string NewPatchId, string Message) ProposeAlternativePatch(
        string originalPatchId, string newContent, string reason, string author = "operator", bool supersedeOriginal = true)
    {
        var orig = Memory.GetPatchProposal(originalPatchId);
        if (orig is null) return (false, "", $"No patch proposal found with id: {originalPatchId}");
        if (newContent is null || newContent.Length == 0) return (false, "", "Alternative patch content is empty.");
        if (string.Equals(orig.GetValueOrDefault("new_content") as string, newContent, StringComparison.Ordinal))
            return (false, "", "Alternative content is identical to the original proposal.");

        var missionId = Str(orig, "mission_id");
        var taskId = Str(orig, "task_id");
        var proposal = new PatchProposal
        {
            FilePath = Str(orig, "file_path"),
            ChangeType = EnumExtensions.ParsePatchChangeType(Str(orig, "change_type", "modify")),
            Reason = string.IsNullOrWhiteSpace(reason)
                ? $"Operator alternative to patch {originalPatchId}"
                : $"{reason.Trim()} (operator alternative to patch {originalPatchId})",
            Risk = Str(orig, "risk", "unknown"),
            OldContent = orig.GetValueOrDefault("old_content") as string,
            BaseHash = orig.GetValueOrDefault("base_hash") as string,
            DestinationPath = orig.GetValueOrDefault("destination_path") as string,
            NewContent = newContent,
            RequiresApproval = true,
            Status = PatchStatus.Proposed,
        };
        Memory.SavePatchSet(new PatchSet
        {
            MissionId = missionId, TaskId = taskId,
            Summary = $"Operator alternative for {proposal.FilePath} (edited from {originalPatchId})",
            Proposals = new() { proposal },
        });
        var approval = new ApprovalRequest
        {
            MissionId = missionId, TaskId = taskId, ActionType = ApprovalActionType.PatchProposal, TargetId = proposal.Id,
            Title = $"Approve operator alternative patch for {proposal.FilePath}",
            Description = $"Operator-edited alternative to patch {originalPatchId}.\nFile: {proposal.FilePath}\nReason: {proposal.Reason}\n\n" +
                          "Approval alone does not apply the patch.",
            RequestedBy = author,
            Metadata = new() { ["patch_proposal_id"] = proposal.Id, ["alternative_to"] = originalPatchId, ["file_path"] = proposal.FilePath },
        };
        Memory.SaveApprovalRequest(approval);
        Memory.LogEvent(missionId, "patch_alternative_created",
            $"Operator proposed an alternative patch for {proposal.FilePath} (edited from {originalPatchId}).", taskId, "queen",
            new() { ["original_patch_id"] = originalPatchId, ["new_patch_id"] = proposal.Id, ["approval_request_id"] = approval.Id });

        if (supersedeOriginal && Str(orig, "status") == PatchStatus.Proposed.Value())
        {
            Memory.UpdatePatchStatus(originalPatchId, PatchStatus.Superseded,
                lastError: $"Superseded by operator alternative {proposal.Id}.");
            var origApproval = Memory.GetApprovalForTarget(originalPatchId);
            if (origApproval is not null && Str(origApproval, "status") == ApprovalStatus.Pending.Value())
                Memory.UpdateApprovalStatus(Str(origApproval, "id"), ApprovalStatus.Rejected,
                    $"Superseded by operator alternative patch {proposal.Id}.");
        }
        return (true, proposal.Id, $"Alternative patch created: {proposal.Id}");
    }

    public string FormatPendingApprovals(int limit = 20) => FormatApprovals(limit, ApprovalStatus.Pending);

    public string FormatApprovals(int limit = 20, ApprovalStatus? status = ApprovalStatus.Pending)
    {
        var rows = Memory.ListApprovalRequests(status, limit);
        var label = status?.Value() ?? "all";
        if (rows.Count == 0) return $"No approval requests found for status: {label}.";
        var blocks = rows.Select(a =>
            $"Approval ID: {Str(a, "id")}\nStatus: {Str(a, "status")}\nAction: {Str(a, "action_type")}\nTarget ID: {Str(a, "target_id")}\n" +
            $"Mission ID: {Str(a, "mission_id")}\nTask ID: {Str(a, "task_id", "n/a")}\nTitle: {Str(a, "title")}\nRequested By: {Str(a, "requested_by")}\n" +
            $"Created At: {Str(a, "created_at", "n/a")}\nDecided At: {Str(a, "decided_at", "n/a")}\nDecision Note: {Str(a, "decision_note", "n/a")}\n" +
            $"Description:\n{TextUtil.Truncate(Str(a, "description"), 260, "...[description truncated]")}");
        return $"Approval Requests | status={label} | count={rows.Count}\n\n" + string.Join(Divider, blocks);
    }

    public string FormatApprovalDetail(string approvalId)
    {
        try { approvalId = Validation.ValidateApprovalId(approvalId); }
        catch (Exception e) { return $"Invalid approval id: {e.Message}"; }
        var approval = Memory.GetApprovalRequest(approvalId);
        if (approval is null) return $"No approval request found with id: {approvalId}";
        var metadata = ParseMetadata(approval);
        var targetId = Str(approval, "target_id");
        var relatedPatch = "";
        var applyLine = "";
        if (Str(approval, "action_type") == ApprovalActionType.PatchProposal.Value())
        {
            relatedPatch = $"\nInspect Related Patch: /patch {targetId}";
            applyLine = $"\nApply If Approved: /apply {approvalId}";
        }
        return $"Approval ID: {Str(approval, "id")}\nStatus: {Str(approval, "status")}\nAction Type: {Str(approval, "action_type")}\nTarget ID: {targetId}\n" +
               $"Mission ID: {Str(approval, "mission_id")}\nTask ID: {Str(approval, "task_id")}\nRequested By: {Str(approval, "requested_by")}\nTitle: {Str(approval, "title")}\n\n" +
               $"Description:\n{Str(approval, "description")}\n\nDecision Note:\n{Str(approval, "decision_note", "n/a")}\n\n" +
               $"Created At: {Str(approval, "created_at")}\nDecided At: {Str(approval, "decided_at", "n/a")}\n\n" +
               $"Metadata:\n{Json.Dumps(metadata, indented: true)}{relatedPatch}{applyLine}\n\n" +
               "Safety Note: /apply only works when approval is approved and both write gates are enabled.";
    }

    // ---- missions / patches / sources views -------------------------------

    public string FormatMissionDetail(string missionId)
    {
        var mission = Memory.GetMission(missionId);
        if (mission is null) return $"No mission found with id: {missionId}";
        var tasks = Memory.GetTasksForMission(missionId);
        var goal = TextUtil.Truncate(Str(mission, "goal"), 600, "...[goal truncated]");
        var userResult = TextUtil.Truncate(Str(mission, "user_result", Str(mission, "final_result")), 1200, "...[result truncated]");
        var taskLines = tasks.Select(t =>
            $"- {Str(t, "id")} | {Str(t, "assigned_ant")} | {Str(t, "task_type")} | {Str(t, "status")} | {Str(t, "elapsed_seconds", "0")}s | {Str(t, "title")}\n" +
            $"  Summary: {TextUtil.Truncate(Str(t, "result_summary", Str(t, "result")), 260, "...[summary truncated]")}");
        var tasksBlock = tasks.Count > 0 ? string.Join("\n", taskLines) : "No tasks saved for this mission.";
        return $"Mission ID: {Str(mission, "id")}\nStatus: {Str(mission, "status")}\nPheromone Score: {Str(mission, "success_score")}\n" +
               $"Best Output Task ID: {Str(mission, "best_output_task_id", "n/a")}\nCreated At: {Str(mission, "created_at")}\nSaved At: {Str(mission, "saved_at")}\n\n" +
               $"Goal:\n{goal}\n\nUser Result Preview:\n{userResult}\n\nTasks:\n{tasksBlock}";
    }

    public string FormatMissionHistory(int limit = 10)
    {
        var rows = Memory.GetRecentMissions(limit);
        if (rows.Count == 0) return "No missions in ANTHILL memory yet.";
        var blocks = rows.Select(m =>
            $"Mission ID: {Str(m, "id")} | Status: {Str(m, "status")} | Score: {Str(m, "success_score")}\n" +
            $"Goal: {TextUtil.Truncate(Str(m, "goal"), 200, "...[goal truncated]")}\nSaved At: {Str(m, "saved_at")}");
        return $"ANTHILL Mission History | count={rows.Count}\n\n" + string.Join(Divider, blocks);
    }

    public string FormatPatchList(int limit = 20)
    {
        var patches = Memory.ListPatchProposals(limit: limit);
        if (patches.Count == 0) return "No patch proposals recorded.";
        var blocks = patches.Select(p =>
            $"Patch ID: {Str(p, "id")} | Status: {Str(p, "status")} | Change: {Str(p, "change_type")}\n" +
            $"File: {Str(p, "file_path")}\nReason: {TextUtil.Truncate(Str(p, "reason"), 200, "...[reason truncated]")}\nMission ID: {Str(p, "mission_id")}");
        return $"Patch Proposals | count={patches.Count}\n\n" + string.Join(Divider, blocks);
    }

    public string FormatPatchDetail(string patchId)
    {
        try { patchId = Validation.ValidatePatchId(patchId); }
        catch (Exception e) { return $"Invalid patch id: {e.Message}"; }
        var patch = Memory.GetPatchProposal(patchId);
        if (patch is null) return $"No patch proposal found with id: {patchId}";
        return $"Patch ID: {Str(patch, "id")}\nStatus: {Str(patch, "status")}\nChange Type: {Str(patch, "change_type")}\nFile: {Str(patch, "file_path")}\n" +
               $"Mission ID: {Str(patch, "mission_id")}\nTask ID: {Str(patch, "task_id")}\n\nReason:\n{Str(patch, "reason")}\n\nRisk:\n{Str(patch, "risk")}\n\n" +
               $"Old Content:\n{TextUtil.Truncate(Str(patch, "old_content"), AnthillRuntime.MaxPatchContentChars, "...[old_content truncated]")}\n\n" +
               $"New Content:\n{TextUtil.Truncate(Str(patch, "new_content"), AnthillRuntime.MaxPatchContentChars, "...[new_content truncated]")}\n\n" +
               $"Applied At: {Str(patch, "applied_at", "n/a")}\nBackup Path: {Str(patch, "backup_path", "n/a")}\nLast Error: {Str(patch, "last_error", "n/a")}";
    }

    public string FormatSources(int limit = 20)
    {
        var rows = Memory.GetRecentSources(limit);
        if (rows.Count == 0) return "No source records saved.";
        var blocks = rows.Select(s =>
            $"Source ID: {Str(s, "id")} | Confidence: {Str(s, "confidence_label")} ({Str(s, "confidence_score")})\n" +
            $"Title: {Str(s, "title")}\nDomain: {Str(s, "domain")}\nURL: {Str(s, "url")}\nSummary: {TextUtil.Truncate(Str(s, "summary"), 240, "...[summary truncated]")}");
        return $"Saved Sources | count={rows.Count}\n\n" + string.Join(Divider, blocks);
    }

    public string FormatSourceDetail(string sourceId)
    {
        try { sourceId = Validation.ValidateSourceId(sourceId); }
        catch (Exception e) { return $"Invalid source id: {e.Message}"; }
        var s = Memory.GetSourceRecord(sourceId);
        if (s is null) return $"No source record found with id: {sourceId}";
        return $"Source ID: {Str(s, "id")}\nMission ID: {Str(s, "mission_id")}\nTask ID: {Str(s, "task_id", "n/a")}\nTitle: {Str(s, "title")}\n" +
               $"Domain: {Str(s, "domain")}\nURL: {Str(s, "url")}\nProvider: {Str(s, "provider")}\n" +
               $"Scores: relevance={Str(s, "relevance_score")} freshness={Str(s, "freshness_score")} authority={Str(s, "authority_score")} confidence={Str(s, "confidence_score")} ({Str(s, "confidence_label")})\n" +
               $"Quality Notes: {Str(s, "quality_notes")}\n\nSnippet:\n{Str(s, "snippet")}\n\nSummary:\n{Str(s, "summary")}";
    }

    public string FormatSourceQuality(int limit = 20)
    {
        var rows = Memory.GetSourceQualitySummary(limit);
        if (rows.Count == 0) return "No source quality data yet.";
        var blocks = rows.Select(r =>
            $"{Str(r, "domain")} | sources={Str(r, "source_count")} | avg_confidence={Str(r, "avg_confidence")} | " +
            $"avg_relevance={Str(r, "avg_relevance")} | avg_authority={Str(r, "avg_authority")} | last_seen={Str(r, "last_seen")}");
        return $"Source Quality by Domain | count={rows.Count}\n\n" + string.Join("\n", blocks);
    }

    // ---- diagnostics / status views --------------------------------------

    public string FormatMemoryView(int limit = 10) =>
        $"ANTHILL Recent Memory (limit={limit})\n\n{Memory.FormatRecentMemory(limit, AnthillRuntime.MemoryResultChars)}";

    public string FormatPheromoneView(int limit = 15) =>
        $"ANTHILL Pheromone Trails (top {limit})\n\n{Memory.FormatPheromoneContext(limit)}";

    public string FormatConfigStatus()
    {
        var c = AnthillRuntime.Config;
        return $"ANTHILL v{AnthillRuntime.Version} Configuration\nSafety Profile: {c.SafetyProfile}\nConfig Path: {AnthillRuntime.ConfigPath}\n" +
               $"Workspace Root: {AnthillRuntime.WorkspaceRootPath}\nDB Path: {AnthillRuntime.DbPath}\n" +
               $"API Auth Enabled: {AnthillRuntime.EnableApiAuth}\nAPI Host: {AnthillRuntime.ApiHost}\nAPI Port: {AnthillRuntime.ApiPort}\n" +
               $"Web Search: {AnthillRuntime.EnableWebSearch}\nFile Tools: {AnthillRuntime.EnableFileTools}\nShell Tool: {AnthillRuntime.EnableShellTool}\n" +
               $"Patch Application: {AnthillRuntime.EnablePatchApplication}\nFile Writing: {AnthillRuntime.EnableFileWriting}\n" +
               $"Parallel Execution: {AnthillRuntime.EnableParallelExecution} (workers={AnthillRuntime.MaxParallelWorkers})\n" +
               $"Native Kernel: {(Native.NativeKernel.UsingNative ? "active" : "managed-fallback")}";
    }

    public string FormatSchemaStatus()
    {
        var status = Memory.GetSchemaStatus();
        return $"ANTHILL Schema Status\nAnthill Version: {status.GetValueOrDefault("anthill_version")}\n" +
               $"Expected Schema Version: {status.GetValueOrDefault("expected_schema_version")}\n" +
               $"Recorded Schema Version: {status.GetValueOrDefault("schema_version")}\n" +
               $"Migration Count: {status.GetValueOrDefault("migration_count")}";
    }

    public string FormatSystemStatus()
    {
        var events = Memory.SummarizeEvents();
        var tasks = Memory.SummarizeTaskMetrics();
        var pendingApprovals = Memory.CountPendingApprovals();
        return $"ANTHILL v{AnthillRuntime.Version} System Status\nSafety Profile: {AnthillRuntime.Config.SafetyProfile}\n" +
               $"Native Kernel: {(Native.NativeKernel.UsingNative ? "active" : "managed-fallback")}\n" +
               $"Events Logged: {events.GetValueOrDefault("event_count")}\nFailure Events: {events.GetValueOrDefault("failure_event_count")}\n" +
               $"Tasks Recorded: {tasks.GetValueOrDefault("task_count")}\nFailed Tasks: {tasks.GetValueOrDefault("failed_count")}\nSkipped Tasks: {tasks.GetValueOrDefault("skipped_count")}\n" +
               $"Pending Approvals: {pendingApprovals}\nModel Calls This Session: {Router?.CallCount ?? 0}";
    }

    public string FormatRuntimeDiagnostics()
    {
        var failures = Memory.GetRecentFailureEvents(AnthillRuntime.DiagnosticEventLimit);
        var header = $"ANTHILL v{AnthillRuntime.Version} Runtime Diagnostics\nRecent Failure Events: {failures.Count}\n";
        if (failures.Count == 0) return header + "\nNo recent failure events. The colony is healthy.";
        var blocks = failures.Select(f =>
            $"[{Str(f, "event_type")}] {Str(f, "created_at")}\nMission: {Str(f, "mission_id")} | Task: {Str(f, "task_id", "n/a")} | Ant: {Str(f, "ant_name", "n/a")}\n" +
            $"Message: {TextUtil.Truncate(Str(f, "message"), 300, "...[message truncated]")}");
        return header + "\n" + string.Join(Divider, blocks);
    }

    public string FormatEventLog(int limit = 30, string? eventType = null, string? missionId = null)
    {
        var rows = Memory.GetRecentEvents(limit, eventType, missionId);
        if (rows.Count == 0) return "No events recorded.";
        var blocks = rows.Select(e =>
            $"[{Str(e, "event_type")}] {Str(e, "created_at")}\nMission: {Str(e, "mission_id")} | Task: {Str(e, "task_id", "n/a")} | Ant: {Str(e, "ant_name", "n/a")}\n" +
            $"{TextUtil.Truncate(Str(e, "message"), 300, "...[message truncated]")}");
        return $"ANTHILL Event Log | count={rows.Count}\n\n" + string.Join("\n\n", blocks);
    }

    public string FormatTaskMetrics()
    {
        var m = Memory.SummarizeTaskMetrics();
        return $"ANTHILL Task Metrics\nTasks: {m.GetValueOrDefault("task_count")}\nAvg Elapsed: {m.GetValueOrDefault("avg_elapsed_seconds")}s\n" +
               $"Max Elapsed: {m.GetValueOrDefault("max_elapsed_seconds")}s\nFailed: {m.GetValueOrDefault("failed_count")}\nSkipped: {m.GetValueOrDefault("skipped_count")}";
    }

    public string FormatMessageMetrics()
    {
        var m = Memory.SummarizeMessageMetrics();
        return $"ANTHILL Message Metrics\nMetrics: {m.GetValueOrDefault("metric_count")}\nInput Chars: {m.GetValueOrDefault("input_chars")}\n" +
               $"Output Chars: {m.GetValueOrDefault("output_chars")}\nInput Tokens (est): {m.GetValueOrDefault("input_tokens_est")}\nOutput Tokens (est): {m.GetValueOrDefault("output_tokens_est")}";
    }

    public string FormatAgentCommunication(int limit = 30, string? missionId = null)
    {
        var summary = Memory.SummarizeAgentMessages();
        var rows = Memory.GetRecentAgentMessages(limit, missionId);
        var header = $"ANTHILL v{AnthillRuntime.Version} Agent Communication Ledger\nSchema: {AnthillRuntime.AgentMessageVersion}\n" +
                     $"Enabled: {(AnthillRuntime.EnableAgentCommunicationLedger ? "ON" : "OFF")}\nLimit: {limit}\n" +
                     $"Message Count: {summary.GetValueOrDefault("message_count")}\n";
        if (rows.Count == 0) return header + "\nNo agent messages recorded yet.";
        var blocks = rows.Select(r =>
            $"Message ID: {Str(r, "id")}\nCreated At: {Str(r, "created_at")}\nMission ID: {Str(r, "mission_id")}\nTask ID: {Str(r, "task_id", "n/a")}\n" +
            $"Route: {Str(r, "sender")} -> {Str(r, "recipient")}\nType: {Str(r, "message_type")} | Schema: {Str(r, "schema_version")}\n" +
            $"Chars/Tokens: {Str(r, "content_chars")} / {Str(r, "estimated_tokens")}\nContent:\n{TextUtil.Truncate(Str(r, "content"), 700, "...[content truncated]")}");
        return header + "\n" + string.Join(Divider, blocks);
    }

    public string FormatModelRoutes() => Router is null ? "Model routing is disabled." : Router.FormatRoutes();
    public string FormatModelStatus() => Router is null ? "Model routing is disabled." : Router.FormatModels();

    // ---- task graph -------------------------------------------------------

    public Dictionary<string, object?> BuildTaskGraphData(string? missionId = null, bool includeResults = false, bool includeResultPreview = true)
    {
        if (!AnthillRuntime.EnableTaskGraphExport)
            return new() { ["schema_version"] = AnthillRuntime.TaskGraphVersion, ["enabled"] = false, ["nodes"] = new List<object>(), ["edges"] = new List<object>() };
        missionId ??= LatestMissionId();
        if (missionId is null)
            return new() { ["schema_version"] = AnthillRuntime.TaskGraphVersion, ["enabled"] = true, ["mission_id"] = null, ["nodes"] = new List<object>(), ["edges"] = new List<object>() };
        var mission = Memory.GetMission(missionId);
        if (mission is null)
            return new() { ["schema_version"] = AnthillRuntime.TaskGraphVersion, ["enabled"] = true, ["mission_id"] = missionId, ["nodes"] = new List<object>(), ["edges"] = new List<object>(), ["error"] = "mission_not_found" };

        var tasks = Memory.GetTasksForMission(missionId, 300);
        var edges = new List<Dictionary<string, string>>();
        var childIds = new Dictionary<string, List<string>>();
        var parsed = new List<(Dictionary<string, object?> Row, List<string> Deps, List<string> Parents)>();

        foreach (var row in tasks)
        {
            var deps = ParseJsonStringList(Str(row, "depends_on_json", "[]"));
            var parents = ParseJsonStringList(Str(row, "parent_task_ids_json", "[]"));
            var pid = Str(row, "parent_task_id");
            if (!string.IsNullOrEmpty(pid) && !parents.Contains(pid)) parents.Add(pid);
            var id = Str(row, "id");
            childIds.TryAdd(id, new());
            foreach (var dep in deps) { childIds.TryAdd(dep, new()); childIds[dep].Add(id); edges.Add(new() { ["from"] = dep, ["to"] = id, ["type"] = "depends_on" }); }
            foreach (var parent in parents) { childIds.TryAdd(parent, new()); childIds[parent].Add(id); edges.Add(new() { ["from"] = parent, ["to"] = id, ["type"] = "parent_task" }); }
            parsed.Add((row, deps, parents));
        }

        var nodes = new List<Dictionary<string, object?>>();
        foreach (var (row, deps, parents) in parsed)
        {
            var status = Str(row, "status", "unknown");
            string? statusMessage = status switch
            {
                "failed" => Str(row, "failure_reason"),
                "skipped" => Str(row, "skipped_reason"),
                "blocked" => Str(row, "blocked_reason"),
                "ready" => "Dependencies satisfied; task is ready to run.",
                _ => null,
            };
            var id = Str(row, "id");
            var node = new Dictionary<string, object?>
            {
                ["task_id"] = id, ["mission_id"] = missionId, ["title"] = Str(row, "title"), ["name"] = Str(row, "title"),
                ["assigned_ant"] = Str(row, "assigned_ant", "unknown"), ["assigned_worker"] = Str(row, "assigned_worker"),
                ["assigned_agent"] = string.IsNullOrEmpty(Str(row, "assigned_worker")) ? Str(row, "assigned_ant", "unknown") : Str(row, "assigned_worker"),
                ["role"] = Str(row, "assigned_ant", "unknown"), ["task_type"] = Str(row, "task_type", "general"), ["status"] = status,
                ["dependency_ids"] = deps, ["depends_on"] = deps,
                // v0.3.8.57: stored is not the same as reachable. The declared inputs are what
                // makes a worker's context auditable, so the graph the console reads carries them.
                ["input_artifact_ids"] = ParseJsonStringList(Str(row, "input_artifact_ids_json", "[]")), ["parent_task_id"] = row.GetValueOrDefault("parent_task_id"),
                ["parent_task_ids"] = parents, ["child_task_ids"] = childIds.GetValueOrDefault(id, new()).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
                ["attempt_count"] = row.GetValueOrDefault("attempt_count"), ["max_attempts"] = row.GetValueOrDefault("max_attempts"),
                ["created_at"] = row.GetValueOrDefault("created_at"), ["started_at"] = row.GetValueOrDefault("started_at"),
                ["completed_at"] = row.GetValueOrDefault("completed_at"), ["failed_at"] = row.GetValueOrDefault("failed_at"),
                ["skipped_at"] = row.GetValueOrDefault("skipped_at"), ["failure_type"] = row.GetValueOrDefault("failure_type"),
                ["status_message"] = statusMessage, ["elapsed_seconds"] = row.GetValueOrDefault("elapsed_seconds"),
            };
            if (includeResultPreview && !string.IsNullOrEmpty(Str(row, "result_summary")))
            {
                var redacted = System.Text.RegularExpressions.Regex.Replace(Str(row, "result_summary"),
                    @"(?i)\b(token|api[_-]?key|password|passwd|secret|authorization)\b\s*[:=]\s*[^,\s;]+", "$1=[redacted]");
                node["result_summary_preview"] = TextUtil.Truncate(redacted, 240, "...[preview truncated]");
            }
            if (includeResults) node["result_summary"] = row.GetValueOrDefault("result_summary");
            nodes.Add(node);
        }

        var statusCounts = nodes.GroupBy(n => n.GetValueOrDefault("status")?.ToString() ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var antCounts = nodes.GroupBy(n => n.GetValueOrDefault("assigned_ant")?.ToString() ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var workerCounts = nodes.GroupBy(n => n.GetValueOrDefault("assigned_worker")?.ToString() ?? n.GetValueOrDefault("assigned_ant")?.ToString() ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var safeMission = includeResults ? mission : new Dictionary<string, object?>
        {
            ["id"] = mission.GetValueOrDefault("id"), ["goal"] = mission.GetValueOrDefault("goal"), ["status"] = mission.GetValueOrDefault("status"),
            ["best_output_task_id"] = mission.GetValueOrDefault("best_output_task_id"), ["success_score"] = mission.GetValueOrDefault("success_score"),
            ["created_at"] = mission.GetValueOrDefault("created_at"), ["saved_at"] = mission.GetValueOrDefault("saved_at"),
        };
        return new()
        {
            ["schema_version"] = AnthillRuntime.TaskGraphVersion, ["enabled"] = true, ["mission"] = safeMission, ["mission_id"] = missionId,
            ["nodes"] = nodes, ["edges"] = edges, ["status_counts"] = statusCounts, ["ant_counts"] = antCounts, ["worker_counts"] = workerCounts,
        };
    }

    public string FormatTaskGraph(string? missionId = null)
    {
        var graph = BuildTaskGraphData(missionId);
        if (graph.TryGetValue("error", out var err) && err is not null)
            return $"Task graph error: {err} for mission {graph.GetValueOrDefault("mission_id")}";
        var mission = graph.GetValueOrDefault("mission") as Dictionary<string, object?> ?? new();
        var nodes = graph.GetValueOrDefault("nodes") as List<Dictionary<string, object?>> ?? new();
        var edges = graph.GetValueOrDefault("edges") as List<Dictionary<string, string>> ?? new();
        var header = $"ANTHILL v{AnthillRuntime.Version} Task Graph\nSchema: {AnthillRuntime.TaskGraphVersion}\n" +
                     $"Mission ID: {graph.GetValueOrDefault("mission_id") ?? "n/a"}\nMission Status: {mission.GetValueOrDefault("status") ?? "n/a"}\n" +
                     $"Goal: {TextUtil.Truncate(mission.GetValueOrDefault("goal")?.ToString() ?? "", 250, "...[goal truncated]")}\n" +
                     $"Nodes: {nodes.Count} | Edges: {edges.Count}\n";
        if (nodes.Count == 0) return header + "\nNo task graph nodes found.";
        var lines = nodes.Select(n =>
        {
            var deps = string.Join(", ", (n.GetValueOrDefault("dependency_ids") as List<string>) ?? new());
            if (deps.Length == 0) deps = "none";
            var preview = n.GetValueOrDefault("result_summary_preview")?.ToString() ?? n.GetValueOrDefault("status_message")?.ToString() ?? "";
            return $"[{n.GetValueOrDefault("status")}] {n.GetValueOrDefault("assigned_agent")}::{n.GetValueOrDefault("task_type")}\n" +
                   $"Task ID: {n.GetValueOrDefault("task_id")}\nTitle: {n.GetValueOrDefault("title")}\nDepends On: {deps}\n" +
                   $"Attempts: {n.GetValueOrDefault("attempt_count")}/{n.GetValueOrDefault("max_attempts")}\nElapsed: {n.GetValueOrDefault("elapsed_seconds") ?? "n/a"}\n" +
                   $"Status Message: {TextUtil.Truncate(preview, 240, "...[message truncated]")}";
        });
        return header + "\n\n" + string.Join(Divider, lines);
    }

    // ---- result composition -----------------------------------------------
    // v3.1.0 (ADR-001): moved to ResultAssembler. The Queen remains the facade — it decides
    // WHEN results are assembled, at finalization, after grading and learning.

    public string? LatestMissionId()
    {
        if (LastMissionId is not null) return LastMissionId;
        var recent = Memory.GetRecentMissions(1);
        return recent.Count > 0 ? Str(recent[0], "id") : null;
    }

    private static Dictionary<string, object?> ParseMetadata(Dictionary<string, object?> row)
    {
        try
        {
            var json = row.GetValueOrDefault("metadata_json")?.ToString() ?? "{}";
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new();
        }
        catch { return new(); }
    }

    private static List<string> ParseJsonStringList(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }
}
