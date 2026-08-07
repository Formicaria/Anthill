using Anthill.Core.Conversations;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Tools;
using Xunit;

using ThreadingTask = System.Threading.Tasks.Task;

namespace Anthill.Tests;

/// <summary>
/// v3.7.0 — the escalation gate, made LOAD-BEARING.
///
/// The gate shipped one increment earlier was correct and enforced nothing: no dispatch path called
/// it, so it was a policy engine with no teeth. These tests are about the wiring, and specifically
/// about the two properties that make it trustworthy rather than decorative:
///
///   - it sits at the SAME chokepoint as authorization, so there is exactly one place a tool call
///     can be stopped, and a second enforcement point cannot drift out of step with it
///   - OUTSIDE a conversation nothing changes, so missions run as they did and the mechanism can
///     only narrow, never widen
/// </summary>
public class ConversationScopeTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _memory;
    private readonly ToolRegistry _registry;

    public ConversationScopeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-cscope-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "memory.db"));
        _registry = new ToolRegistry(_memory);
        _registry.Register(new SpyTool("apply_patch"));      // side-effecting
        _registry.Register(new SpyTool("system_info"));      // read-only
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Records whether it actually ran — the only way to prove a refusal had zero side effects.</summary>
    private sealed class SpyTool : ITool
    {
        public SpyTool(string name) => Name = name;
        public string Name { get; }
        public string Description => "spy";
        public int Calls { get; private set; }
        public ToolResult Run(IReadOnlyDictionary<string, object?> args)
        {
            Calls++;
            return new ToolResult(Name, true, "ran");
        }
    }

    private SpyTool Spy(string name) => (SpyTool)_registry.Tools.Single(t => t.Name == name);

    private static Conversation Chat(EscalationPolicy policy = EscalationPolicy.Ask) => new()
    {
        Id = "c1", Role = "queen", Policy = policy,
        PolicySetBy = policy == EscalationPolicy.Ask ? null : "zwright",
        PolicySetAt = policy == EscalationPolicy.Ask ? null : DateTime.UtcNow,
    };

    // ---- the gate now actually stops things ------------------------------------------------------

    /// <summary>
    /// The whole point. Under Ask with no answer recorded, the tool is refused AND NEVER RUNS — a
    /// gate that lets the side effect happen and reports a refusal afterwards is not a gate.
    /// </summary>
    [Fact]
    public void UnderAsk_AnUnapprovedSideEffect_IsRefusedAndNeverRuns()
    {
        using (ConversationScope.Enter(Chat()))
        {
            var result = _registry.RunTool("apply_patch", antName: "queen");

            Assert.False(result.Success);
            Assert.Equal(Anthill.SDK.Contracts.FailureClass.AuthorizationFailure, result.Failure);
            Assert.Contains("escalation_refused", result.Error);
        }

        Assert.Equal(0, Spy("apply_patch").Calls);
    }

    [Fact]
    public void UnderAsk_AnApprovedSideEffect_Runs()
    {
        var answers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["apply_patch"] = "approve",
        };

        using (ConversationScope.Enter(Chat(), answers))
            Assert.True(_registry.RunTool("apply_patch", antName: "queen").Success);

        Assert.Equal(1, Spy("apply_patch").Calls);
    }

    /// <summary>
    /// Approving one action does not approve another. An answer is per-action, so an operator who
    /// said yes to a patch has not thereby said yes to a shell command.
    /// </summary>
    [Fact]
    public void AnAnswerForOneAction_DoesNotCoverAnother()
    {
        var answers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["write_text_file"] = "approve",
        };

        using (ConversationScope.Enter(Chat(), answers))
            Assert.False(_registry.RunTool("apply_patch", antName: "queen").Success);
    }

    /// <summary>Read-only work is never gated — it is the bulk of a conversation and changes nothing.</summary>
    [Fact]
    public void ReadOnlyToolsAreNeverGated()
    {
        using (ConversationScope.Enter(Chat()))
            Assert.True(_registry.RunTool("system_info", antName: "queen").Success);

        Assert.Equal(1, Spy("system_info").Calls);
    }

    [Theory]
    [InlineData(EscalationPolicy.AutoApprove)]
    [InlineData(EscalationPolicy.Bypass)]
    public void UnderAStandingPermission_SideEffectsProceed(EscalationPolicy policy)
    {
        using (ConversationScope.Enter(Chat(policy)))
            Assert.True(_registry.RunTool("apply_patch", antName: "queen").Success);

        Assert.Equal(1, Spy("apply_patch").Calls);
    }

    // ---- it only ever narrows ----------------------------------------------------------------------

    /// <summary>
    /// Outside a conversation the gate is absent, not permissive: a mission is governed by its own
    /// capability grants, and this mechanism adds nothing to them. Without this, every existing
    /// mission path would have changed behaviour.
    /// </summary>
    [Fact]
    public void OutsideAConversation_NothingIsGated()
    {
        Assert.Null(ConversationScope.Current);
        Assert.True(_registry.RunTool("apply_patch", antName: "queen").Success);
    }

    /// <summary>
    /// Authorization still runs FIRST. A role that may never use a tool is refused on that basis,
    /// without troubling the operator about something that could not have happened anyway.
    /// </summary>
    [Fact]
    public void AuthorizationStillRunsFirst()
    {
        using (ConversationScope.Enter(Chat(EscalationPolicy.Bypass)))
        {
            // researcher may not use apply_patch, and bypass must not change that
            var result = _registry.RunTool("apply_patch", antName: "researcher");

            Assert.False(result.Success);
            Assert.Contains("authorization_denied", result.Error);
            Assert.DoesNotContain("escalation", result.Error);
        }

        Assert.Equal(0, Spy("apply_patch").Calls);
    }

    [Fact]
    public void LeavingAScope_RestoresThePrevious()
    {
        using (ConversationScope.Enter(Chat(EscalationPolicy.Bypass)))
            Assert.NotNull(ConversationScope.Current);

        Assert.Null(ConversationScope.Current);
    }

    /// <summary>
    /// The scope flows across async continuations. A gate that stops applying the moment something
    /// awaits is not a gate — and the agent loop awaits constantly.
    /// </summary>
    [Fact]
    public async ThreadingTask TheScopeFlowsAcrossAsyncContinuations()
    {
        using (ConversationScope.Enter(Chat()))
        {
            var allowed = await ThreadingTask.Run(async () =>
            {
                await ThreadingTask.Yield();
                return _registry.RunTool("apply_patch", antName: "queen").Success;
            });

            Assert.False(allowed);
        }
    }

    // ---- the decision is written down ----------------------------------------------------------------

    /// <summary>
    /// Every decision reaches storage, refusals included. A refusal that leaves no trace is
    /// indistinguishable from an attempt that never happened — and the refused attempts are the ones
    /// an audit most needs, because nobody saw them.
    /// </summary>
    [Fact]
    public void EveryDecisionIsRecorded_IncludingRefusals()
    {
        _memory.SaveConversation(Chat());

        using (ConversationScope.Enter(Chat(), null, _memory.SaveEscalationDecision))
        {
            _registry.RunTool("apply_patch", antName: "queen");     // refused
            _registry.RunTool("system_info", antName: "queen");     // allowed, not side-effecting
        }

        var decisions = _memory.LoadEscalationDecisions("c1");

        Assert.Contains(decisions, d => d.Action == "apply_patch" && !d.Allowed);
        Assert.Contains(decisions, d => d.Action == "system_info" && d.Allowed);
    }

    /// <summary>
    /// A standing permission records WHO gave it, so an audit reading a long autonomous run can
    /// answer "why was this allowed" with a name and a time rather than a shrug.
    /// </summary>
    [Fact]
    public void AStandingPermission_IsAttributedOnEveryActionItCovers()
    {
        _memory.SaveConversation(Chat(EscalationPolicy.Bypass));

        using (ConversationScope.Enter(Chat(EscalationPolicy.Bypass), null, _memory.SaveEscalationDecision))
            _registry.RunTool("apply_patch", antName: "queen");

        var decision = _memory.LoadEscalationDecisions("c1").Single(d => d.Action == "apply_patch");

        Assert.True(decision.Allowed);
        Assert.Equal("zwright", decision.DecidedBy);
        Assert.False(decision.WasAskedDirectly);
    }

    /// <summary>
    /// A failure to WRITE the decision must not change the decision. Storage is where the record
    /// goes, not where the answer comes from — otherwise a full disk would silently turn refusals
    /// into approvals or vice versa.
    /// </summary>
    [Fact]
    public void ARecordingFailure_DoesNotChangeTheOutcome()
    {
        using (ConversationScope.Enter(Chat(), null, _ => throw new IOException("disk is full")))
        {
            Assert.False(_registry.RunTool("apply_patch", antName: "queen").Success);
            Assert.True(_registry.RunTool("system_info", antName: "queen").Success);
        }
    }
}
