namespace Anthill.Core.Conversations;

/// <summary>
/// v3.7.0 — how much the operator wants to be asked before the colony does something with effects.
///
/// Three modes rather than a boolean, because the two extremes are both legitimate and the middle is
/// where most work actually happens. An operator watching a risky change wants every write
/// confirmed; the same operator running a known-good refactor across forty files wants to say yes
/// once; and an operator on a scratch branch wants to be left alone entirely.
///
/// THE EXIT GATE IS ABOUT ACCOUNTABILITY, NOT PROMPTING. It says no conversation may begin
/// side-effecting work "without a RECORDED OPERATOR DECISION" — and choosing
/// <see cref="AutoApprove"/> or <see cref="Bypass"/> IS that decision, recorded once with who made
/// it and when, rather than asked each time. What the gate forbids is work proceeding under an
/// unrecorded default, which is exactly what a hard-coded policy would be.
/// </summary>
public enum EscalationPolicy
{
    /// <summary>
    /// Ask before each side-effecting action. The default, and deliberately so: a colony that
    /// escalates without asking on its first run is one an operator has not yet decided to trust.
    /// </summary>
    Ask = 0,

    /// <summary>
    /// Approve side-effecting actions automatically, and RECORD each one against the standing
    /// decision. The operator is not interrupted; the audit is unchanged.
    /// </summary>
    AutoApprove,

    /// <summary>
    /// Do not gate at all. Still recorded — an action taken under bypass carries the id of the
    /// standing decision that permitted it, so "why was this allowed" has an answer that is not
    /// "nobody knows".
    /// </summary>
    Bypass,
}

/// <summary>What was decided about one side-effecting action, and on whose authority.</summary>
public sealed record EscalationDecision(
    string Id,
    string ConversationId,
    string Action,
    bool Allowed,
    EscalationPolicy Policy,
    string DecidedBy,
    DateTime DecidedAt,
    string? Reason = null)
{
    /// <summary>
    /// Whether a human considered THIS action specifically, as opposed to a standing policy
    /// covering it. Both are recorded decisions; only one is a fresh judgement, and an audit
    /// reading a long run needs to tell them apart.
    /// </summary>
    public bool WasAskedDirectly => Policy == EscalationPolicy.Ask;
}

/// <summary>One turn. The transcript is the artifact; the route is how it was produced.</summary>
public sealed record ConversationTurn(
    string Id,
    string ConversationId,
    int Ordinal,
    string Role,
    string Content)
{
    /// <summary>Provider and model that produced this turn. Null for user and system turns.</summary>
    public string? Provider { get; init; }
    public string? Model { get; init; }

    /// <summary>Tools OFFERED for this turn, so a transcript explains what the model could have done.</summary>
    public IReadOnlyList<string> ToolsOffered { get; init; } = Array.Empty<string>();

    /// <summary>Tools it actually called.</summary>
    public IReadOnlyList<string> ToolsCalled { get; init; } = Array.Empty<string>();

    /// <summary>Set when this turn escalated the conversation into mission work.</summary>
    public string? MissionId { get; init; }

    public DateTime CreatedAt { get; init; } = Common.AnthillTime.NowUtc();
}

/// <summary>
/// v3.7.0 — a conversation as a first-class runtime object rather than an in-memory session.
///
/// Persisted for reasons the phase names directly: a conversation must survive a restart with its
/// transcript and route intact, and the transcript of an escalated run has to show the conversation
/// and the mission as ONE history. Neither is possible while a conversation exists only in a
/// process's memory — a restart mid-mission would leave the mission with an audit trail whose first
/// half is gone, which is the half explaining why the work was started at all.
///
/// The route is recorded PER TURN rather than per conversation, because it can change: a
/// tool-capable model may be substituted mid-conversation by capability-aware routing, and a
/// transcript that reports only the configured route would describe a conversation that did not
/// happen.
/// </summary>
public sealed record Conversation
{
    public required string Id { get; init; }

    /// <summary>What the operator called it. Not identity — two conversations may share a title.</summary>
    public string Title { get; init; } = "";

    /// <summary>The role whose tools and contract this conversation runs under.</summary>
    public string Role { get; init; } = "researcher";

    public EscalationPolicy Policy { get; init; } = EscalationPolicy.Ask;

    /// <summary>
    /// Who set <see cref="Policy"/> and when. Required for anything other than <see cref="EscalationPolicy.Ask"/>:
    /// a standing permission with no author is indistinguishable from a default nobody chose, and
    /// the whole point of the standing decision is that somebody made it.
    /// </summary>
    public string? PolicySetBy { get; init; }
    public DateTime? PolicySetAt { get; init; }

    /// <summary>Missions this conversation started. One history, across the escalation boundary.</summary>
    public IReadOnlyList<string> MissionIds { get; init; } = Array.Empty<string>();

    public bool Cancelled { get; init; }
    public DateTime CreatedAt { get; init; } = Common.AnthillTime.NowUtc();
    public DateTime UpdatedAt { get; init; } = Common.AnthillTime.NowUtc();

    /// <summary>
    /// Whether the policy is validly recorded. A conversation claiming AutoApprove or Bypass with
    /// no author fails this — and fails CLOSED, being treated as <see cref="EscalationPolicy.Ask"/>
    /// by <see cref="EffectivePolicy"/> rather than granting a permission nobody can be shown to
    /// have given.
    /// </summary>
    public bool PolicyIsAttributed =>
        Policy == EscalationPolicy.Ask
        || (!string.IsNullOrWhiteSpace(PolicySetBy) && PolicySetAt is not null);

    /// <summary>
    /// The policy that actually applies. Falls back to <see cref="EscalationPolicy.Ask"/> for an
    /// unattributed standing permission and for a cancelled conversation — in both cases the safe
    /// answer is the one that stops and asks.
    /// </summary>
    public EscalationPolicy EffectivePolicy =>
        Cancelled || !PolicyIsAttributed ? EscalationPolicy.Ask : Policy;
}

/// <summary>
/// v3.7.0 — the escalation boundary: does this action need a decision, and has one been made?
///
/// Deliberately a pure function over a conversation and an action name. The gate is a POLICY
/// question, and keeping it separate from the machinery that executes actions means the rule can be
/// tested exhaustively without standing up a tool registry, a model or a mission.
/// </summary>
public static class EscalationGate
{
    /// <summary>
    /// Actions that change something outside the process. Derived from the tool names the colony
    /// already treats as side-effecting, rather than a second opinion about which those are.
    /// </summary>
    public static readonly IReadOnlySet<string> SideEffecting =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "apply_patch", "write_text_file", "shell_command", "run_allowlisted_check",
            // v3.7.0: turning a conversation into a MISSION is itself a side effect, and the most
            // consequential one here — it is the moment bounded conversational work becomes
            // autonomous multi-task execution. Listed here rather than special-cased in the runner,
            // because a boundary enforced in two places eventually disagrees with itself.
            //
            // It is NOT a tool and never will be: no model may call it, and nothing registers it in
            // the tool registry. It appears in this set purely so the one gate covers it.
            ConversationRunner.StartMissionAction,
        };

    /// <summary>Whether <paramref name="action"/> needs an operator decision at all.</summary>
    public static bool NeedsDecision(string? action) =>
        !string.IsNullOrWhiteSpace(action) && SideEffecting.Contains(action!);

    /// <summary>
    /// Decide, and produce the RECORD of that decision — never a bare bool.
    ///
    /// Returning a record rather than a boolean is the design: the exit gate is satisfied by what
    /// gets written down, so a caller that could obtain permission without also obtaining something
    /// to persist would be able to satisfy the check and leave no trace. Here the permission IS the
    /// record.
    /// </summary>
    public static EscalationDecision Evaluate(Conversation conversation, string action, string? operatorAnswer = null)
    {
        var policy = conversation.EffectivePolicy;
        var now = Common.AnthillTime.NowUtc();

        // Not side-effecting: allowed, and recorded as such. Reading and searching are the bulk of a
        // conversation, and a decision record for each would bury the ones that matter — so this
        // returns an allowed decision without the caller needing to special-case it.
        if (!NeedsDecision(action))
            return new EscalationDecision(Guid.NewGuid().ToString("N")[..12], conversation.Id, action,
                Allowed: true, policy, DecidedBy: "system", now, "not a side-effecting action");

        return policy switch
        {
            EscalationPolicy.Bypass => new EscalationDecision(
                Guid.NewGuid().ToString("N")[..12], conversation.Id, action, true, policy,
                conversation.PolicySetBy ?? "operator", now,
                $"standing decision: approvals bypassed for this conversation (set {conversation.PolicySetAt:u})"),

            EscalationPolicy.AutoApprove => new EscalationDecision(
                Guid.NewGuid().ToString("N")[..12], conversation.Id, action, true, policy,
                conversation.PolicySetBy ?? "operator", now,
                $"standing decision: side effects auto-approved (set {conversation.PolicySetAt:u})"),

            // Ask: only an explicit yes allows it. Absence of an answer is NOT consent — a caller
            // that forgot to ask gets a refusal, which is the failure mode worth having.
            _ => new EscalationDecision(
                Guid.NewGuid().ToString("N")[..12], conversation.Id, action,
                Allowed: string.Equals(operatorAnswer, "approve", StringComparison.OrdinalIgnoreCase),
                policy, DecidedBy: string.IsNullOrWhiteSpace(operatorAnswer) ? "nobody" : "operator", now,
                string.IsNullOrWhiteSpace(operatorAnswer)
                    ? "no operator decision was recorded for this action"
                    : $"operator answered '{operatorAnswer}'"),
        };
    }
}
