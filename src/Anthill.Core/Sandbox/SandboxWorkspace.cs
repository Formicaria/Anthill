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
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceRoot, file);
            if (rel.StartsWith(".git") || rel.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || rel.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (++copied > maxCopyFiles) break; // bounded — a sandbox is not a backup
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        return new SandboxWorkspace(target, "copy", sourceRoot);
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
            var src = Path.GetFullPath(Path.Combine(Root, rel));
            if (!src.StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase)) continue; // no traversal
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
            p.WaitForExit(60_000);
            return (p.ExitCode == 0, p.ExitCode == 0 ? stdout : stderr);
        }
        catch (Exception e) { return (false, e.Message); }
    }
}
