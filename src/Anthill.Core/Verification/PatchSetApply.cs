using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Orchestration;
using Anthill.Core.Security;
using Anthill.SDK.Common;

namespace Anthill.Core.Verification;

/// <param name="Refusals">Empty when the set can be applied. One line per reason it cannot.</param>
public sealed record SetApplyOutcome(
    bool Applied, int Count, string Message, IReadOnlyList<string> Refusals)
{
    public static SetApplyOutcome Refused(string message, IReadOnlyList<string> refusals) =>
        new(false, 0, message, refusals);
}

/// <summary>
/// A PATCH SET APPLIES AS A UNIT, ON EVERY PATH. v0.3.8.91.
///
/// THE CLAIM AND THE CODE. Six places in this repository state that a patch set "applies as a unit or
/// not at all" — `PLAN.md` lists it under Done and load-bearing, `ApplyTransaction`'s header frames it
/// as the v0.3.8.57 guarantee, `AutoApplyAtomicityTests` is named for it. It was true of exactly one
/// lane. The bypass path looped `foreach (var proposal in patchSet.Proposals)` calling a single-patch
/// apply and CONTINUED past a failure, so a three-file set whose second proposal hit a stale base
/// left files one and three written — a tree that was never verified, with the verification record
/// still describing the set as a whole. Under the git-commit policy it also left one commit per file,
/// any prefix of which could be the final state.
///
/// Verification already reasons about the set as a unit and says why: `PatchSetMaterializer` "FAILS
/// CLOSED AND AS A UNIT", and `ExecutionService` states that "a patch is applied as a unit, so it
/// must be judged as one". Application is the half that did not hold up its end.
///
/// HOW THIS WORKS, and it is deliberately the same shape `AutoApplyRunner` already proved:
///   1. Compute every proposal against the live tree WITHOUT writing. Any refusal stops the set
///      before a byte moves — a failure discovered halfway through is the expensive kind.
///   2. Open a durable journal before the first mutation.
///   3. Stage each file's pre-state and backup BEFORE its write, so a crash at any instant leaves a
///      journal startup recovery can replay.
///   4. On any failure, roll the WHOLE set back under the hash rule — restore only where the current
///      bytes still match what this apply wrote, because anything else is newer work and not
///      rollback's to destroy.
///
/// WHY THE PREFLIGHT LIVES HERE. `AutoApplyRunner` had its own copy in `Anthill.Api`. Two
/// implementations of one rule is a named defect class in this repository and this one had already
/// half-drifted — the auto-apply lane had a preflight and the ordinary lane had none at all. Api's
/// version now delegates to this one.
/// </summary>
public static class PatchSetApply
{
    /// <summary>
    /// Every proposal in a set, as domain objects, in a stable order.
    ///
    /// Ordered by id so a rollback report and a journal read the same way twice. The order does not
    /// affect correctness — the set is all-or-nothing — but an unstable order makes two runs of the
    /// same failure produce two different-looking reports, which costs an operator time.
    /// </summary>
    public static List<(string PatchId, PatchProposal Proposal)> LoadSet(
        Memory.SqliteMemory memory, string patchSetId)
    {
        var set = new List<(string, PatchProposal)>();
        if (string.IsNullOrWhiteSpace(patchSetId)) return set;

        foreach (var row in memory.GetPatchProposalsForSet(patchSetId))
        {
            var id = row.GetValueOrDefault("id")?.ToString() ?? "";
            if (id.Length == 0) continue;

            set.Add((id, new PatchProposal
            {
                Id = id,
                FilePath = row.GetValueOrDefault("file_path")?.ToString() ?? "",
                ChangeType = EnumExtensions.ParsePatchChangeType(
                    row.GetValueOrDefault("change_type")?.ToString() ?? "modify"),
                OldContent = row.GetValueOrDefault("old_content") as string,
                NewContent = row.GetValueOrDefault("new_content") as string,
                BaseHash = row.GetValueOrDefault("base_hash") as string,
                DestinationPath = row.GetValueOrDefault("destination_path") as string,
            }));
        }

        return set.OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Compute every proposal against the tree WITHOUT writing. Empty means the set can be applied.
    ///
    /// Uses `PatchApply.Compute` — the same function the applier runs — so a preflight that passes
    /// and an apply that then refuses would mean the two disagree, which is exactly the drift a
    /// second hand-written checker introduces. `requireBaseHash: true` matches the live applier: this
    /// writes to the operator's real tree, so a destructive proposal that cannot say what it was
    /// built against is refused here rather than discovered mid-set.
    /// </summary>
    public static List<string> Preflight(IEnumerable<(string PatchId, PatchProposal Proposal)> set)
    {
        var refusals = new List<string>();
        var guard = new WorkspacePathGuard(AnthillRuntime.AllowedWorkspaceRoot);

        foreach (var (patchId, proposal) in set)
        {
            string? current;
            string? safeDestination = null;
            var destinationTaken = false;
            try
            {
                var resolved = guard.ResolveSafePath(proposal.FilePath);
                current = File.Exists(resolved) ? File.ReadAllText(resolved) : null;

                if (!string.IsNullOrWhiteSpace(proposal.DestinationPath))
                {
                    safeDestination = guard.ResolveSafePath(proposal.DestinationPath!);
                    destinationTaken = File.Exists(safeDestination) || Directory.Exists(safeDestination);
                }
            }
            catch (Exception error)
            {
                refusals.Add($"{proposal.FilePath}: {error.Message}");
                continue;
            }

            var outcome = PatchApply.Compute(
                proposal.ChangeType.Value(), proposal.OldContent, proposal.NewContent, current,
                proposal.BaseHash,
                safeDestination is null ? null : proposal.DestinationPath,
                destinationTaken,
                requireBaseHash: true);

            if (!outcome.Ok)
                refusals.Add($"{proposal.FilePath} ({proposal.ChangeType.Value()}) [{patchId}]: {outcome.Reason}");
        }

        return refusals;
    }

    /// <summary>
    /// Apply a whole set inside one durable transaction, or apply none of it.
    ///
    /// <paramref name="applyOne"/> performs the single-file write and reports what it left behind —
    /// the same delegate shape the auto-apply lane uses, so the transaction owns durability and the
    /// tool keeps owning path guards and patch semantics.
    ///
    /// <paramref name="rollBack"/> restores the set. Passed in rather than done here because a
    /// rollback has to update the store as well as the disk, and the store update belongs to the
    /// caller that owns those records.
    /// </summary>
    public static SetApplyOutcome ApplySet(
        Memory.SqliteMemory memory,
        string patchSetId,
        IReadOnlyList<(string PatchId, PatchProposal Proposal)> set,
        Func<string, Queen.AutoApplyOutcome> applyOne,
        Action<IReadOnlyList<Queen.AutoApplyOutcome>, ApplyTransaction, string> rollBack)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(applyOne);

        if (set.Count == 0)
            return SetApplyOutcome.Refused($"patch set {patchSetId} has no proposals", Array.Empty<string>());

        var refusals = Preflight(set);
        if (refusals.Count > 0)
            return SetApplyOutcome.Refused(
                $"{refusals.Count} of {set.Count} proposal(s) in set {patchSetId} cannot be applied to "
              + "the tree as it stands, so none were", refusals);

        var workspace = AnthillRuntime.AllowedWorkspaceRoot;
        var guard = new WorkspacePathGuard(workspace);
        var tx = ApplyTransaction.Begin(workspace, note: $"patch set {patchSetId}");
        var applied = new List<Queen.AutoApplyOutcome>();

        foreach (var (patchId, proposal) in set)
        {
            ApplyTransaction.Entry? entry;
            try
            {
                var target = guard.ResolveSafePath(proposal.FilePath);
                var destination = string.IsNullOrWhiteSpace(proposal.DestinationPath)
                    ? null : guard.ResolveSafePath(proposal.DestinationPath!);
                entry = tx.StageExternal(target, proposal.ChangeType.Value(), destination);
            }
            catch (Exception error)
            {
                // Staging could not even record intent. Refuse before mutating anything further.
                rollBack(applied, tx, $"{proposal.FilePath} could not be staged: {error.Message}");
                return SetApplyOutcome.Refused(
                    $"patch set {patchSetId} was not applied: {proposal.FilePath} could not be staged",
                    new[] { error.Message });
            }

            var outcome = applyOne(patchId);
            if (outcome.Success)
            {
                tx.MarkApplied(entry, outcome.AppliedHash);
                applied.Add(outcome);
                continue;
            }

            // A write failed AFTER preflight passed — a race, a permission change, a full disk. The
            // whole set goes back, including whatever the failed operation left behind: its entry is
            // journaled, its backup exists, and the hash rule decides per file whether the pre-apply
            // bytes can safely be restored.
            rollBack(applied, tx, $"{outcome.FilePath} could not be written: {outcome.Error}");
            return SetApplyOutcome.Refused(
                $"patch set {patchSetId} was rolled back: {outcome.FilePath} could not be written",
                new[] { outcome.Error ?? "unknown write failure" });
        }

        tx.Commit();
        return new SetApplyOutcome(true, applied.Count,
            $"patch set {patchSetId} applied as a unit: {applied.Count} file(s)", Array.Empty<string>());
    }
}
