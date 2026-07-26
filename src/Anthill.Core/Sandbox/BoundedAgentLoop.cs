using Anthill.Core.Contracts;

namespace Anthill.Core.Sandbox;

/// <summary>
/// V2.10.0 — the bounded iteration engine: Observe → choose → execute → inspect → replan →
/// continue or stop. EVERY loop is constrained by hard budgets (turns, tool calls, elapsed time,
/// repeated-action detection, cancellation) enforced HERE, not by the agent's judgment. The step
/// function returns a structured step result; the loop returns WHY it stopped — a loop can never
/// end without an explicable stop reason, and it can never run unbounded.
/// </summary>
public sealed record LoopBudget(
    int MaxTurns = 8,
    int MaxToolCalls = 24,
    int MaxSeconds = 600,
    int MaxRepeatedActions = 2);

public sealed record LoopStep(
    bool Done,                    // agent believes the goal is reached
    string ActionKey,             // stable identity of what this turn did (for repeat detection)
    int ToolCallsUsed,
    string Note = "");

public sealed record LoopOutcome(
    string StopReason,            // completed | max_turns | max_tool_calls | timeout | repeated_action | cancelled | step_fault
    int Turns,
    int ToolCalls,
    bool Completed,
    string Detail = "");

public static class BoundedAgentLoop
{
    public static LoopOutcome Run(LoopBudget budget, Func<int, LoopStep> step,
        CancellationToken ct = default, Func<DateTime>? now = null)
    {
        var clock = now ?? (() => DateTime.UtcNow);
        var started = clock();
        var toolCalls = 0;
        string? lastAction = null;
        var repeats = 0;

        for (var turn = 1; turn <= budget.MaxTurns; turn++)
        {
            if (ct.IsCancellationRequested)
                return new("cancelled", turn - 1, toolCalls, false, "cancellation requested before turn");
            if ((clock() - started).TotalSeconds > budget.MaxSeconds)
                return new("timeout", turn - 1, toolCalls, false, $"elapsed budget {budget.MaxSeconds}s exhausted");

            LoopStep result;
            try { result = step(turn); }
            catch (OperationCanceledException) { return new("cancelled", turn, toolCalls, false, "cancelled mid-step"); }
            catch (Exception e)
            {
                return new("step_fault", turn, toolCalls, false,
                    $"{FailureClass.InternalDefect}: {e.Message}");
            }

            toolCalls += Math.Max(0, result.ToolCallsUsed);
            if (toolCalls > budget.MaxToolCalls)
                return new("max_tool_calls", turn, toolCalls, false, $"tool budget {budget.MaxToolCalls} exhausted");

            // Repeated-action detection: an agent doing the same thing again and again is looping,
            // not progressing — stop it before it burns the remaining budgets.
            if (result.ActionKey.Length > 0 && result.ActionKey == lastAction)
            {
                if (++repeats >= budget.MaxRepeatedActions)
                    return new("repeated_action", turn, toolCalls, false,
                        $"action '{result.ActionKey}' repeated {repeats + 1}x");
            }
            else repeats = 0;
            lastAction = result.ActionKey;

            if (result.Done)
                return new("completed", turn, toolCalls, true, result.Note);
        }
        return new("max_turns", budget.MaxTurns, toolCalls, false, $"turn budget {budget.MaxTurns} exhausted");
    }
}
