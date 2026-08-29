using Anthill.Core.Domain;
using Anthill.Core.Missions;
using Anthill.Core.Outcomes;
using Anthill.SDK.Artifacts;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.98 — THREE QUESTIONS ASKED, ONE ANSWERED, MISSION COMPLETE.
///
/// That was never a bug in any component. It was a question the runtime could not ask: a mission's
/// deliverables lived as clauses inside the goal string, so no layer could state afterwards whether
/// the thing requested had been produced. Intake gives each request an id; this is the other half —
/// the id reaches the work, and the work answers for it.
///
/// WHAT THE LEDGER CLAIMS, AND WHAT IT DOES NOT. `Served` means a task that OWNS this deliverable
/// ran to completion. It says nothing about whether the answer is good, deep, or on topic — that is
/// a semantic call, and a model asserting it is the evidence v2.19.0 stopped accepting. The failure
/// caught here is structural and was previously invisible: a plan that attributed three questions
/// to three tasks, one of which failed, reported complete because the other two finished.
/// </summary>
public class DeliverableLedgerTests
{
    private static MissionSpecification ThreeQuestions() => MissionIntake.Resolve(
        "What is the Anthill colony capable of now? What is good and bad about its workflow? "
      + "Does it hit the proper ants it needs to?");

    private static Task Done(string role, params string[] deliverables) => new()
    {
        AssignedAnt = role, Status = TaskStatus.Complete, Result = "…",
        DeliverableIds = deliverables.ToList(),
    };

    private static Task Failed(string role, params string[] deliverables) => new()
    {
        AssignedAnt = role, Status = TaskStatus.Failed,
        DeliverableIds = deliverables.ToList(),
    };

    [Fact]
    public void TheSpecification_SplitsAMultiQuestionRequest_IntoIdentifiedDeliverables()
    {
        var spec = ThreeQuestions();

        Assert.Equal(3, spec.Deliverables.Count);
        Assert.Equal(new[] { "d1", "d2", "d3" }, spec.Deliverables.Select(d => d.Id).ToArray());
    }

    /// <summary>
    /// THE FAILURE THIS EXISTS FOR. The plan attributed a question to each task and one task failed.
    /// Every other gate is content: tasks ran, a verifier passed, an answer exists — and one of the
    /// three questions the operator asked has no owner that finished.
    /// </summary>
    [Fact]
    public void ADeclaredDeliverableWhoseTaskFailed_IsUnserved()
    {
        var spec = ThreeQuestions();
        var ledger = DeliverableLedger.Build(spec, new[]
        {
            Done("builder", "d1"),
            Failed("builder", "d2"),
            Done("builder", "d3"),
        });

        var unserved = DeliverableLedger.Unserved(ledger);
        var missing = Assert.Single(unserved);
        Assert.Equal("d2", missing.Id);
        Assert.Equal(DeliverableClaim.Declared, missing.Claim);

        // And the gate refuses the mission for it, naming the id — a refusal an operator cannot
        // locate is a refusal they cannot answer.
        var verdict = AssessmentObjective.Evaluate(spec, Inspected(), VerifierRead(), "an answer", ledger);
        Assert.False(verdict.Satisfied);
        Assert.Contains("'d2'", verdict.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// A plan that claimed nothing is credited to whatever compiles the answer — honest, because
    /// the compiled answer IS what addresses the questions, and deliberately WEAKER. The ledger
    /// says which case applies rather than hiding the difference.
    /// </summary>
    [Fact]
    public void WithNoClaims_TheCompilingTaskIsCredited_AndTheRecordSaysItWasInferred()
    {
        var ledger = DeliverableLedger.Build(ThreeQuestions(), new[]
        {
            Done("researcher"), Done("builder"), Done("verifier"),
        });

        Assert.All(ledger, entry =>
        {
            Assert.True(entry.Served);
            Assert.Equal(DeliverableClaim.Inferred, entry.Claim);
        });
        Assert.Empty(DeliverableLedger.Unserved(ledger));
    }

    /// <summary>A mission with nothing that can compile an answer owns none of what was asked.</summary>
    [Fact]
    public void WithNothingToCompileTheAnswer_EveryDeliverableIsUnowned()
    {
        var ledger = DeliverableLedger.Build(ThreeQuestions(), new[] { Done("researcher") });

        Assert.All(ledger, entry =>
        {
            Assert.False(entry.Served);
            Assert.Equal(DeliverableClaim.Unowned, entry.Claim);
            Assert.Empty(entry.ServingTaskIds);
        });
    }

    /// <summary>
    /// THE BOUNDARY. A specification with no deliverables — every mission before this release —
    /// produces an empty ledger, so no consumer of it can constrain work that was never asked to
    /// declare anything.
    /// </summary>
    [Fact]
    public void AnUnclassifiedRequest_HasNoLedger()
    {
        var general = MissionIntake.Resolve("Add a changelog entry for the release.");
        Assert.Equal(MissionSpecification.GeneralClass, general.MissionClass);
        Assert.Empty(DeliverableLedger.Build(general, new[] { Done("builder") }));
        Assert.Empty(DeliverableLedger.Build(null, new[] { Done("builder") }));
    }

    /// <summary>
    /// The ledger is a PURE FUNCTION of the specification and the terminal tasks. The evaluator
    /// builds its own copy rather than trusting one it was handed, and that is only sound while
    /// building it twice gives the same answer.
    /// </summary>
    [Fact]
    public void ItIsReproducible()
    {
        var spec = ThreeQuestions();
        var tasks = new[] { Done("builder", "d1"), Failed("builder", "d2"), Done("builder", "d3") };

        // Flattened rather than compared as records: `ServingTaskIds` is a List, and record
        // equality would compare the two lists by reference and pass for the wrong reason — the
        // vacuous-assertion shape this repository keeps finding in its own guards.
        static string Flatten(IReadOnlyList<DeliverableEntry> l) =>
            string.Join("|", l.Select(e => $"{e.Id}:{e.Claim}:{e.Served}:{string.Join(",", e.ServingTaskIds)}"));

        Assert.Equal(Flatten(DeliverableLedger.Build(spec, tasks)),
                     Flatten(DeliverableLedger.Build(spec, tasks)));

        var snapshot = DeliverableLedger.Snapshot(DeliverableLedger.Build(spec, tasks));
        Assert.Equal(3, snapshot["requested"]);
        Assert.Equal(2, snapshot["served"]);
    }

    private static IReadOnlyList<Evidence> Inspected() => new[]
    {
        Evidence.Create(EvidenceKinds.Inspection, deterministic: false, passed: true, "m", detail: "x"),
    };

    private static IReadOnlyList<ArtifactConsumption> VerifierRead() => new[]
    {
        new ArtifactConsumption
        {
            ArtifactId = "a1", ContentHash = "h", Schema = ArtifactSchemas.FileSet,
            MissionId = "m", ConsumerRole = "verifier",
        },
    };
}
