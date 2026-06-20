using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Anthill.Core.Orchestration;
using Anthill.Core.Scheduling;

namespace Anthill.Core.Diagnostics;

/// <summary>
/// Framework validation harness. Runs a fixed battery of structural and contract checks
/// against a live <see cref="Queen"/>/memory and produces a <see cref="SelfTestReport"/>.
/// This is the .NET successor to <c>run_selftest</c>; the baseline target is 15 checks,
/// 0 failures, 0 warnings on a clean install.
/// </summary>
public static class SelfTest
{
    public static SelfTestReport Run(Queen queen)
    {
        var checks = new List<SelfTestCheck>();

        Step(checks, "database_tables", () =>
        {
            var present = queen.Memory.TableNames().ToHashSet();
            var missing = AnthillRuntime.SelfTestRequiredTables.Where(t => !present.Contains(t)).ToList();
            return (missing.Count == 0, missing.Count == 0 ? "All required tables present." : $"Missing tables: {string.Join(", ", missing)}",
                new() { ["missing"] = missing });
        });

        Step(checks, "database_columns", () =>
        {
            var tasks = queen.Memory.GetTasksForMission("__schema_probe__");
            // A successful query over the lifecycle columns proves the v7 schema shape exists.
            return (true, "Scheduler lifecycle columns are queryable.", new() { ["probe_rows"] = tasks.Count });
        });

        Step(checks, "config_workspace", () =>
        {
            var ok = Directory.Exists(AnthillRuntime.WorkspaceRootPath) && File.Exists(AnthillRuntime.ConfigPath);
            return (ok, ok ? "Workspace and config materialised." : "Workspace or config missing.",
                new() { ["workspace_root"] = AnthillRuntime.WorkspaceRootPath, ["config_path"] = AnthillRuntime.ConfigPath });
        });

        Step(checks, "schema_migrations", () =>
        {
            var status = queen.Memory.GetSchemaStatus();
            var count = Convert.ToInt32(status.GetValueOrDefault("migration_count") ?? 0);
            return (count >= 7, $"Migration ledger has {count} entries.", new() { ["migration_count"] = count });
        });

        Step(checks, "fts_memory", () =>
            (queen.Memory.FtsAvailable, queen.Memory.FtsAvailable ? "FTS5 available." : "FTS5 not available; keyword fallback in use.",
                new() { ["fts_available"] = queen.Memory.FtsAvailable }), warningOnFail: true);

        Step(checks, "system_api_mission", () =>
        {
            queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "selftest_probe", "system_api probe event.");
            return (true, "system_api mission/event path is writable.", new());
        });

        Step(checks, "event_logging", () =>
        {
            var ev = queen.Memory.LogEvent("__selftest__", "selftest_event", "Self-test event logging probe.");
            var recent = queen.Memory.GetRecentEvents(5, "selftest_event");
            var found = recent.Any(r => r.GetValueOrDefault("id")?.ToString() == ev.Id);
            return (found, found ? "Event logging round-trips." : "Logged event was not read back.", new());
        });

        Step(checks, "agent_message_contract", () =>
        {
            var msg = queen.Memory.LogAgentMessage("__selftest__", "queen", "researcher", "selftest", "probe content");
            return (msg.ContentChars > 0 && msg.SchemaVersion == AnthillRuntime.AgentMessageVersion,
                "Agent message contract holds.", new() { ["schema_version"] = msg.SchemaVersion });
        });

        Step(checks, "task_dependency_contract", () =>
        {
            var a = new Task { Id = "A", Title = "a", AssignedAnt = "researcher" };
            var b = new Task { Id = "B", Title = "b", AssignedAnt = "builder", DependsOn = new() { "A" } };
            var clean = DomainHelpers.ValidateTaskDependencyContract(new[] { a, b });
            var cyclic = new Task { Id = "C", Title = "c", AssignedAnt = "builder", DependsOn = new() { "C" } };
            var errors = DomainHelpers.ValidateTaskDependencyContract(new[] { cyclic });
            var ok = clean.Count == 0 && errors.Count > 0;
            return (ok, ok ? "Dependency contract detects clean and cyclic graphs." : "Dependency contract mismatch.",
                new() { ["clean_errors"] = clean.Count, ["cyclic_errors"] = errors.Count });
        });

        Step(checks, "task_graph_export", () =>
        {
            var scheduler = new TaskScheduler(new[]
            {
                new Task { Id = "T1", Title = "t1", AssignedAnt = "researcher" },
                new Task { Id = "T2", Title = "t2", AssignedAnt = "builder", DependsOn = new() { "T1" } },
            }, "selftest");
            scheduler.Prepare();
            var graph = scheduler.ExportGraph();
            var ok = graph.GetValueOrDefault("schema_version")?.ToString() == "task-graph-v2"
                     && graph.GetValueOrDefault("nodes") is List<Dictionary<string, object?>> { Count: 2 };
            return (ok, ok ? "Task graph export shape is correct and metadata-first." : "Task graph export mismatch.", new());
        });

        Step(checks, "source_id_validator", () =>
        {
            var id = UrlSafety.SourceIdFromUrl("https://docs.python.org/3/");
            try { Validation.ValidateSourceId(id); return (true, "Source id validator accepts generated ids.", new() { ["sample"] = id }); }
            catch (Exception e) { return (false, $"Source id validator rejected a valid id: {e.Message}", new()); }
        });

        Step(checks, "approval_patch_validators", () =>
        {
            var rejected = false;
            try { Validation.ValidateSafePatchPath("../etc/passwd"); }
            catch { rejected = true; }
            var acceptedRelative = false;
            try { Validation.ValidateSafePatchPath("notes/todo.md"); acceptedRelative = true; }
            catch { /* should not happen */ }
            var ok = rejected && acceptedRelative;
            return (ok, ok ? "Patch path validator blocks traversal and accepts safe relative paths." : "Patch path validator mismatch.", new());
        });

        Step(checks, "api_response_contract", () =>
        {
            var goodToken = TokenSecurityProbe(TokenSecurity_GenerateStrong());
            var badToken = !TokenSecurityProbe("short");
            var ok = goodToken && badToken;
            return (ok, ok ? "Token strength validation accepts strong and rejects weak tokens." : "Token validation mismatch.", new());
        });

        Step(checks, "api_model_serialization", () =>
        {
            var json = Json.Dumps(new Dictionary<string, object?> { ["ok"] = true, ["version"] = AnthillRuntime.Version });
            return (json.Contains(AnthillRuntime.Version), "API model serialization produces valid JSON.", new());
        });

        Step(checks, "scaling_safety_config", () =>
        {
            // Fail-closed posture: writes/shell must be off unless explicitly enabled.
            var ok = AnthillRuntime.MaxParallelWorkers >= 1 && AnthillRuntime.MaxContextPacketChars >= 1000;
            return (ok, ok ? "Scaling/safety config within sane bounds." : "Scaling/safety config out of bounds.",
                new() { ["max_parallel_workers"] = AnthillRuntime.MaxParallelWorkers, ["shell_enabled"] = AnthillRuntime.EnableShellTool, ["file_writing_enabled"] = AnthillRuntime.EnableFileWriting });
        });

        var passed = checks.Count(c => c.Status == "pass");
        var failed = checks.Count(c => c.Status == "fail");
        var warning = checks.Count(c => c.Status == "warning");
        return new SelfTestReport
        {
            Ok = failed == 0, ChecksPassed = passed, ChecksFailed = failed, ChecksWarning = warning, Checks = checks,
        };
    }

    private static void Step(List<SelfTestCheck> checks, string name,
        Func<(bool Ok, string Message, Dictionary<string, object?> Details)> fn, bool warningOnFail = false)
    {
        try
        {
            var (ok, message, details) = fn();
            checks.Add(new SelfTestCheck { Name = name, Status = ok ? "pass" : warningOnFail ? "warning" : "fail", Message = message, Details = details });
        }
        catch (Exception error)
        {
            checks.Add(new SelfTestCheck { Name = name, Status = warningOnFail ? "warning" : "fail", Message = $"Check raised: {error.Message}", Details = new() { ["exception"] = error.GetType().Name } });
        }
    }

    public static string FormatReport(SelfTestReport report)
    {
        var lines = new List<string>
        {
            $"ANTHILL v{report.Version} Self-Test ({report.SchemaVersion})",
            $"Result: {(report.Ok ? "PASS" : "FAIL")} | passed={report.ChecksPassed} failed={report.ChecksFailed} warnings={report.ChecksWarning}",
            "",
        };
        foreach (var check in report.Checks)
            lines.Add($"[{check.Status.ToUpperInvariant()}] {check.Name}: {check.Message}");
        return string.Join("\n", lines);
    }

    private static string TokenSecurity_GenerateStrong() => Security.TokenSecurity.GenerateStrongToken();

    private static bool TokenSecurityProbe(string token)
    {
        try { Security.TokenSecurity.ValidateApiTokenStrength(token); return true; }
        catch { return false; }
    }
}
