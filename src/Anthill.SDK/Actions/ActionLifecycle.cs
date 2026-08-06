namespace Anthill.SDK.Actions;

/// <summary>
/// NORTH_STAR Phase 6 — the ONE lifecycle every state-changing system shares (code patches and
/// homelab actions alike). Transitions are enforced structurally: approval cannot be skipped,
/// nothing executes from draft, and a terminal state is terminal. Illegal transitions are refused
/// with a reason rather than silently coerced.
/// </summary>
public enum ActionState
{
    Draft, Validated, RiskScored, WaitingForApproval, Approved, Scheduled,
    Executing, Verifying, CompletedVerified,
    Failed, Compensating, Compensated, RollbackFailed, Escalated,
}

public static class ActionLifecycle
{
    private static readonly Dictionary<ActionState, ActionState[]> Legal = new()
    {
        [ActionState.Draft] = new[] { ActionState.Validated, ActionState.Failed },
        [ActionState.Validated] = new[] { ActionState.RiskScored, ActionState.Failed },
        [ActionState.RiskScored] = new[] { ActionState.WaitingForApproval, ActionState.Failed },
        // No path from RiskScored straight to Approved/Executing — approval is structural.
        [ActionState.WaitingForApproval] = new[] { ActionState.Approved, ActionState.Failed, ActionState.Escalated },
        [ActionState.Approved] = new[] { ActionState.Scheduled, ActionState.Executing, ActionState.Failed },
        [ActionState.Scheduled] = new[] { ActionState.Executing, ActionState.Failed },
        [ActionState.Executing] = new[] { ActionState.Verifying, ActionState.Failed, ActionState.Compensating },
        // Execution alone can never declare success — verification is the only door to completion.
        [ActionState.Verifying] = new[] { ActionState.CompletedVerified, ActionState.Failed, ActionState.Compensating },
        [ActionState.Failed] = new[] { ActionState.Compensating, ActionState.Escalated },
        [ActionState.Compensating] = new[] { ActionState.Compensated, ActionState.RollbackFailed },
        [ActionState.RollbackFailed] = new[] { ActionState.Escalated },
        [ActionState.CompletedVerified] = Array.Empty<ActionState>(),
        [ActionState.Compensated] = Array.Empty<ActionState>(),
        [ActionState.Escalated] = Array.Empty<ActionState>(),
    };

    public static bool IsTerminal(ActionState s) => Legal[s].Length == 0;

    public static bool CanTransition(ActionState from, ActionState to) => Legal[from].Contains(to);

    public sealed record TransitionResult(bool Ok, ActionState State, string Reason);

    public static TransitionResult Transition(ActionState from, ActionState to) =>
        CanTransition(from, to)
            ? new(true, to, "")
            : new(false, from, IsTerminal(from)
                ? $"'{from}' is terminal — no further transitions"
                : $"illegal transition {from} -> {to} (allowed: {string.Join(", ", Legal[from])})");
}
