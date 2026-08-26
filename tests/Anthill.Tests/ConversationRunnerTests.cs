using Anthill.Core.Conversations;
using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.7.0 — the escalation boundary: what turns a conversation into a mission.
///
/// The phase wants escalation "explicit, bounded and approved", and EXPLICIT is the word doing the
/// work. Three designs were available:
///
///   - the model decides when to escalate — rejected, because the agent's judgement about what
///     deserves autonomous multi-task execution would become a security boundary, and a model that
///     wants to be helpful escalates
///   - escalate automatically on complexity — the same objection with a heuristic in front of it
///   - the CALLER asks, and the request is gated — chosen
///
/// So starting a mission goes through the SAME gate as apply_patch. An operator who set a standing
/// policy has already answered this; one who did not gets asked once, where they expect to be asked.
/// </summary>
public class ConversationRunnerTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _memory;
    private int _missionsStarted;
    private CancellationToken _lastToken;
    /// <summary>v0.3.8.95: what the pipeline was told about the conversation's project —
    /// captured from the delegate itself, so a test asserts what the producer actually
    /// delivered across the boundary rather than what the caller intended to deliver.</summary>
    private string? _lastProjectId;

    /// <summary>
    /// Holds the fake mission OPEN until a test lets it finish.
    ///
    /// Necessary rather than decorative: the runner releases a conversation's cancellation lease
    /// when the work completes, so a fake that returns instantly has already finished by the time a
    /// test calls Cancel — and every cancellation test would assert against a mission that is not
    /// running. The scenario these tests exist for is a mission still in flight, so the fake has to
    /// actually be in flight.
    /// </summary>
    private readonly ManualResetEventSlim _release = new(false);

    public ConversationRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-runner-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "memory.db"));
    }

    public void Dispose()
    {
        // Let any still-blocked fake mission end before the fixture goes away.
        _release.Set();
        _release.Dispose();
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// The mission pipeline, faked — the runner decides WHETHER, the Queen decides what.
    ///
    /// The fake REPORTS ITS ID through the callback, because that is the contract the runner
    /// depends on: the real pipeline fires onMissionCreated as soon as the mission row exists and
    /// then keeps working, so the runner can record history without waiting for the work to finish.
    /// A fake that only returned the id would test a pipeline that does not exist.
    /// </summary>
    private ConversationRunner Runner() => new(_memory, (_, projectId, onCreated, token) =>
    {
        var id = $"mission-{Interlocked.Increment(ref _missionsStarted)}";
        _lastToken = token;
        _lastProjectId = projectId;

        // The id is reported IMMEDIATELY — that is the contract the runner depends on — and then the
        // mission keeps running, exactly as the real pipeline does.
        onCreated(id);

        try { _release.Wait(TimeSpan.FromSeconds(10), token); }
        catch (OperationCanceledException) { /* cancelled mid-flight, which is a valid ending */ }

        return id;
    });

    private Conversation Chat(EscalationPolicy policy = EscalationPolicy.Ask, bool cancelled = false)
    {
        var conversation = new Conversation
        {
            Id = "c1", Role = "queen", Policy = policy, Cancelled = cancelled,
            PolicySetBy = policy == EscalationPolicy.Ask ? null : "zwright",
            PolicySetAt = policy == EscalationPolicy.Ask ? null : DateTime.UtcNow,
        };
        _memory.SaveConversation(conversation);
        return conversation;
    }

    private static Dictionary<string, string> Approve() =>
        new(StringComparer.OrdinalIgnoreCase) { [ConversationRunner.StartMissionAction] = "approve" };

    // ---- the project crosses into the pipeline ----------------------------------------------

    /// <summary>
    /// v0.3.8.95 — the conversation's PROJECT reaches the mission pipeline as data. Asserted from
    /// the pipeline delegate's own captured argument (the consumer's view of the producer's
    /// value), because this crossing existed nowhere before: the handoff dropped the project on
    /// the floor and every mission's workspace was cut from the one configured repository.
    /// </summary>
    [Fact]
    public void TheConversationsProject_TravelsIntoTheMissionPipeline()
    {
        var conversation = new Conversation
        {
            Id = "c-proj", Role = "queen", Policy = EscalationPolicy.Bypass,
            // Attributed, as Chat() attributes it — an unattributed standing policy fails closed
            // to Ask, which would refuse this run and test the gate rather than the crossing.
            PolicySetBy = "zwright", PolicySetAt = DateTime.UtcNow,
            ProjectId = "proj-9",
        };
        _memory.SaveConversation(conversation);
        var runner = Runner();

        Assert.True(runner.Run(conversation, "work inside the project", ConversationMode.Mission).Started);
        _release.Set();
        Assert.Equal("proj-9", _lastProjectId);
    }

    /// <summary>And a conversation without one delivers null — never "" and never a guess.</summary>
    [Fact]
    public void AProjectlessConversation_DeliversNullToThePipeline()
    {
        var runner = Runner();
        _lastProjectId = "sentinel-not-cleared";

        Assert.True(runner.Run(Chat(EscalationPolicy.Bypass), "work with no project",
            ConversationMode.Mission).Started);
        _release.Set();
        Assert.Null(_lastProjectId);
    }

    // ---- every message is a mission ---------------------------------------------------------

    /// <summary>
    /// v0.3.8.58 — a bare message, with no mode asked for, IS a mission.
    ///
    /// This replaces `Chat_RunsWithoutAnEscalationDecision_AndIsAnswered`, and the inversion is the
    /// whole change: that test asserted a turn which started no mission, recorded no escalation
    /// decision, and was answered by whatever provider served the `conversation` route. Every one of
    /// those properties is now a defect. The default mode is still Chat because the API still sends
    /// it — and it must reach the mission path anyway, which is exactly what a caller who never
    /// updates their request would otherwise miss.
    /// </summary>
    [Fact]
    public void AMessageWithNoModeRequested_StillBecomesAMission()
    {
        var outcome = Runner().Run(Chat(EscalationPolicy.Bypass), "what does this repository do?");

        Assert.Equal(ConversationMode.Mission, outcome.Mode);
        Assert.True(outcome.Started);
        Assert.Equal("mission-1", outcome.MissionId);
        Assert.Equal(1, _missionsStarted);
    }

    /// <summary>
    /// And it is GATED like one. The old chat lane ran with no escalation decision at all, which
    /// was defensible only while it did no work; a lane that could edit the operator's files
    /// without a recorded decision is the thing this release removes.
    /// </summary>
    [Fact]
    public void AMessageWithNoModeRequested_IsGatedLikeAMission()
    {
        var outcome = Runner().Run(Chat(), "delete the logging module");

        Assert.False(outcome.Started);
        Assert.Equal(0, _missionsStarted);
        Assert.Contains(_memory.LoadEscalationDecisions("c1"),
            d => d.Action == ConversationRunner.StartMissionAction && !d.Allowed);
    }

    /// <summary>
    /// The operator's message reaches the mission as its GOAL, with the conversation context that
    /// tells the colony what "this" and "these" point at.
    /// </summary>
    [Fact]
    public void TheOperatorsMessage_IsTheMissionGoal()
    {
        string? goal = null;
        var runner = new ConversationRunner(_memory, (g, _, onCreated, _) => { goal = g; onCreated("m1"); return "m1"; });

        runner.Run(Chat(EscalationPolicy.Bypass), "make the header sticky", ConversationMode.Mission);

        Assert.NotNull(goal);
        Assert.Contains("make the header sticky", goal);
    }

    /// <summary>
    /// ATTACHMENTS REACH THE WORK. They used to be read by the chat prompt, which no longer exists,
    /// so deleting that lane without rehoming them would have left a turn showing a file the mission
    /// could not read — recorded, visible, and consumed by nobody.
    /// </summary>
    [Fact]
    public void AnAttachment_TravelsIntoTheMissionGoal()
    {
        string? goal = null;
        var runner = new ConversationRunner(_memory, (g, _, onCreated, _) => { goal = g; onCreated("m1"); return "m1"; });

        runner.Run(Chat(EscalationPolicy.Bypass), "implement this", ConversationMode.Mission,
            attachments: new[] { ("spec.md", "The header must stay pinned on scroll.") });

        Assert.Contains("spec.md", goal);
        Assert.Contains("The header must stay pinned on scroll.", goal);
    }

    /// <summary>
    /// A truncated attachment SAYS it was truncated. Silently clipping a spec is worse than
    /// dropping it: the colony proceeds confidently against half a document and nothing records why.
    /// </summary>
    [Fact]
    public void AnOversizedAttachment_IsTruncatedOutLoud()
    {
        string? goal = null;
        var runner = new ConversationRunner(_memory, (g, _, onCreated, _) => { goal = g; onCreated("m1"); return "m1"; });

        runner.Run(Chat(EscalationPolicy.Bypass), "read it", ConversationMode.Mission,
            attachments: new[] { ("big.txt", new string('x', 20_000)) });

        Assert.Contains("truncated", goal);
        Assert.Contains("20000 chars total", goal);
    }

    /// <summary>
    /// v0.3.8.46, found live: an answer the operator gives is RECORDED when given, not only if
    /// some tool consults it. The trap: an operator approved a refused action, the re-run mission
    /// planned differently and never asked again, the approval evaporated unrecorded — and the
    /// stale refusal kept the conversation in "waiting on you" forever.
    /// </summary>
    [Fact]
    public void AnAnswerTheOperatorGives_IsRecordedWhetherOrNotTheWorkConsultsIt()
    {
        var chat = Chat();
        // The earlier refusal that put the action on the waiting list.
        _memory.SaveEscalationDecision(EscalationGate.Evaluate(chat, "run_allowlisted_check"));
        Assert.Contains("run_allowlisted_check", ConversationStateReader.Read(_memory, "c1").WaitingOn);

        var answers = Approve();
        answers["run_allowlisted_check"] = "approve";
        Runner().Run(chat, "go", ConversationMode.Mission, answers);

        // The approval exists in the record even though no tool asked for it — and the waiting
        // list clears, because the operator has answered.
        var decisions = _memory.LoadEscalationDecisions("c1");
        Assert.Contains(decisions, d => d.Action == "run_allowlisted_check" && d.Allowed);
        Assert.DoesNotContain("run_allowlisted_check", ConversationStateReader.Read(_memory, "c1").WaitingOn);
    }

    [Fact]
    public void EveryTurnIsRecorded_InOrder()
    {
        var runner = Runner();
        var chat = Chat();

        runner.Run(chat, "first");
        runner.Run(chat, "second");

        var turns = _memory.LoadConversationTurns("c1");
        Assert.Equal(new[] { 1, 2 }, turns.Select(t => t.Ordinal));
        Assert.Equal("first", turns[0].Content);
    }

    // ---- escalation is explicit, and gated -------------------------------------------------------

    /// <summary>
    /// The load-bearing refusal. Asking for a mission under Ask, with no answer, starts NOTHING —
    /// and the mission pipeline is never invoked, which is what makes this a boundary rather than a
    /// report written after the fact.
    /// </summary>
    [Fact]
    public void UnderAsk_AnUnapprovedEscalation_StartsNothing()
    {
        var outcome = Runner().Run(Chat(), "refactor the whole module", ConversationMode.Mission);

        Assert.False(outcome.Started);
        Assert.Null(outcome.MissionId);
        Assert.Equal(0, _missionsStarted);
        Assert.Contains("escalation refused", outcome.Summary);
    }

    [Fact]
    public void UnderAsk_AnApprovedEscalation_StartsAMission()
    {
        var outcome = Runner().Run(Chat(), "refactor the module", ConversationMode.Mission, Approve());

        Assert.True(outcome.Started);
        Assert.Equal("mission-1", outcome.MissionId);
        Assert.Equal(1, _missionsStarted);
        Assert.True(outcome.Decision!.WasAskedDirectly);
    }

    /// <summary>
    /// A standing policy already answered this question. That is the payoff of reusing the tool gate
    /// rather than inventing a second approval path — one decision covers both.
    /// </summary>
    [Theory]
    [InlineData(EscalationPolicy.AutoApprove)]
    [InlineData(EscalationPolicy.Bypass)]
    public void UnderAStandingPolicy_EscalationProceedsWithoutAsking(EscalationPolicy policy)
    {
        var outcome = Runner().Run(Chat(policy), "do the work", ConversationMode.Mission);

        Assert.True(outcome.Started);
        Assert.Equal(1, _missionsStarted);
        Assert.Equal("zwright", outcome.Decision!.DecidedBy);
        Assert.False(outcome.Decision.WasAskedDirectly);
    }

    /// <summary>
    /// The same fail-closed rule as everywhere else: a standing permission nobody can be shown to
    /// have given falls back to asking.
    /// </summary>
    [Fact]
    public void AnUnattributedStandingPolicy_StillAsks()
    {
        var orphan = new Conversation { Id = "c1", Policy = EscalationPolicy.Bypass };
        _memory.SaveConversation(orphan);

        Assert.False(Runner().Run(orphan, "go", ConversationMode.Mission).Started);
        Assert.Equal(0, _missionsStarted);
    }

    /// <summary>
    /// A cancelled conversation starts nothing. Refusing to BEGIN work is the cheap half of
    /// "cancelling a conversation cancels the work it started" — stopping something is harder than
    /// not starting it.
    ///
    /// v0.3.8.58: asserted through BOTH entry points. The bare-message path used to be a different
    /// lane with its own cancellation check, and collapsing two lanes into one is exactly when a
    /// guarantee that held on both quietly comes to hold on the survivor only.
    /// </summary>
    [Theory]
    [InlineData(ConversationMode.Mission)]
    [InlineData(ConversationMode.Chat)]
    public void ACancelledConversation_StartsNothing(ConversationMode requested)
    {
        var outcome = Runner().Run(Chat(EscalationPolicy.Bypass, cancelled: true),
            "go", requested, Approve());

        Assert.False(outcome.Started);
        Assert.Equal(0, _missionsStarted);
        Assert.Contains("cancelled", outcome.Summary);
        Assert.Empty(_memory.LoadConversationTurns("c1"));   // nothing recorded, nothing invented
    }

    /// <summary>The cancellation token reaches the mission, so a cancelled conversation can stop it.</summary>
    [Fact]
    public void TheCancellationTokenReachesTheMission()
    {
        using var cts = new CancellationTokenSource();

        Runner().Run(Chat(EscalationPolicy.Bypass), "go", ConversationMode.Mission, null, cts.Token);
        cts.Cancel();

        Assert.True(_lastToken.IsCancellationRequested);
    }

    // ---- one history ------------------------------------------------------------------------------

    /// <summary>
    /// The exit gate: the conversation and the mission are ONE history. Recorded on BOTH sides so
    /// the join works from either direction — the turn says which mission it started, and the
    /// conversation lists what it has started.
    /// </summary>
    [Fact]
    public void AnEscalatedTurn_LinksTheMissionFromBothSides()
    {
        Runner().Run(Chat(EscalationPolicy.Bypass), "go", ConversationMode.Mission);

        var turn = Assert.Single(_memory.LoadConversationTurns("c1"));
        Assert.Equal("mission-1", turn.MissionId);
        Assert.Contains("mission-1", _memory.LoadConversation("c1")!.MissionIds);
    }

    /// <summary>
    /// A REFUSED escalation is still part of the history — arguably its most interesting moment,
    /// since it is when the colony wanted more authority than it had.
    /// </summary>
    [Fact]
    public void ARefusedEscalation_IsStillRecorded()
    {
        Runner().Run(Chat(), "go", ConversationMode.Mission);

        Assert.Single(_memory.LoadConversationTurns("c1"));
        var decision = Assert.Single(_memory.LoadEscalationDecisions("c1"));
        Assert.False(decision.Allowed);
        Assert.Equal(ConversationRunner.StartMissionAction, decision.Action);
    }

    // ---- one budget, both modes -------------------------------------------------------------------

    /// <summary>
    /// The limit per-execution budgets structurally CANNOT enforce. Each escalation gets a fresh
    /// loop budget and looks like the first one; only a budget belonging to the CONVERSATION can see
    /// that the total work it has authorised keeps growing.
    /// </summary>
    [Fact]
    public void TheConversationBudget_CapsHowMuchWorkOneConversationCanStart()
    {
        var runner = Runner();
        var chat = Chat(EscalationPolicy.Bypass) with { Budget = new ConversationBudget(MaxMissions: 2) };
        _memory.SaveConversation(chat);

        for (var i = 0; i < 3; i++)
            chat = _memory.LoadConversation("c1")! with { Budget = chat.Budget };

        // start two, which the budget allows
        runner.Run(chat, "one", ConversationMode.Mission);
        chat = _memory.LoadConversation("c1")! with { Budget = new ConversationBudget(MaxMissions: 2) };
        runner.Run(chat, "two", ConversationMode.Mission);
        chat = _memory.LoadConversation("c1")! with { Budget = new ConversationBudget(MaxMissions: 2) };

        var third = runner.Run(chat, "three", ConversationMode.Mission);

        Assert.False(third.Started);
        Assert.Equal(2, _missionsStarted);
        Assert.Contains("budget exhausted", third.Summary);
    }

    /// <summary>
    /// Budget is checked BEFORE the gate. Asking an operator to approve something that will be
    /// refused anyway trains them to approve without reading — and the decision log should not fill
    /// with approvals for work that never ran.
    /// </summary>
    [Fact]
    public void AnExhaustedBudget_DoesNotAskTheOperator()
    {
        var runner = Runner();
        var chat = Chat() with { Budget = new ConversationBudget(MaxMissions: 0) };
        _memory.SaveConversation(chat);

        var outcome = runner.Run(chat, "go", ConversationMode.Mission, Approve());

        Assert.False(outcome.Started);
        Assert.Null(outcome.Decision);
        Assert.Empty(_memory.LoadEscalationDecisions("c1"));
    }

    /// <summary>
    /// The tool loop stops inventing its own numbers: its budget is PROJECTED from the
    /// conversation's, so both modes count against limits that came from one place.
    /// </summary>
    [Fact]
    public void TheToolLoopBudget_ComesFromTheConversation()
    {
        var budget = new ConversationBudget(MaxTurns: 3, MaxToolCalls: 7, MaxSeconds: 42);

        var loop = budget.ForToolLoop();

        Assert.Equal(3, loop.MaxTurns);
        Assert.Equal(7, loop.MaxToolCalls);
        Assert.Equal(42, loop.MaxSeconds);
    }

    // ---- cancelling a conversation cancels its work ----------------------------------------------

    /// <summary>
    /// The exit gate, in full. Marking a row cancelled does not stop a mission that is ALREADY
    /// RUNNING — this is the half that actually stops it, and without it the gate would have been
    /// satisfied on paper by a flag nobody was reading.
    /// </summary>
    [Fact]
    public void Cancelling_StopsWorkThatIsAlreadyRunning()
    {
        var runner = Runner();
        runner.Run(Chat(EscalationPolicy.Bypass), "go", ConversationMode.Mission);

        var stopped = runner.Cancel("c1");

        Assert.Equal(1, stopped);
        Assert.True(_lastToken.IsCancellationRequested);
        Assert.True(_memory.LoadConversation("c1")!.Cancelled);
    }

    /// <summary>
    /// A conversation that escalated several times has several things to stop, and the operator
    /// should not have to know that — which is why live work is keyed by CONVERSATION, not mission.
    /// </summary>
    [Fact]
    public void Cancelling_StopsEveryMissionTheConversationStarted()
    {
        var runner = Runner();
        var chat = Chat(EscalationPolicy.Bypass);
        runner.Run(chat, "first", ConversationMode.Mission);
        runner.Run(chat, "second", ConversationMode.Mission);

        Assert.Equal(2, runner.Cancel("c1"));
    }

    /// <summary>
    /// The count distinguishes "stopped two missions" from "there was nothing running". Silence on
    /// that distinction is what makes people press cancel twice.
    /// </summary>
    [Fact]
    public void CancellingWithNothingRunning_ReportsZero_AndStillMarksCancelled()
    {
        var runner = Runner();
        Chat();

        Assert.Equal(0, runner.Cancel("c1"));
        Assert.True(_memory.LoadConversation("c1")!.Cancelled);
    }

    /// <summary>
    /// Cancelling twice is safe. An operator who does not see an immediate effect presses it again,
    /// and the second press must not throw on a token source already disposed.
    /// </summary>
    [Fact]
    public void CancellingTwice_IsSafe()
    {
        var runner = Runner();
        runner.Run(Chat(EscalationPolicy.Bypass), "go", ConversationMode.Mission);

        Assert.Equal(1, runner.Cancel("c1"));
        Assert.Equal(0, runner.Cancel("c1"));
    }

    /// <summary>
    /// And after cancelling, no NEW work can start — the guarantee that does not depend on anyone
    /// else's cooperation, since it holds even for a mission that ignores its token.
    /// </summary>
    [Fact]
    public void AfterCancelling_NoNewWorkStarts()
    {
        var runner = Runner();
        var chat = Chat(EscalationPolicy.Bypass);
        runner.Run(chat, "first", ConversationMode.Mission);
        runner.Cancel("c1");

        var outcome = runner.Run(_memory.LoadConversation("c1")!, "second", ConversationMode.Mission);

        Assert.False(outcome.Started);
        Assert.Equal(1, _missionsStarted);
    }

    /// <summary>
    /// Starting a mission is registered in the ONE side-effect set, not special-cased in the runner.
    /// A boundary enforced in two places eventually disagrees with itself.
    /// </summary>
    [Fact]
    public void StartingAMission_IsInTheSharedSideEffectSet() =>
        Assert.True(EscalationGate.NeedsDecision(ConversationRunner.StartMissionAction));

    /// <summary>
    /// And it is NOT a tool. No model may call it, and nothing registers it in the tool registry —
    /// it appears in the side-effect set purely so one gate covers it.
    /// </summary>
    [Fact]
    public void StartingAMission_IsNotATool() =>
        Assert.False(Anthill.Core.Tools.ToolInventory.Exists(ConversationRunner.StartMissionAction));

    // ---- an id, or nothing --------------------------------------------------------------------

    /// <summary>
    /// The defect this guards against was real, and was found in the RUNNING system rather than here.
    ///
    /// Before missions moved to the background the runner linked the pipeline's return value, which
    /// is the mission REPORT rather than its id. A conversation's MissionIds ended up holding a
    /// multi-kilobyte narrative: it filled the console panel end to end, and every
    /// conversation-to-mission join silently resolved to nothing while the data looked healthy.
    ///
    /// A report is distinguishable from an id by two cheap properties — length and whitespace — and
    /// that is deliberately all this checks. A stricter rule (GUIDs only) would reject id formats
    /// the pipeline is free to adopt later, and a guard that fails on correct input gets deleted.
    /// </summary>
    [Theory]
    [InlineData("Mission Failed\n\nGoal:\nrefactor everything")]
    [InlineData("a mission id with spaces")]
    public void AReportReportedWhereAnIdWasExpected_IsNotLinked(string notAnId)
    {
        var runner = new ConversationRunner(_memory, (_, _, onCreated, _) =>
        {
            onCreated(notAnId);
            return notAnId;
        });

        var outcome = runner.Run(Chat(EscalationPolicy.Bypass), "go", ConversationMode.Mission);

        // The work DID start, so that is reported honestly — but nothing is linked, because a bad
        // link is worse than a missing one. A gap is something an operator can investigate.
        Assert.True(outcome.Started);
        Assert.Null(outcome.MissionId);
        Assert.Contains("not a mission id", outcome.Summary);
        Assert.Empty(_memory.LoadConversation("c1")!.MissionIds);
        Assert.Null(Assert.Single(_memory.LoadConversationTurns("c1")).MissionId);
    }

    /// <summary>A real id is still linked — the guard must not cost the normal case.</summary>
    [Fact]
    public void ARealMissionId_IsStillLinked()
    {
        var id = Guid.NewGuid().ToString();
        var runner = new ConversationRunner(_memory, (_, _, onCreated, _) => { onCreated(id); return id; });

        Assert.Equal(id, runner.Run(Chat(EscalationPolicy.Bypass), "go", ConversationMode.Mission).MissionId);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("has space", false)]
    [InlineData("has\nnewline", false)]
    [InlineData("94c901b6-7626-4476-a8cf-856f192f9629", true)]
    [InlineData("mission-1", true)]
    public void LooksLikeMissionId_AcceptsIdsAndRejectsProse(string candidate, bool expected) =>
        Assert.Equal(expected, ConversationRunner.LooksLikeMissionId(candidate));

    /// <summary>Anything longer than a GUID by a wide margin is prose, not an identifier.</summary>
    [Fact]
    public void LooksLikeMissionId_RejectsSomethingFarTooLongToBeAnId() =>
        Assert.False(ConversationRunner.LooksLikeMissionId(
            new string('a', ConversationRunner.MaxMissionIdLength + 1)));

    /// <summary>
    /// v0.3.8.48, found live: approving a refused start_mission re-sends the SAME message —
    /// convApprove restates it to meet the gate — and the transcript said the operator spoke twice.
    /// The refused attempt IS the turn; approval links the mission to it rather than inventing a
    /// duplicate.
    /// </summary>
    [Fact]
    public void ApprovingARefusedEscalation_DoesNotDuplicateTheOperatorsTurn()
    {
        var conversation = Chat();   // Ask: the first attempt is refused and recorded
        var runner = Runner();

        Assert.False(runner.Run(conversation, "do the thing", ConversationMode.Mission).Started);
        Assert.Single(_memory.LoadConversationTurns("c1"));

        var outcome = runner.Run(conversation, "do the thing", ConversationMode.Mission, Approve());
        _release.Set();

        Assert.True(outcome.Started);
        var userTurns = _memory.LoadConversationTurns("c1")
            .Where(t => t.Role == "user" && t.Content == "do the thing").ToList();
        var only = Assert.Single(userTurns);
        Assert.Equal(outcome.MissionId, only.MissionId);   // the attempt gained its mission link
    }

    /// <summary>
    /// v0.3.8.48, found live: the mission settled, its answer sat in mission history, and the chat
    /// that started it showed nothing. When the pipeline finishes, the mission's result becomes the
    /// conversation's next turn — asked in chat, answered in chat.
    ///
    /// v0.3.8.95: the turn now leads with the answer and carries the COMPILED MISSION RECORD
    /// beneath it — MissionReport's projection of the rows, the artifact that had writers and no
    /// reader. This mission has no evaluation persisted, and the record must SAY so ("none
    /// persisted") rather than invent one: honest absence is part of what is under test.
    /// </summary>
    [Fact]
    public void ASettledMissionsAnswer_LandsInTheConversation()
    {
        var conversation = Chat(EscalationPolicy.Bypass);
        var id = Guid.NewGuid().ToString();
        var runner = new ConversationRunner(_memory, (_, _, onCreated, _) =>
        {
            onCreated(id);
            // The pipeline saves the settled mission BEFORE returning, as the Queen does.
            _memory.SaveMission(new Anthill.Core.Domain.Mission
            {
                Id = id, Goal = "say OK", Status = Anthill.Core.Domain.MissionStatus.Complete,
                UserResult = "OK", SuccessScore = 1,
            });
            return id;
        });

        Assert.True(runner.Run(conversation, "say OK", ConversationMode.Mission).Started);

        // The answer is recorded by the background thread that ran the pipeline; wait for it.
        ConversationTurn? answer = null;
        for (var i = 0; i < 100 && answer is null; i++)
        {
            answer = _memory.LoadConversationTurns("c1")
                .FirstOrDefault(t => t.Role == "assistant");
            if (answer is null) Thread.Sleep(50);
        }

        Assert.NotNull(answer);
        Assert.StartsWith("OK", answer!.Content, StringComparison.Ordinal);
        Assert.Contains("=== MISSION RECORD", answer.Content, StringComparison.Ordinal);
        Assert.Contains("outcome_code: none persisted", answer.Content, StringComparison.Ordinal);
        Assert.Equal(id, answer.MissionId);
    }

    /// <summary>A cancelled conversation gets no late answer — the operator already moved on.</summary>
    [Fact]
    public void ACancelledConversation_GetsNoLateMissionAnswer()
    {
        var conversation = Chat(EscalationPolicy.Bypass);
        var id = Guid.NewGuid().ToString();
        var pipelineDone = new ManualResetEventSlim(false);
        var runner = new ConversationRunner(_memory, (_, _, onCreated, _) =>
        {
            onCreated(id);
            _memory.SaveMission(new Anthill.Core.Domain.Mission
            {
                Id = id, Goal = "slow work", Status = Anthill.Core.Domain.MissionStatus.Complete,
                UserResult = "too late",
            });
            // Cancel the conversation while the pipeline is still "running" — but only after the
            // runner has linked the mission, so the runner's own save cannot overwrite the cancel.
            for (var i = 0; i < 100 && !(_memory.LoadConversation("c1")?.MissionIds.Contains(id) ?? false); i++)
                Thread.Sleep(50);
            _memory.SaveConversation(_memory.LoadConversation("c1")! with { Cancelled = true });
            pipelineDone.Set();
            return id;
        });

        runner.Run(conversation, "slow work", ConversationMode.Mission);
        Assert.True(pipelineDone.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(300);   // give the background recorder its chance to misbehave

        Assert.DoesNotContain(_memory.LoadConversationTurns("c1"), t => t.Role == "assistant");
    }
}
