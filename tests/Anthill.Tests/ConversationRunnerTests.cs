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
    private ConversationRunner Runner(Func<string, Action<string>?, ConversationReply>? ask = null) => new(_memory, (_, onCreated, token) =>
    {
        var id = $"mission-{Interlocked.Increment(ref _missionsStarted)}";
        _lastToken = token;

        // The id is reported IMMEDIATELY — that is the contract the runner depends on — and then the
        // mission keeps running, exactly as the real pipeline does.
        onCreated(id);

        try { _release.Wait(TimeSpan.FromSeconds(10), token); }
        catch (OperationCanceledException) { /* cancelled mid-flight, which is a valid ending */ }

        return id;
    }, ask);

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

    // ---- chat is the default, and is not gated here ----------------------------------------------

    /// <summary>
    /// Chat runs without an escalation decision. The tools it may call are gated at DISPATCH, which
    /// is the correct place: a conversation that only reads needs no permission, and one that tries
    /// to write is stopped at the write rather than at the sentence before it.
    ///
    /// v0.3.8.42: and it is ANSWERED. The reply is recorded as an assistant turn carrying the
    /// provider/model that produced it, and the prompt the provider saw contains the operator's
    /// message — the loop the ConversationMode.Chat doc always described, finally built.
    /// </summary>
    [Fact]
    public void Chat_RunsWithoutAnEscalationDecision_AndIsAnswered()
    {
        string? seen = null;
        var outcome = Runner((prompt, _) => { seen = prompt;
            return new ConversationReply(true, "It orchestrates missions.", "agent:claude-code", "Claude Code", null); })
            .Run(Chat(), "what does this repository do?");

        Assert.Equal(ConversationMode.Chat, outcome.Mode);
        Assert.True(outcome.Started);
        Assert.Null(outcome.MissionId);
        Assert.Equal(0, _missionsStarted);
        Assert.Empty(_memory.LoadEscalationDecisions("c1"));
        Assert.Contains("answered by agent:claude-code/Claude Code", outcome.Summary);
        Assert.Contains("what does this repository do?", seen);

        var turns = _memory.LoadConversationTurns("c1");
        Assert.Equal(2, turns.Count);
        Assert.Equal("assistant", turns[1].Role);
        Assert.Equal("It orchestrates missions.", turns[1].Content);
        Assert.Equal("agent:claude-code", turns[1].Provider);
        Assert.Equal("Claude Code", turns[1].Model);
    }

    /// <summary>A runtime composed without reasoning says so — it does not spin, and it does not
    /// pretend. The message is still recorded; history survives the missing capability.</summary>
    [Fact]
    public void Chat_WithoutAComposedProvider_SaysSo()
    {
        var outcome = Runner().Run(Chat(), "hello?");

        Assert.False(outcome.Started);
        Assert.Contains("no reasoning provider is composed", outcome.Summary);
        Assert.Single(_memory.LoadConversationTurns("c1"));   // the operator's message, nothing invented
    }

    /// <summary>A failed provider call records NO fake turn — the summary carries the classified
    /// error and the remedy (route 'conversation' to a working provider).</summary>
    [Fact]
    public void Chat_ProviderFailure_RecordsNoFakeTurn()
    {
        var outcome = Runner((_, _) => new ConversationReply(false, "", "local", "llama", "ConnectError: Could not connect"))
            .Run(Chat(), "hello?");

        Assert.False(outcome.Started);
        Assert.Contains("Could not connect", outcome.Summary);
        Assert.Contains("Providers & Model Routing", outcome.Summary);
        Assert.Single(_memory.LoadConversationTurns("c1"));
    }

    /// <summary>Stop pressed while the provider was thinking: the reply is discarded, because a
    /// reply landing in a cancelled conversation would look like it ignored the Stop.</summary>
    [Fact]
    public void Chat_ReplyAfterCancel_IsDiscarded()
    {
        var chat = Chat();
        var runner = Runner((_, _) =>
        {
            // The cancel arrives while the provider is thinking.
            _memory.SaveConversation(chat with { Cancelled = true });
            return new ConversationReply(true, "too late", "local", "llama", null);
        });

        var outcome = runner.Run(chat, "hello?");

        Assert.False(outcome.Started);
        Assert.Contains("cancelled while answering", outcome.Summary);
        Assert.Single(_memory.LoadConversationTurns("c1"));
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
    /// </summary>
    [Fact]
    public void ACancelledConversation_StartsNothing()
    {
        var outcome = Runner().Run(Chat(EscalationPolicy.Bypass, cancelled: true),
            "go", ConversationMode.Mission, Approve());

        Assert.False(outcome.Started);
        Assert.Equal(0, _missionsStarted);
        Assert.Contains("cancelled", outcome.Summary);
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
        var runner = new ConversationRunner(_memory, (_, onCreated, _) =>
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
        var runner = new ConversationRunner(_memory, (_, onCreated, _) => { onCreated(id); return id; });

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
}
