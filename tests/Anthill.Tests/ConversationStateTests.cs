using Anthill.Core.Conversations;
using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.7.0 — "what is it doing, what did it do, what is it waiting on", surfaced conversationally.
///
/// Three questions kept SEPARATE. Collapsing them into one status string is the usual design and it
/// is why status displays go stale: "running" cannot express that the colony finished forty seconds
/// ago and is now waiting for a human — which is the single state an operator most needs to notice,
/// because nothing moves until they act.
///
/// Everything here is DERIVED from the transcript and the decision log, never stored. A stored
/// status is a second thing to keep in step with reality, and it fails exactly where it is relied
/// on: a process that died leaves its last write saying "running" forever.
/// </summary>
public class ConversationStateTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _memory;

    public ConversationStateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-state-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "memory.db"));
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Conversation Save(Conversation conversation)
    {
        _memory.SaveConversation(conversation);
        return conversation;
    }

    private Conversation Chat(EscalationPolicy policy = EscalationPolicy.Ask, bool cancelled = false) =>
        Save(new Conversation
        {
            Id = "c1", Policy = policy, Cancelled = cancelled,
            PolicySetBy = policy == EscalationPolicy.Ask ? null : "zwright",
            PolicySetAt = policy == EscalationPolicy.Ask ? null : DateTime.UtcNow,
        });

    private ConversationState Read() => ConversationStateReader.Read(_memory, "c1");

    [Fact]
    public void AFreshConversation_SaysSoRatherThanNothing()
    {
        Chat();
        Assert.Equal("no turns yet", Read().Doing);
    }

    /// <summary>
    /// The state that matters most: work stopped, and it stopped because it needs a human. This is
    /// the one condition where nothing moves until the operator acts, so it is a first-class
    /// property rather than something a UI has to infer.
    /// </summary>
    [Fact]
    public void ARefusedActionAwaitingADecision_IsReportedAsWaiting()
    {
        var chat = Chat();
        _memory.SaveEscalationDecision(EscalationGate.Evaluate(chat, "apply_patch"));

        var state = Read();

        Assert.True(state.NeedsOperator);
        Assert.Contains("apply_patch", state.WaitingOn);
        Assert.Contains("waiting for an operator decision", state.Doing);
    }

    /// <summary>
    /// An action that was refused and LATER approved is not still waiting. Leaving it on the list
    /// would train an operator to ignore the list — the only way this feature actually fails.
    /// </summary>
    [Fact]
    public void AnActionSinceApproved_IsNoLongerWaiting()
    {
        var chat = Chat();
        _memory.SaveEscalationDecision(EscalationGate.Evaluate(chat, "apply_patch"));
        _memory.SaveEscalationDecision(EscalationGate.Evaluate(chat, "apply_patch", "approve"));

        var state = Read();

        Assert.False(state.NeedsOperator);
        Assert.Empty(state.WaitingOn);
    }

    /// <summary>
    /// A standing policy never produces a waiting state — nothing was ever refused for want of a
    /// decision, because the decision was made in advance.
    /// </summary>
    [Fact]
    public void UnderAStandingPolicy_NothingWaits()
    {
        var chat = Chat(EscalationPolicy.Bypass);
        _memory.SaveEscalationDecision(EscalationGate.Evaluate(chat, "apply_patch"));

        Assert.False(Read().NeedsOperator);
    }

    /// <summary>Cancelled beats everything. An operator who stopped it does not need a to-do list.</summary>
    [Fact]
    public void ACancelledConversation_ReportsCancelled_NotWaiting()
    {
        var chat = Chat(cancelled: true);
        _memory.SaveEscalationDecision(EscalationGate.Evaluate(chat with { Cancelled = false }, "apply_patch"));

        Assert.Equal("cancelled", Read().Doing);
    }

    [Fact]
    public void ARunningMission_IsWhatItIsDoing()
    {
        Save(Chat() with { MissionIds = new[] { "m-1", "m-2" } });

        Assert.Equal("running mission m-2", Read().Doing);
    }

    /// <summary>
    /// What it DID names the model that actually produced each turn. Capability-aware routing can
    /// substitute a model mid-conversation, and an operator reading back a surprising answer needs
    /// the model that produced it rather than the one that was configured.
    /// </summary>
    [Fact]
    public void TheHistoryNamesTheModelThatActuallyAnswered()
    {
        Chat();
        _memory.SaveConversationTurn(new ConversationTurn("t1", "c1", 1, "assistant", "looked")
        {
            Model = "gemma4:31b",
            ToolsCalled = new[] { "search_workspace" },
        });

        var did = Assert.Single(Read().Did);
        Assert.Contains("search_workspace", did);
        Assert.Contains("gemma4:31b", did);
    }

    [Fact]
    public void AnEscalatedTurn_ReadsAsAnEscalation()
    {
        Chat();
        _memory.SaveConversationTurn(new ConversationTurn("t1", "c1", 1, "user", "go") { MissionId = "m-7" });

        Assert.Contains("escalated into mission m-7", Assert.Single(Read().Did));
    }

    /// <summary>The history is bounded — beyond that an operator reads the transcript itself.</summary>
    [Fact]
    public void TheHistoryIsBounded()
    {
        Chat();
        for (var i = 1; i <= ConversationStateReader.RecentTurns + 5; i++)
            _memory.SaveConversationTurn(new ConversationTurn($"t{i}", "c1", i, "user", $"turn {i}"));

        var state = Read();
        Assert.Equal(ConversationStateReader.RecentTurns, state.Did.Count);
        Assert.Contains($"#{ConversationStateReader.RecentTurns + 5}", state.Did[^1]);   // the LATEST, not the first
    }

    /// <summary>
    /// The reported policy is the EFFECTIVE one. An unattributed standing permission falls back to
    /// Ask, and reporting the stored value would tell an operator they had switched approvals off
    /// when they had not.
    /// </summary>
    [Fact]
    public void TheReportedPolicy_IsTheEffectiveOne()
    {
        Save(new Conversation { Id = "c1", Policy = EscalationPolicy.Bypass });   // no author

        Assert.Equal(EscalationPolicy.Ask, Read().Policy);
    }

    /// <summary>An unknown conversation is answered, not thrown. A missing id is a question, not a fault.</summary>
    [Fact]
    public void AnUnknownConversation_IsAnswered()
    {
        var state = ConversationStateReader.Read(_memory, "nope");

        Assert.Equal("no such conversation", state.Doing);
        Assert.False(state.NeedsOperator);
    }
}
