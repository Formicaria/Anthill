using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Orchestration;

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

        // Verification temporarily writes the patch to the workspace, so the same write gates
        // that guard Apply must be on. This is not a bypass: the change never persists here.
        if (!AnthillRuntime.EnablePatchApplication || !AnthillRuntime.EnableFileWriting)
            return Fail("Write gates are off (patch_application_enabled / file_writing_enabled) — " +
                        "verification needs to temporarily apply the patch to build against it.", "write_gates_off");

        var missionId = patch.GetValueOrDefault("mission_id")?.ToString() ?? AnthillRuntime.SystemApiMissionId;
        var taskId = patch.GetValueOrDefault("task_id")?.ToString();
        var filePath = patch.GetValueOrDefault("file_path")?.ToString() ?? "";

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
