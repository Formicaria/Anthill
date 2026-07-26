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

/// <summary>
/// Execution framework Stage D-3: SoldierAnt — security, permission, policy, and risk review.
/// The deterministic <see cref="PolicyScan"/> is the AUTHORITY: its findings and blocks are
/// computed before and independent of any model text, so nothing generated can override a
/// deterministic block. Review input = the task description plus every prior completed task
/// result in the mission (where patch metadata and changed paths live).
/// </summary>
public sealed class SoldierAnt : BaseAnt
{
    public SoldierAnt() : base("soldier") { }

    public override string Run(Task task, Mission mission)
    {
        var contract = AntExecutionCatalog.ContractFor("soldier")!;
        if (!contract.SupportsTaskType(task.TaskType))
            return UiCartographerAnt.Compat(AntExecutionResult.Blocked(
                $"task type '{task.TaskType}' is outside the soldier execution contract"));

        var input = task.Description + "\n" + string.Join("\n",
            mission.Tasks.Where(t => t.Id != task.Id && t.Result is not null).Select(t => t.Result));

        var findings = PolicyScan.Scan(input);
        var scope = PolicyScan.ScopeMismatch(input);
        if (scope is not null) findings.Add(scope);

        var blocked = findings.Where(f => f.Blocking).ToList();
        var risk = PolicyScan.OverallRisk(findings);
        var review =
            $"risk_level: {risk}\n" +
            $"blocked: {blocked.Count > 0}\n" +
            "matched_rules:\n" + (findings.Count == 0 ? "  (none)\n" : string.Join("\n", findings.Select(f => $"  - [{f.Risk}]{(f.Blocking ? " BLOCKING" : "")} {f.RuleId}: {f.Detail}")) + "\n") +
            $"required_approvals: {(blocked.Count > 0 ? "operator review required before any apply" : "standard patch approval")}\n" +
            $"recommended_next: {(blocked.Count > 0 ? "route to operator via builder; do NOT proceed" : "proceed to verifier")}";

        var soldierResult = new AntExecutionResult
        {
            Success = true, // the REVIEW succeeded; the verdict lives in the artifact + evidence
            StatusCode = blocked.Count > 0 ? "succeeded_with_warnings" : "succeeded",
            Summary = blocked.Count > 0
                ? $"SECURITY REVIEW: {blocked.Count} BLOCKING finding(s), risk {risk} — deterministic block, not overridable."
                : $"Security review passed: {findings.Count} advisory finding(s), risk {risk}.",
            Artifacts = { new AntArtifact("security_review", "Deterministic policy review", review) },
            Evidence = findings.Select(f => new AntEvidence("policy_rule", f.RuleId, f.Detail)).ToList(),
            Handoffs =
            {
                blocked.Count > 0
                    ? new AntHandoff("soldier", "builder", "blocking findings need operator explanation", "build", new[] { "security_review" }, true, 1, $"soldier-block:{mission.Id}:{task.Id}")
                    : new AntHandoff("soldier", "verifier", "review passed — verify", "verification", new[] { "security_review" }, false, 1, $"soldier-ok:{mission.Id}:{task.Id}"),
            },
            Warnings = blocked.Select(b => b.RuleId).ToList(),
        };
        return UiCartographerAnt.Compat(soldierResult);
    }
}

/// <summary>
/// Execution framework Stage D-4: ScribeAnt — operator documentation, release notes, changelog
/// entries, and DOCUMENTATION-ONLY patch proposals. Deterministic assembly from real mission
/// results (no model required). The docs-path restriction is enforced HERE, fail closed: any
/// proposed path outside docs/, README.md, or CHANGELOG.md (or any non-.md file) refuses the
/// whole proposal — ScribeAnt can never propose a source-code patch, and it has no apply
/// permission anywhere in the system. Docs containing security-sensitive instructions hand off to
/// the soldier for review; everything else goes to the verifier.
/// </summary>
public sealed class ScribeAnt : BaseAnt
{
    public ScribeAnt() : base("scribe") { }

    private static readonly System.Text.RegularExpressions.Regex DocsPath =
        new(@"^(?:docs/[\w./\-]+\.md|README\.md|CHANGELOG\.md)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public override string Run(Task task, Mission mission)
    {
        var contract = AntExecutionCatalog.ContractFor("scribe")!;
        if (!contract.SupportsTaskType(task.TaskType))
            return UiCartographerAnt.Compat(AntExecutionResult.Blocked(
                $"task type '{task.TaskType}' is outside the scribe execution contract"));

        var priorResults = mission.Tasks.Where(t => t.Id != task.Id && t.Result is not null)
            .Select(t => $"[{t.AssignedAnt}] {t.Title}: {t.ResultSummary ?? Truncate(t.Result!)}").ToList();
        var changedFiles = System.Text.RegularExpressions.Regex
            .Matches(string.Join("\n", priorResults) + "\n" + task.Description, @"\b(?:src|docs|tests)/[\w./\-]+|README\.md|CHANGELOG\.md")
            .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var artifacts = new List<AntArtifact>
        {
            new("release_notes", "Operator summary",
                $"Mission: {mission.Goal}\nCompleted stages: {priorResults.Count}\n" +
                (changedFiles.Count > 0 ? $"Referenced files: {string.Join(", ", changedFiles.Take(10))}\n" : "") +
                string.Join("\n", priorResults.Take(10))),
        };
        var warnings = new List<string>();

        // Documentation patch proposals: docs paths only, structurally validated, never applied.
        if (task.TaskType == "docs_patch_proposal")
        {
            var targets = System.Text.RegularExpressions.Regex
                .Matches(task.Description, @"target:\s*([^\s,]+)")
                .Select(m => m.Groups[1].Value.Replace('\\', '/')).ToList();
            if (targets.Count == 0)
                return UiCartographerAnt.Compat(AntExecutionResult.Failed(
                    Contracts.FailureClass.ValidationFailure, "docs_patch_proposal requires explicit 'target: <docs path>' entries"));
            var illegal = targets.Where(t => !DocsPath.IsMatch(t)).ToList();
            if (illegal.Count > 0)
                return UiCartographerAnt.Compat(AntExecutionResult.Blocked(
                    $"documentation-only restriction: refused non-docs target(s) {string.Join(", ", illegal)}"));
            artifacts.Add(new AntArtifact("docs_patch_set", "Documentation patch proposal (requires approval; scribe holds no apply permission)",
                System.Text.Json.JsonSerializer.Serialize(new { targets, source_mission = mission.Id, requires_approval = true })));
        }

        var sensitive = System.Text.RegularExpressions.Regex.IsMatch(
            task.Description + string.Join("\n", priorResults),
            @"credential|secret|token|password|authentication|firewall", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (sensitive) warnings.Add("security-sensitive documentation — soldier review required");

        var result = new AntExecutionResult
        {
            Success = true,
            StatusCode = warnings.Count > 0 ? "succeeded_with_warnings" : "succeeded",
            Summary = $"Documentation produced: {artifacts.Count} artifact(s) from {priorResults.Count} mission result(s).",
            Artifacts = artifacts,
            Evidence = changedFiles.Select(f => new AntEvidence("file_path", f)).ToList(),
            Handoffs =
            {
                sensitive
                    ? new AntHandoff("scribe", "soldier", "docs contain security-sensitive instructions", "security_review", new[] { "release_notes" }, true, 1, $"scribe-sec:{mission.Id}:{task.Id}")
                    : new AntHandoff("scribe", "verifier", "documentation ready for verification", "verification", new[] { "release_notes" }, false, 1, $"scribe-ok:{mission.Id}:{task.Id}"),
            },
            Warnings = warnings,
        };
        return UiCartographerAnt.Compat(result);
    }

    private static string Truncate(string s) => s.Length <= 160 ? s : s[..160] + "…";
}
