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
/// v2.19.0: returns a structured AntExecutionResult. The former UI_MAP_JSON compatibility adapter
/// is gone — mission control reads StatusCode, Handoffs, and Evidence as fields, not as prose.
/// </summary>
public sealed class UiCartographerAnt : BaseAnt
{
    private readonly ToolRegistry _tools;
    private static readonly string[] UiFileHints = { ".html", ".js", ".css", ".jsx", ".ts", ".tsx" };
    private const int MaxFilesToRead = 6;
    private const int MaxCharsPerFile = 200_000;

    public UiCartographerAnt(ToolRegistry tools) : base("ui_cartographer") => _tools = tools;

    public override AntExecutionResult Execute(Task task, Mission mission)
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
            return AntExecutionResult.Failed(FailureClass.DependencyFailure,
                $"workspace listing unavailable: {listing.Error}");
        // Format-agnostic extraction: pull file-path tokens out of whatever shape the listing
        // tool prints (plain names, decorated rows, sizes appended — all fine).
        var candidates = Regex.Matches(listing.Output, @"[\w][\w./\\\-]*\.(?:html|js|css|jsx|ts|tsx)\b", RegexOptions.IgnoreCase)
            .Select(m => m.Value.Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxFilesToRead).ToList();
        // The embedded console UI lives under src/Anthill.UI — try the known locations too.
        foreach (var known in new[] { "src/Anthill.UI/index.html", "src/Anthill.UI/app.js" })
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
            return AntExecutionResult.Failed(FailureClass.DependencyFailure,
                "no UI files could be read from the workspace");

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
            // The operator record is the readable map; the ui_map artifact stays the machine copy
            // for the coder handoff. A route/function count alone would not survive review.
            Narrative =
                $"files_examined: {string.Join(", ", examined)}\n" +
                $"routes ({routes.Count}): {string.Join(", ", routes.OrderBy(r => r))}\n" +
                $"functions ({functions.Count}): {string.Join(", ", functions.OrderBy(f => f).Take(40))}\n" +
                $"api_call_sites ({apiCalls.Count}): {string.Join(", ", apiCalls.OrderBy(a => a))}\n" +
                $"style_blocks: {styleBlocks}\n" +
                $"likely_modification_points: {string.Join(", ", routes.OrderBy(r => r).Select(r => $"page-{r}"))}"
                + (warnings.Count > 0 ? $"\nwarnings: {string.Join("; ", warnings)}" : ""),
        };
        return result;
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

    /// <summary>
    /// v2.19.0: migrated to the structured contract. The result below was always built in full —
    /// including the medic/verifier handoffs — and then discarded through Compat(), which
    /// stringified it so the executor (which never parsed it) marked failing checks as completed
    /// tasks. It is now returned as-is and TaskOutcomeMapper decides the task's fate.
    /// </summary>
    public override AntExecutionResult Execute(Task task, Mission mission)
    {
        var contract = AntExecutionCatalog.ContractFor("tester")!;
        if (!contract.SupportsTaskType(task.TaskType))
            return AntExecutionResult.Blocked(
                $"task type '{task.TaskType}' is outside the tester execution contract");

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
            Failure = allPassed ? null : new AntFailure(FailureClass.VerificationFailure, "one or more checks failed", Retryable: true),
        };
        return result;
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
    private readonly Anthill.SDK.Artifacts.IArtifactStore? _artifacts;

    /// <summary>
    /// v3.8.25: the artifact store, so the review can read the PATCH rather than prose about it.
    ///
    /// Optional, and that is not laziness. Dozens of tests and the CLI construct a soldier with no
    /// store, and in that configuration it behaves exactly as it did before — prose review, same
    /// deterministic rules. A required dependency would have made this release a rewrite of every
    /// call site to gain a capability none of them use.
    /// </summary>
    public SoldierAnt(Anthill.SDK.Artifacts.IArtifactStore? artifacts = null) : base("soldier") =>
        _artifacts = artifacts;

    /// <summary>
    /// The warning that means "a deterministic policy rule said no". v3.8.22. Named here and read by
    /// <c>ExecutionService.PersistExecutionRecord</c> — the same structured-disclosure idiom
    /// <c>provider_failure</c> uses, and for the same reason: a downstream gate must never infer a
    /// block by parsing prose a model may have written.
    /// </summary>
    public const string SoldierBlockMarker = "deterministic_block";

    /// <summary>
    /// The mission's patch-set artifacts, as review material. v3.8.25.
    ///
    /// Returns EMPTY when there is no store, no patch set, or the read faults — and empty means the
    /// review proceeds on prose alone, exactly as it did before. Deliberately not a block: a security
    /// review that refuses to run because it could not load an artifact is a review that stops
    /// happening the first time the store hiccups, and the deterministic rules it does apply to the
    /// description are worth more than nothing.
    ///
    /// What it does NOT do is claim to have reviewed the patch when it did not. The review text
    /// records how many patch artifacts were read, so "0 patch artifacts" is visible to an operator
    /// rather than being indistinguishable from a clean scan of a real one.
    /// </summary>
    private (string Material, int Count) ReadPatchSetArtifacts(Mission mission)
    {
        if (_artifacts is null) return ("", 0);
        try
        {
            var patches = _artifacts.ForMission(mission.Id, Anthill.SDK.Artifacts.ArtifactSchemas.PatchSet);
            return patches.Count == 0
                ? ("", 0)
                : (string.Join("\n", patches.Select(p => p.Payload)), patches.Count);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[soldier] could not read patch artifacts for {mission.Id}: {error.Message}");
            return ("", 0);
        }
    }

    /// <summary>
    /// v2.19.0: migrated to the structured contract. Note the deliberate distinction preserved
    /// here — the REVIEW succeeding is not the same as the review PASSING. A blocking finding
    /// leaves StatusCode succeeded_with_warnings (the soldier did its job) while the blocking
    /// verdict lives in the artifact, evidence and warnings. Stage 6 reads those warnings when
    /// deciding whether a mission may be completed_verified; a security block must prevent
    /// verified success without pretending the review itself errored.
    /// </summary>
    public override AntExecutionResult Execute(Task task, Mission mission)
    {
        var contract = AntExecutionCatalog.ContractFor("soldier")!;
        if (!contract.SupportsTaskType(task.TaskType))
            return AntExecutionResult.Blocked(
                $"task type '{task.TaskType}' is outside the soldier execution contract");

        // v3.8.25 — THE REVIEW READS THE PATCH.
        //
        // Until this release the soldier's entire input was the task description plus every prior
        // task's RESULT PROSE. It was reviewing descriptions of a change, and a policy engine that
        // scans a description cannot find a secret in the change: the `secret_material` rule looks
        // for `-----BEGIN PRIVATE KEY-----` and `api_key = "…"` in source, and source was the one
        // thing it never saw. Every rule about paths and content was matching a summary.
        //
        // The prose is KEPT and the patch is ADDED, rather than swapped. The description carries the
        // approved_scope declaration that ScopeMismatch parses, and prior results carry context a
        // patch body does not. Replacing one input with the other would have traded one blind spot
        // for a different one.
        var (patchMaterial, patchArtifactCount) = ReadPatchSetArtifacts(mission);

        var input = task.Description + "\n" + string.Join("\n",
            mission.Tasks.Where(t => t.Id != task.Id && t.Result is not null).Select(t => t.Result))
            + (patchMaterial.Length > 0 ? "\n" + patchMaterial : "");

        var findings = PolicyScan.Scan(input);
        var scope = PolicyScan.ScopeMismatch(input);
        if (scope is not null) findings.Add(scope);

        var blocked = findings.Where(f => f.Blocking).ToList();
        var risk = PolicyScan.OverallRisk(findings);
        var review =
            $"risk_level: {risk}\n" +
            $"blocked: {blocked.Count > 0}\n" +
            // v3.8.25: what was ACTUALLY reviewed. Zero means the scan saw prose only, which must
            // never be mistaken for a clean scan of a real patch.
            $"patch_artifacts_reviewed: {patchArtifactCount}\n" +
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
            // v3.8.22: the marker leads, then the rule ids. Until this release the soldier's block
            // was a list of rule-id strings that nothing downstream recognised as a block — the
            // mission gate ignored them entirely, so "deterministic block, not overridable" in the
            // Summary above was a claim the code did not implement and a blocked patch could reach
            // completed_verified. PersistExecutionRecord reads this marker onto Task.DeterministicBlock,
            // exactly as it reads provider_failure onto GenerationDegraded. A named marker rather than
            // "warnings is non-empty", so a future advisory warning here cannot silently become a block.
            Warnings = blocked.Count > 0
                ? new List<string> { SoldierBlockMarker }.Concat(blocked.Select(b => b.RuleId)).ToList()
                : new List<string>(),
            // The review text is the record: operators need the findings, not a one-line verdict.
            Narrative = review,
        };
        return soldierResult;
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

    public override AntExecutionResult Execute(Task task, Mission mission)
    {
        var contract = AntExecutionCatalog.ContractFor("scribe")!;
        if (!contract.SupportsTaskType(task.TaskType))
            return AntExecutionResult.Blocked(
                $"task type '{task.TaskType}' is outside the scribe execution contract");

        var priorResults = mission.Tasks.Where(t => t.Id != task.Id && t.Result is not null)
            .Select(t => $"[{t.AssignedAnt}] {t.Title}: {t.ResultSummary ?? Truncate(t.Result!)}").ToList();
        var changedFiles = System.Text.RegularExpressions.Regex
            .Matches(string.Join("\n", priorResults) + "\n" + task.Description, @"\b(?:src|docs|tests)/[\w./\-]+|README\.md|CHANGELOG\.md")
            .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var releaseNotes =
            $"Mission: {mission.Goal}\nCompleted stages: {priorResults.Count}\n" +
            (changedFiles.Count > 0 ? $"Referenced files: {string.Join(", ", changedFiles.Take(10))}\n" : "") +
            string.Join("\n", priorResults.Take(10));
        var artifacts = new List<AntArtifact> { new("release_notes", "Operator summary", releaseNotes) };
        var warnings = new List<string>();
        var proposedTargets = new List<string>();

        // Documentation patch proposals: docs paths only, structurally validated, never applied.
        if (task.TaskType == "docs_patch_proposal")
        {
            var targets = System.Text.RegularExpressions.Regex
                .Matches(task.Description, @"target:\s*([^\s,]+)")
                .Select(m => m.Groups[1].Value.Replace('\\', '/')).ToList();
            if (targets.Count == 0)
                return AntExecutionResult.Failed(
                    FailureClass.ValidationFailure, "docs_patch_proposal requires explicit 'target: <docs path>' entries");
            var illegal = targets.Where(t => !DocsPath.IsMatch(t)).ToList();
            if (illegal.Count > 0)
                return AntExecutionResult.Blocked(
                    $"documentation-only restriction: refused non-docs target(s) {string.Join(", ", illegal)}");
            proposedTargets = targets;
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
            // The operator record is the documentation itself, plus anything that gates its
            // publication — a one-line artifact count would discard the deliverable.
            Narrative = releaseNotes
                + (proposedTargets.Count > 0
                    ? $"\n\nProposed documentation targets (requires approval; scribe holds no apply permission): {string.Join(", ", proposedTargets)}"
                    : "")
                + (sensitive
                    ? "\n\nsecurity-sensitive documentation — soldier review required before publication."
                    : ""),
        };
        return result;
    }


    private static string Truncate(string s) => s.Length <= 160 ? s : s[..160] + "…";
}

/// <summary>
/// Execution framework Stage D-5: MedicAnt — diagnoses real failures and recommends ONE bounded
/// repair route; it never repairs anything itself and never applies changes. Loop control is hard:
/// at most <see cref="MaxDiagnosesPerMission"/> diagnoses per mission, and a repeated diagnosis of
/// the same failure escalates to the operator (via builder) instead of looping. Classification is
/// deterministic keyword→FailureClass mapping; retryability comes from the v2.9 taxonomy.
/// </summary>
public sealed class MedicAnt : BaseAnt
{
    public const int MaxDiagnosesPerMission = 2;
    public MedicAnt() : base("medic") { }

    /// <summary>
    /// v2.19.0: migrated to the structured contract.
    ///
    /// Failure DETECTION also changed. It used to sniff prior task result text for
    /// <c>"status":"failed</c> and <c>": FAIL"</c>, because a failing tester was recorded as a
    /// COMPLETED task carrying its real outcome inside serialised prose — text-matching was the
    /// only way to notice. Since stage 3b a failing check genuinely fails, so TaskStatus is
    /// authoritative and the prose sniffing is not merely redundant but misleading: it would now
    /// match a summary that merely mentions the word.
    /// </summary>
    public override AntExecutionResult Execute(Task task, Mission mission)
    {
        var contract = AntExecutionCatalog.ContractFor("medic")!;
        if (!contract.SupportsTaskType(task.TaskType))
            return AntExecutionResult.Blocked(
                $"task type '{task.TaskType}' is outside the medic execution contract");

        // Only diagnose actual failures (spec: never invoke medic before failure).
        var failed = mission.Tasks
            .Where(t => t.Id != task.Id && (t.Status == TaskStatus.Failed || t.FailureReason is not null))
            .OrderByDescending(t => t.FailedAt ?? t.FinishedAt ?? DateTime.MinValue)
            .FirstOrDefault();
        if (failed is null)
            return AntExecutionResult.Blocked("no failed task in this mission — nothing to diagnose");

        // Loop control 1: diagnosis budget per mission.
        var priorDiagnoses = mission.Tasks.Count(t => t.Id != task.Id && t.AssignedAnt == "medic" && t.Result is not null);
        if (priorDiagnoses >= MaxDiagnosesPerMission)
            return new AntExecutionResult
            {
                Success = true, StatusCode = "succeeded_with_warnings",
                Summary = $"Diagnosis budget exhausted ({priorDiagnoses}/{MaxDiagnosesPerMission}) — escalating to operator, no further repair loops.",
                Narrative = $"Diagnosis budget exhausted ({priorDiagnoses}/{MaxDiagnosesPerMission}). Repeated failures exceed the repair budget; operator review required.",
                Artifacts = { new AntArtifact("failure_diagnosis", "Escalation", "repeated failures exceed the repair budget; operator review required") },
                Handoffs = { new AntHandoff("medic", "builder", "escalation: repair budget exhausted", "build", new[] { "failure_diagnosis" }, true, 1, $"medic-esc:{mission.Id}") },
                Warnings = { "escalated" },
            };

        // Deterministic classification.
        var text = (failed.FailureReason ?? "") + " " + (failed.Result ?? "");
        var (cls, cause, confidence) = Classify(text);
        var retryable = FailureClassify.IsRetryable(cls);

        // Loop control 2: identical diagnosis already issued → escalate, don't repeat the route.
        var dedupe = $"{failed.Id}:{cls}";
        var repeated = mission.Tasks.Any(t => t.Id != task.Id && t.AssignedAnt == "medic"
            && (t.Result?.Contains(dedupe) ?? false));

        var targetRole = repeated ? "builder"
            : text.Contains("ui", StringComparison.OrdinalIgnoreCase) || text.Contains(".html") || text.Contains("app.js") ? "ui_cartographer"
            : retryable ? "tester"
            : "coder";
        var targetType = targetRole switch
        {
            "builder" => "build", "ui_cartographer" => "ui_mapping",
            "tester" => "test_execution", _ => "code_change",
        };

        var diagnosis =
            $"dedupe: {dedupe}\nfailure_classification: {cls}\nprobable_cause: {cause}\nconfidence: {confidence}\n" +
            $"retryable: {retryable}\nrecommended_role: {targetRole}\nrecommended_task_type: {targetType}\n" +
            $"verification_plan: re-run the failed check via tester, then verifier\n" +
            $"source_task: {failed.Id} ({failed.Title})";

        return new AntExecutionResult
        {
            Success = true,
            StatusCode = "succeeded",
            Summary = $"Diagnosis: {cls} ({cause}) — route to {targetRole}{(repeated ? " [escalated: repeat diagnosis]" : "")}.",
            // The full diagnosis becomes the task's recorded text (Queen uses Narrative ?? Summary).
            // Loop control 2 below matches the dedupe key in a PRIOR medic task's result, so that
            // key must survive into the record — with only the one-line summary stored, an
            // identical diagnosis would be re-issued forever instead of escalating.
            Narrative = diagnosis,
            Artifacts =
            {
                new AntArtifact("failure_diagnosis", "Failure diagnosis", diagnosis),
                new AntArtifact("repair_recommendation", "Bounded repair route", $"{targetRole}:{targetType} (single attempt, then re-test)"),
            },
            Evidence = { new AntEvidence("failure_id", failed.Id, failed.FailureReason ?? "structured failure in result") },
            Handoffs = { new AntHandoff("medic", targetRole, repeated ? "escalation: repeat diagnosis" : $"repair route for {cls}",
                targetType, new[] { "failure_diagnosis" }, true, 1, $"medic:{dedupe}") },
        };
    }


    internal static (FailureClass, string, string) Classify(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("timed out") || t.Contains("timeout")) return (FailureClass.Timeout, "operation exceeded its time budget", "high");
        if (t.Contains("rate limit") || t.Contains("429")) return (FailureClass.RateLimit, "provider rate limiting", "high");
        if (t.Contains("unreachable") || t.Contains("connection") || t.Contains("transient")) return (FailureClass.TransientProviderFailure, "backing service unavailable", "medium");
        if (t.Contains("authorization_denied") || t.Contains("permission")) return (FailureClass.AuthorizationFailure, "capability/tool boundary denied the operation", "high");
        if (t.Contains("exit_code=") || t.Contains(": fail") || t.Contains("build") || t.Contains("test")) return (FailureClass.VerificationFailure, "deterministic check failed", "high");
        if (t.Contains("invalid") || t.Contains("validation")) return (FailureClass.ValidationFailure, "input failed validation", "medium");
        return (FailureClass.InternalDefect, "unclassified failure — treated as internal defect (not retryable)", "low");
    }
}

/// <summary>
/// Execution framework Stage D-6: ArchivistAnt — turns TERMINAL mission history into durable
/// memory candidates with provenance. The learning semantics are hard rules, not judgment calls:
/// positive procedural memory comes ONLY from completed_verified (a completed mission whose
/// verifier passed); completed-but-unverified, partial, failed, and timed_out NEVER reinforce
/// positively; failures produce negative lessons; cancellation is stored neutrally. Secret-like
/// content is redacted before anything is written, every candidate carries its source mission and
/// evidence, and nothing here auto-promotes to a certified skill (that is V2.12 territory).
/// Candidates are emitted as structured artifacts for the memory pipeline to ingest.
/// </summary>
public sealed class ArchivistAnt : BaseAnt
{
    public ArchivistAnt() : base("archivist") { }

    private static readonly System.Text.RegularExpressions.Regex SecretLike = new(
        @"(?:password|passwd|api[_-]?key|token|secret)\s*[:=]\s*['""]?[^'""\s]{4,}|-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public override AntExecutionResult Execute(Task task, Mission mission)
    {
        var contract = AntExecutionCatalog.ContractFor("archivist")!;
        if (!contract.SupportsTaskType(task.TaskType))
            return AntExecutionResult.Blocked(
                $"task type '{task.TaskType}' is outside the archivist execution contract");

        // Terminal outcome determination — deterministic, from real mission state:
        // explicit "outcome: x" in the task wins; otherwise Complete + verifier-PASS = verified.
        var explicitOutcome = System.Text.RegularExpressions.Regex.Match(task.Description, @"outcome:\s*(\w+)").Groups[1].Value;
        var verifierPassed = mission.Tasks.Any(t => t.AssignedAnt == "verifier"
            && t.Status == TaskStatus.Complete
            && (t.Result?.Contains("PASS", StringComparison.OrdinalIgnoreCase) ?? false));
        var outcome = explicitOutcome.Length > 0 ? explicitOutcome
            : mission.Status switch
            {
                MissionStatus.Complete => verifierPassed ? "completed_verified" : "completed_unverified",
                MissionStatus.Partial => "partial",
                MissionStatus.Failed => "failed",
                _ => "unknown",
            };
        if (outcome is "unknown" or "" || mission.Status == MissionStatus.Running)
            return AntExecutionResult.Blocked("mission is not terminal — archival runs only after a terminal outcome");

        var candidates = new List<Dictionary<string, object?>>
        {
            Candidate("episodic", $"Mission '{Redact(mission.Goal)}' ended {outcome}.", mission, outcome, "high"),
        };
        switch (outcome)
        {
            case "completed_verified": // the ONLY source of positive procedural memory
                var steps = string.Join(" -> ", mission.Tasks.Where(t => t.Status == TaskStatus.Complete).Select(t => t.AssignedAnt).Distinct());
                candidates.Add(Candidate("procedural_candidate", $"Verified route for similar goals: {steps}", mission, outcome, "medium"));
                break;
            case "failed":
            case "partial":
            case "timed_out":
                var failures = mission.Tasks.Where(t => t.Status == TaskStatus.Failed)
                    .Select(t => Redact($"{t.AssignedAnt}: {t.FailureReason ?? t.Title}")).ToList();
                candidates.Add(Candidate("negative",
                    $"Do not repeat: {(failures.Count > 0 ? string.Join("; ", failures.Take(3)) : $"mission ended {outcome} without verified success")}",
                    mission, outcome, "medium"));
                break;
            case "cancelled": break; // neutral — the episodic record above is the whole story
        }

        var payload = System.Text.Json.JsonSerializer.Serialize(candidates);

        // The operator record is the candidate ledger, not the JSON blob: what was learned, from
        // which mission, and at what confidence. Summaries are already redacted at Candidate().
        var narrative =
            $"outcome: {outcome}\n" +
            $"source_mission: {mission.Id}\n" +
            $"verifier_passed: {verifierPassed}\n" +
            "candidates:\n" +
            string.Join("\n", candidates.Select(c =>
                $"  - [{c["memory_class"]}] (confidence {c["confidence"]}) {c["summary"]}")) + "\n" +
            "auto_promote: false — certification requires the evaluation pipeline, never archival.";

        return new AntExecutionResult
        {
            Success = true,
            StatusCode = "succeeded",
            Summary = $"Archived terminal outcome '{outcome}': {candidates.Count} memory candidate(s)"
                + (outcome == "completed_verified" ? " (incl. positive procedural)" : outcome is "failed" or "partial" or "timed_out" ? " (incl. negative lesson)" : " (neutral)") + ".",
            Artifacts = { new AntArtifact("memory_candidate", "Memory candidates with provenance", payload) },
            Evidence = { new AntEvidence("mission_id", mission.Id, $"outcome={outcome} verifier_passed={verifierPassed}") },
            Narrative = narrative,
        };
    }


    private static Dictionary<string, object?> Candidate(string cls, string summary, Mission m, string outcome, string confidence) => new()
    {
        ["memory_class"] = cls, ["summary"] = summary, ["source_mission"] = m.Id,
        ["outcome"] = outcome, ["confidence"] = confidence,
        ["evidence"] = m.Tasks.Where(t => t.Result is not null).Select(t => t.Id).ToList(),
        ["auto_promote"] = false, // never a certified skill without the V2.12 evaluation pipeline
    };

    private static string Redact(string s) => SecretLike.Replace(s ?? "", "[REDACTED]");
}
