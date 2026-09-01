using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Conversations;
using Anthill.Core.Memory;
using Anthill.Core.Missions;
using Anthill.Core.Modules;
using Anthill.Core.Orchestration;
using Anthill.Core.Outcomes;
using Anthill.Core.Security;
using Anthill.Modules.Tools;
using Anthill.SDK.Artifacts;
using Anthill.SDK.Contracts;
using Anthill.SDK.Events;
using Anthill.SDK.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A SYMPTOM REACHES A DIAGNOSIS, OR THE MISSION DOES NOT PASS. v0.3.8.101, PLAN.md §2b —
/// troubleshooting and diagnostic execution.
///
/// THE CLASS. An operator reports a symptom — "why is the test suite failing?" — and the colony
/// answers with a root cause. What separates a diagnosis from an audit finding, and from a guess
/// wearing a diagnosis's clothes, is EXECUTION: checks actually ran, each left a receipt with its
/// exit status, and the diagnosis rests on those receipts by name. `.98` proved assessment against
/// inspection records; `.100` proved creation against the record's own bytes; this class is proved
/// against COMMAND RECEIPTS, because "I ran it and here is how it exited" is the one claim about a
/// symptom that reading cannot counterfeit.
///
/// THE BOUNDARY, BOTH DIRECTIONS (ADR-008, stated once so releases inherit it): "is the colony
/// healthy?" is an audit and answers from read-only records under `observe` authority; "why is it
/// unhealthy?" is troubleshooting and answers from executed checks under `execute checks`
/// authority. Direction one: an assessment that executed checks has left assessment, and the audit
/// gate refuses it. Direction two: a troubleshooting mission that executed nothing — or whose
/// checks all passed and which therefore diagnosed nothing — has not delivered a diagnosis, and
/// the diagnosis gate refuses it rather than letting assessment-shaped records carry it.
///
/// A REPRODUCED SYMPTOM IS SUCCESS. The check that fails is the symptom CONFIRMED — the tester
/// task carrying it fails by design, and grading the mission down for it would make every honest
/// reproduction read as a broken mission. The failed check is input to the deliverable, not a
/// defect of the mission that ran it.
/// </summary>
[Collection("specialist-gates")]
public class TroubleshootingMissionTests : IDisposable
{
    private readonly string _dir;
    private readonly bool _specialistWas = AnthillRuntime.EnableSpecialistAntExecution;
    private readonly ActivationTier _tierWas = AnthillRuntime.ActivationTier;
    private readonly bool _useOllamaWas = AnthillRuntime.UseOllama;
    private readonly bool _objectiveWas = AnthillRuntime.EnableObjectiveVerification;
    private readonly string _workspaceWas = AnthillRuntime.AllowedWorkspaceRoot;
    private readonly RosterGates.Snapshot _gatesWere = RosterGates.Capture();

    public TroubleshootingMissionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-diagnose-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        AnthillRuntime.EnableSpecialistAntExecution = _specialistWas;
        AnthillRuntime.ActivationTier = _tierWas;
        AnthillRuntime.UseOllama = _useOllamaWas;
        AnthillRuntime.EnableObjectiveVerification = _objectiveWas;
        AnthillRuntime.AllowedWorkspaceRoot = _workspaceWas;
        RosterGates.Restore(_gatesWere);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>The same symptom, phrased two ways — classification is by meaning (`.98`'s rule).</summary>
    public static TheoryData<string> EquivalentSymptomRequests => new()
    {
        "Why is the test suite failing in this repository right now?",
        "Troubleshoot the failing checks in this codebase and find the root cause.",
    };

    /// <summary>
    /// The check runner, shadowed deterministically through the same last-write-wins registration
    /// the composition root uses. `dotnet_test` fails with a real-looking exit status; everything
    /// else passes. The REGISTRY still records the dispatch, so the receipt rows this class is
    /// about are produced by the honest witness (`ToolEvidence`'s chokepoint), not by this fake.
    /// </summary>
    private sealed class FailingCheckTool : ITool
    {
        public string Name => "run_allowlisted_check";
        public string Description => "deterministic check fixture — dotnet_test fails";

        public ToolResult Run(IReadOnlyDictionary<string, object?> args)
        {
            var id = args.GetValueOrDefault("check_id")?.ToString() ?? "";
            return string.Equals(id, "dotnet_test", StringComparison.OrdinalIgnoreCase)
                ? new ToolResult(Name, false, "exit_code=1\n1 test failed: ReportingTests.TotalsAreExact",
                    "dotnet_test failed: ReportingTests.TotalsAreExact asserted 41, expected 42", FailureClass.VerificationFailure)
                : new ToolResult(Name, true, "exit_code=0\nall checks passed");
        }
    }

    /// <summary>Every check passes: the symptom does not reproduce, and nothing gets diagnosed.</summary>
    private sealed class PassingCheckTool : ITool
    {
        public string Name => "run_allowlisted_check";
        public string Description => "deterministic check fixture — everything passes";

        public ToolResult Run(IReadOnlyDictionary<string, object?> args) =>
            new(Name, true, "exit_code=0\nall checks passed");
    }

    // -------------------------------------------------------------------------------------------
    // Intake: the class exists, derived from dimensions
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Deterministic and pure, so it is asserted directly: a symptom about a target becomes the
    /// troubleshooting class, under `execute checks` authority — the first class to carry it — and
    /// requires the receipt evidence the store actually spells.
    /// </summary>
    [Theory]
    [MemberData(nameof(EquivalentSymptomRequests))]
    public void ASymptomRequest_ClassifiesAsTroubleshooting_UnderExecuteChecksAuthority(string request)
    {
        var specification = MissionIntake.Resolve(request);

        Assert.Equal(MissionSpecification.TroubleshootingClass, specification.MissionClass);
        Assert.Equal(MissionIntent.Diagnose, specification.Intent);
        Assert.Equal(MissionAuthority.ExecuteChecks, specification.Authority);
        Assert.Contains(EvidenceKinds.CommandCheck, specification.RequiredEvidence);
        Assert.True(specification.IsActionable);
    }

    /// <summary>A symptom about nothing nameable stays `general` — silence over a guess.</summary>
    [Fact]
    public void ASymptomWithNoTarget_StaysGeneral() =>
        Assert.Equal(MissionSpecification.GeneralClass,
            MissionIntake.Resolve("Why though?").MissionClass);

    /// <summary>
    /// Change outranks diagnose, unchanged: "find out why it is broken and fix it" is a change
    /// mission, and this release gives it no class — the `.103`+ lanes own it. Pinned so adding
    /// the troubleshooting class cannot have widened intake's reach into consequential work.
    /// </summary>
    [Fact]
    public void ASymptomThatAsksForARepair_IsNotTroubleshooting() =>
        Assert.NotEqual(MissionSpecification.TroubleshootingClass,
            MissionIntake.Resolve("Find out why the build is broken in this repo and fix it.").MissionClass);

    /// <summary>And the audit class is untouched by the new verbs: observe-only, as before.</summary>
    [Fact]
    public void AnAuditRequest_StillClassifiesAsAudit_UnderObserveAuthority()
    {
        var specification = MissionIntake.Resolve("Assess the current health of the colony and report what is enabled.");
        Assert.Equal(MissionSpecification.SystemAuditClass, specification.MissionClass);
        Assert.Equal(MissionAuthority.Observe, specification.Authority);
    }

    // -------------------------------------------------------------------------------------------
    // THE GATE: a symptom reaches a diagnosis supported by receipts
    // -------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(EquivalentSymptomRequests))]
    public void ASymptom_ReachesADiagnosis_SupportedByCommandReceipts(string request)
    {
        var run = RunColony(request, TroubleshootingScript(), new FailingCheckTool());
        using var memory = run.Memory;

        // ---- 1. CHECKS RAN, AND EACH LEFT A RECEIPT WITH ITS EXIT STATUS ------------------------
        //
        // From the registry's own dispatch record — the chokepoint every tool call passes — never
        // from a task's self-report. A diagnosis built on no execution is an audit finding at best
        // and a guess at worst, and either one wearing this class's name is what the gate refuses.
        var receipts = ((IEvidenceStore)memory).ForMission(run.MissionId)
            .Where(e => string.Equals(e.Kind, EvidenceKinds.CommandCheck, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(receipts.Count > 0,
            "the troubleshooting mission executed no checks — its diagnosis, whatever it says, "
          + $"rests on nothing that ran.\n{Dump(memory, run.MissionId)}");
        Assert.All(receipts, r => Assert.Contains("exit_code=", r.Detail, StringComparison.OrdinalIgnoreCase));

        // THE SYMPTOM REPRODUCED: at least one receipt is a failure, and it names the check.
        var failing = receipts.Where(r => !r.Passed).ToList();
        Assert.True(failing.Count > 0,
            "no executed check failed — the symptom was never reproduced, and the positive gate "
          + $"requires a reproduction to diagnose.\n{Dump(memory, run.MissionId)}");
        Assert.Contains(failing, r => r.Detail.Contains("dotnet_test", StringComparison.OrdinalIgnoreCase));

        // ---- 2. A DIAGNOSIS EXISTS, AND IT CITES ITS RECEIPTS BY NAME ---------------------------
        //
        // The citation is stamped by the deterministic layer from the mission's own check
        // receipts — never written by the model — for `.100`'s reason: an identity a model wrote
        // is an identity it could have invented. What the gate then checks is that every cited
        // receipt resolves to a check this mission actually ran.
        var diagnosis = DiagnosisArtifacts(memory, run.MissionId);
        Assert.True(diagnosis.Count > 0,
            $"no failure_diagnosis record exists for the mission.\n{Dump(memory, run.MissionId)}");

        var cited = diagnosis.SelectMany(CitedReceipts).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(cited.Count > 0,
            "the diagnosis cites no receipt — a root cause resting on nothing that ran.\n"
          + Dump(memory, run.MissionId));
        Assert.All(cited, id => Assert.Contains(receipts, r =>
            r.Detail.Contains(id, StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(cited, id => id.Contains("dotnet_test", StringComparison.OrdinalIgnoreCase));

        // ---- 3. THE DIAGNOSIS REACHES THE OPERATOR ----------------------------------------------
        var mission = memory.GetMission(run.MissionId);
        var answer = mission?.GetValueOrDefault("final_result")?.ToString() ?? "";
        Assert.False(string.IsNullOrWhiteSpace(answer), "the mission produced no operator-facing answer.");
        Assert.Contains("dotnet_test", answer, StringComparison.OrdinalIgnoreCase);

        // ---- 4. A REPRODUCED, DIAGNOSED SYMPTOM GRADES POSITIVE ---------------------------------
        //
        // The failed tester task is the symptom CONFIRMED, not a defect of the mission. Without
        // this assertion, every honest reproduction reads as a broken run and the class teaches
        // its operators to prefer missions that reproduce nothing.
        var evaluation = memory.LoadMissionEvaluation(run.MissionId);
        Assert.NotNull(evaluation);
        Assert.True(evaluation!.IsPositive,
            $"a reproduced and diagnosed symptom did not reach a positive canonical evaluation: "
          + $"{evaluation.OutcomeCode} — {evaluation.Explanation}\n{Dump(memory, run.MissionId)}");
    }

    /// <summary>
    /// The mission's own records, rendered for a failure message — the R3 lesson applied in
    /// advance: a composed failure that does not show the store's state gets debugged by guessing,
    /// and the first two rounds of this class were.
    /// </summary>
    private static string Dump(SqliteMemory memory, string missionId)
    {
        try
        {
            var taskLines = string.Join("\n", memory.GetTasksForMission(missionId)
                .Select(t => "    task "
                    + $"{Anthill.SDK.Common.TextUtil.Truncate(t.GetValueOrDefault("id")?.ToString() ?? "-", 8)} "
                    + $"ant={t.GetValueOrDefault("assigned_ant")} type={t.GetValueOrDefault("task_type")} "
                    + $"status={t.GetValueOrDefault("status")}"));
            var evidenceLines = string.Join("\n", ((IEvidenceStore)memory).ForMission(missionId)
                .Select(e => $"    evidence kind={e.Kind} task={Anthill.SDK.Common.TextUtil.Truncate(e.TaskId ?? "-", 8)} "
                           + $"passed={e.Passed} detail={Anthill.SDK.Common.TextUtil.Truncate(e.Detail, 80)}"));
            var diagnosisLines = string.Join("\n---\n", ((IArtifactStore)memory).ForMission(missionId)
                .Where(a => string.Equals(a.Schema, ArtifactSchemas.FailureDiagnosis, StringComparison.OrdinalIgnoreCase))
                .Select(a => Anthill.SDK.Common.TextUtil.Truncate(a.Payload, 500)));
            return $"\n  TASKS:\n{taskLines}\n  EVIDENCE:\n{evidenceLines}\n  DIAGNOSES:\n{diagnosisLines}";
        }
        catch (Exception error) { return $"\n  (dump failed: {error.Message})"; }
    }

    // -------------------------------------------------------------------------------------------
    // The boundary, both directions
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// DIRECTION ONE: an assessment that executed checks has left assessment. The audit class's
    /// authority is `observe`; receipts in its store are the record of that boundary crossed, and
    /// the audit gate refuses the mission by name rather than grading what came back.
    /// </summary>
    [Fact]
    public void AnAudit_ThatExecutedChecks_IsRefusedAtTheBoundary()
    {
        var run = RunColony(
            "Assess the current health of the colony and report what is enabled.",
            AuditWithChecksScript(), new FailingCheckTool());
        using var memory = run.Memory;

        var evaluation = memory.LoadMissionEvaluation(run.MissionId);
        Assert.NotNull(evaluation);
        Assert.False(evaluation!.IsPositive,
            $"an audit that executed checks reached a positive outcome: {evaluation.Explanation}");
        // v0.3.8.104 — THE DELIVERABLE LANE IS NOW LEGITIMATELY SATISFIED, and that is not a
        // weakening. The audit inspected, compiled and verified: it delivered what an audit
        // delivers. What it could NOT do is execute a check, because the ceiling refused the
        // dispatch — so the mission is not a verified success (asserted above) while the audit's
        // own objective is met. Asserting `not_satisfied` here would be asserting that the audit
        // failed at being an audit, which it did not; the thing that failed is the thing it should
        // never have attempted.

        // v0.3.8.104 — THE BOUNDARY MOVED EARLIER, AND THAT IS THE POINT.
        //
        // Until this release an audit COULD execute a check and was caught at grading, by an
        // explanation naming the executed check. Now the mission's authority ceiling is read at the
        // dispatch chokepoint: an audit is admitted at `observe`, `run_allowlisted_check` requires
        // `execute_checks`, and the dispatch is refused before anything runs. The boundary `.101`
        // established is unchanged; it is enforced where it costs nothing instead of where it
        // costs a check run.
        //
        // So the assertion moves with it rather than softening. The old one looked for the reason
        // in the grade; this looks for the refusal in the record, which is a stronger claim: not
        // "the audit was marked down for executing checks" but "the audit could not execute one".
        var refused = memory.GetRecentEvents(200, "authority_ceiling_refused", run.MissionId);
        Assert.NotEmpty(refused);

        // AND THE RECORD AGREES, which is the assertion that would survive the event vocabulary
        // being renamed: an audit's store holds NO command-check receipts, because no check ran.
        // `.101` made a receipt the thing a diagnosis rests on; the absence of one here is the
        // boundary visible in the same place the gates read.
        var receipts = ((Anthill.SDK.Artifacts.IEvidenceStore)memory).ForMission(run.MissionId)
            .Where(e => string.Equals(e.Kind, Anthill.SDK.Artifacts.EvidenceKinds.CommandCheck,
                        StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(receipts.Count == 0,
            $"an audit left {receipts.Count} command-check receipt(s); its authority is `observe` "
          + "and the ceiling should have refused every check dispatch before it ran.");
    }

    /// <summary>
    /// DIRECTION TWO: a troubleshooting mission whose checks all passed has reproduced nothing and
    /// diagnosed nothing, and it does not pass on the strength of the records an AUDIT would
    /// accept. The honest outcome is a refusal that says exactly that — recorded in §2c as this
    /// release's sharpest limit: "could not reproduce" as a first-class positive answer is future
    /// work, and until it exists the mission must not silently grade as if it had diagnosed.
    /// </summary>
    [Fact]
    public void ASymptomThatDoesNotReproduce_DoesNotGradeAsADiagnosis()
    {
        var run = RunColony(
            "Why is the test suite failing in this repository right now?",
            TroubleshootingScript(), new PassingCheckTool());
        using var memory = run.Memory;

        Assert.Empty(DiagnosisArtifacts(memory, run.MissionId));

        var evaluation = memory.LoadMissionEvaluation(run.MissionId);
        Assert.NotNull(evaluation);
        Assert.False(evaluation!.IsPositive,
            "a troubleshooting mission that diagnosed nothing graded positive — assessment-shaped "
          + "records carried a diagnosis mission.");
        Assert.Contains("diagnosis", evaluation.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MissionEvaluation.Deliverable.NotSatisfied, evaluation.DeliverableStatus);
    }

    // -------------------------------------------------------------------------------------------
    // The diagnosis gate's own edges, checked directly
    // -------------------------------------------------------------------------------------------

    private static MissionSpecification TroubleshootingSpec() =>
        MissionIntake.Resolve("Why is the test suite failing in this repository right now?");

    private static Evidence Receipt(string detail, bool passed = false) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Kind = EvidenceKinds.CommandCheck,
        Deterministic = true, Passed = passed, MissionId = "m1",
        Detail = detail,
    };

    private static Artifact DiagnosisRow(string payload) => new()
    {
        Id = "d1", MissionId = "m1", Schema = ArtifactSchemas.FailureDiagnosis,
        ProducerRole = "medic", ContentHash = "sha256:x",
        Visibility = ArtifactVisibility.Colony, Payload = payload,
    };

    /// <summary>An unreadable evidence store fails CLOSED — the S3 rule, same as the audit gate:
    /// an outage is never permission, and a receipt that cannot be shown is one that is not held.</summary>
    [Fact]
    public void AnUnreadableEvidenceStore_FailsClosed()
    {
        var result = DiagnosisIntegrity.Evaluate(TroubleshootingSpec(), evidence: null,
            new[] { DiagnosisRow("supporting_receipt: dotnet_test exit_code=1") });
        Assert.False(result.Satisfied);
        Assert.Contains("store", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReceiptsWithNoDiagnosis_Fail()
    {
        var result = DiagnosisIntegrity.Evaluate(TroubleshootingSpec(),
            new[] { Receipt("check dotnet_test exit_code=1 success=False") },
            artifacts: Array.Empty<Artifact>());
        Assert.False(result.Satisfied);
        Assert.Contains("no diagnosis", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A cited receipt that resolves to nothing this mission ran is refused BY NAME —
    /// the class's own fabrication, exactly parallel to `.99`'s invented url.</summary>
    [Fact]
    public void ADiagnosisCitingAReceiptThatNeverRan_Fails_ByName()
    {
        var result = DiagnosisIntegrity.Evaluate(TroubleshootingSpec(),
            new[] { Receipt("check dotnet_test exit_code=1 success=False") },
            new[] { DiagnosisRow("probable_cause: redis down\nsupporting_receipt: redis_ping exit_code=1") });
        Assert.False(result.Satisfied);
        Assert.Contains("redis_ping", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void ADiagnosisRestingOnARecordedReceipt_Satisfies()
    {
        var result = DiagnosisIntegrity.Evaluate(TroubleshootingSpec(),
            new[] { Receipt("check dotnet_test exit_code=1 success=False") },
            new[] { DiagnosisRow("probable_cause: failing assertion\nsupporting_receipt: dotnet_test exit_code=1") });
        Assert.True(result.Satisfied, result.Explanation);
    }

    /// <summary>Direction one at the unit level: the audit gate refuses receipts under `observe`.</summary>
    [Fact]
    public void TheAuditGate_RefusesExecutedChecks_DirectLy()
    {
        var specification = MissionIntake.Resolve("Assess the current health of the colony and report what is enabled.");
        var result = AssessmentObjective.Evaluate(specification,
            new[]
            {
                Receipt("check dotnet_test exit_code=1 success=False"),
                new Evidence
                {
                    Id = "i1", Kind = EvidenceKinds.Inspection, Deterministic = false,
                    Passed = true, MissionId = "m1", Detail = "read repository",
                },
            },
            consumptions: Array.Empty<ArtifactConsumption>(),
            answer: "the colony is healthy");
        Assert.False(result.Satisfied);
        Assert.Contains("executed check", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // ---- harness ---------------------------------------------------------------------------------

    private static ScriptBook TroubleshootingScript() => WithCommonRoles(new ScriptBook()
        .Role("planner", TroubleshootingPlan)
        .Role("builder",
            "The failing check is dotnet_test: ReportingTests.TotalsAreExact asserts 41 where 42 is "
          + "expected. The diagnosis and its receipts are recorded on the mission."));

    private static ScriptBook AuditWithChecksScript() => WithCommonRoles(new ScriptBook()
        .Role("planner", AuditWithChecksPlan)
        .Role("builder", "SCRIPTED: assessment compiled from the inspection findings."));

    private static ScriptBook WithCommonRoles(ScriptBook book) => book
        .Role("researcher", "SCRIPTED: framed the reported symptom and its context.")
        .Role("web", "SCRIPTED: external search performed.")
        .Role("file", "SCRIPTED: workspace files listed.")
        .Role("verifier", "Verification Passed: the conclusion rests on recorded receipts.")
        .Role("tester", "SCRIPTED: checks executed.")
        .Role("soldier", "SCRIPTED: no security concern.")
        .Role("medic", "SCRIPTED: diagnosis recorded.")
        .Role("scribe", "SCRIPTED: summary recorded.")
        .Role("archivist", "SCRIPTED: nothing to archive.");

    private const string TroubleshootingPlan = """
        {
          "tasks": [
            {
              "title": "Frame the reported symptom",
              "description": "Identify what is reportedly failing and what evidence would confirm it.",
              "assigned_ant": "researcher",
              "task_type": "research",
              "depends_on": []
            },
            {
              "title": "Reproduce the failure",
              "description": "Run the dotnet_build and dotnet_test checks to reproduce the reported failure and record receipts.",
              "assigned_ant": "tester",
              "task_type": "validation_check",
              "depends_on": []
            },
            {
              "title": "Compile the findings",
              "description": "Assemble the diagnosis and its receipts into the answer.",
              "assigned_ant": "builder",
              "task_type": "build_answer",
              "depends_on": []
            },
            {
              "title": "Verify the conclusion",
              "description": "Check that the root cause is supported by the recorded receipts.",
              "assigned_ant": "verifier",
              "task_type": "verification",
              "depends_on": []
            }
          ]
        }
        """;

    /// <summary>An audit plan that (wrongly) includes check execution — the boundary's direction one.</summary>
    private const string AuditWithChecksPlan = """
        {
          "tasks": [
            {
              "title": "Inspect the colony",
              "description": "Read the repository and runtime records relevant to the assessment.",
              "assigned_ant": "researcher",
              "task_type": "research",
              "depends_on": []
            },
            {
              "title": "Run the checks",
              "description": "Run the dotnet_build and dotnet_test checks.",
              "assigned_ant": "tester",
              "task_type": "validation_check",
              "depends_on": []
            },
            {
              "title": "Compile the assessment",
              "description": "Assemble the findings into the assessment.",
              "assigned_ant": "builder",
              "task_type": "build_answer",
              "depends_on": []
            },
            {
              "title": "Verify the assessment",
              "description": "Check the assessment against what was inspected.",
              "assigned_ant": "verifier",
              "task_type": "verification",
              "depends_on": []
            }
          ]
        }
        """;

    private sealed record ColonyRun(SqliteMemory Memory, string MissionId);

    private ColonyRun RunColony(string request, ScriptBook book, ITool checkTool)
    {
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.ActivationTier = ActivationTier.Full;
        AnthillRuntime.UseOllama = true;
        AnthillRuntime.EnableObjectiveVerification = true;
        AnthillRuntime.AllowedWorkspaceRoot = SourceText.RepoRoot();

        using var scripted = ScriptedColony.Begin(book,
            "planner", "researcher", "web", "file", "builder", "verifier", "tester", "soldier",
            "medic", "scribe", "archivist", "fallback");

        var memory = new SqliteMemory(Path.Combine(_dir, $"diagnose-{Guid.NewGuid():N}.db"));
        var conversation = new Conversation
        {
            Id = "diagnose-conversation", Role = "queen",
            Policy = EscalationPolicy.Ask, PolicySetBy = "operator", PolicySetAt = DateTime.UtcNow,
        };
        memory.SaveConversation(conversation);

        var host = new ModuleHost(memory, NullEventBus.Instance);
        host.Load(new ToolsModule(new WorkspacePathGuard()));
        var queen = new Queen(memory);
        queen.AdoptModuleTools(host.ContributedTools);
        // The deterministic check runner LAST, displacing the real one through the same path.
        queen.AdoptModuleTools(new[] { checkTool });

        string? missionId = null;
        using var settled = new ManualResetEventSlim(false);
        var runner = new ConversationRunner(memory,
            (goal, _, onCreated, cancel) =>
            {
                try
                {
                    queen.RunMission(goal, onMissionCreated: id => { missionId = id; onCreated(id); }, cancel);
                    return missionId ?? "";
                }
                finally { settled.Set(); }
            });

        runner.Run(conversation, request, ConversationMode.Mission,
            answers: new Dictionary<string, string> { [ConversationRunner.StartMissionAction] = "approve" });

        Assert.True(settled.Wait(TimeSpan.FromMinutes(2)),
            "the troubleshooting mission did not settle within two minutes.");
        Assert.NotNull(missionId);
        return new ColonyRun(memory, missionId!);
    }

    private static IReadOnlyList<Artifact> DiagnosisArtifacts(SqliteMemory memory, string missionId) =>
        ((IArtifactStore)memory).ForMission(missionId)
            .Where(a => string.Equals(a.Schema, ArtifactSchemas.FailureDiagnosis, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// The receipts a diagnosis declares, read from the record's own `supporting_receipt:` lines —
    /// stamped by the deterministic layer, so their presence is a production commitment and their
    /// resolution is the gate's job. RED UNTIL THE SLICE LANDS: nothing writes these lines today.
    /// </summary>
    private static IEnumerable<string> CitedReceipts(Artifact diagnosis) =>
        System.Text.RegularExpressions.Regex
            .Matches(diagnosis.Payload, @"^\s*supporting_receipt:\s*(?<id>\S+)",
                System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Groups["id"].Value.Trim());
}
