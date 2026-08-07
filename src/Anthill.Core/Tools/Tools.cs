using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Security;

namespace Anthill.Core.Tools;

/// <summary>
/// Tool dispatch + observability. Logs each call/result as events, hardens metadata,
/// and reinforces a per-tool pheromone trail by outcome. Mirrors the Python ToolRegistry,
/// including the success/failure strength deltas.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new();
    private readonly SqliteMemory _memory;

    public ToolRegistry(SqliteMemory memory) => _memory = memory;

    public void Register(ITool tool) => _tools[tool.Name] = tool;

    /// <summary>
    /// v3.4.1: remove a tool from THIS run's registry. Exists for operator-defined tools, which are
    /// the only kind that can stop existing while the process is alive — deleting a definition has
    /// to take the tool out of the registry too, or a model keeps being offered a tool whose
    /// definition is gone and every call fails for a reason the transcript cannot show.
    ///
    /// Built-ins are refused. Registration composes the run's capabilities from config, and a
    /// runtime call able to strip <c>apply_patch</c> out of the registry would be a second, unaudited
    /// way to change what the colony can do.
    /// </summary>
    public bool Unregister(string name)
    {
        if (ToolInventory.Implemented.Contains(name ?? "")) return false;
        return _tools.Remove(name ?? "");
    }

    /// <summary>
    /// The tools actually registered for this run. v3.1.0: <see cref="Configuration.RuntimeProfile"/>
    /// reports the run's tool grants from THIS rather than re-deriving them from the capability
    /// gates — so the profile describes what was built, not what the gates imply should have been.
    /// </summary>
    public IReadOnlyCollection<string> Names => _tools.Keys.ToList();

    /// <summary>
    /// The registered tools themselves, for anything that needs more than their names —
    /// <see cref="ToolSchemaProjection"/> needs each one's description and argument schema to offer
    /// it to a model. Read-only: registration stays the single way a tool enters the registry.
    /// </summary>
    public IReadOnlyCollection<ITool> Tools => _tools.Values.ToList();

    public ToolResult RunTool(string name, string? missionId = null, string? taskId = null, string? antName = null,
        Dictionary<string, object?>? args = null)
    {
        args ??= new();
        if (missionId is not null)
            _memory.LogEvent(missionId, "tool_called", $"Tool called: {name}", taskId, antName,
                new() { ["tool_name"] = name, ["arguments"] = SafeMetadata(args) });

        if (!_tools.TryGetValue(name, out var tool))
        {
            // ValidationFailure, not a defect: the CALL named something that does not exist, and a
            // model can correct that by choosing from the tools it was actually offered.
            var missing = new ToolResult(name, false, "", $"Tool not found or not registered: {name}",
                FailureClass.ValidationFailure);
            if (missionId is not null) LogToolResult(missionId, taskId, antName, missing);
            return missing;
        }

        // Execution framework Stage B: enforce the caller's declared boundary BEFORE the tool runs.
        // A denial is a structured failure with an audit event and zero side effects; spoofing an
        // unknown ant name is refused outright.
        var decision = ToolAuthorization.Evaluate(antName, name);
        if (!decision.Allowed)
        {
            // The class carries the denial now; the `authorization_denied:` prefix stays only as
            // human-readable text. Nothing may recover the status by matching that prefix — the
            // typed field is the one callers branch on.
            var denied = new ToolResult(name, false, "", $"authorization_denied: {decision.Reason}",
                FailureClass.AuthorizationFailure);
            if (missionId is not null)
                _memory.LogEvent(missionId, "tool_denied", $"Tool DENIED: {name}", taskId, antName,
                    new() { ["tool_name"] = name, ["ant_name"] = antName, ["reason"] = decision.Reason });
            return denied;
        }

        // v3.7.0 — the escalation gate, at the SAME chokepoint as authorization.
        //
        // Deliberately after ToolAuthorization and before execution. Authorization asks "may this
        // ROLE ever do this"; escalation asks "has the OPERATOR agreed to this happening now". They
        // are different questions with different answers, and a tool must pass both — but there is
        // no point asking the operator about something the role could never do anyway.
        //
        // Outside a conversation this returns null and nothing changes, which is why missions run
        // exactly as they did.
        var escalation = Conversations.ConversationScope.Evaluate(name);
        if (escalation is { Allowed: false })
        {
            var refused = new ToolResult(name, false, "",
                $"escalation_refused: {escalation.Reason}", FailureClass.AuthorizationFailure);
            if (missionId is not null)
                _memory.LogEvent(missionId, "escalation_refused",
                    $"Tool REFUSED pending operator decision: {name}", taskId, antName,
                    new() { ["tool_name"] = name, ["decision_id"] = escalation.Id,
                            ["policy"] = escalation.Policy.ToString(), ["reason"] = escalation.Reason });
            return refused;
        }

        ToolResult result;
        try
        {
            result = tool.Run(args);
        }
        catch (Exception error)
        {
            result = new ToolResult(name, false, "", $"Tool execution failed: {error.Message}",
                ClassifyThrown(error));
        }

        if (missionId is not null)
        {
            LogToolResult(missionId, taskId, antName, result);
            _memory.UpdatePheromoneTrail($"tool:{name}", "tool", result.Success, result.Success ? 0.02 : -0.04,
                new() { ["mission_id"] = missionId, ["task_id"] = taskId, ["ant_name"] = antName });
        }
        return result;
    }

    /// <summary>
    /// Classify an exception that escaped a tool.
    ///
    /// This is a fallback, not the intended path: a tool that knows why it failed should say so by
    /// returning a classified <see cref="ToolResult"/>, because the tool knows things the exception
    /// type does not. What this catches is the tool that threw without ever considering failure —
    /// and for that, the exception TYPE is the only honest evidence available.
    ///
    /// Anything unrecognised is an InternalDefect and therefore NOT retryable. Guessing "transient"
    /// for an unknown fault is how a deterministic crash becomes a retry storm.
    /// </summary>
    internal static FailureClass ClassifyThrown(Exception error) => error switch
    {
        OperationCanceledException or TimeoutException => FailureClass.Timeout,
        HttpRequestException or IOException => FailureClass.TransientProviderFailure,
        UnauthorizedAccessException => FailureClass.AuthorizationFailure,
        // The model chose the arguments, so a rejected argument is something it can fix and retry.
        ArgumentException or FormatException or JsonException => FailureClass.ValidationFailure,
        _ => FailureClass.InternalDefect,
    };

    private void LogToolResult(string missionId, string? taskId, string? antName, ToolResult result) =>
        _memory.LogEvent(missionId, result.Success ? "tool_completed" : "tool_failed",
            $"Tool {(result.Success ? "completed" : "failed")}: {result.ToolName}", taskId, antName,
            new()
            {
                ["tool_name"] = result.ToolName, ["success"] = result.Success, ["error"] = result.Error,
                ["output_preview"] = TextUtil.Truncate(result.Output, 500),
                // The class is on the EVENT too, so "which tools fail, and how" is a query rather
                // than an exercise in grepping error prose out of a metadata blob.
                ["failure_class"] = result.Failure.ToString(), ["retryable"] = result.Retryable,
            });

    private static Dictionary<string, object?> SafeMetadata(IReadOnlyDictionary<string, object?> metadata)
    {
        var safe = new Dictionary<string, object?>();
        foreach (var (key, value) in metadata)
            safe[key] = value is string or int or long or double or bool or null ? value : value.ToString();
        return safe;
    }

    public string DescribeTools() => _tools.Count == 0
        ? "No tools registered."
        : string.Join("\n", _tools.Select(kv => $"- {kv.Key}: {kv.Value.Description}"));
}

public sealed class SystemInfoTool : ITool
{
    // v3.8.11 — the runtime gates arrive through an interface, read LIVE on every call. This is
    // the colony's SECOND gate: RuntimeOptions already decided whether to register this tool,
    // and this re-check is what stops one that somehow reached the registry from acting.
    // Capturing the values would quietly collapse the two into one.
    private readonly IToolRuntimeOptions _options;

    public SystemInfoTool(IToolRuntimeOptions? options = null) => _options = options ?? ToolRuntime.Live;
    public string Name => "system_info";
    public string Description => "Read-only tool that returns basic OS, runtime, and workspace information.";

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        var info = new Dictionary<string, object?>
        {
            ["os"] = RuntimeInformation.OSDescription,
            ["os_architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["machine"] = Environment.MachineName,
            ["current_working_directory"] = Directory.GetCurrentDirectory(),
            ["script_directory"] = _options.ScriptDirectory,
            ["allowed_workspace_root"] = new WorkspacePathGuard().Root,
            ["file_tools_enabled"] = _options.FileToolsEnabled,
            ["shell_tool_enabled"] = _options.ShellToolEnabled,
            ["patch_application_enabled"] = _options.PatchApplicationEnabled,
            ["file_writing_enabled"] = _options.FileWritingEnabled,
            ["parallel_execution_enabled"] = AnthillRuntime.EnableParallelExecution,
            ["max_parallel_workers"] = AnthillRuntime.MaxParallelWorkers,
            ["fts_memory_enabled"] = AnthillRuntime.EnableFtsMemory,
            ["native_kernel"] = Native.NativeKernel.UsingNative ? "active" : "managed-fallback",
        };
        return new ToolResult(Name, true, Json.Dumps(info, indented: true));
    }
}

public sealed class DirectoryListTool : ITool
{
    public string Name => "list_directory";
    public string Description => "Read-only tool that lists files and folders inside the allowed workspace.";
    private readonly WorkspacePathGuard _guard;
    // v3.8.11 — the runtime gates arrive through an interface, read LIVE on every call. This is
    // the colony's SECOND gate: RuntimeOptions already decided whether to register this tool,
    // and this re-check is what stops one that somehow reached the registry from acting.
    // Capturing the values would quietly collapse the two into one.
    private readonly IToolRuntimeOptions _options;

    public DirectoryListTool(WorkspacePathGuard guard, IToolRuntimeOptions? options = null)
    {
        _guard = guard;
        _options = options ?? ToolRuntime.Live;
    }

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        if (!_options.FileToolsEnabled) return new ToolResult(Name, false, "", "File tools are disabled by config.", FailureClass.AuthorizationFailure);
        var requested = (args.GetValueOrDefault("path")?.ToString()) ?? ".";
        string safePath;
        try { safePath = _guard.ResolveSafePath(requested); }
        catch (Exception e) { return new ToolResult(Name, false, "", e.Message, ToolRegistry.ClassifyThrown(e)); }
        if (_guard.IsBlockedPath(safePath)) return new ToolResult(Name, false, "", "Refusing to list blocked internal/system path.", FailureClass.AuthorizationFailure);
        if (!Directory.Exists(safePath)) return new ToolResult(Name, false, "", $"Directory does not exist: {safePath}", FailureClass.ValidationFailure);

        var items = new List<string>();
        var entries = new DirectoryInfo(safePath).GetFileSystemInfos().OrderBy(p => p.Name.ToLowerInvariant()).ToList();
        for (var i = 0; i < entries.Count; i++)
        {
            if (i >= AnthillRuntime.MaxDirectoryItems) { items.Add($"...[truncated after {AnthillRuntime.MaxDirectoryItems} items]"); break; }
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
    private readonly WorkspacePathGuard _guard;
    // v3.8.11 — the runtime gates arrive through an interface, read LIVE on every call. This is
    // the colony's SECOND gate: RuntimeOptions already decided whether to register this tool,
    // and this re-check is what stops one that somehow reached the registry from acting.
    // Capturing the values would quietly collapse the two into one.
    private readonly IToolRuntimeOptions _options;

    public ReadTextFileTool(WorkspacePathGuard guard, IToolRuntimeOptions? options = null)
    {
        _guard = guard;
        _options = options ?? ToolRuntime.Live;
    }

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        if (!_options.FileToolsEnabled) return new ToolResult(Name, false, "", "File tools are disabled by config.", FailureClass.AuthorizationFailure);
        var requested = args.GetValueOrDefault("path")?.ToString();
        if (string.IsNullOrEmpty(requested)) return new ToolResult(Name, false, "", "Missing required argument: path", FailureClass.ValidationFailure);
        string safePath;
        try { safePath = _guard.ResolveSafePath(requested); }
        catch (Exception e) { return new ToolResult(Name, false, "", e.Message, ToolRegistry.ClassifyThrown(e)); }
        if (_guard.IsBlockedPath(safePath)) return new ToolResult(Name, false, "", "Refusing to read from blocked internal/system path.", FailureClass.AuthorizationFailure);
        var suffix = Path.GetExtension(safePath).ToLowerInvariant();
        if (_options.BlockedFileSuffixes.Contains(suffix)) return new ToolResult(Name, false, "", $"Refusing to read blocked file type: {suffix}", FailureClass.AuthorizationFailure);
        if (!File.Exists(safePath)) return new ToolResult(Name, false, "", $"File does not exist: {safePath}", FailureClass.ValidationFailure);
        if (!_options.PatchAllowedSuffixes.Contains(suffix)) return new ToolResult(Name, false, "", $"Refusing to read unsupported file type: {suffix}", FailureClass.AuthorizationFailure);
        string content;
        try { content = File.ReadAllText(safePath); }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Could not read file: {e.Message}", ToolRegistry.ClassifyThrown(e)); }
        content = TextUtil.Truncate(content, AnthillRuntime.MaxFileReadChars, $"...[file truncated after {AnthillRuntime.MaxFileReadChars} characters]");
        return new ToolResult(Name, true, content);
    }
}

public sealed class WriteTextFileTool : ITool
{
    public string Name => "write_text_file";
    public string Description => "Writes or creates a text file inside the allowed workspace. Requires file_writing_enabled.";
    private readonly WorkspacePathGuard _guard;
    // v3.8.11 — the runtime gates arrive through an interface, read LIVE on every call. This is
    // the colony's SECOND gate: RuntimeOptions already decided whether to register this tool,
    // and this re-check is what stops one that somehow reached the registry from acting.
    // Capturing the values would quietly collapse the two into one.
    private readonly IToolRuntimeOptions _options;

    public WriteTextFileTool(WorkspacePathGuard guard, IToolRuntimeOptions? options = null)
    {
        _guard = guard;
        _options = options ?? ToolRuntime.Live;
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
        catch (Exception e) { return new ToolResult(Name, false, "", e.Message, ToolRegistry.ClassifyThrown(e)); }
        if (_guard.IsBlockedPath(safePath)) return new ToolResult(Name, false, "", "Refusing to write to blocked internal/system path.", FailureClass.AuthorizationFailure);
        var suffix = Path.GetExtension(safePath).ToLowerInvariant();
        if (_options.BlockedFileSuffixes.Contains(suffix)) return new ToolResult(Name, false, "", $"Refusing to write blocked file type: {suffix}", FailureClass.AuthorizationFailure);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
            File.WriteAllText(safePath, content);
            return new ToolResult(Name, true, $"Written {content.Length} chars to {safePath}");
        }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Could not write file: {e.Message}", ToolRegistry.ClassifyThrown(e)); }
    }
}

public sealed class ShellCommandTool : ITool
{
    // v3.8.11 — the runtime gates arrive through an interface, read LIVE on every call. This is
    // the colony's SECOND gate: RuntimeOptions already decided whether to register this tool,
    // and this re-check is what stops one that somehow reached the registry from acting.
    // Capturing the values would quietly collapse the two into one.
    private readonly IToolRuntimeOptions _options;

    public ShellCommandTool(IToolRuntimeOptions? options = null) => _options = options ?? ToolRuntime.Live;
    public string Name => "shell_command";
    public string Description => "Optional minimal shell command tool. Disabled by default. High risk.";
    private static readonly HashSet<string> SafeCommands = new() { "dir", "ls", "pwd", "echo", "dotnet", "type", "cat", "find", "grep" };

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        if (!_options.ShellToolEnabled) return new ToolResult(Name, false, "", "Shell tool is disabled by config.", FailureClass.AuthorizationFailure);
        var command = (args.GetValueOrDefault("command")?.ToString() ?? "").Trim();
        if (command.Length == 0) return new ToolResult(Name, false, "", "Missing required argument: command", FailureClass.ValidationFailure);
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return new ToolResult(Name, false, "", "Empty command after parsing.", FailureClass.ValidationFailure);
        var baseCommand = parts[0].ToLowerInvariant();
        if (!SafeCommands.Contains(baseCommand)) return new ToolResult(Name, false, "", $"Command is not allowlisted: {baseCommand}", FailureClass.AuthorizationFailure);
        try
        {
            var psi = new ProcessStartInfo(parts[0])
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                WorkingDirectory = new WorkspacePathGuard().Root,
            };
            foreach (var arg in parts.Skip(1)) psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(30_000)) { try { proc.Kill(true); } catch { } return new ToolResult(Name, false, "", "Shell command timed out.", FailureClass.Timeout); }
            return new ToolResult(Name, proc.ExitCode == 0, stdout.Trim(), string.IsNullOrEmpty(stderr.Trim()) ? null : stderr.Trim());
        }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Shell command failed: {e.Message}", ToolRegistry.ClassifyThrown(e)); }
    }
}

public sealed class WebSearchTool : ITool
{
    // v3.8.11 — the runtime gates arrive through an interface, read LIVE on every call. This is
    // the colony's SECOND gate: RuntimeOptions already decided whether to register this tool,
    // and this re-check is what stops one that somehow reached the registry from acting.
    // Capturing the values would quietly collapse the two into one.
    private readonly IToolRuntimeOptions _options;

    public WebSearchTool(IToolRuntimeOptions? options = null) => _options = options ?? ToolRuntime.Live;
    public string Name => "web_search";
    public string Description => "Read-only web search tool for current/public information. Disabled unless web search is enabled.";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(AnthillRuntime.WebSearchTimeoutSeconds) };

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        if (!_options.WebSearchEnabled)
            return new ToolResult(Name, false, "", "Web search is disabled by config. Enable read-only external research to use it.", FailureClass.AuthorizationFailure);
        var query = (args.GetValueOrDefault("query")?.ToString() ?? "").Trim();
        var maxResults = Math.Max(1, Math.Min(
            int.TryParse(args.GetValueOrDefault("max_results")?.ToString(), out var mr) ? mr : AnthillRuntime.MaxWebResults,
            AnthillRuntime.MaxWebResults));
        if (query.Length == 0) return new ToolResult(Name, false, "", "Missing required argument: query", FailureClass.ValidationFailure);
        try { return DuckDuckGoHtmlSearch(query, maxResults); }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Web search failed: {e.Message}", ToolRegistry.ClassifyThrown(e)); }
    }

    private ToolResult DuckDuckGoHtmlSearch(string query, int maxResults)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://duckduckgo.com/html/?q={Uri.EscapeDataString(query)}");
        req.Headers.Add("User-Agent", "ANTHILL-Core/1.8 read-only research");
        using var response = Http.Send(req);
        response.EnsureSuccessStatusCode();
        var html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        var results = new List<Dictionary<string, string>>();
        var pattern = new System.Text.RegularExpressions.Regex(
            "<a[^>]+class=\"result__a\"[^>]+href=\"([^\"]+)\"[^>]*>(.*?)</a>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        foreach (System.Text.RegularExpressions.Match match in pattern.Matches(html))
        {
            var title = TextUtil.StripHtmlTags(match.Groups[2].Value);
            var rawUrl = match.Groups[1].Value;
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(rawUrl)) continue;
            var cleanUrl = UrlSafety.DecodeSearchUrl(rawUrl);
            // SSRF guard: drop any result resolving to a private/loopback/local host.
            if (UrlSafety.IsBlockedOutboundUrl(cleanUrl)) continue;
            results.Add(new() { ["title"] = title, ["url"] = cleanUrl, ["snippet"] = "", ["source"] = AnthillRuntime.WebSearchProvider });
            if (results.Count >= maxResults) break;
        }

        if (results.Count == 0)
        {
            var preview = TextUtil.Truncate(TextUtil.StripHtmlTags(html), 1000, "...[search page truncated]");
            return new ToolResult(Name, true, Json.Dumps(new { query, results = Array.Empty<object>(), preview }, indented: true));
        }
        return new ToolResult(Name, true, Json.Dumps(new { query, results }, indented: true));
    }
}

public sealed class ApplyPatchTool : ITool
{
    public string Name => "apply_patch";
    public string Description => "Approval-gated tool that applies safe ADD or MODIFY patch proposals with backups.";
    private readonly WorkspacePathGuard _guard;
    // v3.8.11 — the runtime gates arrive through an interface, read LIVE on every call. This is
    // the colony's SECOND gate: RuntimeOptions already decided whether to register this tool,
    // and this re-check is what stops one that somehow reached the registry from acting.
    // Capturing the values would quietly collapse the two into one.
    private readonly IToolRuntimeOptions _options;

    public ApplyPatchTool(WorkspacePathGuard guard, IToolRuntimeOptions? options = null)
    {
        _guard = guard;
        _options = options ?? ToolRuntime.Live;
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
        try { Validation.ValidateSafePatchPath(filePath); safePath = _guard.ResolveSafePath(filePath); }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Unsafe patch path: {e.Message}", ToolRegistry.ClassifyThrown(e)); }
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
        catch (Exception e) { return new ToolResult(Name, false, "", $"Patch application failed: {e.Message}", ToolRegistry.ClassifyThrown(e)); }
    }

    private string? BackupFile(string path)
    {
        if (!File.Exists(path)) return null;
        var backupRoot = Path.GetFullPath(Path.Combine(_options.ScriptDirectory, _options.BackupDirectory));
        Directory.CreateDirectory(backupRoot);
        var safeName = Path.GetRelativePath(new WorkspacePathGuard().Root, path).Replace("\\", "__").Replace("/", "__");
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
