using Anthill.Core.Common;
using Anthill.Core.Domain;

namespace Anthill.Core.Agents;

public sealed record AntPermissionContract(
    bool ReadWorkspace,
    bool WriteWorkspace,
    bool ReadMemory,
    bool WriteMemory,
    bool UseWeb,
    bool RunShell,
    bool RunAllowlistedChecks,
    bool ProposePatches,
    bool ApplyPatches);

public sealed record AntWorkerDefinition(
    string WorkerId,
    string DisplayName,
    string ParentRoleId,
    string Purpose,
    bool Enabled,
    AntPermissionContract Permissions,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> ForbiddenTools);

public sealed record AntRoleDefinition(
    string RoleId,
    string DisplayName,
    string Colony,
    string Purpose,
    bool Enabled,
    bool Executable,
    AntPermissionContract Permissions,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> ForbiddenTools,
    IReadOnlyList<string> AllowedPaths,
    IReadOnlyList<string> ForbiddenPaths,
    IReadOnlyList<AntWorkerDefinition> Workers);

public sealed record AntSelectionResult(bool Allowed, string Reason);

public static class AntRegistry
{
    private static readonly AntPermissionContract None = new(false, false, false, false, false, false, false, false, false);
    private static readonly AntPermissionContract ReadMemory = None with { ReadMemory = true };
    private static readonly AntPermissionContract ReadWorkspace = ReadMemory with { ReadWorkspace = true };
    private static readonly AntPermissionContract ProposeBackend = ReadWorkspace with { ProposePatches = true };
    private static readonly AntPermissionContract Web = ReadMemory with { UseWeb = true };
    private static readonly AntPermissionContract Checks = ReadMemory with { RunAllowlistedChecks = true };
    private static readonly AntPermissionContract WriteMemory = ReadMemory with { WriteMemory = true };

    public static readonly IReadOnlyList<AntRoleDefinition> Roles = BuildRoles();
    public static readonly IReadOnlyDictionary<string, AntRoleDefinition> ByRole =
        Roles.ToDictionary(r => r.RoleId, StringComparer.OrdinalIgnoreCase);
    public static readonly IReadOnlyDictionary<string, AntWorkerDefinition> ByWorker =
        Roles.SelectMany(r => r.Workers).ToDictionary(w => w.WorkerId, StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlySet<string> ExecutableRoleIds =
        Roles.Where(r => r.Executable && r.Enabled).Select(r => r.RoleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static AntWorkerDefinition? DefaultWorkerFor(string roleId, string taskType = "", string goal = "")
    {
        if (!ByRole.TryGetValue(roleId, out var role)) return null;
        var text = $"{taskType} {goal}".ToLowerInvariant();
        return role.RoleId switch
        {
            "researcher" => role.Workers.FirstOrDefault(w => text.Contains("mission") || text.Contains("history") ? w.WorkerId.EndsWith("mission_researcher") : w.WorkerId.EndsWith("repo_researcher")),
            "web" => role.Workers.FirstOrDefault(w => text.Contains("verify") || text.Contains("source quality") ? w.WorkerId.EndsWith("source_verifier") : w.WorkerId.EndsWith("source_finder")),
            "file" => role.Workers.FirstOrDefault(w => text.Contains("read") || text.Contains("snippet") ? w.WorkerId.EndsWith("file_reader") : w.WorkerId.EndsWith("file_scout")),
            "coder" => role.Workers.FirstOrDefault(w =>
                (text.Contains("ui") || text.Contains("frontend") || text.Contains("canvas") || text.Contains("html") || text.Contains("css") || text.Contains("javascript")) ? w.WorkerId.EndsWith("ui_coder") :
                (text.Contains("doc") || text.Contains("readme") || text.Contains("changelog") || text.Contains(".md")) ? w.WorkerId.EndsWith("docs_coder") :
                w.WorkerId.EndsWith("backend_coder")),
            "builder" => role.Workers.FirstOrDefault(w => text.Contains("compile") || text.Contains("data") ? w.WorkerId.EndsWith("result_compiler") : w.WorkerId.EndsWith("response_builder")),
            "verifier" => role.Workers.FirstOrDefault(w => text.Contains("safety") || text.Contains("risk") ? w.WorkerId.EndsWith("safety_verifier") : w.WorkerId.EndsWith("result_verifier")),
            _ => role.Workers.FirstOrDefault()
        };
    }

    public static AntSelectionResult ValidateTask(Task task, MissionConstraints constraints)
    {
        if (!ByRole.TryGetValue(task.AssignedAnt, out var role))
            return new(false, $"Unknown ant role: {task.AssignedAnt}");
        if (!role.Enabled)
            return new(false, $"Ant role is disabled: {task.AssignedAnt}");
        if (!role.Executable)
            return new(false, $"Ant role is visible-only in this revision: {task.AssignedAnt}");
        if (string.IsNullOrWhiteSpace(task.AssignedWorker))
            task.AssignedWorker = DefaultWorkerFor(task.AssignedAnt, task.TaskType, task.Description)?.WorkerId;
        if (!string.IsNullOrWhiteSpace(task.AssignedWorker))
        {
            if (!ByWorker.TryGetValue(task.AssignedWorker, out var worker))
                return new(false, $"Unknown ant worker: {task.AssignedWorker}");
            if (!string.Equals(worker.ParentRoleId, task.AssignedAnt, StringComparison.OrdinalIgnoreCase))
                return new(false, $"Worker {task.AssignedWorker} does not belong to role {task.AssignedAnt}");
            if (!worker.Enabled)
                return new(false, $"Ant worker is disabled: {task.AssignedWorker}");
            if (Exceeds(worker.Permissions, role.Permissions))
                return new(false, $"Worker permissions exceed parent role: {task.AssignedWorker}");
        }
        if (constraints.BlocksPatches && (role.Permissions.ProposePatches || task.TaskType is "patch_proposal" or "patch" or "code_change"))
            return new(false, "Mission constraints block patch proposal tasks.");
        if (task.AssignedAnt == "web" && !role.Permissions.UseWeb)
            return new(false, "WebAnt is not allowed to use web tools.");
        return new(true, "allowed");
    }

    public static List<string> ValidateRegistry()
    {
        var errors = new List<string>();
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in Roles)
        {
            if (!roles.Add(role.RoleId)) errors.Add($"Duplicate role id: {role.RoleId}");
            if (string.IsNullOrWhiteSpace(role.Colony)) errors.Add($"Role missing colony: {role.RoleId}");
            if (role.Permissions.ApplyPatches) errors.Add($"Role may not apply patches: {role.RoleId}");
            foreach (var worker in role.Workers)
            {
                if (!workers.Add(worker.WorkerId)) errors.Add($"Duplicate worker id: {worker.WorkerId}");
                if (!string.Equals(worker.ParentRoleId, role.RoleId, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Worker parent mismatch: {worker.WorkerId}");
                if (worker.Permissions.ApplyPatches) errors.Add($"Worker may not apply patches: {worker.WorkerId}");
                if (Exceeds(worker.Permissions, role.Permissions))
                    errors.Add($"Worker permissions exceed parent: {worker.WorkerId}");
            }
        }
        return errors;
    }

    private static bool Exceeds(AntPermissionContract child, AntPermissionContract parent) =>
        (child.ReadWorkspace && !parent.ReadWorkspace) ||
        (child.WriteWorkspace && !parent.WriteWorkspace) ||
        (child.ReadMemory && !parent.ReadMemory) ||
        (child.WriteMemory && !parent.WriteMemory) ||
        (child.UseWeb && !parent.UseWeb) ||
        (child.RunShell && !parent.RunShell) ||
        (child.RunAllowlistedChecks && !parent.RunAllowlistedChecks) ||
        (child.ProposePatches && !parent.ProposePatches) ||
        (child.ApplyPatches && !parent.ApplyPatches);

    private static IReadOnlyList<AntRoleDefinition> BuildRoles()
    {
        AntWorkerDefinition W(string parent, string id, string name, string purpose, AntPermissionContract perms, string[] allowed, string[] forbidden) =>
            new($"{parent}.{id}", name, parent, purpose, true, perms, allowed, forbidden);
        AntRoleDefinition R(string id, string name, string colony, string purpose, bool exec, AntPermissionContract perms,
            string[] allowed, string[] forbidden, params AntWorkerDefinition[] workers) =>
            new(id, name, colony, purpose, true, exec, perms, allowed, forbidden, Array.Empty<string>(), new[] { "py.old/", ".git/", "data/", ".venv/" }, workers);

        var noApply = new[] { "apply_patch", "python_file_creation", "python_file_modification" };
        return new List<AntRoleDefinition>
        {
            R("queen", "Queen", "Core", "Central mission authority and final orchestration layer.", false, ReadMemory with { WriteMemory = true }, new[] { "read_mission", "write_mission_state" }, noApply),
            R("director", "Director", "Core", "Autonomy objective lifecycle, backlog, loop detection, and stop reasons.", false, ReadMemory with { WriteMemory = true }, new[] { "read_objective_state", "write_objective_state", "create_autonomy_mission" }, noApply),
            R("planner", "PlannerAnt", "Command", "Creates and validates focused mission DAGs.", false, ReadMemory, new[] { "read_mission_prompt", "read_ant_registry", "read_memory_summary" }, noApply,
                W("planner", "mission_planner", "MissionPlanner", "Create small focused task DAGs.", ReadMemory, new[] { "read_mission_prompt", "read_ant_registry" }, noApply),
                W("planner", "dependency_mapper", "DependencyMapper", "Validate task dependencies before execution.", ReadMemory, new[] { "read_planned_tasks", "read_mission_constraints" }, noApply)),
            R("constraint", "ConstraintAnt", "Command / Safety", "Enforces mission boundaries before risky work executes.", false, ReadMemory, new[] { "read_policy_rules", "read_ant_registry" }, noApply,
                W("constraint", "scope_guard", "ScopeGuard", "Detect no-patch, read-only, verification-only, and language boundaries.", ReadMemory, new[] { "read_mission_prompt", "read_policy_rules" }, noApply),
                W("constraint", "tool_guard", "ToolGuard", "Check requested tools against role permissions.", ReadMemory, new[] { "read_task_plan", "read_permission_contracts" }, noApply)),
            R("researcher", "ResearcherAnt", "Context", "Understands repository, mission, prior results, and architectural intent.", true, ReadWorkspace, new[] { "read_workspace_docs", "read_memory" }, noApply,
                W("researcher", "repo_researcher", "RepoResearcher", "Read repo-level docs and architecture context.", ReadWorkspace, new[] { "read_workspace_docs", "read_readme", "read_changelog" }, noApply),
                W("researcher", "mission_researcher", "MissionResearcher", "Read prior mission and objective memory.", ReadMemory, new[] { "read_mission_history", "read_pheromone_summary" }, noApply)),
            R("file", "FileAnt", "Workspace", "Finds and reads relevant workspace files, read-only by default.", true, ReadWorkspace, new[] { "list_workspace_files", "read_workspace_file" }, noApply,
                W("file", "file_scout", "FileScout", "Find relevant files and folders.", ReadWorkspace, new[] { "list_workspace_files", "search_workspace_files" }, noApply),
                W("file", "file_reader", "FileReader", "Read exact file content or snippets.", ReadWorkspace, new[] { "read_text_file", "read_file_snippet" }, noApply)),
            R("web", "WebAnt", "External Research", "Performs external public research when explicitly allowed.", true, Web, new[] { "web_search", "read_public_source" }, noApply,
                W("web", "source_finder", "SourceFinder", "Find relevant public sources.", Web, new[] { "web_search", "open_public_source" }, noApply),
                W("web", "source_verifier", "SourceVerifier", "Check source relevance and authority.", Web, new[] { "read_public_source", "compare_sources" }, noApply)),
            R("coder", "CoderAnt", "Code", "Creates structured patch proposals only. Never applies changes.", true, ProposeBackend, new[] { "read_workspace_files", "create_patch_proposal" }, noApply,
                W("coder", "backend_coder", "BackendCoder", "C#/.NET backend, API, runtime, config, and tests patch proposals.", ProposeBackend, new[] { "read_backend_files", "create_patch_proposal" }, noApply),
                W("coder", "ui_coder", "UICoder", "Frontend/UI, styling, routes, dashboards, and visualizations patch proposals.", ProposeBackend, new[] { "read_frontend_files", "create_patch_proposal" }, noApply),
                W("coder", "docs_coder", "DocsCoder", "Docs, README, changelog, and operator documentation patch proposals.", ProposeBackend, new[] { "read_docs", "create_docs_patch_proposal" }, noApply)),
            R("ui_cartographer", "UICartographerAnt", "UI", "Maps the frontend before UI changes are proposed.", false, ReadWorkspace with { WriteMemory = true }, new[] { "read_frontend_routes", "read_component_files" }, noApply,
                W("ui_cartographer", "route_mapper", "RouteMapper", "Find pages, routes, navigation, and layout structure.", ReadWorkspace, new[] { "read_frontend_routes", "read_navigation_files" }, noApply),
                W("ui_cartographer", "component_mapper", "ComponentMapper", "Find components, styling, API hooks, and implementation points.", ReadWorkspace, new[] { "read_component_files", "read_style_files" }, noApply)),
            R("builder", "BuilderAnt", "Output", "Compiles task outputs into final operator-facing responses.", true, ReadMemory, new[] { "read_task_outputs", "read_patch_metadata" }, noApply,
                W("builder", "response_builder", "ResponseBuilder", "Write final answer or mission report.", ReadMemory, new[] { "read_task_outputs", "read_verifier_output" }, noApply),
                W("builder", "result_compiler", "ResultCompiler", "Compile structured mission data, checks, warnings, and result.", ReadMemory, new[] { "read_task_outputs", "read_event_log" }, noApply)),
            R("verifier", "VerifierAnt", "Verification", "Checks result quality, correctness, safety, and completeness.", true, ReadMemory, new[] { "read_task_outputs", "read_patch_metadata" }, noApply,
                W("verifier", "result_verifier", "ResultVerifier", "Check final result quality and completeness.", ReadMemory, new[] { "read_mission_result", "read_task_outputs" }, noApply),
                W("verifier", "safety_verifier", "SafetyVerifier", "Check constraints, approval boundaries, and risky claims.", ReadMemory, new[] { "read_mission_constraints", "read_patch_metadata" }, noApply)),
            R("tester", "TesterAnt", "Testing", "Runs or interprets allowlisted verification checks.", false, Checks, new[] { "run_allowlisted_check", "read_test_summary" }, noApply,
                W("tester", "dotnet_tester", "DotnetTester", "Run or interpret .NET build/test checks.", Checks, new[] { "run_allowlisted_check", "read_test_errors" }, noApply),
                W("tester", "frontend_tester", "FrontendTester", "Run or interpret frontend checks when present.", Checks, new[] { "run_allowlisted_check", "read_frontend_build_errors" }, noApply)),
            R("soldier", "SoldierAnt", "Security", "Security sentinel for runtime and patch risk boundaries.", false, ReadWorkspace, new[] { "read_permission_contracts", "read_patch_metadata" }, noApply,
                W("soldier", "runtime_sentinel", "RuntimeSentinel", "Check runtime/security-sensitive operations.", ReadWorkspace, new[] { "read_config_file", "read_permission_contracts" }, noApply),
                W("soldier", "patch_sentinel", "PatchSentinel", "Check patch path, language, and approval boundaries.", ReadWorkspace, new[] { "read_patch_metadata", "read_permission_contracts" }, noApply)),
            R("medic", "MedicAnt", "Repair", "Diagnoses failures and routes focused repair attempts.", false, ReadMemory, new[] { "read_failure_events", "read_test_errors" }, noApply,
                W("medic", "failure_diagnoser", "FailureDiagnoser", "Analyze build/test/runtime failures.", ReadMemory, new[] { "read_failure_events", "read_test_errors" }, noApply),
                W("medic", "fix_router", "FixRouter", "Route a small repair task to the right worker.", ReadMemory, new[] { "read_failure_summary", "read_ant_registry" }, noApply)),
            R("archivist", "ArchivistAnt", "Memory", "Stores durable mission memory and operator rules.", false, WriteMemory, new[] { "write_memory_summary", "write_memory_rule" }, noApply,
                W("archivist", "memory_archivist", "MemoryArchivist", "Store useful durable mission memory.", WriteMemory, new[] { "read_mission_result", "write_memory_summary" }, noApply),
                W("archivist", "rule_archivist", "RuleArchivist", "Store durable operator rules and deprecated boundaries.", WriteMemory, new[] { "read_operator_rules", "write_memory_rule" }, noApply)),
            R("quartermaster", "QuartermasterAnt", "Resources", "Resource pressure, queue depth, provider pressure, and concurrency advice.", false, WriteMemory, new[] { "read_resource_metrics", "read_provider_metrics" }, noApply,
                W("quartermaster", "resource_monitor", "ResourceMonitor", "Summarize system/resource/provider pressure.", WriteMemory, new[] { "read_resource_metrics", "read_queue_depth" }, noApply),
                W("quartermaster", "concurrency_advisor", "ConcurrencyAdvisor", "Recommend safe effective concurrency and throttling.", WriteMemory, new[] { "read_scheduler_state", "read_active_missions" }, noApply)),
            R("scribe", "ScribeAnt", "Communication / Docs", "Operator reports, changelogs, release notes, and summaries.", false, ReadWorkspace with { ReadMemory = true, WriteMemory = true, ProposePatches = true }, new[] { "read_changed_files_summary", "create_docs_patch_proposal_when_allowed" }, noApply,
                W("scribe", "changelog_scribe", "ChangelogScribe", "Draft changelog and release note entries.", ReadWorkspace with { ReadMemory = true, WriteMemory = true, ProposePatches = true }, new[] { "read_changed_files_summary", "create_docs_patch_proposal_when_allowed" }, noApply),
                W("scribe", "operator_scribe", "OperatorScribe", "Create concise operator-facing summaries.", WriteMemory, new[] { "read_mission_result", "read_test_summary" }, noApply)),

            // ---- Homelab colony (v1.9.0, NORTH_STAR Phase 4) --------------------------------------
            // Read-only, visible-only (Executable: false — the planner can never assign them tasks in
            // v1.9.0). Their deterministic data collection is plain C# service code (HomelabScheduler +
            // providers), never routed through the model router; LLM behavior arrives later strictly
            // for explanation/summarization/recommendation (NORTH_STAR §3.2 rules 5-6).
            R("inventory", "InventoryAnt", "Homelab", "Knows what exists: hosts, VMs, containers, storage, and services.", false, ReadMemory, new[] { "read_homelab_inventory" }, noApply),
            R("network_scout", "NetworkScoutAnt", "Homelab", "Knows the network shape: devices, subnets, VLANs, and unknown arrivals.", false, ReadMemory, new[] { "read_network_inventory" }, noApply),
            R("health", "HealthAnt", "Homelab", "Knows what is alive, degraded, or broken from health-check history.", false, ReadMemory, new[] { "read_health_results" }, noApply),
            R("proxmox", "ProxmoxAnt", "Homelab", "Knows the Proxmox cluster read-only: nodes, VMs, LXCs, and their state.", false, ReadMemory, new[] { "read_proxmox_inventory" }, noApply),
            R("storage", "StorageAnt", "Homelab", "Knows pools, disks, capacity, and SMART state.", false, ReadMemory, new[] { "read_storage_inventory" }, noApply),
            R("backup", "BackupAnt", "Homelab", "Knows what is protected, what is stale, and what is not backed up.", false, ReadMemory, new[] { "read_backup_inventory" }, noApply),
            R("security_scout", "SecurityScoutAnt", "Homelab", "Knows exposure and risk findings: open ports, unknown devices, exposed services.", false, ReadMemory, new[] { "read_risk_findings" }, noApply),
            R("change_archivist", "ChangeArchivistAnt", "Homelab", "Keeps the homelab change log and links changes to incidents and missions.", false, WriteMemory, new[] { "read_change_log", "write_change_summary" }, noApply),
        };
    }
}
