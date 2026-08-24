using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Security;
using Anthill.SDK.Common;
using Anthill.SDK.Events;

namespace Anthill.Core.Verification;

/// <summary>
/// FINISH OR DISCARD EVERY APPLY THAT A CRASH INTERRUPTED. v0.3.8.91.
///
/// THE STATE THIS RESOLVES. `ApplyApprovedPatch` wrote to disk and then made four separate,
/// un-transacted database updates. A process death between them left the file changed and the patch
/// still `approved`. The Patch Center offered Apply again, the recompute found the file no longer
/// matching its base hash, the patch was marked FAILED — and `RevertAppliedPatch` then refused,
/// because only an APPLIED patch can be reverted. A change that really landed, recorded as never
/// having happened, and unrevertable. `ApplyTransaction.Recover` could not help: it replays the
/// FILESYSTEM journal, which the manual lane never wrote, and it knows nothing about database rows.
///
/// HOW IT DECIDES, and it decides from bytes rather than from belief. Each intent carries the
/// target's hash before the write and, once the tool reported success, the hash it left. At startup:
///
///   - Prepared — nothing was touched. Discard the intent; the patch is still proposed and the
///     operator can apply it normally.
///   - Mutating — the ambiguous one, and the reason the hashes exist. If the file still hashes to
///     `PreHash`, the write never landed: discard. Otherwise bytes moved during an apply nobody
///     finished, and this process will not guess whose they are — the intent is reported and left
///     for an operator, because silently "completing" an apply whose result was never verified is
///     the failure this whole release is about.
///   - Applied — the write landed and the database never caught up. FINISH IT: patch to Applied,
///     approval Consumed, the event that should have been written. This is the case that used to
///     become an unrevertable phantom.
///
/// WHAT IT WILL NOT DO. It never re-runs an apply, never rolls one back, and never decides an
/// ambiguous case in favour of "it worked". Reconciliation makes the record match what the disk
/// already says; it is not a second applier.
/// </summary>
public static class PatchApplyReconciler
{
    public sealed record Outcome(int Completed, int Discarded, int NeedsOperator, IReadOnlyList<string> Notes);

    /// <summary>
    /// Sweep the intent journal. Called once at startup, before anything else may apply a patch.
    ///
    /// Never throws: a reconciliation that fails must not stop the process from starting, because a
    /// colony that will not boot cannot be used to fix anything. Every failure becomes a note.
    /// </summary>
    public static Outcome Reconcile(SqliteMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);

        int completed = 0, discarded = 0, needsOperator = 0;
        var notes = new List<string>();

        IReadOnlyList<PatchApplyIntent> open;
        try { open = memory.OpenApplyIntents(); }
        catch (Exception error)
        {
            return new Outcome(0, 0, 0, new[] { $"could not read the apply intent journal: {error.Message}" });
        }

        foreach (var intent in open)
        {
            try
            {
                switch (intent.Phase)
                {
                    case PatchApplyPhase.Prepared:
                        memory.CloseApplyIntent(intent.Id);
                        discarded++;
                        notes.Add($"patch {intent.PatchId}: interrupted before any write — nothing to undo");
                        break;

                    case PatchApplyPhase.Applied:
                        Complete(memory, intent);
                        completed++;
                        notes.Add($"patch {intent.PatchId}: the write had landed and the records had not — "
                                + "completed them");
                        break;

                    case PatchApplyPhase.Mutating:
                    {
                        var current = CurrentHash(intent.TargetPath);

                        if (intent.PreHash is not null && string.Equals(current, intent.PreHash, StringComparison.Ordinal))
                        {
                            memory.CloseApplyIntent(intent.Id);
                            discarded++;
                            notes.Add($"patch {intent.PatchId}: the file still holds its pre-apply bytes — "
                                    + "the write never landed");
                            break;
                        }

                        if (intent.PostHash is not null && string.Equals(current, intent.PostHash, StringComparison.Ordinal))
                        {
                            Complete(memory, intent);
                            completed++;
                            notes.Add($"patch {intent.PatchId}: the file holds exactly what the apply wrote — "
                                    + "completed the records");
                            break;
                        }

                        needsOperator++;
                        notes.Add($"patch {intent.PatchId} ({intent.TargetPath}): interrupted mid-write and the "
                                + "file matches neither its pre-apply nor its post-apply hash. Left for an "
                                + "operator — this process will not decide whose bytes those are.");
                        memory.LogEvent(intent.MissionId, EventTypes.PatchApplyUnreconciled,
                            $"An interrupted apply of patch {intent.PatchId} left {intent.TargetPath} in a state "
                          + "matching neither hash. It has NOT been completed or undone.",
                            antName: "queen",
                            metadata: new()
                            {
                                ["patch_id"] = intent.PatchId, ["intent_id"] = intent.Id,
                                ["target_path"] = intent.TargetPath, ["severity"] = "critical",
                            });
                        break;
                    }

                    case PatchApplyPhase.Recorded:
                        memory.CloseApplyIntent(intent.Id);
                        discarded++;
                        break;
                }
            }
            catch (Exception error)
            {
                needsOperator++;
                notes.Add($"patch {intent.PatchId}: reconciliation failed — {error.Message}");
            }
        }

        return new Outcome(completed, discarded, needsOperator, notes);
    }

    /// <summary>
    /// Write the database effects the crash interrupted, then close the intent.
    ///
    /// The order is the same one the live path uses and matters for the same reason: the intent is
    /// closed LAST, so a crash during reconciliation leaves the row and the next start tries again.
    /// Every step is idempotent — a status set twice is the same status.
    /// </summary>
    private static void Complete(SqliteMemory memory, PatchApplyIntent intent)
    {
        memory.UpdatePatchStatus(intent.PatchId, PatchStatus.Applied,
            AnthillTime.NowUtc().ToIso(), null, null);

        if (!string.IsNullOrWhiteSpace(intent.ApprovalId))
            memory.UpdateApprovalStatus(intent.ApprovalId!, ApprovalStatus.Consumed,
                "Approval consumed by an apply that startup reconciliation completed.");

        memory.LogEvent(intent.MissionId, EventTypes.PatchApplyReconciled,
            $"Patch {intent.PatchId} was written to disk by an apply that did not finish recording it. "
          + "Startup reconciliation completed the records to match the tree.",
            antName: "queen",
            metadata: new()
            {
                ["patch_id"] = intent.PatchId, ["intent_id"] = intent.Id,
                ["approval_request_id"] = intent.ApprovalId, ["target_path"] = intent.TargetPath,
            });

        memory.CloseApplyIntent(intent.Id);
    }

    /// <summary>The target's current bytes, or null when it is absent or unreadable.</summary>
    private static string? CurrentHash(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return null;
        try
        {
            var guard = new WorkspacePathGuard(AnthillRuntime.AllowedWorkspaceRoot);
            var resolved = guard.ResolveSafePath(targetPath);
            return File.Exists(resolved) ? ApplyTransaction.HashFile(resolved) : null;
        }
        catch { return null; }
    }
}
