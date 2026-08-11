using Anthill.Core.Conversations;
using Anthill.Core.Memory;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.7.0 — conversations as first-class runtime objects, and the escalation boundary.
///
/// The operator chose the approval model: manual, automatic, or bypassed entirely — the shape Claude
/// Code and opencode use. That reconciles with the phase's exit gate more cleanly than it first
/// appears, because the gate says no conversation may begin side-effecting work "without a RECORDED
/// OPERATOR DECISION" — not without a PROMPT. Choosing auto-approve or bypass IS that decision,
/// recorded once with an author and a timestamp.
///
/// What the gate forbids is work proceeding under an unrecorded default. So the tests below are
/// mostly about attribution: who permitted this, when, and can it be shown afterwards.
/// </summary>
public class ConversationTests : IDisposable
{
    private readonly string _dir;

    public ConversationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-conv-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SqliteMemory Memory() => new(Path.Combine(_dir, "memory.db"));

    private static Conversation Chat(EscalationPolicy policy = EscalationPolicy.Ask, string? by = "zwright") => new()
    {
        Id = "c1",
        Title = "a conversation",
        Role = "researcher",
        Policy = policy,
        PolicySetBy = policy == EscalationPolicy.Ask ? null : by,
        PolicySetAt = policy == EscalationPolicy.Ask ? null : DateTime.UtcNow,
    };

    // ---- the escalation boundary ----------------------------------------------------------------

    /// <summary>
    /// Reading, searching and indexing are the bulk of a conversation and change nothing outside the
    /// process. Gating them would bury the decisions that matter under decisions that do not.
    /// </summary>
    [Theory]
    [InlineData("read_text_file")]
    [InlineData("search_workspace")]
    [InlineData("repository_index")]
    [InlineData("system_info")]
    public void ReadOnlyActions_NeedNoDecision(string action)
    {
        Assert.False(EscalationGate.NeedsDecision(action));
        Assert.True(EscalationGate.Evaluate(Chat(), action).Allowed);
    }

    [Theory]
    [InlineData("apply_patch")]
    [InlineData("write_text_file")]
    [InlineData("shell_command")]
    public void SideEffectingActions_NeedADecision(string action) =>
        Assert.True(EscalationGate.NeedsDecision(action));

    /// <summary>
    /// Under Ask, absence of an answer is NOT consent. A caller that forgot to ask gets a refusal —
    /// the failure mode worth having, because the alternative is work proceeding because nobody got
    /// round to the question.
    /// </summary>
    [Fact]
    public void UnderAsk_NoAnswerMeansNo()
    {
        var decision = EscalationGate.Evaluate(Chat(), "apply_patch");

        Assert.False(decision.Allowed);
        Assert.Equal("nobody", decision.DecidedBy);
        Assert.Contains("no operator decision", decision.Reason);
    }

    [Fact]
    public void UnderAsk_AnExplicitApprovalAllowsIt()
    {
        var decision = EscalationGate.Evaluate(Chat(), "apply_patch", "approve");

        Assert.True(decision.Allowed);
        Assert.Equal("operator", decision.DecidedBy);
        Assert.True(decision.WasAskedDirectly);
    }

    [Fact]
    public void UnderAsk_AnythingOtherThanApproval_Refuses()
    {
        Assert.False(EscalationGate.Evaluate(Chat(), "apply_patch", "deny").Allowed);
        Assert.False(EscalationGate.Evaluate(Chat(), "apply_patch", "maybe").Allowed);
    }

    /// <summary>
    /// Auto-approve does not interrupt, and the audit is unchanged: the action is still recorded,
    /// still attributed, and still says which standing decision permitted it.
    /// </summary>
    [Fact]
    public void UnderAutoApprove_ItProceeds_AndIsStillAttributed()
    {
        var decision = EscalationGate.Evaluate(Chat(EscalationPolicy.AutoApprove), "apply_patch");

        Assert.True(decision.Allowed);
        Assert.Equal("zwright", decision.DecidedBy);
        Assert.Contains("standing decision", decision.Reason);
        // and an audit can tell this apart from a fresh judgement
        Assert.False(decision.WasAskedDirectly);
    }

    /// <summary>
    /// Bypass still produces a record. "Why was this allowed" must have an answer that is not
    /// "nobody knows" — the operator turned the gate off, and that is itself the decision.
    /// </summary>
    [Fact]
    public void UnderBypass_ItProceeds_AndTheRecordSaysWhy()
    {
        var decision = EscalationGate.Evaluate(Chat(EscalationPolicy.Bypass), "shell_command");

        Assert.True(decision.Allowed);
        Assert.Equal("zwright", decision.DecidedBy);
        Assert.Contains("approvals bypassed", decision.Reason);
    }

    /// <summary>
    /// The load-bearing refusal. A standing permission with no author is indistinguishable from a
    /// default nobody chose — so it fails CLOSED, back to Ask, rather than granting a permission
    /// that cannot be shown to have been given.
    /// </summary>
    [Theory]
    [InlineData(EscalationPolicy.AutoApprove)]
    [InlineData(EscalationPolicy.Bypass)]
    public void AnUnattributedStandingPermission_FallsBackToAsking(EscalationPolicy policy)
    {
        var orphan = Chat(policy) with { PolicySetBy = null, PolicySetAt = null };

        Assert.False(orphan.PolicyIsAttributed);
        Assert.Equal(EscalationPolicy.Ask, orphan.EffectivePolicy);
        Assert.False(EscalationGate.Evaluate(orphan, "apply_patch").Allowed);
    }

    /// <summary>
    /// The exit gate "cancelling a conversation cancels the work it started", at the gate level: a
    /// cancelled conversation cannot authorise anything further, whatever its policy says.
    /// </summary>
    [Fact]
    public void ACancelledConversation_AuthorisesNothingFurther()
    {
        var cancelled = Chat(EscalationPolicy.Bypass) with { Cancelled = true };

        Assert.Equal(EscalationPolicy.Ask, cancelled.EffectivePolicy);
        Assert.False(EscalationGate.Evaluate(cancelled, "apply_patch").Allowed);
    }

    // ---- persistence ------------------------------------------------------------------------------

    /// <summary>
    /// The exit gate: a conversation survives a restart with its transcript and route intact. The
    /// route is stored PER TURN because it can change mid-conversation — capability-aware routing
    /// may substitute a tool-capable model, and a transcript reporting only the configured route
    /// would describe a conversation that did not happen.
    /// </summary>
    [Fact]
    public void AConversationSurvivesARestart_WithItsTranscriptAndRoute()
    {
        using (var memory = Memory())
        {
            memory.SaveConversation(Chat(EscalationPolicy.AutoApprove));
            memory.SaveConversationTurn(new ConversationTurn("t1", "c1", 1, "user", "please look"));
            memory.SaveConversationTurn(new ConversationTurn("t2", "c1", 2, "assistant", "looking")
            {
                Provider = "ollama",
                Model = "gemma4:31b",
                ToolsOffered = new[] { "search_workspace", "repository_index" },
                ToolsCalled = new[] { "search_workspace" },
            });
        }

        using var reopened = Memory();
        var conversation = reopened.LoadConversation("c1")!;
        var turns = reopened.LoadConversationTurns("c1");

        Assert.Equal(EscalationPolicy.AutoApprove, conversation.Policy);
        Assert.Equal("zwright", conversation.PolicySetBy);
        Assert.Equal(2, turns.Count);
        Assert.Equal("gemma4:31b", turns[1].Model);
        Assert.Equal(new[] { "search_workspace", "repository_index" }, turns[1].ToolsOffered);
        Assert.Equal(new[] { "search_workspace" }, turns[1].ToolsCalled);
    }

    /// <summary>Turns come back in order. Ordinal, not timestamp — two turns can share a clock tick.</summary>
    [Fact]
    public void TheTranscriptIsOrdered()
    {
        using var memory = Memory();
        memory.SaveConversation(Chat());
        foreach (var i in new[] { 3, 1, 2 })
            memory.SaveConversationTurn(new ConversationTurn($"t{i}", "c1", i, "user", $"turn {i}"));

        Assert.Equal(new[] { 1, 2, 3 }, memory.LoadConversationTurns("c1").Select(t => t.Ordinal));
    }

    /// <summary>
    /// The exit gate: the conversation and the mission are ONE history. A turn records the mission
    /// it started, so an escalated run can be read end to end rather than as two disconnected halves.
    /// </summary>
    [Fact]
    public void AnEscalatedTurn_LinksToItsMission()
    {
        using var memory = Memory();
        memory.SaveConversation(Chat() with { MissionIds = new[] { "m-42" } });
        memory.SaveConversationTurn(new ConversationTurn("t1", "c1", 1, "assistant", "starting work")
        {
            MissionId = "m-42",
        });

        Assert.Equal("m-42", memory.LoadConversationTurns("c1")[0].MissionId);
        Assert.Contains("m-42", memory.LoadConversation("c1")!.MissionIds);
    }

    /// <summary>
    /// Decisions persist, INCLUDING refusals. An audit asking "did the colony try to do X" needs the
    /// refused attempts as much as the permitted ones — arguably more, since a refused attempt is
    /// the one nobody saw happen.
    /// </summary>
    [Fact]
    public void RefusedDecisionsArePersisted_NotOnlyApprovals()
    {
        using var memory = Memory();
        memory.SaveConversation(Chat());
        memory.SaveEscalationDecision(EscalationGate.Evaluate(Chat(), "apply_patch"));            // refused
        memory.SaveEscalationDecision(EscalationGate.Evaluate(Chat(), "write_text_file", "approve"));

        var decisions = memory.LoadEscalationDecisions("c1");

        Assert.Equal(2, decisions.Count);
        Assert.Contains(decisions, d => d.Action == "apply_patch" && !d.Allowed);
        Assert.Contains(decisions, d => d.Action == "write_text_file" && d.Allowed);
    }

    /// <summary>
    /// An unreadable policy reads as Ask. Fail closed: the cost of asking unnecessarily is an
    /// interruption; the cost of the reverse is unattributed side effects.
    /// </summary>
    [Fact]
    public void AnUnreadablePolicy_FallsBackToAsking()
    {
        using var memory = Memory();
        memory.SaveConversation(Chat(EscalationPolicy.Bypass));

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_dir, "memory.db")}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET policy='SomethingNobodyDefined' WHERE id='c1'";
        cmd.ExecuteNonQuery();

        Assert.Equal(EscalationPolicy.Ask, memory.LoadConversation("c1")!.EffectivePolicy);
    }

    /// <summary>Policy is stored by NAME — an ordinal that shifts would silently change a permission level.</summary>
    [Fact]
    public void PolicyIsStoredByName_NotOrdinal()
    {
        using var memory = Memory();
        memory.SaveConversation(Chat(EscalationPolicy.Bypass));

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_dir, "memory.db")}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT policy FROM conversations WHERE id='c1'";

        Assert.Equal("Bypass", cmd.ExecuteScalar()?.ToString());
    }

    // ---- v0.3.8.46: pins and search -------------------------------------------------------------

    /// <summary>A pin survives restart like everything else in the record, and sorts first.</summary>
    [Fact]
    public void APinnedConversation_SortsFirst_AndSurvivesReload()
    {
        using var memory = Memory();
        memory.SaveConversation(new Conversation { Id = "old-pinned", Title = "keep me handy",
            Pinned = true, UpdatedAt = DateTime.UtcNow.AddDays(-30) });
        memory.SaveConversation(new Conversation { Id = "fresh", Title = "just now",
            UpdatedAt = DateTime.UtcNow });

        var list = memory.LoadConversations();

        // Pinned beats recency: the whole point of the pin is escaping the recency sort.
        Assert.Equal("old-pinned", list[0].Id);
        Assert.True(list[0].Pinned);
        Assert.False(list[1].Pinned);
    }

    /// <summary>
    /// Search reaches TRANSCRIPT content, not just titles — "which conversation was that in" is
    /// almost always a question about something said, not something named.
    /// </summary>
    [Fact]
    public void Search_FindsByTurnContent_CaseInsensitive_AndEscapesWildcards()
    {
        using var memory = Memory();
        memory.SaveConversation(new Conversation { Id = "a", Title = "deploy notes" });
        memory.SaveConversation(new Conversation { Id = "b", Title = "misc" });
        memory.SaveConversationTurn(new ConversationTurn("t1", "b", 1, "user", "the Kestrel port CHANGED"));

        // Title match and content match, each found; case folded by SQLite LIKE.
        Assert.Equal("a", Assert.Single(memory.SearchConversations("DEPLOY")).Id);
        Assert.Equal("b", Assert.Single(memory.SearchConversations("kestrel")).Id);

        // A literal % is a character to find, not "match everything": zero hits, not two.
        Assert.Empty(memory.SearchConversations("100%"));

        // Blank query is the plain list, not an error and not an empty result.
        Assert.Equal(2, memory.SearchConversations("  ").Count);
    }

    /// <summary>One conversation with five matching turns is one result, not five.</summary>
    [Fact]
    public void Search_ReturnsEachConversationOnce()
    {
        using var memory = Memory();
        memory.SaveConversation(new Conversation { Id = "a", Title = "misc" });
        for (var i = 1; i <= 5; i++)
            memory.SaveConversationTurn(new ConversationTurn($"t{i}", "a", i, "user", "ants everywhere"));

        Assert.Single(memory.SearchConversations("ants"));
    }
}
