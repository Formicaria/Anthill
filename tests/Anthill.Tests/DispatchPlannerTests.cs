using Anthill.Core.Agents;
using Anthill.Core.Missions;
using Anthill.Core.Planning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE PRE-DISPATCH PLANNING STAGE. v0.3.8.118.
///
/// These run against the REAL role registry — `AntExecutionCatalog.Contracts` — rather than a hand-built
/// fixture, on purpose. `docs/GUARDS.md` puts a typed registry above every weaker tier, and the
/// defect this stage exists to close was precisely a second, hand-maintained idea of what the
/// colony can do drifting away from the registry that actually decides. A fixture would let these
/// pass while production refused the same request.
///
/// WHAT THEY ARE ABOUT. The live tests found that a request the runtime could not execute was
/// silently converted into researcher `section_analysis` tasks. That is worse than a failure: a
/// refusal costs an operator a minute, while a substitution produces plausible output against a
/// question nobody asked, and there was no record afterwards saying which had happened. Every fact
/// below is one shape of "refuse instead of substitute", plus the compatibility case that keeps
/// every mission which ran before this stage existed running exactly as it did.
/// </summary>
public class DispatchPlannerTests
{
    private const string Mission = "msn_test_0001";
    private const string When = "2026-09-03T00:00:00Z";

    /// <summary>Every fact here depends on the registry being real and non-trivial.</summary>
    [Fact]
    public void TheseTests_AreReadingTheRealRoleRegistry()
    {
        var reg = AntExecutionCatalog.Contracts;

        Assert.True(reg.Count >= 8,
            $"the role registry reports {reg.Count} contracts; every assertion below would prove "
          + "little against a colony this small.");

        // The two roles these tests name, and the task types they name, really are registered —
        // so a refusal below means the planner refused, not that the fixture was wrong.
        Assert.True(reg.ContainsKey("researcher"), "no `researcher` role is registered.");
        Assert.True(reg.ContainsKey("verifier"), "no `verifier` role is registered.");
        Assert.Contains("research", reg["researcher"].SupportedTaskTypes);
        Assert.Contains("verification", reg["verifier"].SupportedTaskTypes);

        // And the type these tests use as the UNSUPPORTED one genuinely is unsupported, or the
        // negative facts would be passing for the wrong reason.
        Assert.DoesNotContain(reg.Values, c => c.SupportedTaskTypes.Contains("deep_competitive_scan"));
    }

    // -------------------------------------------------------------------------------------------
    // Refuse, never substitute
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE HEADLINE FACT. A mission asking for a task type the colony cannot execute must stop
    /// before dispatch. It must not become section analysis, and it must not become anything else
    /// the planner finds convenient.
    ///
    /// The old path had no opinion about this because it had no structured request to have an
    /// opinion about: `MissionRequest` carried `{ Goal, IdempotencyKey }`, the goal was long, and
    /// `Planner.IsLongInput` chunked it. The operator was never told.
    /// </summary>
    [Fact]
    public void AnUnsupportedTaskType_StopsPlanningAndDoesNotFallBackToSectionAnalysis()
    {
        var requested = new RequestedWorkflow(
            Tasks: [new RequestedTask("Scan the competitive landscape", TaskType: "deep_competitive_scan")],
            RequiredRoles: [], OptionalRoles: [], OutputSchema: null, PermissionMode: null);

        var result = DispatchPlanner.Plan(Mission, "scan the market", requested, nowIso: When);

        Assert.False(result.Ok);
        Assert.Null(result.Plan);
        Assert.Contains(result.Blockers, b => b.Code == PlanBlocker.Codes.UnsupportedTaskType);

        // The refusal names the operator's own string, so it is actionable rather than "planning failed".
        Assert.Contains(result.Blockers, b => b.Subject == "deep_competitive_scan");
        Assert.Contains("deep_competitive_scan", result.Explanation);
    }

    /// <summary>
    /// An unsupported output schema fails planning rather than silently producing another one.
    /// Handing back a different shape than the one asked for is the same defect as substituting a
    /// task, one layer along, and is harder to notice because the content still reads plausibly.
    /// </summary>
    [Fact]
    public void AnUnsupportedOutputSchema_FailsPlanningRatherThanProducingADifferentOne()
    {
        var requested = new RequestedWorkflow(
            Tasks: [new RequestedTask("Write it up", TaskType: "synthesis", Role: "builder")],
            RequiredRoles: [], OptionalRoles: [],
            OutputSchema: "schema_no_role_produces", PermissionMode: null);

        var result = DispatchPlanner.Plan(Mission, "write it up", requested, nowIso: When);

        Assert.False(result.Ok);
        Assert.Null(result.Plan);
        Assert.Contains(result.Blockers, b => b.Code == PlanBlocker.Codes.UnsupportedOutputSchema
                                           && b.Subject == "schema_no_role_produces");
    }

    /// <summary>A role the registry has never heard of is refused by name, with the registered set
    /// in the detail — the standing rule that a refusal must name the layer that said no.</summary>
    [Fact]
    public void AnUnknownRole_IsRefusedAndTheRefusalNamesIt()
    {
        var requested = new RequestedWorkflow(
            Tasks: [], RequiredRoles: ["strategist"], OptionalRoles: [],
            OutputSchema: null, PermissionMode: null);

        var result = DispatchPlanner.Plan(Mission, "do the thing", requested, nowIso: When);

        Assert.False(result.Ok);
        Assert.Contains(result.Blockers, b => b.Code == PlanBlocker.Codes.UnknownRole && b.Subject == "strategist");
    }

    /// <summary>
    /// A real role paired with a task type it does not declare is a mismatch, not something to
    /// quietly reassign. Reassignment is how `researcher.mission_researcher` ended up running work
    /// nobody asked it to run.
    /// </summary>
    [Fact]
    public void ARoleThatDoesNotSupportTheRequestedTaskType_IsRefusedRatherThanReassigned()
    {
        var requested = new RequestedWorkflow(
            Tasks: [new RequestedTask("Research it", TaskType: "research", Role: "builder")],
            RequiredRoles: [], OptionalRoles: [], OutputSchema: null, PermissionMode: null);

        var result = DispatchPlanner.Plan(Mission, "research it", requested, nowIso: When);

        Assert.False(result.Ok);
        Assert.Contains(result.Blockers, b => b.Code == PlanBlocker.Codes.RoleTaskTypeMismatch);
    }

    /// <summary>
    /// A descriptive label that is not a registered task type is refused rather than fuzzily
    /// matched. Near-matching is exactly how a request becomes something adjacent to itself without
    /// anyone being told.
    /// </summary>
    [Fact]
    public void ADescriptiveLabel_IsNotFuzzyMatchedToANearbyTaskType()
    {
        var requested = new RequestedWorkflow(
            Tasks: [new RequestedTask("researching the competitors")],   // not a registered type
            RequiredRoles: [], OptionalRoles: [], OutputSchema: null, PermissionMode: null);

        var result = DispatchPlanner.Plan(Mission, "research", requested, nowIso: When);

        Assert.False(result.Ok);
        Assert.Contains(result.Blockers, b => b.Code == PlanBlocker.Codes.UnresolvableTaskLabel);
    }

    /// <summary>A dependency naming a step that is not in the request is refused — a plan whose
    /// edges point at nothing is not a plan.</summary>
    [Fact]
    public void ADependencyOnAStepThatWasNotRequested_IsRefused()
    {
        var requested = new RequestedWorkflow(
            Tasks: [new RequestedTask("research", TaskType: "research", DependsOn: ["a step nobody asked for"])],
            RequiredRoles: [], OptionalRoles: [], OutputSchema: null, PermissionMode: null);

        var result = DispatchPlanner.Plan(Mission, "research", requested, nowIso: When);

        Assert.False(result.Ok);
        Assert.Contains(result.Blockers, b => b.Code == PlanBlocker.Codes.UnknownDependency);
    }

    // -------------------------------------------------------------------------------------------
    // Honour what resolves, and change nothing for missions that ask for nothing
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// COMPATIBILITY, AND IT IS THE FACT MOST LIKELY TO BE BROKEN BY A LATER CHANGE. Nearly every
    /// mission arrives with no structured workflow, exactly as they all did before this type
    /// existed. Those must plan, not be refused for requesting nothing — and the plan must record
    /// that the PLANNER chose, which is the fact that was previously nowhere.
    /// </summary>
    [Fact]
    public void AMissionThatRequestsNoWorkflow_IsNotRefusedAndRecordsThatThePlannerChose()
    {
        var result = DispatchPlanner.Plan(Mission, "summarise the repo", RequestedWorkflow.None, nowIso: When);

        Assert.True(result.Ok, result.Explanation);
        Assert.NotNull(result.Plan);
        Assert.Equal(DispatchPlanner.Strategies.PlannerChosen, result.Plan!.Strategy);
        Assert.NotEmpty(result.Plan.StrategyReason);
        Assert.Empty(result.Plan.Tasks);

        // A null workflow is the same as None — a caller that predates the field must not crash.
        Assert.True(DispatchPlanner.Plan(Mission, "summarise the repo", null, nowIso: When).Ok);
    }

    /// <summary>
    /// A request that resolves is honoured, and the operator's own words survive. Losing the label
    /// leaves an operator unable to find their own step in the record; treating the label AS the
    /// type is what let an arbitrary name be handed to a worker as if it were executable. Both
    /// halves are kept, separately.
    /// </summary>
    [Fact]
    public void ARequestedWorkflowThatResolves_KeepsTheLabelAndDispatchesTheResolvedType()
    {
        var requested = new RequestedWorkflow(
            Tasks:
            [
                new RequestedTask("Gather the source material", TaskType: "research", Role: "researcher"),
                new RequestedTask("Write it up", TaskType: "synthesis", Role: "builder",
                                  DependsOn: ["Gather the source material"]),
            ],
            // The verifier is REQUIRED but has no authored step: the runtime inserts it. That
            // combination is the one the first `.118` run got wrong, and it is exercised here.
            RequiredRoles: ["researcher", "builder", "verifier"], OptionalRoles: [],
            OutputSchema: null, PermissionMode: null);

        var result = DispatchPlanner.Plan(Mission, "gather and write up", requested, nowIso: When);

        Assert.True(result.Ok, result.Explanation);
        var plan = result.Plan!;
        Assert.Equal(DispatchPlanner.Strategies.OperatorRequested, plan.Strategy);
        Assert.Equal(2, plan.Tasks.Count);

        var first = plan.Tasks[0];
        Assert.Equal("Gather the source material", first.Label);   // the operator's words, verbatim
        Assert.Equal("research", first.TaskType);                  // what the runtime dispatches on
        Assert.Equal("researcher", first.Role);
        Assert.Equal(PlannedTask.Sources.Requested, first.Source);

        // The dependency resolved to the FIRST task's id, not to the label it was written as.
        var second = plan.Tasks[1];
        Assert.Single(second.DependsOnTaskIds);
        Assert.Equal(first.TaskId, second.DependsOnTaskIds[0]);

        // Nothing is unmet: two roles are dispatched and the third is guaranteed by policy.
        Assert.Empty(plan.UnmetRequiredRoles);
        Assert.Contains("researcher", plan.DispatchedRoles);
        Assert.Contains("builder", plan.DispatchedRoles);
        Assert.DoesNotContain("verifier", plan.DispatchedRoles);
    }

    // -------------------------------------------------------------------------------------------
    // The record the rest of the release is measured against
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// EVERY REGISTERED ROLE GETS A ROW, INCLUDING THE ONES THAT DO NOT RUN, AND EVERY ROW HAS A
    /// REASON. `Registered` was previously being read as `participated`, which is how a mission
    /// reported roles as having taken part when no task record proved invocation.
    ///
    /// `Completed` and `Failed` must stay false at planning time: a plan cannot know them, and a
    /// type that let it pretend to would reintroduce the defect it exists to close.
    /// </summary>
    [Fact]
    public void EveryRegisteredRole_GetsADispositionWithAReasonAndNoInventedOutcome()
    {
        var result = DispatchPlanner.Plan(Mission, "anything", RequestedWorkflow.None, nowIso: When);
        var plan = result.Plan!;

        Assert.Equal(AntExecutionCatalog.Contracts.Count, plan.Roles.Count);
        Assert.All(plan.Roles, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
        Assert.All(plan.Roles, r => Assert.True(r.Registered));

        // Nothing has run yet, so nothing may claim to have.
        Assert.All(plan.Roles, r => Assert.False(r.Dispatched));
        Assert.All(plan.Roles, r => Assert.False(r.Completed));
        Assert.All(plan.Roles, r => Assert.False(r.Failed));
    }

    /// <summary>
    /// The closure requirements are written down BEFORE the mission runs, so the completion
    /// decision is measured against something recorded in advance rather than reconstructed
    /// afterwards. `checks: 0` beside `status: complete` is what the absence of this looked like.
    /// </summary>
    [Fact]
    public void EveryPlan_RecordsWhatCompletionWillRequire()
    {
        var plan = DispatchPlanner.Plan(Mission, "anything", RequestedWorkflow.None, nowIso: When).Plan!;

        Assert.Contains(DispatchPlanner.ClosureRequirements.ChecksExist, plan.ClosureRequirements);
        Assert.Contains(DispatchPlanner.ClosureRequirements.VerifierExecuted, plan.ClosureRequirements);
        Assert.Contains(DispatchPlanner.ClosureRequirements.VerifierArtifactReachedCompiler, plan.ClosureRequirements);
        Assert.Equal(Mission, plan.MissionId);
        Assert.Equal(When, plan.PlannedAt);
        Assert.Equal(DispatchPlan.Version, plan.PlannerVersion);
    }

    /// <summary>
    /// The stage is deterministic in everything except task ids. Two identical requests must
    /// produce the same shape, or a plan is not something a later stage can be measured against.
    /// </summary>
    [Fact]
    public void PlanningIsDeterministic_TheSameRequestPlansTheSameShapeTwice()
    {
        RequestedWorkflow Req() => new(
            Tasks: [new RequestedTask("Gather", TaskType: "research", Role: "researcher")],
            RequiredRoles: ["researcher"], OptionalRoles: [], OutputSchema: null, PermissionMode: null);

        var a = DispatchPlanner.Plan(Mission, "gather", Req(), nowIso: When).Plan!;
        var b = DispatchPlanner.Plan(Mission, "gather", Req(), nowIso: When).Plan!;

        Assert.Equal(a.Strategy, b.Strategy);
        Assert.Equal(a.StrategyReason, b.StrategyReason);
        Assert.Equal(a.Tasks.Select(t => (t.Label, t.TaskType, t.Role, t.Source)),
                     b.Tasks.Select(t => (t.Label, t.TaskType, t.Role, t.Source)));
        Assert.Equal(a.Roles.Select(r => (r.RoleId, r.Reason, r.Dispatched)),
                     b.Roles.Select(r => (r.RoleId, r.Reason, r.Dispatched)));
    }
    // -------------------------------------------------------------------------------------------
    // Policy-inserted roles — the distinction the first `.118` test run surfaced
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// REQUIRING A POLICY-INSERTED ROLE IS SATISFIED, NOT REFUSED — and this fact exists because the
    /// planner originally got it backwards. `AntExecutionCatalog` documents the mode as "inserted by
    /// POLICY whenever its inputs exist, whatever the plan says … the steps a plan must not be able
    /// to omit", and names tester, soldier and verifier.
    ///
    /// Refusing a mission for requiring the one thing the runtime guarantees would refuse exactly
    /// the missions this release most wants to succeed: the ones asking to be verified.
    /// </summary>
    [Fact]
    public void RequiringAPolicyInsertedRole_IsSatisfiedByTheRuntimeRatherThanRefused()
    {
        Assert.Equal(SchedulingMode.PolicyInserted, AntExecutionCatalog.Contracts["verifier"].Scheduling);

        var requested = new RequestedWorkflow(
            Tasks: [new RequestedTask("Gather", TaskType: "research", Role: "researcher")],
            RequiredRoles: ["verifier"], OptionalRoles: [], OutputSchema: null, PermissionMode: null);

        var result = DispatchPlanner.Plan(Mission, "gather", requested, nowIso: When);

        Assert.True(result.Ok, result.Explanation);
        var verifier = result.Plan!.Roles.Single(r => r.RoleId == "verifier");

        Assert.True(verifier.Required);
        Assert.True(verifier.SatisfiedByPolicy);
        Assert.False(verifier.Dispatchable);          // the planner may not pick it
        Assert.True(verifier.Routable);               // but it is routed and run
        Assert.Equal(RoleDisposition.Reasons.PolicyInserted, verifier.Reason);

        // Required, absent from the plan, and NOT unmet — because something else guarantees it.
        Assert.Empty(result.Plan.UnmetRequiredRoles);
    }

    /// <summary>
    /// …but AUTHORING A STEP for one is refused. The role is guaranteed; an operator-written task
    /// for it is a different thing and would duplicate what policy already adds. Two verifier tasks
    /// that disagree are worse than either alone, and nothing tells the operator which verdict
    /// closed the mission.
    /// </summary>
    [Fact]
    public void AuthoringAStepForAPolicyInsertedRole_IsRefusedWithTheAlternativeNamed()
    {
        var requested = new RequestedWorkflow(
            Tasks: [new RequestedTask("Check it holds up", TaskType: "verification", Role: "verifier")],
            RequiredRoles: [], OptionalRoles: [], OutputSchema: null, PermissionMode: null);

        var result = DispatchPlanner.Plan(Mission, "check it", requested, nowIso: When);

        Assert.False(result.Ok);
        var blocker = Assert.Single(result.Blockers, b => b.Code == PlanBlocker.Codes.RoleIsPolicyInserted);
        Assert.Equal("verifier", blocker.Subject);
        // A refusal that does not say what to do instead is a refusal an operator cannot act on.
        Assert.Contains("required_roles", blocker.Detail);
    }

    /// <summary>
    /// A lifecycle-only role IS refused when required. The medic runs on a typed retryable failure
    /// and the archivist after finalization; neither can be promised in advance, so requiring one is
    /// a request nothing can honour — the opposite conclusion to the policy-inserted case, from the
    /// same field.
    /// </summary>
    [Fact]
    public void RequiringALifecycleOnlyRole_IsRefusedBecauseNothingCanPromiseIt()
    {
        var lifecycle = AntExecutionCatalog.Contracts
            .Where(kv => kv.Value.Scheduling is SchedulingMode.FailureTriggered or SchedulingMode.PostFinalization)
            .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.NotEmpty(lifecycle);   // vacuity floor: such a role really exists

        var requested = new RequestedWorkflow(
            Tasks: [], RequiredRoles: [lifecycle[0]], OptionalRoles: [],
            OutputSchema: null, PermissionMode: null);

        var result = DispatchPlanner.Plan(Mission, "anything", requested, nowIso: When);

        Assert.False(result.Ok);
        Assert.Contains(result.Blockers, b => b.Code == PlanBlocker.Codes.RoleUnavailable
                                           && b.Subject == lifecycle[0]);
    }
}
