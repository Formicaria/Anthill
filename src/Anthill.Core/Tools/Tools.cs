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

/// <summary>A read-mostly capability the ants can invoke. Every tool fails closed when its config gate is off.</summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }
    ToolResult Run(IReadOnlyDictionary<string, object?> args);
}

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

    public ToolResult RunTool(string name, string? missionId = null, string? taskId = null, string? antName = null,
        Dictionary<string, object?>? args = null)
    {
        args ??= new();
        if (missionId is not null)
            _memory.LogEvent(missionId, "tool_called", $"Tool called: {name}", taskId, antName,
                new() { ["tool_name"] = name, ["arguments"] = SafeMetadata(args) });

        if (!_tools.TryGetValue(name, out var tool))
        {
            var missing = new ToolResult(name, false, "", $"Tool not found or not registered: {name}");
            if (missionId is not null) LogToolResult(missionId, taskId, antName, missing);
            return missing;
        }

        ToolResult result;
        try
        {
            result = tool.Run(args);
        }
        catch (Exception error)
        {
            result = new ToolResult(name, false, "", $"Tool execution failed: {error.Message}");
        }

        if (missionId is not null)
        {
            LogToolResult(missionId, taskId, antName, result);
            _memory.UpdatePheromoneTrail($"tool:{name}", "tool", result.Success, result.Success ? 0.02 : -0.04,
                new() { ["mission_id"] = missionId, ["task_id"] = taskId, ["ant_name"] = antName });
        }
        return result;
    }

    private void LogToolResult(string missionId, string? taskId, string? antName, ToolResult result) =>
        _memory.LogEvent(missionId, result.Success ? "tool_completed" : "tool_failed",
            $"Tool {(result.Success ? "completed" : "failed")}: {result.ToolName}", taskId, antName,
            new()
            {
                ["tool_name"] = result.ToolName, ["success"] = result.Success, ["error"] = result.Error,
                ["output_preview"] = TextUtil.Truncate(result.Output, 500),
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
            ["script_directory"] = AnthillRuntime.ScriptDir,
            ["allowed_workspace_root"] = new WorkspacePathGuard().Root,
            ["file_tools_enabled"] = AnthillRuntime.EnableFileTools,
            ["shell_tool_enabled"] = AnthillRuntime.EnableShellTool,
            ["patch_application_enabled"] = AnthillRuntime.EnablePatchApplication,
            ["file_writing_enabled"] = AnthillRuntime.EnableFileWriting,
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
    public DirectoryListTool(WorkspacePathGuard guard) => _guard = guard;

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        if (!AnthillRuntime.EnableFileTools) return new ToolResult(Name, false, "", "File tools are disabled by config.");
        var requested = (args.GetValueOrDefault("path")?.ToString()) ?? ".";
        string safePath;
        try { safePath = _guard.ResolveSafePath(requested); }
        catch (Exception e) { return new ToolResult(Name, false, "", e.Message); }
        if (_guard.IsBlockedPath(safePath)) return new ToolResult(Name, false, "", "Refusing to list blocked internal/system path.");
        if (!Directory.Exists(safePath)) return new ToolResult(Name, false, "", $"Directory does not exist: {safePath}");

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
    public ReadTextFileTool(WorkspacePathGuard guard) => _guard = guard;

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        if (!AnthillRuntime.EnableFileTools) return new ToolResult(Name, false, "", "File tools are disabled by config.");
        var requested = args.GetValueOrDefault("path")?.ToString();
        if (string.IsNullOrEmpty(requested)) return new ToolResult(Name, false, "", "Missing required argument: path");
        string safePath;
        try { safePath = _guard.ResolveSafePath(requested); }
        catch (Exception e) { return new ToolResult(Name, false, "", e.Message); }
        if (_guard.IsBlockedPath(safePath)) return new ToolResult(Name, false, "", "Refusing to read from blocked internal/system path.");
        var suffix = Path.GetExtension(safePath).ToLowerInvariant();
        if (AnthillRuntime.BlockedFileSuffixes.Contains(suffix)) return new ToolResult(Name, false, "", $"Refusing to read blocked file type: {suffix}");
        if (!File.Exists(safePath)) return new ToolResult(Name, false, "", $"File does not exist: {safePath}");
        if (!AnthillRuntime.PatchAllowedSuffixes.Contains(suffix)) return new ToolResult(Name, false, "", $"Refusing to read unsupported file type: {suffix}");
        string content;
        try { content = File.ReadAllText(safePath); }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Could not read file: {e.Message}"); }
        content = TextUtil.Truncate(content, AnthillRuntime.MaxFileReadChars, $"...[file truncated after {AnthillRuntime.MaxFileReadChars} characters]");
        return new ToolResult(Name, true, content);
    }
}

public sealed class WriteTextFileTool : ITool
{
    public string Name => "write_text_file";
    public string Description => "Writes or creates a text file inside the allowed workspace. Requires file_writing_enabled.";
    private readonly WorkspacePathGuard _guard;
    public WriteTextFileTool(WorkspacePathGuard guard) => _guard = guard;

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        if (!AnthillRuntime.EnableFileWriting) return new ToolResult(Name, false, "", "File writing is disabled by config.");
        var requested = args.GetValueOrDefault("path")?.ToString();
        var content   = args.GetValueOrDefault("content")?.ToString();
        if (string.IsNullOrEmpty(requested)) return new ToolResult(Name, false, "", "Missing required argument: path");
        if (content is null)                 return new ToolResult(Name, false, "", "Missing required argument: content");
        string safePath;
        try { safePath = _guard.ResolveSafePath(requested); }
        catch (Exception e) { return new ToolResult(Name, false, "", e.Message); }
        if (_guard.IsBlockedPath(safePath)) return new ToolResult(Name, false, "", "Refusing to write to blocked internal/system path.");
        var suffix = Path.GetExtension(safePath).ToLowerInvariant();
        if (AnthillRuntime.BlockedFileSuffixes.Contains(suffix)) return new ToolResult(Name, false, "", $"Refusing to write blocked file type: {suffix}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
            File.WriteAllText(safePath, content);
            return new ToolResult(Name, true, $"Written {content.Length} chars to {safePath}");
        }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Could not write file: {e.Message}"); }
    }
}

public sealed class ShellCommandTool : ITool
{
    public string Name => "shell_command";
    public string Description => "Optional minimal shell command tool. Disabled by default. High risk.";
    private static readonly HashSet<string> SafeCommands = new() { "dir", "ls", "pwd", "echo", "dotnet", "type", "cat", "find", "grep" };

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        if (!AnthillRuntime.EnableShellTool) return new ToolResult(Name, false, "", "Shell tool is disabled by config.");
        var command = (args.GetValueOrDefault("command")?.ToString() ?? "").Trim();
        if (command.Length == 0) return new ToolResult(Name, false, "", "Missing required argument: command");
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return new ToolResult(Name, false, "", "Empty command after parsing.");
        var baseCommand = parts[0].ToLowerInvariant();
        if (!SafeCommands.Contains(baseCommand)) return new ToolResult(Name, false, "", $"Command is not allowlisted: {baseCommand}");
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
            if (!proc.WaitForExit(30_000)) { try { proc.Kill(true); } catch { } return new ToolResult(Name, false, "", "Shell command timed out."); }
            return new ToolResult(Name, proc.ExitCode == 0, stdout.Trim(), string.IsNullOrEmpty(stderr.Trim()) ? null : stderr.Trim());
        }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Shell command failed: {e.Message}"); }
    }
}

public sealed class WebSearchTool : ITool
{
    public string Name => "web_search";
    public string Description => "Read-only web search tool for current/public information. Disabled unless web search is enabled.";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(AnthillRuntime.WebSearchTimeoutSeconds) };

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        if (!AnthillRuntime.EnableWebSearch)
            return new ToolResult(Name, false, "", "Web search is disabled by config. Enable read-only external research to use it.");
        var query = (args.GetValueOrDefault("query")?.ToString() ?? "").Trim();
        var maxResults = Math.Max(1, Math.Min(
            int.TryParse(args.GetValueOrDefault("max_results")?.ToString(), out var mr) ? mr : AnthillRuntime.MaxWebResults,
            AnthillRuntime.MaxWebResults));
        if (query.Length == 0) return new ToolResult(Name, false, "", "Missing required argument: query");
        try { return DuckDuckGoHtmlSearch(query, maxResults); }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Web search failed: {e.Message}"); }
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
    public ApplyPatchTool(WorkspacePathGuard guard) => _guard = guard;

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        if (!AnthillRuntime.EnablePatchApplication) return new ToolResult(Name, false, "", "Patch application is disabled by config.");
        if (!AnthillRuntime.EnableFileWriting) return new ToolResult(Name, false, "", "File writing is disabled by config.");
        if (args.GetValueOrDefault("patch") is not Dictionary<string, object?> patch)
            return new ToolResult(Name, false, "", "Missing required dict argument: patch");

        var changeType = (patch.GetValueOrDefault("change_type")?.ToString() ?? "").Trim().ToLowerInvariant();
        var filePath = (patch.GetValueOrDefault("file_path")?.ToString() ?? "").Trim();
        var oldContent = patch.GetValueOrDefault("old_content") as string;
        var newContent = patch.GetValueOrDefault("new_content") as string;

        string safePath;
        try { Validation.ValidateSafePatchPath(filePath); safePath = _guard.ResolveSafePath(filePath); }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Unsafe patch path: {e.Message}"); }
        if (_guard.IsBlockedPath(safePath)) return new ToolResult(Name, false, "", "Refusing to patch blocked internal/system path.");
        if (changeType is not ("add" or "modify"))
            return new ToolResult(Name, false, "", $"ANTHILL currently supports only add and modify patches. Refusing change_type: {changeType}");
        if (string.IsNullOrEmpty(newContent)) return new ToolResult(Name, false, "", "Patch new_content is required and must be non-empty.");

        try
        {
            return changeType switch
            {
                "add" => ApplyAdd(safePath, newContent),
                "modify" when string.IsNullOrEmpty(oldContent) => new ToolResult(Name, false, "", "MODIFY patches require old_content for exact replacement."),
                "modify" => ApplyModify(safePath, oldContent!, newContent),
                _ => new ToolResult(Name, false, "", $"Unsupported change_type: {changeType}"),
            };
        }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Patch application failed: {e.Message}"); }
    }

    private string? BackupFile(string path)
    {
        if (!File.Exists(path)) return null;
        var backupRoot = Path.GetFullPath(Path.Combine(AnthillRuntime.ScriptDir, AnthillRuntime.BackupDir));
        Directory.CreateDirectory(backupRoot);
        var safeName = Path.GetRelativePath(new WorkspacePathGuard().Root, path).Replace("\\", "__").Replace("/", "__");
        var backupPath = Path.Combine(backupRoot, $"{safeName}.{AnthillTime.TimestampId()}.bak");
        File.Copy(path, backupPath, overwrite: true);
        return backupPath;
    }

    private ToolResult ApplyAdd(string safePath, string newContent)
    {
        if (File.Exists(safePath)) return new ToolResult(Name, false, "", $"ADD refused because file already exists: {safePath}");
        Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
        File.WriteAllText(safePath, newContent, new UTF8Encoding(false));
        return new ToolResult(Name, true, Json.Dumps(new { action = "add", file_path = safePath, backup_path = (string?)null }, indented: true));
    }

    private ToolResult ApplyModify(string safePath, string oldContent, string newContent)
    {
        if (!File.Exists(safePath)) return new ToolResult(Name, false, "", $"MODIFY refused because file does not exist: {safePath}");
        var current = File.ReadAllText(safePath);
        var occurrences = CountOccurrences(current, oldContent);
        if (occurrences == 0) return new ToolResult(Name, false, "", "MODIFY refused because old_content was not found exactly in the target file.");
        if (occurrences > 1) return new ToolResult(Name, false, "", $"MODIFY refused because old_content appears {occurrences} times. Patch must be unambiguous.");
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
