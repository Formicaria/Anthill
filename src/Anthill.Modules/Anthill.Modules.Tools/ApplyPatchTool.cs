using System.Text;
using Anthill.SDK.Common;
using Anthill.SDK.Contracts;
using Anthill.SDK.Tools;

namespace Anthill.Modules.Tools;

/// <summary>
/// The tool that changes the operator's files, and the reason the module boundary had to be drawn
/// carefully rather than quickly.
///
/// Two gates, both re-read on every call — patch application AND file writing — plus the path
/// validator, the workspace containment check and the blocked-path check. None of that moved: the
/// gates arrive through <see cref="IToolRuntimeOptions"/>, the containment through
/// <see cref="IWorkspacePathGuard"/>, and <c>Validation.ValidateSafePatchPath</c> has been in the SDK
/// since v3.8.12. What moved is only the part that reads and writes bytes.
///
/// CORRECTED IN v3.8.18. The paragraph above was written in v3.8.16 and was false for one path:
/// <c>ValidateSafePatchPath</c> was called WITHOUT the injected options, so the suffix allow-list
/// and blocked-path parts resolved through ambient state while every other gate on this tool used
/// the contract. An external review found it. The comment being wrong was the worse half — it is
/// the first thing a reader checking this boundary would have trusted.
/// </summary>
public sealed class ApplyPatchTool : ITool
{
    public string Name => "apply_patch";
    public string Description => "Approval-gated tool that applies safe ADD, MODIFY, DELETE or RENAME patch proposals with backups.";
    private readonly IWorkspacePathGuard _guard;
    private readonly IToolRuntimeOptions _options;

    public ApplyPatchTool(IWorkspacePathGuard guard, IToolRuntimeOptions? options = null)
    {
        _guard = guard;
        _options = options ?? SafetyPolicy.RequiredToolOptions;
    }

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        if (!_options.PatchApplicationEnabled) return new ToolResult(Name, false, "", "Patch application is disabled by config.", FailureClass.AuthorizationFailure);
        if (!_options.FileWritingEnabled) return new ToolResult(Name, false, "", "File writing is disabled by config.", FailureClass.AuthorizationFailure);
        if (args.GetValueOrDefault("patch") is not Dictionary<string, object?> patch)
            return new ToolResult(Name, false, "", "Missing required dict argument: patch", FailureClass.ValidationFailure);

        var changeType = (patch.GetValueOrDefault("change_type")?.ToString() ?? "").Trim().ToLowerInvariant();
        var filePath = (patch.GetValueOrDefault("file_path")?.ToString() ?? "").Trim();
        var oldContent = patch.GetValueOrDefault("old_content") as string;
        var newContent = patch.GetValueOrDefault("new_content") as string;
        // v0.3.8.37: null when the producer recorded no base (every proposal before this release).
        var baseHash = patch.GetValueOrDefault("base_hash") as string;
        // v0.3.8.52: null for every change type but rename, and for every pre-release proposal.
        var destination = (patch.GetValueOrDefault("destination_path")?.ToString() ?? "").Trim();

        string safePath;
        // v3.8.18 — _options is PASSED. It was held and not passed, so the suffix allow-list and
        // blocked-path parts this tool validates against came from process-global state while the
        // tool's own gates came from the injected contract. Two answers to one question, on the
        // tool that writes to disk.
        try { Validation.ValidateSafePatchPath(filePath, _options); safePath = _guard.ResolveSafePath(filePath); }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Unsafe patch path: {e.Message}", ToolFailure.Classify(e)); }
        if (_guard.IsBlockedPath(safePath)) return new ToolResult(Name, false, "", "Refusing to patch blocked internal/system path.", FailureClass.AuthorizationFailure);

        // v0.3.8.52 — a rename's DESTINATION passes the identical gauntlet as its source: the same
        // ValidateSafePatchPath with the same injected options, the same guard resolution, the same
        // blocked-path check. A move is a write to the destination, and validating only the source
        // would make `rename` the one way to put bytes at a path this tool would otherwise refuse.
        string? safeDestination = null;
        if (destination.Length > 0)
        {
            try { Validation.ValidateSafePatchPath(destination, _options); safeDestination = _guard.ResolveSafePath(destination); }
            catch (Exception e) { return new ToolResult(Name, false, "", $"Unsafe patch destination: {e.Message}", ToolFailure.Classify(e)); }
            if (_guard.IsBlockedPath(safeDestination))
                return new ToolResult(Name, false, "", "Refusing to move a patched file to a blocked internal/system path.", FailureClass.AuthorizationFailure);
        }

        try
        {
            // NULL means "does not exist" and is distinct from an existing empty file — PatchApply
            // refuses those two cases differently, so the distinction must survive the read.
            var current = File.Exists(safePath) ? File.ReadAllText(safePath) : null;
            // Directory too, not just File: a rename onto a directory would fail at the move with an
            // IO error rather than a refusal that names the problem.
            var destinationTaken = safeDestination is not null
                && (File.Exists(safeDestination) || Directory.Exists(safeDestination));
            return ApplyComputed(safePath, safeDestination,
                PatchApply.Compute(changeType, oldContent, newContent, current, baseHash,
                    safeDestination is null ? null : destination, destinationTaken,
                    // v0.3.8.57 — THIS is the tool that writes to the operator's real tree, so a
                    // destructive change with no base hash is refused here. The sandbox and the
                    // materializer deliberately do not opt in: refusing a legacy proposal during
                    // verification tells the operator nothing actionable, while accepting one at
                    // this line is the silent stale write the hash exists to prevent.
                    requireBaseHash: true));
        }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Patch application failed: {e.Message}", ToolFailure.Classify(e)); }
    }

    private string? BackupFile(string path)
    {
        if (!File.Exists(path)) return null;
        var backupRoot = Path.GetFullPath(Path.Combine(_options.ScriptDirectory, _options.BackupDirectory));
        Directory.CreateDirectory(backupRoot);
        // v3.8.16 — was `new WorkspacePathGuard().Root`, a second guard built inline that resolved to
        // the same configured root by coincidence. The injected one is that root, stated.
        var safeName = Path.GetRelativePath(_guard.Root, path).Replace("\\", "__").Replace("/", "__");
        var backupPath = Path.Combine(backupRoot, $"{safeName}.{AnthillTime.TimestampId()}.bak");
        File.Copy(path, backupPath, overwrite: true);
        return backupPath;
    }

    /// <summary>
    /// Do the IO for a computed result. v3.8.32 — the DECISION of what the file should contain now
    /// comes from <see cref="PatchApply.Compute"/>, shared with the sandbox runner and the
    /// verifier's materializer, which each had their own divergent copy of these rules.
    ///
    /// What stays here is what only this tool does: back the file up before overwriting it, create
    /// parent directories, and write UTF-8 without a BOM.
    ///
    /// v0.3.8.52 adds the two change types that do not write bytes. Both still take a backup FIRST,
    /// for the same reason the overwrite arm does — it is the only thing that makes them reversible,
    /// and a delete with no backup is the one patch outcome the revert path cannot undo.
    /// </summary>
    private ToolResult ApplyComputed(string safePath, string? safeDestination, PatchApplyResult outcome)
    {
        // The class is named AT the construction, as two literals rather than through a mapping
        // helper. `EveryToolFailureInTheSource_NamesItsFailureClass` scans the statement text and a
        // helper call defeats it — and the guard is right to insist: a reader looking at a failure
        // site should see how it classifies without navigating somewhere else. The first draft here
        // used a `RefusalClass(status)` helper and the guard caught it.
        //
        // TargetRejection vs ValidationFailure is the distinction that matters to the model:
        // "your old_content does not match this file" is a fixable argument problem, whereas a
        // malformed proposal is the caller's own construction being wrong.
        // v0.3.8.37 adds RefusedStaleBase to this arm. It belongs with the target problems: the
        // proposal was well-formed and the file moved on underneath it, so the remedy is to re-read
        // and propose again. Classifying it as ValidationFailure would tell the coder its arguments
        // were malformed and send it to fix a fragment that was never wrong.
        // v0.3.8.52 adds RefusedDestinationOccupied to this arm on the same test: the proposal was
        // well-formed and the TREE is what refuses it. A malformed rename — no destination, or one
        // carrying new_content — is the caller's own construction being wrong and falls through to
        // ValidationFailure below.
        if (outcome.Status is PatchApplyStatus.RefusedOldContentNotFound
                           or PatchApplyStatus.RefusedAmbiguous
                           or PatchApplyStatus.RefusedStaleBase
                           or PatchApplyStatus.RefusedDestinationOccupied
                           // v0.3.8.57 — both are the TREE disagreeing with the proposal, not a
                           // malformed proposal: the target exists when the patch expected to create
                           // it, or the patch cannot say what it was built against. TargetRejection
                           // is what routes these back for a fresh read rather than to the coder as
                           // a formatting error.
                           or PatchApplyStatus.RefusedTargetExists
                           or PatchApplyStatus.RefusedMissingBaseHash)
            return new ToolResult(Name, false, "", outcome.Reason, FailureClass.TargetRejection);
        if (!outcome.Ok)
            return new ToolResult(Name, false, "", outcome.Reason, FailureClass.ValidationFailure);

        switch (outcome.Status)
        {
            case PatchApplyStatus.Created:
                Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
                File.WriteAllText(safePath, outcome.Content!, new UTF8Encoding(false));
                return new ToolResult(Name, true, Json.Dumps(
                    new { action = "add", file_path = safePath, backup_path = (string?)null }, indented: true));

            // v0.3.8.57 — the `Overwrote` arm is GONE. `PatchApply` no longer produces that status:
            // an `add` onto an existing file is now RefusedTargetExists, handled above. The write it
            // used to perform replaced a whole file with whatever fragment the proposal carried, and
            // the backup it took first made that recoverable rather than correct.

            case PatchApplyStatus.Deleted:
            {
                // Backup THEN delete, in that order. Reversed, a failure between the two loses the
                // file outright; this way the worst case is a backup with nothing removed.
                var deleteBackup = BackupFile(safePath);
                File.Delete(safePath);
                return new ToolResult(Name, true, Json.Dumps(
                    new { action = "delete", file_path = safePath, backup_path = deleteBackup }, indented: true));
            }

            case PatchApplyStatus.Renamed:
            {
                // Move, not copy-then-delete. File.Move preserves the bytes exactly and cannot leave
                // both halves behind if it fails partway. The backup still comes first, because the
                // revert path needs something to restore when the destination is later gone.
                var renameBackup = BackupFile(safePath);
                Directory.CreateDirectory(Path.GetDirectoryName(safeDestination!)!);
                File.Move(safePath, safeDestination!);
                return new ToolResult(Name, true, Json.Dumps(
                    new { action = "rename", file_path = safePath, destination_path = safeDestination,
                          backup_path = renameBackup }, indented: true));
            }

            default:
            {
                var backupPath = BackupFile(safePath);
                File.WriteAllText(safePath, outcome.Content!, new UTF8Encoding(false));
                return new ToolResult(Name, true, Json.Dumps(
                    new { action = "modify", file_path = safePath, backup_path = backupPath }, indented: true));
            }
        }
    }
}
