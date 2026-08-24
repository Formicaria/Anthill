namespace Anthill.Core.Orchestration;

/// <summary>
/// What actually happened when a patch application was attempted. v0.3.8.91.
///
/// WHY THIS TYPE EXISTS. `ApproveAndApplyPatch` decided whether a patch had landed by reading the
/// English sentence the apply helper returned:
///
///     applied = result.Contains("applied") &amp;&amp; !result.Contains("not applied")
///
/// Three of that helper's REFUSAL sentences satisfy it. "Patch cannot be applied because status is
/// rejected" contains "applied" and does not contain "not applied", so a patch an operator had
/// explicitly REJECTED was reported as applied, returned HTTP success, and triggered a real
/// `git commit` — for a file that was never written. "Patch is already applied" did the same and
/// re-committed. The comment above the check claimed "every success sentence contains 'applied' and
/// no refusal does"; the counterexamples were in the same file, eighty lines up.
///
/// `architecture.md` already states the rule this violated — *"it never reconstructs failure state
/// from prose"* — and the violation sat on the highest-consequence action in the system.
///
/// The outcome is what callers branch on. The message is for humans and is never parsed.
/// </summary>
public enum PatchApplyOutcome
{
    /// <summary>Bytes reached the operator's tree and the records were updated.</summary>
    Applied,
    /// <summary>The approval id, patch id, or approval type did not resolve.</summary>
    RefusedUnknown,
    /// <summary>The approval exists and is not in the approved state.</summary>
    RefusedNotApproved,
    /// <summary>The patch's own status forbids application — rejected, failed, or already applied.</summary>
    RefusedStatus,
    /// <summary>The write was attempted and the tool refused or failed. Nothing landed.</summary>
    Failed,
}

/// <param name="Message">Operator-facing text. NEVER the thing a caller branches on.</param>
public sealed record PatchApplyResult(PatchApplyOutcome Outcome, string Message)
{
    public bool Applied => Outcome == PatchApplyOutcome.Applied;
}
