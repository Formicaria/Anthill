using Anthill.Core.Missions;
using Anthill.Core.Planning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A PLAN THAT COULD NEVER DELIVER IS REFUSED BEFORE IT RUNS. v0.3.8.104.
///
/// Every gate this program built before now answers "did the mission deliver", and answers it after
/// the model calls, the tool dispatches and the operator's wait are already spent. Preflight asks
/// the half that is answerable in advance — not whether the answer will be good, which is semantic
/// and stays outside, but whether anything in the plan is positioned to produce it at all.
///
/// THE ORDERING IS THE SAFETY. It runs after `EnsureClassCoverage`, which SUPPLIES what a class
/// requires and the planner omitted. `.98` recorded that deliberately: its exit line said "a missing
/// builder fails preflight" and the implementation supplied the builder instead, because refusing a
/// plan for lacking a step the runtime knows how to add punishes the operator for the planner's
/// omission. So anything preflight rejects that coverage could have added is a bug in coverage, and
/// these tests are written against plans coverage genuinely cannot repair.
/// </summary>
public class MissionPreflightTests
{
    private static Anthill.Core.Domain.Task Task(string role, string title, string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString(),
        Title = title,
        Description = title,
        AssignedAnt = role,
        TaskType = role == "builder" ? "build_answer" : "research",
    };

    /// <summary>A plan that passes: something produces, something assembles, something verifies.</summary>
    private static List<Anthill.Core.Domain.Task> Sound() => new()
    {
        Task("researcher", "Inspect the repository"),
        Task("builder", "Compile the answer"),
        Task("verifier", "Verify the answer"),
    };

    private static MissionSpecification Audit() =>
        MissionIntake.Resolve("Audit this repository: what is implemented, and what is enabled now?");

    [Fact]
    public void ASoundPlan_Passes()
    {
        var result = MissionPreflight.Check(Sound(), Audit());
        Assert.True(result.Ok, result.Explanation);
    }

    /// <summary>
    /// A DELIVERABLE NOTHING CLAIMS. Only checked where the plan claimed SOME deliverables — a plan
    /// that attributes nothing is the honest weaker case the ledger already records as `inferred`,
    /// and refusing it would reject every plan a model wrote without deliverable ids.
    /// </summary>
    [Fact]
    public void MissionPreflight_RejectsUnproducedDeliverable()
    {
        // AUDIT-CLASSIFIED AND MULTI-QUESTION. The first draft asked three questions with no
        // assessment verb, which intake resolves to `general` — and a general specification carries
        // NO deliverables, so the test asserted against an empty list and failed for a reason that
        // had nothing to do with preflight. A deliverable check needs a mission that HAS them.
        var specification = MissionIntake.Resolve(
            "Audit this repository: what is implemented? What is enabled right now? "
          + "Which workers actually ran?");
        Assert.Equal(MissionSpecification.SystemAuditClass, specification.MissionClass);
        Assert.True(specification.Deliverables.Count >= 3,
            $"expected at least three deliverables, got {specification.Deliverables.Count}");

        var tasks = Sound();
        // The plan claims the first deliverable and says nothing about the rest.
        tasks[0].DeliverableIds.Add(specification.Deliverables[0].Id);

        var result = MissionPreflight.Check(tasks, specification);

        Assert.False(result.Ok);
        Assert.Contains(result.Blockers, b => b.Code == MissionPreflight.Codes.UnproducedDeliverable);
        Assert.Contains(specification.Deliverables[1].Id,
            result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// A RECOGNIZED CLASS WITH NO VERIFIER. Its success criterion IS its integrity gate, and every
    /// one of those gates reads what a verifier consumed — so this plan could not satisfy its own
    /// class however well the work went.
    /// </summary>
    [Fact]
    public void MissionPreflight_RejectsUnverifiedCriterion()
    {
        var tasks = Sound().Where(t => t.AssignedAnt != "verifier").ToList();

        var result = MissionPreflight.Check(tasks, Audit());

        Assert.False(result.Ok);
        Assert.Contains(result.Blockers, b => b.Code == MissionPreflight.Codes.UnverifiedCriterion);
        Assert.Contains("system_audit", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void MissionPreflight_RejectsInvalidDependency()
    {
        var tasks = Sound();
        tasks[1].DependsOn.Add("a-task-that-is-not-in-this-plan");

        var result = MissionPreflight.Check(tasks, Audit());

        Assert.False(result.Ok);
        Assert.Contains(result.Blockers, b => b.Code == MissionPreflight.Codes.InvalidDependency);
    }

    [Fact]
    public void MissionPreflight_RejectsOrphanedTask()
    {
        var tasks = Sound();
        tasks[0].AssignedAnt = "";

        var result = MissionPreflight.Check(tasks, Audit());

        Assert.False(result.Ok);
        Assert.Contains(result.Blockers, b => b.Code == MissionPreflight.Codes.OrphanedTask);
    }

    /// <summary>
    /// THE CAPABILITY BLOCKER, and it is a different KIND of refusal. The plan is well formed; the
    /// colony simply cannot do a thing it requires, and it will not be able to on a retry — which
    /// is why the outcome is `blocked_missing_capability` rather than a failure.
    /// </summary>
    [Fact]
    public void MissingCompatibleWorker_ReturnsBlockedMissingCapability()
    {
        var tasks = Sound();
        tasks[0].RequiredCapability = "translate_to_latin";   // nothing declares it, deliberately

        var result = MissionPreflight.Check(tasks, Audit());

        Assert.False(result.Ok);
        Assert.True(result.IsCapabilityBlocked,
            "a plan requiring a capability nothing serves is a CAPABILITY block, not a malformed "
          + "plan — the two want different outcomes and different operator responses.");
        Assert.Contains("translate_to_latin", result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND A CAPABILITY THE COLONY DOES HAVE IS NOT A BLOCKER. The negative above is only worth
    /// something if the positive resolves — otherwise it would pass for any string at all.
    /// </summary>
    [Fact]
    public void ACapabilityAWorkerDeclares_IsNotABlocker()
    {
        var tasks = Sound();
        tasks[0].RequiredCapability = WorkerCapabilities.InspectRepository;

        var result = MissionPreflight.Check(tasks, Audit());

        Assert.True(result.Ok, result.Explanation);
    }

    /// <summary>
    /// AN UNCLASSIFIED MISSION IS CHECKED STRUCTURALLY AND NOTHING MORE. It declares no
    /// deliverables to produce and no class to verify, so inventing requirements for it would
    /// refuse missions that have run since the beginning — the coding lane above all.
    /// </summary>
    [Fact]
    public void AnUnclassifiedMission_IsOnlyCheckedStructurally()
    {
        var general = MissionSpecification.General("fix the build");

        Assert.True(MissionPreflight.Check(Sound(), general).Ok);
        // Still structural: a broken dependency is broken whatever the class.
        var broken = Sound();
        broken[1].DependsOn.Add("nope");
        Assert.False(MissionPreflight.Check(broken, general).Ok);
    }

    /// <summary>Every blocker names its subject — a refusal an operator cannot locate is one they
    /// cannot act on, which is the rule `.99` paid for by finding the explanation had never been
    /// persisted at all.</summary>
    [Fact]
    public void EveryBlocker_NamesWhatItIsAbout()
    {
        var tasks = Sound();
        tasks[0].RequiredCapability = "translate_to_latin";
        tasks[1].DependsOn.Add("missing");

        foreach (var blocker in MissionPreflight.Check(tasks, Audit()).Blockers)
        {
            Assert.False(string.IsNullOrWhiteSpace(blocker.Code));
            Assert.False(string.IsNullOrWhiteSpace(blocker.Subject));
            Assert.True(blocker.Detail.Length > 40,
                $"blocker '{blocker.Code}' explains itself in {blocker.Detail.Length} characters; "
              + "an operator needs to know what to do about it.");
        }
    }
}
