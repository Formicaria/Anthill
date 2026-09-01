using System.Diagnostics;
using System.Text;
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

        // v0.3.8.110 — `dotnet` IS AN INTERPRETER, AND THE ALLOWLIST TREATED IT AS A READER.
        //
        // THE RESIDUAL THIS CLOSES, named in PLAN.md §5 as "`dotnet` on the shell allowlist as
        // arbitrary workspace code execution". Every other entry in `SafeCommands` reads: `ls`,
        // `cat`, `grep` and their siblings cannot execute what they find. `dotnet` can, and the
        // allowlist matched on the PROGRAM alone — so `dotnet run`, `dotnet exec whatever.dll`, and
        // `dotnet build` of a project carrying an MSBuild `Exec` task all passed every check in this
        // method and ran workspace-controlled code inside the workspace. An allowlist whose entries
        // are not equivalent in what they GRANT is a list of names, not a policy.
        //
        // The subcommand is allowlisted rather than the program removed, because `dotnet --version`
        // is a legitimate and pinned capability — it is how a colony answers whether the SDK is
        // present at all. What is refused is every road from `dotnet` to running code the workspace
        // supplied.
        //
        // BUILD IS REFUSED TOO, and that is the one that looks over-strict. A project file chooses
        // what happens during a build: `<Exec Command="..."/>` and a build-time task are ordinary
        // MSBuild, and a mission's workspace is a tree the colony's own agents write into. Admitting
        // `build` here would mean the shell tool executes whatever the tree says to execute, which
        // is the property this whole change exists to remove. The verification lane already builds
        // and tests — through `run_allowlisted_check`, whose catalog is declared outside the
        // workspace and cannot be edited by anything running in it. That is the difference, and it
        // is why one of these two is deterministic evidence and the other is a shell.
        if (baseCommand == "dotnet" && DotnetSubcommandRefusal(parts) is { } dotnetRefusal)
            return new ToolResult(Name, false, "", dotnetRefusal, FailureClass.AuthorizationFailure);

        // v0.3.8.59 (PLAN.md §1b S2) — THE ARGUMENTS ARE CONTAINED, because the cwd never was.
        //
        // Setting WorkingDirectory does not sandbox a process. It sets where RELATIVE paths resolve
        // and has no bearing on absolute ones, so `cat /etc/passwd`, `grep -r secret /` and
        // `find / -name '*.key'` all ran exactly as written — three of the nine allowlisted commands
        // reading anything the colony's user could read. The allowlist was doing the work of a
        // sandbox and was never that.
        if (DangerousFlag(baseCommand, parts) is { } dangerous)
            return new ToolResult(Name, false, "",
                $"'{dangerous}' can execute or delete and is refused: the allowlist admits {baseCommand} "
              + "as a way to READ the workspace.", FailureClass.AuthorizationFailure);

        foreach (var argument in parts.Skip(1))
        {
            if (PathLikeArgument(argument) is not { } candidate) continue;

            // The guard THROWS on refusal by design — IWorkspacePathGuard says so, on the grounds
            // that a bool a caller forgets to check is worse than an exception. Caught here rather
            // than adding a non-throwing overload for one call site, which would give the interface
            // two answers to one question.
            try { _guard.ResolveSafePath(candidate); }
            catch (UnauthorizedAccessException)
            {
                return new ToolResult(Name, false, "",
                    $"Argument '{argument}' points outside the workspace. Shell commands run inside "
                  + $"{_guard.EffectiveRoot} and may only name paths within it.",
                    FailureClass.AuthorizationFailure);
            }
        }

        try
        {
            var psi = new ProcessStartInfo(parts[0])
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                // EffectiveRoot, not Root: inside a mission the workspace is the mission's disposable
                // tree, and running in the configured root instead pointed every shell command at the
                // live checkout the mission exists to stay out of.
                WorkingDirectory = _guard.EffectiveRoot,
                CreateNoWindow = true,   // v0.3.8.53: never flash a console from the desktop shell
                StandardOutputEncoding = Encoding.UTF8,   // v0.3.8.55: children emit UTF-8, not the OS codepage
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in parts.Skip(1)) psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi)!;

            // v0.3.8.59 (PLAN.md §1b S7, unavoidably here) — THE TIMEOUT CAN ACTUALLY FIRE.
            //
            // This read `ReadToEnd()` on stdout, then stderr, and only then called
            // `WaitForExit(30_000)`. Both halves were broken. A process that never exits blocks
            // forever in the FIRST ReadToEnd, so execution never reached the timeout that was
            // supposed to bound it — the guard sat downstream of the thing that hangs. And reading
            // the streams sequentially deadlocks whenever the child fills its stderr pipe while this
            // side is still draining stdout: each waits for the other, neither is timed out.
            //
            // Both pipes are now drained CONCURRENTLY and the wait is what bounds the whole thing.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(30_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new ToolResult(Name, false, "", "Shell command timed out after 30s and was "
                  + "terminated with its child processes.", FailureClass.Timeout);
            }

            // The child is gone, so both reads complete; the bound is belt-and-braces for a
            // grandchild holding the pipe open after its parent exited.
            var stdout = Drain(stdoutTask);
            var stderr = Drain(stderrTask);

            return new ToolResult(Name, proc.ExitCode == 0, Cap(stdout),
                string.IsNullOrEmpty(stderr.Trim()) ? null : Cap(stderr));
        }
        catch (Exception e) { return new ToolResult(Name, false, "", $"Shell command failed: {e.Message}", ToolFailure.Classify(e)); }
    }

    /// <summary>
    /// Read what a finished process wrote, without hanging on an inherited pipe. A grandchild that
    /// outlives its parent keeps the handle open, so the stream does not reach EOF even though the
    /// process this tool started has exited.
    /// </summary>
    private static string Drain(System.Threading.Tasks.Task<string> read)
    {
        try { return read.Wait(TimeSpan.FromSeconds(5)) ? read.Result : ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Output is BOUNDED. `find /` on a large tree returns hundreds of megabytes, and the whole of
    /// it travelled into a ToolResult, into an artifact and into a model prompt. Unbounded output is
    /// a memory failure at best and an eviction of everything else in the context at worst.
    /// </summary>
    private static string Cap(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= MaxOutputChars
            ? trimmed
            : trimmed[..MaxOutputChars] + $"\n… [truncated at {MaxOutputChars} characters]";
    }

    private const int MaxOutputChars = 20_000;

    /// <summary>
    /// Flags that turn a reading command into a writing or executing one.
    ///
    /// `find . -exec rm {} ;` is not traversal and passes every containment check above — the path
    /// is the workspace and the damage is done by the flag. `-delete` is the same in one word. The
    /// allowlist admits these commands as a way to LOOK at the workspace, and this keeps them to it.
    /// </summary>
    private static string? DangerousFlag(string baseCommand, IReadOnlyList<string> parts)
    {
        if (baseCommand is not ("find" or "grep")) return null;

        string[] refused = { "-exec", "-execdir", "-ok", "-okdir", "-delete", "-fprintf", "-fls", "-fprint" };
        return parts.Skip(1).FirstOrDefault(p =>
            refused.Contains(p.Split('=')[0], StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What `dotnet` may be asked to do here. v0.3.8.110.
    ///
    /// AN ALLOWLIST, NOT A DENYLIST, and the direction is the whole security value. A denylist of
    /// `run`/`exec`/`tool` would be a list of the roads to code execution somebody had thought of;
    /// the SDK adds verbs, `dotnet <toolname>` dispatches to an installed global tool by name alone,
    /// and `dotnet whatever.dll` executes an assembly with no verb at all. Every one of those is a
    /// road this list has never heard of and refuses anyway.
    ///
    /// What is admitted is the set that reports and cannot execute what the workspace supplied:
    /// `--version`, `--info`, `--list-sdks`, `--list-runtimes`, `--help`. That is enough to answer
    /// "is the SDK here and which one", which is the reason `dotnet` was on the allowlist at all.
    ///
    /// Returns null when the invocation is permitted, and the refusal sentence otherwise.
    /// </summary>
    private static string? DotnetSubcommandRefusal(IReadOnlyList<string> parts)
    {
        string[] permitted = { "--version", "--info", "--list-sdks", "--list-runtimes", "--help", "-h" };

        var subcommand = parts.Count > 1 ? parts[1].Trim() : "";
        if (subcommand.Length == 0)
            return "'dotnet' with no arguments is refused: the allowlist admits it only as a way to "
                 + "REPORT the SDK's presence and version.";

        if (permitted.Contains(subcommand, StringComparer.OrdinalIgnoreCase)) return null;

        return $"'dotnet {subcommand}' is refused. `dotnet` is an interpreter, and every other entry "
             + "on this allowlist can only read — admitting a verb that runs code the workspace "
             + "supplied would make the allowlist a list of names rather than a policy. Permitted: "
             + string.Join(", ", permitted) + ". To build or test, use `run_allowlisted_check`, "
             + "whose catalog is declared outside the workspace and cannot be edited by anything "
             + "running inside it.";
    }

    /// <summary>
    /// Which arguments are PATHS, and therefore have to be contained.
    ///
    /// Deliberately conservative in the direction of checking too much rather than too little: an
    /// argument is treated as a path if it is rooted, contains a separator, or contains `..`. A bare
    /// token — `Release`, `secret`, `*.key` — names no location and is left alone, so
    /// `grep -r secret .` still works while `grep -r secret /` does not.
    ///
    /// `--flag=value` is split, because the path in `--output=/etc/x` is on the right of the equals
    /// and checking the whole token would find no separator at the front and wave it through.
    /// </summary>
    private static string? PathLikeArgument(string argument)
    {
        var value = argument;

        // A leading '-' is a flag on every platform the colony runs on. Split once, so `-name` stays
        // a flag and `--out=/etc/passwd` is checked as `/etc/passwd`.
        if (value.StartsWith('-'))
        {
            var equals = value.IndexOf('=');
            if (equals < 0) return null;
            value = value[(equals + 1)..];
            if (value.Length == 0) return null;
        }

        var looksLikePath = Path.IsPathRooted(value)
            || value.Contains('/') || value.Contains('\\')
            || value.Split('/', '\\').Contains("..");

        return looksLikePath ? value : null;
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
