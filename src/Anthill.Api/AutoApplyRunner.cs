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

        var verifyCmdConfigured = !string.IsNullOrWhiteSpace(AnthillRuntime.AutonomyAutoApplyVerifyCmd);
        var verifyDescription = verifyCmdConfigured ? AnthillRuntime.AutonomyAutoApplyVerifyCmd
            : (AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify ? "(none — keep without verify)" : "dotnet build && dotnet test");
        var workspace = Directory.Exists(AnthillRuntime.AllowedWorkspaceRoot)
            ? Path.GetFullPath(AnthillRuntime.AllowedWorkspaceRoot) : AnthillRuntime.AllowedWorkspaceRoot;
        queen.Memory.LogEvent(missionId, "autonomy_autoapply_started",
            $"Director auto-applying {eligible.Count} eligible patch(es), then verifying with: {verifyDescription}.", antName: "director",
            metadata: new() { ["mission_id"] = missionId, ["eligible_count"] = eligible.Count, ["verify_cmd"] = verifyDescription, ["workspace"] = workspace });

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

        // v1.8.21 fix: on a deployment with no build toolchain, the default `dotnet build && dotnet test`
        // verify always fails and every applied patch is rolled back — so auto-apply never persists. When
        // the operator has explicitly opted in (autonomy_autoapply_keep_without_verify) AND set no verify
        // command, keep the applied patches instead of running (and failing) the built-in verify.
        if (!verifyCmdConfigured && AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify)
        {
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
            KeepApplied(queen, missionId, applied,
                "autonomy_autoapply_verified",
                $"Verify passed — kept {applied.Count} auto-applied patch(es).",
                new() { ["verify_exit"] = verify.ExitCode, ["verify_seconds"] = verify.Seconds });
        }
        else
        {
            // Roll back in reverse apply order.
            var reason = verify.TimedOut ? "verify timed out" : $"verify failed (exit {verify.ExitCode})";
            for (var i = applied.Count - 1; i >= 0; i--)
                queen.RollbackAutoApplied(applied[i], missionId, null, reason);
            queen.Memory.LogEvent(missionId, "autonomy_autoapply_reverted",
                $"Verify FAILED ({reason}) — rolled back all {applied.Count} auto-applied patch(es). " +
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
    /// Finalize a set of kept (not rolled-back) auto-applied patches: consume the human approvals that
    /// would otherwise sit in the queue, optionally git-commit locally, and log the outcome. Shared by
    /// the verify-green path and the keep-without-verify path (v1.8.21).
    /// </summary>
    private static void KeepApplied(Queen queen, string missionId, List<Queen.AutoApplyOutcome> applied,
        string eventType, string message, Dictionary<string, object?>? extra = null)
    {
        foreach (var a in applied)
        {
            var approval = queen.Memory.GetApprovalForTarget(a.PatchId);
            if (approval is not null)
                queen.Memory.UpdateApprovalStatus(approval.GetValueOrDefault("id")?.ToString() ?? "",
                    ApprovalStatus.Consumed, "Auto-applied by the Director and kept.");
        }
        var committed = false;
        if (AnthillRuntime.AutonomyAutoApplyGitCommit)
        {
            committed = GitCommit(applied, out var commitNote);
            if (!committed)
                queen.Memory.LogEvent(missionId, "autonomy_autoapply_git_failed",
                    $"Kept the applied patch(es) on disk but the local git commit failed: {commitNote}", antName: "director",
                    metadata: new() { ["mission_id"] = missionId, ["note"] = commitNote });
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
    internal static VerifyResult RunVerify()
    {
        var cmd = string.IsNullOrWhiteSpace(AnthillRuntime.AutonomyAutoApplyVerifyCmd)
            ? "dotnet build && dotnet test"
            : AnthillRuntime.AutonomyAutoApplyVerifyCmd;
        var dir = Directory.Exists(AnthillRuntime.AllowedWorkspaceRoot)
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
        var files = string.Join(" ", applied.Select(a => "\"" + (a.ResolvedPath ?? a.FilePath).Replace("\"", "") + "\""));
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
            if (fx != 0) { RunShell("git merge --abort", dir, 20); note = "kept + committed; sync with main skipped: " + Tail(fo, 150); }
            // Push ONLY the standalone branch (never main); no force.
            var (px, po, _, _) = RunShell($"{env}git push {remote} {branch}", dir, 120);
            if (px != 0) note = (note.Length > 0 ? note + " | " : "") + "committed locally but push failed: " + Tail(po, 200);
        }
        return true;
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

    internal static string Tail(string s, int max) =>
        s.Length <= max ? s.TrimEnd() : "…" + s[^max..].TrimEnd();
}
