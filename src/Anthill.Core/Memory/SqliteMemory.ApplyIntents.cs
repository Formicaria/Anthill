using Anthill.Core.Verification;

namespace Anthill.Core.Memory;

/// <summary>
/// The apply intent journal: what a write to the operator's tree was about to do, and how far it
/// got. v0.3.8.91.
///
/// WHY IT EXISTS. `ApplyApprovedPatch` performed the filesystem write first and then four separate,
/// un-transacted database updates — patch status, approval status, event, pheromone. A crash between
/// them left the file changed on disk and the patch still `approved` in the database. On restart the
/// Patch Center offered Apply again, the recompute found the file no longer matching its base hash,
/// the patch was marked FAILED, and `RevertAppliedPatch` then refused because only an APPLIED patch
/// can be reverted. A change that really landed, recorded as never having happened, and unrevertable.
///
/// The auto-apply lane already had a durable journal for the FILESYSTEM half. This is the half that
/// was missing everywhere: the database effects that must follow a write, recorded before the write
/// happens, so a restart can finish or discard them deterministically instead of guessing.
/// </summary>
public sealed partial class SqliteMemory
{
    /// <summary>
    /// Record the intent BEFORE anything is touched, with the target's current bytes.
    ///
    /// The pre-hash is what makes reconciliation decidable rather than a guess: after a crash the
    /// file either still hashes to this (the write never landed) or does not (it did, or something
    /// else moved it — and those are distinguishable by the post-hash once there is one).
    /// </summary>
    public PatchApplyIntent BeginApplyIntent(
        string patchId, string? approvalId, string? patchSetId, string missionId,
        string? targetPath, string? preHash)
    {
        var intent = new PatchApplyIntent(
            Id: $"intent_{Guid.NewGuid():N}"[..24],
            PatchId: patchId, ApprovalId: approvalId, PatchSetId: patchSetId, MissionId: missionId,
            TargetPath: targetPath, PreHash: preHash, PostHash: null,
            Phase: PatchApplyPhase.Prepared);

        var now = AnthillTime.NowUtc().ToIso();
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"INSERT INTO patch_apply_intents
                    (id, patch_id, approval_id, patch_set_id, mission_id, target_path, pre_hash,
                     post_hash, phase, created_at, updated_at)
                  VALUES (@id, @pid, @aid, @sid, @mid, @path, @pre, NULL, @phase, @now, @now)",
                ("@id", intent.Id), ("@pid", patchId), ("@aid", approvalId), ("@sid", patchSetId),
                ("@mid", missionId), ("@path", targetPath), ("@pre", preHash),
                ("@phase", PatchApplyPhase.Prepared.ToString()), ("@now", now));
        }
        return intent;
    }

    /// <summary>Advance the phase, and record what the write left when it lands.</summary>
    public void AdvanceApplyIntent(string intentId, PatchApplyPhase phase, string? postHash = null)
    {
        if (string.IsNullOrWhiteSpace(intentId)) return;
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null,
                @"UPDATE patch_apply_intents
                     SET phase = @phase,
                         post_hash = COALESCE(@post, post_hash),
                         updated_at = @now
                   WHERE id = @id",
                ("@phase", phase.ToString()), ("@post", postHash),
                ("@now", AnthillTime.NowUtc().ToIso()), ("@id", intentId));
        }
    }

    /// <summary>
    /// Disk and database agree; the row has nothing left to say. Deleted rather than marked, so the
    /// table only ever holds work in flight and a reconciliation sweep reads a short list.
    /// </summary>
    public void CloseApplyIntent(string intentId)
    {
        if (string.IsNullOrWhiteSpace(intentId)) return;
        lock (_writeLock)
        {
            using var conn = Connect();
            NonQuery(conn, null, "DELETE FROM patch_apply_intents WHERE id = @id", ("@id", intentId));
        }
    }

    /// <summary>Every intent that never reached <see cref="PatchApplyPhase.Recorded"/>.</summary>
    public IReadOnlyList<PatchApplyIntent> OpenApplyIntents() =>
        Query(@"SELECT id, patch_id, approval_id, patch_set_id, mission_id, target_path, pre_hash,
                  post_hash, phase FROM patch_apply_intents ORDER BY created_at ASC")
            .Select(row => new PatchApplyIntent(
                Id: row.GetValueOrDefault("id")?.ToString() ?? "",
                PatchId: row.GetValueOrDefault("patch_id")?.ToString() ?? "",
                ApprovalId: row.GetValueOrDefault("approval_id") as string,
                PatchSetId: row.GetValueOrDefault("patch_set_id") as string,
                MissionId: row.GetValueOrDefault("mission_id")?.ToString() ?? "",
                TargetPath: row.GetValueOrDefault("target_path") as string,
                PreHash: row.GetValueOrDefault("pre_hash") as string,
                PostHash: row.GetValueOrDefault("post_hash") as string,
                Phase: Enum.TryParse<PatchApplyPhase>(
                    row.GetValueOrDefault("phase")?.ToString(), out var phase)
                    ? phase
                    // An unparseable phase is treated as the most dangerous one it could be. A row
                    // written by a newer build, or corrupted, must not read as "nothing happened".
                    : PatchApplyPhase.Mutating))
            .ToList();
}
