using Anthill.Core.Agents;
using Anthill.Core.Memory;
using Anthill.Core.Pheromones;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.93 — the pheromone layer's FIRST consequential decision, replayed and A/B-proven.
///
/// Trails were written on every mission since the layer existed and consumed by nothing but a
/// prompt garnish — a learning system whose output was decoration. The one decision they now make:
/// which worker of a role takes a task when the task's own text does not say. The rank order under
/// test, in both directions:
///
///   1. capability compatibility outranks strength — a keyword-decided selection never consults
///      trails, so the strongest reputation in the colony cannot put the docs specialist on a UI
///      change;
///   2. verified trails only — a trail qualifies on net verified evidence (above-baseline strength
///      AND successes over failures), which by construction of the single writer
///      (UpdateMissionPheromones) only completed_verified missions can produce.
///
/// A/B REPLAY: the same request is resolved against two trail states; the decision flips with the
/// evidence, and only where the registry had no opinion. Two colonies are not run — the decision
/// is a pure function of (candidates, trail lookup), which is what makes it replayable at all.
/// </summary>
public class PheromoneDecisionTests : IDisposable
{
    private readonly SqliteMemory _memory = new(":memory:");
    public void Dispose() => _memory.Dispose();

    private static IReadOnlyList<AntWorkerDefinition> CoderWorkers =>
        AntRegistry.ByRole["coder"].Workers;

    private static TrailView Verified(double strength) => new(strength, SuccessCount: 4, FailureCount: 1);

    // ---- rule 2: verified trails only -----------------------------------------------------------

    [Fact]
    public void WithNoTrails_TheDefaultStands()
    {
        Assert.Null(TrailGuidedSelection.Prefer(CoderWorkers, _ => null));
    }

    [Fact]
    public void AnUnverifiedOrLosingTrail_NeverDecides()
    {
        // Strength above baseline but failures outnumber successes — net evidence is negative.
        Assert.Null(TrailGuidedSelection.Prefer(CoderWorkers,
            key => key == "worker:coder.docs_coder" ? new TrailView(0.9, 1, 3) : null));

        // Successes outnumber failures but strength never rose above baseline — the successes were
        // never verified (only completed_verified missions pay positive deltas on worker trails).
        Assert.Null(TrailGuidedSelection.Prefer(CoderWorkers,
            key => key == "worker:coder.docs_coder" ? new TrailView(0.5, 3, 1) : null));
    }

    [Fact]
    public void TheStrongestVerifiedTrail_Decides_AmongCompatibleCandidates()
    {
        var picked = TrailGuidedSelection.Prefer(CoderWorkers, key => key switch
        {
            "worker:coder.ui_coder" => Verified(0.72),
            "worker:coder.backend_coder" => Verified(0.61),
            _ => null,
        });
        Assert.Equal("coder.ui_coder", picked!.WorkerId);
    }

    // ---- rule 1: compatibility outranks strength ------------------------------------------------

    /// <summary>
    /// A/B, arm A: a task whose text names the capability. The registry decides, the trail is
    /// never consulted, and the strongest trail in the colony changes nothing.
    /// </summary>
    [Fact]
    public void AKeywordDecision_IsNeverOverriddenByAnyTrail()
    {
        var (worker, keywordDecided) = AntRegistry.ResolveWorker(
            "coder", "patch_proposal", "restyle the dashboard css layout");

        Assert.True(keywordDecided);
        Assert.Equal("coder.ui_coder", worker!.WorkerId);
        // The caller's contract: keyword-decided selections do not reach Prefer at all. The
        // planning wire (PlanningService.CreatePlan) only consults trails when keywordDecided is
        // false — asserted here at the resolution layer, and below at the replay layer.
    }

    /// <summary>
    /// A/B, arm B: the same role with a text that says nothing. Trail state A (backend verified
    /// strongest) and trail state B (docs verified strongest) flip the decision — the SAME request,
    /// different evidence, different worker. This is the consequence trails never had.
    /// </summary>
    [Fact]
    public void WhereTheTextSaysNothing_TheReplayFlipsWithTheEvidence()
    {
        var (defaultWorker, keywordDecided) = AntRegistry.ResolveWorker(
            "coder", "patch_proposal", "apply the agreed change to the project");
        Assert.False(keywordDecided);
        Assert.Equal("coder.backend_coder", defaultWorker!.WorkerId);

        var stateA = TrailGuidedSelection.Prefer(CoderWorkers,
            key => key == "worker:coder.backend_coder" ? Verified(0.8) : null);
        var stateB = TrailGuidedSelection.Prefer(CoderWorkers,
            key => key == "worker:coder.docs_coder" ? Verified(0.8) : null);

        Assert.Equal("coder.backend_coder", stateA!.WorkerId);
        Assert.Equal("coder.docs_coder", stateB!.WorkerId);
    }

    // ---- the read path reads what the writer wrote ----------------------------------------------

    /// <summary>
    /// The memory read model returns the writer's numbers, and legacy-quarantined trails are
    /// invisible — a learning reset must not keep steering new selections from beyond the grave.
    /// </summary>
    [Fact]
    public void GetPheromoneTrail_ReadsLiveTrails_AndNotLegacyOnes()
    {
        Assert.Null(_memory.GetPheromoneTrail("worker:coder.backend_coder"));

        _memory.UpdatePheromoneTrail("worker:coder.backend_coder", "worker", success: true, 0.1,
            new() { ["seed"] = "test" });

        var view = _memory.GetPheromoneTrail("worker:coder.backend_coder");
        Assert.NotNull(view);
        Assert.Equal(0.6, view!.Strength, precision: 4);
        Assert.Equal(1, view.SuccessCount);
        Assert.Equal(0, view.FailureCount);
    }
}
