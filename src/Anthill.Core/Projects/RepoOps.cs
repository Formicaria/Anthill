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

        var (_, branch) = Git(root!, "rev-parse", "--abbrev-ref", "HEAD");
        var (_, last) = Git(root!, "log", "-1", "--format=%h %s");
        var (stOk, status) = Git(root!, "status", "--porcelain");
        var dirty = new List<(string, string)>();
        if (stOk)
            foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(200))
                if (line.Length > 3) dirty.Add((line[..2].Trim(), line[3..].Trim()));
        return new RepoState(true, branch, dirty.Count, dirty, string.IsNullOrWhiteSpace(last) ? null : last, null);
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
