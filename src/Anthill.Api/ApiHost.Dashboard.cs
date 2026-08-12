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
/// Dashboard and console read surfaces.
///
/// v3.8.17 — split out of ApiHost.cs, which was 3,294 lines and 102 endpoints. Same class,
/// same behaviour: ApiHost has been `public static partial` with eight files since the homelab
/// moved, so this is where the file was always going to divide.
/// </summary>
public static partial class ApiHost
{
    // ---- Live dashboard: settings, ant profiles, filtered events, pheromone memory ----
    private static void MapDashboardEndpoints(WebApplication app)
    {
        // Effective settings (secret-free) for the settings panel to render.
        app.MapGet("/settings", (HttpContext ctx) =>
            RequireAuth(ctx, "read_config") ?? ApiJson.Ok(AnthillRuntime.SettingsSnapshot()));

        // Apply a partial settings update (Ollama host/model/routes, feature knobs). Whitelisted
        // keys only; persisted to config.json and re-projected into the live runtime.
        app.MapPost("/settings", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_settings"); if (auth is not null) return auth;
            Dictionary<string, System.Text.Json.JsonElement>? body;
            try { body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (body is null || body.Count == 0) return ApiJson.Error("No settings provided.", "bad_request");
            var applied = AnthillRuntime.ApplySettingsUpdate(body);
            if (applied.Count == 0) return ApiJson.Error("No editable settings in request.", "bad_request");
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["applied"] = applied, ["settings"] = AnthillRuntime.SettingsSnapshot(),
            }, $"Updated {applied.Count} setting(s).");
        });

        // ---- Maintenance / data hygiene (admin-only, audited) ----
        app.MapGet("/maintenance/stats", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var (bkCount, bkBytes) = FileSecurity.BackupStats(AnthillRuntime.BackupDir, AnthillRuntime.PathFromScript);
            long diskFree = 0, diskTotal = 0;
            try { var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(AnthillRuntime.PathFromScript(AnthillRuntime.DbPath)))!); diskFree = d.AvailableFreeSpace; diskTotal = d.TotalSize; }
            catch { /* best effort */ }
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["db_bytes"] = Queen.Memory.DatabaseFileBytes(),
                ["backup_count"] = bkCount, ["backup_bytes"] = bkBytes,
                ["max_db_backups"] = AnthillRuntime.MaxDbBackups, ["event_retention_days"] = AnthillRuntime.EventRetentionDays,
                ["disk_free_bytes"] = diskFree, ["disk_total_bytes"] = diskTotal,
                ["table_counts"] = Queen.Memory.TableCounts(),
            });
        });

        app.MapPost("/maintenance/flush", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_settings"); if (auth is not null) return auth;
            var (deletedBackups, backupFreed) = FileSecurity.PruneBackups(AnthillRuntime.BackupDir, AnthillRuntime.MaxDbBackups, AnthillRuntime.PathFromScript);
            var (dbBefore, dbAfter, eventsDeleted) = Queen.Memory.FlushCache(AnthillRuntime.EventRetentionDays);
            var totalFreed = backupFreed + Math.Max(0, dbBefore - dbAfter);
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "maintenance_flush",
                $"Flush cache: freed {totalFreed} bytes ({deletedBackups} backups, {eventsDeleted} old events).", antName: "operator",
                metadata: new() { ["backups_deleted"] = deletedBackups, ["backup_bytes_freed"] = backupFreed, ["db_reclaimed"] = Math.Max(0, dbBefore - dbAfter), ["events_deleted"] = eventsDeleted });
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["bytes_freed"] = totalFreed, ["backups_deleted"] = deletedBackups, ["backup_bytes_freed"] = backupFreed,
                ["db_reclaimed_bytes"] = Math.Max(0, dbBefore - dbAfter), ["events_deleted"] = eventsDeleted,
            }, $"Freed {HumanBytes(totalFreed)}.");
        });

        app.MapPost("/maintenance/clear-missions", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_settings"); if (auth is not null) return auth;

            // v0.3.8.38 — REFUSE while work is in flight.
            //
            // ClearMissionHistory drops tasks, events, patches and approvals with foreign_keys OFF.
            // Run mid-mission it deletes rows a worker is still writing, and the audit trail that
            // would explain what the mission did goes with them. The console disabled its button;
            // the endpoint accepted the call from anywhere, and a disabled button is not a gate.
            var active = Jobs.ActiveJobIds();
            if (active.Count > 0)
                return ApiJson.Error(
                    $"{active.Count} mission(s) are still queued or running. Cancel or wait for them "
                    + "before clearing history — deleting now would destroy the record of work that "
                    + "is still happening.", "conflict");

            var (freed, missions) = Queen.Memory.ClearMissionHistory();
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "maintenance_clear_missions",
                $"Cleared mission history: {missions} mission(s), freed {freed} bytes.", antName: "operator");
            return ApiJson.Ok(new Dictionary<string, object?> { ["missions_deleted"] = missions, ["bytes_freed"] = freed },
                $"Cleared {missions} mission(s); freed {HumanBytes(freed)}.");
        });

        app.MapPost("/maintenance/reset-config", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_settings"); if (auth is not null) return auth;
            var preserved = AnthillRuntime.ResetConfig();
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "maintenance_reset_config",
                "Config reset to safe defaults (connection settings preserved).", antName: "operator");
            return ApiJson.Ok(new Dictionary<string, object?> { ["preserved"] = preserved, ["settings"] = AnthillRuntime.SettingsSnapshot() },
                "Config reset to defaults. Connection settings preserved.");
        });

        // Completed Objectives: the Director's loop-retired objectives (collapsed rows) — shown
        // in Configuration → Autonomy instead of the active/paused backlog.
        app.MapGet("/objectives/completed", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_objectives"); if (auth is not null) return auth;
            // v1.8.16: all ended objectives (completed cleanly, stopped no-followup, retired looping,
            // failed, or manually paused/stopped) — not just the loop-retired ones.
            var rows = Queen.Memory.ListEndedObjectives().Select(o =>
            {
                var endReason = o.Metadata.GetValueOrDefault("end_reason")?.ToString()
                    ?? (o.Metadata.GetValueOrDefault("retired_code") is not null ? ObjectiveEndReason.RetiredLooping : null);
                return new Dictionary<string, object?>
                {
                    ["id"] = o.Id, ["title"] = o.Title,
                    ["end_reason"] = endReason,
                    ["end_reason_label"] = ObjectiveEndReason.Label(endReason),
                    ["end_detail"] = o.Metadata.GetValueOrDefault("end_detail") ?? o.Metadata.GetValueOrDefault("retired_reason"),
                    ["ended_at"] = o.Metadata.GetValueOrDefault("ended_at") ?? o.Metadata.GetValueOrDefault("retired_at"),
                    // Legacy fields kept so older UI keeps working.
                    ["retired_code"] = o.Metadata.GetValueOrDefault("retired_code"),
                    ["retired_reason"] = o.Metadata.GetValueOrDefault("retired_reason"),
                    ["retired_at"] = o.Metadata.GetValueOrDefault("retired_at"),
                    ["status"] = o.Status.Value(),
                    ["run_count"] = o.RunCount,
                    ["patch_counts"] = Queen.Memory.PatchCountsForObjective(o.Id),
                };
            }).ToList();
            return ApiJson.Ok(rows);
        });
        // Expanded detail for one completed objective: compiled runs, missions, and tasks.
        app.MapGet("/objectives/{id}/detail", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_objectives"); if (auth is not null) return auth;
            var o = Queen.Memory.GetObjective(id);
            return o is null ? ApiJson.Error($"No objective found with id: {id}", "not_found") : ApiJson.Ok(CompletedObjectiveDetail(o));
        });

        // Dump directives: clear the whole objective backlog + its run history.
        app.MapPost("/objectives/clear", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_objectives"); if (auth is not null) return auth;
            var (freed, deleted) = Queen.Memory.ClearObjectives();
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "objectives_cleared",
                $"Dumped {deleted} objective(s) from the backlog.", antName: "operator");
            return ApiJson.Ok(new Dictionary<string, object?> { ["objectives_deleted"] = deleted, ["bytes_freed"] = freed },
                $"Dumped {deleted} objective(s).");
        });

        // Console display state: custom ant names, accent colours, node positions, layout prefs.
        //
        // v2.14.14: both directions now run the workspace layout through DashboardWorkspaceState.
        // Before this, SanitizeInto was called only from unit tests, so validation, clamping,
        // off-screen recovery, and profile isolation were all dead code in the running system.
        // Ant names, colours, positions, and map preferences pass through untouched either way —
        // a corrupt panel layout must never cost the operator their colony.
        static (int W, int H) ViewportOf(HttpContext ctx)
        {
            var q = ctx.Request.Query;
            return (int.TryParse(q["vw"], out var w) && w > 0 ? w : DashboardWorkspaceState.DefaultViewportWidth,
                    int.TryParse(q["vh"], out var h) && h > 0 ? h : DashboardWorkspaceState.DefaultViewportHeight);
        }

        app.MapGet("/ui/state", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_ui_state"); if (auth is not null) return auth;
            var (vw, vh) = ViewportOf(ctx);
            return ApiJson.Ok(UiStateStore.WithSanitizedWorkspace(
                UiStateStore.Load(),
                DashboardWorkspaceState.KnownPanelIds,
                DashboardWorkspaceState.KnownOverlayIds, vw, vh));
        });

        app.MapPut("/ui/state", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_ui_state"); if (auth is not null) return auth;
            System.Text.Json.JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var saved = UiStateStore.Save(body);
            var (vw, vh) = ViewportOf(ctx);
            // Sanitize what we hand back so the client immediately reflects any repair, rather
            // than believing an off-screen panel is where it asked for.
            return ApiJson.Ok(UiStateStore.WithSanitizedWorkspace(
                saved,
                DashboardWorkspaceState.KnownPanelIds,
                DashboardWorkspaceState.KnownOverlayIds, vw, vh), "Console layout saved.");
        });

        // ---- Operator shell console (Configuration → Shell) — admin only ----
        app.MapGet("/shell/info", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "operator_shell"); if (auth is not null) return auth;
            // v0.3.8.49 (§14): the platform is discovered on the host and the quick actions come WITH
            // the info, so the console renders only what this environment can actually run — Windows
            // gets Windows service commands, Linux gets systemd, and an unknown host gets none rather
            // than Linux buttons that would fail.
            var platform = ShellPlatform.Detect();
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["enabled"] = AnthillRuntime.EnableOperatorShell,
                ["default_dir"] = OperatorShell.DefaultWorkingDir(),
                ["timeout_seconds"] = OperatorShell.TimeoutSeconds,
                ["host"] = Environment.MachineName,
                ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ["platform"] = platform,
                ["quick_actions"] = ShellPlatform.ActionsPayload(platform),
            });
        });

        app.MapPost("/shell/exec", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "operator_shell"); if (auth is not null) return auth;
            if (!AnthillRuntime.EnableOperatorShell)
                return ApiJson.Error("The operator shell is disabled. Enable it in Configuration → Security.", "shell_disabled");

            System.Text.Json.JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var command = body.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
            var dir = body.TryGetProperty("dir", out var d) ? d.GetString() : null;
            command = command.Trim();
            if (command.Length == 0) return ApiJson.Error("Missing required field: command.", "bad_request");

            var who = ResolveIdentity(ctx)?.Username ?? "admin";
            // Audit BEFORE running, so the record survives even if the command wedges the host.
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "operator_shell_command",
                $"Operator {who} ran a shell command.", antName: "operator",
                metadata: new() { ["operator"] = who, ["command"] = command, ["dir"] = dir ?? OperatorShell.DefaultWorkingDir() });

            OperatorShell.ShellResult result;
            try { result = OperatorShell.Execute(command, dir); }
            catch (Exception ex)
            {
                Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "operator_shell_error",
                    $"Operator shell command failed to start: {ex.Message}", antName: "operator",
                    metadata: new() { ["operator"] = who, ["command"] = command, ["error"] = ex.Message });
                return ApiJson.Error($"Failed to run command: {ex.Message}", "shell_error");
            }

            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "operator_shell_result",
                $"Operator {who} shell command exited {result.ExitCode}{(result.TimedOut ? " (timed out)" : "")}.", antName: "operator",
                metadata: new() { ["operator"] = who, ["exit_code"] = result.ExitCode, ["timed_out"] = result.TimedOut, ["elapsed_seconds"] = result.ElapsedSeconds });

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["exit_code"] = result.ExitCode, ["stdout"] = result.Stdout, ["stderr"] = result.Stderr,
                ["timed_out"] = result.TimedOut, ["dir"] = result.WorkingDir, ["elapsed_seconds"] = result.ElapsedSeconds,
            });
        });

        // Filterable event feed (ant / type / level / since / mission).
        app.MapGet("/events/json", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_events"); if (auth is not null) return auth;
            var q = ctx.Request.Query;
            int.TryParse(q["limit"].FirstOrDefault(), out var limit);
            var rows = Queen.Memory.QueryEventsRich(
                ant: q["ant"].FirstOrDefault(),
                typeContains: q["type"].FirstOrDefault(),
                sinceIso: q["since"].FirstOrDefault(),
                level: q["level"].FirstOrDefault(),
                missionId: q["mission_id"].FirstOrDefault(),
                limit: Math.Clamp(limit <= 0 ? 200 : limit, 1, 1000)); // cap so a huge ?limit can't sweep the whole log
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["events"] = rows,
                ["ants"] = Queen.Memory.DistinctEventAnts(),
                ["types"] = Queen.Memory.DistinctEventTypes(),
            });
        });

        // Pheromone memory: list (with net scores) and prune the unusable/errored trails.
        // v2.24.0 Phase E: the Shadow Operations line's first production surface. Two releases
        // built a recommendation engine and a fault-simulation harness that nothing could reach —
        // no endpoint, no storage, no call site. Qualification now reads REAL recorded pairs.
        app.MapGet("/shadow/json", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_graph"); if (auth is not null) return auth;

            var pairs = Queen.Memory.LoadScoreablePairs(500);
            var timing = ShadowTimingMetrics.From(Queen.Memory.ShadowResolutionSeconds(500));
            var pending = Queen.Memory.CountUnresolvedShadowRecommendations();

            // The scoreboard is computed from REHYDRATED stored pairs, not from anything held in
            // memory during this process. Before v2.24.0 `QualificationScoreboard.Compute` had no
            // production caller at all — it could only be handed pairs built by the simulator or by
            // its own tests, so the qualification evidence V3 requires did not exist.
            var metrics = QualificationScoreboard.Compute(Queen.Memory.LoadScoreableRecommendations(500));

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["pairs"] = pairs,
                // Projected explicitly rather than serialising the records directly: no naming
                // policy is configured, so a record would go out PascalCase while the joined rows
                // above are snake_case. Renaming a metric property must not silently rename a
                // field the dashboard reads.
                ["timing"] = new Dictionary<string, object?>
                {
                    ["sample"] = timing.Sample,
                    ["median_resolution_seconds"] = timing.MedianResolutionSeconds,
                    ["p90_resolution_seconds"] = timing.P90ResolutionSeconds,
                },
                ["metrics"] = new Dictionary<string, object?>
                {
                    ["sample"] = metrics.Sample,
                    ["diagnosis_precision"] = metrics.DiagnosisPrecision,
                    ["diagnosis_recall"] = metrics.DiagnosisRecall,
                    ["action_selection_accuracy"] = metrics.ActionSelectionAccuracy,
                    ["unnecessary_action_rate"] = metrics.UnnecessaryActionRate,
                    ["predicted_success_accuracy"] = metrics.PredictedSuccessAccuracy,
                    ["policy_violations"] = metrics.PolicyViolations,
                    ["unverified_success_claims"] = metrics.UnverifiedSuccessClaims,
                },
                // Structural invariants. Both must be zero; a non-zero value is a regression in the
                // recommender, not a score to be interpreted.
                ["invariants_hold"] = metrics.PolicyViolations == 0 && metrics.UnverifiedSuccessClaims == 0,
                // Stated explicitly: an empty scoreboard means "not yet qualified", never "passing".
                // A qualification gate that reads as satisfied because nothing was measured is the
                // most dangerous possible failure for this subsystem.
                ["qualified_sample"] = pairs.Count,
                ["awaiting_operator_judgment"] = pending,
                // v0.3.8.46: the backlog itself. The judge endpoint spent a release with a COUNT
                // as its only surface — a queue the operator was asked to clear but could not see.
                ["pending"] = Queen.Memory.LoadUnresolvedShadowRecommendations(50),
                ["status"] = pairs.Count == 0
                    ? "no scored incidents yet — shadow mode has not qualified anything"
                    : $"{pairs.Count} scored incident(s); {pending} awaiting operator judgment",
            });
        });

        // v2.25.0: the operator's half of the shadow loop. Without this endpoint,
        // RecordOperatorJudgment had no production caller — the v2.24.0 storage could fill with
        // recommendations that could never become scoreable. (The seventh instance of the
        // "tested code with no call site" defect, caught one release later.)
        app.MapPost("/shadow/judge", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "approve"); if (auth is not null) return auth;
            ShadowJudgeBody? body = null;
            try { body = await ctx.Request.ReadFromJsonAsync<ShadowJudgeBody>(); }
            catch { /* fall through to validation */ }
            if (body is null || string.IsNullOrWhiteSpace(body.IncidentId))
                return ApiJson.Error("incident_id is required.", "bad_request");

            LiveIncidentObserver.RecordOperatorJudgment(Queen.Memory, body.IncidentId,
                body.DiagnosisCorrect, body.ActionWasNeeded, body.ActionMatched, body.WouldHaveSucceeded,
                body.Note ?? "");
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["incident_id"] = body.IncidentId,
                ["scoreable_pairs"] = Queen.Memory.LoadScoreablePairs(500).Count,
                ["awaiting_operator_judgment"] = Queen.Memory.CountUnresolvedShadowRecommendations(),
            }, "Judgment recorded — the pair is now scoreable.");
        });

        // v2.25.0 Phase F: the V3.0 readiness gate. Not a feature — an evaluation. The one rule:
        // unmeasured reads as NOT ready; unattested reads as NOT ready. Nothing here can be
        // satisfied by silence.
        app.MapGet("/readiness/json", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            return ApiJson.Ok(ReadinessSnapshot());
        });

        app.MapPost("/readiness/attest", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "approve"); if (auth is not null) return auth;
            AttestBody? body = null;
            try { body = await ctx.Request.ReadFromJsonAsync<AttestBody>(); }
            catch { /* fall through to validation */ }
            if (body is null || string.IsNullOrWhiteSpace(body.ThresholdId))
                return ApiJson.Error("threshold_id is required.", "bad_request");

            var by = CurrentUsername(ctx) ?? "operator";
            if (!Queen.Memory.SaveReadinessAttestation(body.ThresholdId, body.Satisfied, body.Note ?? "", by))
                return ApiJson.Error(
                    $"Unknown or non-attestable threshold '{body.ThresholdId}'. Attestable: "
                    + string.Join(", ", V3Readiness.AttestableIds.OrderBy(x => x)), "bad_request");
            return ApiJson.Ok(ReadinessSnapshot(), "Attestation recorded.");
        });

        // The certification report — plain text, suitable for filing with the release. It is
        // ALWAYS truthful about its own status: an unready system gets a report that says so,
        // never a certificate.
        app.MapGet("/readiness/certification", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var report = EvaluateReadiness();
            var lines = new List<string>
            {
                "OPERATION ANTHILL — V3.0 READINESS CERTIFICATION",
                $"Generated {AnthillTime.NowUtc().ToIso()} by ANTHILL v{AnthillRuntime.Version}",
                new string('=', 72),
                report.Statement,
                "",
            };
            foreach (var c in report.Checks)
            {
                lines.Add($"[{(c.Satisfied ? "PASS" : "FAIL")}] {c.Title}");
                lines.Add($"       id: {c.Id}  kind: {c.Kind}");
                lines.Add($"       {c.Detail}");
                lines.Add("");
            }
            lines.Add(new string('=', 72));
            lines.Add(report.Ready
                ? "Every threshold holds. This document certifies V3.0 readiness."
                : "This document does NOT certify readiness. It records the current state honestly.");
            return Results.Text(string.Join("\n", lines), "text/plain");
        });

        app.MapGet("/pheromones/json", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_pheromones"); if (auth is not null) return auth;
            int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var limit);
            var trails = Queen.Memory.ListPheromoneTrails(Math.Clamp(limit <= 0 ? 300 : limit, 1, 2000));

            // v2.24.0: the colony dashboard reads THIS endpoint, and v2.20.0 surfaced the learning
            // reset everywhere except here. After the reset every pre-boundary trail sits at the
            // neutral 0.5 with its success count restarted, so the HUD renders a wall of identical
            // 50% bars and the field looks dead. That is the reset working, not the pheromone
            // system failing — but with nothing saying so it is indistinguishable from a break.
            //
            // The client reads `data` as the trail array, so that shape is preserved exactly and
            // the reset travels beside it.
            var legacyCount = trails.Count(t => Convert.ToInt64(t.GetValueOrDefault("legacy") ?? 0L) != 0);
            return ApiJson.Ok(trails, new Dictionary<string, object?>
            {
                ["learning_reset"] = Queen.Memory.LearningResetDate(),
                ["legacy_trails"] = legacyCount,
                ["total_trails"] = trails.Count,
                ["note"] = legacyCount > 0
                    ? "Trails marked legacy were reset to neutral strength at the learning boundary; "
                      + "they re-differentiate as missions reach completed_verified."
                    : null,
            });
        });

        // v1.8.23 Phase 9: one composed read model for the Memory + Pheromone Explorer.
        app.MapGet("/memory/explorer", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_memory"); if (auth is not null) return auth;
            var query = (ctx.Request.Query["q"].FirstOrDefault() ?? "").Trim();
            var needle = query.ToLowerInvariant();
            int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var rawLimit);
            var limit = Math.Clamp(rawLimit <= 0 ? 80 : rawLimit, 10, 300);

            static string S(Dictionary<string, object?> row, params string[] keys) =>
                string.Join(" ", keys.Select(k => row.GetValueOrDefault(k)?.ToString() ?? ""));
            bool Matches(Dictionary<string, object?> row, params string[] keys) =>
                needle.Length == 0 || S(row, keys).ToLowerInvariant().Contains(needle);

            var missions = Queen.Memory.GetRecentMissions(limit)
                .Where(m => m.GetValueOrDefault("id")?.ToString() != AnthillRuntime.SystemApiMissionId)
                .Where(m => Matches(m, "id", "goal", "status", "user_result", "debug_result", "final_result"))
                .ToList();
            var missionIds = missions.Select(m => m.GetValueOrDefault("id")?.ToString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToHashSet(StringComparer.Ordinal);

            var tasks = missions.SelectMany(m => Queen.Memory.GetTasksForMission(m.GetValueOrDefault("id")?.ToString() ?? "", 120))
                .Where(t => Matches(t, "id", "mission_id", "title", "description", "assigned_ant", "assigned_worker", "task_type", "status", "result_summary", "failure_reason"))
                .Take(limit * 8)
                .ToList();
            var trails = Queen.Memory.ListPheromoneTrails(Math.Min(2000, limit * 12))
                .Where(t => Matches(t, "trail_key", "trail_type"))
                .ToList();
            var sources = CallerHas(ctx, "read_sources")
                ? Queen.Memory.GetRecentSources(limit * 3)
                    .Where(s => missionIds.Count == 0 || missionIds.Contains(s.GetValueOrDefault("mission_id")?.ToString() ?? ""))
                    .Where(s => Matches(s, "id", "mission_id", "title", "url", "domain", "summary", "notes"))
                    .Take(limit * 3)
                    .ToList()
                : new List<Dictionary<string, object?>>();
            var patches = CallerHas(ctx, "read_patches")
                ? Queen.Memory.ListPatchProposals(limit: limit * 4)
                    .Where(p => missionIds.Count == 0 || missionIds.Contains(p.GetValueOrDefault("mission_id")?.ToString() ?? ""))
                    .Where(p => Matches(p, "id", "mission_id", "task_id", "file_path", "change_type", "reason", "risk", "status", "patch_set_summary", "last_error"))
                    .Take(limit * 4)
                    .ToList()
                : new List<Dictionary<string, object?>>();
            var events = CallerHas(ctx, "read_events")
                ? Queen.Memory.GetRecentEvents(limit * 8)
                    .Where(e => missionIds.Count == 0 || missionIds.Contains(e.GetValueOrDefault("mission_id")?.ToString() ?? ""))
                    .Where(e => Matches(e, "id", "mission_id", "task_id", "ant_name", "event_type", "message", "level"))
                    .Take(limit * 8)
                    .ToList()
                : new List<Dictionary<string, object?>>();

            static long L(Dictionary<string, object?> row, string key) =>
                row.GetValueOrDefault(key) switch
                {
                    long v => v,
                    int v => v,
                    double v => (long)v,
                    decimal v => (long)v,
                    string s when long.TryParse(s, out var v) => v,
                    _ => 0,
                };
            static double D(Dictionary<string, object?> row, string key) =>
                row.GetValueOrDefault(key) switch
                {
                    double v => v,
                    float v => v,
                    decimal v => (double)v,
                    long v => v,
                    int v => v,
                    string s when double.TryParse(s, out var v) => v,
                    _ => 0,
                };
            static bool Loopish(Dictionary<string, object?> t)
            {
                var s = S(t, "trail_key", "trail_type").ToLowerInvariant();
                return s.Contains("pattern") || s.Contains("loop") || s.Contains("retry") || s.Contains("cycle") || s.Contains("dependency");
            }

            var failureDominant = trails.Count(t => L(t, "failure_count") > L(t, "success_count"));
            var loopPatterns = trails.Count(Loopish);
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["query"] = query,
                // v2.20.0 Stage 7: reports must identify the learning reset boundary, so a rate
                // measured after it is never silently compared against one measured before it.
                ["learning_reset"] = Queen.Memory.LearningResetDate() is { } resetDate
                    ? new Dictionary<string, object?>
                    {
                        ["date"] = resetDate,
                        ["note"] = "derived learning state was reset at the v2.19.0 boundary; legacy trails re-enter planning after a post-reset success",
                    }
                    : null,
                ["summary"] = new Dictionary<string, object?>
                {
                    ["missions"] = missions.Count,
                    ["tasks"] = tasks.Count,
                    ["sources"] = sources.Count,
                    ["patches"] = patches.Count,
                    ["events"] = events.Count,
                    ["trails"] = trails.Count,
                    ["strong_trails"] = trails.Count(t => D(t, "strength") >= 0.6),
                    ["failure_dominant_trails"] = failureDominant,
                    ["loop_pattern_trails"] = loopPatterns,
                },
                ["missions"] = missions,
                ["tasks"] = tasks,
                ["sources"] = sources,
                ["patches"] = patches,
                ["events"] = events,
                ["trails"] = trails,
            });
        });

        // v1.8.22 Ant Inspector + Performance Observatory: per-caste task stats (all history), the
        // model route each role runs on, and the capability gates that apply to each ant.
        app.MapGet("/ants/stats", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var routes = new Dictionary<string, object?>();
            foreach (var role in new[] { "researcher", "web", "file", "coder", "builder", "verifier", "planner", "strategist" })
            {
                // v0.3.8.49 (§12): resolve through the AUTHORITATIVE router, not a second inline copy.
                //
                // This used to read `cfg?.GetValueOrDefault("model") ?? OllamaModel`, which handed
                // the local Ollama tag to EVERY provider — so the Inspector showed every ant as
                // "agent:claude-code · gemma4:31b", an agent CLI paired with a local model it has
                // never heard of and cannot serve. That is exactly the defect ModelRouter.RoleRoute
                // fixed on the call path (a keyed/agent provider with no model of its own keeps an
                // EMPTY model — the provider decides); reading the raw config here reintroduced it on
                // the display path. Now the Inspector shows the same route a mission would run.
                string provider, model;
                if (Queen.Router is not null)
                {
                    var resolved = Queen.Router.ResolveRoute(role);
                    (provider, model) = (resolved.Provider, resolved.Model);
                }
                else
                {
                    AnthillRuntime.ModelRouting.TryGetValue(role, out var cfg);
                    provider = cfg?.GetValueOrDefault("provider") ?? AnthillRuntime.DefaultModelProvider;
                    var isLocal = string.Equals(provider, AnthillRuntime.DefaultModelProvider, StringComparison.OrdinalIgnoreCase);
                    model = cfg?.GetValueOrDefault("model") ?? (isLocal ? AnthillRuntime.OllamaModel : "");
                }
                routes[role] = new Dictionary<string, object?>
                {
                    ["provider"] = provider,
                    ["model"] = model,
                };
            }
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["ants"] = Queen.Memory.AntTaskStats(),
                ["routes"] = routes,
                ["gates"] = new Dictionary<string, object?>
                {
                    ["web_search"] = AnthillRuntime.EnableWebSearch,
                    ["file_tools"] = AnthillRuntime.EnableFileTools,
                    ["file_writing"] = AnthillRuntime.EnableFileWriting,
                    ["patch_application"] = AnthillRuntime.EnablePatchApplication,
                    ["shell_tool"] = AnthillRuntime.EnableShellTool,
                },
            });
        });

        app.MapPost("/pheromones/prune", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "prune_pheromones"); if (auth is not null) return auth;
            double.TryParse(ctx.Request.Query["min_strength"].FirstOrDefault(), out var minS);
            var removed = Queen.Memory.PrunePheromones(minS <= 0 ? 0.15 : minS);
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["removed"] = removed, ["trails"] = Queen.Memory.ListPheromoneTrails(300),
            }, $"Pruned {removed} unusable pheromone trail(s).");
        });
    }
}
