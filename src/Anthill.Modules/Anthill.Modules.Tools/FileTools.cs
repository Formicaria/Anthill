using Anthill.SDK.Common;
using Anthill.SDK.Contracts;
using Anthill.SDK.Tools;

namespace Anthill.Modules.Tools;

// v3.8.16 — moved from Anthill.Core/Tools/Tools.cs. Behaviour is unchanged; what changed is what
// they are allowed to see. Each one now holds an IWorkspacePathGuard and an IToolRuntimeOptions
// rather than reaching into AnthillRuntime, and the limits arrive as SDK constants.
//
// The v3.8.11 comment that used to sit on every one of these still applies and is stated once here:
// the runtime gates are read LIVE on every call. This is the colony's SECOND gate — the composition
// root already decided whether to register the tool, and this re-check is what stops one that
// somehow reached the registry from acting. Capturing the values would quietly collapse the two
// into one.

public sealed class DirectoryListTool : ITool
{
    public string Name => "list_directory";
    public string Description => "Read-only tool that lists files and folders inside the allowed workspace.";
    private readonly IWorkspacePathGuard _guard;
    private readonly IToolRuntimeOptions _options;

    public DirectoryListTool(IWorkspacePathGuard guard, IToolRuntimeOptions? options = null)
    {
        _guard = guard;
        _options = options ?? SafetyPolicy.RequiredToolOptions;
    }

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        if (!_options.FileToolsEnabled) return new ToolResult(Name, false, "", "File tools are disabled by config.", FailureClass.AuthorizationFailure);
        var requested = (args.GetValueOrDefault("path")?.ToString()) ?? ".";
        string safePath;
        try { safePath = _guard.ResolveSafePath(requested); }
        catch (Exception e) { return new ToolResult(Name, false, "", e.Message, ToolFailure.Classify(e)); }
        if (_guard.IsBlockedPath(safePath)) return new ToolResult(Name, false, "", "Refusing to list blocked internal/system path.", FailureClass.AuthorizationFailure);
        if (!Directory.Exists(safePath)) return new ToolResult(Name, false, "", $"Directory does not exist: {safePath}", FailureClass.ValidationFailure);

        var items = new List<string>();
        var entries = new DirectoryInfo(safePath).GetFileSystemInfos().OrderBy(p => p.Name.ToLowerInvariant()).ToList();
        for (var i = 0; i < entries.Count; i++)
        {
            if (i >= ToolLimits.MaxDirectoryItems) { items.Add($"...[truncated after {ToolLimits.MaxDirectoryItems} items]"); break; }
            var child = entries[i];
            if (_guard.IsBlockedPath(child.FullName)) continue;
            var type = child is DirectoryInfo ? "DIR " : "FILE";
            items.Add($"{type}  {child.Name}");
        }
        var output = items.Count > 0 ? string.Join("\n", items) : "(directory is empty or all items are blocked)";
        return new ToolResult(Name, true, output);
    }
}

public sealed class ReadTextFileTool : ITool
{
    public string Name => "read_text_file";
    public string Description => "Read-only tool that reads text files inside the allowed workspace with a character limit.";
    private readonly IWorkspacePathGuard _guard;
    private readonly IToolRuntimeOptions _options;

    public ReadTextFileTool(IWorkspacePathGuard guard, IToolRuntimeOptions? options = null)
    {
        _guard = guard;
        _options = options ?? SafetyPolicy.RequiredToolOptions;
    }

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        if (!_options.FileToolsEnabled) return new ToolResult(Name, false, "", "File tools are disabled by config.", FailureClass.AuthorizationFailure);
        var requested = args.GetValueOrDefault("path")?.ToString();
        if (string.IsNullOrEmpty(requested)) return new ToolResult(Name, false, "", "Missing required argument: path", FailureClass.ValidationFailure);
        string safePath;
        try { safePath = _guard.ResolveSafePath(requested); }
        catch (Exception e) { return new ToolResult(Name, false, "", e.Message, ToolFailure.Classify(e)); }
        if (_guard.IsBlockedPath(safePath)) return new ToolResult(Name, false, "", "Refusing to read from blocked internal/system path.", FailureClass.AuthorizationFailure);
        var suffix = Path.GetExtension(safePath).ToLowerInvariant();
        if (_options.BlockedFileSuffixes.Contains(suffix)) return new ToolResult(Name, false, "", $"Refusing to read blocked file type: {suffix}", FailureClass.AuthorizationFailure);
        if (!File.Exists(safePath)) return new ToolResult(Name, false, "", $"File does not exist: {safePath}", FailureClass.ValidationFailure);
        if (!_options.PatchAllowedSuffixes.Contains(suffix)) return new ToolResult(Name, false, "", $"Refusing to read unsupported file type: {suffix}", FailureClass.AuthorizationFailure);
        string content;
        try { content = File.ReadAllText(safePath); }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Could not read file: {e.Message}", ToolFailure.Classify(e)); }
        content = TextUtil.Truncate(content, ToolLimits.MaxFileReadChars, $"...[file truncated after {ToolLimits.MaxFileReadChars} characters]");
        return new ToolResult(Name, true, content);
    }
}

public sealed class WriteTextFileTool : ITool
{
    public string Name => "write_text_file";
    public string Description => "Writes or creates a text file inside the allowed workspace. Requires file_writing_enabled.";
    private readonly IWorkspacePathGuard _guard;
    private readonly IToolRuntimeOptions _options;

    public WriteTextFileTool(IWorkspacePathGuard guard, IToolRuntimeOptions? options = null)
    {
        _guard = guard;
        _options = options ?? SafetyPolicy.RequiredToolOptions;
    }

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        if (!_options.FileWritingEnabled) return new ToolResult(Name, false, "", "File writing is disabled by config.", FailureClass.AuthorizationFailure);
        var requested = args.GetValueOrDefault("path")?.ToString();
        var content   = args.GetValueOrDefault("content")?.ToString();
        if (string.IsNullOrEmpty(requested)) return new ToolResult(Name, false, "", "Missing required argument: path", FailureClass.ValidationFailure);
        if (content is null)                 return new ToolResult(Name, false, "", "Missing required argument: content", FailureClass.ValidationFailure);
        string safePath;
        try { safePath = _guard.ResolveSafePath(requested); }
        catch (Exception e) { return new ToolResult(Name, false, "", e.Message, ToolFailure.Classify(e)); }
        if (_guard.IsBlockedPath(safePath)) return new ToolResult(Name, false, "", "Refusing to write to blocked internal/system path.", FailureClass.AuthorizationFailure);
        var suffix = Path.GetExtension(safePath).ToLowerInvariant();
        if (_options.BlockedFileSuffixes.Contains(suffix)) return new ToolResult(Name, false, "", $"Refusing to write blocked file type: {suffix}", FailureClass.AuthorizationFailure);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
            File.WriteAllText(safePath, content);
            return new ToolResult(Name, true, $"Written {content.Length} chars to {safePath}");
        }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Could not write file: {e.Message}", ToolFailure.Classify(e)); }
    }
}
