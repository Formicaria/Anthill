using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A patch set applies as a unit ON EVERY PATH, not only on the auto-apply lane. v0.3.8.91.
///
/// THE CLAIM AND THE CODE. Six places in this repository state the guarantee — `PLAN.md` lists it
/// under Done and load-bearing, `ApplyTransaction`'s header frames it as the v0.3.8.57 guarantee,
/// `AutoApplyAtomicityTests` is named for it, `AutoApplyRunner` says it twice, and the changelog
/// records it. Every one of them was true of exactly one lane.
///
/// The bypass path looped `foreach (var proposal in patchSet.Proposals)` calling a single-patch
/// apply, and CONTINUED past a failure. A three-file set whose second proposal hit a stale base left
/// files one and three written: a tree nothing had verified, described by a verification record that
/// judged the set as a whole. Under the git-commit policy it also produced one commit per file, any
/// prefix of which could be the final state. `AutoApplyAtomicityTests` could not see it — that guard
/// reads `AutoApplyRunner`, and this was in `ExecutionService`.
///
/// These assertions are structural, over the shape of the apply paths. The behavioural half — a real
/// three-file set with a poisoned middle proposal, asserting the tree is byte-identical afterwards —
/// needs a lifecycle fixture and is the next commit's work; `PLAN.md` names it rather than this file
/// implying it.
/// </summary>
public class PatchSetAtomicityTests
{
    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { SourceText.RepoRoot() }.Concat(parts).ToArray()));

    /// <summary>
    /// THE BYPASS LANE NO LONGER APPLIES PROPOSALS ONE AT A TIME.
    ///
    /// The precise shape that was wrong: a loop over `patchSet.Proposals` containing an apply call.
    /// The lane still loops — it evaluates the gate per proposal — so this asserts what must not be
    /// in the loop rather than that no loop exists, which is the distinction a lazier guard would
    /// miss and then pass forever.
    /// </summary>
    [Fact]
    public void TheBypassLane_AppliesTheSet_NotEachProposal()
    {
        var code = SourceText.CodeOnly(Source("src", "Anthill.Core", "Orchestration", "ExecutionService.cs"));

        var start = code.IndexOf("private void ApplyUnderBypass", StringComparison.Ordinal);
        Assert.True(start > 0, "ApplyUnderBypass has moved or been renamed.");
        var body = code[start..Math.Min(code.Length, start + 6000)];

        Assert.Contains("_applyPatchSet(", body, StringComparison.Ordinal);

        // A per-proposal apply is the defect. The gate loop is fine; an apply inside it is not.
        Assert.DoesNotContain("_approveApplyPatch(", body, StringComparison.Ordinal);

        var loops = Regex.Matches(body, @"foreach\s*\(\s*var\s+proposal\s+in\s+patchSet\.Proposals\s*\)");
        foreach (Match loop in loops)
        {
            var after = body[loop.Index..Math.Min(body.Length, loop.Index + 1200)];
            Assert.False(after.Contains("_applyPatchSet(", StringComparison.Ordinal),
                "the set applier is being called inside a per-proposal loop, which applies the set "
              + "once per proposal rather than once.");
        }
    }

    /// <summary>
    /// THE SET APPLIER PREFLIGHTS EVERYTHING, JOURNALS, AND ROLLS BACK THE WHOLE SET.
    ///
    /// All four steps, in order. Any three of them is a partial guarantee, and a partial atomicity
    /// guarantee is the state this replaced — the difference between "we roll back on failure" and
    /// "nothing is written until every target has been checked" is a half-applied tree.
    /// </summary>
    [Fact]
    public void TheSetApplier_ChecksEverythingBeforeItWritesAnything()
    {
        var code = SourceText.CodeOnly(Source("src", "Anthill.Core", "Verification", "PatchSetApply.cs"));

        var apply = code.IndexOf("public static SetApplyOutcome ApplySet", StringComparison.Ordinal);
        Assert.True(apply > 0, "ApplySet has moved or been renamed.");
        var body = code[apply..];

        var preflightAt = body.IndexOf("Preflight(set)", StringComparison.Ordinal);
        var beginAt = body.IndexOf("ApplyTransaction.Begin", StringComparison.Ordinal);
        var stageAt = body.IndexOf("StageExternal", StringComparison.Ordinal);
        var applyAt = body.IndexOf("applyOne(", StringComparison.Ordinal);

        Assert.True(preflightAt > 0, "the set applier no longer preflights.");
        Assert.True(beginAt > preflightAt,
            "the journal opens before the preflight. Nothing should be journaled for a set that was "
          + "never going to be applied.");
        Assert.True(stageAt > beginAt, "a file is staged before the journal exists.");
        Assert.True(applyAt > stageAt,
            "a write happens before its pre-state and backup are staged, so a crash at that instant "
          + "leaves a mutation the journal cannot describe.");

        Assert.Contains("rollBack(", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THERE IS ONLY ONE PREFLIGHT.
    ///
    /// `AutoApplyRunner` had its own copy in `Anthill.Api` — a second implementation of one rule,
    /// which is a named defect class here, and which had already half-drifted: the auto-apply lane
    /// had a preflight and the ordinary lane had none at all. Api's now delegates.
    /// </summary>
    [Fact]
    public void ThePreflight_HasOneImplementation()
    {
        var runner = SourceText.CodeOnly(Source("src", "Anthill.Api", "AutoApplyRunner.cs"));

        Assert.Contains("PatchSetApply.Preflight(", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("requireBaseHash: true", runner,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The proposals a set applier reads come back UNSEALED.
    ///
    /// Patch bodies are encrypted at rest. A set read that returned the sealed text would hand
    /// ciphertext to `PatchApply.Compute`, which would compare it against the live file, find no
    /// match, and refuse every proposal with "stale base" — a perfectly correct-looking refusal for
    /// a reason that has nothing to do with the tree, on the path that exists to apply things.
    /// </summary>
    [Fact]
    public void TheSetRead_UnsealsThePatchBodies()
    {
        var code = SourceText.CodeOnly(Source("src", "Anthill.Core", "Memory", "SqliteMemory.Operations.cs"));

        var start = code.IndexOf("GetPatchProposalsForSet", StringComparison.Ordinal);
        Assert.True(start > 0, "GetPatchProposalsForSet has moved or been renamed.");
        var body = code[start..Math.Min(code.Length, start + 1500)];

        Assert.Contains("_cipher.Unprotect", body, StringComparison.Ordinal);
    }
}
