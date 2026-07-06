using System.Reflection;
using Anthill.Core.Agents;
using Anthill.Core.Autonomy;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Diagnostics;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Models;
using Anthill.Core.Orchestration;
using Anthill.Core.Planning;
using Anthill.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Anthill.Api;

/// <summary>
/// Builds and runs the secured ANTHILL API host. Mirrors the FastAPI surface of the Python
/// build: constant-time token auth, failed-auth + mission rate limiting, hardened response
/// headers, no public docs endpoints, permission-gated reads, and the embedded colony UI.
/// </summary>
public static partial class ApiHost
{
    public static Queen Queen { get; private set; } = null!;
    public static ApiJobRegistry Jobs { get; private set; } = null!;
    public static ColonyDirector Director { get; private set; } = null!;
    private static RateLimiter MissionLimiter = null!;
    private static RateLimiter AuthLimiter = null!;
    private static string UiHtml = "";
    // One shared client for the host's own internal probes (Ollama reachability, model list).
    // A per-request `new HttpClient` leaks sockets under the header's periodic polling; this
    // reuses connections. Per-call timeouts are applied via CancellationToken.
    private static readonly HttpClient InternalHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static int Run(string[] args)
    {
        AnthillRuntime.Initialize();

        // Fail loudly at boot if the security posture is unsafe.
        try { TokenSecurity.ValidateApiRuntimeSecurity(); }
        catch (AnthillSecurityException ex) { Console.Error.WriteLine(ex.Message); return 1; }

        // --autonomous starts the Director immediately at boot (still gated by autonomy_enabled).
        var autostart = args.Contains("--autonomous");
        var hostArgs = args.Where(a => a != "--autonomous").ToArray();

        var builder = WebApplication.CreateBuilder(hostArgs);
        builder.WebHost.UseUrls($"http://{AnthillRuntime.ApiHost}:{AnthillRuntime.ApiPort}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        Queen = new Queen();
        // Phase 3: the Director multiplexes its concurrent missions through this same worker
        // pool, so size it to whichever is larger — api_job_workers or autonomy_concurrency —
        // ensuring autonomous missions can actually run side by side without starving user jobs.
        var jobWorkers = Math.Max(AnthillRuntime.ApiJobWorkers,
            AnthillRuntime.EnableAutonomy ? AnthillRuntime.AutonomyConcurrency : 1);
        Jobs = new ApiJobRegistry(Queen, jobWorkers);
        Director = new ColonyDirector(Queen, Jobs);
        MissionLimiter = new RateLimiter(AnthillRuntime.RateLimitMissionWindow, AnthillRuntime.RateLimitMissionMax);
        AuthLimiter = new RateLimiter(AnthillRuntime.RateLimitAuthWindow, AnthillRuntime.RateLimitAuthMax);
        UiHtml = LoadUi();
        InitHomelab(); // v1.9.0 homelab foundation (read-only; see Homelab/ApiHost.Homelab.cs)

        var app = builder.Build();

        // Outermost safety net: turn any unhandled exception (including a response-serialization
        // failure during result execution) into a valid JSON 500 instead of an empty-body 500.
        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[unhandled] {ctx.Request.Method} {ctx.Request.Path}: {ex}");
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.Clear();
                    ctx.Response.StatusCode = 500;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(
                        "{\"success\":false,\"message\":\"Internal server error.\",\"error\":\"internal_error\",\"data\":null}");
                }
            }
        });

        // Security headers on every response.
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Frame-Options"] = "DENY";
            h["X-Content-Type-Options"] = "nosniff";
            h["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; img-src 'self' data:";
            h["Referrer-Policy"] = "no-referrer";
            await next();
        });

        MapEndpoints(app);
        MapHomelabEndpoints(app);
        AssertNoDuplicateRoutes(app);

        Console.WriteLine($"ANTHILL v{AnthillRuntime.Version} API listening on http://{AnthillRuntime.ApiHost}:{AnthillRuntime.ApiPort}");
        if (NetworkUtil.IsWildcardBindHost(AnthillRuntime.ApiHost))
        {
            var lanIp = NetworkUtil.GetLikelyLanIPv4();
            Console.WriteLine(lanIp is not null
                ? $"Open the colony console at http://{lanIp}:{AnthillRuntime.ApiPort}/ui  (or http://localhost:{AnthillRuntime.ApiPort}/ui on this machine)"
                : $"Open the colony console at http://localhost:{AnthillRuntime.ApiPort}/ui");
            Console.WriteLine("Listening on all network interfaces — protected by the operator login, not network isolation.");
        }
        else
        {
            Console.WriteLine($"Open the colony console at http://{AnthillRuntime.ApiHost}:{AnthillRuntime.ApiPort}/ui");
        }

        if (autostart)
        {
            if (Director.Start()) Console.WriteLine("Autonomous Colony Director started (--autonomous).");
            else Console.Error.WriteLine("--autonomous ignored: set autonomy_enabled=true in config to start the Director.");
        }

        app.Run();
        return 0;
    }

    /// <summary>
    /// Boot-time guard: two endpoints sharing an identical method+template throw
    /// <c>AmbiguousMatchException</c> during routing on every matching request — before any handler
    /// or middleware runs — which surfaces as an uncatchable empty HTTP 500 (the Patch Center bug:
    /// a legacy <c>ProtectedText("/patches")</c> collided with the structured <c>GET /patches</c>).
    /// Fail loudly at startup instead of silently at request time.
    /// </summary>
    private static void AssertNoDuplicateRoutes(WebApplication app)
    {
        if (app.Services.GetService(typeof(Microsoft.AspNetCore.Routing.EndpointDataSource))
            is not Microsoft.AspNetCore.Routing.EndpointDataSource source) return;
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var ep in source.Endpoints)
        {
            if (ep is not Microsoft.AspNetCore.Routing.RouteEndpoint re) continue;
            var methods = re.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods
                          ?? new[] { "*" };
            var template = re.RoutePattern.RawText ?? "";
            foreach (var m in methods)
            {
                var key = $"{m} {template}";
                seen[key] = seen.GetValueOrDefault(key) + 1;
            }
        }
        var dupes = seen.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
        if (dupes.Count > 0)
            throw new InvalidOperationException(
                "Duplicate route registrations (would throw AmbiguousMatchException at request time): "
                + string.Join(", ", dupes));
    }

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

        app.MapGet("/health", () => ApiJson.Ok(new Dictionary<string, object?>
        {
            ["status"] = "ok", ["version"] = AnthillRuntime.Version,
            ["native_kernel"] = Anthill.Core.Native.NativeKernel.UsingNative ? "active" : "managed-fallback",
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
                .Select(m => new Dictionary<string, object?>
                {
                    ["id"] = m.GetValueOrDefault("id"), ["goal"] = m.GetValueOrDefault("goal"),
                    ["status"] = m.GetValueOrDefault("status"), ["success_score"] = m.GetValueOrDefault("success_score"),
                    ["created_at"] = m.GetValueOrDefault("created_at"), ["saved_at"] = m.GetValueOrDefault("saved_at"),
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
                ["worker_telemetry"] = Queen.Memory.SummarizeWorkerTelemetry(),
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
            var job = Jobs.GetJob(id);
            return job is null ? ApiJson.Error($"No job found with id: {id}", "not_found") : ApiJson.Ok(job.ToDict());
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
            return ApiJson.Ok(Jobs.Submit(goal).ToDict(), "Mission queued.");
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
                var constraints = MissionConstraints.Parse(goal);
                var tasks = Queen.PlanPreview(goal);
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
                    // Dependencies rendered as human 1-based step numbers (task ids are GUIDs).
                    ["depends_on"] = t.DependsOn.Select(d => indexById.GetValueOrDefault(d, 0)).Where(n => n > 0).ToList(),
                }).ToList();
                return ApiJson.Ok(new Dictionary<string, object?>
                {
                    ["goal"] = goal,
                    ["task_count"] = tasks.Count,
                    ["spec_ingestion"] = Planner.IsLongInput(goal),
                    ["has_coder_task"] = tasks.Any(t => t.AssignedAnt == "coder"),
                    // v1.8.22: worker path the plan resolves to, plus any capability warnings.
                    ["selected_path"] = tasks.Select(t => t.AssignedWorker ?? t.AssignedAnt).ToList(),
                    ["constraint_warnings"] = tasks
                        .Select(t => AntRegistry.ValidateTask(t, constraints))
                        .Where(r => !r.Allowed)
                        .Select(r => r.Reason)
                        .Distinct()
                        .ToList(),
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

        // ---- Patch Center 2.0 (v1.8.24): operator actions by PATCH id ----
        // Approve/reject pending patches that have no approval record (the record is created
        // first, then the normal approve/reject transition runs — never a direct status write).
        app.MapPost("/patches/{id}/approve", (HttpContext ctx, string id) =>
            RequireAuth(ctx, "approve") ?? Results.Text(Queen.ApprovePatchDirect(id, CurrentUsername(ctx) ?? "operator"), "text/plain"));
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
    }

    // ---- Authentication + operator accounts ----
    private static void MapAuthEndpoints(WebApplication app)
    {
        // Public: tells the UI whether to show first-run setup or the login screen.
        app.MapGet("/auth/status", () => ApiJson.Ok(new Dictionary<string, object?>
        {
            ["setup_required"] = Queen.Memory.CountUsers() == 0,
            ["auth_enabled"] = AnthillRuntime.EnableApiAuth,
            ["user_count"] = Queen.Memory.CountUsers(),
        }));

        // Public, first-run only: create the initial administrator and log them straight in.
        app.MapPost("/auth/setup", async (HttpContext ctx) =>
        {
            if (Queen.Memory.CountUsers() > 0)
                return ApiJson.Error("Setup already complete. An administrator already exists.", "bad_request");
            if (!AuthLimiter_TryConsume(ctx)) return ApiJson.Error("Too many attempts. Try again later.", "rate_limited");
            LoginRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<LoginRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var username = string.IsNullOrWhiteSpace(body?.Username) ? "admin" : body!.Username!.Trim();
            var err = Queen.Memory.CreateUser(username, body?.Password ?? "", UserRoles.Admin);
            if (err.Length > 0) return ApiJson.Error(err, "bad_request");
            var token = AuthSessions.Issue(SqliteMemory.NormalizeUsername(username), UserRoles.Admin);
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["token"] = token, ["username"] = SqliteMemory.NormalizeUsername(username), ["role"] = UserRoles.Admin,
            }, "Administrator created. You are now signed in.");
        });

        // Public, rate-limited: username + password -> session token.
        app.MapPost("/auth/login", async (HttpContext ctx) =>
        {
            var ip = ClientIp(ctx);
            if (AuthLimiter.IsLimited(ip)) return ApiJson.Error("Too many failed logins. Try again later.", "rate_limited");
            LoginRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<LoginRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var ok = Queen.Memory.VerifyLogin(body?.Username ?? "", body?.Password ?? "");
            if (ok is null)
            {
                AuthLimiter.RecordAttempt(ip);
                return ApiJson.Error("Invalid username or password.", "unauthorized");
            }
            AuthLimiter.Clear(ip);
            var role = ok.GetValueOrDefault("role") as string ?? UserRoles.Coordinator;
            var username = ok.GetValueOrDefault("username") as string ?? "";
            var token = AuthSessions.Issue(username, role);
            return ApiJson.Ok(new Dictionary<string, object?> { ["token"] = token, ["username"] = username, ["role"] = role }, "Signed in.");
        });

        app.MapPost("/auth/logout", (HttpContext ctx) =>
        {
            AuthSessions.Revoke(ExtractToken(ctx));
            return ApiJson.Ok(new Dictionary<string, object?> { ["ok"] = true }, "Signed out.");
        });

        app.MapGet("/auth/me", (HttpContext ctx) =>
        {
            var id = ResolveIdentity(ctx);
            return id is null
                ? ApiJson.Error("Unauthorized.", "unauthorized")
                : ApiJson.Ok(new Dictionary<string, object?> { ["username"] = id.Username, ["role"] = id.Role });
        });

        // ---- User management (admin-only via the role layer) ----
        app.MapGet("/users", (HttpContext ctx) =>
            RequireAuth(ctx, "manage_users") ?? ApiJson.Ok(Queen.Memory.ListUsers().Select(UserDict).ToList()));

        app.MapPost("/users", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_users"); if (auth is not null) return auth;
            UserRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<UserRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var err = Queen.Memory.CreateUser(body?.Username ?? "", body?.Password ?? "", body?.Role ?? UserRoles.Coordinator);
            if (err.Length > 0) return ApiJson.Error(err, "bad_request");
            return ApiJson.Ok(UserDict(Queen.Memory.GetUser(body!.Username!)!), "User created.");
        });

        app.MapPatch("/users/{username}", async (HttpContext ctx, string username) =>
        {
            var auth = RequireAuth(ctx, "manage_users"); if (auth is not null) return auth;
            if (Queen.Memory.GetUser(username) is null) return ApiJson.Error($"No user found: {username}", "not_found");
            UserPatch? body;
            try { body = await ctx.Request.ReadFromJsonAsync<UserPatch>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var norm = SqliteMemory.NormalizeUsername(username);
            if (!string.IsNullOrEmpty(body?.Password))
            {
                var e = Queen.Memory.SetUserPassword(norm, body.Password); if (e.Length > 0) return ApiJson.Error(e, "bad_request");
                AuthSessions.RevokeUser(norm); // force re-login with the new password
            }
            if (!string.IsNullOrEmpty(body?.Role))
            {
                var e = Queen.Memory.SetUserRole(norm, body.Role); if (e.Length > 0) return ApiJson.Error(e, "bad_request");
                AuthSessions.RevokeUser(norm); // new permissions take effect on next login
            }
            if (body?.Active is bool active)
            {
                var e = Queen.Memory.SetUserActive(norm, active); if (e.Length > 0) return ApiJson.Error(e, "bad_request");
                if (!active) AuthSessions.RevokeUser(norm);
            }
            return ApiJson.Ok(UserDict(Queen.Memory.GetUser(norm)!), "User updated.");
        });

        app.MapDelete("/users/{username}", (HttpContext ctx, string username) =>
        {
            var auth = RequireAuth(ctx, "manage_users"); if (auth is not null) return auth;
            var norm = SqliteMemory.NormalizeUsername(username);
            var me = ResolveIdentity(ctx);
            if (me is not null && string.Equals(me.Username, norm, StringComparison.OrdinalIgnoreCase))
                return ApiJson.Error("You cannot delete your own account while signed in.", "bad_request");
            var err = Queen.Memory.DeleteUser(norm);
            if (err.Length > 0) return ApiJson.Error(err, "bad_request");
            AuthSessions.RevokeUser(norm);
            return ApiJson.Ok(new Dictionary<string, object?> { ["username"] = norm }, "User removed.");
        });
    }

    private static Dictionary<string, object?> UserDict(Dictionary<string, object?> row) => new()
    {
        ["username"] = row.GetValueOrDefault("username"),
        ["role"] = row.GetValueOrDefault("role"),
        ["active"] = Convert.ToInt64(row.GetValueOrDefault("active") ?? 0L) == 1,
        ["created_at"] = row.GetValueOrDefault("created_at"),
        ["last_login_at"] = row.GetValueOrDefault("last_login_at"),
    };

    /// <summary>Consumes one auth-limiter slot for an unauthenticated, abuse-prone endpoint.</summary>
    private static bool AuthLimiter_TryConsume(HttpContext ctx)
    {
        var ip = ClientIp(ctx);
        if (AuthLimiter.IsLimited(ip)) return false;
        AuthLimiter.RecordAttempt(ip);
        return true;
    }

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
        app.MapGet("/ui/state", (HttpContext ctx) =>
            RequireAuth(ctx, "read_ui_state") ?? ApiJson.Ok(UiStateStore.Load()));

        app.MapPut("/ui/state", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_ui_state"); if (auth is not null) return auth;
            System.Text.Json.JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<System.Text.Json.JsonElement>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            return ApiJson.Ok(UiStateStore.Save(body), "Console layout saved.");
        });

        // ---- Operator shell console (Configuration → Shell) — admin only ----
        app.MapGet("/shell/info", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "operator_shell"); if (auth is not null) return auth;
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["enabled"] = AnthillRuntime.EnableOperatorShell,
                ["default_dir"] = OperatorShell.DefaultWorkingDir(),
                ["timeout_seconds"] = OperatorShell.TimeoutSeconds,
                ["host"] = Environment.MachineName,
                ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
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
        app.MapGet("/pheromones/json", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_pheromones"); if (auth is not null) return auth;
            int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var limit);
            return ApiJson.Ok(Queen.Memory.ListPheromoneTrails(Math.Clamp(limit <= 0 ? 300 : limit, 1, 2000)));
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
                AnthillRuntime.ModelRouting.TryGetValue(role, out var cfg);
                routes[role] = new Dictionary<string, object?>
                {
                    ["provider"] = cfg?.GetValueOrDefault("provider") ?? AnthillRuntime.DefaultModelProvider,
                    ["model"] = cfg?.GetValueOrDefault("model") ?? AnthillRuntime.OllamaModel,
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

    // ---- Model provider connections (API keys for OpenAI/Anthropic/Perplexity/OpenRouter/...) ----
    private static void MapProviderEndpoints(WebApplication app)
    {
        // Static catalog metadata: which providers exist, whether they need a key, curated model
        // lists, and where to go get a key. No secrets here — safe to read with read_providers.
        app.MapGet("/providers/catalog", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_providers"); if (auth is not null) return auth;
            var catalog = ProviderCatalog.All.Select(p => new Dictionary<string, object?>
            {
                ["provider"] = p.Id, ["name"] = p.Name, ["kind"] = p.Kind, ["description"] = p.Description,
                ["requires_key"] = p.RequiresKey, ["default_endpoint"] = p.DefaultEndpoint,
                ["key_help_url"] = p.KeyHelpUrl, ["default_model"] = p.DefaultModel, ["models"] = p.Models,
            }).ToList();
            return ApiJson.Ok(catalog);
        });

        // Secret-free connection status for every keyed provider (configured or not).
        app.MapGet("/providers", (HttpContext ctx) =>
            RequireAuth(ctx, "read_providers") ?? ApiJson.Ok(Queen.Memory.ListProviderConnections()));

        // Add or update a connection. api_key is optional on update (blank = leave the stored key
        // untouched); required the first time a provider is connected.
        app.MapPost("/providers", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_providers"); if (auth is not null) return auth;
            ProviderUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ProviderUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.Provider)) return ApiJson.Error("Provider is required.", "bad_request");

            var err = Queen.Memory.UpsertProviderCredential(
                body!.Provider!, body.ApiKey, body.BaseUrl, body.Enabled ?? true, body.Label);
            if (err.Length > 0) return ApiJson.Error(err, "bad_request");
            return ApiJson.Ok(Queen.Memory.ListProviderConnections(), $"Saved {SqliteMemory.NormalizeProvider(body.Provider)} connection.");
        });

        app.MapDelete("/providers/{provider}", (HttpContext ctx, string provider) =>
        {
            var auth = RequireAuth(ctx, "manage_providers"); if (auth is not null) return auth;
            Queen.Memory.DeleteProviderCredential(provider);
            return ApiJson.Ok(Queen.Memory.ListProviderConnections(), $"Removed {SqliteMemory.NormalizeProvider(provider)} connection.");
        });

        // Fires one small live request through the real routing path (ModelRouter) to confirm the
        // stored key actually works, and records the outcome for the console to display.
        app.MapPost("/providers/{provider}/test", (HttpContext ctx, string provider) =>
        {
            var auth = RequireAuth(ctx, "manage_providers"); if (auth is not null) return auth;
            var p = SqliteMemory.NormalizeProvider(provider);
            if (!ProviderCatalog.KeyedProviders.Contains(p))
                return ApiJson.Error($"Unknown provider '{p}'.", "bad_request");
            if (Queen.Router is null)
                return ApiJson.Error("Model routing is disabled for this colony.", "bad_request");

            var client = Queen.Router.GetClientForProvider(p);
            var reply = client.Generate("Reply with the single word: OK", retries: 1);
            var ok = !reply.StartsWith("ERROR:", StringComparison.Ordinal);
            Queen.Memory.SetProviderVerification(p, ok, reply);
            return ok
                ? ApiJson.Ok(Queen.Memory.ListProviderConnections(), $"{p} connection verified.")
                : ApiJson.Error(reply, "provider_test_failed");
        });
    }

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
            Director.Start();
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
            var o = new Objective
            {
                Title = title, Charter = charter,
                Priority = body!.Priority ?? 0, MaxRuns = Math.Max(0, body.MaxRuns ?? 0),
            };
            Queen.Memory.SaveObjective(o);
            return ApiJson.Ok(ObjectiveDict(o), "Objective added to the backlog.");
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

    /// <summary>
    /// Everything the top-right header needs in one call: version, a live Ollama reachability
    /// probe (so "online" means the model backend, not just the API), the active default model,
    /// and a local-vs-providers breakdown of the model routes.
    /// </summary>
    private static Dictionary<string, object?> SystemSummary()
    {
        // Per-role routing: how many roles run on local Ollama vs a cloud provider.
        var routes = AnthillRuntime.ModelRouting;
        var providerRoles = new List<string>();
        var localRoles = new List<string>();
        foreach (var (role, cfg) in routes)
        {
            var provider = cfg.GetValueOrDefault("provider") ?? AnthillRuntime.DefaultModelProvider;
            if (string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase)) localRoles.Add(role);
            else providerRoles.Add(role);
        }
        var routeList = routes.Select(kv => new Dictionary<string, object?>
        {
            ["role"] = kv.Key,
            ["provider"] = kv.Value.GetValueOrDefault("provider") ?? AnthillRuntime.DefaultModelProvider,
            ["model"] = kv.Value.GetValueOrDefault("model"),
        }).ToList();

        // Live Ollama probe (cheap GET /api/version). Only meaningful when Ollama is in use.
        bool? ollamaReachable = null;
        if (AnthillRuntime.UseOllama)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var resp = InternalHttp.GetAsync($"{AnthillRuntime.OllamaHost.TrimEnd('/')}/api/version", cts.Token)
                    .GetAwaiter().GetResult();
                ollamaReachable = resp.IsSuccessStatusCode;
            }
            catch { ollamaReachable = false; }
        }

        var providersConfigured = Queen.Memory.ListProviderConnections()
            .Count(p => p.GetValueOrDefault("configured") as bool? == true);

        return new Dictionary<string, object?>
        {
            ["version"] = AnthillRuntime.Version,
            ["native_kernel"] = Anthill.Core.Native.NativeKernel.UsingNative ? "active" : "managed-fallback",
            ["safety_profile"] = AnthillRuntime.Config.SafetyProfile,
            ["api_host"] = AnthillRuntime.ApiHost,
            ["use_ollama"] = AnthillRuntime.UseOllama,
            ["ollama_host"] = AnthillRuntime.OllamaHost,
            ["ollama_reachable"] = ollamaReachable,
            ["default_model"] = AnthillRuntime.OllamaModel,
            ["routing_mode"] = providerRoles.Count == 0 ? "local" : (localRoles.Count == 0 ? "providers" : "mixed"),
            ["local_role_count"] = localRoles.Count,
            ["provider_role_count"] = providerRoles.Count,
            ["providers_configured"] = providersConfigured,
            ["routes"] = routeList,
        };
    }

    // ---- mission report -----------------------------------------------------

    /// <summary>
    /// Assembles the structured mission report for /missions/{id}/report: mission-level outcome
    /// and final output, per-task readable results (coder JSON translated to plain English),
    /// tangible changes (patch proposals + approval state), and problems (failures, timeouts,
    /// unparseable proposals) — everything the console needs to show what actually happened.
    /// </summary>
    /// <summary>True when the authenticated caller's role permits the named permission (and it's enabled).</summary>
    private static bool CallerHas(HttpContext ctx, string permission)
    {
        if (!AnthillRuntime.EnableApiAuth) return true;
        var identity = ResolveIdentity(ctx);
        return identity is not null && UserRoles.RoleAllows(identity.Role, permission) && ApiPermissionAllowed(permission);
    }

    private static IResult MissionReport(string id, bool includeSensitive)
    {
        var mission = Queen.Memory.GetMission(id);
        if (mission is null) return ApiJson.Error($"No mission found with id: {id}", "not_found");

        var tasks = Queen.Memory.GetTasksForMission(id);
        // Patches/approvals/objectives are admin-only surfaces — skip the queries entirely for
        // non-admin callers so nothing sensitive is even assembled.
        var patches = includeSensitive ? Queen.Memory.ListPatchProposalsForMission(id) : new List<Dictionary<string, object?>>();
        var approvals = includeSensitive ? Queen.Memory.ListApprovalRequestsForMission(id) : new List<Dictionary<string, object?>>();
        var approvalByTarget = approvals
            .GroupBy(a => a.GetValueOrDefault("target_id")?.ToString() ?? "")
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.GetValueOrDefault("created_at")?.ToString()).First());

        // Problem events for this mission, translated for humans. patch_proposal_parse_failed is
        // the big silent one: the coder did work, but its proposal never reached the approval
        // queue — from the outside it looks like "nothing happened".
        var problemTypes = new HashSet<string>
        {
            "task_failed", "task_blocked", "task_skipped_dependency", "mission_failed",
            "patch_proposal_parse_failed", "autonomy_error",
            // v1.8.21: auto-apply failures the operator needs to see ("why didn't it save?").
            "autonomy_autoapply_reverted", "autonomy_autoapply_apply_failed", "autonomy_autoapply_git_failed",
        };
        var missionEvents = Queen.Memory.GetRecentEvents(400, null, id);
        var problems = missionEvents
            .Where(e => problemTypes.Contains(e.GetValueOrDefault("event_type")?.ToString() ?? ""))
            .Select(e => new Dictionary<string, object?>
            {
                ["type"] = e.GetValueOrDefault("event_type"),
                ["message"] = e.GetValueOrDefault("message"),
                ["task_id"] = e.GetValueOrDefault("task_id"),
                ["at"] = e.GetValueOrDefault("created_at"),
            })
            .ToList();

        // v1.8.21: the latest gated auto-apply outcome for this mission, surfaced so the operator can
        // see whether auto-applied changes were kept, kept-unverified, reverted, or skipped — and why.
        Dictionary<string, object?>? autoApply = null;
        if (includeSensitive)
        {
            var aa = missionEvents.FirstOrDefault(e =>
                (e.GetValueOrDefault("event_type")?.ToString() ?? "").StartsWith("autonomy_autoapply_"));
            if (aa is not null)
            {
                var t = aa.GetValueOrDefault("event_type")?.ToString() ?? "";
                autoApply = new Dictionary<string, object?>
                {
                    ["type"] = t,
                    ["outcome"] = t.Replace("autonomy_autoapply_", ""),
                    ["kept"] = t is "autonomy_autoapply_verified" or "autonomy_autoapply_kept_unverified",
                    ["message"] = aa.GetValueOrDefault("message"),
                    ["at"] = aa.GetValueOrDefault("created_at"),
                };
            }
        }

        var taskReports = tasks.Select(t =>
        {
            var ant = t.GetValueOrDefault("assigned_ant")?.ToString() ?? "";
            var result = t.GetValueOrDefault("result")?.ToString() ?? "";
            return new Dictionary<string, object?>
            {
                ["id"] = t.GetValueOrDefault("id"),
                ["title"] = t.GetValueOrDefault("title"),
                ["ant"] = ant,
                ["task_type"] = t.GetValueOrDefault("task_type"),
                ["status"] = t.GetValueOrDefault("status"),
                ["elapsed_seconds"] = t.GetValueOrDefault("elapsed_seconds"),
                ["readable_output"] = ReadableTaskOutput(ant, result),
                ["failure_reason"] = t.GetValueOrDefault("failure_reason"),
                ["skipped_reason"] = t.GetValueOrDefault("skipped_reason"),
                ["blocked_reason"] = t.GetValueOrDefault("blocked_reason"),
            };
        }).ToList();

        var patchReports = patches.Select(p =>
        {
            var patchId = p.GetValueOrDefault("id")?.ToString() ?? "";
            var approval = approvalByTarget.GetValueOrDefault(patchId);
            return new Dictionary<string, object?>
            {
                ["id"] = patchId,
                ["file_path"] = p.GetValueOrDefault("file_path"),
                ["change_type"] = p.GetValueOrDefault("change_type"),
                ["reason"] = p.GetValueOrDefault("reason"),
                ["risk"] = p.GetValueOrDefault("risk"),
                ["status"] = p.GetValueOrDefault("status"),
                ["applied_at"] = p.GetValueOrDefault("applied_at"),
                ["last_error"] = p.GetValueOrDefault("last_error"),
                ["approval_id"] = approval?.GetValueOrDefault("id"),
                ["approval_status"] = approval?.GetValueOrDefault("status"),
            };
        }).ToList();

        // Autonomy linkage (admin-only surface): which objective drove this mission, and which it
        // created. Only assembled for callers who can read objectives directly.
        var run = includeSensitive ? Queen.Memory.GetAutonomyRunForMission(id) : null;
        var runObjective = run?.GetValueOrDefault("objective_id")?.ToString() is { Length: > 0 } oid
            ? Queen.Memory.GetObjective(oid) : null;
        var createdObjectives = includeSensitive
            ? Queen.Memory.ListObjectivesCreatedByMission(id)
                .Select(o => new Dictionary<string, object?>
                {
                    ["id"] = o.Id, ["title"] = o.Title, ["charter"] = o.Charter,
                    ["priority"] = o.Priority, ["status"] = o.Status.Value(),
                }).ToList()
            : new List<Dictionary<string, object?>>();

        var statuses = tasks.Select(t => t.GetValueOrDefault("status")?.ToString() ?? "").ToList();
        return ApiJson.Ok(new Dictionary<string, object?>
        {
            ["autonomy_run"] = run is null ? null : new Dictionary<string, object?>
            {
                ["run_id"] = run.GetValueOrDefault("id"),
                ["objective_id"] = run.GetValueOrDefault("objective_id"),
                ["objective_title"] = runObjective?.Title,
                ["generated_goal"] = run.GetValueOrDefault("generated_goal"),
                ["follow_ups_created"] = run.GetValueOrDefault("follow_ups_created"),
            },
            ["created_objectives"] = createdObjectives,
            ["id"] = id,
            ["goal"] = mission.GetValueOrDefault("goal"),
            ["status"] = mission.GetValueOrDefault("status"),
            ["success_score"] = mission.GetValueOrDefault("success_score"),
            ["created_at"] = mission.GetValueOrDefault("created_at"),
            ["completed_at"] = mission.GetValueOrDefault("completed_at"),
            ["final_output"] = mission.GetValueOrDefault("user_result"),
            ["task_counts"] = new Dictionary<string, object?>
            {
                ["total"] = statuses.Count,
                ["complete"] = statuses.Count(s => s == "complete"),
                ["failed"] = statuses.Count(s => s == "failed"),
                ["skipped"] = statuses.Count(s => s == "skipped"),
            },
            ["tasks"] = taskReports,
            ["patches"] = patchReports,
            // v1.8.16: rollup of patch activity for this mission (proposed/approved/applied/rejected/failed).
            ["patch_counts"] = includeSensitive ? Queen.Memory.PatchCountsForMission(id) : null,
            ["pending_approvals"] = approvals.Count(a => a.GetValueOrDefault("status")?.ToString() == "pending"),
            ["sources_saved"] = Queen.Memory.CountSourcesForMission(id),
            ["auto_apply"] = autoApply,
            ["problems"] = problems,
        });
    }

    /// <summary>
    /// Turns a task's raw result into readable English. Coder results are structured JSON patch
    /// sets — rendered as "Proposed change to <file>: <reason>" lines instead of raw JSON. Other
    /// ants already produce prose; it is passed through (bounded) as-is.
    /// </summary>
    internal static string ReadableTaskOutput(string ant, string result)
    {
        if (string.IsNullOrWhiteSpace(result)) return "";
        if (ant == "coder")
        {
            try
            {
                var parsed = Json.ExtractJsonObject(result);
                var summary = parsed["summary"]?.GetValue<string>()?.Trim() ?? "";
                var lines = new List<string>();
                if (summary.Length > 0) lines.Add(summary);
                if (parsed["proposals"] is System.Text.Json.Nodes.JsonArray proposals)
                {
                    if (proposals.Count == 0)
                        lines.Add("No file changes were proposed.");
                    foreach (var item in proposals)
                    {
                        if (item is not System.Text.Json.Nodes.JsonObject o) continue;
                        var file = o["file_path"]?.GetValue<string>() ?? "?";
                        var change = o["change_type"]?.GetValue<string>() ?? "modify";
                        var reason = o["reason"]?.GetValue<string>() ?? "";
                        lines.Add($"Proposed {change} to {file}: {reason}");
                    }
                }
                if (lines.Count > 0) return string.Join("\n", lines);
            }
            catch { /* not parseable as a patch set — fall through to raw text */ }
        }
        return TextUtil.Truncate(result, 4000, "\n...[output truncated — full text in the mission detail]");
    }

    // ---- Patch Center helpers (v1.8.16) ------------------------------------

    private static readonly Dictionary<string, string> PatchStatusLabels = new()
    {
        ["proposed"] = "Pending", ["approved"] = "Approved", ["applied"] = "Applied",
        ["rejected"] = "Rejected", ["failed"] = "Failed", ["superseded"] = "Superseded",
    };

    private static PatchStatus? ParsePatchStatusOrNull(string s) => s switch
    {
        "proposed" => PatchStatus.Proposed, "approved" => PatchStatus.Approved, "rejected" => PatchStatus.Rejected,
        "applied" => PatchStatus.Applied, "failed" => PatchStatus.Failed, "superseded" => PatchStatus.Superseded, _ => null,
    };

    /// <summary>Shapes one Patch Center list row: normalizes risk, adds a status label, no content body.</summary>
    private static Dictionary<string, object?> PatchCenterRow(Dictionary<string, object?> p)
    {
        var status = p.GetValueOrDefault("status")?.ToString() ?? "proposed";
        var riskRaw = p.GetValueOrDefault("risk")?.ToString() ?? "";
        return new Dictionary<string, object?>
        {
            ["id"] = p.GetValueOrDefault("id"),
            ["file_path"] = p.GetValueOrDefault("file_path"),
            ["change_type"] = p.GetValueOrDefault("change_type"),
            ["risk"] = RiskLevel.Normalize(riskRaw),
            ["risk_raw"] = riskRaw,
            ["reason"] = p.GetValueOrDefault("reason"),
            ["status"] = status,
            ["status_label"] = PatchStatusLabels.GetValueOrDefault(status, status),
            ["mission_id"] = p.GetValueOrDefault("mission_id"),
            ["mission_goal"] = p.GetValueOrDefault("mission_goal"),
            ["objective_id"] = p.GetValueOrDefault("objective_id"),
            ["run_id"] = p.GetValueOrDefault("run_id"),
            ["task_id"] = p.GetValueOrDefault("task_id"),
            ["patch_set_id"] = p.GetValueOrDefault("patch_set_id"),
            ["patch_set_summary"] = p.GetValueOrDefault("patch_set_summary"),
            ["created_at"] = p.GetValueOrDefault("created_at"),
            ["applied_at"] = p.GetValueOrDefault("applied_at"),
            ["last_error"] = p.GetValueOrDefault("last_error"),
            ["has_backup"] = !string.IsNullOrEmpty(p.GetValueOrDefault("backup_path")?.ToString()),
            ["approval_id"] = p.GetValueOrDefault("approval_id"),
            ["approval_status"] = p.GetValueOrDefault("approval_status"),
        };
    }

    /// <summary>Full JSON detail for one patch (Patch Center diff view): metadata + old/new content + approval.</summary>
    private static IResult PatchDetailJson(string patchId)
    {
        var p = Queen.Memory.GetPatchProposal(patchId);
        if (p is null) return ApiJson.Error($"No patch found with id: {patchId}", "not_found");
        var missionId = p.GetValueOrDefault("mission_id")?.ToString() ?? "";
        var approval = Queen.Memory.GetApprovalForTarget(patchId);
        var run = string.IsNullOrEmpty(missionId) ? null : Queen.Memory.GetAutonomyRunForMission(missionId);
        var objectiveId = run?.GetValueOrDefault("objective_id")?.ToString();
        var objective = string.IsNullOrEmpty(objectiveId) ? null : Queen.Memory.GetObjective(objectiveId!);
        var status = p.GetValueOrDefault("status")?.ToString() ?? "proposed";
        var riskRaw = p.GetValueOrDefault("risk")?.ToString() ?? "";
        return ApiJson.Ok(new Dictionary<string, object?>
        {
            ["id"] = patchId,
            ["file_path"] = p.GetValueOrDefault("file_path"),
            ["change_type"] = p.GetValueOrDefault("change_type"),
            ["risk"] = RiskLevel.Normalize(riskRaw),
            ["risk_raw"] = riskRaw,
            ["reason"] = p.GetValueOrDefault("reason"),
            ["status"] = status,
            ["status_label"] = PatchStatusLabels.GetValueOrDefault(status, status),
            ["old_content"] = p.GetValueOrDefault("old_content"),
            ["new_content"] = p.GetValueOrDefault("new_content"),
            ["mission_id"] = missionId,
            ["mission_goal"] = p.GetValueOrDefault("mission_goal"),
            ["task_id"] = p.GetValueOrDefault("task_id"),
            ["patch_set_summary"] = p.GetValueOrDefault("patch_set_summary"),
            ["objective_id"] = objectiveId,
            ["objective_title"] = objective?.Title,
            ["run_id"] = run?.GetValueOrDefault("id"),
            ["created_at"] = p.GetValueOrDefault("created_at"),
            ["applied_at"] = p.GetValueOrDefault("applied_at"),
            ["last_error"] = p.GetValueOrDefault("last_error"),
            ["has_backup"] = !string.IsNullOrEmpty(p.GetValueOrDefault("backup_path")?.ToString()),
            ["approval_id"] = approval?.GetValueOrDefault("id"),
            ["approval_status"] = approval?.GetValueOrDefault("status"),
        });
    }

    private static Dictionary<string, object?> ObjectiveDict(Objective o) => new()
    {
        ["id"] = o.Id, ["title"] = o.Title, ["charter"] = o.Charter, ["priority"] = o.Priority,
        ["status"] = o.Status.Value(), ["max_runs"] = o.MaxRuns, ["run_count"] = o.RunCount,
        ["consecutive_failures"] = o.ConsecutiveFailures, ["parent_objective_id"] = o.ParentObjectiveId,
        ["created_at"] = o.CreatedAt.ToIso(), ["last_run_at"] = o.LastRunAt.ToIsoOrNull(),
        ["success_ema"] = o.SuccessEma,
        // Retirement markers (stamped by the Director). Looping-retired objectives are shown in the
        // console's "Completed Objectives" box and filtered out of the active/paused backlog list.
        ["retired_code"] = o.Metadata.GetValueOrDefault("retired_code"),
        ["retired_reason"] = o.Metadata.GetValueOrDefault("retired_reason"),
        ["retired_at"] = o.Metadata.GetValueOrDefault("retired_at"),
        // v1.8.16 unified lifecycle end markers.
        ["end_reason"] = o.Metadata.GetValueOrDefault("end_reason"),
        ["end_detail"] = o.Metadata.GetValueOrDefault("end_detail"),
        ["ended_at"] = o.Metadata.GetValueOrDefault("ended_at"),
    };

    /// <summary>
    /// Compiles the "Completed Objectives" expanded view for one retired objective: the objective's
    /// own fields plus every autonomy run it produced, the missions those runs launched, and the
    /// tasks within those missions — all from existing models, no new storage.
    /// </summary>
    private static Dictionary<string, object?> CompletedObjectiveDetail(Objective o)
    {
        var runs = Queen.Memory.ListAutonomyRuns(o.Id, limit: 100);
        var missions = new List<Dictionary<string, object?>>();
        var tasks = new List<Dictionary<string, object?>>();
        foreach (var missionId in runs.Select(r => r.GetValueOrDefault("mission_id")?.ToString())
                     .Where(m => !string.IsNullOrEmpty(m)).Distinct())
        {
            var mission = Queen.Memory.GetMission(missionId!);
            if (mission is not null)
                missions.Add(new Dictionary<string, object?>
                {
                    ["id"] = mission.GetValueOrDefault("id"), ["goal"] = mission.GetValueOrDefault("goal"),
                    ["status"] = mission.GetValueOrDefault("status"), ["success_score"] = mission.GetValueOrDefault("success_score"),
                });
            foreach (var t in Queen.Memory.GetTasksForMission(missionId!, 200))
                tasks.Add(new Dictionary<string, object?>
                {
                    ["mission_id"] = missionId, ["title"] = t.GetValueOrDefault("title"),
                    ["ant"] = t.GetValueOrDefault("assigned_ant"), ["status"] = t.GetValueOrDefault("status"),
                    ["worker"] = t.GetValueOrDefault("assigned_worker"),
                    ["path_node"] = t.GetValueOrDefault("assigned_worker") ?? t.GetValueOrDefault("assigned_ant"),
                });
        }
        var endReason = o.Metadata.GetValueOrDefault("end_reason")?.ToString()
            ?? (o.Metadata.GetValueOrDefault("retired_code") is not null ? ObjectiveEndReason.RetiredLooping : null);
        return new Dictionary<string, object?>
        {
            ["id"] = o.Id, ["title"] = o.Title, ["charter"] = o.Charter,
            ["end_reason"] = endReason,
            ["end_reason_label"] = ObjectiveEndReason.Label(endReason),
            ["end_detail"] = o.Metadata.GetValueOrDefault("end_detail") ?? o.Metadata.GetValueOrDefault("retired_reason"),
            ["ended_at"] = o.Metadata.GetValueOrDefault("ended_at") ?? o.Metadata.GetValueOrDefault("retired_at"),
            ["patch_counts"] = Queen.Memory.PatchCountsForObjective(o.Id),
            ["retired_code"] = o.Metadata.GetValueOrDefault("retired_code"),
            ["retired_reason"] = o.Metadata.GetValueOrDefault("retired_reason"),
            ["retired_at"] = o.Metadata.GetValueOrDefault("retired_at"),
            ["run_count"] = o.RunCount, ["last_run_at"] = o.LastRunAt.ToIsoOrNull(),
            ["runs"] = runs.Select(r => new Dictionary<string, object?>
            {
                ["generated_goal"] = r.GetValueOrDefault("generated_goal"), ["mission_status"] = r.GetValueOrDefault("mission_status"),
                ["mission_id"] = r.GetValueOrDefault("mission_id"), ["started_at"] = r.GetValueOrDefault("started_at"),
                ["success_score"] = r.GetValueOrDefault("success_score"),
            }).ToList(),
            ["missions"] = missions,
            ["tasks"] = tasks,
        };
    }

    private static void ProtectedJson(WebApplication app, string path, string permission, Func<HttpContext, IResult> handler) =>
        app.MapGet(path, (HttpContext ctx) => RequireAuth(ctx, permission) ?? handler(ctx));

    private static void ProtectedText(WebApplication app, string path, string permission, Func<string> handler) =>
        app.MapGet(path, (HttpContext ctx) => RequireAuth(ctx, permission) ?? Results.Text(handler(), "text/plain"));

    private static IResult? RequireAuth(HttpContext ctx, string permission)
    {
        var ip = ClientIp(ctx);
        if (AnthillRuntime.EnableApiAuth)
        {
            if (AuthLimiter.IsLimited(ip))
                return ApiJson.Error("Too many failed authentication attempts. Try again later.", "rate_limited");
            var identity = ResolveIdentity(ctx);
            if (identity is null)
            {
                AuthLimiter.RecordAttempt(ip);
                return ApiJson.Error("Unauthorized. Log in to the colony.", "unauthorized");
            }
            AuthLimiter.Clear(ip); // a valid session must not consume the failed-auth budget
            if (!UserRoles.RoleAllows(identity.Role, permission))
                return ApiJson.Error($"Permission denied: your role ({identity.Role}) is not allowed to {permission}.", "permission_denied");
        }
        // Capability gate: the feature must also be enabled at all (independent of who you are).
        if (!ApiPermissionAllowed(permission))
            return ApiJson.Error($"Permission denied: {permission} is disabled.", "permission_denied");
        return null;
    }

    private static bool ApiPermissionAllowed(string permission) => AnthillRuntime.ApiPermissions.GetValueOrDefault(permission, false);

    /// <summary>Human-readable byte size (e.g. "34.0 GB") for maintenance messages.</summary>
    private static string HumanBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }

    /// <summary>
    /// Resolves the caller's identity from their bearer token: first as a login session, then —
    /// for back-compat with scripts/CI — as the optional static ANTHILL_API_TOKEN, which acts as a
    /// programmatic admin when configured. Returns null when neither matches.
    /// </summary>
    private static AuthSession? ResolveIdentity(HttpContext ctx)
    {
        var token = ExtractToken(ctx);
        if (token is null) return null;
        var session = AuthSessions.Resolve(token);
        if (session is not null) return session;
        if (HasStaticToken() && TokenSecurity.ConstantTimeEquals(token, AnthillRuntime.ApiAuthToken))
            return new AuthSession("api-token", UserRoles.Admin, DateTime.UtcNow.AddHours(1));
        return null;
    }

    /// <summary>Acting operator's username for audit trails (v1.8.24 Patch Center actions); null when unauthenticated.</summary>
    private static string? CurrentUsername(HttpContext ctx) => ResolveIdentity(ctx)?.Username;

    /// <summary>True when a strong, non-placeholder static API token is configured for programmatic use.</summary>
    private static bool HasStaticToken() =>
        !string.IsNullOrEmpty(AnthillRuntime.ApiAuthToken)
        && AnthillRuntime.ApiAuthToken != AnthillRuntime.ApiTokenDefaultPlaceholder
        && AnthillRuntime.ApiAuthToken.Length >= AnthillRuntime.ApiTokenMinLength;

    private static string? ExtractToken(HttpContext ctx)
    {
        var direct = ctx.Request.Headers["X-Anthill-Token"].FirstOrDefault();
        if (!string.IsNullOrEmpty(direct)) return direct;
        var authz = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(authz) && authz.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authz["Bearer ".Length..].Trim();
        return null;
    }

    private static string ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string LoadUi()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));
        if (name is null) return "<h1>ANTHILL</h1><p>UI resource missing.</p>";
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class MissionRequest { public string Goal { get; set; } = ""; }
public sealed class LoginRequest { public string? Username { get; set; } public string? Password { get; set; } }
public sealed class UserRequest { public string? Username { get; set; } public string? Password { get; set; } public string? Role { get; set; } }
public sealed class UserPatch { public string? Password { get; set; } public string? Role { get; set; } public bool? Active { get; set; } }
public sealed class RejectBody { public string? Reason { get; set; } }
/// <summary>v1.8.24: operator-edited alternative patch content (Patch Center 2.0).</summary>
public sealed class AlternativePatchBody
{
    [System.Text.Json.Serialization.JsonPropertyName("new_content")] public string? NewContent { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("reason")] public string? Reason { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("supersede_original")] public bool? SupersedeOriginal { get; set; }
}
public sealed class ProviderUpsertRequest
{
    public string? Provider { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("api_key")] public string? ApiKey { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("base_url")] public string? BaseUrl { get; set; }
    public bool? Enabled { get; set; }
    public string? Label { get; set; }
}
public sealed class ObjectiveRequest
{
    public string? Title { get; set; }
    public string? Charter { get; set; }
    public int? Priority { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("max_runs")] public int? MaxRuns { get; set; }
}
public sealed class ObjectivePatch
{
    public string? Status { get; set; }
    public int? Priority { get; set; }
}

/// <summary>Standard JSON response envelopes — {success,message,data} / {success,message,error,data}.</summary>
public static class ApiJson
{
    // AllowNamedFloatingPointLiterals so a stray NaN/Infinity double serializes as "NaN" instead of
    // throwing during response writing (one of the ways an endpoint used to emit an empty 500).
    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static IResult Ok(object? data = null, string message = "ok") =>
        Envelope(new Dictionary<string, object?> { ["success"] = true, ["message"] = TextUtil.SanitizeUtf16(message), ["data"] = SanitizeJson(data) }, 200);

    public static IResult Error(string message, string? error = null, object? data = null) =>
        Envelope(new Dictionary<string, object?> { ["success"] = false, ["message"] = TextUtil.SanitizeUtf16(message), ["error"] = error, ["data"] = SanitizeJson(data) },
            error switch { "unauthorized" => 401, "permission_denied" => 403, "rate_limited" => 429, "not_found" => 404, _ => 400 });

    /// <summary>
    /// Serializes the response envelope to a string HERE — inside our own try/catch — and returns it as
    /// pre-rendered content, instead of handing the object graph to <c>Results.Json</c> which serializes
    /// later during result execution, after the endpoint's own try/catch has already returned. A failure
    /// at that later stage is uncatchable and surfaces as a silent empty HTTP 500 (the recurring Patch
    /// Center bug). By serializing up front we either succeed, or we return a valid JSON error that names
    /// the exception — the operator never sees an empty 500 again.
    /// </summary>
    private static IResult Envelope(Dictionary<string, object?> payload, int statusCode)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(payload, JsonOpts);
            return Results.Content(json, "application/json", System.Text.Encoding.UTF8, statusCode);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ApiJson] response serialization failed ({ex.GetType().Name}): {ex.Message}");
            var safe = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["success"] = false,
                ["message"] = TextUtil.SanitizeUtf16($"Response could not be serialized: {ex.Message}"),
                ["error"] = "serialization_error",
                ["data"] = null,
            }, JsonOpts);
            return Results.Content(safe, "application/json", System.Text.Encoding.UTF8, 500);
        }
    }

    /// <summary>
    /// Recursively replaces invalid UTF-16 (lone surrogates) in every string reachable from the
    /// payload so <see cref="System.Text.Json"/> can never throw "Cannot transcode invalid UTF-16"
    /// while writing the response. That failure happens during result execution — after the endpoint
    /// handler (and its try/catch) has returned — so it would otherwise surface as an uncatchable
    /// empty HTTP 500 (the v1.8.18 Patch Center bug: LLM-generated patch text with lone surrogates).
    /// Dictionaries and lists are rebuilt with sanitized contents; byte[] and other scalars pass
    /// through untouched so base64/number serialization is preserved.
    /// </summary>
    internal static object? SanitizeJson(object? value)
    {
        switch (value)
        {
            case null: return null;
            case string s: return TextUtil.SanitizeUtf16(s);
            case double d when double.IsNaN(d) || double.IsInfinity(d): return null; // STJ throws on non-finite
            case float f when float.IsNaN(f) || float.IsInfinity(f): return null;
            case byte[]: return value; // keep byte[] → base64, don't expand into a number array
            case System.Collections.IDictionary dict:
            {
                var result = new Dictionary<string, object?>(dict.Count);
                foreach (System.Collections.DictionaryEntry entry in dict)
                    result[entry.Key?.ToString() ?? ""] = SanitizeJson(entry.Value);
                return result;
            }
            case System.Collections.IEnumerable seq:
            {
                var list = new List<object?>();
                foreach (var item in seq) list.Add(SanitizeJson(item));
                return list;
            }
            default: return value; // scalars (bool/number/DateTime/etc.) and POCOs pass through
        }
    }
}
