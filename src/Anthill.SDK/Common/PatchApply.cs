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

    /// <summary>Change type is neither add nor modify.</summary>
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
}

/// <summary>The computed result. <see cref="Content"/> is non-null exactly when <see cref="Ok"/>.</summary>
public readonly record struct PatchApplyResult(PatchApplyStatus Status, string? Content, string Reason)
{
    public bool Ok => Status is PatchApplyStatus.Created or PatchApplyStatus.Modified or PatchApplyStatus.Overwrote;
}

public static class PatchApply
{
    /// <summary>Change-type spellings the colony accepts, normalised.</summary>
    public const string Add = "add";
    public const string Modify = "modify";

    /// <summary>
    /// Compute the post-apply contents of one file.
    /// </summary>
    /// <param name="changeType">"add" or "modify"; compared case-insensitively after trimming.</param>
    /// <param name="oldContent">Exact text to replace. Required for modify.</param>
    /// <param name="newContent">Replacement text, or the whole file for add. Required, non-empty.</param>
    /// <param name="currentContent">The file's current contents, or NULL when it does not exist.
    /// Null and empty-string are different states and the caller must not conflate them: an empty
    /// existing file is a legitimate modify target that no <c>old_content</c> can match, while a
    /// missing file is a different refusal.</param>
    public static PatchApplyResult Compute(string? changeType, string? oldContent, string? newContent,
        string? currentContent)
    {
        var kind = (changeType ?? "").Trim().ToLowerInvariant();
        if (kind is not (Add or Modify))
            return new(PatchApplyStatus.RefusedUnsupportedChangeType, null,
                $"ANTHILL supports only add and modify patches; refusing change_type '{changeType}'.");

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
