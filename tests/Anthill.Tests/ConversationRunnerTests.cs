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

    public ConversationRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-runner-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "memory.db"));
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>The mission pipeline, faked — the runner decides WHETHER, the Queen decides what.</summary>
    private ConversationRunner Runner() => new(_memory, (_, token) =>
    {
        _missionsStarted++;
        _lastToken = token;
        return $"mission-{_missionsStarted}";
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

    // ---- chat is the default, and is not gated here ----------------------------------------------

    /// <summary>
    /// Chat runs without an escalation decision. The tools it may call are gated at DISPATCH, which
    /// is the correct place: a conversation that only reads needs no permission, and one that tries
    /// to write is stopped at the write rather than at the sentence before it.
    /// </summary>
    [Fact]
    public void Chat_RunsWithoutAnEscalationDecision()
    {
        var outcome = Runner().Run(Chat(), "what does this repository do?");

        Assert.Equal(ConversationMode.Chat, outcome.Mode);
        Assert.True(outcome.Started);
        Assert.Null(outcome.MissionId);
        Assert.Equal(0, _missionsStarted);
        Assert.Empty(_memory.LoadEscalationDecisions("c1"));
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
}
