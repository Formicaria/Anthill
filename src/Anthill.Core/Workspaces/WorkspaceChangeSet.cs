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
    /// v0.3.8.96 — the agent-CLI settings file ANTHILL itself materializes into a working
    /// directory (<c>AgentCliCatalog.LocalSettingsRelativePath</c>, duplicated here because Core
    /// cannot reference the provider module; a test holds the two strings equal). The live
    /// qualification run found it riding in EVERY captured change set: the colony wrote its own
    /// scaffolding into the worktree, then diffed the worktree, then proposed its scaffolding to
    /// the operator as the mission's work — where it tripped the soldier's script rule and, on
    /// approval, would have been applied into the operator's repository. Scaffolding is not work,
    /// and the producer that put it there is the one that must not count it.
    /// </summary>
    public const string AgentSettingsRelativePath = ".claude/settings.local.json";

    /// <summary>True when <paramref name="relativePath"/> is the colony's own materialized agent
    /// scaffolding rather than mission work. Separator-insensitive; never throws.</summary>
    public static bool IsColonyScaffolding(string? relativePath) =>
        !string.IsNullOrWhiteSpace(relativePath)
        && string.Equals(relativePath.Trim().Trim('"').Replace('\\', '/'),
            AgentSettingsRelativePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What one capture produced: the set, and every change that could NOT be represented in it.
    /// v0.3.8.97. <see cref="Faithful"/> false means the set is a PARTIAL account of the worktree
    /// and must not be proposed — a reviewer approving it would be approving a description that
    /// silently omits work the agent did. Callers refuse loudly on an unfaithful capture; nothing
    /// may quietly propose the representable subset.
    /// </summary>
    public sealed record CaptureResult(PatchSet Set, IReadOnlyList<string> Problems)
    {
        public bool Faithful => Problems.Count == 0;
    }

    /// <summary>
    /// Build the change set for <paramref name="workspace"/>.
    ///
    /// Returns an empty, FAITHFUL set when nothing changed, rather than null: "the mission ran and
    /// changed nothing" is a real, reportable outcome and collapsing it into an error would make a
    /// legitimately no-op mission look broken.
    ///
    /// v0.3.8.97 — FAITHFUL OR LOUD. This producer used to drop what it could not represent:
    /// deletions were skipped by a `continue`, a rename decayed into an Add of the destination with
    /// the source left in place, an oversized or unreadable file vanished with a `return null`, and
    /// a git failure returned an empty set indistinguishable from a clean tree. Every one of those
    /// was a change set that reviewed clean and described the worktree wrongly. Deletes and renames
    /// are now first-class proposals (ApplyPatchTool has applied both since v0.3.8.52); everything
    /// still unrepresentable — and every read that failed — lands in
    /// <see cref="CaptureResult.Problems"/> instead of nowhere.
    /// </summary>
    public static CaptureResult Create(MissionWorkspace workspace, string missionId, string taskId, string summary)
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
        var problems = new List<string>();

        if (workspace is null || !workspace.Usable || !Directory.Exists(workspace.Root))
            return new CaptureResult(set, problems);

        var against = workspace.BaseRevision.Length > 0 ? workspace.BaseRevision : "HEAD";

        // --name-status rather than a unified diff: the pipeline wants whole-file old and new
        // content, not hunks, and reconstructing files from a patch would be a second, subtly
        // different implementation of something git already does exactly.
        // --find-renames explicitly: rename detection must not depend on the operator's diff.renames
        // config — a tree where it is off would report every rename as D+A under one config and
        // R under another, and a capture whose shape depends on user config is not deterministic.
        var (ok, status) = Git(workspace.Root, $"diff --name-status --find-renames {against} --");
        if (!ok)
        {
            // v0.3.8.97 — a diff that failed is NOT a clean tree. Returning the empty set here made
            // the two indistinguishable, which is the exact "silently dropped" shape this release
            // removes.
            problems.Add($"git diff --name-status failed in {workspace.Root}: {status}");
            return new CaptureResult(set, problems);
        }

        foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var code = parts[0].Trim();
            var path = parts[^1].Trim();

            // v0.3.8.96: the colony's own materialized scaffolding is never mission work.
            if (IsColonyScaffolding(path)) continue;

            switch (code[0])
            {
                case 'A':
                    Add(set, problems, WorktreeProposal(workspace, against, path, added: true));
                    break;

                case 'M':
                    Add(set, problems, WorktreeProposal(workspace, against, path, added: false));
                    break;

                case 'D':
                    // A first-class DELETE proposal, anchored to the base content so the applier's
                    // base-hash rule can refuse a stale delete exactly as it refuses a stale write.
                    Add(set, problems, BaseAnchoredProposal(workspace, against, path,
                        PatchChangeType.Delete, destination: null));
                    break;

                case 'R':
                {
                    // R<similarity>\told\tnew. A PURE rename (R100) is one Rename proposal — the
                    // move the agent actually made. A rename WITH edits decomposes into the two
                    // operations it truly is — delete of the source, add of the destination with
                    // the worktree's content — because apply_patch's rename moves bytes verbatim
                    // and pretending an edited move is a pure one would apply the OLD content
                    // under the new name.
                    var source = parts.Length >= 3 ? parts[1].Trim() : "";
                    if (source.Length == 0)
                    {
                        problems.Add($"rename entry '{line.Trim()}' names no source path");
                        break;
                    }
                    if (IsColonyScaffolding(source)) break;

                    if (string.Equals(code, "R100", StringComparison.Ordinal))
                        Add(set, problems, BaseAnchoredProposal(workspace, against, source,
                            PatchChangeType.Rename, destination: path));
                    else
                    {
                        Add(set, problems, BaseAnchoredProposal(workspace, against, source,
                            PatchChangeType.Delete, destination: null));
                        Add(set, problems, WorktreeProposal(workspace, against, path, added: true));
                    }
                    break;
                }

                case 'C':
                    // A copy leaves its source intact, so the destination is genuinely new work.
                    Add(set, problems, WorktreeProposal(workspace, against, path, added: true));
                    break;

                default:
                    // T (typechange), U (unmerged), X and anything git grows later: unrepresentable
                    // in the proposal vocabulary, and therefore LOUD rather than absent.
                    problems.Add($"unsupported change '{code}' for {path} — this change cannot be "
                        + "represented as a patch proposal and the capture refuses to drop it silently");
                    break;
            }
        }

        // Untracked files are genuine new work — a mission that creates a file has not committed it,
        // so it appears nowhere in a diff against the base and would be silently dropped.
        var (untrackedOk, untracked) = Git(workspace.Root, "ls-files --others --exclude-standard");
        if (!untrackedOk)
            problems.Add($"git ls-files --others failed in {workspace.Root}: {untracked}");
        else
            foreach (var path in untracked.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // v0.3.8.96: the settings file is created untracked, so this loop is where it
                // actually entered every change set. Same rule, both discovery paths.
                if (IsColonyScaffolding(path)) continue;
                Add(set, problems, WorktreeProposal(workspace, against, path.Trim(), added: true));
            }

        return new CaptureResult(set, problems);
    }

    private static void Add(PatchSet set, List<string> problems,
        (PatchProposal? Proposal, string? Problem) produced)
    {
        if (produced.Proposal is not null) set.Proposals.Add(produced.Proposal);
        if (produced.Problem is not null) problems.Add(produced.Problem);
    }

    /// <summary>An Add/Modify proposal whose new content is the WORKTREE's bytes. A file that is
    /// missing, oversized, or unreadable is a named problem, never a silent null. v0.3.8.97.</summary>
    private static (PatchProposal? Proposal, string? Problem) WorktreeProposal(
        MissionWorkspace workspace, string against, string path, bool added)
    {
        string newContent;
        try
        {
            var full = Path.Combine(workspace.Root, path);
            if (!File.Exists(full))
                return (null, $"{path}: listed as changed and absent from the worktree");
            if (new FileInfo(full).Length > MaxProposalChars)
                return (null, $"{path}: exceeds the {MaxProposalChars}-character proposal cap "
                    + "(binary, bundle, or fixture) and cannot be proposed for review");
            newContent = File.ReadAllText(full);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, $"{path}: unreadable during capture — {error.Message}");
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

        return (new PatchProposal
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
        }, null);
    }

    /// <summary>A Delete or Rename proposal anchored to the BASE revision's content, which is the
    /// only text the applier's stale-base refusal can honestly compare against. A base that cannot
    /// be read is a named problem — a destructive proposal with no base would either be refused
    /// downstream for a reason the reviewer could not predict, or worse, applied blind. v0.3.8.97.</summary>
    private static (PatchProposal? Proposal, string? Problem) BaseAnchoredProposal(
        MissionWorkspace workspace, string against, string path, PatchChangeType changeType,
        string? destination)
    {
        var (ok, original) = Git(workspace.Root, $"show {against}:{path}");
        if (!ok)
            return (null, $"{path}: base content could not be read for its "
                + $"{changeType.ToString().ToLowerInvariant()} proposal — {original}");

        return (new PatchProposal
        {
            FilePath = path,
            ChangeType = changeType,
            NewContent = null,
            OldContent = original,
            DestinationPath = destination,
            BaseHash = PatchApply.HashOf(original),
            Reason = $"Produced in mission workspace {workspace.Id}, based on {Short(against)}",
            Risk = "medium",
            RequiresApproval = true,
        }, null);
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

        // -uall: individual FILES, never a collapsed "dir/" entry — a directory whose content is
        // entirely untracked would otherwise appear as one opaque path the scaffolding exclusion
        // below cannot see into (found by this method's own test: .claude/ hid the settings file).
        var (ok, output) = Git(workspaceRoot, "status --porcelain -uall");
        if (!ok) return Array.Empty<string>();

        var paths = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Porcelain v1: two status columns, a space, then the path ("R old -> new" for renames;
            // the NEW path is the one that exists to review).
            var entry = line.Length > 3 ? line[3..].Trim() : "";
            var arrow = entry.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) entry = entry[(arrow + 4)..].Trim();
            // v0.3.8.96: the acting coder's success is judged by this list, and the colony's own
            // materialized settings file must not be able to make an idle turn look like work.
            if (IsColonyScaffolding(entry)) continue;
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
