using System.Security.Cryptography;
using System.Text;

namespace Anthill.Core.Workspaces;

/// <summary>
/// WHAT THE WORKSPACE LOOKED LIKE WHEN VERIFICATION READ IT. v0.3.8.91.
///
/// THE HOLE THIS FILLS. Verification runs against a materialized sandbox and binds its evidence to
/// three things: the base revision, the patch-set content hash, and `AppliedTreeHash`. That last one
/// sounds like a tree hash and is not — `HashAppliedTree` iterates only the paths the patch touched.
/// Files the patch did not touch are not in it at all.
///
/// So the reviewer's scenario goes through untouched. Verification compiles a sandbox containing
/// A.cs and B.cs; the patch modifies only A.cs. Somebody edits B.cs in the live tree. At apply time
/// `ApplyPatchTool` resolves A.cs, hashes A.cs, matches its base hash, and writes. The build was
/// proven against a tree that no longer exists and nothing notices — every hash the system holds is
/// about A.cs, and the thing that changed is B.cs.
///
/// A FINGERPRINT OF THE WHOLE WORKING TREE, not of the patch's files. `git rev-parse HEAD` plus the
/// full `git status --porcelain` listing, hashed together: HEAD catches a commit, a checkout, a
/// rebase or a pull; the porcelain listing catches uncommitted edits, staged changes, deletions and
/// new untracked files. Between them they cover "did anything about this working tree change".
///
/// HEAD ALONE WOULD HAVE BEEN THE WRONG CHECK, and it is worth writing down because it was the first
/// design: it does not move when somebody edits a file without committing, which is exactly the case
/// the freshness check exists for. A check named for a property it does not deliver is the defect
/// this repository keeps finding, and it would have been especially bad here — an operator reading
/// "workspace unchanged since verification" would have believed it.
///
/// EMPTY MEANS UNMEASURED, NEVER "UNCHANGED". A workspace that is not a git checkout, or a host with
/// no git, produces an empty fingerprint. Callers must treat that as "cannot tell" — and the
/// promotion gate does: it refuses on a fingerprint that MOVED and stays silent on one that was
/// never captured, which is the same non-retroactive rule the evidence check already follows.
/// </summary>
public static class WorkspaceFingerprint
{
    /// <summary>
    /// Capture the current state of a working tree, or "" when it cannot be measured.
    ///
    /// Never throws: a fingerprint that could not be taken is an absence, and turning a git hiccup
    /// into an exception on the apply path would convert "cannot tell" into "cannot apply".
    /// </summary>
    public static string Capture(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return "";
        if (!Directory.Exists(Path.Combine(workspaceRoot, ".git"))) return "";

        var head = Git(workspaceRoot, "rev-parse HEAD");
        if (head is null) return "";

        // `--porcelain` is the stable, machine-readable form; `-uall` lists untracked files
        // individually rather than collapsing a new directory to one line, so a file added inside an
        // existing untracked directory still moves the fingerprint.
        var status = Git(workspaceRoot, "status --porcelain -uall");
        if (status is null) return "";

        return Sha($"head:{head}\nstatus:\n{status}");
    }

    /// <summary>
    /// Did the tree move between <paramref name="captured"/> and now?
    ///
    /// Three outcomes, and the middle one is the point: unchanged, moved, or unmeasurable. A caller
    /// that collapsed the third into either of the others would be asserting something it does not
    /// know.
    /// </summary>
    public static FreshnessVerdict Compare(string? captured, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(captured)) return FreshnessVerdict.NotCaptured;

        var now = Capture(workspaceRoot);
        if (now.Length == 0) return FreshnessVerdict.Unmeasurable;

        return string.Equals(captured.Trim(), now, StringComparison.Ordinal)
            ? FreshnessVerdict.Unchanged
            : FreshnessVerdict.Moved;
    }

    private static string? Git(string root, string arguments)
    {
        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "git", arguments)
            {
                WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            });
            if (proc is null) return null;

            var output = proc.StandardOutput.ReadToEnd();

            // Kill the tree on timeout rather than reading ExitCode on a live process — v0.3.8.57
            // found five sites that carried on past a timeout and then threw somewhere unrelated.
            if (!proc.WaitForExit(10_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }

            return proc.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    private static string Sha(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>Whether the working tree is still the one verification read. Three states, not two.</summary>
public enum FreshnessVerdict
{
    /// <summary>Nothing was captured — a non-git workspace, or a set from before this existed.</summary>
    NotCaptured,
    /// <summary>Something was captured and cannot be re-read now. Not the same as unchanged.</summary>
    Unmeasurable,
    Unchanged,
    Moved,
}
