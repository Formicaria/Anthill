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
using Anthill.SDK.External;
using Anthill.SDK.Tools;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// AN APPROVED SEND TO A RESOLVED TARGET, OR A MISSION THAT SAYS IT DID NOT SEND. v0.3.8.103,
/// PLAN.md §2b — approval-gated external actions and universal authority adapters.
///
/// THE CLASS. An operator asks for something to leave the colony — "post the release summary to the
/// team's incident webhook" — and the colony answers with a SEND that happened, or with the reason
/// it did not. What separates this from `.102`'s infrastructure lane is WHO bears the consequence:
/// a restarted container is the operator's own machine and is reversible by the paired action, while
/// a message posted to a third party is irreversible the instant it lands and is read by people the
/// colony cannot reach. So the record this class must leave is not before/after state — there is no
/// "before" to restore — it is WHERE THE THING WENT.
///
/// TARGET RESOLUTION IS THE POINT, and it is the exit line's first half. The operator approves a
/// destination, not a template: "the team's incident webhook" is an alias, and an alias is not
/// something a human can consent to. The adapter resolves it to a concrete destination BEFORE
/// approval is offered, the resolution is recorded, and what the adapter actually hit is recorded
/// beside it — because an approval of one target and a send to another is the whole failure wearing
/// a signed form. This is `.102`'s TOCTOU re-read in the shape this class needs it.
///
/// DENIED AUTHORITY CANNOT BE REPLACED BY PROSE, the exit line's second half and the harder one. A
/// model whose send was refused will still write "I've posted the summary to the team" — not from
/// malice but because that is what the surrounding text is about, and prose has no way to know a
/// tool refused. `.99` named this failure in its own domain: two channels, and the prose one wins.
/// The answer to it is the same answer — the outcome line is RENDERED FROM THE RECORD, so the
/// channel that reports what happened is the channel that knows.
///
/// WHAT THE GATE DOES NOT CLAIM: that the message was WELL WRITTEN, that the recipient was the right
/// one, or that sending was WISE. Those are semantic and stay outside, the standing line. What is
/// checkable is that a resolved target was approved by a human, that what was hit is what was
/// approved, that a receipt exists — and that when none of it happened, the mission says so.
/// </summary>
[Collection("specialist-gates")]
public class ExternalActionMissionTests : IDisposable
{
    private readonly string _dir;
    private readonly bool _specialistWas = AnthillRuntime.EnableSpecialistAntExecution;
    private readonly ActivationTier _tierWas = AnthillRuntime.ActivationTier;
    private readonly bool _useOllamaWas = AnthillRuntime.UseOllama;
    private readonly bool _objectiveWas = AnthillRuntime.EnableObjectiveVerification;
    private readonly string _workspaceWas = AnthillRuntime.AllowedWorkspaceRoot;
    private readonly RosterGates.Snapshot _gatesWere = RosterGates.Capture();

    public ExternalActionMissionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-extact-" + Guid.NewGuid().ToString("N")[..10]);
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

    private const string Alias = "the team's incident webhook";
    private const string Resolved = "https://hooks.anthill.test/incident";

    /// <summary>
    /// The same ask, phrased three ways. Classification is by meaning — `.98`'s rule, and the one
    /// `.98` learned the hard way when four fixtures agreed by accident because none of them
    /// contained the word the resolver actually keyed on. The third phrasing here deliberately says
    /// "send" rather than "post", so a resolver keyed on one verb cannot pass all three.
    /// </summary>
    public static TheoryData<string> EquivalentSendRequests => new()
    {
        "Post the release summary to the team's incident webhook.",
        "Please publish the release summary to the team's incident webhook endpoint.",
        "Send the release summary over to the team's incident webhook.",
    };

    // -------------------------------------------------------------------------------------------
    // Intake: the class exists, and the boundaries with the classes either side of it hold
    // -------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(EquivalentSendRequests))]
    public void ASendRequest_ClassifiesAsExternalAction_UnderModifyAuthority(string request)
    {
        var specification = MissionIntake.Resolve(request);

        Assert.Equal(MissionSpecification.ExternalActionClass, specification.MissionClass);
        Assert.Equal(MissionIntent.Change, specification.Intent);
        Assert.True(specification.Targets.HasFlag(MissionTargets.External));
        Assert.Equal(MissionAuthority.Modify, specification.Authority);
        Assert.True(specification.IsActionable);
    }

    /// <summary>
    /// THE `.102` LANE IS UNTOUCHED. An infrastructure change stays a system action, and this is
    /// the direction that matters: a release adding a class must never take a mission away from a
    /// class that already serves it.
    /// </summary>
    [Fact]
    public void AnInfrastructureChange_IsNotAnExternalAction() =>
        Assert.Equal(MissionSpecification.SystemActionClass,
            MissionIntake.Resolve("Restart the media-server container on host pve1.").MissionClass);

    /// <summary>
    /// THE HARD BOUNDARY, and the one worth pinning because it is genuinely ambiguous: a request
    /// that names BOTH a service and an external destination. "Tell the team's webhook that the
    /// media-server container restarted" mentions a container, and it is not a restart — nothing is
    /// being done to the container at all. The VERB says what is being done; the target says to
    /// whom. A resolver that let the service noun win would silently turn a notification into an
    /// infrastructure action, which is the worst direction for this to be wrong in.
    /// </summary>
    [Fact]
    public void ASendAboutAService_IsASend_NotAnInfrastructureAction() =>
        Assert.Equal(MissionSpecification.ExternalActionClass,
            MissionIntake.Resolve(
                "Notify the team's incident webhook that the media-server container restarted.").MissionClass);

    /// <summary>And the coding lane, which every release since `.97` protects, is untouched.</summary>
    [Fact]
    public void ARepositoryChangeRequest_IsNotAnExternalAction() =>
        Assert.NotEqual(MissionSpecification.ExternalActionClass,
            MissionIntake.Resolve("Fix the failing build in this repository and update the docs.").MissionClass);

    /// <summary>
    /// A SEND WITH NO NAMED DESTINATION IS NOT THIS CLASS. "Send the report to the team" names no
    /// endpoint, so there is nothing to resolve and nothing a human could approve. It resolves as
    /// it did before this class existed — the honest outcome, and the one that keeps the class from
    /// capturing every sentence containing "send".
    /// </summary>
    [Fact]
    public void ASendWithNoNamedDestination_StaysUnclassified() =>
        Assert.NotEqual(MissionSpecification.ExternalActionClass,
            MissionIntake.Resolve("Send the release summary to the team.").MissionClass);

    // -------------------------------------------------------------------------------------------
    // THE GATE, positive: a resolved target, a human decision, and a receipt
    // -------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(EquivalentSendRequests))]
    public void AnApprovedSend_ResolvesItsTarget_AndLeavesAReceipt(string request)
    {
        var adapter = new RecordingAdapter();
        var run = RunColony(request, SendScript(), adapter, approveSend: true);
        using var memory = run.Memory;

        // ---- 1. THE RECORD EXISTS ---------------------------------------------------------------
        var action = ActionRecord(memory, run.MissionId);
        Assert.True(action is not null,
            "no external_action record was stored for the mission" + Dump(memory, run.MissionId));

        // ---- 2. THE TARGET WAS RESOLVED, AND THE RESOLUTION IS IN THE RECORD ---------------------
        //
        // The operator's words are kept beside the resolution rather than replaced by it: an
        // approval that shows only the resolved url cannot be audited against what was asked, and
        // one that shows only the alias cannot be consented to at all.
        Assert.Contains("webhook", action!.RequestedTarget, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Resolved, action.ResolvedTarget);
        Assert.False(string.IsNullOrWhiteSpace(action.Method), "the record does not say what was done to the target.");

        // ---- 3. WHAT WAS HIT IS WHAT WAS APPROVED ------------------------------------------------
        //
        // `.102`'s TOCTOU re-read in this class's shape. An approval of one destination and a send
        // to another is the entire failure wearing a signed form, and it is checkable without any
        // judgment: the adapter reports where it actually went.
        Assert.Equal(action.ResolvedTarget, action.ExecutedTarget);
        Assert.Equal(action.ResolvedTarget, Assert.Single(adapter.Sent).Target);

        // ---- 4. A HUMAN DECIDED, AND THE DECISION IS THE PERMISSION -------------------------------
        Assert.False(string.IsNullOrWhiteSpace(action.ApprovedBy),
            "the record does not say who approved the send.");
        Assert.Contains("operator", action.ApprovedBy, StringComparison.OrdinalIgnoreCase);

        // ---- 5. AND IT ACTUALLY LEFT -------------------------------------------------------------
        Assert.Equal(ExternalAction.Outcomes.Sent, action.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(action.Receipt),
            "no receipt exists — 'the request was made' is not 'the destination accepted it'.");

        // ---- 6. JUDGED AGAINST THE OBJECTIVE -----------------------------------------------------
        var evaluation = memory.LoadMissionEvaluation(run.MissionId);
        Assert.NotNull(evaluation);
        Assert.True(evaluation!.IsPositive,
            $"an approved, resolved, receipted send did not reach a positive canonical evaluation: "
          + $"{evaluation.OutcomeCode} — {evaluation.Explanation}\n{Dump(memory, run.MissionId)}");
    }

    // -------------------------------------------------------------------------------------------
    // The negatives that give the positive its meaning
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// NO APPROVAL, NOTHING LEAVES. The escalation lane's standing rule is that absence of an
    /// answer is not consent, and the adapter's own ledger is the witness — not the mission's
    /// account of itself.
    /// </summary>
    [Fact]
    public void AnUnapprovedSend_SendsNothing_AndTheMissionSaysSo()
    {
        var adapter = new RecordingAdapter();
        var run = RunColony("Post the release summary to the team's incident webhook.",
            SendScript(), adapter, approveSend: false);
        using var memory = run.Memory;

        Assert.Empty(adapter.Sent);

        var evaluation = memory.LoadMissionEvaluation(run.MissionId);
        Assert.NotNull(evaluation);
        Assert.False(evaluation!.IsPositive,
            "a mission whose send was never approved graded positive — the proposal was accepted "
          + "as the send.");
        Assert.Contains("external action", evaluation.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MissionEvaluation.Deliverable.NotSatisfied, evaluation.DeliverableStatus);
    }

    /// <summary>
    /// THE EXIT LINE'S SECOND HALF, AND THE SHARPEST TEST IN THIS FILE: DENIED AUTHORITY CANNOT BE
    /// REPLACED BY PROSE.
    ///
    /// The builder here is scripted to write exactly what a real model writes when its tool call was
    /// refused several steps upstream: a confident, fluent, entirely false report that the message
    /// was posted. Nothing about that prose is detectable by reading it — it is well formed, on
    /// topic, and indistinguishable from the truth.
    ///
    /// So the answer is not composed from it. The outcome line is RENDERED FROM THE RECORD, and the
    /// record knows what the prose cannot: nothing was sent, and why. This is `.99`'s rule — the
    /// channel that reports what happened must be the channel that knows — applied where being
    /// wrong costs more than a citation.
    ///
    /// Asserted as an EQUALITY against the record's own rendering rather than as a word search: an
    /// assertion that merely looked for "not sent" would pass on an answer that said "not sent" in
    /// one paragraph and "posted successfully" in the next, which is the failure it exists to catch.
    /// </summary>
    [Fact]
    public void ARefusedSend_IsNotReplacedByProseThatClaimsItHappened()
    {
        var adapter = new RecordingAdapter();
        var run = RunColony("Post the release summary to the team's incident webhook.",
            LyingScript(), adapter, approveSend: false);
        using var memory = run.Memory;

        Assert.Empty(adapter.Sent);

        var action = ActionRecord(memory, run.MissionId);
        Assert.True(action is not null,
            "a refused send left no record at all, so there is nothing for the answer to be rendered "
          + "from and the prose wins by default" + Dump(memory, run.MissionId));
        Assert.Equal(ExternalAction.Outcomes.NotSent, action!.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(action.RefusedBecause),
            "the record says nothing left but not why — a refusal an operator cannot locate is one "
          + "they cannot answer.");
        Assert.Equal("", action.Receipt);
        Assert.Equal("", action.ApprovedBy);

        // The answer LEADS with what the record says, and the model's claim is not what was
        // published as the mission's outcome.
        var mission = memory.GetMission(run.MissionId);
        Assert.NotNull(mission);
        var answer = mission!.FinalResult ?? mission.UserResult ?? "";
        Assert.StartsWith(action.Render(), answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND A SEND TO A DESTINATION THAT DOES NOT RESOLVE NEVER REACHES APPROVAL.
    ///
    /// An operator cannot consent to an alias, so an unresolvable one is refused before the decision
    /// is offered rather than after — asking a human to approve something the colony cannot name is
    /// how a signature gets attached to whatever the alias happens to mean later.
    /// </summary>
    [Fact]
    public void AnUnresolvableTarget_IsRefusedBeforeApprovalIsOffered()
    {
        var adapter = new RecordingAdapter { Resolves = false };
        var run = RunColony("Post the release summary to the team's incident webhook.",
            SendScript(), adapter, approveSend: true);
        using var memory = run.Memory;

        Assert.Empty(adapter.Sent);

        var action = ActionRecord(memory, run.MissionId);
        Assert.True(action is not null, "an unresolvable send left no record" + Dump(memory, run.MissionId));
        Assert.Equal(ExternalAction.Outcomes.NotSent, action!.Outcome);
        Assert.Equal("", action.ResolvedTarget);
        Assert.Contains("resolve", action.RefusedBecause, StringComparison.OrdinalIgnoreCase);

        var evaluation = memory.LoadMissionEvaluation(run.MissionId);
        Assert.NotNull(evaluation);
        Assert.False(evaluation!.IsPositive);
    }

    // -------------------------------------------------------------------------------------------
    // UNIVERSAL AUTHORITY: the ceiling is finally read, not merely declared
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// `MissionAuthority` has existed since `.98` with a doc comment describing it as "the ceiling
    /// on what the mission may DO, agreed across specification, operator policy, worker contract and
    /// adapter before dispatch" — and nothing has ever read it. Intake set it, the snapshot showed
    /// it, tests asserted it, and no dispatch consulted it: a declaration reaching nobody, which is
    /// this repository's named house defect and the reason `.98`'s capability branch shipped dead.
    ///
    /// It is read here. A tool that changes the world outside the colony requires Modify, and a
    /// mission whose specification tops out lower is refused by name — the ceiling, not a vague
    /// denial, because a refusal that does not say which of the four sources said no cannot be
    /// acted on.
    /// </summary>
    [Theory]
    [InlineData(MissionAuthority.Observe)]
    [InlineData(MissionAuthority.ExecuteChecks)]
    public void ASendUnderAnInsufficientCeiling_IsRefused_AndTheCeilingIsNamed(MissionAuthority ceiling)
    {
        var decision = MissionAuthorityGate.Evaluate(ceiling, ExternalActionToolNames.Execute);

        Assert.False(decision.Allowed);
        Assert.Contains(ceiling.ToString(), decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(MissionAuthority.Modify.ToString(), decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASendUnderModify_PassesTheCeiling() =>
        Assert.True(MissionAuthorityGate.Evaluate(MissionAuthority.Modify, ExternalActionToolNames.Execute).Allowed);

    /// <summary>
    /// THE CEILING IS UNIVERSAL, NOT A SECOND GATE FOR ONE CLASS. Every tool the escalation lane
    /// already treats as side-effecting declares the authority it needs, so a mission cannot reach
    /// ANY of them from under a lower ceiling — and a read-only tool is unaffected by the ceiling
    /// entirely, because a ceiling that refused reading would make every audit a Modify mission.
    /// </summary>
    [Fact]
    public void EverySideEffectingTool_DeclaresTheAuthorityItNeeds()
    {
        var undeclared = EscalationGate.SideEffecting
            .Where(action => MissionAuthorityGate.Required(action) is null)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

        Assert.True(undeclared.Count == 0,
            "these actions pass the escalation gate as side-effecting and declare no authority "
          + "requirement, so a mission may reach them from under any ceiling: "
          + string.Join(", ", undeclared)
          + ". Declare the authority each one needs, or say in the table why it needs none.");
    }

    [Fact]
    public void AReadOnlyTool_IsUnaffectedByTheCeiling()
    {
        Assert.Null(MissionAuthorityGate.Required("list_directory"));
        Assert.True(MissionAuthorityGate.Evaluate(MissionAuthority.Observe, "list_directory").Allowed);
    }

    // -------------------------------------------------------------------------------------------
    // The gate's own edges, checked directly
    // -------------------------------------------------------------------------------------------

    private static MissionSpecification SendSpec() =>
        MissionIntake.Resolve("Post the release summary to the team's incident webhook.");

    private static Artifact ActionRow(string payload) => new()
    {
        Id = "ea1", MissionId = "m1", Schema = ArtifactSchemas.ExternalAction,
        ProducerRole = "tester", ContentHash = "sha256:x",
        Visibility = ArtifactVisibility.Colony, Payload = payload,
    };

    private static ExternalAction Complete() => new(
        ProposalId: "p1", ActionType: "webhook_post", RequestedTarget: Alias,
        ResolvedTarget: Resolved, ExecutedTarget: Resolved, Method: "POST",
        RequestSummary: "release summary, 412 characters", Receipt: "202 Accepted",
        Outcome: ExternalAction.Outcomes.Sent, RefusedBecause: "", ApprovedBy: "operator:abc");

    /// <summary>
    /// FAILS CLOSED ON AN UNREADABLE STORE, and the asymmetry with `.99` is deliberate rather than
    /// an inconsistency. `CitationIntegrity` returns satisfied for a null store because its job is
    /// to catch a claim the record CONTRADICTS, and an unreadable store contradicts nothing. This
    /// class's whole question is whether something was DONE, and absence is the entire answer: a
    /// store that cannot be read cannot show a send, so the mission has not shown one.
    /// </summary>
    [Fact]
    public void AnUnreadableArtifactStore_FailsClosed()
    {
        var result = ExternalActionIntegrity.Evaluate(SendSpec(), artifacts: null);
        Assert.False(result.Satisfied);
        Assert.Contains("store", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMissionWithNoActionRecord_Fails()
    {
        var result = ExternalActionIntegrity.Evaluate(SendSpec(), Array.Empty<Artifact>());
        Assert.False(result.Satisfied);
        Assert.Contains("no external-action record", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACompleteRecord_Satisfies()
    {
        var result = ExternalActionIntegrity.Evaluate(SendSpec(), new[] { ActionRow(Complete().ToJson()) });
        Assert.True(result.Satisfied, result.Explanation);
    }

    /// <summary>Each missing piece is refused BY NAME — a demotion an operator cannot locate is one
    /// they cannot answer, which is the rule `.99` paid for by finding the explanation was never
    /// even persisted.</summary>
    [Theory]
    [InlineData("resolved", "resolve")]
    [InlineData("executed", "approved target")]
    [InlineData("receipt", "receipt")]
    [InlineData("approver", "approv")]
    public void ARecordMissingItsPieces_FailsByName(string omit, string expected)
    {
        var record = omit switch
        {
            "resolved" => Complete() with { ResolvedTarget = "" },
            // Not missing — DIFFERENT. The send went somewhere other than the approved destination,
            // which no absence check would ever catch.
            "executed" => Complete() with { ExecutedTarget = "https://hooks.anthill.test/elsewhere" },
            "receipt" => Complete() with { Receipt = "" },
            _ => Complete() with { ApprovedBy = "" },
        };

        var result = ExternalActionIntegrity.Evaluate(SendSpec(), new[] { ActionRow(record.ToJson()) });

        Assert.False(result.Satisfied);
        Assert.Contains(expected, result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A RECORD THAT SAYS NOTHING WAS SENT IS A COMPLETE RECORD AND A FAILED MISSION. The two are
    /// not the same judgment: the record is honest, and the mission did not deliver what was asked.
    /// Confusing them is how "we told you we didn't do it" becomes a passing grade.
    /// </summary>
    [Fact]
    public void AnHonestNotSentRecord_StillFailsTheMission()
    {
        var result = ExternalActionIntegrity.Evaluate(SendSpec(), new[]
        {
            ActionRow((Complete() with
            {
                ExecutedTarget = "", Receipt = "", ApprovedBy = "",
                Outcome = ExternalAction.Outcomes.NotSent,
                RefusedBecause = "no operator decision was recorded for execute_external_action",
            }).ToJson()),
        });

        Assert.False(result.Satisfied);
        Assert.Contains("no operator decision", result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>The rendering names the destination in both directions — an operator reading the
    /// answer must be able to see WHERE, whether or not it arrived.</summary>
    [Fact]
    public void TheRendering_NamesTheDestination_SentOrNot()
    {
        Assert.Contains(Resolved, Complete().Render(), StringComparison.Ordinal);

        var refused = Complete() with
        {
            ExecutedTarget = "", Receipt = "", ApprovedBy = "",
            Outcome = ExternalAction.Outcomes.NotSent, RefusedBecause = "the operator did not approve it",
        };
        Assert.Contains(Resolved, refused.Render(), StringComparison.Ordinal);
        Assert.Contains("the operator did not approve it", refused.Render(), StringComparison.Ordinal);
    }

    // ---- harness ---------------------------------------------------------------------------------

    /// <summary>
    /// The adapter seam, exercised by a recorder rather than by a network. What it records is the
    /// only witness this file trusts for "did anything leave" — the mission's own account of itself
    /// is exactly what is under test, so it cannot also be the evidence.
    /// </summary>
    private sealed class RecordingAdapter : IExternalActionAdapter
    {
        public bool Resolves { get; init; } = true;
        public List<(string Target, string Method, string Body)> Sent { get; } = new();

        public string Kind => "webhook";

        public ExternalTargetResolution Resolve(string requestedTarget) =>
            Resolves
                ? ExternalTargetResolution.Resolved(Resolved, "POST")
                : ExternalTargetResolution.Unresolvable(
                    $"'{requestedTarget}' does not resolve to a configured, allowlisted destination");

        public ExternalSendReceipt Send(string resolvedTarget, string method, string body)
        {
            Sent.Add((resolvedTarget, method, body));
            return ExternalSendReceipt.Accepted(resolvedTarget, "202 Accepted");
        }
    }

    private static ScriptBook SendScript() => Book(
        "The release summary was proposed for the team's incident webhook, approved by the operator, "
      + "and delivered; the record carries the resolved destination and the receipt.");

    /// <summary>
    /// The builder that lies — not from malice, but because it is writing about a send and has no
    /// way to know the tool refused several steps upstream. This is what a real model produces.
    /// </summary>
    private static ScriptBook LyingScript() => Book(
        "Done — I've posted the release summary to the team's incident webhook and the team has it. "
      + "The message was delivered successfully.");

    private static ScriptBook Book(string builder) => new ScriptBook()
        .Role("planner", SendPlan)
        .Role("builder", builder)
        .Role("researcher", "SCRIPTED: located the destination in the configured endpoints.")
        .Role("web", "SCRIPTED: external search performed.")
        .Role("file", "SCRIPTED: workspace files listed.")
        .Role("verifier", "Verification Passed: the external-action record is complete.")
        .Role("tester", "SCRIPTED: no checks required.")
        .Role("soldier", "SCRIPTED: no security concern.")
        .Role("medic", "SCRIPTED: no diagnosis required.")
        .Role("scribe", "SCRIPTED: summary recorded.")
        .Role("archivist", "SCRIPTED: nothing to archive.");

    private const string SendPlan = """
        {
          "tasks": [
            {
              "title": "Perform the send",
              "description": "Resolve the destination, propose the send, and — under the operator's recorded decision — deliver it and record the receipt.",
              "assigned_ant": "tester",
              "task_type": "external_action",
              "depends_on": []
            },
            {
              "title": "Compile the report",
              "description": "Assemble the external-action record into the answer.",
              "assigned_ant": "builder",
              "task_type": "build_answer",
              "depends_on": []
            },
            {
              "title": "Verify the record",
              "description": "Check the resolved target, receipt and approval are recorded.",
              "assigned_ant": "verifier",
              "task_type": "verification",
              "depends_on": []
            }
          ]
        }
        """;

    private sealed record ColonyRun(SqliteMemory Memory, string MissionId);

    private ColonyRun RunColony(string request, ScriptBook book, RecordingAdapter adapter, bool approveSend)
    {
        AnthillRuntime.EnableSpecialistAntExecution = true;
        AnthillRuntime.ActivationTier = ActivationTier.Full;
        AnthillRuntime.UseOllama = true;
        AnthillRuntime.EnableObjectiveVerification = true;
        AnthillRuntime.AllowedWorkspaceRoot = SourceText.RepoRoot();

        using var scripted = ScriptedColony.Begin(book,
            "planner", "researcher", "web", "file", "builder", "verifier", "tester", "soldier",
            "medic", "scribe", "archivist", "fallback");

        var memory = new SqliteMemory(Path.Combine(_dir, $"extact-{Guid.NewGuid():N}.db"));
        var conversation = new Conversation
        {
            Id = "extact-conversation", Role = "queen",
            Policy = EscalationPolicy.Ask, PolicySetBy = "operator", PolicySetAt = DateTime.UtcNow,
        };
        memory.SaveConversation(conversation);

        var host = new ModuleHost(memory, NullEventBus.Instance);
        host.Load(new ToolsModule(new WorkspacePathGuard()));
        var queen = new Queen(memory);
        queen.AdoptModuleTools(host.ContributedTools);
        // Wired the way production wires it: the ambient scope for conversational flows, then the
        // SAVED escalation record for mission flows — a mission runs outside the ambient scope, so
        // the record is where the permission lives (the v0.3.8.46 rule, `.102`'s finding).
        queen.AdoptModuleTools(ExternalActionTools.For(adapter,
            missionId =>
            {
                var live = ConversationScope.Evaluate(ExternalActionToolNames.Execute);
                var decision = live ?? OperatorDecisions.ForMission(
                    memory, missionId, ExternalActionToolNames.Execute);
                if (decision is null) return null;
                return (decision.Allowed, decision.Id, decision.Reason ?? "");
            }));

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
        // The human step. Present-and-approve is the positive; ABSENT is the negative — the gate's
        // own rule is that absence is not consent, so the unapproved run simply never answers,
        // exactly as an operator who never clicked would.
        if (approveSend) answers[ExternalActionToolNames.Execute] = "approve";

        runner.Run(conversation, request, ConversationMode.Mission, answers: answers);

        Assert.True(settled.Wait(TimeSpan.FromMinutes(2)),
            "the external-action mission did not settle within two minutes.");
        Assert.NotNull(missionId);
        return new ColonyRun(memory, missionId!);
    }

    private static ExternalAction? ActionRecord(SqliteMemory memory, string missionId) =>
        ((IArtifactStore)memory).ForMission(missionId)
            .Where(a => string.Equals(a.Schema, ArtifactSchemas.ExternalAction, StringComparison.OrdinalIgnoreCase))
            .Select(a => ExternalAction.FromJson(a.Payload))
            .FirstOrDefault(r => r is not null);

    private static string Dump(SqliteMemory memory, string missionId)
    {
        try
        {
            var taskLines = string.Join("\n", memory.GetTasksForMission(missionId)
                .Select(t => "    task "
                    + $"{Anthill.SDK.Common.TextUtil.Truncate(t.GetValueOrDefault("id")?.ToString() ?? "-", 8)} "
                    + $"ant={t.GetValueOrDefault("assigned_ant")} type={t.GetValueOrDefault("task_type")} "
                    + $"status={t.GetValueOrDefault("status")} "
                    + $"summary={Anthill.SDK.Common.TextUtil.Truncate(t.GetValueOrDefault("result_summary")?.ToString() ?? "-", 160)}"));
            var artifactLines = string.Join("\n", ((IArtifactStore)memory).ForMission(missionId)
                .Select(a => $"    artifact schema={a.Schema} payload={Anthill.SDK.Common.TextUtil.Truncate(a.Payload, 120)}"));
            var evidenceLines = string.Join("\n", ((Anthill.SDK.Artifacts.IEvidenceStore)memory).ForMission(missionId)
                .Select(e => $"    evidence kind={e.Kind} passed={e.Passed} "
                    + $"detail={Anthill.SDK.Common.TextUtil.Truncate(e.Detail, 160)}"));
            return $"\n  TASKS:\n{taskLines}\n  EVIDENCE:\n{evidenceLines}\n  ARTIFACTS:\n{artifactLines}";
        }
        catch (Exception error) { return $"\n  (dump failed: {error.Message})"; }
    }
}
