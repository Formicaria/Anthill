namespace Anthill.Core.Conversations;

/// <summary>
/// v3.7.0 — the conversation whose escalation policy governs the current flow, and where its
/// decisions get written.
///
/// The gate shipped one increment ago was correct and NOT LOAD-BEARING: nothing in the dispatch path
/// called it, so it was a policy engine with no enforcement behind it. This is the wiring, and it
/// goes through <see cref="Tools.ToolRegistry.RunTool"/> — the single chokepoint every tool call
/// already passes, where authorization is already enforced. A second enforcement point would be a
/// second thing to keep in step, and the two would eventually disagree.
///
/// Ambient for the same reason <c>MissionWorkspaceScope</c> is: the conversation is a property of
/// the FLOW, the tool registry is a shared singleton, and threading it through every ant and every
/// dispatch would be a large refactor of code with no other reason to change.
///
/// OUTSIDE a scope there is no conversation and nothing changes — missions run exactly as they did.
/// This narrows what a conversation may do; it never widens anything.
/// </summary>
public static class ConversationScope
{
    /// <summary>
    /// What the current flow is doing, and the answers it has been given.
    ///
    /// <paramref name="Answers"/> maps an action to the operator's reply for it. Under
    /// <see cref="EscalationPolicy.Ask"/> an action absent from this map has NOT been approved —
    /// absence is not consent, and a caller that forgot to ask gets a refusal.
    ///
    /// <paramref name="Record"/> is how the decision reaches storage. Supplied rather than resolved
    /// so the gate stays testable without a database, and so the caller that owns the conversation
    /// also owns writing its history.
    /// </summary>
    public sealed record Context(
        Conversation Conversation,
        IReadOnlyDictionary<string, string> Answers,
        Action<EscalationDecision>? Record = null);

    private static readonly AsyncLocal<Context?> Ambient = new();

    public static Context? Current => Ambient.Value;

    /// <summary>
    /// Decide whether <paramref name="action"/> may proceed in the current flow, and RECORD it.
    ///
    /// Returns null when there is no conversation in scope — which callers read as "not governed
    /// here", not as "allowed". The distinction matters: a mission running outside any conversation
    /// is governed by its own capability grants, and treating an absent conversation as a grant
    /// would be inventing permission out of nothing.
    /// </summary>
    public static EscalationDecision? Evaluate(string action)
    {
        var context = Ambient.Value;
        if (context is null) return null;

        var answer = context.Answers.GetValueOrDefault(action ?? "");
        var decision = EscalationGate.Evaluate(context.Conversation, action ?? "", answer);

        // Recorded whatever the outcome. A refusal that leaves no trace is indistinguishable from
        // an attempt that never happened, and the refused attempts are the ones an audit most needs.
        // Best-effort: a storage failure must not turn a correct refusal into an exception, nor a
        // correct approval into a failed tool call.
        try { context.Record?.Invoke(decision); } catch { }

        return decision;
    }

    /// <summary>Enter a scope. Disposing restores the previous one, so scopes nest safely.</summary>
    public static IDisposable Enter(Context? context)
    {
        var previous = Ambient.Value;
        Ambient.Value = context;
        return new Scope(previous);
    }

    /// <summary>Convenience for the common shape: a conversation, its answers, and a memory to write to.</summary>
    public static IDisposable Enter(Conversation conversation,
        IReadOnlyDictionary<string, string>? answers = null,
        Action<EscalationDecision>? record = null) =>
        Enter(new Context(conversation,
            answers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), record));

    private sealed class Scope : IDisposable
    {
        private readonly Context? _previous;
        private bool _disposed;
        public Scope(Context? previous) => _previous = previous;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = _previous;
        }
    }
}
