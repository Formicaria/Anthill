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
    private static string Compat(AntExecutionResult r)
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
