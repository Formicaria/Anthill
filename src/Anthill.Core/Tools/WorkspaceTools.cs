using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Security;
using Anthill.Core.Workspaces;

namespace Anthill.Core.Tools;

/// <summary>
/// v3.5.0 — search the workspace an agent is actually working in.
///
/// This is the first of the two tools that were DECLARED by a contract and never built. The cost of
/// that was concrete rather than theoretical: <c>ToolAuthorization</c> short-circuits on contract
/// presence, so <c>ui_cartographer</c> — a role whose entire purpose is mapping a repository — was
/// allowed exactly three tools, one of which did not exist, and could therefore dispatch nothing.
/// It ran and produced no work, which reads as a weak model rather than a missing tool.
///
/// Deliberately NOT an index. v3.6.0 owns repository indexing and awareness; this is the bounded
/// literal-or-regex scan that a scoped workspace tool should be, and building it now means the role
/// works today rather than after another phase. When the index arrives it can back this same tool.
///
/// Every bound here exists because the OUTPUT goes into a model's context. An unbounded search over
/// a real repository does not fail — it returns forty thousand matches, fills the window, and the
/// agent loses the conversation it was having. Truncation is reported rather than silent, because a
/// model that cannot tell a complete result from a cut-off one will conclude the wrong thing.
/// </summary>
public sealed class SearchWorkspaceTool : ITool
{
    public const int MaxMatches = 200;
    public const int MaxFilesScanned = 5_000;
    public const int MaxFileBytes = 2_000_000;
    public const int MaxLineChars = 400;

    private readonly WorkspacePathGuard _guard;
    public SearchWorkspaceTool(WorkspacePathGuard guard) => _guard = guard;

    public string Name => "search_workspace";
    public string Description =>
        "Search the mission workspace for a literal string or regular expression. "
      + "Returns matching file paths with line numbers. Read-only.";

    public string ParametersJson => """
        {"type":"object",
         "properties":{
           "query":{"type":"string","description":"Text or regular expression to find"},
           "regex":{"type":"boolean","description":"Treat query as a regular expression (default false)"},
           "path":{"type":"string","description":"Subdirectory to search, relative to the workspace root"},
           "glob":{"type":"string","description":"Filename pattern to limit the search, e.g. *.cs"}},
         "required":["query"]}
        """;

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        if (!AnthillRuntime.EnableFileTools)
            return new ToolResult(Name, false, "", "File tools are disabled by config.",
                FailureClass.AuthorizationFailure);

        var query = args.GetValueOrDefault("query")?.ToString() ?? "";
        if (query.Trim().Length == 0)
            return new ToolResult(Name, false, "", "Missing required argument: query",
                FailureClass.ValidationFailure);

        var glob = args.GetValueOrDefault("glob")?.ToString();
        if (string.IsNullOrWhiteSpace(glob)) glob = "*";

        string root;
        try { root = _guard.ResolveSafePath(args.GetValueOrDefault("path")?.ToString() ?? "."); }
        catch (Exception error) { return new ToolResult(Name, false, "", error.Message, ToolRegistry.ClassifyThrown(error)); }

        if (!Directory.Exists(root))
            return new ToolResult(Name, false, "", $"Directory does not exist: {root}",
                FailureClass.ValidationFailure);

        Regex matcher;
        try
        {
            var asRegex = args.GetValueOrDefault("regex") is true or "true" or "True";
            matcher = new Regex(asRegex ? query : Regex.Escape(query),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                // A pathological pattern must not hang a mission. Catastrophic backtracking is the
                // one way a "read-only" tool can consume a machine, and the model supplies the
                // pattern, so the timeout is not optional.
                TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException error)
        {
            // The model wrote the pattern and can fix it — which it can only do if it is told.
            return new ToolResult(Name, false, "", $"Invalid regular expression: {error.Message}",
                FailureClass.ValidationFailure);
        }

        var output = new StringBuilder();
        var matches = 0;
        var scanned = 0;
        var truncated = false;

        foreach (var file in Files(root, glob!))
        {
            if (scanned >= MaxFilesScanned || matches >= MaxMatches) { truncated = true; break; }
            scanned++;

            string[] lines;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxFileBytes) continue;          // minified bundles, fixtures, blobs
                lines = File.ReadAllLines(file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;   // an unreadable file is not a failed search
            }

            for (var i = 0; i < lines.Length && matches < MaxMatches; i++)
            {
                bool hit;
                try { hit = matcher.IsMatch(lines[i]); }
                catch (RegexMatchTimeoutException) { return TimedOut(query); }
                if (!hit) continue;

                matches++;
                var text = lines[i].Trim();
                if (text.Length > MaxLineChars) text = text[..MaxLineChars] + "…";
                output.Append(Path.GetRelativePath(root, file)).Append(':').Append(i + 1)
                      .Append(": ").Append(text).Append('\n');
            }
        }

        if (matches == 0)
            return new ToolResult(Name, true, $"No matches for '{query}' under {Path.GetFileName(root)}.");

        // Truncation is STATED. A model handed a silently cut-off result concludes the thing it was
        // looking for does not exist anywhere else, which is a wrong answer produced confidently.
        var header = truncated || matches >= MaxMatches
            ? $"{matches} match(es) — TRUNCATED at the limit; narrow the query or path for the rest.\n"
            : $"{matches} match(es) in {scanned} file(s).\n";

        return new ToolResult(Name, true, header + output);
    }

    private ToolResult TimedOut(string query) =>
        new(Name, false, "", $"The pattern took too long to evaluate against this workspace: '{query}'. "
                           + "Simplify it — nested quantifiers over long lines are the usual cause.",
            FailureClass.Timeout);

    /// <summary>
    /// Files worth searching, in deterministic order, skipping the directories every ecosystem fills
    /// with other people's code. Searching node_modules is slow, useless, and floods the result with
    /// matches in dependencies the mission cannot change.
    /// </summary>
    private static IEnumerable<string> Files(string root, string glob)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, glob).OrderBy(f => f, StringComparer.Ordinal); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }

            foreach (var file in files)
            {
                var suffix = Path.GetExtension(file);
                if (AnthillRuntime.BlockedFileSuffixes.Contains(suffix)) continue;
                yield return file;
            }

            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(dir).OrderByDescending(d => d, StringComparer.Ordinal); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name is ".git" or "node_modules" or "bin" or "obj" or "dist" or "target"
                    or "vendor" or ".venv" or "__pycache__" or ".next") continue;
                if (AnthillRuntime.BlockedPathParts.Contains(name)) continue;
                stack.Push(child);
            }
        }
    }
}

/// <summary>
/// v3.5.0 — what this mission has changed, diffed against the revision it started from.
///
/// The second contract-declared, never-built tool: <c>scribe</c>'s only allowed tool was
/// <c>read_changed_files_summary</c>, so the role that writes release notes could dispatch nothing
/// at all.
///
/// This is where the manifest earns its keep. The diff is taken against the workspace's RECORDED
/// BASE REVISION, not against whatever the source checkout's HEAD happens to be now. Those differ
/// the moment anyone commits during a long mission, and diffing against a moving HEAD produces a
/// change set that includes other people's work — which a scribe would then describe as this
/// mission's, in release notes, convincingly.
/// </summary>
public sealed class ChangedFilesSummaryTool : ITool
{
    public const int MaxDiffChars = 20_000;

    public string Name => "read_changed_files_summary";
    public string Description =>
        "Summarise what this mission has changed in its workspace, against the revision it started "
      + "from. Returns changed file paths with added/removed line counts. Read-only.";

    public string ParametersJson => """
        {"type":"object",
         "properties":{
           "include_patch":{"type":"boolean",
             "description":"Include the unified diff as well as the file list (default false)"}},
         "required":[]}
        """;

    public ToolResult Run(IReadOnlyDictionary<string, object?> args)
    {
        var workspace = MissionWorkspaceScope.Current;
        if (workspace is null || !workspace.Usable || workspace.Root.Length == 0)
            // A clear refusal rather than a diff of the live checkout. Summarising the operator's
            // uncommitted work as "what this mission changed" would be a confident, plausible lie.
            return new ToolResult(Name, false, "",
                "There is no mission workspace in scope, so there is no change set to summarise.",
                FailureClass.UnsafeState);

        // Against the RECORDED base. `git diff HEAD` inside a detached worktree would silently mean
        // something different once the agent commits, and `git diff` against the source's current
        // HEAD would fold in commits this mission never made.
        var against = workspace.BaseRevision.Length > 0 ? workspace.BaseRevision : "HEAD";

        var (statOk, stat) = Git(workspace.Root, $"diff --stat {against} --");
        if (!statOk)
            return new ToolResult(Name, false, "", $"Could not read the change set: {stat.Trim()}",
                FailureClass.DependencyFailure);

        var (_, untracked) = Git(workspace.Root, "ls-files --others --exclude-standard");

        var report = new StringBuilder();
        report.Append("base_revision=").Append(against).Append('\n');
        report.Append("workspace=").Append(workspace.Id).Append('\n');

        var tracked = stat.Trim();
        report.Append("--- tracked changes ---\n")
              .Append(tracked.Length == 0 ? "(none)" : tracked).Append('\n');

        var added = untracked.Trim();
        if (added.Length > 0) report.Append("--- new files ---\n").Append(added).Append('\n');

        if (args.GetValueOrDefault("include_patch") is true or "true" or "True")
        {
            var (patchOk, patch) = Git(workspace.Root, $"diff {against} --");
            if (patchOk && patch.Trim().Length > 0)
            {
                var body = patch.Length > MaxDiffChars
                    ? patch[..MaxDiffChars] + "\n…(patch truncated — read specific files for the rest)"
                    : patch;
                report.Append("--- patch ---\n").Append(body).Append('\n');
            }
        }

        return new ToolResult(Name, true, report.ToString());
    }

    private static (bool Ok, string Output) Git(string workdir, string args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workdir, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,   // v0.3.8.55: children emit UTF-8, not the OS codepage
                StandardErrorEncoding = Encoding.UTF8,
            })!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            // v0.3.8.57 — TIMEOUT KILLS THE TREE.
            //
            // WaitForExit(ms) returns false and execution CARRIED ON: the git process and
            // every child it spawned kept running, and `ExitCode` on a process that has not
            // exited throws — so the timeout was reported as an exception message rather than
            // as a timeout, and "stop means stop" was false for every one of these sites.
            if (!process.WaitForExit(60_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return (false, "git timed out after 60s and was terminated with its child processes");
            }
            return (process.ExitCode == 0, process.ExitCode == 0 ? stdout : stderr);
        }
        catch (Exception error)
        {
            return (false, error.Message);
        }
    }
}
