using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The twenty qualification scenarios, as ONE machine-checked ledger. v0.3.8.57.
///
/// PLAN.md Stage F, AUTONOMY-10 Phase 5, TRAINING_MISSIONS.md and QA-CHECKLIST.md all converge on the
/// same list, and the repository already proves most of it — spread across forty test files, with
/// `AuditScenarioTests` holding a prose index in a doc comment. A prose index cannot be wrong in a
/// way anything notices: a cited test can be renamed, deleted or reduced to a stub and the comment
/// still reads exactly as confidently.
///
/// So this is the index, executable. Every scenario names the file that proves it; the test asserts
/// those files exist and mention the scenario's subject. Scenarios with no proof are recorded as
/// OPEN with the reason, and the test asserts they are still open — a scenario that quietly acquires
/// a proof without the ledger changing is drift in the direction that feels like progress.
///
/// WHAT THIS DOES NOT CLAIM. Citing a file is weaker than running the scenario, and it is not a
/// substitute for the composed Queen-driven run that scenarios 3, 4, 7 and 15 still need. The value
/// is that the twenty are in one place, each with a named owner, and that the list cannot rot
/// silently. Where a scenario is only partially covered, that is what it says.
/// </summary>
public class QualificationMatrixTests
{
    /// <param name="Proofs">
    /// Test files that prove this scenario. Empty means OPEN — and open is a first-class state here,
    /// not a gap in the data.
    /// </param>
    /// <param name="Note">
    /// For a proved scenario: what the proof actually establishes, which is often narrower than the
    /// scenario's title. For an open one: why, and what closing it needs.
    /// </param>
    private sealed record Scenario(int Number, string Name, string[] Proofs, string Note);

    private static readonly Scenario[] Matrix =
    {
        new(1, "research with real structured sources and cited synthesis",
            new[] { "ColonyAcceptanceTests.cs", "StructuredCoreOutputTests.cs" },
            "Composed research mission; `source_set` carries per-source confidence and `research_brief` "
          + "became a typed artifact at v0.3.8.57. CITATION QUALITY is not asserted — a brief that "
          + "cites its sources badly still parses."),

        new(2, "policy-limited file inspection",
            new[] { "PermissionBoundaryTests.cs", "ToolAuthorizationTests.cs", "WorkspaceToolsTests.cs" },
            "Capability boundary and path guard; a role cannot read outside what its contract grants."),

        new(3, "documentation patch", new string[0],
            "OPEN. ScribeAntTests proves the docs-path restriction and the refusal of non-docs targets, "
          + "but no test drives a docs patch from goal to applied change through the Queen. Needs a "
          + "ScriptedColony script book — the harness exists (ScriptedColony.cs); the scenario does not."),

        new(4, "successful code patch", new[] { "CodePatchLifecycleTests.cs" },
            "Composed through the Queen with the scripted provider. This is the one full lifecycle "
          + "the suite has, and it is what scenarios 3 and 7 should be modelled on."),

        new(5, "UI patch requiring UI Cartographer",
            new[] { "UiChangeGateTests.cs", "UiCartographerAntTests.cs" },
            "v0.3.8.57 makes the map STRUCTURALLY required at dispatch. The gate and the producer are "
          + "each proved; the composed UI-patch lifecycle is not — see scenario 3's note."),

        new(6, "broken patch → Tester → FailureContext → Medic → Coder → fresh retest",
            new[] { "MedicAntTests.cs", "MissionRevisionTests.cs", "BoundedRepairTests.cs" },
            "The repair route, the bound (typed signature since v0.3.8.57), and the rule that a new "
          + "patch set REPLACES the revision so old green evidence cannot ride."),

        new(7, "security violation blocked by Soldier",
            new[] { "SoldierAntTests.cs", "DeterministicBlockTests.cs", "StageBConsequentialTests.cs" },
            "The soldier reads the real patch set and its block cannot be overridden by model text. "
          + "PARTIAL: a composed mission where a soldier block stops a real lifecycle is still open."),

        new(8, "provider outage and malformed provider responses",
            new[] { "ModelReliabilityTests.cs", "ProviderWireFormatTests.cs", "JsonRepairTests.cs",
                    "FullRosterQualificationTests.cs" },
            "Outage classification, malformed-wire handling, and neutral attribution — an ant is not "
          + "blamed for a dead provider."),

        new(9, "tool timeout and whole-process-tree cancellation",
            new[] { "ProcessTreeCancellationTests.cs", "ModelCallCancellationTests.cs", "ToolFailureClassTests.cs" },
            "v0.3.8.57 found five sites that abandoned a running process on timeout. The sweep now "
          + "covers all twelve bounded-wait sites."),

        new(10, "user cancellation", new[] { "ColonyAcceptanceTests.cs", "ModelCallCancellationTests.cs" },
            "Cancellation leaves nothing half-done and is durable across the scheduler."),

        new(11, "runtime restart and mission recovery",
            new[] { "ColonyAcceptanceTests.cs", "AttemptCrashRecoveryTests.cs", "DurableMissionRuntimeTests.cs" },
            "The task graph survives restart; an interrupted attempt is recovered rather than lost."),

        new(12, "base-hash conflict", new[] { "PatchBaseHashTests.cs", "PatchConformanceTests.cs" },
            "Stale and MISSING base both refuse on the live write path (v0.3.8.57), and the "
          + "conformance ledger pins that every applier asks with the full set of facts."),

        new(13, "empty auto-apply allowlist", new[] { "AutoApplyConfigTests.cs", "AutoApplyPolicyTests.cs" },
            "An empty allowlist fails CLOSED — the inert state is no writes, not all writes."),

        new(14, "unverified memory candidate rejection",
            new[] { "ArchivistAntTests.cs", "MemoryCandidateIngestTests.cs", "ScribeArchivistOrderingTests.cs" },
            "Positive procedural memory only from completed_verified; nothing archival auto-promotes."),

        new(15, "legitimate all-twelve-role coverage", new[] { "CodePatchLifecycleTests.cs" },
            "PARTIAL — and the partial is precise, because two rewrites of this entry taught what the "
          + "scenario actually turns on. Two tests in the cited file split the claim. "
          + "AllTwelveRoles_RunThroughTheirRealTriggers_InOneComposedScriptedMission gets all twelve "
          + "through production triggers and proves tester and soldier were INSERTED, not planned. "
          + "AGoalThatEarnsTheRoles_LeavesNoRoleAnsweringThatItHadNothingToDo adds the other clause, "
          + "'no role invoked to satisfy a count': its goal changes a UI route and documents it, so "
          + "the cartographer maps a real console page (and the map is load-bearing — UiChangeGate "
          + "refuses the coder's UI patch without a conformant ui_map) and the web ant runs a real "
          + "search through ScriptedWebSearchTool, which fakes the socket and leaves dedupe, SSRF "
          + "refusal, domain scoring and source persistence real. It asserts no PLANNED role ends "
          + "blocked or failed_permanent, which is the executable form of the clause.\n\n"
          + "WHAT IS STILL MISSING, stated so the citation cannot be read as more than it is: the "
          + "tester's failure is ENVIRONMENTAL — a materialized revision in a temp directory has no "
          + "build — so the medic's trigger is real while the failure's relationship to the change is "
          + "not. Closing that needs an allowlisted check that fails BECAUSE of the proposal. That is "
          + "its own scenario and is named in docs/PLAN.md rather than implied by these two passing."),

        new(16, "failure during every patch transaction position",
            new[] { "AuditScenarioTests.cs", "AutoApplyAtomicityTests.cs" },
            "A set whose second proposal refuses materializes nothing and leaves the tree byte-identical; "
          + "a mid-set write failure rolls the batch back."),

        new(17, "restart during approval/apply/finalization",
            new[] { "FinalizationOrderTests.cs", "ApprovalDedupeTests.cs", "PatchOperatorActionTests.cs" },
            "Finalization replay is idempotent and the archivist claims a ledger entry, so a replayed "
          + "finalization cannot archive twice. PARTIAL: restart mid-APPLY is covered by the rollback "
          + "path rather than by a test that kills the process there."),

        new(18, "duplicate mission and external-action delivery",
            new[] { "DirectorTests.cs", "ApprovalDedupeTests.cs", "AutonomyTests.cs" },
            "Mission idempotency keys and approval dedupe — the same observation delivered twice is "
          + "one effect."),

        new(19, "a moved repository base",
            new[] { "MissionWorkspaceTests.cs", "WorkspaceChangeSetTests.cs", "RepoOpsTests.cs" },
            "Workspace recovery reconciles recorded workspaces against what is on disk at startup."),

        new(20, "direct-agent work excluded from colony learning",
            new[] { "DirectAgentLaneTests.cs", "ChatSpeaksToTheColonyTests.cs" },
            "v0.3.8.53 contained the lane's OUTPUT (unverified `direct_change`, never positive memory); "
          + "v0.3.8.57 refused an agent for the conversation route but left the grant in the access "
          + "scope; v0.3.8.58 DELETED the lane — every operator message is a mission, so there is no "
          + "direct-agent output to exclude from learning and no unconfined access anywhere."),
    };

    private static string TestPath(string file) =>
        Path.Combine(SourceText.RepoRoot(), "tests", "Anthill.Tests", file);

    // -------------------------------------------------------------------------------------------
    // The ledger is complete and well-formed
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void AllTwentyScenarios_AreAccountedFor()
    {
        Assert.Equal(20, Matrix.Length);
        Assert.Equal(Enumerable.Range(1, 20), Matrix.Select(s => s.Number).OrderBy(n => n));
    }

    /// <summary>
    /// Every cited proof file EXISTS. This is the whole reason the index moved out of a doc comment:
    /// a prose citation survives the deletion of the thing it cites, and reads identically afterwards.
    /// </summary>
    [Fact]
    public void EveryCitedProof_IsARealTestFile()
    {
        var missing = Matrix
            .SelectMany(s => s.Proofs.Select(p => (s.Number, File: p)))
            .Where(x => !File.Exists(TestPath(x.File)))
            .Select(x => $"scenario {x.Number} cites {x.File}")
            .ToList();

        Assert.True(missing.Count == 0,
            "these qualification scenarios cite test files that do not exist: "
          + string.Join("; ", missing)
          + ". Either the file moved — update the ledger — or the proof is gone and the scenario is "
          + "open again.");
    }

    /// <summary>
    /// Every OPEN scenario really is open, and every proved one really has a proof. Both directions,
    /// because the failure modes are different and both are bad: a closed scenario recorded as open
    /// gets re-done, and an open one recorded as closed never gets done at all.
    /// </summary>
    [Fact]
    public void OpenAndProvedScenarios_AreLabelledCorrectly()
    {
        foreach (var scenario in Matrix)
        {
            var open = scenario.Proofs.Length == 0;

            Assert.True(open == scenario.Note.StartsWith("OPEN", StringComparison.Ordinal),
                open
                    ? $"scenario {scenario.Number} ({scenario.Name}) cites no proof but its note does not "
                      + "begin with OPEN — an unproved scenario that does not say so reads as covered."
                    : $"scenario {scenario.Number} ({scenario.Name}) is marked OPEN and cites "
                      + $"{scenario.Proofs.Length} proof(s). If those close it, rewrite the note; if they "
                      + "only partially cover it, say PARTIAL and what is missing.");
        }
    }

    /// <summary>
    /// The open ones are named in the plan, so they are visible where the work is chosen rather than
    /// only where the tests live.
    /// </summary>
    [Fact]
    public void TheOpenScenarios_AreNamedInThePlan()
    {
        var plan = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "docs", "PLAN.md"));
        var open = Matrix.Where(s => s.Proofs.Length == 0).Select(s => s.Number).ToList();

        Assert.NotEmpty(open);
        Assert.Contains("qualification scenario", plan, StringComparison.OrdinalIgnoreCase);

        foreach (var number in open)
            Assert.True(plan.Contains($"scenario {number}", StringComparison.OrdinalIgnoreCase),
                $"qualification scenario {number} is open and docs/PLAN.md does not mention it. An open "
              + "scenario that appears only in a test file is one nobody schedules.");
    }

    /// <summary>
    /// Partial coverage is SAID. A scenario whose proofs establish something narrower than its title
    /// is the most dangerous entry in a matrix like this — it is cited, it passes, and the gap is
    /// invisible unless the note admits it.
    /// </summary>
    [Fact]
    public void PartialCoverage_IsDeclaredRatherThanImplied()
    {
        var partial = Matrix.Where(s => s.Note.Contains("PARTIAL", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(partial);
        foreach (var scenario in partial)
            Assert.True(scenario.Proofs.Length > 0,
                $"scenario {scenario.Number} claims PARTIAL coverage and cites nothing, which is OPEN.");
    }
}
