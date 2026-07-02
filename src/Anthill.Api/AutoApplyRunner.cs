using System.Diagnostics;
using System.Text;
using Anthill.Core.Autonomy;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Orchestration;

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

        // Auto-apply writes to disk — it can't do anything unless the write gates are also on.
        if (!AnthillRuntime.EnablePatchApplication || !AnthillRuntime.EnableFileWriting)
        {
            queen.Memory.LogEvent(SystemMissionId, "autonomy_autoapply_skipped",
                "Auto-apply is enabled but the write gates (patch_application_enabled / file_writing_enabled) are off.",
                antName: "director", metadata: new() { ["reason"] = "write_gates_off", ["mission_id"] = missionId });
            return;
        }

        // Candidate patches: still-proposed proposals from this mission.
        var candidates = queen.Memory.ListPatchProposalsForMission(missionId)
            .Where(p => (p.GetValueOrDefault("status")?.ToString() ?? "") == PatchStatus.Proposed.Value())
            .Select(p => p.GetValueOrDefault("id")?.ToString() ?? "")
            .Where(id => id.Length > 0)
            .ToList();
        if (candidates.Count == 0) return;

        var eligible = new List<(string PatchId, string? TaskId)>();
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
                NewContent = full.GetValueOrDefault("new_content") as string,
            };
            var decision = AutoApplyPolicy.Evaluate(proposal);
            if (decision.Eligible) eligible.Add((patchId, full.GetValueOrDefault("task_id")?.ToString()));
            else
                queen.Memory.LogEvent(missionId, "autonomy_autoapply_ineligible",
                    $"Patch not eligible for auto-apply: {proposal.FilePath} — {decision.Reason}", full.GetValueOrDefault("task_id")?.ToString(), "director",
                    metadata: new() { ["patch_id"] = patchId, ["file_path"] = proposal.FilePath, ["reason"] = decision.Reason });
        }
        if (eligible.Count == 0) return;

        queen.Memory.LogEvent(missionId, "autonomy_autoapply_started",
            $"Director auto-applying {eligible.Count} eligible patch(es), then verifying.", antName: "director",
            metadata: new() { ["mission_id"] = missionId, ["eligible_count"] = eligible.Count });

        // Apply each eligible patch, remembering enough to roll back.
        var applied = new List<Queen.AutoApplyOutcome>();
        foreach (var (patchId, taskId) in eligible)
        {
            var outcome = queen.ApplyPatchForAutomation(patchId, missionId, taskId);
            if (outcome.Success) applied.Add(outcome);
            else
                queen.Memory.LogEvent(missionId, "autonomy_autoapply_apply_failed",
                    $"Auto-apply could not write patch {outcome.FilePath}: {outcome.Error}", taskId, "director",
                    metadata: new() { ["patch_id"] = patchId, ["error"] = outcome.Error });
        }
        if (applied.Count == 0) return;

        // Verify: the change must still build + test green, or every applied patch is reverted.
        var verify = RunVerify();
        if (verify.Green)
        {
            foreach (var a in applied)
            {
                // Consume the human approval that would otherwise sit in the queue for this patch.
                var approval = queen.Memory.GetApprovalForTarget(a.PatchId);
                if (approval is not null)
                    queen.Memory.UpdateApprovalStatus(approval.GetValueOrDefault("id")?.ToString() ?? "",
                        ApprovalStatus.Consumed, "Auto-applied by the Director and verified green.");
            }
            var committed = AnthillRuntime.AutonomyAutoApplyGitCommit && GitCommit(applied, out var commitNote);
            queen.Memory.LogEvent(missionId, "autonomy_autoapply_verified",
                $"Verify passed — kept {applied.Count} auto-applied patch(es).", antName: "director",
                metadata: new()
                {
                    ["mission_id"] = missionId, ["kept_count"] = applied.Count, ["verify_exit"] = verify.ExitCode,
                    ["verify_seconds"] = verify.Seconds, ["git_committed"] = committed,
                    ["files"] = applied.Select(a => a.FilePath).ToList(),
                });
        }
        else
        {
            // Roll back in reverse apply order.
            var reason = verify.TimedOut ? "verify timed out" : $"verify failed (exit {verify.ExitCode})";
            for (var i = applied.Count - 1; i >= 0; i--)
                queen.RollbackAutoApplied(applied[i], missionId, null, reason);
            queen.Memory.LogEvent(missionId, "autonomy_autoapply_reverted",
                $"Verify FAILED — rolled back all {applied.Count} auto-applied patch(es).", antName: "director",
                metadata: new()
                {
                    ["mission_id"] = missionId, ["reverted_count"] = applied.Count, ["verify_exit"] = verify.ExitCode,
                    ["timed_out"] = verify.TimedOut, ["verify_tail"] = Tail(verify.Output, 1500),
                });
        }
    }

    private sealed record VerifyResult(bool Green, int ExitCode, bool TimedOut, double Seconds, string Output);

    /// <summary>Runs the verify step in the workspace root: the operator command, or built-in dotnet build+test.</summary>
    private static VerifyResult RunVerify()
    {
        var cmd = string.IsNullOrWhiteSpace(AnthillRuntime.AutonomyAutoApplyVerifyCmd)
            ? "dotnet build && dotnet test"
            : AnthillRuntime.AutonomyAutoApplyVerifyCmd;
        var dir = Directory.Exists(AnthillRuntime.AllowedWorkspaceRoot)
            ? Path.GetFullPath(AnthillRuntime.AllowedWorkspaceRoot) : Environment.CurrentDirectory;
        var (exit, output, timedOut, seconds) = RunShell(cmd, dir, AnthillRuntime.AutonomyAutoApplyVerifyTimeout);
        return new VerifyResult(!timedOut && exit == 0, exit, timedOut, seconds, output);
    }

    /// <summary>git add + commit the applied files locally (never pushed). Returns false on any error.</summary>
    private static bool GitCommit(List<Queen.AutoApplyOutcome> applied, out string note)
    {
        note = "";
        var dir = Directory.Exists(AnthillRuntime.AllowedWorkspaceRoot)
            ? Path.GetFullPath(AnthillRuntime.AllowedWorkspaceRoot) : Environment.CurrentDirectory;
        var files = string.Join(" ", applied.Select(a => "\"" + (a.ResolvedPath ?? a.FilePath).Replace("\"", "") + "\""));
        var msg = $"ANTHILL auto-applied {applied.Count} verified patch(es) [autonomy]";
        var (exit, output, timedOut, _) = RunShell($"git add {files} && git commit -m \"{msg}\"", dir, 60);
        note = Tail(output, 300);
        return !timedOut && exit == 0;
    }

    private static (int Exit, string Output, bool TimedOut, double Seconds) RunShell(string command, string dir, int timeoutSeconds)
    {
        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir,
        };
        if (isWindows) { psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(command); }
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

    private static string Tail(string s, int max) =>
        s.Length <= max ? s.TrimEnd() : "…" + s[^max..].TrimEnd();
}
