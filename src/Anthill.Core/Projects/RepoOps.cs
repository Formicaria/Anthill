using System.Diagnostics;

namespace Anthill.Core.Projects;

/// <summary>
/// v0.3.8.51 (field report) — GIT AWARENESS. The colony applied patches and the operator asked,
/// reasonably, why nothing hit git: Anthill treated every working directory as an anonymous
/// folder. This helper is the single place the platform asks a directory what it is (a repo on a
/// branch with dirty files, or just a folder) and the single place anything commits through.
///
/// Design constraints, in order:
///   - NEVER throw. A missing git binary, a non-repo folder, a timeout — all degrade to
///     "not a repo" or a failed (Ok=false, Message) result. Git absence must not break Anthill.
///   - Deterministic identity. Commits made through Anthill are attributed to the actor who
///     caused them (operator name or "bypass-policy(...)") with a fixed anthill email, via -c
///     overrides — never silently borrowing whatever user.name the machine happens to carry.
///   - Bounded. Every git call gets a hard timeout; describe output is capped.
/// </summary>
public static class RepoOps
{
    public sealed record RepoState(bool IsRepo, string? Branch, int DirtyCount,
        IReadOnlyList<(string Status, string Path)> Dirty, string? LastCommit, string? Error);

    private const int TimeoutMs = 8000;

    /// <summary>Run one git command in <paramref name="root"/>. Never throws.</summary>
    internal static (bool Ok, string Output) Git(string root, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                // v0.3.8.53: the desktop shell is a WinExe — without this every git call the files
                // pane polls opened its own console window, a cascade of flashing CMD boxes.
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return (false, "git could not be started");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(TimeoutMs)) { try { p.Kill(entireProcessTree: true); } catch { } return (false, "git timed out"); }
            return (p.ExitCode == 0, p.ExitCode == 0 ? stdout.TrimEnd() : (stderr + stdout).Trim());
        }
        catch (Exception e)
        {
            // Typically: git is not installed. That is a fact about the machine, not a failure.
            return (false, $"git unavailable: {e.Message}");
        }
    }

    /// <summary>What IS this directory? A repo (branch, dirty files, last commit) or a plain folder.</summary>
    public static RepoState Describe(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new RepoState(false, null, 0, Array.Empty<(string, string)>(), null, "no such directory");

        var (isRepo, probe) = Git(root!, "rev-parse", "--is-inside-work-tree");
        if (!isRepo || !probe.StartsWith("true", StringComparison.OrdinalIgnoreCase))
            return new RepoState(false, null, 0, Array.Empty<(string, string)>(), null,
                probe.Contains("unavailable") ? probe : null);

        // v0.3.8.52 (fourth field round): these calls used to DISCARD their Ok flags, so a repo
        // with no commits yet — where `rev-parse HEAD` and `log -1` both print a fatal — wore
        // git's stderr as its BRANCH NAME in the files pane, a paragraph long, shoving every
        // toolbar button off screen. symbolic-ref answers the branch even on an unborn HEAD;
        // rev-parse remains the fallback for detached heads; and a failure anywhere is null,
        // never stderr passed off as data.
        var (brOk, branchOut) = Git(root!, "symbolic-ref", "--short", "HEAD");
        if (!brOk) (brOk, branchOut) = Git(root!, "rev-parse", "--abbrev-ref", "HEAD");
        var branch = brOk && branchOut.Length > 0 && branchOut.Length <= 200 && !branchOut.Contains('\n')
            ? branchOut.Trim() : null;

        var (lastOk, last) = Git(root!, "log", "-1", "--format=%h %s");
        var lastCommit = lastOk && !string.IsNullOrWhiteSpace(last) ? last.Split('\n')[0].Trim() : null;

        var (stOk, status) = Git(root!, "status", "--porcelain");
        var dirty = new List<(string, string)>();
        if (stOk)
            foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(200))
                if (line.Length > 3) dirty.Add((line[..2].Trim(), line[3..].Trim()));
        return new RepoState(true, branch, dirty.Count, dirty, lastCommit, null);
    }

    /// <summary>
    /// v0.3.8.52 (fourth field round): make the working directory a repository — the operator's
    /// explicit act from the files pane, offered only when the directory is not one already.
    /// Prefers an initial branch named main; falls back for gits that predate --initial-branch.
    /// </summary>
    public static (bool Ok, string Output) Init(string root)
    {
        var r = Git(root, "init", "-b", "main");
        return r.Ok ? r : Git(root, "init");
    }

    /// <summary>The commit hash HEAD resolves to, or null (unborn HEAD, not a repo, no git).
    /// v0.3.8.53 (audit Phase 7): the direct-agent lane records the BASE revision its changes
    /// were made against, and a base that cannot be named is recorded as exactly that.</summary>
    public static string? Head(string root)
    {
        var (ok, output) = Git(root, "rev-parse", "HEAD");
        return ok && output.Length >= 7 && !output.Contains('\n') ? output.Trim() : null;
    }

    /// <summary>The root of the repository that owns <paramref name="dir"/>, or null if none does.
    /// This is how the commit hook finds the right repo for an applied file instead of assuming
    /// the project root and the repo root coincide.</summary>
    public static string? TopLevel(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
        var (ok, output) = Git(dir, "rev-parse", "--show-toplevel");
        return ok && output.Length > 0 ? Path.GetFullPath(output.Trim()) : null;
    }

    /// <summary>The uncommitted diff (HEAD → working tree) for one file, or for everything when
    /// <paramref name="relativePath"/> is empty. (Ok=false, Message) when git refuses.</summary>
    public static (bool Ok, string Output) DiffFile(string root, string relativePath)
        => string.IsNullOrWhiteSpace(relativePath)
            ? Git(root, "diff", "HEAD")
            : Git(root, "diff", "HEAD", "--", relativePath);

    // ---- the commit train (v0.3.8.52) ---------------------------------------------------------

    public sealed record RepoCommit(string Hash, string Author, long Time, string Subject);

    /// <summary>
    /// A ref name that is safe to hand to git as an ARGUMENT: never option-shaped (no leading
    /// dash) and drawn from the character set branch names actually use. Everything git runs
    /// through here goes via ArgumentList — there is no shell — so this guard is specifically
    /// against ref-lookalike OPTIONS, the one injection that survives argument vectors.
    /// </summary>
    public static bool SafeRef(string s) =>
        !string.IsNullOrWhiteSpace(s) && s.Length <= 200 && s[0] != '-'
        && s.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '/' or '-');

    /// <summary>All branches (local and remote-tracking, HEAD pointers dropped) + the current one.</summary>
    public static (string? Current, IReadOnlyList<string> Branches) Branches(string root)
    {
        // Same unborn-HEAD honesty as Describe: symbolic-ref first, and a failure is null.
        var (curOk, current) = Git(root, "symbolic-ref", "--short", "HEAD");
        if (!curOk) (curOk, current) = Git(root, "rev-parse", "--abbrev-ref", "HEAD");
        if (!curOk || current.Contains('\n')) current = "";
        var (ok, output) = Git(root, "branch", "--all", "--format=%(refname:short)");
        var list = new List<string>();
        if (ok)
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var b = line.Trim();
                if (b.Length == 0 || b.EndsWith("/HEAD", StringComparison.Ordinal) || !SafeRef(b)) continue;
                if (!list.Contains(b)) list.Add(b);
            }
        return (string.IsNullOrWhiteSpace(current) ? null : current.Trim(), list.Take(100).ToList());
    }

    /// <summary>
    /// The commit train: recent commits touching <paramref name="relativePath"/> (or the whole
    /// repo when empty) on <paramref name="branch"/> (or HEAD when null/unsafe — an unsafe ref is
    /// silently treated as absent rather than passed through). Follows renames for a single file.
    /// </summary>
    public static IReadOnlyList<RepoCommit> Log(string root, string? branch, string? relativePath, int limit = 20)
    {
        var args = new List<string> { "log", "-n", Math.Clamp(limit, 1, 100).ToString(),
            "--format=%h\u001f%an\u001f%ct\u001f%s" };
        if (!string.IsNullOrWhiteSpace(branch) && SafeRef(branch!)) args.Insert(1, branch!);
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            args.Add("--follow");
            args.Add("--");
            args.Add(relativePath!);
        }
        var (ok, output) = Git(root, args.ToArray());
        if (!ok) return Array.Empty<RepoCommit>();
        var commits = new List<RepoCommit>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\u001f');
            if (f.Length < 4) continue;
            commits.Add(new RepoCommit(f[0], f[1], long.TryParse(f[2], out var t) ? t : 0, f[3]));
        }
        return commits;
    }

    /// <summary>One commit's change to one file (or the whole commit when no path) — the train's
    /// on-click diff. The hash is validated to be hash-shaped before git ever sees it.</summary>
    public static (bool Ok, string Output) ShowCommit(string root, string hash, string? relativePath)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(hash ?? "", "^[0-9a-fA-F]{4,40}$"))
            return (false, "That is not a commit hash.");
        return string.IsNullOrWhiteSpace(relativePath)
            ? Git(root, "show", hash!)
            : Git(root, "show", hash!, "--", relativePath!);
    }

    /// <summary>
    /// Stage and commit. Empty <paramref name="paths"/> means stage everything (-A); otherwise
    /// only the named paths (relative to root) are staged — an applied patch commits ITS file,
    /// not whatever else the tree happens to carry.
    /// </summary>
    public static (bool Ok, string Message) Commit(string root, IReadOnlyList<string> paths, string message, string actor)
    {
        if (string.IsNullOrWhiteSpace(message)) return (false, "A commit message is required.");
        var state = Describe(root);
        if (!state.IsRepo) return (false, state.Error ?? "Not a git repository.");

        var (addOk, addOut) = paths.Count == 0
            ? Git(root, "add", "-A")
            : Git(root, new[] { "add", "--" }.Concat(paths).ToArray());
        if (!addOk) return (false, $"git add failed: {addOut}");

        // gpgsign is forced OFF: a machine whose global config signs commits would otherwise
        // stall every automated commit on a passphrase prompt no colony can answer.
        var (ok, output) = Git(root,
            "-c", $"user.name={Sanitize(actor)}",
            "-c", "user.email=colony@anthill.local",
            "-c", "commit.gpgsign=false",
            "commit", "-m", message);
        if (!ok && output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
            return (false, "Nothing to commit — the staged paths carry no changes.");
        return (ok, ok ? output : $"git commit failed: {output}");
    }

    private static string Sanitize(string actor)
    {
        var trimmed = (actor ?? "").Trim();
        return trimmed.Length == 0 ? "Anthill" : $"Anthill ({trimmed})";
    }
}
