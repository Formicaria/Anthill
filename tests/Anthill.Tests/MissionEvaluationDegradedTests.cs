using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Outcomes;
using Xunit;
using DomainTask = Anthill.Core.Domain.Task;

namespace Anthill.Tests;

/// <summary>
/// v3.0.1 — generation-integrity layer. A mission whose answer was produced by a DEGRADED (non-model)
/// fallback because the routed model was unavailable must not be scored as a verified success, even
/// when the structural plan completed and the verifier passed. Surfaced by live E2E testing: with
/// Ollama down, read-only missions were reporting completed_verified (score 1.00) on canned
/// fallbacks. The signal is structured (Task.GenerationDegraded), never parsed from result prose.
/// </summary>
public class MissionEvaluationDegradedTests : IDisposable
{
    // These tests flip the shared AnthillRuntime.EnableObjectiveVerification global. With assembly
    // parallelization disabled, leaving it mutated leaks into later tests (it broke
    // ObjectiveVerification_IsOffByDefault). Capture on construction, restore on teardown.
    private readonly bool _objectiveVerificationWas = AnthillRuntime.EnableObjectiveVerification;
    public void Dispose() => AnthillRuntime.EnableObjectiveVerification = _objectiveVerificationWas;

    private static DomainTask Verifier(bool pass) => new()
    {
        Title = "Verify", AssignedAnt = "verifier", TaskType = "verification", Status = TaskStatus.Complete,
        Result = pass ? "Verification Passed\nReasoning: checked." : "Verification Failed\nReasoning: missing.",
    };

    private static DomainTask Work(bool degraded = false) => new()
    {
        Title = "Work", AssignedAnt = "researcher", TaskType = "research",
        Status = TaskStatus.Complete, Critical = true, Result = "done", GenerationDegraded = degraded,
    };

    private static Mission MissionWith(string goal, params DomainTask[] tasks)
    {
        var m = new Mission { Goal = goal, Status = MissionStatus.Complete };
        m.Tasks.AddRange(tasks);
        return m;
    }

    [Fact]
    public void IntactGeneration_VerifiedResearch_IsCompletedVerified()
    {
        AnthillRuntime.EnableObjectiveVerification = true;
        var m = MissionWith("research a topic", Work(degraded: false), Verifier(pass: true));

        var e = MissionEvaluator.Evaluate(m, stopReason: null, patchProposalCount: 0);

        Assert.Equal(MissionOutcome.CompletedVerified, e.OutcomeCode); // unchanged baseline (truth-table row)
        Assert.True(e.IsPositive);
    }

    [Fact]
    public void AllFallbackGeneration_CannotBeVerified_EvenWithPassingVerifier()
    {
        AnthillRuntime.EnableObjectiveVerification = true;
        var m = MissionWith("research a topic", Work(degraded: true), Verifier(pass: true));

        var e = MissionEvaluator.Evaluate(m, stopReason: null, patchProposalCount: 0);

        Assert.Equal(MissionOutcome.CompletedUnverified, e.OutcomeCode); // demoted by generation integrity
        Assert.False(e.IsPositive);                                      // and therefore never reinforces
        Assert.Contains("generation=degraded", e.Explanation);
    }
}
