using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.93 — BOTH CODER MODES END IN THE SAME PIPELINE. Structural assertions over the two
/// producers and the one consumer.
///
/// The colony has two ways of producing a change: the model-only coder emits a structured patch
/// JSON that ProcessPatchProposals parses, and an acting CLI edits its isolated worktree, whose
/// diff WorkspaceChangeSet turns into the same PatchSet type at finalization. Until this release
/// only the first reached the pipeline — the harvested set got a bare SavePatchSet: no
/// verification evidence, no patch artifact, no approval card, no bypass gate. Work an acting
/// agent produced was reviewable in principle and unreachable in practice.
///
/// These are shape tests in the PatchPromotionGateTests idiom: they pin that the harvest path
/// CALLS the shared pipeline and that the pipeline is the only place its steps live, so the two
/// modes cannot drift apart again without failing here.
/// </summary>
public class HarvestedPatchPipelineTests
{
    private static string ExecutionSource() => SourceText.CodeOnly(File.ReadAllText(Path.Combine(
        SourceText.RepoRoot(), "src", "Anthill.Core", "Orchestration", "ExecutionService.cs")));

    private static string QueenSource() => SourceText.CodeOnly(File.ReadAllText(Path.Combine(
        SourceText.RepoRoot(), "src", "Anthill.Core", "Orchestration", "Queen.cs")));

    /// <summary>The harvest path enters the pipeline — not a private copy of some of its steps.</summary>
    [Fact]
    public void TheQueenHarvest_CallsTheSharedPipeline()
    {
        var queen = QueenSource();
        var harvestAt = queen.IndexOf("private void HarvestWorkspaceChanges", StringComparison.Ordinal);
        Assert.True(harvestAt > 0, "HarvestWorkspaceChanges has moved or been renamed.");

        var body = queen[harvestAt..];
        Assert.Contains("ProcessHarvestedPatchSet", body);
    }

    /// <summary>
    /// The parse lane delegates to the same method the harvest lane uses. One pipeline, two doors.
    /// </summary>
    [Fact]
    public void BothProducers_EnterProcessPatchSet()
    {
        var code = ExecutionSource();

        // The model-only lane: parse, then the shared pipeline.
        var parseLane = code.IndexOf("private void ProcessPatchProposals", StringComparison.Ordinal);
        Assert.True(parseLane > 0, "ProcessPatchProposals has moved.");
        Assert.Contains("ProcessPatchSet(mission, context, task, patchSet, scheduler)",
            code[parseLane..]);

        // The harvested lane: the public door delegates to the same method.
        Assert.Contains("ProcessPatchSet(mission, context: null, anchorTask, patchSet, scheduler: null)", code);
    }

    /// <summary>
    /// The pipeline's consequential steps — save, artifact, verification, bypass gate, approval
    /// card — each occur exactly once in the execution service, inside the shared method. A second
    /// occurrence of any of them is a second pipeline growing back.
    /// </summary>
    [Theory]
    [InlineData(@"_memory\.SavePatchSet\(")]
    [InlineData(@"RecordPatchArtifact\(mission")]
    [InlineData(@"VerifyPatchSet\(mission")]
    [InlineData(@"ApplyUnderBypass\(mission")]
    [InlineData(@"CreatePatchApprovalRequest\(mission")]
    public void EachPipelineStep_HasExactlyOneCallSite(string pattern)
    {
        var count = Regex.Matches(ExecutionSource(), pattern).Count;
        Assert.True(count == 1,
            $"{pattern} occurs {count} time(s) in ExecutionService — the pipeline steps must live "
          + "in exactly one place, or the two coder modes drift apart the way they did before "
          + "v0.3.8.93.");
    }

    /// <summary>
    /// The one honest divergence is RECORDED, not silent: a set harvested at finalization cannot
    /// have review tasks inserted (the task graph is closed), and the pipeline says so on the
    /// mission's event stream rather than skipping quietly.
    /// </summary>
    [Fact]
    public void TheFinalizationLane_RecordsThatReviewTasksCouldNotBeInserted()
    {
        var code = ExecutionSource();
        var pipeline = code.IndexOf("internal void ProcessPatchSet", StringComparison.Ordinal);
        Assert.True(pipeline > 0, "ProcessPatchSet has moved.");

        var body = code[pipeline..];
        Assert.Contains("policy_review_skipped", body);
        Assert.Contains("harvested_at_finalization", body);
    }
}
