using System.Text.Json;
using System.Text.RegularExpressions;
using Anthill.Core.Domain;
using Anthill.Core.Tools;

namespace Anthill.Core.Agents;

/// <summary>
/// Execution framework Stage D — canary 1: UICartographerAnt. Read-only frontend analysis before
/// UI changes are proposed: it maps routes, pages, functions, API calls, and styles from the REAL
/// repository files so UICoder works from actual structure, never guesses. Deterministic — no
/// model call is required for the map itself. Tool access runs through the enforced dispatch path
/// (list_directory / read_text_file only; write, shell, and patch tools are contract-forbidden and
/// structurally denied in Stage B).
/// Returns the compatibility string BaseAnt requires; the structured result is embedded as a
/// UI_MAP_JSON block (temporary adapter per spec §16 until BaseAnt returns structured results).
/// </summary>
public sealed class UiCartographerAnt : BaseAnt
{
    private readonly ToolRegistry _tools;
    private static readonly string[] UiFileHints = { ".html", ".js", ".css", ".jsx", ".ts", ".tsx" };
    private const int MaxFilesToRead = 6;
    private const int MaxCharsPerFile = 200_000;

    public UiCartographerAnt(ToolRegistry tools) : base("ui_cartographer") => _tools = tools;

    public override string Run(Task task, Mission mission)
    {
        var examined = new List<string>();
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var functions = new HashSet<string>();
        var apiCalls = new HashSet<string>();
        var styleBlocks = 0;
        var warnings = new List<string>();

        // 1. Find UI files (read-only listing through the enforced dispatch path).
        var listing = _tools.RunTool("list_directory", mission.Id, task.Id, Name, new() { ["path"] = "." });
        if (!listing.Success)
            return Compat(AntExecutionResult.Failed(Contracts.FailureClass.DependencyFailure,
                $"workspace listing unavailable: {listing.Error}"));
        // Format-agnostic extraction: pull file-path tokens out of whatever shape the listing
        // tool prints (plain names, decorated rows, sizes appended — all fine).
        var candidates = Regex.Matches(listing.Output, @"[\w][\w./\\\-]*\.(?:html|js|css|jsx|ts|tsx)\b", RegexOptions.IgnoreCase)
            .Select(m => m.Value.Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxFilesToRead).ToList();
        // The embedded console UI lives under src/Anthill.Api/Ui — try the known locations too.
        foreach (var known in new[] { "src/Anthill.Api/Ui/index.html", "src/Anthill.Api/Ui/app.js" })
            if (!candidates.Contains(known)) candidates.Add(known);

        // 2. Read each (bounded) and extract structure deterministically.
        foreach (var path in candidates.Take(MaxFilesToRead + 2))
        {
            var read = _tools.RunTool("read_text_file", mission.Id, task.Id, Name, new() { ["path"] = path });
            if (!read.Success) { warnings.Add($"unreadable: {path}"); continue; }
            var text = read.Output.Length > MaxCharsPerFile ? read.Output[..MaxCharsPerFile] : read.Output;
            examined.Add(path);
            foreach (Match m in Regex.Matches(text, "id=\"page-([a-z0-9_-]+)\"", RegexOptions.IgnoreCase))
                routes.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"function\s+([A-Za-z_][A-Za-z0-9_]*)\s*\("))
                functions.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"api(?:Text)?\(\s*['""](/[A-Za-z0-9_/{}.\-]*)"))
                apiCalls.Add(m.Groups[1].Value);
            styleBlocks += Regex.Matches(text, @"<style", RegexOptions.IgnoreCase).Count;
        }

        if (examined.Count == 0)
            return Compat(AntExecutionResult.Failed(Contracts.FailureClass.DependencyFailure,
                "no UI files could be read from the workspace"));

        // 3. Structured map + handoff to the UI coder (spec §6.5).
        var map = new Dictionary<string, object?>
        {
            ["routes"] = routes.OrderBy(r => r).ToList(),
            ["functions"] = functions.Count,
            ["function_names_sample"] = functions.OrderBy(f => f).Take(40).ToList(),
            ["api_calls"] = apiCalls.OrderBy(a => a).ToList(),
            ["style_blocks"] = styleBlocks,
            ["files_examined"] = examined,
            ["likely_modification_points"] = routes.OrderBy(r => r).Select(r => $"page-{r}").ToList(),
        };
        var mapJson = JsonSerializer.Serialize(map);
        var result = new AntExecutionResult
        {
            Success = true,
            StatusCode = warnings.Count > 0 ? "succeeded_with_warnings" : "succeeded",
            Summary = $"UI map: {routes.Count} routes, {functions.Count} functions, {apiCalls.Count} API call sites across {examined.Count} file(s).",
            Artifacts = { new AntArtifact("ui_map", "Frontend structure map", mapJson) },
            Evidence = examined.Select(f => new AntEvidence("file_path", f)).ToList(),
            Handoffs = { new AntHandoff("ui_cartographer", "coder", "UI map ready for implementation planning",
                "code_change", new[] { "ui_map" }, Required: false, Depth: 1, DedupeKey: $"uimap:{mission.Id}") },
            Warnings = warnings,
        };
        return Compat(result);
    }

    /// <summary>TEMPORARY compatibility adapter (spec §16): BaseAnt.Run returns a string, so the
    /// structured result rides along as a tagged JSON block. Removed when BaseAnt goes structured.</summary>
    internal static string Compat(AntExecutionResult r)
    {
        var payload = JsonSerializer.Serialize(new
        {
            status = r.StatusCode, success = r.Success, summary = r.Summary,
            artifacts = r.Artifacts.Select(a => new { a.Kind, a.Title, a.Content }),
            evidence = r.Evidence.Select(e => new { e.Kind, e.Value }),
            handoffs = r.Handoffs.Select(h => new { h.DestinationRole, h.Reason, h.RequiredTaskType }),
            warnings = r.Warnings,
        });
        return $"{r.Summary}\n\nUI_MAP_JSON:\n{payload}";
    }
}

/// <summary>
/// Execution framework Stage D-2: TesterAnt — deterministic checks and test evidence, nothing
/// else. It runs ONLY allowlisted checks through the enforced dispatch path (run_allowlisted_check
/// is its sole execution tool; shell/write/patch are contract-forbidden and structurally denied),
/// makes no model calls (its contract says so), and never reports success without a real exit
/// code as evidence. Success hands to the verifier; failure hands to the medic.
/// </summary>
public sealed class TesterAnt : BaseAnt
{
    private readonly ToolRegistry _tools;
    public TesterAnt(ToolRegistry tools) : base("tester") => _tools = tools;

    public override string Run(Task task, Mission mission)
    {
        var contract = AntExecutionCatalog.ContractFor("tester")!;
        if (!contract.SupportsTaskType(task.TaskType))
            return UiCartographerAnt.Compat(AntExecutionResult.Blocked(
                $"task type '{task.TaskType}' is outside the tester execution contract"));

        // Deterministic check selection: catalog ids literally named in the task text; a plain
        // build/test/validation task defaults to the SDK-probe + build profile. Never free text.
        var requested = CheckCatalog.Ids
            .Where(id => task.Description.Contains(id, StringComparison.OrdinalIgnoreCase)
                      || task.Title.Contains(id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (requested.Count == 0) requested = new List<string> { "dotnet_version", "dotnet_build" };

        var evidence = new List<AntEvidence>();
        var lines = new List<string>();
        var allPassed = true;
        foreach (var checkId in requested)
        {
            var run = _tools.RunTool("run_allowlisted_check", mission.Id, task.Id, Name,
                new() { ["check_id"] = checkId });
            var exit = System.Text.RegularExpressions.Regex.Match(run.Output, @"exit_code=(-?\d+)").Groups[1].Value;
            evidence.Add(new AntEvidence("check", checkId, $"exit_code={(exit.Length > 0 ? exit : "n/a")} success={run.Success}"));
            lines.Add($"{checkId}: {(run.Success ? "PASS" : "FAIL")}{(run.Success ? "" : $" — {run.Error}")}");
            if (!run.Success) allPassed = false;
        }

        var report = new AntArtifact("test_report", "Deterministic check report", string.Join("\n", lines));
        var result = new AntExecutionResult
        {
            Success = allPassed,
            StatusCode = allPassed ? "succeeded" : "failed_retryable",
            Summary = $"{requested.Count} check(s): {lines.Count(l => l.Contains(": PASS"))} passed, {lines.Count(l => l.Contains(": FAIL"))} failed.",
            Artifacts = { report },
            Evidence = evidence,
            Handoffs =
            {
                allPassed
                    ? new AntHandoff("tester", "verifier", "checks passed — verify results", "verification", new[] { "test_report" }, false, 1, $"tester-ok:{mission.Id}:{task.Id}")
                    : new AntHandoff("tester", "medic", "check failure needs diagnosis", "failure_diagnosis", new[] { "test_report" }, true, 1, $"tester-fail:{mission.Id}:{task.Id}"),
            },
            Failure = allPassed ? null : new AntFailure(Contracts.FailureClass.VerificationFailure, "one or more checks failed", Retryable: true),
        };
        return UiCartographerAnt.Compat(result);
    }
}
