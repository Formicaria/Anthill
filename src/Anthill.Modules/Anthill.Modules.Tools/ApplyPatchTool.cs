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
    public string Description => "Approval-gated tool that applies safe ADD or MODIFY patch proposals with backups.";
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

        string safePath;
        // v3.8.18 — _options is PASSED. It was held and not passed, so the suffix allow-list and
        // blocked-path parts this tool validates against came from process-global state while the
        // tool's own gates came from the injected contract. Two answers to one question, on the
        // tool that writes to disk.
        try { Validation.ValidateSafePatchPath(filePath, _options); safePath = _guard.ResolveSafePath(filePath); }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Unsafe patch path: {e.Message}", ToolFailure.Classify(e)); }
        if (_guard.IsBlockedPath(safePath)) return new ToolResult(Name, false, "", "Refusing to patch blocked internal/system path.", FailureClass.AuthorizationFailure);

        try
        {
            // NULL means "does not exist" and is distinct from an existing empty file — PatchApply
            // refuses those two cases differently, so the distinction must survive the read.
            var current = File.Exists(safePath) ? File.ReadAllText(safePath) : null;
            return ApplyComputed(safePath, PatchApply.Compute(changeType, oldContent, newContent, current));
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
    /// </summary>
    private ToolResult ApplyComputed(string safePath, PatchApplyResult outcome)
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
        if (outcome.Status is PatchApplyStatus.RefusedOldContentNotFound or PatchApplyStatus.RefusedAmbiguous)
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

            case PatchApplyStatus.Overwrote:
            {
                // An `add` onto an existing file. Reversible because the backup is taken FIRST.
                var existingBackup = BackupFile(safePath);
                File.WriteAllText(safePath, outcome.Content!, new UTF8Encoding(false));
                return new ToolResult(Name, true, Json.Dumps(
                    new { action = "add_overwrite", file_path = safePath, backup_path = existingBackup }, indented: true));
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
