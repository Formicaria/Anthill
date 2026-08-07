using System.Diagnostics;
using Anthill.SDK.Common;
using Anthill.SDK.Contracts;
using Anthill.SDK.Security;
using Anthill.SDK.Tools;

namespace Anthill.Modules.Tools;

/// <summary>
/// The highest-consequence tool in the colony, and the one whose gate matters most. Off by default.
/// </summary>
public sealed class ShellCommandTool : ITool
{
    private readonly IWorkspacePathGuard _guard;
    private readonly IToolRuntimeOptions _options;

    /// <param name="guard">
    /// Supplies the working directory. v3.8.16 — this used to be <c>new WorkspacePathGuard().Root</c>
    /// constructed inline on every call, which resolved to the same configured root by coincidence
    /// rather than by design. The injected guard is that root, named.
    /// </param>
    public ShellCommandTool(IWorkspacePathGuard guard, IToolRuntimeOptions? options = null)
    {
        _guard = guard;
        _options = options ?? SafetyPolicy.RequiredToolOptions;
    }

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
                WorkingDirectory = _guard.Root,
            };
            foreach (var arg in parts.Skip(1)) psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(30_000)) { try { proc.Kill(true); } catch { } return new ToolResult(Name, false, "", "Shell command timed out.", FailureClass.Timeout); }
            return new ToolResult(Name, proc.ExitCode == 0, stdout.Trim(), string.IsNullOrEmpty(stderr.Trim()) ? null : stderr.Trim());
        }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Shell command failed: {e.Message}", ToolFailure.Classify(e)); }
    }
}

public sealed class WebSearchTool : ITool
{
    private readonly IToolRuntimeOptions _options;
    private readonly ISsrfPolicy _ssrf;

    /// <param name="ssrf">
    /// The outbound blocklist this tool drops results against. v3.8.18 — added because
    /// <c>IsBlockedOutboundUrl</c> was called with no policy, so the SSRF guard on the colony's only
    /// outbound-fetching tool read process-global state while the tool's enable gate read its
    /// injected contract. Same defect as the patch validator, on the other end of the module.
    /// </param>
    public WebSearchTool(IToolRuntimeOptions? options = null, ISsrfPolicy? ssrf = null)
    {
        _options = options ?? SafetyPolicy.RequiredToolOptions;
        _ssrf = ssrf ?? SafetyPolicy.Ssrf;
    }
    public string Name => "web_search";
    public string Description => "Read-only web search tool for current/public information. Disabled unless web search is enabled.";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(ToolLimits.WebSearchTimeoutSeconds) };

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        if (!_options.WebSearchEnabled)
            return new ToolResult(Name, false, "", "Web search is disabled by config. Enable read-only external research to use it.", FailureClass.AuthorizationFailure);
        var query = (args.GetValueOrDefault("query")?.ToString() ?? "").Trim();
        var maxResults = Math.Max(1, Math.Min(
            int.TryParse(args.GetValueOrDefault("max_results")?.ToString(), out var mr) ? mr : ToolLimits.MaxWebResults,
            ToolLimits.MaxWebResults));
        if (query.Length == 0) return new ToolResult(Name, false, "", "Missing required argument: query", FailureClass.ValidationFailure);
        try { return DuckDuckGoHtmlSearch(query, maxResults); }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Web search failed: {e.Message}", ToolFailure.Classify(e)); }
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
            if (UrlSafety.IsBlockedOutboundUrl(cleanUrl, _ssrf)) continue;
            results.Add(new() { ["title"] = title, ["url"] = cleanUrl, ["snippet"] = "", ["source"] = ToolLimits.WebSearchProvider });
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
