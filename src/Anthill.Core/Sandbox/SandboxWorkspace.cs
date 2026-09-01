using System.Diagnostics;
using System.Text;

namespace Anthill.Core.Sandbox;

/// <summary>
/// V2.10.0 (NORTH_STAR V3-track Phase 3) — a DISPOSABLE workspace for iterative agent work.
/// Autonomous code changes never touch the live production checkout: work happens in a git
/// worktree (preferred — cheap, exact HEAD state) or a bounded file copy when git is unavailable,
/// artifacts are harvested explicitly, and everything is destroyed on dispose. Creation is
/// deterministic C# — no model participates in workspace lifecycle.
/// </summary>
public sealed class SandboxWorkspace : IDisposable
{
    public string Root { get; }
    public string Mode { get; }              // worktree | copy
    public string SourceRoot { get; }
    private readonly bool _isWorktree;
    private bool _disposed;

    private SandboxWorkspace(string root, string mode, string sourceRoot)
    {
        Root = root; Mode = mode; SourceRoot = sourceRoot; _isWorktree = mode == "worktree";
    }

    /// <summary>Create a sandbox from <paramref name="sourceRoot"/>. Uses a git worktree when the
    /// source is a git checkout; falls back to a bounded copy (small text-first repos) otherwise.</summary>
    public static SandboxWorkspace Create(string sourceRoot, int maxCopyFiles = 5000, bool preferCopy = false)
    {
        var target = Path.Combine(Path.GetTempPath(), "anthill-sandbox-" + Guid.NewGuid().ToString("N")[..12]);
        // preferCopy: patch verification must see the workspace AS IT IS ON DISK (a worktree of
        // HEAD would miss uncommitted local changes the patch may have been diffed against).
        if (!preferCopy && Directory.Exists(Path.Combine(sourceRoot, ".git")))
        {
            var (ok, err) = Git(sourceRoot, $"worktree add --detach \"{target}\" HEAD");
            if (ok) return new SandboxWorkspace(target, "worktree", sourceRoot);
            // fall through to copy on any git failure — never block on tooling, never use live root
            Console.Error.WriteLine($"[sandbox] worktree failed ({err}); falling back to copy");
        }
        Directory.CreateDirectory(target);
        var copied = 0;
        var truncated = false;
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceRoot, file);
            if (IsExcluded(rel)) continue;
            if (++copied > maxCopyFiles)
            {
                // v0.3.8.104 — the bound still holds and it no longer holds SILENTLY. This was a
                // bare `break`, so a repository over the bound produced a sandbox missing an
                // arbitrary, filesystem-order-dependent set of files — and the only symptom was a
                // build or test failure inside the sandbox that said nothing about why. Different
                // enumeration order on a different OS means a different set is missing, which is
                // the exact shape of a failure that reproduces on one machine and not another.
                truncated = true;
                break;
            }
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }

        var sandbox = new SandboxWorkspace(target, "copy", sourceRoot) { Truncated = truncated };
        if (truncated)
            Console.Error.WriteLine(
                $"[sandbox] TRUNCATED: {sourceRoot} has more than {maxCopyFiles} eligible files, so "
              + "this sandbox is an incomplete copy. Any build or test run inside it is measuring a "
              + "tree that does not exist. Raise maxCopyFiles or use a git worktree.");
        return sandbox;
    }

    /// <summary>
    /// TRUE when this sandbox is an INCOMPLETE copy of its source. v0.3.8.104.
    ///
    /// Never true for a worktree sandbox, which is complete by construction. A caller that verifies
    /// anything inside a truncated sandbox is measuring a tree that does not exist anywhere, so the
    /// materializer refuses rather than reporting the result.
    /// </summary>
    public bool Truncated { get; private init; }

    /// <summary>
    /// v0.3.8.104 — build outputs and git metadata, excluded at ANY depth including the top level.
    ///
    /// The previous test asked whether the relative path CONTAINED `{sep}bin{sep}`, which a
    /// top-level `bin\Foo.dll` does not: its relative path has no leading separator, so a build
    /// output directory at the repository root was copied into every sandbox. Segment comparison
    /// answers the question the check was always trying to ask.
    /// </summary>
    private static bool IsExcluded(string relativePath)
    {
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Copy selected artifacts OUT of the sandbox (relative paths). This is the only
    /// sanctioned way work leaves the sandbox — and it lands in a caller-chosen directory, never
    /// automatically in the live checkout.</summary>
    public List<string> Harvest(IEnumerable<string> relativePaths, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        var harvested = new List<string>();
        foreach (var rel in relativePaths)
        {
            // v0.3.8.59 (PLAN.md §1b S1): the same missing-separator comparison as the Files pane,
            // on the path work takes to LEAVE the sandbox. Harvest is the one sanctioned exit, so a
            // link planted in the sandbox — by the agent that works there — could name a file
            // outside it and have the colony copy it out for the operator.
            var containment = Security.PathContainment.Resolve(Root, rel);
            if (!containment.Allowed) continue;                      // no traversal, no link escape
            var src = containment.Path;
            if (!File.Exists(src)) continue;
            var dest = Path.Combine(destinationDir, rel.Replace('/', '_').Replace('\\', '_'));
            File.Copy(src, dest, overwrite: true);
            harvested.Add(dest);
        }
        return harvested;
    }

    /// <summary>Diff summary of what the agent changed inside a worktree sandbox (git-mode only).</summary>
    public string ChangeSummary()
    {
        if (!_isWorktree) return "(copy-mode sandbox: no git diff available)";
        var (ok, output) = Git(Root, "status --porcelain");
        return ok ? (output.Trim().Length == 0 ? "(no changes)" : output.Trim()) : "(diff unavailable)";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_isWorktree)
            {
                Git(SourceRoot, $"worktree remove --force \"{Root}\"");
                Git(SourceRoot, "worktree prune");
            }
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch { /* best-effort cleanup; the temp root is disposable by definition */ }
    }

    private static (bool Ok, string Output) Git(string workdir, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workdir, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,   // v0.3.8.55: children emit UTF-8, not the OS codepage
                StandardErrorEncoding = Encoding.UTF8,
            })!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            // v0.3.8.57 — TIMEOUT KILLS THE TREE.
            //
            // WaitForExit(ms) returns false and execution CARRIED ON: the git process and
            // every child it spawned kept running, and `ExitCode` on a process that has not
            // exited throws — so the timeout was reported as an exception message rather than
            // as a timeout, and "stop means stop" was false for every one of these sites.
            if (!p.WaitForExit(60_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return (false, "git timed out after 60s and was terminated with its child processes");
            }
            return (p.ExitCode == 0, p.ExitCode == 0 ? stdout : stderr);
        }
        catch (Exception e) { return (false, e.Message); }
    }
}
