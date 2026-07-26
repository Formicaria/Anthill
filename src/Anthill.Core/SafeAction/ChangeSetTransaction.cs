namespace Anthill.Core.SafeAction;

/// <summary>
/// NORTH_STAR Phase 6 — multi-step change sets with checkpoints. Each step declares its
/// compensation; verification runs after every checkpoint; a failed step stops the run and
/// compensates completed steps in REVERSE order. Partial retention is opt-in per change set —
/// by default a partially applied change set is fully unwound.
/// </summary>
public sealed record ChangeStep(
    string Id,
    Func<bool> Execute,
    Func<bool> Verify,
    Func<bool>? Compensate = null,
    bool Checkpoint = true);

public sealed record ChangeSetResult(
    bool Success,
    string StopReason,
    IReadOnlyList<string> Completed,
    IReadOnlyList<string> Compensated,
    IReadOnlyList<string> CompensationFailures,
    bool AutonomySuspended);

public static class ChangeSetTransaction
{
    public static ChangeSetResult Run(IReadOnlyList<ChangeStep> steps, bool allowPartialRetention = false)
    {
        var completed = new List<string>();
        var compensated = new List<string>();
        var compFailures = new List<string>();
        string stop = "completed";
        var failed = false;

        foreach (var step in steps)
        {
            bool ok;
            try { ok = step.Execute(); }
            catch (Exception e) { ok = false; stop = $"step '{step.Id}' threw: {e.Message}"; }

            if (!ok)
            {
                if (stop == "completed") stop = $"step '{step.Id}' failed";
                failed = true;
                break;
            }

            if (step.Checkpoint)
            {
                bool verified;
                try { verified = step.Verify(); }
                catch (Exception e) { verified = false; stop = $"checkpoint '{step.Id}' verification threw: {e.Message}"; }
                if (!verified)
                {
                    if (stop == "completed") stop = $"checkpoint '{step.Id}' verification failed";
                    completed.Add(step.Id); // it DID execute — it must be compensated
                    failed = true;
                    break;
                }
            }
            completed.Add(step.Id);
        }

        if (!failed)
            return new(true, "completed", completed, compensated, compFailures, false);

        if (allowPartialRetention)
            return new(false, stop + " (partial retention allowed — completed steps kept)",
                completed, compensated, compFailures, false);

        // Compensate in reverse order; a compensation failure suspends autonomy.
        for (var i = completed.Count - 1; i >= 0; i--)
        {
            var id = completed[i];
            var step = steps.First(s => s.Id == id);
            if (step.Compensate is null) { compFailures.Add($"{id}: no compensation defined"); continue; }
            try
            {
                if (step.Compensate()) compensated.Add(id);
                else compFailures.Add($"{id}: compensation returned false");
            }
            catch (Exception e) { compFailures.Add($"{id}: compensation threw: {e.Message}"); }
        }

        var suspend = compFailures.Count > 0;
        if (suspend) stop += " | ROLLBACK FAILURE — autonomy suspended";
        return new(false, stop, completed, compensated, compFailures, suspend);
    }
}
