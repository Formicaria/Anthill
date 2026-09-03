using Anthill.Core.Security;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// PLAN.md §1b S1 — filesystem confinement, the first P0. v0.3.8.59.
///
/// Two independent escapes, and the tests are split accordingly because they need different things
/// from the machine running them.
///
/// THE SIBLING-PREFIX ESCAPE needs nothing. `full.StartsWith(root, StringComparison.Ordinal)` with
/// no separator meant a project at `/srv/project` served `/srv/project-secret/key.txt`. These tests
/// always run, on every platform, and they are the ones that matter most: this was live in the Files
/// pane's read, create and edit routes, and those routes do not consult the runtime write flags, so
/// the containment JSON in §1b does not close it.
///
/// THE LINK ESCAPE needs the ability to CREATE a link, which on Windows requires Developer Mode or
/// an elevated process. Those tests probe first and skip when they cannot. A skipped security test
/// is a bad thing and the honest response is to say so rather than to delete it: on a machine
/// without the privilege the link half of this fix is unverified, the sibling half still is not, and
/// CI on Linux covers both. The probe is per-run rather than per-platform so a Windows machine WITH
/// the privilege gets full coverage instead of a blanket skip.
///
/// WHAT IS NOT TESTED HERE, because it is not fixed: the TOCTOU race where a component is swapped
/// for a link between this resolution and the caller's open. Closing it needs handle-relative,
/// no-follow syscalls .NET does not expose portably. It is recorded in PLAN.md §1b S1 as remaining.
/// A test named for it would have to assert something else and pass, which is the exact defect this
/// release keeps finding.
/// </summary>
public class PathContainmentTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(),
        "anthill-containment-" + Guid.NewGuid().ToString("N")[..10]);

    private readonly string _root;
    private readonly string _sibling;

    public PathContainmentTests()
    {
        // "project" and "project-secret": the sibling whose name begins with the root's name. The
        // defect needs no cleverness to reach — two ordinary directories in one folder.
        _root = Path.Combine(_base, "srv", "project");
        _sibling = Path.Combine(_base, "srv", "project-secret");

        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Directory.CreateDirectory(_sibling);
        File.WriteAllText(Path.Combine(_root, "sub", "ok.txt"), "inside");
        File.WriteAllText(Path.Combine(_sibling, "key.txt"), "SECRET");
    }

    public void Dispose() { try { Directory.Delete(_base, true); } catch { } }

    /// <summary>
    /// Can this machine make a link at all? Windows refuses without Developer Mode or elevation, and
    /// a test that fails for lack of privilege reads as a broken guard.
    /// </summary>
    private bool SymlinksAvailable
    {
        get
        {
            var probe = Path.Combine(_base, "probe-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                Directory.CreateSymbolicLink(probe, _sibling);
                Directory.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// A WINDOWS JUNCTION IS NOT A SYMLINK, AND THIS SUITE HAD NEVER MADE ONE. v0.3.8.110.
    ///
    /// THE RESIDUAL THIS CLOSES, named in PLAN.md §5 as "Windows junctions untested separately".
    /// Every link test above builds its link with `Directory.CreateSymbolicLink`, and each opens
    /// with `if (!SymlinksAvailable) return;` — so on a Windows agent without Developer Mode or
    /// elevation, seven facts pass green having asserted nothing at all. A junction needs NEITHER
    /// privilege: any user can create one with `mklink /J`. So the one link an unprivileged attacker
    /// can actually make inside a workspace was the one link this suite could not have caught.
    ///
    /// The production code was always expected to handle it — <c>PathContainment.LinkTargetOf</c>
    /// tests `FileAttributes.ReparsePoint` rather than asking whether something is a symlink, and a
    /// junction is a reparse point exposing `LinkTarget`. That is a claim about .NET's behaviour on
    /// a filesystem, which is exactly the kind of claim that has to be exercised rather than
    /// reasoned about; `PatchConformanceTests` already carries a comment saying so.
    ///
    /// Skipped by RETURNING on non-Windows, where junctions do not exist — the same shape the
    /// symlink facts use. `mklink` is a `cmd` builtin, so it cannot be started directly.
    /// </summary>
    [Fact]
    public void AJunctionPointingOutOfTheRoot_IsRefused()
    {
        if (!OperatingSystem.IsWindows()) return;

        var link = Path.Combine(_root, "junction-escape");
        var made = TryCreateJunction(link, _sibling);
        if (!made) return;   // a filesystem that cannot hold one has nothing to assert about

        Assert.False(PathContainment.Resolve(_root, Path.Combine("junction-escape", "key.txt")).Allowed,
            "a JUNCTION inside the workspace pointing outside it was followed. Junctions need no "
          + "elevation and no Developer Mode, so this is the reparse point an unprivileged writer "
          + "in the workspace can actually create — and every symlink test in this file would have "
          + "passed while it worked.");
    }

    /// <summary>
    /// `mklink /J` through `cmd`, because it is a shell builtin rather than a program. Returns false
    /// rather than throwing when the filesystem or the shell refuses: a machine that cannot make one
    /// has nothing to say about whether they are followed, and turning that into a failure would be
    /// the "fails for lack of privilege reads as a broken guard" problem this file already names.
    /// </summary>
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(link);
            psi.ArgumentList.Add(target);

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return false;
            if (!process.WaitForExit(15_000)) { try { process.Kill(true); } catch { } return false; }
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch { return false; }
    }

    // -------------------------------------------------------------------------------------------
    // The sibling-prefix escape — the P0, and it needs no privileges
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE FINDING, verbatim. A relative path climbing out of the root and into a sibling whose name
    /// begins with the root's name.
    ///
    /// By the time any comparison runs, `Path.GetFullPath` has already removed the `..` — so the
    /// resolved path contains nothing that looks like traversal. It is simply a different directory
    /// that shares a prefix, which is why a prefix check without a separator waves it through and why
    /// reading the old code does not make the bug obvious.
    /// </summary>
    [Fact]
    public void ASiblingWhoseNameBeginsWithTheRoot_IsNotInsideTheRoot()
    {
        var decision = PathContainment.Resolve(_root, Path.Combine("..", "project-secret", "key.txt"));

        Assert.False(decision.Allowed,
            "a path resolving to a SIBLING of the root was accepted as being inside it. This is the "
          + "Files-pane P0: /srv/project served /srv/project-secret because one string starts with "
          + "the other.");
    }

    /// <summary>The same directory named absolutely, in case a caller supplies a full path.</summary>
    [Fact]
    public void TheSiblingDirectoryItself_IsRefusedWhenNamedAbsolutely() =>
        Assert.False(PathContainment.Resolve(_root, Path.Combine(_sibling, "key.txt")).Allowed);

    /// <summary>
    /// And the boundary is a SEPARATOR, not a prefix — proved from the other side, so the fix cannot
    /// be "compare more strictly" in a way that also refuses legitimate paths.
    /// </summary>
    [Fact]
    public void APathInsideTheRoot_IsStillAllowed()
    {
        var decision = PathContainment.Resolve(_root, Path.Combine("sub", "ok.txt"));

        Assert.True(decision.Allowed, decision.Reason);
        Assert.Equal(Path.Combine(PathContainment.Canonicalize(_root), "sub", "ok.txt"), decision.Path);
    }

    /// <summary>
    /// The root IS inside itself. An off-by-one that refuses the root refuses the Files pane's own
    /// opening listing, which is how a containment fix gets reverted rather than corrected.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(".")]
    public void TheRootItself_IsContained(string requested) =>
        Assert.True(PathContainment.Resolve(_root, requested).Allowed);

    /// <summary>
    /// A file that does not exist yet resolves, because create and edit routes need it to. Refusing
    /// non-existent leaves would make the guard reject every new file — and the pressure would then
    /// be to skip the guard on the create path, which is where it is most needed.
    /// </summary>
    [Theory]
    [InlineData("newfile.txt")]
    [InlineData("sub/new/deep.txt")]
    public void APathThatDoesNotExistYet_IsContainedWhenItsPlaceIs(string requested) =>
        Assert.True(PathContainment.Resolve(_root, requested).Allowed);

    /// <summary>...and a non-existent path OUTSIDE is still refused. "Does not exist" must not be a
    /// route past containment — that is how a write creates the file it was refused for reading.</summary>
    [Fact]
    public void ANonExistentPathOutsideTheRoot_IsStillRefused() =>
        Assert.False(PathContainment.Resolve(_root, Path.Combine("..", "project-secret", "new.txt")).Allowed);

    // -------------------------------------------------------------------------------------------
    // The link escape
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A link INSIDE the workspace pointing OUTSIDE it. This is the escape `Path.GetFullPath` cannot
    /// see, because it is a lexical function and the path is lexically fine.
    ///
    /// It matters more than a traversal bug: the coding agent works inside the workspace, so anything
    /// that can write there can create the link, and every one of the twenty call sites resolving
    /// through the workspace guard then followed it.
    /// </summary>
    [Fact]
    public void ALinkPointingOutOfTheRoot_IsRefused()
    {
        if (!SymlinksAvailable) return;
        Directory.CreateSymbolicLink(Path.Combine(_root, "escape"), _sibling);

        Assert.False(PathContainment.Resolve(_root, Path.Combine("escape", "key.txt")).Allowed,
            "a symlink inside the workspace pointing outside it was followed. GetFullPath is lexical "
          + "and never resolved it, which is why RepositoryIndex's comment claiming this case was "
          + "refused had been wrong since it was written.");
    }

    /// <summary>
    /// An INTERMEDIATE component is the link and the leaf is an ordinary file — the case that breaks
    /// a resolver which only checks the final component. The escape is always available one directory
    /// above wherever the check stops looking.
    /// </summary>
    [Fact]
    public void ALinkInAnIntermediateComponent_IsResolvedToo()
    {
        if (!SymlinksAvailable) return;
        Directory.CreateSymbolicLink(Path.Combine(_root, "sub", "out"), _sibling);

        Assert.False(PathContainment.Resolve(_root, Path.Combine("sub", "out", "key.txt")).Allowed);
    }

    /// <summary>
    /// A RELATIVE link target resolves against the LINK's directory, not the process working
    /// directory. Getting this wrong resolves a link to somewhere it does not point — which can fail
    /// open as easily as closed, and is not reproducible from a different working directory.
    /// </summary>
    [Fact]
    public void ARelativeLinkTarget_ResolvesAgainstTheLinksOwnDirectory()
    {
        if (!SymlinksAvailable) return;
        File.CreateSymbolicLink(Path.Combine(_root, "rel"),
            Path.Combine("..", "project-secret", "key.txt"));

        Assert.False(PathContainment.Resolve(_root, "rel").Allowed);
    }

    /// <summary>A link that stays INSIDE the workspace is fine. Refusing every link would break
    /// ordinary checkouts and get the guard turned off.</summary>
    [Fact]
    public void ALinkPointingInsideTheRoot_IsAllowed()
    {
        if (!SymlinksAvailable) return;
        Directory.CreateSymbolicLink(Path.Combine(_root, "inside"), Path.Combine(_root, "sub"));

        var decision = PathContainment.Resolve(_root, Path.Combine("inside", "ok.txt"));

        Assert.True(decision.Allowed, decision.Reason);
        Assert.Equal(Path.Combine(PathContainment.Canonicalize(_root), "sub", "ok.txt"), decision.Path);
    }

    /// <summary>
    /// When the ROOT is itself a link, everything under it is still contained. Resolving the
    /// candidate but not the root compares a real path against a symbolic one and refuses the entire
    /// workspace — a fix that fails this way looks like a broken product, gets reverted under
    /// pressure, and takes the security fix with it.
    /// </summary>
    [Fact]
    public void WhenTheRootIsALink_ItsOwnContentsAreStillContained()
    {
        if (!SymlinksAvailable) return;
        var linkedRoot = Path.Combine(_base, "linked-root");
        Directory.CreateSymbolicLink(linkedRoot, _root);

        Assert.True(PathContainment.Resolve(linkedRoot, Path.Combine("sub", "ok.txt")).Allowed);
        // And it is still a boundary, not merely a resolution.
        Assert.False(PathContainment.Resolve(linkedRoot, Path.Combine("..", "srv", "project-secret", "key.txt")).Allowed);
    }

    /// <summary>
    /// A link CYCLE is refused by a bound rather than by a stack overflow. `a -> b -> a` is the
    /// obvious way to hang a resolver, so the bound is the defence and the refusal is the design.
    /// </summary>
    [Fact]
    public void ALinkCycle_IsRefusedRatherThanFollowedForever()
    {
        if (!SymlinksAvailable) return;
        var a = Path.Combine(_root, "cycle-a");
        var b = Path.Combine(_root, "cycle-b");
        Directory.CreateSymbolicLink(a, b);
        Directory.CreateSymbolicLink(b, a);

        var decision = PathContainment.Resolve(_root, "cycle-a");

        Assert.False(decision.Allowed);
        Assert.Contains("link", decision.Reason!);
    }

    // -------------------------------------------------------------------------------------------
    // One rule, one implementation
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The workspace guard is the same decision. It carried the separator check correctly and the
    /// link resolution not at all, so proving the resolver alone would leave twenty call sites
    /// untested against the thing that actually reached them.
    /// </summary>
    [Fact]
    public void TheWorkspaceGuard_EnforcesTheSameBoundary()
    {
        var guard = new WorkspacePathGuard(_root);

        Assert.Throws<UnauthorizedAccessException>(
            () => guard.ResolveSafePath(Path.Combine("..", "project-secret", "key.txt")));
        Assert.Equal(
            Path.Combine(PathContainment.Canonicalize(_root), "sub", "ok.txt"),
            guard.ResolveSafePath(Path.Combine("sub", "ok.txt")));

        if (!SymlinksAvailable) return;
        Directory.CreateSymbolicLink(Path.Combine(_root, "guard-escape"), _sibling);
        Assert.Throws<UnauthorizedAccessException>(
            () => guard.ResolveSafePath(Path.Combine("guard-escape", "key.txt")));
    }

    /// <summary>
    /// The detector, named so it can be exercised directly. v0.3.8.114.
    ///
    /// It was `\.StartsWith\([^;]*?[Rr]oot`, and `[^;]` crosses newlines — so it ran from a
    /// `StartsWith` on one line, past the end of that statement, into whatever mentioned a root
    /// next. `EmitConfigCommand` tripped it with a CLI flag check whose FALLBACK on the following
    /// line called `FindRepositoryRoot()`; nothing there compares a path to anything.
    ///
    /// Bounding it to one physical line would have been the other error — `docs/GUARDS.md` names a
    /// source scan sliced by a line or a character budget as its own defect, and three guards were
    /// widened at `.112` for exactly that. So it is bounded by the CALL instead: the match may enter
    /// a nested `(` (so `StartsWith(Path.Combine(root, …))` is still seen) but never crosses `)` or
    /// `;`, which is where the argument list actually ends.
    /// </summary>
    private const string RootPrefixComparison = @"\.StartsWith\((?:[^();]|\()*?[Rr]oot";

    /// <summary>
    /// THE DETECTOR STILL DETECTS. A narrowed reader that stops matching reports zero offenders and
    /// passes forever, which is the failure this suite has caught in six separate forms — so the
    /// shapes it must catch, and the ones it must not, are asserted rather than assumed.
    /// </summary>
    [Fact]
    public void TheRootPrefixDetector_SeesTheShapeAndNotItsNeighbours()
    {
        var caught = new[]
        {
            "full.StartsWith(sandbox.Root, StringComparison.Ordinal)",
            "resolved.StartsWith(root)",
            "target.StartsWith(Path.Combine(root, \"x\"))",
            "src.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase)",
        };

        foreach (var offender in caught)
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(offender, RootPrefixComparison),
                $"the detector no longer sees a root prefix comparison: {offender}");

        var ignored = new[]
        {
            // A flag check whose next statement happens to mention a root. The false positive.
            "a.StartsWith(\"--\", StringComparison.Ordinal))\n    ?? FindRepositoryRoot();",
            "name.StartsWith(\"_comment\", StringComparison.Ordinal)",
            "line.StartsWith(\"///\", StringComparison.Ordinal);\n        var x = RepoRoot();",
        };

        foreach (var innocent in ignored)
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(innocent, RootPrefixComparison),
                $"the detector reports a comparison that is not one: {innocent}");
    }

    /// <summary>
    /// And there is exactly ONE implementation of the rule. The Files pane and the workspace guard
    /// disagreeing is how this shipped: the guard had the separator and the pane did not, and nothing
    /// compared them. A second copy is not a bug today and is the same bug again later.
    /// </summary>
    [Fact]
    public void NoOtherContainmentCheck_RollsItsOwnPrefixComparison()
    {
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(SourceText.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
             || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (path.EndsWith("PathContainment.cs", StringComparison.Ordinal)) continue;

            var code = SourceText.CodeOnly(File.ReadAllText(path));

            // The shape of the defect: any StartsWith whose ARGUMENT names a root.
            //
            // The first draft of this detector keyed on the variable being compared —
            // `full|resolved|candidate|path` — and found nothing beyond the two sites the review
            // named. Four more existed, in PatchVerifyRunner, SandboxWorkspace, PatchSetMaterializer
            // and Verification, and they were invisible because the variable was called `target`,
            // `src` or `full` against `sandbox.Root`. A detector written around the examples in hand
            // finds the examples in hand. Keying on the ROOT side is what the rule is actually about.
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(code, RootPrefixComparison))
            {
                // A comparison that already demands a separator is the correct rule written out by
                // hand. It is still a second copy, but it is not THIS bug, so it is not reported
                // here as one — an over-broad detector gets suppressed rather than read.
                if (match.Value.Contains("DirectorySeparatorChar", StringComparison.Ordinal)) continue;
                offenders.Add(Path.GetFileName(path));
                break;
            }
        }

        Assert.True(offenders.Count == 0,
            "these files compare a path against a root themselves instead of calling "
          + "PathContainment.Resolve: " + string.Join(", ", offenders)
          + ". A prefix comparison is the sibling-prefix bug unless a separator is required, and it "
          + "is blind to links either way.");
    }
}
