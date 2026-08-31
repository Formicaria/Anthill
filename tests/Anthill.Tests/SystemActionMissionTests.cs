using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Conversations;
using Anthill.Core.Memory;
using Anthill.Core.Missions;
using Anthill.Core.Modules;
using Anthill.Core.Orchestration;
using Anthill.Core.Outcomes;
using Anthill.Core.Security;
using Anthill.Modules.Homelab;
using Anthill.Modules.Homelab.Actions;
using Anthill.Modules.Tools;
using Anthill.SDK.Artifacts;
using Anthill.SDK.Contracts;
using Anthill.SDK.Events;
using Anthill.SDK.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A REVERSIBLE OPERATION, OR A MISSION THAT DOES NOT PASS. v0.3.8.102, PLAN.md §2b — local
/// system and homelab/infrastructure actions, the first class to carry Modify authority.
///
/// THE CLASS. An operator asks for something to be DONE to infrastructure — "restart the media
/// container on host pve1" — and the colony answers with an OPERATION: proposed through the
/// homelab's own approval-gated pipeline (propose → blast radius → HUMAN approval → TOCTOU-guarded
/// execute → verify → audit), never beside it. What separates an operation from a description of
/// one is the record it leaves: a BEFORE-STATE captured before anything changed, a RECEIPT of what
/// ran, an AFTER-STATE probed after it ran, and a ROLLBACK NOTE that existed before execution —
/// reversibility as a precondition, not a hope.
///
/// THE AUTHORITY LINE (ADR-008). The mission's authority is Modify, and Modify still does not mean
/// autonomy: the model PROPOSES (a colony-database row — the LocalActionRunner precedent), and
/// execution passes the conversation escalation gate, where the permission IS the record — an
/// `EscalationDecision` with an operator's answer, distinct from the proposal, persisted whatever
/// the outcome. The homelab executor's own gates (approved-state TOCTOU re-read, mandatory
/// rollback note, kill switch, forbidden-action catalog) all still stand underneath; this slice
/// reaches them through the spine rather than re-implementing any of them.
///
/// WHAT THE GATE DOES NOT CLAIM: that the operation was WISE, or that the after-state is the state
/// the operator wanted — semantic judgments, the standing line. What is checkable is that the
/// records exist, agree with the executor's own lifecycle, and that nothing executed without the
/// distinct human decision.
/// </summary>
[Collection("specialist-gates")]
public class SystemActionMissionTests : IDisposable
{
    private readonly string _dir;
    private readonly bool _specialistWas = AnthillRuntime.EnableSpecialistAntExecution;
    private readonly ActivationTier _tierWas = AnthillRuntime.ActivationTier;
    private readonly bool _useOllamaWas = AnthillRuntime.UseOllama;
    private readonly bool _objectiveWas = AnthillRuntime.EnableObjectiveVerification;
    private readonly string _workspaceWas = AnthillRuntime.AllowedWorkspaceRoot;
    private readonly RosterGates.Snapshot _gatesWere = RosterGates.Capture();

    public SystemActionMissionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-sysact-" + Guid.NewGuid().ToString("N")[..10]);
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

    /// <summary>The same ask, phrased two ways — classification is by meaning (`.98`'s rule).</summary>
    public static TheoryData<string> EquivalentActionRequests => new()
    {
        "Restart the media-server container on host pve1.",
        "Please restart the media-server docker container running on the pve1 host.",
    };

    // -------------------------------------------------------------------------------------------
    // Intake: the class exists, and it is the first to carry Modify authority
    // -------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(EquivalentActionRequests))]
    public void AnActionRequest_ClassifiesAsSystemAction_UnderModifyAuthority(string request)
    {
        var specification = MissionIntake.Resolve(request);

        Assert.Equal(MissionSpecification.SystemActionClass, specification.MissionClass);
        Assert.Equal(MissionIntent.Change, specification.Intent);
        Assert.Equal(MissionAuthority.Modify, specification.Authority);
        Assert.True(specification.IsActionable);
    }

    /// <summary>
    /// THE CODING LANE IS UNTOUCHED. A change request about the REPOSITORY — the lane every prior
    /// release protects — must not enter the system-action class: "fix the build in this repo"
    /// keeps resolving exactly as it did before this class existed.
    /// </summary>
    [Fact]
    public void ARepositoryChangeRequest_IsNotASystemAction() =>
        Assert.NotEqual(MissionSpecification.SystemActionClass,
            MissionIntake.Resolve("Fix the failing build in this repository and update the docs.").MissionClass);

    /// <summary>And a diagnose ask about a service stays troubleshooting — the `.101` boundary.</summary>
    [Fact]
    public void AServiceSymptom_StaysTroubleshooting() =>
        Assert.Equal(MissionSpecification.TroubleshootingClass,
            MissionIntake.Resolve("Why is the media-server container on pve1 failing right now?").MissionClass);

    // -------------------------------------------------------------------------------------------
    // THE GATE: a reversible operation with before-state, receipt and after-state
    // -------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(EquivalentActionRequests))]
    public void AnApprovedOperation_LeavesBeforeState_Receipt_AndAfterState(string request)
    {
        var lab = FakeHomelab.Create(_dir);
        var run = RunColony(request, ActionScript(), lab,
            approveExecution: true);
        using var memory = run.Memory;

        // ---- 1. THE OPERATION RECORD EXISTS, AND ITS PIECES ARE ALL PRESENT ---------------------
        var operation = OperationRecord(memory, run.MissionId);
        Assert.NotNull(operation);
        Assert.False(string.IsNullOrWhiteSpace(operation!.ProposalId), "the record names no proposal.");
        Assert.False(string.IsNullOrWhiteSpace(operation.BeforeState),
            "no before-state was captured — what changed cannot be answered without what was.");
        Assert.False(string.IsNullOrWhiteSpace(operation.Receipt),
            "no execution receipt exists — the operation is a description of itself.");
        Assert.False(string.IsNullOrWhiteSpace(operation.AfterState),
            "no after-state was probed — 'command issued' is not 'desired state achieved'.");

        // ---- 2. REVERSIBILITY WAS A PRECONDITION ------------------------------------------------
        Assert.False(string.IsNullOrWhiteSpace(operation.RollbackNote),
            "the operation carries no rollback note — the executor's own mandate, visible in the record.");

        // ---- 3. THE HUMAN DECISION IS DISTINCT AND RECORDED -------------------------------------
        //
        // The approval is the ESCALATION lane's record — an operator's answer, not the proposing
        // model's word, and not the same identity that proposed. The permission IS the record.
        Assert.False(string.IsNullOrWhiteSpace(operation.ApprovedBy),
            "the record does not say who approved the execution.");
        Assert.Contains("operator", operation.ApprovedBy, StringComparison.OrdinalIgnoreCase);

        // ---- 4. THE EXECUTOR'S OWN LIFECYCLE AGREES ---------------------------------------------
        //
        // The record is not the model's account of the pipeline — it must match the pipeline's own
        // rows: the proposal exists in the homelab repository and reached the executed state
        // through the lifecycle every prior release hardened.
        var proposal = lab.Repository.GetActionProposal(operation.ProposalId);
        Assert.NotNull(proposal);
        Assert.Equal("executed", proposal!.State);
        Assert.Contains(lab.Runner.Executed, e => e.Contains(proposal.TargetId, StringComparison.OrdinalIgnoreCase));

        // ---- 5. JUDGED AGAINST THE OBJECTIVE ----------------------------------------------------
        var evaluation = memory.LoadMissionEvaluation(run.MissionId);
        Assert.NotNull(evaluation);
        Assert.True(evaluation!.IsPositive,
            $"an approved, executed, verified operation did not reach a positive canonical "
          + $"evaluation: {evaluation.OutcomeCode} — {evaluation.Explanation}\n{Dump(memory, run.MissionId)}");
    }

    // -------------------------------------------------------------------------------------------
    // The negatives that give the positive its meaning
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// NO APPROVAL, NO EXECUTION — and the mission says so rather than passing on the proposal
    /// alone. The escalation gate's own rule is "absence is not consent"; the class gate's rule is
    /// that a system-action mission whose operation never executed has not delivered the operation,
    /// and the explanation names what is missing.
    /// </summary>
    [Fact]
    public void AnUnapprovedOperation_DoesNotExecute_AndTheMissionSaysSo()
    {
        var lab = FakeHomelab.Create(_dir);
        var run = RunColony("Restart the media-server container on host pve1.",
            ActionScript(), lab, approveExecution: false);
        using var memory = run.Memory;

        // Nothing ran. The executor was never reached with an approved proposal.
        Assert.Empty(lab.Runner.Executed);

        var evaluation = memory.LoadMissionEvaluation(run.MissionId);
        Assert.NotNull(evaluation);
        Assert.False(evaluation!.IsPositive,
            "a mission whose operation was never approved graded positive — the proposal was "
          + "accepted as the operation.");
        Assert.Contains("operation integrity", evaluation.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MissionEvaluation.Deliverable.NotSatisfied, evaluation.DeliverableStatus);
    }

    // -------------------------------------------------------------------------------------------
    // The gate's own edges, checked directly
    // -------------------------------------------------------------------------------------------

    private static MissionSpecification ActionSpec() =>
        MissionIntake.Resolve("Restart the media-server container on host pve1.");

    private static Artifact OperationRow(string payload) => new()
    {
        Id = "op1", MissionId = "m1", Schema = ArtifactSchemas.SystemOperation,
        ProducerRole = "system_operator", ContentHash = "sha256:x",
        Visibility = ArtifactVisibility.Colony, Payload = payload,
    };

    [Fact]
    public void AnUnreadableArtifactStore_FailsClosed()
    {
        var result = OperationIntegrity.Evaluate(ActionSpec(), artifacts: null);
        Assert.False(result.Satisfied);
        Assert.Contains("store", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMissionWithNoOperationRecord_Fails()
    {
        var result = OperationIntegrity.Evaluate(ActionSpec(), Array.Empty<Artifact>());
        Assert.False(result.Satisfied);
        Assert.Contains("no operation record", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Each missing piece is named — the exit line's three nouns are each load-bearing.</summary>
    [Fact]
    public void ARecordMissingItsPieces_FailsByName()
    {
        var missingAfter = OperationIntegrity.Evaluate(ActionSpec(), new[]
        {
            OperationRow(new SystemOperation(
                ProposalId: "p1", ActionType: "restart_container", TargetKind: "container",
                TargetId: "pve1/media-server", RollbackNote: "start it again",
                BeforeState: "[mock] would restart", Receipt: "[mock] restarted",
                AfterState: "", ApprovedBy: "operator:abc").ToJson()),
        });
        Assert.False(missingAfter.Satisfied);
        Assert.Contains("after-state", missingAfter.Explanation, StringComparison.OrdinalIgnoreCase);

        var missingRollback = OperationIntegrity.Evaluate(ActionSpec(), new[]
        {
            OperationRow(new SystemOperation(
                ProposalId: "p1", ActionType: "restart_container", TargetKind: "container",
                TargetId: "pve1/media-server", RollbackNote: "",
                BeforeState: "[mock] would restart", Receipt: "[mock] restarted",
                AfterState: "[mock] verified", ApprovedBy: "operator:abc").ToJson()),
        });
        Assert.False(missingRollback.Satisfied);
        Assert.Contains("rollback", missingRollback.Explanation, StringComparison.OrdinalIgnoreCase);

        var missingApproval = OperationIntegrity.Evaluate(ActionSpec(), new[]
        {
            OperationRow(new SystemOperation(
                ProposalId: "p1", ActionType: "restart_container", TargetKind: "container",
                TargetId: "pve1/media-server", RollbackNote: "start it again",
                BeforeState: "[mock] would restart", Receipt: "[mock] restarted",
                AfterState: "[mock] verified", ApprovedBy: "").ToJson()),
        });
        Assert.False(missingApproval.Satisfied);
        Assert.Contains("approv", missingApproval.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACompleteOperationRecord_Satisfies()
    {
        var result = OperationIntegrity.Evaluate(ActionSpec(), new[]
        {
            OperationRow(new SystemOperation(
                ProposalId: "p1", ActionType: "restart_container", TargetKind: "container",
                TargetId: "pve1/media-server", RollbackNote: "start it again",
                BeforeState: "[mock] would restart media-server", Receipt: "[mock] restart performed",
                AfterState: "[mock] restart verified", ApprovedBy: "operator:abc").ToJson()),
        });
        Assert.True(result.Satisfied, result.Explanation);
    }

    // ---- harness ---------------------------------------------------------------------------------

    /// <summary>
    /// The homelab pipeline, composed for real: the actual repository, the actual executor with
    /// every gate it ships (TOCTOU, rollback-note mandate, kill switch, catalog), and the
    /// deterministic MockActionRunner the module itself provides. Nothing here is a re-model of
    /// the pipeline — the composed mission reaches the same executor production reaches.
    /// </summary>
    private sealed record FakeHomelab(HomelabRepository Repository, ActionExecutor Executor, MockActionRunner Runner)
    {
        public static FakeHomelab Create(string dir)
        {
            var repository = new HomelabRepository(Path.Combine(dir, $"homelab-{Guid.NewGuid():N}.db"));
            var runner = new MockActionRunner();
            var executor = new ActionExecutor(repository, new IHomelabActionRunner[] { runner }, isStopped: () => false);
            return new FakeHomelab(repository, executor, runner);
        }
    }

    private static ScriptBook ActionScript() => new ScriptBook()
        .Role("planner", ActionPlan)
        .Role("builder",
            "The restart of media-server on pve1 was proposed, approved by the operator, executed "
          + "and verified; the operation record carries the before-state, receipt and after-state.")
        .Role("researcher", "SCRIPTED: located the target container in the inventory.")
        .Role("web", "SCRIPTED: external search performed.")
        .Role("file", "SCRIPTED: workspace files listed.")
        .Role("verifier", "Verification Passed: the operation record is complete and matches the pipeline.")
        .Role("tester", "SCRIPTED: no checks required.")
        .Role("soldier", "SCRIPTED: no security concern.")
        .Role("medic", "SCRIPTED: no diagnosis required.")
        .Role("scribe", "SCRIPTED: summary recorded.")
        .Role("archivist", "SCRIPTED: nothing to archive.");

    private const string ActionPlan = """
        {
          "tasks": [
            {
              "title": "Locate the target",
              "description": "Identify the media-server container and its host in the inventory.",
              "assigned_ant": "researcher",
              "task_type": "research",
              "depends_on": []
            },
            {
              "title": "Perform the operation",
              "description": "Propose the restart with a rollback note, obtain approval, execute and verify.",
              "assigned_ant": "system_operator",
              "task_type": "system_operation",
              "depends_on": []
            },
            {
              "title": "Compile the report",
              "description": "Assemble the operation record into the answer.",
              "assigned_ant": "builder",
              "task_type": "build_answer",
              "depends_on": []
            },
            {
              "title": "Verify the operation record",
              "description": "Check before-state, receipt, after-state and approval are recorded.",
              "assigned_ant": "verifier",
              "task_type": "verification",
              "depends_on": []
            }
          ]
        }
        """;

    private sealed record ColonyRun(SqliteMemory Memory, string MissionId);

    private ColonyRun RunColony(string request, ScriptBook book, FakeHomelab lab, bool approveExecution)
    {
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.ActivationTier = ActivationTier.Full;
        AnthillRuntime.UseOllama = true;
        AnthillRuntime.EnableObjectiveVerification = true;
        AnthillRuntime.AllowedWorkspaceRoot = SourceText.RepoRoot();

        using var scripted = ScriptedColony.Begin(book,
            "planner", "researcher", "web", "file", "builder", "verifier", "tester", "soldier",
            "medic", "scribe", "archivist", "fallback");

        var memory = new SqliteMemory(Path.Combine(_dir, $"sysact-{Guid.NewGuid():N}.db"));
        var conversation = new Conversation
        {
            Id = "sysact-conversation", Role = "queen",
            Policy = EscalationPolicy.Ask, PolicySetBy = "operator", PolicySetAt = DateTime.UtcNow,
        };
        memory.SaveConversation(conversation);

        var host = new ModuleHost(memory, NullEventBus.Instance);
        host.Load(new ToolsModule(new WorkspacePathGuard()));
        var queen = new Queen(memory);
        queen.AdoptModuleTools(host.ContributedTools);
        // The homelab's spine tools, over the REAL executor — same last-write-wins path. The
        // decision bridge is the composition's job (the module references only the SDK): the
        // escalation lane's own record, shaped down to what the operation record needs.
        queen.AdoptModuleTools(SystemActionTools.For(lab.Executor,
            () => ConversationScope.Evaluate(SystemActionTools.ExecuteToolName) is { } decision
                ? (decision.Allowed, decision.Id, decision.Reason ?? "")
                : null));

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

        var answers = new Dictionary<string, string> { [ConversationRunner.StartMissionAction] = "approve" };
        // THE HUMAN STEP, as the escalation lane records it. Present-and-approve is the positive;
        // ABSENT is the negative — the gate's own rule is that absence is not consent, so the
        // unapproved run simply never answers, exactly as an operator who never clicked would.
        if (approveExecution) answers[SystemActionTools.ExecuteToolName] = "approve";

        runner.Run(conversation, request, ConversationMode.Mission, answers: answers);

        Assert.True(settled.Wait(TimeSpan.FromMinutes(2)),
            "the system-action mission did not settle within two minutes.");
        Assert.NotNull(missionId);
        return new ColonyRun(memory, missionId!);
    }

    private static SystemOperation? OperationRecord(SqliteMemory memory, string missionId) =>
        ((IArtifactStore)memory).ForMission(missionId)
            .Where(a => string.Equals(a.Schema, ArtifactSchemas.SystemOperation, StringComparison.OrdinalIgnoreCase))
            .Select(a => SystemOperation.FromJson(a.Payload))
            .FirstOrDefault(r => r is not null);

    private static string Dump(SqliteMemory memory, string missionId)
    {
        try
        {
            var taskLines = string.Join("\n", memory.GetTasksForMission(missionId)
                .Select(t => "    task "
                    + $"{Anthill.SDK.Common.TextUtil.Truncate(t.GetValueOrDefault("id")?.ToString() ?? "-", 8)} "
                    + $"ant={t.GetValueOrDefault("assigned_ant")} type={t.GetValueOrDefault("task_type")} "
                    + $"status={t.GetValueOrDefault("status")}"));
            var artifactLines = string.Join("\n", ((IArtifactStore)memory).ForMission(missionId)
                .Select(a => $"    artifact schema={a.Schema} payload={Anthill.SDK.Common.TextUtil.Truncate(a.Payload, 120)}"));
            return $"\n  TASKS:\n{taskLines}\n  ARTIFACTS:\n{artifactLines}";
        }
        catch (Exception error) { return $"\n  (dump failed: {error.Message})"; }
    }
}
