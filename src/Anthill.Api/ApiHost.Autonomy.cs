using System.Reflection;
using Anthill.Core.Agents;
using Anthill.Core.Shadow;
using Anthill.Core.Autonomy;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Conversations;   // v3.7.0: conversations, escalation policy and run state
using Anthill.Core.Diagnostics;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Models;
using Anthill.Core.Orchestration;
using Anthill.Core.Planning;
using Anthill.Core.Readiness;
using Anthill.Core.Sandbox;   // LoopBudget — the agent loop's bounds
using Anthill.Core.Security;
using Anthill.Core.Tools;      // ToolInventory, ToolAuthorization — the /tools report
// `Task` here is Anthill.Core.Domain.Task (the mission task). The threading one must be named.
using ThreadingTask = System.Threading.Tasks.Task;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;


namespace Anthill.Api;

/// <summary>
/// The Colony Director, objectives and autonomy control.
///
/// v3.8.17 — split out of ApiHost.cs, which was 3,294 lines and 102 endpoints. Same class,
/// same behaviour: ApiHost has been `public static partial` with eight files since the homelab
/// moved, so this is where the file was always going to divide.
/// </summary>
public static partial class ApiHost
{
    // ---- Autonomy (Phase 1): objective backlog + Director control plane ----
    private static void MapAutonomyEndpoints(WebApplication app)
    {
        // Director control
        app.MapGet("/autonomy/status", (HttpContext ctx) =>
            RequireAuth(ctx, "read_autonomy") ?? ApiJson.Ok(Director.StatusSnapshot()));

        app.MapPost("/autonomy/start", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "autonomy_control"); if (auth is not null) return auth;
            if (!AnthillRuntime.EnableAutonomy)
                return ApiJson.Error("Autonomy is disabled in config (autonomy_enabled=false).", "autonomy_disabled");
            // v2.26.0: THIS is the explicit operator resume — the one path that clears a durable
            // STOP, audited as such. Starting the Director process no longer does it.
            var wasStopped = AutonomyControl.IsStopped;
            AutonomyControl.Resume();
            Director.Start();
            if (wasStopped)
                Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "autonomy_resumed",
                    $"Operator '{CurrentUsername(ctx) ?? "operator"}' explicitly cleared the STOP sentinel and resumed autonomy.",
                    antName: "operator");
            return ApiJson.Ok(Director.StatusSnapshot(), "Colony Director started.");
        });

        app.MapPost("/autonomy/stop", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "autonomy_control"); if (auth is not null) return auth;
            Director.Stop("api stop");
            return ApiJson.Ok(Director.StatusSnapshot(), "Colony Director stopped; kill switch engaged.");
        });

        app.MapGet("/autonomy/runs", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_autonomy"); if (auth is not null) return auth;
            var objectiveId = ctx.Request.Query["objective_id"].FirstOrDefault();
            var runs = Queen.Memory.ListAutonomyRuns(string.IsNullOrEmpty(objectiveId) ? null : objectiveId);
            // v1.8.16: attach a patch rollup per run so the Autonomy page can show "Patches: 2 applied, 1 pending".
            foreach (var run in runs)
            {
                var mid = run.GetValueOrDefault("mission_id")?.ToString();
                run["patch_counts"] = string.IsNullOrEmpty(mid)
                    ? Queen.Memory.PatchCountsForMission("")   // yields an all-zero rollup
                    : Queen.Memory.PatchCountsForMission(mid!);
            }
            return ApiJson.Ok(runs);
        });

        // Objective backlog CRUD
        app.MapGet("/objectives", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_objectives"); if (auth is not null) return auth;
            ObjectiveStatus? filter = null;
            var statusQ = ctx.Request.Query["status"].FirstOrDefault();
            if (!string.IsNullOrEmpty(statusQ)) filter = EnumExtensions.ParseObjectiveStatus(statusQ);
            return ApiJson.Ok(Queen.Memory.ListObjectives(filter).Select(ObjectiveDict).ToList());
        });

        app.MapGet("/objectives/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_objectives"); if (auth is not null) return auth;
            var o = Queen.Memory.GetObjective(id);
            return o is null ? ApiJson.Error($"No objective found with id: {id}", "not_found") : ApiJson.Ok(ObjectiveDict(o));
        });

        app.MapPost("/objectives", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_objectives"); if (auth is not null) return auth;
            ObjectiveRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ObjectiveRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var title = (body?.Title ?? "").Trim();
            var charter = (body?.Charter ?? "").Trim();
            if (title.Length == 0 || charter.Length == 0)
                return ApiJson.Error("Both 'title' and 'charter' are required.", "bad_request");
            // v0.3.8.48: an objective may declare its project at creation. A named project must
            // exist; absence is recorded as unassigned, never guessed.
            var projectId = (body?.ProjectId ?? "").Trim();
            if (projectId.Length > 0 && Queen.Memory.LoadProject(projectId) is null)
                return ApiJson.Error($"No project '{projectId}'.", "not_found");
            var o = new Objective
            {
                Title = title, Charter = charter,
                Priority = body!.Priority ?? 0, MaxRuns = Math.Max(0, body.MaxRuns ?? 0),
                ProjectId = projectId.Length > 0 ? projectId : null,
            };
            Queen.Memory.SaveObjective(o);
            return ApiJson.Ok(ObjectiveDict(o), "Objective added to the backlog.");
        });

        // v2.26.0 pre-V3 hardening: promote a model-SUGGESTED objective into the executable
        // backlog. This is the only path from `suggested` to executable — suggestions may not
        // promote themselves, and the promotion is an audited operator act.
        app.MapPost("/objectives/{id}/approve", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_objectives"); if (auth is not null) return auth;
            var objective = Queen.Memory.GetObjective(id);
            if (objective is null) return ApiJson.Error("Unknown objective.", "not_found");
            if (objective.Status != ObjectiveStatus.Suggested)
                return ApiJson.Error($"Only 'suggested' objectives can be approved — this one is '{objective.Status.Value()}'.", "bad_request");
            objective.Status = ObjectiveStatus.Pending;
            objective.Metadata["approved_by"] = CurrentUsername(ctx) ?? "operator";
            objective.Metadata["approved_at"] = AnthillTime.NowUtc().ToIso();
            Queen.Memory.SaveObjective(objective);
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "objective_suggestion_approved",
                $"Operator approved suggested objective '{objective.Title}' into the executable backlog.",
                antName: "operator", metadata: new() { ["objective_id"] = objective.Id });
            return ApiJson.Ok(ObjectiveDict(objective), "Suggestion approved into the backlog.");
        });

        // v2.26.0: deterministic self-introspection — the colony answers what it IS from live
        // registries and gates, never from memory search or model opinion.
        app.MapGet("/colony/introspection", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var findings = RuntimeConfigValidator.Validate();
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["version"] = AnthillRuntime.Version,
                ["executable_roles"] = AntRegistry.ExecutableRoleIds.OrderBy(x => x).ToList(),
                ["activation_tier"] = AnthillRuntime.ActivationTier.ToString().ToLowerInvariant(),
                ["specialists"] = new Dictionary<string, object?>
                {
                    ["tester"] = AnthillRuntime.EnableTesterAnt, ["soldier"] = AnthillRuntime.EnableSoldierAnt,
                    ["medic"] = AnthillRuntime.EnableMedicAnt, ["archivist"] = AnthillRuntime.EnableArchivistAnt,
                },
                ["autonomy_enabled"] = AnthillRuntime.EnableAutonomy,
                ["stop_engaged"] = AutonomyControl.IsStopped,
                ["homelab_stop_engaged"] = Anthill.Modules.Homelab.Actions.HomelabActionControl.IsStopped,
                ["director_running"] = Director.IsRunning,
                ["can_write_files"] = AnthillRuntime.EnableFileWriting,
                ["can_apply_patches"] = AnthillRuntime.EnablePatchApplication,
                ["auto_apply_enabled"] = AnthillRuntime.AutonomyAutoApplyEnabled,
                ["break_glass_keep_without_verify"] = AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify,
                ["adaptive_operational"] = AnthillRuntime.EnableHandoffIngestion || AnthillRuntime.EnableObjectiveVerification,
                ["objective_verification_enabled"] = AnthillRuntime.EnableObjectiveVerification,
                ["handoff_ingestion_enabled"] = AnthillRuntime.EnableHandoffIngestion,
                ["running_jobs"] = Jobs.ListJobs(100).Count(j => (j.GetValueOrDefault("status")?.ToString() ?? "") == "running"),
                ["config_health"] = findings.Select(f => new Dictionary<string, object?>
                {
                    ["severity"] = f.Severity, ["combination"] = f.Combination, ["detail"] = f.Detail,
                }).ToList(),
                ["config_healthy"] = findings.Count == 0,
                ["v3_qualified"] = EvaluateReadiness().Ready,
            });
        });

        // v2.26.0: configuration health — incompatible feature combinations, degraded loudly.
        app.MapGet("/config/health", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var findings = RuntimeConfigValidator.Validate();
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["healthy"] = findings.Count == 0,
                // v0.3.8.91: the migration, which this endpoint's own doc comment on
                // `LastConfigMigration` has claimed to surface since it was written and did not.
                // "Why did six roles switch on when I upgraded" is an operator question, and the
                // answer was reaching one line of stderr they had already scrolled past.
                ["config_migration"] = AnthillRuntime.LastConfigMigration is { } migration
                    ? new Dictionary<string, object?>
                    {
                        ["from_version"] = migration.FromVersion,
                        ["to_version"] = migration.ToVersion,
                        ["roster_profile"] = migration.RosterProfile,
                        ["changed_the_roster"] = migration.ChangedTheRoster,
                    }
                    : null,
                // Empty is healthy. Non-empty means a config file exists and could not be read —
                // and this process is running on defaults nobody chose.
                ["config_load_error"] = AnthillRuntime.ConfigLoadError,
                ["findings"] = findings.Select(f => new Dictionary<string, object?>
                {
                    ["severity"] = f.Severity, ["combination"] = f.Combination, ["detail"] = f.Detail,
                }).ToList(),
            });
        });

        // v3.0.0 baseline lock: the generated runtime inventory + call-site audit. What the
        // runtime declares, and who consumes it. The same data CI gates on.
        app.MapGet("/runtime/inventory", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var inventory = Anthill.Core.Diagnostics.RuntimeInventory.Build(AnthillRuntime.PathFromScript("."));
            var audit = Anthill.Core.Diagnostics.CallSiteAudit.Run(inventory);
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["generated_at"] = inventory.GeneratedAt,
                ["declarations"] = inventory.Entries.Count,
                ["by_kind"] = inventory.ByKind.ToDictionary(g => g.Key, g => g.Count()),
                ["audit_clean"] = audit.Clean,
                ["audit"] = audit.Explain(),
                ["entries"] = inventory.Entries.Select(e => new Dictionary<string, object?>
                {
                    ["kind"] = e.Kind, ["name"] = e.Name, ["detail"] = e.Detail,
                    ["consumers"] = e.CallSites.Count, ["orphaned"] = e.Orphaned,
                }).ToList(),
            });
        });

        // v2.26.0: the machine-generated qualification report — measured results only.
        app.MapPost("/readiness/qualification-report", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var (jsonPath, mdPath) = QualificationReportWriter.Write(EvaluateReadiness(), RuntimeConfigValidator.Validate());
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["json_path"] = jsonPath, ["markdown_path"] = mdPath,
            }, "Qualification report written from measured results.");
        });

        app.MapPatch("/objectives/{id}", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_objectives"); if (auth is not null) return auth;
            if (Queen.Memory.GetObjective(id) is null) return ApiJson.Error($"No objective found with id: {id}", "not_found");
            ObjectivePatch? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ObjectivePatch>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (body?.Priority is int p) Queen.Memory.SetObjectivePriority(id, p);
            if (!string.IsNullOrEmpty(body?.Status))
            {
                var newStatus = EnumExtensions.ParseObjectiveStatus(body.Status);
                Queen.Memory.UpdateObjectiveStatus(id, newStatus);
                // v1.8.16: record operator-driven terminal transitions. A move to Done is a manual
                // "stop" that belongs in Completed Objectives. A plain pause stays a resumable
                // backlog item (no end marker). Resuming clears any prior end markers.
                if (newStatus is ObjectiveStatus.Done && Queen.Memory.GetObjective(id) is { } stopped)
                {
                    stopped.Metadata["end_reason"] = ObjectiveEndReason.ManuallyStopped;
                    stopped.Metadata["end_detail"] = "Stopped by operator from the console.";
                    stopped.Metadata["ended_at"] = AnthillTime.NowUtc().ToIso();
                    Queen.Memory.SaveObjective(stopped);
                }
                else if (newStatus is ObjectiveStatus.Active or ObjectiveStatus.Pending &&
                    Queen.Memory.GetObjective(id) is { } resumed &&
                    (resumed.Metadata.ContainsKey("end_reason") || resumed.Metadata.ContainsKey("retired_code")))
                {
                    // Resuming clears the ended/retired markers so it returns to the active backlog.
                    resumed.Metadata.Remove("end_reason"); resumed.Metadata.Remove("end_detail");
                    resumed.Metadata.Remove("ended_at"); resumed.Metadata.Remove("retired_code");
                    resumed.Metadata.Remove("retired_reason"); resumed.Metadata.Remove("retired_at");
                    Queen.Memory.SaveObjective(resumed);
                }
            }
            return ApiJson.Ok(ObjectiveDict(Queen.Memory.GetObjective(id)!), "Objective updated.");
        });

        app.MapDelete("/objectives/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_objectives"); if (auth is not null) return auth;
            if (Queen.Memory.GetObjective(id) is null) return ApiJson.Error($"No objective found with id: {id}", "not_found");
            try { Queen.Memory.DeleteObjective(id); }
            catch (Exception ex) { return ApiJson.Error($"Could not delete objective: {ex.Message}", "delete_failed"); }
            return ApiJson.Ok(new Dictionary<string, object?> { ["id"] = id }, "Objective removed.");
        });
    }

    // ---- header status ------------------------------------------------------
}
