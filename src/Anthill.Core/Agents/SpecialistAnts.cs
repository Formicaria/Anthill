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

    /// <summary>
    /// How many conventional layout locations to probe beyond what the listing discovered. v3.8.28.
    ///
    /// Matches the length of the probe list below — THIRTEEN, and a test pins the two together, because
    /// the first draft said twelve against a list of thirteen and would have silently dropped the last
    /// probe. Declared as a constant rather than left implicit
    /// in the read cap, because the previous cap was `MaxFilesToRead + 2` — sized for exactly the
    /// two ANTHILL paths that used to be hard-coded — and widening the list without widening the cap
    /// silently discards the extra probes.
    /// </summary>
    private const int MaxLayoutProbes = 13;
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
        // v3.8.28 — GENERIC layout probing, not two hard-coded ANTHILL paths.
        //
        // `src/Anthill.UI/index.html` and `src/Anthill.UI/app.js` were appended unconditionally: the
        // cartographer was a role that could only map THIS repository. Pointed at any other project
        // it added two paths that do not exist, logged them as unreadable, and produced a map of
        // whatever the top-level listing happened to catch.
        //
        // These are the conventional locations across the ecosystems the workspace adapters already
        // detect. Every one is a CANDIDATE — unreadable paths are skipped exactly as before — so
        // this widens what can be found without asserting that any of it is there.
        foreach (var known in new[]
                 {
                     "index.html", "src/index.html", "public/index.html", "app/index.html",
                     "src/App.jsx", "src/App.tsx", "src/main.js", "src/main.ts",
                     "src/app.js", "app.js", "static/app.js",
                     "src/Anthill.UI/index.html", "src/Anthill.UI/app.js",   // this repo, now one case among many
                 })
            if (!candidates.Contains(known, StringComparer.OrdinalIgnoreCase)) candidates.Add(known);

        // 2. Read each (bounded) and extract structure deterministically.
        // v3.8.28: the bound was `MaxFilesToRead + 2`, sized for exactly the two hard-coded ANTHILL
        // paths that used to be appended. Widening the probe list to twelve conventional locations
        // without widening this would have silently truncated ten of them — the discovered files
        // come first, so only the first two probes would ever have been tried and the change would
        // have looked like it worked on this repository alone.
        //
        // A probe that misses is a failed read, which the loop already skips and which costs one
        // tool dispatch. The cap exists to bound work, and this is the honest number for the work
        // now being attempted.
        foreach (var path in candidates.Take(MaxFilesToRead + MaxLayoutProbes))
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

        // v3.8.28 — checks come from the WORKSPACE, not from a hard-coded .NET default.
        //
        // The tester selected from `CheckCatalog.Ids` — the global compiled catalog — and when the
        // task named none, fell back to `{ dotnet_version, dotnet_build }`. On a Node or Python
        // project that is a tester which runs the wrong toolchain and reports a failure that says
        // more about the colony than about the code. `WorkspaceAdapters` has detected Node, Python
        // and .NET since v3.5.0, and `WorkspaceCapabilityManifest` has assembled their checks — the
        // tester simply never asked.
        //
        // The manifest is consulted FIRST and the catalog remains the fallback, which is the same
        // precedence `RunAllowlistedCheckTool` already applies when it actually runs the check. Two
        // components disagreeing about which catalog is authoritative is how a tester selects an id
        // the runner then refuses.
        var manifest = Workspaces.WorkspaceCapabilityManifest.ForCurrentMission();
        var available = manifest.IsEmpty
            ? CheckCatalog.Ids.ToList()
            : manifest.Checks.Select(c => c.Id).ToList();

        var requested = available
            .Where(id => task.Description.Contains(id, StringComparison.OrdinalIgnoreCase)
                      || task.Title.Contains(id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (requested.Count == 0)
            requested = manifest.IsEmpty
                // No workspace in scope: the historical .NET default, unchanged, because this is
                // the configuration every existing caller and test runs in.
                ? new List<string> { "dotnet_version", "dotnet_build" }
                // A detected workspace runs EVERYTHING its adapters declare. A tester that picked a
                // subset would be choosing which failures the colony is allowed to notice.
                : available;

        if (requested.Count == 0)
            return AntExecutionResult.Blocked(
                "no checks are available for this workspace — the adapters detected no project type, "
                + "so there is nothing deterministic to run and a PASS would mean nothing");

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

        // v0.3.8.41 — WHICH TREE. A check result that does not name the tree it ran in is not
        // evidence about anything in particular.
        //
        // RunAllowlistedCheckTool resolves its working directory from whatever workspace is ambient,
        // and a mission has two at different moments: the MISSION workspace, which is the source as
        // the coder left it, and the disposable tree VerifyPatchSet materialises a patch set into.
        // Only the second contains the proposal. A tester ant runs as its own task in the DAG, after
        // VerifyPatchSet has returned and disposed its scope — so it resolves to the first, and
        // "3 checks passed" was being recorded as though it said something about the patch.
        //
        // This does not move the tester onto the patched tree; it makes the tree it DID judge part
        // of the record, which is the same remedy v3.8.22's build verdicts needed. A reader can now
        // tell the two apart, and so can a verifier weighing this evidence.
        var judged = Workspaces.MissionWorkspaceScope.Current;
        var tree = judged?.MaterializedPatchSetId is { } patched
            ? $"patched tree (patch set {patched})"
            : judged is not null ? "mission workspace — UNPATCHED" : "the configured workspace";
        evidence.Add(new AntEvidence("workspace", "tree", tree));
        lines.Add($"checked in: {tree}");
        // Structural repair §3: the FULL identity of what was judged, when a revision was ambient —
        // revision id, patch set hash and tree hash, so this report can be paired with (or refused
        // against) a candidate artifact by comparison rather than by trust.
        if (judged?.RevisionId is { } revId)
        {
            evidence.Add(new AntEvidence("revision", revId,
                $"patch_set={judged.MaterializedPatchSetId} patch_set_hash={judged.PatchSetHash} tree_hash={judged.TreeHash}"));
            lines.Add($"revision: {revId} patch_set_hash: {judged.PatchSetHash} tree_hash: {judged.TreeHash}");
        }

        var report = new AntArtifact("test_report", "Deterministic check report", string.Join("\n", lines));
        var result = new AntExecutionResult
        {
            Success = allPassed,
            StatusCode = allPassed ? "succeeded" : "failed_retryable",
            Summary = $"{requested.Count} check(s): {lines.Count(l => l.Contains(": PASS"))} passed, "
                    + $"{lines.Count(l => l.Contains(": FAIL"))} failed, in {tree}.",
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
    /// <remarks>
    /// v0.3.8.57 — when the TASK names its inputs, those are the review material and nothing else.
    ///
    /// The mission-wide read below is correct for a soldier a planner wrote, which has no way to say
    /// which change it means. It is WRONG for the policy-inserted soldier: that task exists because
    /// one specific patch set was just written, and reviewing every patch set the mission has
    /// accumulated makes "the review passed" a claim about material the operator did not ask about.
    /// Worse, the count reported below would say "3 patch artifacts read" without saying which — so
    /// a clean scan of two stale sets and the live one looks identical to a clean scan of the live
    /// one alone.
    /// </remarks>
    private (string Material, int Count) ReadPatchSetArtifacts(Task task, Mission mission)
    {
        if (_artifacts is null) return ("", 0);
        try
        {
            if (task.InputArtifactIds.Count > 0)
            {
                // Declared inputs are read BY ID and not filtered by schema. A task told to review
                // an artifact reviews it; silently dropping one that is not a patch_set would make
                // the soldier disagree with the runtime about what it was given.
                var declared = task.InputArtifactIds
                    .Select(id => _artifacts.Get(id))
                    .Where(a => a is not null)
                    .Select(a => a!)
                    .ToList();
                // v0.3.8.63 (S5): the same check the context compiler applies, applied again at
                // this direct consumer — the review's rule was "at every direct consumer", because
                // the first site that forgets is the leak. Withheld inputs are NAMED, not dropped:
                // a soldier that reviews two of three declared artifacts must know it did.
                var readable = declared.Where(a => a.IsModelReadable).ToList();
                var secretNotes = declared.Where(a => !a.IsModelReadable)
                    .Select(a => $"[WITHHELD: declared input {a.Id} is Secret — payload not shown; the review proceeds without it]")
                    .ToList();
                if (readable.Count == 0 && secretNotes.Count == 0) return ("", 0);
                return (string.Join("\n", secretNotes.Concat(readable.Select(a => DecodeForScanning(a.Payload)))),
                    readable.Count);
            }

            var patches = _artifacts.ForMission(mission.Id, Anthill.SDK.Artifacts.ArtifactSchemas.PatchSet)
                .Where(p => p.IsModelReadable).ToList();
            return patches.Count == 0
                ? ("", 0)
                : (string.Join("\n", patches.Select(p => DecodeForScanning(p.Payload))), patches.Count);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[soldier] could not read patch artifacts for {mission.Id}: {error.Message}");
            return ("", 0);
        }
    }

    /// <summary>
    /// The patch artifact's VALUES, decoded — not its serialization. v0.3.8.71.
    ///
    /// THE DEFECT, and it is the most severe rule in the table. v3.8.25 gave the soldier the real
    /// patch, and its release note said exactly why: "the `secret_material` rule looks for
    /// `-----BEGIN PRIVATE KEY-----` and `api_key = "…"` in source, and source was the one thing it
    /// never saw." The patch arrived. The rule still could not see it.
    ///
    /// `RecordPatchArtifact` stores the proposals as JSON, so a proposal whose content is
    ///
    ///     api_key = "sk-live-9f3a2b7c4d1e"
    ///
    /// is in the payload as
    ///
    ///     "new_content": "…api_key = \"sk-live-9f3a2b7c4d1e\"…"
    ///
    /// and the rule's pattern requires a quote immediately after `[:=]\s*`. In the serialization the
    /// next character is a BACKSLASH. `secret_material` is critical and blocking, it is the rule the
    /// v3.8.26 note widened after a capital K let a secret through — and since v3.8.25 it has been
    /// structurally unable to fire on a quoted secret in patch content, because every quote in every
    /// payload is escaped. It could only ever match the task description, which is prose, which is
    /// the blind spot v3.8.25 existed to close.
    ///
    /// FOUND BY THE SCENARIO 7 FIXTURE, which proposed a runbook containing a credential and got an
    /// empty warnings list. The test was written to prove the block reaches the write; it proved the
    /// block never happened.
    ///
    /// EVERY STRING VALUE, RECURSIVELY, rather than the two field names this payload happens to use.
    /// The rules match paths and content both, the shape of the artifact is not this method's to
    /// know, and a decoder that reads named fields silently stops covering a field the day one is
    /// added — which is this defect's own shape a second time.
    ///
    /// A payload that will not parse is returned RAW rather than dropped: scanning the serialization
    /// is worse than scanning the values and far better than scanning nothing, and a malformed patch
    /// artifact is exactly when a review should be more suspicious, not less.
    /// </summary>
    internal static string DecodeForScanning(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            var values = new List<string>();
            Walk(doc.RootElement, values);
            return values.Count == 0 ? payload : string.Join("\n", values);
        }
        catch { return payload; }

        static void Walk(System.Text.Json.JsonElement element, List<string> into)
        {
            switch (element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String:
                    if (element.GetString() is { Length: > 0 } s) into.Add(s);
                    break;
                case System.Text.Json.JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        // The KEY too: a rule matching a field name is still a rule, and dropping
                        // keys would make this decoder decide which text is reviewable.
                        into.Add(property.Name);
                        Walk(property.Value, into);
                    }
                    break;
                case System.Text.Json.JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) Walk(item, into);
                    break;
            }
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
        var (patchMaterial, patchArtifactCount) = ReadPatchSetArtifacts(task, mission);

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
    private readonly ToolRegistry? _tools;

    /// <summary>
    /// v3.8.28: the registry, so the scribe can finally DISPATCH the one tool its contract grants it.
    ///
    /// `read_changed_files_summary` was built for this role in v3.5.0 and the scribe has never called
    /// it — it inferred changed files by running a regex over prior tasks' PROSE. Optional for the
    /// same reason the soldier's and verifier's dependencies are: existing call sites keep the
    /// previous behaviour rather than being rewritten to gain a capability they do not exercise.
    /// </summary>
    public ScribeAnt(ToolRegistry? tools = null) : base("scribe") => _tools = tools;

    private static readonly System.Text.RegularExpressions.Regex DocsPath =
        new(@"^(?:docs/[\w./\-]+\.md|README\.md|CHANGELOG\.md)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// What this mission actually changed, and where that answer came from.
    ///
    /// Returns the tool's answer when the tool can give one, and the prose-derived guess otherwise.
    /// The SOURCE is returned alongside because release notes built from a regex over other ants'
    /// summaries are a different artifact from release notes built from a diff, and an operator
    /// reading them must be able to tell which they have.
    /// </summary>
    private (List<string> Files, string Source) ReadChangedFiles(
        Mission mission, Task task, List<string> priorResults)
    {
        if (_tools is not null)
        {
            try
            {
                var run = _tools.RunTool("read_changed_files_summary", mission.Id, task.Id, Name, new());
                if (run.Success && !string.IsNullOrWhiteSpace(run.Output))
                {
                    var fromTool = System.Text.RegularExpressions.Regex
                        .Matches(run.Output, @"\b(?:src|docs|tests|scripts|deploy)/[\w./\-]+|README\.md|CHANGELOG\.md")
                        .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (fromTool.Count > 0) return (fromTool, "workspace_diff");
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"[scribe] changed-file summary unavailable for {mission.Id}: {error.Message}");
            }
        }

        var guessed = System.Text.RegularExpressions.Regex
            .Matches(string.Join("\n", priorResults) + "\n" + task.Description,
                     @"\b(?:src|docs|tests)/[\w./\-]+|README\.md|CHANGELOG\.md")
            .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (guessed, "mentioned_in_prose");
    }

    public override AntExecutionResult Execute(Task task, Mission mission)
    {
        var contract = AntExecutionCatalog.ContractFor("scribe")!;
        if (!contract.SupportsTaskType(task.TaskType))
            return AntExecutionResult.Blocked(
                $"task type '{task.TaskType}' is outside the scribe execution contract");

        // v0.3.8.57 (PLAN.md gate 8) — the scribe cannot CERTIFY what nobody verified.
        //
        // `verified_change_summary` is a task type whose OUTPUT ASSERTS a verification. Nothing
        // checked that one had happened, so a mission whose verifier never ran — or ran and failed —
        // could still produce a document telling the operator its change was verified. That document
        // then outlives the mission: it is the artifact a person reads and quotes, and it would have
        // been the most confident and least grounded thing the colony produced.
        //
        // ONLY this task type. A scribe writing release notes or a docs proposal mid-mission is
        // doing legitimate work and is not claiming anything about verification; blocking those
        // would be the gate widening past the sentence it was written to enforce.
        //
        // BLOCKED, not failed: verification arriving later cures this, and a failure would spend a
        // repair budget on something no repair addresses.
        if (string.Equals(task.TaskType, "verified_change_summary", StringComparison.OrdinalIgnoreCase)
            && !Outcomes.MissionVerification.IsSatisfied(mission.Tasks))
            return AntExecutionResult.Blocked(
                "a verified_change_summary asserts that this mission's change was verified, and it was "
              + $"not: {Outcomes.MissionVerification.Explain(mission.Tasks)}. The summary is refused "
              + "rather than written with the claim softened — a document that hedges about whether "
              + "verification happened is read as one that says it did.");

        var priorResults = mission.Tasks.Where(t => t.Id != task.Id && t.Result is not null)
            .Select(t => $"[{t.AssignedAnt}] {t.Title}: {t.ResultSummary ?? Truncate(t.Result!)}").ToList();
        // v3.8.28 — ASK THE WORKSPACE what changed, rather than pattern-matching prose about it.
        //
        // `read_changed_files_summary` was built for this role in v3.5.0 and the scribe never called
        // it. It ran the regex below over prior tasks' RESULT TEXT, so its "changed files" were
        // whatever paths an ant happened to mention — a file discussed but untouched was reported as
        // changed, and a file changed but not mentioned was invisible. Release notes assembled from
        // that describe a release that did not happen.
        //
        // The tool is authoritative when it answers. The regex stays as the fallback for the case it
        // explicitly refuses: no mission workspace in scope, where summarising the operator's own
        // uncommitted work as "what this mission changed" would be a confident, plausible lie.
        var (changedFiles, changedFilesSource) = ReadChangedFiles(mission, task, priorResults);

        var releaseNotes =
            $"Mission: {mission.Goal}\nCompleted stages: {priorResults.Count}\n" +
            $"changed_files_source: {changedFilesSource}\n" +
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
/// repair route; it never repairs anything itself and never applies changes.
///
/// STRUCTURAL REPAIR (§1). Rewritten around four defects the old shape carried:
///
/// <list type="bullet">
/// <item><b>§1A — it diagnosed the NEWEST failure, not its own.</b> The medic task is created by a
///   failure handoff and carries <c>ParentTaskIds</c> naming the failed task. That lineage is now
///   the binding: the medic diagnoses its parent or refuses. Under parallel execution, two medics
///   each diagnose their own parent; neither can steal the other's failure.</item>
/// <item><b>§1B — keyword classification.</b> The failure boundary now persists a typed
///   <c>failure_context</c> artifact (ExecutionService.RecordFailureContext). The medic CONSUMES it.
///   The keyword scan survives only as a last-resort fallback for failures recorded before the
///   artifact existed, and its unknown case is now honest.</item>
/// <item><b>§1C — unknown became InternalDefect(non-retryable).</b> Unknown stays
///   <see cref="FailureClass.UnknownFailure"/>; an unknown or low-confidence diagnosis escalates
///   for evidence rather than inventing a permanent verdict.</item>
/// <item><b>§1D — "ui"/".html"/"app.js" in error prose rerouted recovery.</b> Specialist selection
///   now derives from the failed task's TYPE, its producing role, its artifact KINDS and the
///   structured failure class. Words in prose route nothing.</item>
/// <item><b>§1E — dedupe keyed on task UUID.</b> The loop detector now keys on the failure_context's
///   SEMANTIC signature, which survives task regeneration; the same defect reappearing under a new
///   UUID escalates instead of looping.</item>
/// </list>
/// </summary>
public sealed class MedicAnt : BaseAnt
{
    public const int MaxDiagnosesPerMission = 2;

    private readonly Anthill.SDK.Artifacts.IArtifactStore? _artifacts;

    /// <summary>Optional store, same pattern as SoldierAnt: call sites without one keep working,
    /// and in that configuration the medic falls back to structured task state (never prose-first).</summary>
    public MedicAnt(Anthill.SDK.Artifacts.IArtifactStore? artifacts = null) : base("medic") =>
        _artifacts = artifacts;

    public override AntExecutionResult Execute(Task task, Mission mission)
    {
        var contract = AntExecutionCatalog.ContractFor("medic")!;
        if (!contract.SupportsTaskType(task.TaskType))
            return AntExecutionResult.Blocked(
                $"task type '{task.TaskType}' is outside the medic execution contract");

        // §1A — the diagnosis is BOUND to the failure that invoked it. The handoff path sets
        // ParentTaskIds to the failed source task; a medic that cannot identify its source failure
        // refuses rather than guessing at some other failure. (Blocked, not a diagnosis of the
        // newest failure — "do not inspect some other failure" is the requirement, verbatim.)
        var failed = (task.ParentTaskIds ?? new List<string>())
            .Concat(task.ParentTaskId is { } p ? new[] { p } : Array.Empty<string>())
            .Select(id => mission.Tasks.FirstOrDefault(t => t.Id == id))
            .FirstOrDefault(t => t is not null && t.Id != task.Id
                && (t.Status == TaskStatus.Failed || t.FailureReason is not null));
        if (failed is null)
            return AntExecutionResult.Blocked(
                "the source failure for this diagnosis cannot be identified from the task's parent "
                + "lineage — refusing to diagnose an unrelated failure. UNVERIFIED.");

        // Loop control 1: diagnosis budget per mission.
        var priorDiagnoses = mission.Tasks.Count(t => t.Id != task.Id && t.AssignedAnt == "medic" && t.Result is not null);
        if (priorDiagnoses >= MaxDiagnosesPerMission)
            return Escalation(mission, task,
                $"Diagnosis budget exhausted ({priorDiagnoses}/{MaxDiagnosesPerMission}) — escalating to operator, no further repair loops.",
                $"medic-esc:{mission.Id}");

        // §1B/§2 — the typed failure_context is the diagnosis input. Prose is the last resort.
        var context = LoadFailureContext(mission, failed);
        FailureClass cls; string cause, confidence, signature;
        if (context is not null)
        {
            cls = Anthill.SDK.Contracts.FailureClassNames.ParseOrNone(context.FailureClass);
            if (cls == FailureClass.None) cls = FailureClass.UnknownFailure;
            cause = context.NormalizedError.Length > 0 ? context.NormalizedError : "structured failure without error text";
            confidence = FailureClassify.IsKnown(cls) ? "high" : "low";
            signature = context.FailureSignature;
        }
        else
        {
            (cls, cause, confidence) = Classify((failed.FailureReason ?? "") + " " + (failed.Result ?? ""));
            signature = Anthill.SDK.Artifacts.FailureContext.ComputeSignature(
                Anthill.SDK.Contracts.FailureClassNames.Wire(cls),
                Anthill.SDK.Artifacts.FailureContext.NormalizeError(failed.FailureReason ?? failed.Result),
                null, null, null, null, null, null);
        }
        var retryable = FailureClassify.IsRetryable(cls);

        // §1E — loop control 2, keyed on the SEMANTIC failure signature. A prior occurrence of the
        // same signature means the same defect came back under a new task UUID without a materially
        // changed artifact: escalate, do not loop.
        //
        // v0.3.8.57 — READ FROM THE TYPED RECORD, not by grepping a previous medic's prose.
        //
        // This used to be `mission.Tasks.Any(t => t.AssignedAnt == "medic" && t.Result.Contains(
        // signature))` — the bound on repair loops, decided by a substring search of narrative text.
        // Task results are summarised and truncated (`ResultChars`, `MaxResultSummaryChars`), so a
        // long diagnosis whose signature fell past the cut silently stopped matching, and the loop
        // control quietly went away in exactly the missions that had produced the most output. Prose
        // as a control channel is the failure ADR-004 exists to end, and this was the last place in
        // the repair path where a bound depended on it.
        //
        // The failure_context artifacts already carry the signature as a field. Counting them
        // answers the same question from data that cannot be truncated into a different answer.
        var repeated = HasSeenSignatureBefore(mission, task, signature);
        if (repeated)
            return Escalation(mission, task,
                $"Semantic duplicate: failure signature {signature} was already diagnosed in this mission "
                + "and nothing material changed — escalating instead of looping.",
                $"medic-dup:{mission.Id}:{signature}", signature);

        // §1C — unknown/low-confidence never becomes a confident verdict. It escalates for
        // evidence; it does not invoke a repair specialist on a guess, and it is not "internal".
        if (!FailureClassify.IsKnown(cls))
            return Escalation(mission, task,
                "The failure is UNCLASSIFIED and the evidence is insufficient for a confident diagnosis "
                + "— escalating for evidence/operator review rather than guessing a repair route.",
                $"medic-unk:{mission.Id}:{signature}", signature);

        // §1D — specialist selection from STRUCTURE: policy says no → never route around it;
        // otherwise the failed task's type/role/artifact kinds pick the specialist. Prose picks nothing.
        var (targetRole, targetType, routeReason) = SelectSpecialist(cls, failed, context, retryable);

        var diagnosis =
            $"failure_signature: {signature}\nfailure_classification: {Anthill.SDK.Contracts.FailureClassNames.Wire(cls)}\n" +
            $"probable_cause: {cause}\nconfidence: {confidence}\nretryable: {retryable}\n" +
            $"failure_context: {(context is not null ? "consumed (typed artifact)" : "ABSENT — legacy prose fallback used")}\n" +
            $"recommended_role: {targetRole}\nrecommended_task_type: {targetType}\nroute_reason: {routeReason}\n" +
            $"verification_plan: fresh build/test of the repaired artifact, then verifier bound to its revision\n" +
            $"source_task: {failed.Id} ({failed.Title})";

        return new AntExecutionResult
        {
            Success = true,
            StatusCode = "succeeded",
            Summary = $"Diagnosis: {Anthill.SDK.Contracts.FailureClassNames.Wire(cls)} ({TextShort(cause)}) — route to {targetRole}.",
            // The full diagnosis becomes the task's recorded text; the signature must survive into
            // the record because loop control 2 matches it in PRIOR medic results.
            Narrative = diagnosis,
            Artifacts =
            {
                new AntArtifact("failure_diagnosis", "Failure diagnosis", diagnosis),
                new AntArtifact("repair_recommendation", "Bounded repair route", $"{targetRole}:{targetType} (single attempt, then fresh checks)"),
            },
            Evidence = { new AntEvidence("failure_id", failed.Id, failed.FailureReason ?? "structured failure in result"),
                         new AntEvidence("failure_signature", signature) },
            Handoffs = { new AntHandoff("medic", targetRole, $"repair route for {Anthill.SDK.Contracts.FailureClassNames.Wire(cls)}",
                targetType, new[] { "failure_diagnosis" }, true, 1, $"medic:{signature}") },
        };
    }

    /// <summary>§1D — the routing table, from structure. Never from words in error prose.</summary>
    internal static (string Role, string TaskType, string Reason) SelectSpecialist(
        FailureClass cls, Task failed, Anthill.SDK.Artifacts.FailureContext? context, bool retryable)
    {
        // Policy and security say NO — recovery escalates to the operator, never routes around.
        if (FailureClassify.MustEscalate(cls))
            return ("builder", "build", "policy/security/authorization denial — never routed around");

        // Transient classes retry the same deterministic surface.
        if (retryable)
            return ("tester", "test_execution", "transient failure class — bounded re-run");

        // A UI specialist is selected by ARTIFACT/TASK classification only: the failed task was
        // ui-typed work or worked over ui_map artifacts. The word "UI" in a message routes nothing.
        var uiTyped = failed.TaskType is "ui_mapping" or "frontend_check" or "route_mapping"
            or "component_mapping" or "style_mapping" or "frontend_dependency_mapping" or "ui_change_impact";
        var uiArtifacts = context?.ArtifactKinds.Contains("ui_map", StringComparer.OrdinalIgnoreCase) ?? false;
        if (uiTyped || (uiArtifacts && failed.AssignedAnt == "ui_cartographer"))
            return ("ui_cartographer", "ui_mapping", "failed task is ui-typed / produced ui artifacts");

        // Deterministic check failures over code work → the coder repairs, then fresh checks.
        if (cls is FailureClass.BuildFailure or FailureClass.TestFailure or FailureClass.VerificationFailure
            or FailureClass.PatchConflict)
            return ("coder", "code_change", "deterministic check failed over code work — repair then fresh checks");

        if (cls is FailureClass.ValidationFailure or FailureClass.InvalidArtifact)
            return ("coder", "code_change", "structurally invalid artifact — producer must re-produce");

        // Provider/model/dependency/tool problems are environmental: no repair specialist can fix
        // them by editing code. Escalate with the classification visible.
        return ("builder", "build", "environmental failure — operator/provider recovery, not a code repair");
    }

    private AntExecutionResult Escalation(Mission mission, Task task, string message, string dedupeKey, string? signature = null)
        => new()
        {
            Success = true, StatusCode = "succeeded_with_warnings",
            Summary = message,
            Narrative = message + (signature is not null ? $"\nfailure_signature: {signature}" : ""),
            Artifacts = { new AntArtifact("failure_diagnosis", "Escalation", message) },
            Handoffs = { new AntHandoff("medic", "builder", "escalation to operator", "build",
                new[] { "failure_diagnosis" }, true, 1, dedupeKey) },
            Warnings = { "escalated" },
        };

    /// <summary>
    /// Has this exact failure signature already been recorded for a DIFFERENT task? v0.3.8.57.
    ///
    /// The bound on repair looping. A signature is semantic — failure class plus normalised error
    /// plus the identifying fields — so a second occurrence under a new task id means the same defect
    /// came back and nothing material changed.
    ///
    /// FALLS BACK TO THE PROSE SCAN when there is no artifact store, and only then. Dozens of tests
    /// and the CLI construct a medic without one, and in that configuration the old behaviour is
    /// still better than no bound at all. Where a store exists the typed record is authoritative,
    /// because it is the one that cannot be truncated into disagreeing with itself.
    /// </summary>
    private bool HasSeenSignatureBefore(Mission mission, Task task, string signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;

        if (_artifacts is null)
            return mission.Tasks.Any(t => t.Id != task.Id && t.AssignedAnt == "medic"
                && (t.Result?.Contains(signature, StringComparison.Ordinal) ?? false));

        try
        {
            // Distinct TASKS, not distinct artifacts: one failing task can record more than one
            // context across attempts, and counting those would escalate a single failure on its
            // own retry — turning a bounded repair into no repair at all.
            var tasksWithThisSignature = _artifacts
                .ForMission(mission.Id, Anthill.SDK.Artifacts.ArtifactSchemas.FailureContext)
                .Select(a => (a.TaskId, Context: Anthill.SDK.Artifacts.FailureContext.FromJson(a.Payload)))
                .Where(x => x.Context is not null
                         && string.Equals(x.Context!.FailureSignature, signature, StringComparison.Ordinal))
                .Select(x => x.TaskId ?? "")
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // More than one distinct task has failed this way. The current failure is one of them,
            // so "seen before" means at least two.
            return tasksWithThisSignature.Count > 1;
        }
        catch (Exception error)
        {
            // An unreadable store must not silently REMOVE the bound. Fall back to the prose scan,
            // which is weaker but is a bound, and say that the strong one was unavailable.
            Console.Error.WriteLine(
                $"[medic] could not read failure_context signatures for {mission.Id}: {error.Message} "
              + "— falling back to the narrative scan for loop control");
            return mission.Tasks.Any(t => t.Id != task.Id && t.AssignedAnt == "medic"
                && (t.Result?.Contains(signature, StringComparison.Ordinal) ?? false));
        }
    }

    /// <summary>The typed failure record for THIS failed task, if the boundary produced one.</summary>
    private Anthill.SDK.Artifacts.FailureContext? LoadFailureContext(Mission mission, Task failed)
    {
        if (_artifacts is null) return null;
        try
        {
            return _artifacts
                .ForMission(mission.Id, Anthill.SDK.Artifacts.ArtifactSchemas.FailureContext)
                .Where(a => a.TaskId == failed.Id)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => Anthill.SDK.Artifacts.FailureContext.FromJson(a.Payload))
                .FirstOrDefault(c => c is not null);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[medic] could not read failure_context for {failed.Id}: {error.Message}");
            return null;
        }
    }

    private static string TextShort(string s) => s.Length <= 120 ? s : s[..120] + "…";

    /// <summary>
    /// LEGACY fallback classifier, used only when no failure_context artifact exists (failures
    /// recorded by older builds, or call sites constructed without a store). §1C: the unmatched
    /// case is now honestly <see cref="FailureClass.UnknownFailure"/> at low confidence — it is
    /// NEVER InternalDefect, and the caller escalates it instead of routing a repair.
    /// </summary>
    internal static (FailureClass, string, string) Classify(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("timed out") || t.Contains("timeout")) return (FailureClass.Timeout, "operation exceeded its time budget", "high");
        if (t.Contains("rate limit") || t.Contains("429")) return (FailureClass.RateLimit, "provider rate limiting", "high");
        if (t.Contains("unreachable") || t.Contains("connection") || t.Contains("transient")) return (FailureClass.TransientProviderFailure, "backing service unavailable", "medium");
        if (t.Contains("authorization_denied") || t.Contains("permission")) return (FailureClass.AuthorizationFailure, "capability/tool boundary denied the operation", "high");
        if (t.Contains("exit_code=") || t.Contains(": fail") || t.Contains("build") || t.Contains("test")) return (FailureClass.VerificationFailure, "deterministic check failed", "high");
        if (t.Contains("invalid") || t.Contains("validation")) return (FailureClass.ValidationFailure, "input failed validation", "medium");
        return (FailureClass.UnknownFailure, "unclassified failure — evidence insufficient; unknown stays unknown", "low");
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

    /// <summary>
    /// What never reaches memory.
    ///
    /// v0.3.8.72 examined this alongside <c>PolicyScan.secret_material</c> and deliberately left the
    /// pattern alone. It is written against SOURCE, its inputs are plain strings — a mission goal, a
    /// task title, a failure reason — and it never sees an artifact payload. Teaching it to squint
    /// through an encoding it is never handed would be carrying the cost of the other site's defect
    /// without its cause, and encoding-aware patterns are what that release concluded against.
    ///
    /// The failure direction is worth stating because it is the opposite of the soldier's: a miss
    /// here does not decline to block a patch, it writes a secret into a durable memory candidate.
    /// So if a serialized payload ever DOES reach this, the fix is to decode at that call site, the
    /// way <c>SoldierAnt.DecodeForScanning</c> does — not to widen this.
    /// </summary>
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
