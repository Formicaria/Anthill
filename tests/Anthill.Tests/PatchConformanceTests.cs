using System.Text.RegularExpressions;
using Anthill.SDK.Common;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Every applier answers the same question the same way. v0.3.8.57.
///
/// AUTONOMY-10 asks for "every patch entry point run through the same add/modify/delete/rename,
/// path, casing, symlink, stale-base and rollback suite", so that "the API applier, sandbox and
/// auto-apply runner cannot drift apart".
///
/// The honest way to satisfy that is NOT to give each applier its own copy of the suite. Four copies
/// of a semantics table is four things to keep in step, and the drift it is meant to catch would
/// live in whichever copy someone forgot. It is to prove that they SHARE ONE DECISION — the matrix
/// below runs against `PatchApply.Compute`, which every applier calls — and then to pin that each
/// call site actually asks it, with the full set of facts.
///
/// The distinction matters because a caller can share the function and still get a different answer
/// by supplying less: a `Compute` called without `destinationExists` cannot refuse a rename onto an
/// occupied path, and one called without the base hash cannot notice a stale patch. That is the real
/// shape drift takes here, so it is what the ledger checks.
///
/// WHAT COMPUTE CANNOT ANSWER, stated rather than implied. It does no IO, so path containment,
/// casing and symlink resolution are the caller's job by construction — a pure function cannot
/// resolve a path. Those are asserted as "every deciding call site resolves through a path guard"
/// instead of being faked into the matrix.
/// </summary>
public class PatchConformanceTests
{
    private const string Before = "before old after";
    private static string Base => PatchApply.HashOf(Before);

    // -------------------------------------------------------------------------------------------
    // A. The shared decision, over every change type and every outcome class
    // -------------------------------------------------------------------------------------------

    /// <summary>Clean applications, one per change type.</summary>
    [Theory]
    [InlineData(PatchApply.Add, PatchApplyStatus.Created)]
    [InlineData(PatchApply.Modify, PatchApplyStatus.Modified)]
    [InlineData(PatchApply.Delete, PatchApplyStatus.Deleted)]
    [InlineData(PatchApply.Rename, PatchApplyStatus.Renamed)]
    public void EachChangeType_AppliesCleanly(string changeType, PatchApplyStatus expected)
    {
        var result = PatchApply.Compute(
            changeType,
            oldContent: changeType == PatchApply.Modify ? "old" : null,
            newContent: changeType is PatchApply.Add or PatchApply.Modify ? "new" : null,
            currentContent: changeType == PatchApply.Add ? null : Before,
            expectedBaseHash: changeType == PatchApply.Add ? null : Base,
            destinationPath: changeType == PatchApply.Rename ? "moved.txt" : null,
            destinationExists: false,
            requireBaseHash: true);

        Assert.Equal(expected, result.Status);
        Assert.True(result.Ok, result.Reason);
    }

    /// <summary>
    /// A stale base is refused for every DESTRUCTIVE change type — the check that makes a patch
    /// built from an old read fail instead of applying silently.
    /// </summary>
    [Theory]
    [InlineData(PatchApply.Modify)]
    [InlineData(PatchApply.Delete)]
    [InlineData(PatchApply.Rename)]
    public void AStaleBase_IsRefusedForEveryDestructiveChangeType(string changeType)
    {
        var result = PatchApply.Compute(
            changeType, "old", changeType == PatchApply.Modify ? "new" : null,
            currentContent: "the file has moved on since the patch was built",
            expectedBaseHash: Base,
            destinationPath: changeType == PatchApply.Rename ? "moved.txt" : null,
            destinationExists: false, requireBaseHash: true);

        Assert.Equal(PatchApplyStatus.RefusedStaleBase, result.Status);
    }

    /// <summary>The refusal classes that keep an ill-formed or impossible patch off the disk.</summary>
    [Theory]
    // A create over something that already exists (v0.3.8.57).
    [InlineData(PatchApply.Add, "x", "new", Before, false, PatchApplyStatus.RefusedTargetExists)]
    // A modify of a file that is not there.
    [InlineData(PatchApply.Modify, "old", "new", null, false, PatchApplyStatus.RefusedTargetMissing)]
    // A modify whose anchor does not occur.
    [InlineData(PatchApply.Modify, "absent", "new", Before, false, PatchApplyStatus.RefusedOldContentNotFound)]
    // A modify whose anchor occurs twice — the edit is ambiguous.
    [InlineData(PatchApply.Modify, "old", "new", "old and old", false, PatchApplyStatus.RefusedAmbiguous)]
    // A rename onto an occupied destination.
    [InlineData(PatchApply.Rename, null, null, Before, true, PatchApplyStatus.RefusedDestinationOccupied)]
    // A change type nobody implements.
    [InlineData("chmod", null, "new", Before, false, PatchApplyStatus.RefusedUnsupportedChangeType)]
    public void TheRefusalClasses_HoldAcrossTheMatrix(
        string changeType, string? oldContent, string? newContent,
        string? current, bool destinationExists, PatchApplyStatus expected)
    {
        var result = PatchApply.Compute(changeType, oldContent, newContent, current,
            // A hash matching `current` so these fail for their OWN reason rather than as stale.
            expectedBaseHash: current is null ? null : PatchApply.HashOf(current),
            destinationPath: changeType == PatchApply.Rename ? "moved.txt" : null,
            destinationExists: destinationExists);

        Assert.Equal(expected, result.Status);
        Assert.False(result.Ok);
        // A refusal never carries content — nothing can be written by a caller that ignores Status.
        Assert.Null(result.Content);
    }

    /// <summary>
    /// A refusal is inert. `Ok` and `WritesContent` must both be false for every refusal, because a
    /// caller that switches on one and not the other is how a refused patch gets written anyway.
    /// </summary>
    [Fact]
    public void EveryRefusal_IsInert()
    {
        var refusals = new[]
        {
            PatchApply.Compute(PatchApply.Add, null, "new", Before),
            PatchApply.Compute(PatchApply.Modify, "old", "new", null),
            PatchApply.Compute(PatchApply.Modify, "old", "new", "moved on", Base),
            PatchApply.Compute(PatchApply.Modify, "old", "new", Before, null, requireBaseHash: true),
            PatchApply.Compute("chmod", null, "new", Before),
        };

        Assert.All(refusals, r =>
        {
            Assert.False(r.Ok);
            Assert.False(r.WritesContent);
            Assert.False(string.IsNullOrWhiteSpace(r.Reason));
        });
    }

    /// <summary>
    /// Rollback is byte-identical by construction: a modify's result is the ONLY thing written, so
    /// restoring the pre-apply bytes restores the file exactly. Proved by round-tripping the content
    /// the engine produces back through a modify that undoes it.
    /// </summary>
    [Fact]
    public void AModify_IsExactlyReversible()
    {
        var forward = PatchApply.Compute(PatchApply.Modify, "old", "new", Before, Base);
        Assert.Equal(PatchApplyStatus.Modified, forward.Status);

        var back = PatchApply.Compute(PatchApply.Modify, "new", "old", forward.Content,
            PatchApply.HashOf(forward.Content));

        Assert.Equal(Before, back.Content);
    }

    // -------------------------------------------------------------------------------------------
    // B. Every applier asks that decision, with the full set of facts
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Files that DECIDE whether a patch applies, and what each is for.
    ///
    /// A ledger rather than a discovered list, so a new applier cannot appear without someone
    /// stating which kind it is. `WritesLiveTree` decides the one argument that differs between
    /// them: a path that writes to the operator's own checkout must refuse a destructive proposal
    /// with no base hash; a sandbox that verifies one must not, or a legacy proposal becomes
    /// unverifiable rather than merely unappliable.
    /// </summary>
    private static readonly (string Path, bool WritesLiveTree, string Purpose)[] Deciders =
    {
        ("src/Anthill.Modules/Anthill.Modules.Tools/ApplyPatchTool.cs", true,
            "the operator's own tree, through the approved apply path"),
        ("src/Anthill.Api/AutoApplyRunner.cs", true,
            "the Director's auto-apply preflight, which gates writes to the same tree"),
        ("src/Anthill.Core/Verification/PatchSetMaterializer.cs", false,
            "materialises a patch set into a disposable tree so it can be verified"),
        ("src/Anthill.Core/Sandbox/SandboxedCoderRunner.cs", false,
            "applies inside the sandbox the coder runs in"),
    };

    private static string Read(string relative) =>
        SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(),
            relative.Replace('/', Path.DirectorySeparatorChar))));

    /// <summary>
    /// The ledger is COMPLETE. Any file calling `PatchApply.Compute` is either listed as a decider
    /// or is the CLI self-test — nothing else may quietly become a fifth applier.
    ///
    /// This is the anti-drift device. The matrix above proves the semantics; this proves the set of
    /// things governed by them, which is the half a behavioural test cannot see.
    /// </summary>
    [Fact]
    public void EveryCallerOfTheApplyEngine_IsAKnownDecider()
    {
        var root = SourceText.RepoRoot();
        var callers = SourceText.ProductionFiles(root)
            .Where(f => SourceText.CodeOnly(File.ReadAllText(f)).Contains("PatchApply.Compute(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();

        var known = Deciders.Select(d => d.Path)
            // The qualification self-test drives the engine to prove it works; it applies nothing.
            .Append("src/Anthill.Cli/QualificationCommand.cs")
            .ToHashSet(StringComparer.Ordinal);

        var unknown = callers.Where(c => !known.Contains(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();

        Assert.True(unknown.Count == 0,
            "These files decide whether a patch applies and are not in the conformance ledger, so "
          + "nothing checks that they ask the engine the same question the others do: "
          + string.Join(", ", unknown));
        Assert.Equal(Deciders.Length + 1, callers.Count);   // and the ledger names nothing stale
    }

    /// <summary>
    /// Every decider passes the FULL set of facts.
    ///
    /// Sharing the function is not enough to share the answer: a `Compute` called without
    /// `destinationExists` cannot refuse a rename onto an occupied path, and one called without the
    /// base hash cannot notice a stale patch. Those omissions compile and look correct at the call
    /// site, which is exactly why they are checked here rather than trusted.
    /// </summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void EveryDecider_PassesTheFullSetOfFacts(int index)
    {
        var (path, writesLiveTree, purpose) = Deciders[index];
        var source = Read(path);

        var start = source.IndexOf("PatchApply.Compute(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{path} no longer calls the apply engine ({purpose})");
        var call = source[start..Math.Min(source.Length, start + 700)];

        // Case-insensitive: the call sites legitimately differ between a local (`baseHash`) and a
        // property (`proposal.BaseHash`). What matters is that the fact is passed at all.
        Assert.True(Regex.IsMatch(call, "basehash", RegexOptions.IgnoreCase),
            $"{path} computes a patch outcome without passing the base hash, so a stale patch cannot "
          + "be detected on that route.");

        Assert.True(Regex.IsMatch(call, "destination", RegexOptions.IgnoreCase),
            $"{path} does not pass a destination, so a rename cannot be judged on that route.");

        // The occupancy fact — a rename onto an occupied path cannot be refused without it.
        Assert.True(Regex.IsMatch(call, @"Exists\(|destinationTaken|destinationExists|destinationTarget",
                RegexOptions.IgnoreCase),
            $"{path} does not tell the engine whether the rename destination is occupied.");

        if (writesLiveTree)
            Assert.Contains("requireBaseHash: true", call, StringComparison.Ordinal);
        else
            Assert.DoesNotContain("requireBaseHash: true", call, StringComparison.Ordinal);
    }

    /// <summary>
    /// And every decider CONTAINS its paths before deciding, THROUGH THE SHARED RESOLVER.
    ///
    /// `Compute` does no IO, so containment, casing and link resolution cannot be its job — a pure
    /// function has no path to resolve. So they are checked where they live: at the call site,
    /// before the decision.
    ///
    /// v0.3.8.59 (PLAN.md §1b S1) — THIS TEST USED TO ACCEPT THE BUG, and the reasoning it accepted
    /// it with was good. It allowed a second idiom: `PatchSetMaterializer` writes into a disposable
    /// sandbox it created, so it resolved against that root inline —
    /// <c>GetFullPath(Combine(sandboxRoot, …))</c> then <c>StartsWith(sandboxRoot + separator)</c> —
    /// and the comment defended that as correct, on the grounds that insisting on the helper would
    /// be "a test dictating spelling rather than checking a property".
    ///
    /// The spelling WAS the property. `GetFullPath` is lexical: it strips `..` and knows nothing
    /// about the filesystem, so that idiom never resolved a symlink or junction and a link inside
    /// the sandbox pointing at the operator's live checkout materialised straight through it. The
    /// separator was right and the resolution was not — and because this test named the idiom as an
    /// accepted alternative, the file that used it was pinned in that shape by its own guard.
    ///
    /// So the inline idiom is now REFUSED rather than allowed. All four deciders resolve through a
    /// named guard, three via `ResolveSafePath` and the materializer via `PathContainment.Resolve`
    /// directly; both end at the same function. A test may not dictate spelling — but where two
    /// spellings mean different things, choosing between them is checking a property.
    /// </summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void EveryDecider_ContainsItsPathsThroughTheSharedResolver(int index)
    {
        var (path, _, _) = Deciders[index];
        var source = Read(path);

        Assert.True(
            Regex.IsMatch(source, @"ResolveSafePath|PathContainment\.Resolve"),
            $"{path} computes a patch outcome without containing the path through the shared "
          + "resolver, so a `..`-laden or link-bearing proposal is judged and written on that route "
          + "with nothing checking where it actually lands.");

        // And NOT by hand. A file that resolves correctly and also keeps the old lexical comparison
        // has two answers to one question, and the weaker one is the one that gets copied.
        Assert.False(
            Regex.IsMatch(source, @"\.StartsWith\([^;]*?[Rr]oot"),
            $"{path} still compares a path against a root by hand. That comparison is lexical — it "
          + "cannot see a symlink — and its presence beside the real check is how the wrong idiom "
          + "spreads to the next applier.");
    }
}
