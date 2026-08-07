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
        if (changeType is not ("add" or "modify"))
            return new ToolResult(Name, false, "", $"ANTHILL currently supports only add and modify patches. Refusing change_type: {changeType}", FailureClass.ValidationFailure);
        if (string.IsNullOrEmpty(newContent)) return new ToolResult(Name, false, "", "Patch new_content is required and must be non-empty.", FailureClass.ValidationFailure);

        try
        {
            return changeType switch
            {
                "add" => ApplyAdd(safePath, newContent),
                "modify" when string.IsNullOrEmpty(oldContent) => new ToolResult(Name, false, "", "MODIFY patches require old_content for exact replacement.", FailureClass.ValidationFailure),
                "modify" => ApplyModify(safePath, oldContent!, newContent),
                _ => new ToolResult(Name, false, "", $"Unsupported change_type: {changeType}", FailureClass.ValidationFailure),
            };
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

    private ToolResult ApplyAdd(string safePath, string newContent)
    {
        // A coder that proposes ADD for a file that already exists (a common LLM slip) previously
        // hard-failed here, stalling auto-apply. Instead treat it as a full-file overwrite: back up
        // the current file first so it is fully reversible, then write the proposed content. This stays
        // inside the safety model — the pre-apply backup, auto-apply verify+rollback, and the
        // standalone-branch-never-main review gate all still apply, and the Patch Center shows the diff
        // before any manual apply. New files still take the plain add path below.
        if (File.Exists(safePath))
        {
            var existingBackup = BackupFile(safePath);
            File.WriteAllText(safePath, newContent, new UTF8Encoding(false));
            return new ToolResult(Name, true, Json.Dumps(new { action = "add_overwrite", file_path = safePath, backup_path = existingBackup }, indented: true));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
        File.WriteAllText(safePath, newContent, new UTF8Encoding(false));
        return new ToolResult(Name, true, Json.Dumps(new { action = "add", file_path = safePath, backup_path = (string?)null }, indented: true));
    }

    private ToolResult ApplyModify(string safePath, string oldContent, string newContent)
    {
        if (!File.Exists(safePath)) return new ToolResult(Name, false, "", $"MODIFY refused because file does not exist: {safePath}", FailureClass.ValidationFailure);
        var current = File.ReadAllText(safePath);
        var occurrences = CountOccurrences(current, oldContent);
        if (occurrences == 0) return new ToolResult(Name, false, "", "MODIFY refused because old_content was not found exactly in the target file.", FailureClass.TargetRejection);
        if (occurrences > 1) return new ToolResult(Name, false, "", $"MODIFY refused because old_content appears {occurrences} times. Patch must be unambiguous.", FailureClass.TargetRejection);
        var backupPath = BackupFile(safePath);
        var index = current.IndexOf(oldContent, StringComparison.Ordinal);
        var updated = current[..index] + newContent + current[(index + oldContent.Length)..];
        File.WriteAllText(safePath, updated, new UTF8Encoding(false));
        return new ToolResult(Name, true, Json.Dumps(new { action = "modify", file_path = safePath, backup_path = backupPath }, indented: true));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1) { count++; index += needle.Length; }
        return count;
    }
}
