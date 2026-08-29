using Anthill.Core.Agents;
using Anthill.Core.Domain;
using Anthill.Core.Missions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.98 — WHICH WORKER SERVES A TASK, AND WHAT DECIDED IT.
///
/// THE DEFECT THIS FILE PINS is not that capability resolution was wrong. It is that capability
/// resolution was UNREACHABLE. The branch was written in `PlanningService.CreatePlan`, guarded by
/// `if (string.IsNullOrWhiteSpace(task.AssignedWorker))` — and `Planner.CreateTasks` fills that
/// field on every path before it returns. The code compiled, read correctly, was covered by
/// nothing, and never executed once: the audit acceptance test still resolved
/// `researcher.mission_researcher` from the word "missions" with the whole capability system in
/// the build. "Declared and reaching nobody", the same shape as the phantom tools (ADR-006) and
/// the unreachable operator switches, found again — this time by a test that entered at the
/// conversation instead of at the unit under construction.
///
/// So the tests below assert two different things, deliberately: the RULE (what the resolver
/// decides, and on what evidence) and the WIRING (that the layer which actually fills a blank
/// worker is the one that consults the rule). A rule test alone is what passed while the feature
/// did nothing.
/// </summary>
public class WorkerResolutionTests
{
    private static readonly IReadOnlyList<string> AuditCapabilities = MissionIntake.SystemAuditCapabilities;

    // ---- the rule ------------------------------------------------------------------------------

    /// <summary>
    /// The exact live disagreement: an audit that says "missions" in passing. The keyword resolver
    /// routes it to the mission-history researcher; the mission's declared capability routes it to
    /// the researcher that reads the repository. Capability wins, and the basis says so.
    /// </summary>
    [Fact]
    public void ADeclaredCapability_OutranksAKeywordThatWouldRouteElsewhere()
    {
        var text = "Assess what this colony can do today and whether its missions reach the right workers.";

        var (keyword, keywordDecided) = AntRegistry.ResolveWorker("researcher", "research", text);
        Assert.True(keywordDecided);
        Assert.Equal("researcher.mission_researcher", keyword!.WorkerId);

        var (worker, basis) = WorkerResolution.Resolve("researcher", "research", text, AuditCapabilities);
        Assert.Equal("researcher.repo_researcher", worker!.WorkerId);
        Assert.Equal(WorkerDecisionBasis.Specification, basis);
    }

    /// <summary>
    /// A mission that declared nothing behaves EXACTLY as it did before this release. That is what
    /// makes capability-first resolution safe to ship: it can only change an outcome where the
    /// specification actually stated a requirement.
    /// </summary>
    [Fact]
    public void WithNoDeclaredCapabilities_TheKeywordResolverStillDecides()
    {
        var (worker, basis) = WorkerResolution.Resolve(
            "coder", "patch_proposal", "update the ui canvas layout", requiredCapabilities: null);

        Assert.Equal("coder.ui_coder", worker!.WorkerId);
        Assert.Equal(WorkerDecisionBasis.Keyword, basis);
    }

    /// <summary>
    /// Text that names no specialization, and a specification that requires nothing of this role:
    /// the answer is declaration order, and it is LABELLED a guess. That label is the whole
    /// contract with the pheromone layer — a trail may replace this and nothing else.
    /// </summary>
    [Fact]
    public void WhereNothingSaysAnything_TheBasisIsDefault()
    {
        var (worker, basis) = WorkerResolution.Resolve(
            "coder", "patch_proposal", "adjust the value", requiredCapabilities: null);

        Assert.Equal("coder.backend_coder", worker!.WorkerId);
        Assert.Equal(WorkerDecisionBasis.Default, basis);
    }

    /// <summary>
    /// A capability no worker in the role declares narrows NOTHING rather than emptying the field.
    /// A verifier task on an audit mission must still get a verifier — a specification that
    /// silently left a task with no worker would be this system deciding by omission.
    /// </summary>
    [Fact]
    public void CompatibleCandidates_NarrowsWhenItCan_AndNeverEmptiesTheRole()
    {
        // The two researchers an audit can use, and NOT the third. The mission researcher reads
        // history and declares `recall_mission_history` alone — an audit requires none of what it
        // can do, so a trail cannot reach it here however strong its record. That narrowing IS the
        // rule: reputation ranks compatible choices and never creates compatibility.
        var researchers = WorkerResolution.CompatibleCandidates("researcher", AuditCapabilities);
        Assert.Equal(new[] { "researcher.repo_researcher", "researcher.runtime_researcher" },
            researchers.Select(w => w.WorkerId).ToArray());

        // The coder role declares none of an audit's capabilities. The field stays whole.
        var coders = WorkerResolution.CompatibleCandidates("coder", AuditCapabilities);
        Assert.Equal(AntRegistry.ByRole["coder"].Workers.Count, coders.Count);

        Assert.Empty(WorkerResolution.CompatibleCandidates("no_such_role", AuditCapabilities));
    }

    /// <summary>The specification's requirements reach the task, and the basis rides with it.</summary>
    [Fact]
    public void Assign_RecordsTheBasisOnTheTask()
    {
        var specification = MissionIntake.Resolve(
            "Assess what this colony can do today and whether its missions reach the right workers.");
        Assert.Equal(MissionSpecification.SystemAuditClass, specification.MissionClass);

        var task = new Anthill.Core.Domain.Task
        {
            AssignedAnt = "researcher", TaskType = "research",
            Title = "Inspect the implementation",
            Description = "Read the repository to establish what is implemented and wired.",
        };
        WorkerResolution.Assign(task, specification.OriginalRequest, specification);

        Assert.Equal("researcher.repo_researcher", task.AssignedWorker);
        Assert.Equal(WorkerDecisionBasis.Specification, task.WorkerBasis);
    }

    /// <summary>
    /// A WORKER THE PLAN NAMED IS A PROPOSAL. ADR-008's division — the model proposes structure, a
    /// deterministic gate decides what is kept — applied to the second door into the same defect:
    /// an explicitly named worker used to skip resolution entirely, so a planner could bypass the
    /// capability system simply by being specific.
    /// </summary>
    [Fact]
    public void APlannedWorkerThatServesNoRequiredCapability_IsRepaired()
    {
        var task = new Anthill.Core.Domain.Task
        {
            AssignedAnt = "researcher", TaskType = "research",
            AssignedWorker = "researcher.mission_researcher",
        };

        Assert.Equal("researcher.mission_researcher", WorkerResolution.RepairIncompatible(task, AuditCapabilities));
        Assert.Equal("researcher.repo_researcher", task.AssignedWorker);
        Assert.Equal(WorkerDecisionBasis.Specification, task.WorkerBasis);
    }

    /// <summary>
    /// And it repairs NOTHING ELSE. A compatible choice stands, a mission that declared nothing
    /// stands, and a coding mission is untouched — the narrowness is what makes this safe to apply
    /// to every plan rather than only to audits.
    /// </summary>
    [Fact]
    public void RepairIncompatible_LeavesEveryOtherPlanAlone()
    {
        var compatible = new Anthill.Core.Domain.Task
        {
            AssignedAnt = "builder", AssignedWorker = "builder.result_compiler",
        };
        Assert.Null(WorkerResolution.RepairIncompatible(compatible, AuditCapabilities));
        Assert.Equal("builder.result_compiler", compatible.AssignedWorker);

        // A mission that declared no capabilities cannot repair anything: there is nothing to be
        // incompatible WITH, and this is every mission that ran before v0.3.8.98.
        var undeclared = new Anthill.Core.Domain.Task
        {
            AssignedAnt = "coder", AssignedWorker = "coder.docs_coder",
        };
        Assert.Null(WorkerResolution.RepairIncompatible(undeclared, requiredCapabilities: null));
        Assert.Null(WorkerResolution.RepairIncompatible(undeclared, Array.Empty<string>()));
        Assert.Equal("coder.docs_coder", undeclared.AssignedWorker);

        // A role serving none of the required capabilities keeps whatever the plan named — the
        // coder is there for a reason the specification does not describe, and silently moving it
        // would be this gate deciding a planning question it was not asked.
        var unrelated = new Anthill.Core.Domain.Task
        {
            AssignedAnt = "coder", AssignedWorker = "coder.ui_coder",
        };
        Assert.Null(WorkerResolution.RepairIncompatible(unrelated, AuditCapabilities));
        Assert.Equal("coder.ui_coder", unrelated.AssignedWorker);
    }

    /// <summary>
    /// A TASK'S OWN CAPABILITY OUTRANKS THE MISSION'S LIST, and it has to.
    ///
    /// An audit declares `inspect_repository` first, so every researcher task resolves to the
    /// repository researcher — including the runtime-inspection step the class inserts, which would
    /// then be served by a worker that cannot read live state at all, silently, while the capability
    /// it was created for went unused. A step that names its own requirement asks the narrower and
    /// more accurate question.
    /// </summary>
    [Fact]
    public void ATasksOwnCapability_DecidesBeforeTheMissionsList()
    {
        var spec = MissionIntake.Resolve(
            "Assess what this colony can do today and whether its missions reach the right workers.");

        var repository = new Anthill.Core.Domain.Task { AssignedAnt = "researcher", TaskType = "research" };
        WorkerResolution.Assign(repository, spec.OriginalRequest, spec);
        Assert.Equal("researcher.repo_researcher", repository.AssignedWorker);

        var runtime = new Anthill.Core.Domain.Task
        {
            AssignedAnt = "researcher", TaskType = "research",
            RequiredCapability = WorkerCapabilities.InspectRuntimeState,
        };
        WorkerResolution.Assign(runtime, spec.OriginalRequest, spec);
        Assert.Equal("researcher.runtime_researcher", runtime.AssignedWorker);
        Assert.Equal(WorkerDecisionBasis.Specification, runtime.WorkerBasis);
    }

    /// <summary>
    /// AND THE CAPABILITY HAS A SERVER. A requirement no worker declares is a declaration reaching
    /// nobody — the defect this release keeps finding — so every capability the audit class demands
    /// must resolve to a worker that says it can do it.
    /// </summary>
    [Fact]
    public void EveryCapabilityAnAuditRequires_HasAWorkerThatDeclaresIt()
    {
        foreach (var capability in MissionIntake.SystemAuditCapabilities)
            Assert.True(
                AntRegistry.ByWorker.Values.Any(w => w.Enabled && w.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase)),
                $"the audit class requires '{capability}' and no enabled worker declares it — "
              + "a requirement nothing can serve is a declaration reaching nobody.");
    }

    // ---- the wiring ----------------------------------------------------------------------------

    /// <summary>
    /// THE REGRESSION GUARD. `Planner.AssignDefaultWorkers` is the first and, on every planner
    /// path, the ONLY place a blank worker is filled. If it resolves without the specification —
    /// as it did until this release, by calling `AntRegistry.DefaultWorkerFor` directly — then
    /// every capability-aware resolver downstream of it is dead code, whatever it says.
    ///
    /// Asserted on the member's own body rather than the file, so an unrelated legitimate use of
    /// the keyword resolver elsewhere in the planner (the static fallback plan authors its own
    /// workers) cannot make this pass or fail for the wrong reason.
    /// </summary>
    [Fact]
    public void ThePlannersWorkerFill_GoesThroughTheOneResolver()
    {
        var code = SourceText.CodeOnly(File.ReadAllText(
            Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Core", "Planning", "Planner.cs")));

        var body = SourceText.MemberBody(code, code.IndexOf("List<Task> AssignDefaultWorkers", StringComparison.Ordinal));
        Assert.NotEqual("", body);

        Assert.Contains("WorkerResolution.Assign", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultWorkerFor", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveWorker", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the trail rule reads the recorded basis instead of re-deriving one. Re-deriving is how
    /// two layers came to disagree about whether a worker had been chosen at all.
    /// </summary>
    [Fact]
    public void TheTrailRule_ReadsTheRecordedBasis()
    {
        var code = SourceText.CodeOnly(File.ReadAllText(
            Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Core", "Orchestration", "PlanningService.cs")));

        Assert.Contains("task.WorkerBasis == Domain.WorkerDecisionBasis.Default", code, StringComparison.Ordinal);
        Assert.Contains("WorkerResolution.CompatibleCandidates", code, StringComparison.Ordinal);
    }
}
