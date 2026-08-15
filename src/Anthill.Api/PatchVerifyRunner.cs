using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Orchestration;
using Anthill.Core.Sandbox;

namespace Anthill.Api;

/// <summary>
/// v1.8.24: operator-triggered, UNBIASED verification of a single pending patch.
///
/// "Unbiased" means the judgment is the real toolchain, not the ant that proposed the change:
/// the patch is applied to the workspace (with a backup), the verify command runs
/// (operator-configured <c>autonomy_autoapply_verify_cmd</c>, or the built-in
/// <c>dotnet build &amp;&amp; dotnet test</c>), and then the pre-apply state is ALWAYS restored —
/// green or red, the working tree is left exactly as it was. Verification never ships code.
///
/// If (and only if) the verify is green, the patch is auto-APPROVED through the normal
/// Queen/approval path (<see cref="Queen.ApprovePatchDirect"/>). It is NOT auto-applied:
/// writing to disk permanently still requires the operator's explicit Apply. A red verify
/// leaves the patch pending with the failure tail recorded, so the operator can decide.
/// </summary>
public static class PatchVerifyRunner
{
    public static Dictionary<string, object?> VerifyAndMaybeApprove(Queen queen, string patchId)
    {
        Dictionary<string, object?> Fail(string message, string code) => new()
        { ["verified"] = false, ["approved"] = false, ["error"] = message, ["error_code"] = code };

        var patch = queen.Memory.GetPatchProposal(patchId);
        if (patch is null) return Fail($"No patch proposal found with id: {patchId}", "not_found");
        var status = patch.GetValueOrDefault("status")?.ToString() ?? "";
        if (status != PatchStatus.Proposed.Value() && status != PatchStatus.Approved.Value())
            return Fail($"Patch status is '{status}' — only pending or approved patches can be verified.", "bad_status");

        var missionId = patch.GetValueOrDefault("mission_id")?.ToString() ?? AnthillRuntime.SystemApiMissionId;
        var taskId = patch.GetValueOrDefault("task_id")?.ToString();
        var filePath = patch.GetValueOrDefault("file_path")?.ToString() ?? "";

        // v2.10.1 (NORTH_STAR Phase 3): when sandboxed execution is enabled, verification happens
        // in a DISPOSABLE COPY of the workspace — the live checkout is never written to, so no
        // write gates are required and no restore step can ever leave the install modified.
        if (AnthillRuntime.EnableSandboxExecution)
            return VerifyInSandbox(queen, patchId, patch, missionId, taskId, filePath, Fail);

        // Legacy path: verification temporarily writes the patch to the LIVE workspace, so the same
        // write gates that guard Apply must be on. Not a bypass — the change never persists here.
        if (!AnthillRuntime.EnablePatchApplication || !AnthillRuntime.EnableFileWriting)
            return Fail("Write gates are off (patch_application_enabled / file_writing_enabled) — " +
                        "verification needs to temporarily apply the patch to build against it. " +
                        "Enable sandbox_execution_enabled to verify without touching the workspace.", "write_gates_off");

        queen.Memory.LogEvent(missionId, "patch_verify_started",
            $"Operator requested unbiased verification of patch {patchId} ({filePath}).", taskId, "operator",
            new() { ["patch_id"] = patchId, ["file_path"] = filePath });

        // 1. Apply with backup (same gated path automation uses).
        var outcome = queen.ApplyPatchForAutomation(patchId, missionId, taskId);
        if (!outcome.Success)
            return Fail($"Could not stage the patch for verification: {outcome.Error}", "stage_failed");

        // 2. Run the verify command.
        AutoApplyRunner.VerifyResult verify;
        try { verify = AutoApplyRunner.RunVerify(); }
        finally
        {
            // 3. ALWAYS restore the pre-apply state — green or red. Restore failure is loud.
            Restore(queen, outcome, missionId, taskId);
        }

        var tail = AutoApplyRunner.Tail(verify.Output, 2000);
        if (verify.Green)
        {
            // Reset the transient 'applied' bookkeeping, then approve through the normal gate.
            queen.Memory.UpdatePatchStatus(patchId, PatchStatus.Proposed, lastError: null);
            var approveMsg = queen.ApprovePatchDirect(patchId, "verify_runner");
            queen.Memory.LogEvent(missionId, "patch_verified_approved",
                $"Verification PASSED for {filePath} (exit {verify.ExitCode}, {verify.Seconds}s) — patch auto-approved. Apply still requires the operator.",
                taskId, "queen",
                new() { ["patch_id"] = patchId, ["verify_exit"] = verify.ExitCode, ["verify_seconds"] = verify.Seconds });
            return new()
            {
                ["verified"] = true, ["approved"] = true, ["exit_code"] = verify.ExitCode,
                ["seconds"] = verify.Seconds, ["output_tail"] = tail, ["approve_message"] = approveMsg,
            };
        }

        var reason = verify.TimedOut ? "verify timed out" : $"verify failed (exit {verify.ExitCode})";
        queen.Memory.UpdatePatchStatus(patchId, PatchStatus.Proposed, lastError: $"Verification failed: {reason}.");
        queen.Memory.LogEvent(missionId, "patch_verify_failed",
            $"Verification FAILED for {filePath} — {reason}. Patch stays pending; workspace restored.", taskId, "queen",
            new() { ["patch_id"] = patchId, ["verify_exit"] = verify.ExitCode, ["timed_out"] = verify.TimedOut, ["verify_tail"] = AutoApplyRunner.Tail(verify.Output, 1000) });
        return new()
        {
            ["verified"] = false, ["approved"] = false, ["exit_code"] = verify.ExitCode,
            ["timed_out"] = verify.TimedOut, ["seconds"] = verify.Seconds, ["output_tail"] = tail,
        };
    }

    /// <summary>
    /// v2.10.1 sandboxed verification (NORTH_STAR Phase 3): copy the workspace, write the patched
    /// content INTO THE COPY, build/test there, destroy the copy. The live checkout is never
    /// written to — there is nothing to restore and no way for a crash mid-verify to leave the
    /// running install modified. A green verify still only APPROVES; applying remains the
    /// operator's explicit action against the real workspace.
    /// </summary>
    private static Dictionary<string, object?> VerifyInSandbox(
        Queen queen, string patchId, Dictionary<string, object?> patch,
        string missionId, string? taskId, string filePath,
        Func<string, string, Dictionary<string, object?>> fail)
    {
        var newContent = patch.GetValueOrDefault("new_content")?.ToString();
        if (string.IsNullOrEmpty(newContent))
            return fail("Patch has no new_content to verify.", "empty_patch");

        var liveRoot = Directory.Exists(AnthillRuntime.AllowedWorkspaceRoot)
            ? Path.GetFullPath(AnthillRuntime.AllowedWorkspaceRoot) : Environment.CurrentDirectory;

        queen.Memory.LogEvent(missionId, "patch_verify_started",
            $"Sandboxed verification of patch {patchId} ({filePath}) — live workspace is not modified.",
            taskId, "operator", new() { ["patch_id"] = patchId, ["file_path"] = filePath, ["sandboxed"] = true });

        AutoApplyRunner.VerifyResult verify;
        try
        {
            // preferCopy: verify against the workspace AS IT IS ON DISK, including uncommitted
            // local state the patch was diffed against — a HEAD worktree could miss it.
            using var sandbox = SandboxWorkspace.Create(liveRoot, preferCopy: true);
            // v0.3.8.59 (PLAN.md §1b S1) — through the one resolver. This carried the SAME defect
            // the review found in the Files pane: a prefix comparison with no separator, so a
            // sandbox at .../work served .../work-other, and no link was ever resolved. Not named in
            // the review, found by the sweep the fix prompted — which is the argument for the sweep.
            var containment = Anthill.Core.Security.PathContainment.Resolve(sandbox.Root, filePath);
            if (!containment.Allowed)
                return fail($"Patch path escapes the sandbox: {filePath}", "path_escape");
            var target = containment.Path;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, newContent);
            verify = AutoApplyRunner.RunVerify(sandbox.Root);
        }
        catch (Exception e)
        {
            return fail($"Sandboxed verification could not run: {e.Message}", "sandbox_failed");
        }

        var tail = AutoApplyRunner.Tail(verify.Output, 2000);
        if (verify.Green)
        {
            var approveMsg = queen.ApprovePatchDirect(patchId, "verify_runner_sandboxed");
            queen.Memory.LogEvent(missionId, "patch_verified_approved",
                $"Sandboxed verification PASSED for {filePath} (exit {verify.ExitCode}, {verify.Seconds}s) — patch auto-approved. Apply still requires the operator.",
                taskId, "queen",
                new() { ["patch_id"] = patchId, ["verify_exit"] = verify.ExitCode, ["verify_seconds"] = verify.Seconds, ["sandboxed"] = true });
            return new()
            {
                ["verified"] = true, ["approved"] = true, ["exit_code"] = verify.ExitCode,
                ["seconds"] = verify.Seconds, ["output_tail"] = tail, ["approve_message"] = approveMsg,
                ["sandboxed"] = true,
            };
        }

        var reason = verify.TimedOut ? "verify timed out" : $"verify failed (exit {verify.ExitCode})";
        queen.Memory.UpdatePatchStatus(patchId, PatchStatus.Proposed, lastError: $"Sandboxed verification failed: {reason}.");
        queen.Memory.LogEvent(missionId, "patch_verify_failed",
            $"Sandboxed verification FAILED for {filePath} — {reason}. Patch stays pending; live workspace was never touched.",
            taskId, "queen",
            new() { ["patch_id"] = patchId, ["verify_exit"] = verify.ExitCode, ["timed_out"] = verify.TimedOut, ["sandboxed"] = true, ["verify_tail"] = AutoApplyRunner.Tail(verify.Output, 1000) });
        return new()
        {
            ["verified"] = false, ["approved"] = false, ["exit_code"] = verify.ExitCode,
            ["timed_out"] = verify.TimedOut, ["seconds"] = verify.Seconds, ["output_tail"] = tail,
            ["sandboxed"] = true,
        };
    }

    /// <summary>Restores the pre-verification file state without marking the patch failed (unlike rollback).</summary>
    private static void Restore(Queen queen, Queen.AutoApplyOutcome outcome, string missionId, string? taskId)
    {
        try
        {
            if (outcome.ChangeType.Equals("add", StringComparison.OrdinalIgnoreCase))
            {
                if (outcome.ResolvedPath is { Length: > 0 } p && File.Exists(p)) File.Delete(p);
            }
            else if (outcome.BackupPath is { Length: > 0 } backup && outcome.ResolvedPath is { Length: > 0 } target && File.Exists(backup))
            {
                File.Copy(backup, target, overwrite: true);
            }
        }
        catch (Exception e)
        {
            queen.Memory.LogEvent(missionId, "patch_verify_restore_failed",
                $"Could not restore {outcome.FilePath} after verification: {e.Message} — backup at {outcome.BackupPath ?? "n/a"}.",
                taskId, "queen", new() { ["patch_id"] = outcome.PatchId, ["error"] = e.Message, ["backup_path"] = outcome.BackupPath });
        }
    }
}
