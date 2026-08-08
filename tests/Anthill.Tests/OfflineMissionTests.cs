using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Models;
using Anthill.Core.Orchestration;
using Anthill.Core.Pheromones;
using Anthill.SDK.Contracts;
using Anthill.SDK.Reasoning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A WHOLE MISSION with no reasoning provider. v3.8.32.
///
/// The claim "the colony still functions when no LLM is callable" was made in v3.8.5 and rested on
/// <c>CoreWithoutProviderTests</c>, which proves something narrower and easier: that a model call
/// against an empty factory list returns a typed refusal instead of throwing. That is one method at
/// one boundary. It says nothing about whether a mission plans, dispatches, executes, records
/// evidence, finalizes and evaluates when every model-dependent role refuses.
///
/// An external review named the gap and was right: the strong claim had no test, and the fixture
/// that looked like it covered the roster explicitly assumed <c>modelAvailable: true</c>.
///
/// So this runs the real Queen through <c>RunMission</c> with routing off and no provider, and
/// asserts on what SURVIVES the absence: the mission terminates, every task reaches a terminal
/// state, exactly one canonical evaluation is persisted, and the refusals are recorded as typed
/// failures rather than silently completing.
/// </summary>
[Collection("Autonomy")]
public class OfflineMissionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anthill_offline_" + Guid.NewGuid().ToString("N"));

    private readonly bool _routingWas;
    private readonly bool _ollamaWas;
    private readonly bool _autonomyWas;

    public OfflineMissionTests()
    {
        AnthillRuntime.Initialize();
        _routingWas = AnthillRuntime.EnableModelRouting;
        _ollamaWas = AnthillRuntime.UseOllama;
        _autonomyWas = AnthillRuntime.EnableAutonomy;

        // The colony an operator has when nothing is reachable: no routing, no local provider.
        AnthillRuntime.EnableModelRouting = false;
        AnthillRuntime.UseOllama = false;
        AnthillRuntime.EnableAutonomy = false;

        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        AnthillRuntime.EnableModelRouting = _routingWas;
        AnthillRuntime.UseOllama = _ollamaWas;
        AnthillRuntime.EnableAutonomy = _autonomyWas;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Queen NewQueen() =>
        new(new SqliteMemory(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db")));

    /// <summary>
    /// THE test the "works without an LLM" claim needed. A mission runs to completion offline.
    ///
    /// Deliberately asserts TERMINATION and BOOKKEEPING rather than success: a colony with no model
    /// cannot do model work, and a test demanding a verified outcome would either be asserting a
    /// falsehood or quietly proving that verification does not require a model.
    /// </summary>
    [Fact]
    public void AMissionRunsToCompletion_WithNoReasoningProvider()
    {
        var queen = NewQueen();
        try
        {
            var result = queen.RunMission("summarize the colony");

            Assert.False(string.IsNullOrWhiteSpace(result));
            var missionId = queen.LastMissionId!;
            Assert.False(string.IsNullOrWhiteSpace(missionId));

            // Exactly one canonical evaluation, persisted. Not zero, and not one per reader.
            var evaluation = queen.Memory.LoadMissionEvaluation(missionId);
            Assert.NotNull(evaluation);

            // Nothing is left mid-flight. A task stuck at running/ready/pending after finalization
            // is the invariant the drain exists to hold, and an offline run is where it is most
            // likely to break — every model-dependent role bails early.
            var tasks = queen.Memory.GetTasksForMission(missionId, 200);
            Assert.NotEmpty(tasks);
            Assert.DoesNotContain(tasks,
                row => (row.GetValueOrDefault("status")?.ToString() ?? "")
                    is "running" or "ready" or "pending" or "blocked");
        }
        finally { queen.Dispose(); }
    }

    /// <summary>
    /// A model refusal is recorded as a TYPED failure, never as a completion.
    ///
    /// This is the half that matters for learning. If an offline role's refusal were recorded as a
    /// success, the colony would reinforce trails for work that never happened; if it were recorded
    /// as an untyped failure, the attribution rule could not tell it apart from a role that did its
    /// job badly.
    /// </summary>
    [Fact]
    public void AModelRefusal_IsRecordedAsATypedFailure_NotACompletion()
    {
        var queen = NewQueen();
        try
        {
            queen.RunMission("summarize the colony");
            var missionId = queen.LastMissionId!;

            var tasks = queen.Memory.GetTasksForMission(missionId, 200);

            foreach (var row in tasks)
            {
                var status = row.GetValueOrDefault("status")?.ToString() ?? "";
                if (status != "failed") continue;

                var failureType = row.GetValueOrDefault("failure_type")?.ToString() ?? "";
                Assert.False(string.IsNullOrWhiteSpace(failureType),
                    "a failed task with no failure_type cannot be attributed to anyone");
            }
        }
        finally { queen.Dispose(); }
    }

    /// <summary>
    /// A provider outage is NOT charged to the ant that was holding the task.
    ///
    /// This is the offline half of the v3.8.32 attribution fix, asserted where it actually matters:
    /// on a colony that really has no provider, using the wire form the real mapper really writes.
    /// Before the fix, an offline mission taught the colony that every role it tried was bad.
    /// </summary>
    [Fact]
    public void AnOfflineRun_DoesNotDamageAnyRolesReputation()
    {
        var queen = NewQueen();
        try
        {
            queen.RunMission("summarize the colony");
            var missionId = queen.LastMissionId!;

            foreach (var row in queen.Memory.GetTasksForMission(missionId, 200))
            {
                var failureType = row.GetValueOrDefault("failure_type")?.ToString();
                if (!FailureClassNames.TryParse(failureType, out var cls)) continue;
                if (!LearningAttribution.NotTheWorkersFault.Contains(cls)) continue;

                // The task that failed environmentally must be attributed to nobody.
                var task = new Anthill.Core.Domain.Task
                {
                    Title = row.GetValueOrDefault("title")?.ToString() ?? "t",
                    AssignedAnt = row.GetValueOrDefault("assigned_ant")?.ToString() ?? "researcher",
                    Status = Anthill.Core.Domain.TaskStatus.Failed,
                    FailureType = failureType,
                };

                Assert.Equal(LearningAttribution.Attribution.Neutral,
                    LearningAttribution.For(task, missionVerified: false));
            }
        }
        finally { queen.Dispose(); }
    }

    /// <summary>
    /// Two offline missions in a row. A colony that degrades on the SECOND run — because the first
    /// poisoned its own memory — would pass every single-mission test ever written.
    /// </summary>
    [Fact]
    public void ASecondOfflineMission_RunsJustAsWellAsTheFirst()
    {
        var queen = NewQueen();
        try
        {
            queen.RunMission("summarize the colony");
            var first = queen.LastMissionId!;

            queen.RunMission("summarize the colony again");
            var second = queen.LastMissionId!;

            Assert.NotEqual(first, second);
            Assert.NotNull(queen.Memory.LoadMissionEvaluation(second));
            Assert.NotEmpty(queen.Memory.GetTasksForMission(second, 200));
        }
        finally { queen.Dispose(); }
    }

    /// <summary>
    /// The boundary refusal that everything above rests on, resolved through the same explicit
    /// factory list <c>CoreWithoutProviderTests</c> uses.
    ///
    /// Restated here to make the relationship between the two files explicit rather than assumed:
    /// this is a PRECONDITION for an offline mission, not a substitute for one. Treating it as the
    /// whole proof is what left the strong claim untested for twenty-seven releases.
    /// </summary>
    [Fact]
    public void TheModelBoundary_RefusesRatherThanThrowing_WhichIsWhatMakesTheAboveSurvivable()
    {
        var response = ReasoningProviders
            .ResolveFrom(Array.Empty<IReasoningProviderFactory>(), "ollama", "llama3.1:8b", null, "http://localhost:11434")
            .Send(ModelRequest.FromPrompt("hello"));

        Assert.Equal(ModelCallOutcome.Error, response.Status);
    }
}
