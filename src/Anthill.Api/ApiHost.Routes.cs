using System.Reflection;
using Anthill.Core.Agents;
using Anthill.Modules.Reasoning;   // v0.3.8.48
using Anthill.SDK.Reasoning;      // ProviderCatalog   // v0.3.8.48: AgentCliDiscovery for the structured routes surface
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
/// The colony endpoints: missions, tasks, events, patches, approvals, tools.
///
/// v3.8.17 — split out of ApiHost.cs, which was 3,294 lines and 102 endpoints. Same class,
/// same behaviour: ApiHost has been `public static partial` with eight files since the homelab
/// moved, so this is where the file was always going to divide.
/// </summary>
public static partial class ApiHost
{
    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/", () => ApiJson.Ok(new Dictionary<string, object?>
        {
            ["name"] = "ANTHILL Core", ["version"] = AnthillRuntime.Version, ["ui"] = "/ui",
        }, "ANTHILL local API. Authenticate with X-Anthill-Token for colony endpoints."));

        // no-store: the UI is embedded in the binary, so a cached copy silently pins operators to
        // the previous version's console after an upgrade (stale canvas logic, missing panels).
        app.MapGet("/ui", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, must-revalidate";
            return Results.Content(UiHtml, "text/html");
        });

        // v2.6.3: the console script, served same-origin ('self') so the page needs no inline JS.
        // no-store for the same upgrade-staleness reason as /ui. Public like /ui — it contains no
        // secrets (all data still requires auth via the API endpoints it calls).
        app.MapGet("/ui/app.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, must-revalidate";
            return Results.Content(UiAppJs, "text/javascript; charset=utf-8");
        });

        // v2.17.1: the Missions thread reconciler. Same-origin like the others so CSP stays
        // script-src 'self'; it is pure logic with no DOM access so node --test can exercise it.
        app.MapGet("/ui/mission-thread.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, must-revalidate";
            return Results.Content(UiMissionThreadJs, "text/javascript; charset=utf-8");
        });

        // v3.3.0: the responsive dashboard grid. Same-origin like every other asset so the CSP
        // stays script-src 'self' — the console carries no inline script, and this must not be
        // the change that reintroduces one.
        app.MapGet("/ui/dashboard-grid.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, must-revalidate";
            return Results.Content(UiGridJs, "text/javascript; charset=utf-8");
        });
        // v0.3.8.52: the homelab domain, split out of app.js. Same-origin and same no-store policy
        // as every other asset, so the CSP stays script-src 'self' and a redeploy is never served a
        // stale half of the console.
        app.MapGet("/ui/homelab.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, must-revalidate";
            return Results.Content(UiHomelabJs, "text/javascript; charset=utf-8");
        });

        // v0.3.8.55: routing controls + themes, split out of app.js under the size guard.
        // Renamed from inspector-routing.js at v0.3.8.124, when the Ant Inspector page it was named
        // for was retired and routing moved into projects.
        // Same-origin, same no-store policy — the CSP stays script-src 'self'.
        app.MapGet("/ui/routing-controls.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, must-revalidate";
            return Results.Content(UiRoutingControlsJs, "text/javascript; charset=utf-8");
        });
        app.MapGet("/ui/themes.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, must-revalidate";
            return Results.Content(UiThemesJs, "text/javascript; charset=utf-8");
        });
        app.MapGet("/ui/console-extras.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, must-revalidate";
            return Results.Content(UiConsoleExtrasJs, "text/javascript; charset=utf-8");
        });
        // Colony Live (design doc §17, stage 3). Same-origin, same no-store policy — the CSP stays script-src
        // 'self'. Topology is the projection layer; live is the renderer; app.js only toggles them.
        app.MapGet("/ui/colony-topology.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, must-revalidate";
            return Results.Content(UiColonyTopologyJs, "text/javascript; charset=utf-8");
        });
        app.MapGet("/ui/colony-live.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, must-revalidate";
            return Results.Content(UiColonyLiveJs, "text/javascript; charset=utf-8");
        });
        app.MapGet("/ui/colony-host.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, must-revalidate";
            return Results.Content(UiColonyHostJs, "text/javascript; charset=utf-8");
        });
        app.MapGet("/ui/colony-home.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, must-revalidate";
            return Results.Content(UiColonyHomeJs, "text/javascript; charset=utf-8");
        });
        app.MapGet("/ui/micromound.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, must-revalidate";
            return Results.Content(UiMicromoundJs, "text/javascript; charset=utf-8");
        });
        app.MapGet("/ui/knowledge.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, must-revalidate";
            return Results.Content(UiKnowledgeJs, "text/javascript; charset=utf-8");
        });
        app.MapGet("/ui/dashboard-grid.css", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, must-revalidate";
            return Results.Content(UiGridCss, "text/css; charset=utf-8");
        });

        app.MapGet("/health", () => ApiJson.Ok(new Dictionary<string, object?>
        {
            ["status"] = "ok", ["version"] = AnthillRuntime.Version,
            ["native_kernel"] = Anthill.Core.Native.NativeKernel.UsingNative ? "active" : "managed-fallback",
            // v2.14.3: UI feature flag (not a secret — it only decides which console shell renders).
            ["dashboard_workspace_enabled"] = AnthillRuntime.EnableDashboardWorkspace,
        }));

        ProtectedJson(app, "/status", "read_status", _ =>
        {
            var events = Queen.Memory.SummarizeEvents();
            var tasks = Queen.Memory.SummarizeTaskMetrics();
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["version"] = AnthillRuntime.Version, ["safety_profile"] = AnthillRuntime.Config.SafetyProfile,
                ["native_kernel"] = Anthill.Core.Native.NativeKernel.UsingNative ? "active" : "managed-fallback",
                ["events"] = events.GetValueOrDefault("event_count"), ["failures"] = events.GetValueOrDefault("failure_event_count"),
                ["tasks"] = tasks.GetValueOrDefault("task_count"), ["pending_approvals"] = Queen.Memory.CountPendingApprovals(),
                ["model_calls"] = Queen.Router?.CallCount ?? 0,
                ["api_host"] = AnthillRuntime.ApiHost, ["api_port"] = AnthillRuntime.ApiPort,
                ["reachable_ip"] = NetworkUtil.IsWildcardBindHost(AnthillRuntime.ApiHost) ? NetworkUtil.GetLikelyLanIPv4() : AnthillRuntime.ApiHost,
            });
        });

        ProtectedJson(app, "/selftest", "read_selftest", _ =>
        {
            var report = SelfTest.Run(Queen);
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["ok"] = report.Ok, ["checks_passed"] = report.ChecksPassed, ["checks_failed"] = report.ChecksFailed,
                ["checks_warning"] = report.ChecksWarning, ["report"] = SelfTest.FormatReport(report),
            });
        });

        ProtectedText(app, "/diagnostics", "read_diagnostics", () => Queen.FormatRuntimeDiagnostics());
        ProtectedText(app, "/config", "read_config", () => Queen.FormatConfigStatus());
        ProtectedText(app, "/schema", "read_schema", () => Queen.FormatSchemaStatus());
        ProtectedText(app, "/memory", "read_memory", () => Queen.FormatMemoryView());
        ProtectedText(app, "/events", "read_events", () => Queen.FormatEventLog());
        ProtectedText(app, "/tasks", "read_tasks", () => Queen.FormatTaskMetrics());
        ProtectedText(app, "/messages", "read_messages", () => Queen.FormatMessageMetrics());
        ProtectedText(app, "/communication", "read_communication", () => Queen.FormatAgentCommunication());
        ProtectedText(app, "/pheromones", "read_pheromones", () => Queen.FormatPheromoneView());
        ProtectedText(app, "/models", "read_models", () => Queen.FormatModelStatus());
        ProtectedText(app, "/routes", "read_models", () => Queen.FormatModelRoutes());

        /*
         * v0.3.8.48 — the STRUCTURED routes surface (defects 16/18): per-role provider+model as
         * data, plus the unified list of models that can actually run — installed local models,
         * configured providers' catalogs, installed agents. The console stops parsing prose.
         */
        app.MapGet("/routes/json", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_models"); if (auth is not null) return auth;

            var available = new List<Dictionary<string, object?>>();
            // Installed local models, from the live Ollama the colony actually calls.
            foreach (var m in await DiscoverOllamaModelsAsync())
                available.Add(new() { ["provider"] = "ollama", ["model"] = m,
                    ["label"] = $"{m} · Ollama" });
            // Configured keyed providers offer their curated lists; unconfigured ones offer nothing.
            var configured = Queen.Memory.ListProviderConnections()
                .Where(c => Convert.ToBoolean(c.GetValueOrDefault("configured") ?? false))
                .Select(c => c.GetValueOrDefault("provider")?.ToString() ?? "")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var p in ProviderCatalog.All.Where(p => configured.Contains(p.Id)))
                foreach (var m in p.Models ?? Array.Empty<string>())
                    available.Add(new() { ["provider"] = p.Id, ["model"] = m,
                        ["label"] = $"{m} · {p.Name}" });
            // Installed agents route as themselves.
            foreach (var a in AgentCliDiscovery.Scan().Where(x => x.Installed))
                available.Add(new() { ["provider"] = a.Agent.Id, ["model"] = a.Agent.DisplayName,
                    ["label"] = $"{a.Agent.DisplayName} · agent" });

            var availableKeys = available
                .Select(m => $"{m["provider"]}|{m["model"]}".ToLowerInvariant()).ToHashSet();
            var roles = AnthillRuntime.RoutableRoles.Select(role =>
            {
                var (provider, model) = Queen.Router!.GetRoute(role);
                return new Dictionary<string, object?>
                {
                    ["role"] = role, ["provider"] = provider, ["model"] = model,
                    // An unavailable CURRENT selection is warned about, never hidden.
                    ["available"] = availableKeys.Contains($"{provider}|{model}".ToLowerInvariant()),
                };
            }).ToList();

            return ApiJson.Ok(new Dictionary<string, object?>
                { ["roles"] = roles, ["available_models"] = available });
        });

        // Merge-safe single-role update (defect 18's fix): ONE role changes, the rest of
        // model_routes is untouched, and the router re-projects from the saved config.
        app.MapPost("/routes/{role}", async (HttpContext ctx, string role) =>
        {
            var auth = RequireAuth(ctx, "manage_models"); if (auth is not null) return auth;
            if (!AnthillRuntime.RoutableRoles.Contains(role))
                return ApiJson.Error($"'{role}' is not a routable role.", "bad_request");
            RouteBody? body;
            try { body = await ctx.Request.ReadFromJsonAsync<RouteBody>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.Provider) || string.IsNullOrWhiteSpace(body?.Model))
                return ApiJson.Error("Both provider and model are required.", "bad_request");

            // v0.3.8.96 — write-through, in one call. This handler used to update the live
            // ModelRouting dictionary and then SaveConfig — which serializes the Config OBJECT,
            // whose ModelRoutes it never touched: every save persisted the stale routes, and a
            // route lived exactly until the next restart. Found live, by restarting mid-
            // qualification. The runtime owns both halves now.
            AnthillRuntime.SetModelRoute(role, body!.Provider!, body.Model!);
            return ApiJson.Ok(new Dictionary<string, object?>
                { ["role"] = role, ["provider"] = body.Provider, ["model"] = body.Model },
                $"{role} now routes to {body.Model} ({body.Provider}).");
        });

        // Per-provider circuit-breaker health: which model routes are healthy vs. open (cooling down
        // after repeated timeouts) vs. half-open (probing). Powers the console's provider-health chip.
        app.MapGet("/providers/health", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_models"); if (auth is not null) return auth;
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["breaker_enabled"] = AnthillRuntime.EnableModelCircuitBreaker,
                ["failure_threshold"] = AnthillRuntime.ModelCircuitFailureThreshold,
                ["cooldown_seconds"] = AnthillRuntime.ModelCircuitCooldownSeconds,
                ["call_timeout_seconds"] = AnthillRuntime.ModelCallTimeoutSeconds,
                ["providers"] = Queen.Router?.ProviderHealth() ?? new List<Dictionary<string, object?>>(),
            });
        });

        // Is a newer release published on the public GitHub repo? Cached; ?force=1 bypasses.
        app.MapGet("/update/check", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var force = ctx.Request.Query["force"].FirstOrDefault() is "1" or "true";
            return ApiJson.Ok(UpdateChecker.Check(force));
        });

        // Consolidated header status: version, what's actually online (API + Ollama reachability),
        // the active default model, and whether routing is fully local or uses cloud providers.
        app.MapGet("/system/summary", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            return ApiJson.Ok(SystemSummary());
        });
        ProtectedText(app, "/sources", "read_sources", () => Queen.FormatSources());
        ProtectedText(app, "/source-quality", "read_sources", () => Queen.FormatSourceQuality());
        // NOTE: GET /patches is the structured Patch Center list (app.MapGet below). The legacy
        // ProtectedText "/patches" (Queen.FormatPatchList) was a DUPLICATE registration of the same
        // route template — two endpoints matching /patches threw AmbiguousMatchException in routing
        // (before any handler/middleware), surfacing as an uncatchable empty HTTP 500. Removed.
        ProtectedText(app, "/approvals", "read_approvals", () => Queen.FormatApprovals());
        ProtectedText(app, "/missions", "read_status", () => Queen.FormatMissionHistory());

        ProtectedJson(app, "/graph", "read_graph", ctx =>
        {
            var includeResults = ctx.Request.Query["include_results"] == "true";
            if (includeResults && !ApiPermissionAllowed("read_graph_results"))
                return ApiJson.Error("Permission denied: read_graph_results is disabled.", "permission_denied");
            return ApiJson.Ok(Queen.BuildTaskGraphData(includeResults: includeResults));
        });

        // JSON mission history for the Results page: one row per mission, newest first.
        app.MapGet("/missions/json", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var limit = Math.Clamp(int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? l : 50, 1, AnthillRuntime.ApiMaxLimit);
            var rows = Queen.Memory.GetRecentMissions(limit)
                .Where(m => m.GetValueOrDefault("id")?.ToString() != AnthillRuntime.SystemApiMissionId)
                .Select(m =>
                {
                    // v2.18.2: the ANSWER was missing from this projection entirely, so the
                    // Missions conversation had nothing to show and every finished exchange read
                    // "Working — no answer recorded yet" forever. Present since v2.16.0.
                    //
                    // Bounded rather than raw: this endpoint returns up to 100 missions and
                    // user_result can be a whole diff or file dump. The full text always remains
                    // available from /missions/{id}/report, which the activity disclosure loads.
                    var answer = (m.GetValueOrDefault("final_result")?.ToString()
                                  ?? m.GetValueOrDefault("user_result")?.ToString() ?? "").Trim();
                    var clipped = answer.Length > MissionAnswerPreviewChars;
                    return new Dictionary<string, object?>
                    {
                        ["id"] = m.GetValueOrDefault("id"), ["goal"] = m.GetValueOrDefault("goal"),
                        ["status"] = m.GetValueOrDefault("status"), ["success_score"] = m.GetValueOrDefault("success_score"),
                        ["created_at"] = m.GetValueOrDefault("created_at"), ["saved_at"] = m.GetValueOrDefault("saved_at"),
                        ["answer"] = clipped ? TextUtil.Truncate(answer, MissionAnswerPreviewChars, "") : answer,
                        ["answer_truncated"] = clipped,
                    };
                }).ToList();
            return ApiJson.Ok(rows);
        });
        app.MapGet("/missions/{id}", (HttpContext ctx, string id) =>
            RequireAuth(ctx, "read_status") ?? Results.Text(Queen.FormatMissionDetail(id), "text/plain"));
        // Structured, human-readable mission report: what the mission was, what the colony
        // produced (mission-level output, separate from per-task outputs), which tangible
        // changes it proposed (patches + their approval state), and anything that went wrong.
        app.MapGet("/missions/{id}/report", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            // The report can surface patch proposals, approval state, and autonomy objectives —
            // all admin-only reads (read_patches/read_approvals/read_objectives are never in the
            // coordinator set). Include those sections only for callers who could read them
            // directly, so the report can't become a side channel around the permission model.
            var sensitive = CallerHas(ctx, "read_patches");
            return MissionReport(id, sensitive);
        });
        app.MapGet("/missions/{id}/graph", (HttpContext ctx, string id) =>
            RequireAuth(ctx, "read_graph") ?? ApiJson.Ok(Queen.BuildTaskGraphData(id)));
        // v1.8.22: the ant colony registry (roles, workers, permission contracts) + worker telemetry.
        app.MapGet("/colony/registry", (HttpContext ctx) =>
            RequireAuth(ctx, "read_graph") ?? ApiJson.Ok(new Dictionary<string, object?>
            {
                ["roles"] = AntRegistry.Roles,
                ["validation_errors"] = AntRegistry.ValidateRegistry(),
                ["view_modes"] = new[] { "command", "expanded", "active", "group" },
                ["executable_roles"] = AntRegistry.ExecutableRoleIds.ToList(),
                // v2.24.0 Phase D: the activation ceiling, stated plainly. Without this the console
                // could show a role as gated-off with no way to tell whether its own flag or the
                // tier was responsible — two different fixes wearing the same symptom.
                ["activation"] = new Dictionary<string, object?>
                {
                    // v3.8.26: one switch instead of nine, reported so an operator can see which
                    // profile produced the state below rather than reverse-engineering it from
                    // eight booleans.
                    ["roster_profile"] = AnthillRuntime.RosterProfile,
                    ["disabled_roles"] = AnthillRuntime.DisabledRoles.OrderBy(r => r, StringComparer.Ordinal).ToList(),
                    ["tier"] = ActivationTiers.Name(AnthillRuntime.ActivationTier),
                    ["explanation"] = ActivationTiers.Explain(AnthillRuntime.ActivationTier),
                    ["specialist_execution_enabled"] = AnthillRuntime.EnableSpecialistAntExecution,
                    // v3.8.26 — READINESS, per role, in one place.
                    //
                    // Every field here already existed and was answerable only by reading source or
                    // correlating three endpoints. An operator deciding whether to open a gate needs
                    // ONE row per role that says what it is, how it gets scheduled, whether its
                    // handler and contract exist, whether the tools it declares are actually
                    // registered, and — when it is not ready — the single reason why.
                    //
                    // `blocked_reason` is the field that matters. Every other field can be read as
                    // "something is off somewhere"; this one names it.
                    //
                    // v3.8.32 — the ladder MOVED to Anthill.Core.Agents.RoleReadiness. It lived here
                    // as a lambda, which is why it was wrong for the six core ants for six releases:
                    // nothing could call it without an HTTP host, so nothing ever tested it over the
                    // whole roster. The defect and its reasoning are recorded on RoleReadiness.
                    ["roles"] = RoleReadiness
                        .ForAllRoles(Queen.Tools.Names, Queen.Tools.GrantedCapabilities)
                        .Select(r => new Dictionary<string, object?>
                        {
                            ["role_id"] = r.RoleId,
                            ["ready"] = r.Ready,
                            ["blocked_reason"] = r.BlockedReason,
                            ["scheduling_mode"] = r.SchedulingMode,
                            ["handler_present"] = r.HandlerPresent,
                            ["contract_present"] = true,   // every row here is a contract key
                            ["contract_version"] = r.ContractVersion,
                            ["declared_tools"] = r.DeclaredTools,
                            ["unregistered_tools"] = r.UnregisteredTools,
                            ["required_capabilities"] = r.RequiredCapabilities,
                            ["ungranted_capabilities"] = r.UngrantedCapabilities,
                            // `gated` says whether the two fields after it mean anything for this
                            // role at all. Both used to report false for core ants.
                            ["gated"] = r.Gated,
                            ["gate_status"] = r.GateStatus.ToString(),
                            ["admitted_by_tier"] = r.AdmittedByTier,
                            ["gate_open"] = r.GateOpen,
                            ["runtime_available"] = r.RuntimeAvailable,
                        }).ToList(),
                },
                ["worker_telemetry"] = Queen.Memory.SummarizeWorkerTelemetry(),
                // Execution framework Stage F: truthful per-role runtime state — the UI must never
                // reduce "not running" to a single ambiguous 'inactive'.
                ["runtime_status"] = AntExecutorCatalog.Snapshot.Values.Select(a => new Dictionary<string, object?>
                {
                    ["role_id"] = a.RoleId,
                    ["runtime_kind"] = a.RuntimeKind.ToString(),
                    ["implemented"] = a.Implemented,
                    ["enabled"] = a.Enabled,
                    ["planner_eligible"] = a.PlannerEligible,
                    ["runtime_available"] = a.RuntimeAvailable,
                    ["unavailability_reason"] = a.UnavailabilityReason,
                    ["status_label"] = a.RuntimeKind switch
                    {
                        Anthill.Core.Agents.AntRuntimeKind.ControlPlane => "Control Plane — Online",
                        Anthill.Core.Agents.AntRuntimeKind.DeterministicService => "Deterministic Service — Online",
                        Anthill.Core.Agents.AntRuntimeKind.VisualScaffold => "Visual Scaffold — Not Implemented",
                        _ when a.RuntimeAvailable => "Mission Agent — Idle",
                        _ when a.Implemented => $"Mission Agent — {(a.UnavailabilityReason.Contains("disabled") ? "Disabled" : "Unavailable")}",
                        _ => "Unavailable — Missing Runtime Handler",
                    },
                }).ToList(),
            }));
        app.MapGet("/colony/workers/telemetry", (HttpContext ctx) =>
            RequireAuth(ctx, "read_graph") ?? ApiJson.Ok(Queen.Memory.SummarizeWorkerTelemetry()));
        app.MapGet("/sources/{id}", (HttpContext ctx, string id) =>
            RequireAuth(ctx, "read_sources") ?? Results.Text(Queen.FormatSourceDetail(id), "text/plain"));
        app.MapGet("/patches/{id}", (HttpContext ctx, string id) =>
            RequireAuth(ctx, "read_patches") ?? Results.Text(Queen.FormatPatchDetail(id), "text/plain"));
        app.MapGet("/approvals/{id}", (HttpContext ctx, string id) =>
            RequireAuth(ctx, "read_approvals") ?? Results.Text(Queen.FormatApprovalDetail(id), "text/plain"));

        // ---- Patch Center (v1.8.16): structured JSON for the visual patch review page ----
        // Filterable list of patch proposals (status, mission, objective, file substring, risk).
        app.MapGet("/patches", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_patches"); if (auth is not null) return auth;
            try
            {
                var q = ctx.Request.Query;
                PatchStatus? status = null;
                var statusQ = (q["status"].FirstOrDefault() ?? "").Trim().ToLowerInvariant();
                // "pending" is the UI label for a proposed (awaiting-approval) patch.
                if (statusQ is "pending") status = PatchStatus.Proposed;
                else if (statusQ.Length > 0) status = ParsePatchStatusOrNull(statusQ);
                var missionId = q["mission_id"].FirstOrDefault();
                var objectiveId = q["objective_id"].FirstOrDefault();
                var file = q["file"].FirstOrDefault();
                var riskFilter = RiskLevel.Normalize(q["risk"].FirstOrDefault());
                var wantRisk = !string.IsNullOrWhiteSpace(q["risk"].FirstOrDefault());
                int.TryParse(q["limit"].FirstOrDefault(), out var limit);
                var rows = Queen.Memory.ListPatchesForCenter(status, missionId, objectiveId, file, limit <= 0 ? 200 : limit)
                    .Select(PatchCenterRow)
                    .Where(r => !wantRisk || (r.GetValueOrDefault("risk")?.ToString() == riskFilter))
                    .ToList();
                return ApiJson.Ok(rows);
            }
            catch (Exception ex) { return ApiJson.Error($"Could not load patches: {ex.Message}", "patch_list_error"); }
        });
        // Full detail for one patch, including the sealed old/new content for the diff view.
        app.MapGet("/patches/{id}/detail", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_patches"); if (auth is not null) return auth;
            try { return PatchDetailJson(id); }
            catch (Exception ex) { return ApiJson.Error($"Could not load patch detail: {ex.Message}", "patch_detail_error"); }
        });

        app.MapGet("/jobs", (HttpContext ctx) => RequireAuth(ctx, "read_status") ?? ApiJson.Ok(Jobs.ListJobs()));
        app.MapGet("/jobs/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            // v0.3.8.38: durable-aware. This read live memory only, so a job listed by /jobs
            // returned not-found here after a restart or once trimming evicted it.
            var job = Jobs.GetJobProjection(id);
            return job is null ? ApiJson.Error($"No job found with id: {id}", "not_found") : ApiJson.Ok(job);
        });
        app.MapPost("/jobs/{id}/cancel", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;
            var ok = Jobs.Cancel(id);
            return ApiJson.Ok(new Dictionary<string, object?> { ["id"] = id, ["cancelled"] = ok },
                ok ? "Job cancelled (queued work dropped; a running mission finishes)." : "Job not found or already finished.");
        });
        app.MapPost("/jobs/cancel-all", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;
            var n = Jobs.CancelAll();
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "jobs_cancel_all", $"Cancelled {n} non-terminal job(s).", antName: "operator");
            return ApiJson.Ok(new Dictionary<string, object?> { ["cancelled"] = n },
                $"Cancelled {n} job(s). Queued work dropped; any running mission finishes (bounded by its timeout).");
        });

        app.MapPost("/missions", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;
            if (!MissionLimiter.IsAllowed(ClientIp(ctx)))
                return ApiJson.Error("Mission rate limit exceeded. Try again shortly.", "rate_limited");
            MissionRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<MissionRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var goal = (body?.Goal ?? "").Trim();
            if (goal.Length == 0) return ApiJson.Error("Mission goal is required.", "bad_request");
            if (AnthillRuntime.MaxGoalLength > 0 && goal.Length > AnthillRuntime.MaxGoalLength) return ApiJson.Error("Mission goal is too long.", "bad_request");

            // v0.3.8.38 — idempotency reaches the store at last. Header first (the conventional
            // place, and what a proxy or retrying client sets), body second.
            //
            // BOUNDED and validated: an unbounded key becomes an unbounded index entry, and a key
            // with control characters becomes a logging problem. Too long or malformed is rejected
            // rather than silently truncated — a truncated key collides with other truncated keys,
            // which would suppress DIFFERENT missions as duplicates. That is worse than no key.
            var key = (ctx.Request.Headers["Idempotency-Key"].FirstOrDefault() ?? body?.IdempotencyKey ?? "").Trim();
            if (key.Length > 200)
                return ApiJson.Error("Idempotency-Key must be 200 characters or fewer.", "bad_request");
            if (key.Length > 0 && !key.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ':' or '.'))
                return ApiJson.Error("Idempotency-Key may contain only letters, digits, '-', '_', ':' and '.'.", "bad_request");

            var submitted = Jobs.Submit(goal, key.Length == 0 ? null : key);
            var dict = submitted.ToDict();
            // Replay is reported rather than hidden: a client that retried deserves to know it got
            // the original mission back instead of a second one.
            dict["idempotency_key"] = key.Length == 0 ? null : key;
            return ApiJson.Ok(dict, "Mission queued.");
        });

        /*
         * v3.4.0 (ADR-006) — run one tool-calling conversation and return the whole transcript.
         *
         * This is the tool loop's first production call site, and it is deliberately the smallest
         * one that is honest: an operator asks for something, the agent reasons and calls tools
         * under its role's authorization, and every step comes back. Missions remain the durable,
         * scheduled path — this is the direct one, for asking a question and seeing the work.
         *
         * SYNCHRONOUS, and bounded because of it. A tool-calling run is several model calls, so it
         * takes as long as it takes; the LoopBudget caps turns, tool calls and wall-clock, and the
         * request's own cancellation token aborts it if the operator gives up. Making this
         * fire-and-forget would need a job record to be worth anything, and that is the mission
         * path, which already exists.
         *
         * run_mission, not read_status: this SPENDS model budget and can invoke tools. Whether a
         * given tool may run is still the registry's decision, per role.
         */
        app.MapPost("/agent/run", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;
            if (!MissionLimiter.IsAllowed(ClientIp(ctx)))
                return ApiJson.Error("Agent run rate limit exceeded. Try again shortly.", "rate_limited");

            AgentRunRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<AgentRunRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }

            var goal = (body?.Goal ?? "").Trim();
            if (goal.Length == 0) return ApiJson.Error("A goal is required.", "bad_request");
            if (AnthillRuntime.MaxGoalLength > 0 && goal.Length > AnthillRuntime.MaxGoalLength)
                return ApiJson.Error("Goal is too long.", "bad_request");

            // The role decides which tools exist for this run, so an unknown one must be refused
            // rather than defaulted — silently downgrading to some other role's toolset would be a
            // capability decision made by a typo.
            var role = string.IsNullOrWhiteSpace(body?.Role) ? "researcher" : body!.Role!.Trim().ToLowerInvariant();
            if (!AntRegistry.ExecutableRoleIds.Contains(role))
                return ApiJson.Error($"Unknown or non-executable role '{role}'.", "bad_request");

            var opening = new List<ModelMessage>();
            if (!string.IsNullOrWhiteSpace(body?.System))
                opening.Add(new ModelMessage(ModelMessage.System, body!.System!));
            opening.Add(new ModelMessage(ModelMessage.User, goal));

            var budget = new LoopBudget(
                MaxTurns: Math.Clamp(body?.MaxTurns ?? 8, 1, 24),
                MaxToolCalls: Math.Clamp(body?.MaxToolCalls ?? 24, 1, 96));

            /*
             * An agent run is recorded as a MISSION before it starts, and the reason is not
             * bookkeeping.
             *
             * The first version passed no mission id, and the consequence was worse than untidy:
             * ModelRouter only writes its model_call event and pheromone update when it HAS one, so
             * a run spent real model budget, invoked real tools, and left no trace at all — nothing
             * in /events, no route reinforcement, no token accounting. A run nobody can audit
             * afterwards is not something an operator should be able to start, and the failure was
             * invisible precisely because the endpoint's own response looked complete.
             *
             * It also has to be a real row rather than a synthetic id: `events` has a foreign key to
             * missions(id), so a made-up id would be rejected and the logging would silently do
             * nothing — the same shape of bug one layer down.
             *
             * Saved BEFORE the run so the trail exists even if the process dies mid-conversation,
             * which is exactly when an operator most wants to know what it had already done.
             */
            var run = new Mission { Goal = goal, Status = MissionStatus.Running };
            Queen.Memory.SaveMission(run);
            Queen.Memory.LogEvent(run.Id, "agent_run_started",
                $"Agent run started for role {role}.", null, role,
                new() { ["role"] = role, ["max_turns"] = budget.MaxTurns, ["max_tool_calls"] = budget.MaxToolCalls });

            var result = await ThreadingTask.Run(() => ToolCallingLoop.Run(
                Queen.Router!, Queen.Tools, role, opening, budget,
                missionId: run.Id,
                model: string.IsNullOrWhiteSpace(body?.Model) ? null : body!.Model!.Trim(),
                cancellationToken: ctx.RequestAborted), ctx.RequestAborted);

            run.Status = result.Completed ? MissionStatus.Complete : MissionStatus.Failed;
            run.FinalResult = result.Content;
            // The transcript is the debug record: what it DID, which is the question asked of an
            // agent run, and the one a final answer alone cannot answer.
            run.DebugResult = string.Join("\n\n", result.Transcript.Select(m => $"[{m.Role}] {m.Content}"));
            Queen.Memory.SaveMission(run);
            Queen.Memory.LogEvent(run.Id, "agent_run_finished",
                $"Agent run {result.StopReason} after {result.Turns} turn(s), {result.ToolCalls} tool call(s).",
                null, role,
                new()
                {
                    ["completed"] = result.Completed, ["stop_reason"] = result.StopReason,
                    ["turns"] = result.Turns, ["tool_calls"] = result.ToolCalls,
                    ["prompt_tokens"] = result.Usage.PromptTokens,
                    ["completion_tokens"] = result.Usage.CompletionTokens,
                });

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                // The run id is returned so an operator can find this conversation again — the
                // browser losing its state must not lose the record of what the agent did.
                ["run_id"] = run.Id,
                ["role"] = role,
                ["content"] = result.Content,
                ["completed"] = result.Completed,
                ["stop_reason"] = result.StopReason,
                ["turns"] = result.Turns,
                ["tool_calls"] = result.ToolCalls,
                ["status"] = result.LastStatus.Name(),
                // Absent usage stays absent — a provider that reports nothing is unknown, not free.
                ["prompt_tokens"] = result.Usage.PromptTokens,
                ["completion_tokens"] = result.Usage.CompletionTokens,
                // The transcript IS the deliverable: what it did, not just what it concluded.
                ["transcript"] = result.Transcript.Select(m => new Dictionary<string, object?>
                {
                    ["role"] = m.Role,
                    ["content"] = m.Content,
                    ["tool_call_id"] = m.ToolCallId,
                }).ToList(),
            }, result.Completed ? "Agent run completed." : $"Agent run stopped: {result.StopReason}.");
        });

        // v1.8.18 Mission Composer: dry-run the planner for a goal and return the task plan WITHOUT
        // creating or executing a mission, so the operator can review (and see how verification-only
        // / no-patch constraints reshape the plan) before approving dispatch.
        app.MapPost("/missions/plan", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;
            if (!MissionLimiter.IsAllowed(ClientIp(ctx)))
                return ApiJson.Error("Plan rate limit exceeded. Try again shortly.", "rate_limited");
            MissionRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<MissionRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var goal = (body?.Goal ?? "").Trim();
            if (goal.Length == 0) return ApiJson.Error("Mission goal is required.", "bad_request");
            if (AnthillRuntime.MaxGoalLength > 0 && goal.Length > AnthillRuntime.MaxGoalLength) return ApiJson.Error("Mission goal is too long.", "bad_request");
            try
            {
                // v3.1.0: the plan carries its own explanation. This endpoint used to re-parse the
                // goal for constraints and re-run AntRegistry.ValidateTask over every task to
                // rebuild warnings the planning path had already computed — two readings of one
                // plan, free to disagree. It now reports what the plan says.
                var plan = Queen.PlanPreview(goal);
                var tasks = plan.Tasks;
                var constraints = plan.Constraints;
                var indexById = tasks.Select((t, i) => (t.Id, N: i + 1)).ToDictionary(x => x.Id, x => x.N);
                var rows = tasks.Select((t, i) => new Dictionary<string, object?>
                {
                    ["index"] = i + 1,
                    ["title"] = t.Title,
                    ["ant"] = t.AssignedAnt,
                    ["worker"] = t.AssignedWorker,
                    ["display"] = t.AssignedWorker ?? t.AssignedAnt,
                    ["task_type"] = t.TaskType,
                    ["description"] = TextUtil.Truncate(t.Description, 400),
                    ["critical"] = t.Critical,
                    // v3.1.0: WHICH task the runtime would refuse, not just a deduplicated list of
                    // reasons. The preview used to skip admission entirely and could therefore show
                    // a step that dispatch would reject on sight.
                    ["blocked"] = t.FailureType == PlanningService.AdmissionRefusedFailureType,
                    ["blocked_reason"] = t.FailureType == PlanningService.AdmissionRefusedFailureType
                        ? t.FailureReason : null,
                    // Dependencies rendered as human 1-based step numbers (task ids are GUIDs).
                    ["depends_on"] = t.DependsOn.Select(d => indexById.GetValueOrDefault(d, 0)).Where(n => n > 0).ToList(),
                }).ToList();
                return ApiJson.Ok(new Dictionary<string, object?>
                {
                    ["goal"] = goal,
                    ["task_count"] = tasks.Count,
                    ["spec_ingestion"] = plan.SpecIngestion,
                    ["has_coder_task"] = tasks.Any(t => t.AssignedAnt == "coder"),
                    // v1.8.22: worker path the plan resolves to, plus any capability warnings.
                    ["selected_path"] = tasks.Select(t => t.AssignedWorker ?? t.AssignedAnt).ToList(),
                    ["constraint_warnings"] = plan.RefusalReasons,
                    ["constraints"] = new Dictionary<string, object?>
                    {
                        ["verification_only"] = constraints.VerificationOnly,
                        ["read_only"] = constraints.ReadOnly,
                        ["no_patches"] = constraints.NoPatches,
                        ["one_shot"] = constraints.OneShot,
                        ["blocks_patches"] = constraints.BlocksPatches,
                    },
                    ["tasks"] = rows,
                }, "Plan generated (preview only — no mission was created).");
            }
            catch (Exception ex) { return ApiJson.Error($"Could not generate plan: {ex.Message}", "plan_error"); }
        });

        // Proxy Ollama /api/tags so the UI can list available models without a direct connection
        app.MapGet("/ollama/models", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_models"); if (auth is not null) return auth;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var host = AnthillRuntime.OllamaHost.TrimEnd('/');
                var resp = await InternalHttp.GetAsync($"{host}/api/tags", cts.Token);
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                return Results.Content(body, "application/json");
            }
            catch (Exception ex) { return ApiJson.Error($"Cannot reach Ollama: {ex.Message}", "ollama_unreachable"); }
        });

        app.MapPost("/approve/{id}", (HttpContext ctx, string id) =>
            RequireAuth(ctx, "approve") ?? Results.Text(Queen.ApproveRequest(id), "text/plain"));
        app.MapPost("/reject/{id}", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "reject"); if (auth is not null) return auth;
            RejectBody? body = null;
            try { body = await ctx.Request.ReadFromJsonAsync<RejectBody>(); } catch { /* optional */ }
            return Results.Text(Queen.RejectRequest(id, body?.Reason), "text/plain");
        });
        app.MapPost("/apply/{id}", (HttpContext ctx, string id) =>
            RequireAuth(ctx, "apply_patch") ?? Results.Text(Queen.ApplyApprovedPatch(id), "text/plain"));
        // v2.7.0: manually revert an APPLIED patch by patch id (delete added file / restore backup).
        // Same write permission as apply — reverting also writes to the sandboxed workspace.
        app.MapPost("/revert/{id}", (HttpContext ctx, string id) =>
            RequireAuth(ctx, "apply_patch") ?? Results.Text(Queen.RevertAppliedPatch(id), "text/plain"));

        // ---- Patch Center 2.0 (v1.8.24): operator actions by PATCH id ----
        // Approve/reject pending patches that have no approval record (the record is created
        // first, then the normal approve/reject transition runs — never a direct status write).
        app.MapPost("/patches/{id}/approve", (HttpContext ctx, string id) =>
            RequireAuth(ctx, "approve") ?? Results.Text(Queen.ApprovePatchDirect(id, CurrentUsername(ctx) ?? "operator"), "text/plain"));
        // v0.3.8.51 (field report): the chat card's single action, as JSON. Approve-then-apply
        // through the same audited transitions, one structured answer — the two text/plain
        // endpoints above stay for their existing callers.
        app.MapPost("/patches/{id}/approve-apply", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "apply_patch"); if (auth is not null) return auth;
            var (ok, message) = Queen.ApproveAndApplyPatch(id, CurrentUsername(ctx) ?? "operator");
            return ok ? ApiJson.Ok(null, message) : ApiJson.Error(message, "approve_apply_failed");
        });
        app.MapPost("/patches/{id}/reject", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "reject"); if (auth is not null) return auth;
            RejectBody? body = null;
            try { body = await ctx.Request.ReadFromJsonAsync<RejectBody>(); } catch { /* optional */ }
            return Results.Text(Queen.RejectPatchDirect(id, body?.Reason, CurrentUsername(ctx) ?? "operator"), "text/plain");
        });
        // Operator edits a proposal's content and offers it as an alternative patch. The
        // alternative is a new proposal behind the standard approval gate; nothing touches disk.
        app.MapPost("/patches/{id}/alternative", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "approve"); if (auth is not null) return auth;
            AlternativePatchBody? body;
            try { body = await ctx.Request.ReadFromJsonAsync<AlternativePatchBody>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrEmpty(body?.NewContent)) return ApiJson.Error("new_content is required.", "bad_request");
            var (ok, newId, message) = Queen.ProposeAlternativePatch(
                id, body.NewContent, body.Reason ?? "", CurrentUsername(ctx) ?? "operator", body.SupersedeOriginal ?? true);
            return ok
                ? ApiJson.Ok(new Dictionary<string, object?> { ["new_patch_id"] = newId, ["superseded_original"] = body.SupersedeOriginal ?? true }, message)
                : ApiJson.Error(message, "alternative_failed");
        });
        // Unbiased verification: apply-with-backup → run verify (build+test or operator cmd) →
        // ALWAYS restore. Green ⇒ auto-approve (never auto-apply); red ⇒ stays pending with notes.
        app.MapPost("/patches/{id}/verify", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "approve"); if (auth is not null) return auth;
            try { return ApiJson.Ok(PatchVerifyRunner.VerifyAndMaybeApprove(Queen, id)); }
            catch (Exception ex) { return ApiJson.Error($"Verification error: {ex.Message}", "verify_error"); }
        });

        MapAuthEndpoints(app);
        MapAutonomyEndpoints(app);
        MapDashboardEndpoints(app);
        MapProviderEndpoints(app);
        MapAgentEndpoints(app);   // v3.8.39: installable CLI agents — see ApiHost.Agents.cs
    }
}
