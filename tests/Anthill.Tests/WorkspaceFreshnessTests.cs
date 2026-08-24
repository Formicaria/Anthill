using Anthill.Core.Workspaces;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The live tree must still be the one verification read. v0.3.8.91.
///
/// THE HOLE. Verification binds its evidence to the base revision, the patch-set content hash, and
/// `AppliedTreeHash` — which, despite the name, iterates only the paths the patch touched. Files the
/// patch did not touch are not in it. So: verification compiles a sandbox containing A.cs and B.cs,
/// the patch modifies only A.cs, somebody edits B.cs in the live tree, and the apply still finds
/// A.cs hashing to its recorded base and writes. Every hash the system holds is about A.cs; the
/// thing that changed is B.cs.
///
/// WHY HEAD ALONE WOULD HAVE BEEN THE WRONG CHECK, which is the part worth keeping: `git rev-parse
/// HEAD` does not move when somebody edits a file without committing — which is exactly the case the
/// freshness check exists for. A check named for a property it does not deliver is this repository's
/// most-found defect, and it would be especially bad here, because an operator reading "workspace
/// unchanged since verification" would believe it. The fingerprint is HEAD plus the full
/// `git status --porcelain -uall` listing, so a commit, a checkout, an uncommitted edit, a deletion
/// and a new untracked file all move it.
/// </summary>
public class WorkspaceFreshnessTests : IDisposable
{
    private readonly string _root;
    private readonly bool _git;

    public WorkspaceFreshnessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "anthill-fresh-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "one\n");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "two\n");
        _git = Run("init -q") && Run("-c user.email=t@t -c user.name=t add -A")
            && Run("-c user.email=t@t -c user.name=t commit -q -m seed");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private bool Run(string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", args)
            {
                WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            });
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            return p.WaitForExit(15_000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>A workspace that is not a git checkout is UNMEASURED, and says so.</summary>
    [Fact]
    public void ANonGitWorkspace_ProducesNoFingerprint()
    {
        var plain = Path.Combine(Path.GetTempPath(), "anthill-plain-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(plain);
        try
        {
            Assert.Equal("", WorkspaceFingerprint.Capture(plain));
            // And an absent capture is never reported as "unchanged".
            Assert.Equal(FreshnessVerdict.NotCaptured, WorkspaceFingerprint.Compare("", plain));
            Assert.Equal(FreshnessVerdict.NotCaptured, WorkspaceFingerprint.Compare(null, plain));
        }
        finally { try { Directory.Delete(plain, recursive: true); } catch { } }
    }

    [Fact]
    public void AnUnchangedTree_FingerprintsTheSameTwice()
    {
        Skip.IfNoGit(_git);

        var first = WorkspaceFingerprint.Capture(_root);
        Assert.NotEqual("", first);
        Assert.Equal(FreshnessVerdict.Unchanged, WorkspaceFingerprint.Compare(first, _root));
    }

    /// <summary>
    /// THE CASE HEAD WOULD HAVE MISSED — an uncommitted edit to a file the patch never touched.
    ///
    /// This is the reviewer's exact scenario, and the reason the fingerprint reads the porcelain
    /// listing rather than only the commit id.
    /// </summary>
    [Fact]
    public void AnUncommittedEditToAnUnrelatedFile_MovesTheFingerprint()
    {
        Skip.IfNoGit(_git);

        var before = WorkspaceFingerprint.Capture(_root);
        File.WriteAllText(Path.Combine(_root, "b.txt"), "two, edited by somebody else\n");

        Assert.Equal(FreshnessVerdict.Moved, WorkspaceFingerprint.Compare(before, _root));
    }

    /// <summary>A brand new untracked file is a change too — `-uall` is why.</summary>
    [Fact]
    public void ANewUntrackedFile_MovesTheFingerprint()
    {
        Skip.IfNoGit(_git);

        var before = WorkspaceFingerprint.Capture(_root);
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        File.WriteAllText(Path.Combine(_root, "nested", "c.txt"), "three\n");

        Assert.Equal(FreshnessVerdict.Moved, WorkspaceFingerprint.Compare(before, _root));
    }

    /// <summary>And a commit moves it, which is the case HEAD alone would have caught.</summary>
    [Fact]
    public void ACommit_MovesTheFingerprint()
    {
        Skip.IfNoGit(_git);

        var before = WorkspaceFingerprint.Capture(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "one, revised\n");
        Assert.True(Run("-c user.email=t@t -c user.name=t commit -qam revise"));

        Assert.Equal(FreshnessVerdict.Moved, WorkspaceFingerprint.Compare(before, _root));
    }

    /// <summary>
    /// A fingerprint that WAS captured and cannot be re-read is `Unmeasurable`, not `Unchanged`.
    ///
    /// Three states rather than two, because "we measured and it matches" and "we cannot measure"
    /// are different claims and the gate acts on them differently.
    /// </summary>
    [Fact]
    public void ARecordedFingerprintThatCannotBeReRead_IsUnmeasurable_NotUnchanged()
    {
        var gone = Path.Combine(Path.GetTempPath(), "anthill-gone-" + Guid.NewGuid().ToString("N")[..8]);

        Assert.Equal(FreshnessVerdict.Unmeasurable,
            WorkspaceFingerprint.Compare("a-fingerprint-recorded-earlier", gone));
    }

    private static class Skip
    {
        internal static void IfNoGit(bool ok) =>
            Assert.True(ok,
                "this test needs a working `git` to build a throwaway repository. It is not skipped "
              + "silently: a freshness check that cannot be exercised is a freshness check nobody "
              + "should trust, and a green tick over an unrun test is worse than a red one.");
    }
}
