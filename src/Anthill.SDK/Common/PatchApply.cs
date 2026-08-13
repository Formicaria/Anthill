namespace Anthill.SDK.Common;

/// <summary>
/// What applying a patch to a file's contents DOES, as one pure function. v3.8.32.
///
/// Before this existed the colony had THREE implementations of "apply a patch", and no two agreed:
///
/// <list type="number">
/// <item><c>ApplyPatchTool</c> — the operator's real path. Modify requires <c>old_content</c> and
///   refuses unless it appears EXACTLY once.</item>
/// <item><c>SandboxedCoderRunner.ApplyIntoSandbox</c> — replaced the first occurrence with no
///   uniqueness check, and treated a modify with no <c>old_content</c> as a whole-file overwrite,
///   which the real tool refuses outright.</item>
/// <item><c>PatchSetMaterializer</c> — ignored <c>old_content</c> entirely and overwrote the file
///   with <c>new_content</c> for every change type.</item>
/// </list>
///
/// The third one is the serious one, because it is the VERIFIER's copy. v3.8.23 shipped "patches are
/// verified in a sandbox that contains them" and the sandbox was built by materialising bytes that
/// the operator's applier would never produce. A patch that modifies twenty lines of a large file was
/// compiled as if the file were those twenty lines. The verification ran, passed, and attested to a
/// tree that could not exist.
///
/// So the semantics live here once, as a function of (change type, old, new, current) → (content or
/// refusal), with no IO. The tool does the writing and the backups; the sandbox and the materializer
/// do their own IO; all three ask THIS what the result should be. A divergence now requires editing
/// this file, where the consequences are written down.
/// </summary>
public enum PatchApplyStatus
{
    /// <summary>A new file was created.</summary>
    Created,
    /// <summary>An existing file's <c>old_content</c> occurrence was replaced.</summary>
    Modified,
    /// <summary>An <c>add</c> whose target already existed; the file is replaced wholesale.</summary>
    Overwrote,
    /// <summary>The target file was removed. v0.3.8.52. Carries no content — see
    /// <see cref="PatchApplyResult.WritesContent"/>.</summary>
    Deleted,
    /// <summary>The target file was moved to <c>destination_path</c>, bytes unchanged. v0.3.8.52.</summary>
    Renamed,

    /// <summary>Change type is not one of add, modify, delete or rename.</summary>
    RefusedUnsupportedChangeType,
    /// <summary><c>new_content</c> was absent or empty.</summary>
    RefusedEmptyNewContent,
    /// <summary>A modify whose target file does not exist.</summary>
    RefusedTargetMissing,
    /// <summary>A modify with no <c>old_content</c>. The real applier will not guess.</summary>
    RefusedMissingOldContent,
    /// <summary><c>old_content</c> does not occur in the target.</summary>
    RefusedOldContentNotFound,
    /// <summary><c>old_content</c> occurs more than once, so the edit is ambiguous.</summary>
    RefusedAmbiguous,

    /// <summary>
    /// The target's current contents do not hash to the base the patch was built against. v0.3.8.37.
    ///
    /// The largest gap in AUTONOMY-10 Phase 1: without this, a patch produced from a stale read
    /// applies silently. `old_content` matching is necessary but not sufficient — the same fragment
    /// can occur in a file that has otherwise moved on, and the surrounding lines the coder reasoned
    /// about are gone.
    /// </summary>
    RefusedStaleBase,

    /// <summary>
    /// A <c>delete</c> or <c>rename</c> that carried <c>new_content</c>. v0.3.8.52.
    ///
    /// Refused rather than ignored. A rename is a PURE MOVE: the bytes that arrive at the
    /// destination are the bytes that left the source. A proposal that supplies content alongside
    /// the move is asking for two different operations, and the model that wrote it believed one of
    /// them would happen. Guessing which would make the applier's behaviour depend on an intent that
    /// was never stated — so the proposal comes back for the coder to split.
    /// </summary>
    RefusedUnexpectedNewContent,
    /// <summary>A <c>rename</c> with no <c>destination_path</c>. There is nowhere to move to.</summary>
    RefusedMissingDestination,
    /// <summary>
    /// A <c>rename</c> whose destination is already occupied — including the case where the
    /// destination resolves to the source itself, which is a no-op the caller should not have
    /// proposed. Refused rather than overwriting: a move that silently destroys an unrelated file is
    /// the one failure mode a rename has that a modify does not.
    /// </summary>
    RefusedDestinationOccupied,
}

/// <summary>
/// The computed result. <see cref="Content"/> is non-null exactly when <see cref="WritesContent"/>.
///
/// v0.3.8.52 split those two ideas apart. Until delete and rename existed, "the patch succeeded"
/// and "there are bytes to write" were the same predicate and <see cref="Ok"/> answered both. They
/// are not the same: a delete succeeds and produces no content, and a rename succeeds by moving the
/// bytes it already has. Callers that write must branch on <see cref="WritesContent"/> —
/// <c>Content!</c> under a bare <see cref="Ok"/> now dereferences null for exactly the two new
/// change types.
/// </summary>
public readonly record struct PatchApplyResult(PatchApplyStatus Status, string? Content, string Reason)
{
    public bool Ok => Status is PatchApplyStatus.Created or PatchApplyStatus.Modified or PatchApplyStatus.Overwrote
                              or PatchApplyStatus.Deleted or PatchApplyStatus.Renamed;

    /// <summary>True when <see cref="Content"/> holds bytes the caller is expected to write.</summary>
    public bool WritesContent => Status is PatchApplyStatus.Created or PatchApplyStatus.Modified
                                        or PatchApplyStatus.Overwrote;
}

public static class PatchApply
{
    /// <summary>Change-type spellings the colony accepts, normalised.</summary>
    public const string Add = "add";
    public const string Modify = "modify";
    public const string Delete = "delete";
    public const string Rename = "rename";

    /// <summary>
    /// Compute the post-apply contents of one file.
    /// </summary>
    /// <param name="changeType">"add", "modify", "delete" or "rename"; compared case-insensitively
    /// after trimming.</param>
    /// <param name="oldContent">Exact text to replace. Required for modify.</param>
    /// <param name="newContent">Replacement text, or the whole file for add. Required and non-empty
    /// for those two; REFUSED for delete and rename, which write no bytes of their own.</param>
    /// <param name="currentContent">The file's current contents, or NULL when it does not exist.
    /// Null and empty-string are different states and the caller must not conflate them: an empty
    /// existing file is a legitimate modify target that no <c>old_content</c> can match, while a
    /// missing file is a different refusal.</param>
    /// <summary>
    /// The base a patch was built against, as a hex SHA-256 of the file's contents.
    ///
    /// Content rather than a git revision on purpose: the coder reads a FILE, and the working tree
    /// it read may hold uncommitted changes that no revision names. Hashing what was actually read
    /// is the only thing that answers "is this still the text the patch was reasoned about".
    /// </summary>
    public static string HashOf(string? content)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content ?? ""));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Compute the post-apply contents of one file.
    /// </summary>
    /// <param name="expectedBaseHash">What the target hashed to when the patch was built, or null
    /// when the producer recorded none. Checked for MODIFY, where a base exists; an `add` that
    /// creates a file has no base to be stale against.
    ///
    /// Null is accepted rather than required, and that is a deliberate staging decision: proposals
    /// written before v0.3.8.37 carry no hash, and refusing them all would turn a safety improvement
    /// into an outage. `PatchProposal.BaseHash` is populated going forward, and
    /// `AStalePatchIsRefused` proves the check bites when the hash is present.</param>
    /// <param name="destinationPath">Where a RENAME moves the file to. Required for rename, ignored
    /// otherwise. Relative-vs-absolute and containment are the CALLER's problem — this function does
    /// no IO and cannot resolve a path; the tool validates it through the same
    /// <c>ValidateSafePatchPath</c> and workspace guard as the source.</param>
    /// <param name="destinationExists">Whether something already sits at <paramref name="destinationPath"/>.
    /// Passed in rather than probed, because this function does no IO. A caller that cannot answer
    /// should pass true and refuse, not false and overwrite.</param>
    public static PatchApplyResult Compute(string? changeType, string? oldContent, string? newContent,
        string? currentContent, string? expectedBaseHash = null,
        string? destinationPath = null, bool destinationExists = false)
    {
        var kind = (changeType ?? "").Trim().ToLowerInvariant();
        if (kind is not (Add or Modify or Delete or Rename))
            return new(PatchApplyStatus.RefusedUnsupportedChangeType, null,
                $"ANTHILL supports only add, modify, delete and rename patches; refusing change_type '{changeType}'.");

        // v0.3.8.52 — delete and rename are dispatched BEFORE the new_content guard below.
        //
        // That ordering is the whole restructure. The non-empty check used to sit above the
        // change-type branches, so a delete could never reach a delete branch even once one
        // existed: it carries no content by definition and was refused as malformed one line
        // earlier. Content is a requirement of the change types that write bytes, not of patches in
        // general, so it is now asked where it is actually needed.
        if (kind == Delete) return ComputeDelete(newContent, currentContent, expectedBaseHash);
        if (kind == Rename) return ComputeRename(newContent, currentContent, expectedBaseHash,
            destinationPath, destinationExists);

        if (string.IsNullOrEmpty(newContent))
            return new(PatchApplyStatus.RefusedEmptyNewContent, null,
                "Patch new_content is required and must be non-empty.");

        if (kind == Add)
            return currentContent is null
                ? new(PatchApplyStatus.Created, newContent, "")
                // An `add` onto an existing file is a common model slip. Treating it as a full
                // overwrite (rather than hard-failing and stalling the queue) is a deliberate
                // decision that predates this file; it is safe only because the caller backs the
                // file up first, and it is recorded distinctly so an auditor can see it happened.
                : new(PatchApplyStatus.Overwrote, newContent, "");

        if (currentContent is null)
            return new(PatchApplyStatus.RefusedTargetMissing, null,
                "MODIFY refused because the target file does not exist.");

        if (string.IsNullOrEmpty(oldContent))
            return new(PatchApplyStatus.RefusedMissingOldContent, null,
                "MODIFY patches require old_content for exact replacement.");

        // Checked BEFORE the occurrence search, deliberately. A stale base and a missing fragment
        // are different problems with different remedies — "the file moved on, rebuild the patch"
        // versus "your old_content is wrong" — and reporting the second when the first is true sends
        // the coder to fix a fragment that was never the issue.
        if (StaleBase("MODIFY", currentContent, expectedBaseHash) is { } stale) return stale;

        var occurrences = CountOccurrences(currentContent, oldContent);
        if (occurrences == 0)
            return new(PatchApplyStatus.RefusedOldContentNotFound, null,
                "MODIFY refused because old_content was not found exactly in the target file.");
        if (occurrences > 1)
            return new(PatchApplyStatus.RefusedAmbiguous, null,
                $"MODIFY refused because old_content appears {occurrences} times. Patch must be unambiguous.");

        var index = currentContent.IndexOf(oldContent, StringComparison.Ordinal);
        var updated = currentContent[..index] + newContent + currentContent[(index + oldContent.Length)..];
        return new(PatchApplyStatus.Modified, updated, "");
    }

    /// <summary>
    /// A <c>delete</c>. v0.3.8.52. Produces no content: the caller backs the file up and removes it.
    ///
    /// The staleness check matters MORE here than for a modify, not less. A modify at least proves
    /// its <c>old_content</c> fragment is still present before it touches anything; a delete asserts
    /// nothing about the file it destroys. Without the base hash, a delete proposed against a file
    /// the coder read an hour ago removes whatever happens to be at that path now.
    /// </summary>
    private static PatchApplyResult ComputeDelete(string? newContent, string? currentContent,
        string? expectedBaseHash)
    {
        if (!string.IsNullOrEmpty(newContent))
            return new(PatchApplyStatus.RefusedUnexpectedNewContent, null,
                "DELETE refused because the proposal carries new_content. A delete removes a file "
                + "and writes nothing; content here means the proposal intended something else.");

        if (currentContent is null)
            return new(PatchApplyStatus.RefusedTargetMissing, null,
                "DELETE refused because the target file does not exist.");

        if (StaleBase("DELETE", currentContent, expectedBaseHash) is { } stale) return stale;

        return new(PatchApplyStatus.Deleted, null, "");
    }

    /// <summary>
    /// A <c>rename</c>. v0.3.8.52. A PURE MOVE — the destination receives the source's existing
    /// bytes, and this returns none for the caller to write.
    ///
    /// Ordering note: the content refusal comes first, ahead of the missing-destination one. A
    /// proposal carrying both problems is better told the thing that reveals the misunderstanding —
    /// "this is a move, not a write" — than told to add a field to a request whose shape is wrong.
    /// </summary>
    private static PatchApplyResult ComputeRename(string? newContent, string? currentContent,
        string? expectedBaseHash, string? destinationPath, bool destinationExists)
    {
        if (!string.IsNullOrEmpty(newContent))
            return new(PatchApplyStatus.RefusedUnexpectedNewContent, null,
                "RENAME refused because the proposal carries new_content. A rename is a pure move; "
                + "to move a file AND change it, propose the rename and a following modify.");

        if (string.IsNullOrWhiteSpace(destinationPath))
            return new(PatchApplyStatus.RefusedMissingDestination, null,
                "RENAME refused because the proposal has no destination_path.");

        if (currentContent is null)
            return new(PatchApplyStatus.RefusedTargetMissing, null,
                "RENAME refused because the source file does not exist.");

        // Covers the destination-is-the-source case too: the source demonstrably exists by the line
        // above, so a destination resolving to it reports as occupied. That is the right answer for
        // both — a no-op move and a clobbering move are each something the caller must fix.
        if (destinationExists)
            return new(PatchApplyStatus.RefusedDestinationOccupied, null,
                $"RENAME refused because something already exists at destination '{destinationPath}'. "
                + "Renaming onto an existing file would destroy it.");

        if (StaleBase("RENAME", currentContent, expectedBaseHash) is { } stale) return stale;

        return new(PatchApplyStatus.Renamed, null, "");
    }

    /// <summary>
    /// The shared staleness check: has the target moved on since the patch was built?
    ///
    /// One implementation across modify, delete and rename, because the question and its remedy are
    /// identical for all three. <paramref name="kind"/> only names the change type in the message an
    /// operator reads. Null when there is nothing to complain about — either no expected hash was
    /// recorded (every proposal written before v0.3.8.37) or it matches.
    /// </summary>
    private static PatchApplyResult? StaleBase(string kind, string currentContent, string? expectedBaseHash)
    {
        if (string.IsNullOrWhiteSpace(expectedBaseHash)) return null;
        var actual = HashOf(currentContent);
        if (string.Equals(actual, expectedBaseHash.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        return new(PatchApplyStatus.RefusedStaleBase, null,
            $"{kind} refused because the file has changed since this patch was built "
            + $"(expected base {Short(expectedBaseHash)}, found {Short(actual)}). "
            + "Re-read the file and propose again.");
    }

    /// <summary>First twelve hex characters — enough to compare by eye in an error an operator reads.</summary>
    private static string Short(string? hash) =>
        (hash ?? "").Trim() is { Length: > 12 } h ? h[..12] : (hash ?? "").Trim();

    /// <summary>Non-overlapping occurrence count, ordinal. The uniqueness rule depends on it.</summary>
    public static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
