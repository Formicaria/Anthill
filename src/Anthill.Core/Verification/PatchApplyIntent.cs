namespace Anthill.Core.Verification;

/// <summary>
/// Where an apply had got to when the process stopped. v0.3.8.91.
///
/// The four states the external review asked for, and each boundary is a real crash window:
/// Prepared → Mutating is "we are about to touch the disk", Mutating → Applied is "the bytes
/// landed", Applied → Recorded is "the database agrees". A crash in any of them leaves a row that
/// says which.
/// </summary>
public enum PatchApplyPhase
{
    /// <summary>Intent recorded, nothing touched yet. A crash here means the apply never started.</summary>
    Prepared,
    /// <summary>The write is in flight. A crash here is the ambiguous case the hashes resolve.</summary>
    Mutating,
    /// <summary>Bytes are on disk. The database does NOT yet agree — this is the window that hurt.</summary>
    Applied,
    /// <summary>Disk and database agree. Nothing to reconcile.</summary>
    Recorded,
}

/// <param name="PreHash">The target's bytes before the write, or null when the file did not exist.</param>
/// <param name="PostHash">What the write left, recorded the moment the tool reported success.</param>
public sealed record PatchApplyIntent(
    string Id,
    string PatchId,
    string? ApprovalId,
    string? PatchSetId,
    string MissionId,
    string? TargetPath,
    string? PreHash,
    string? PostHash,
    PatchApplyPhase Phase);
