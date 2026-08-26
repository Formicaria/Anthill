using System.Diagnostics;
using System.Text;
using Anthill.Core.Domain;

namespace Anthill.Core.Workspaces;

/// <summary>
/// v3.5.0 — turn what a mission changed in its workspace into a reviewable change set.
///
/// This closes the loop the phase opened. An agent now works in an isolated worktree it cannot
/// escape, which is safe but useless on its own: the work has to reach the operator somehow, and the
/// only sanctioned route into the live checkout is the patch/approval pipeline that already exists.
/// So this produces a <see cref="PatchSet"/> of ordinary <see cref="PatchProposal"/>s — the same
/// type the Patch Center already reviews, approves and applies. No second review path.
///
/// THE DETAIL THAT MATTERS, and it is not obvious: <c>OldContent</c> is read from the BASE REVISION,
/// not from the live checkout. <c>apply_patch</c> does an exact-match replacement, so old content
/// taken from a checkout that has moved on either fails to match — noisily, which is survivable — or
/// matches the wrong occurrence in a file someone else edited, which is not. The base revision is
/// the only text the mission's change was actually derived from, and the manifest recorded it
/// precisely so this moment could be correct.
/// </summary>
public static class WorkspaceChangeSet
{
    /// <summary>Files above this are reported but not proposed — a patch is reviewed by a human.</summary>
    public const int MaxProposalChars = 400_000;

    /// <summary>
    /// Build the change set for <paramref name="workspace"/>.
    ///
    /// Returns an empty set when nothing changed, rather than null: "the mission ran and changed
    /// nothing" is a real, reportable outcome and collapsing it into an error would make a
    /// legitimately no-op mission look broken.
    /// </summary>
    public static PatchSet Create(MissionWorkspace workspace, string missionId, string taskId, string summary)
    {
        var set = new PatchSet
        {
            MissionId = missionId ?? "",
            TaskId = taskId ?? "",
            // v0.3.8.95: attribution — this producer diffs a workspace, so it is the one producer
            // that can name it. Also the idempotence key finalization checks before re-harvesting
            // a workspace the acting-coder path already captured mid-mission.
            WorkspaceId = workspace?.Id,
            Summary = summary ?? "",
        };

        if (workspace is null || !workspace.Usable || !Directory.Exists(workspace.Root)) return set;

        var against = workspace.BaseRevision.Length > 0 ? workspace.BaseRevision : "HEAD";

        // --name-status rather than a unified diff: the pipeline wants whole-file old and new
        // content, not hunks, and reconstructing files from a patch would be a second, subtly
        // different implementation of something git already does exactly.
        var (ok, status) = Git(workspace.Root, $"diff --name-status {against} --");
        if (!ok) return set;

        foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var code = parts[0].Trim();
            var path = parts[^1].Trim();

            // Deletions are DESCRIBED but never proposed. ApplyPatchTool supports add and modify
            // only, so emitting a delete proposal would produce a change set that cannot be applied
            // — a review that ends in a failure the reviewer could not have predicted.
            if (code.StartsWith('D')) continue;

            var proposal = Proposal(workspace, against, path, added: code.StartsWith('A'));
            if (proposal is not null) set.Proposals.Add(proposal);
        }

        // Untracked files are genuine new work — a mission that creates a file has not committed it,
        // so it appears nowhere in a diff against the base and would be silently dropped.
        var (_, untracked) = Git(workspace.Root, "ls-files --others --exclude-standard");
        foreach (var path in untracked.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var proposal = Proposal(workspace, against, path.Trim(), added: true);
            if (proposal is not null) set.Proposals.Add(proposal);
        }

        return set;
    }

    private static PatchProposal? Proposal(MissionWorkspace workspace, string against, string path, bool added)
    {
        string newContent;
        try
        {
            var full = Path.Combine(workspace.Root, path);
            if (!File.Exists(full)) return null;
            if (new FileInfo(full).Length > MaxProposalChars) return null;   // binaries, bundles, fixtures
            newContent = File.ReadAllText(full);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // From the BASE REVISION — see the class remarks. `git show <base>:<path>` is the text this
        // change was actually derived from, which is the only text an exact-match replacement can
        // safely be anchored to.
        string? oldContent = null;
        if (!added)
        {
            var (ok, original) = Git(workspace.Root, $"show {against}:{path}");
            if (ok) oldContent = original;
        }

        return new PatchProposal
        {
            FilePath = path,
            ChangeType = added || oldContent is null ? PatchChangeType.Add : PatchChangeType.Modify,
            NewContent = newContent,
            OldContent = oldContent,
            // v0.3.8.37: the base this proposal was built against. This producer READS the file, so
            // it is the one place that can state the base honestly — a model-emitted proposal cannot,
            // and carries null.
            BaseHash = PatchApply.HashOf(oldContent),
            Reason = $"Produced in mission workspace {workspace.Id}, based on {Short(against)}",
            Risk = added ? "low" : "medium",
            // Always. A workspace exists so an agent's work is REVIEWED before it reaches the live
            // checkout; a change set that could apply itself would make the isolation pointless.
            RequiresApproval = true,
        };
    }

    /// <summary>
    /// v0.3.8.95 — the paths the workspace's tree differs in, from porcelain status: tracked
    /// modifications and untracked additions alike, one workspace-relative path each. Deterministic
    /// and cheap, so the acting coder's success can be classified by what is ON DISK rather than by
    /// what the model said it did. Empty on a clean tree, on a missing directory, and on git
    /// failure — a caller that must distinguish those asks the workspace, not this.
    /// </summary>
    public static IReadOnlyList<string> ChangedPaths(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return Array.Empty<string>();

        var (ok, output) = Git(workspaceRoot, "status --porcelain");
        if (!ok) return Array.Empty<string>();

        var paths = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Porcelain v1: two status columns, a space, then the path ("R old -> new" for renames;
            // the NEW path is the one that exists to review).
            var entry = line.Length > 3 ? line[3..].Trim() : "";
            var arrow = entry.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) entry = entry[(arrow + 4)..].Trim();
            if (entry.Length > 0) paths.Add(entry.Trim('"'));
        }
        return paths;
    }

    private static string Short(string revision) => revision.Length > 12 ? revision[..12] : revision;

    private static (bool Ok, string Output) Git(string workdir, string args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workdir, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,   // v0.3.8.55: children emit UTF-8, not the OS codepage
                StandardErrorEncoding = Encoding.UTF8,
            })!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            // v0.3.8.57 — TIMEOUT KILLS THE TREE.
            //
            // WaitForExit(ms) returns false and execution CARRIED ON: the git process and
            // every child it spawned kept running, and `ExitCode` on a process that has not
            // exited throws — so the timeout was reported as an exception message rather than
            // as a timeout, and "stop means stop" was false for every one of these sites.
            if (!process.WaitForExit(60_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return (false, "git timed out after 60s and was terminated with its child processes");
            }
            return (process.ExitCode == 0, process.ExitCode == 0 ? stdout : stderr);
        }
        catch (Exception error)
        {
            return (false, error.Message);
        }
    }
}
