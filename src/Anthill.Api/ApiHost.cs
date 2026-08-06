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
/// Builds and runs the secured ANTHILL API host. Mirrors the FastAPI surface of the Python
/// build: constant-time token auth, failed-auth + mission rate limiting, hardened response
/// headers, no public docs endpoints, permission-gated reads, and the embedded colony UI.
/// </summary>
public static partial class ApiHost
{
    /// <summary>v3.1.0 (ADR-001): the composition root this process is running. One host owns one
    /// colony — one database, one resolved profile, one Queen.</summary>
    public static RuntimeHost Host { get; private set; } = null!;

    /// <summary>The host's Queen. A projection of <see cref="Host"/>, kept as a static because the
    /// endpoint closures read it directly; it is no longer where a Queen is created.</summary>
    public static Queen Queen { get; private set; } = null!;
    public static ApiJobRegistry Jobs { get; private set; } = null!;
    public static ColonyDirector Director { get; private set; } = null!;
    private static RateLimiter MissionLimiter = null!;
    private static RateLimiter AuthLimiter = null!;
    private static string UiHtml = "";
    private static string UiAppJs = "";
    /// <summary>
    /// v2.18.2: how much of a mission answer /missions/json carries inline. The conversation view
    /// reads this directly; anything longer is truncated with a flag, and the untruncated text is
    /// served by /missions/{id}/report.
    /// </summary>
    public const int MissionAnswerPreviewChars = 4000;

    private static string UiMissionThreadJs = "";
    private static string UiGridJs = "";
    private static string UiGridCss = "";
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

        // v3.1.0 (ADR-001): the API host asks the composition root for a colony instead of
        // constructing one itself. Queen stays exposed as a static for the 160-odd endpoint
        // closures that read it — replacing those is churn without benefit — but it is now a
        // projection of a host that CAN be instantiated more than once, rather than the only
        // way a Queen comes into existence.
        Host = RuntimeHost.Create();
        Queen = Host.Queen;
        // Phase 3: the Director multiplexes its concurrent missions through this same worker
        // pool, so size it to whichever is larger — api_job_workers or autonomy_concurrency —
        // ensuring autonomous missions can actually run side by side without starving user jobs.
        var jobWorkers = Math.Max(AnthillRuntime.ApiJobWorkers,
            AnthillRuntime.EnableAutonomy ? AnthillRuntime.AutonomyConcurrency : 1);
        Jobs = new ApiJobRegistry(Queen, jobWorkers);
        Director = new ColonyDirector(Queen, Jobs);
        // v2.26.0: configuration health at startup — incompatible feature combinations degrade
        // LOUDLY (events + console), never silently. Live view at /config/health.
        RuntimeConfigValidator.ReportAtStartup(Queen.Memory);
        foreach (var finding in RuntimeConfigValidator.Validate())
            Console.Error.WriteLine($"[config-health:{finding.Severity}] {finding.Combination}: {finding.Detail}");
        MissionLimiter = new RateLimiter(AnthillRuntime.RateLimitMissionWindow, AnthillRuntime.RateLimitMissionMax);
        AuthLimiter = new RateLimiter(AnthillRuntime.RateLimitAuthWindow, AnthillRuntime.RateLimitAuthMax);
        UiHtml = LoadUi();
        UiAppJs = LoadUiAsset("app.js");
        UiMissionThreadJs = LoadUiAsset("mission-thread.js");
        UiGridJs = LoadUiAsset("dashboard-grid.js");
        UiGridCss = LoadUiAsset("dashboard-grid.css");
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
            // CSP hardening (v2.6.3): closed several vectors that need no markup changes —
            //   base-uri 'self'      : block <base> tag hijacking of relative URLs
            //   object-src 'none'    : no <object>/<embed>/<applet> plugin content
            //   frame-ancestors 'none': clickjacking protection (modern peer of X-Frame-Options)
            //   form-action 'self'   : forms can only post back to this origin
            // script-src is now 'self' ONLY — no 'unsafe-inline'. The console carries zero inline JS:
            // the page script lives in /ui/app.js, and all former inline on*= handlers were converted
            // to data-on* driven by a single delegated dispatcher (see app.js). This blocks inline
            // script injection (the main XSS vector). style-src keeps 'unsafe-inline' — 864 inline
            // style attributes remain and style injection is far lower risk. connect-src is
            // intentionally omitted so the "remote API base URL" feature (browser → a different API
            // host) keeps working.
            h["Content-Security-Policy"] =
                "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; " +
                "form-action 'self'; style-src 'self' 'unsafe-inline'; script-src 'self'; " +
                "img-src 'self' data:";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            await next();
        });

        MapEndpoints(app);
        MapHomelabEndpoints(app);
        MapEventStreamEndpoints(app);   // v3.8.3: SSE — see ApiHost.EventStream.cs
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
            // v2.26.0: autostart honours a durable STOP. The Director process starts (so status,
            // introspection, and the resume endpoint work), but it launches nothing while STOP
            // exists, and starting it no longer clears the sentinel. Only an explicit operator
            // resume (POST /autonomy/start) does that.
            if (Director.Start())
            {
                if (AutonomyControl.IsStopped)
                    Console.WriteLine("Colony Director started (--autonomous), but the STOP sentinel is engaged — "
                        + "no objectives will launch until an operator explicitly resumes (POST /autonomy/start).");
                else Console.WriteLine("Autonomous Colony Director started (--autonomous).");
            }
            else Console.Error.WriteLine("--autonomous ignored: set autonomy_enabled=true in config to start the Director.");
        }

        // Learn what the local models can do BEFORE the first agent run asks. The call path reads
        // this cache and never fetches, so without a warm start the first run would negotiate
        // against the declared table, strip tools from a tool-capable model, and get an answer
        // invented instead of looked up. Backgrounded: a sleeping Ollama must not delay startup.
        _ = ThreadingTask.Run(() =>
        {
            try { OllamaCapabilityCache.Warm(AnthillRuntime.OllamaHost); }
            catch { /* best-effort: the table remains the fallback */ }

            // v3.8.2: fitness is reported HERE, after the warm — never during construction.
            //
            // It used to run in the Queen's constructor, which happens before this task starts, so
            // it judged every route against the declared name table instead of what Ollama reports.
            // On a colony routed to gemma4:31b that produced five wrong warnings on every restart,
            // for a model that reports tools and thinking. Startup stays non-blocking; the report
            // simply waits for data worth reporting.
            try { Queen.ReportModelFitness(); }
            catch { /* a warning that throws would be worse than the mismatch it describes */ }
        });

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
                    ["tier"] = ActivationTiers.Name(AnthillRuntime.ActivationTier),
                    ["explanation"] = ActivationTiers.Explain(AnthillRuntime.ActivationTier),
                    ["specialist_execution_enabled"] = AnthillRuntime.EnableSpecialistAntExecution,
                    ["roles"] = AntExecutionCatalog.Contracts.Keys.OrderBy(r => r, StringComparer.Ordinal)
                        .Select(role => new Dictionary<string, object?>
                        {
                            ["role_id"] = role,
                            ["admitted_by_tier"] = ActivationTiers.Admits(AnthillRuntime.ActivationTier, role),
                            ["gate_open"] = AntExecutorCatalog.SpecialistGateOpen(role),
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

        /*
         * v3.3.0 (ADR-006): what each provider/model pair can actually DO.
         *
         * Capability is a property of the MODEL, not of the provider that serves it — a tool-capable
         * model on Ollama is tool-capable, and a text-only model on OpenAI is not made tool-capable
         * by the company hosting it. So this reports per model, and the operator can see why a role
         * pinned to one model gets tools and another does not.
         *
         * Unknown resolves to text-only rather than to a blank: an operator reading "no capabilities
         * listed" would reasonably assume the page was broken, whereas "text only" is the actual,
         * deliberate, fail-closed answer.
         */
        app.MapGet("/providers/capabilities", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_providers"); if (auth is not null) return auth;

            // v3.3.0: DISCOVERED capabilities where the runtime publishes them. Ollama reports a
            // per-model `capabilities` array on /api/tags, and it is authoritative in a way a name
            // table can never be: against three real local models the hand-written table was wrong
            // twice — it called gemma4:31b text-only when Ollama reports tools AND thinking, so the
            // operator's most capable local model would never have been offered a tool.
            //
            // Best-effort by design. An unreachable Ollama must not fail the whole page; the report
            // falls back to declared capabilities and says which it used, per provider.
            var discovered = await DiscoverOllamaModelsAsync();

            // Seed the cache the MODEL CALL PATH reads. Before this, discovery informed the report
            // and nothing else: the page said gemma4:31b supports tools while OllamaClient stripped
            // them from every request, and the model — never shown a tool — answered from priors.
            // A page that reports capabilities the runtime does not act on is a lie with a UI.
            OllamaCapabilityCache.Warm(AnthillRuntime.OllamaHost);

            var report = new List<Dictionary<string, object?>>();
            foreach (var p in ProviderCatalog.All)
            {
                var isOllama = string.Equals(p.Id, "ollama", StringComparison.OrdinalIgnoreCase);
                var useDiscovered = isOllama && discovered.Count > 0;
                // A provider whose catalog list is empty does not have "no models" — it has a
                // DYNAMIC list. Ollama serves whatever the operator has pulled, so the static
                // catalog cannot enumerate it and the live list comes from /ollama/models. Reporting
                // an empty array here would tell an operator their local provider supports nothing,
                // which is both wrong and the exact case this whole per-model design exists for.
                var declared = p.Models ?? Array.Empty<string>();
                var dynamicList = declared.Length == 0;
                var listed = useDiscovered
                    ? discovered.Keys.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToArray()
                    : dynamicList
                        ? new[] { p.DefaultModel }.Where(m => !string.IsNullOrWhiteSpace(m)).ToArray()
                        : declared.ToArray();

                var models = new List<Dictionary<string, object?>>();
                foreach (var model in listed)
                {
                    // What the runtime SAYS beats what the name suggests.
                    var caps = useDiscovered && discovered.TryGetValue(model, out var reported)
                        ? ModelCapabilities.FromOllama(reported)
                        : ModelCapabilityCatalog.For(p.Id, model);
                    models.Add(new Dictionary<string, object?>
                    {
                        ["model"] = model,
                        ["is_default"] = string.Equals(model, p.DefaultModel, StringComparison.OrdinalIgnoreCase),
                        ["tool_calling"] = caps.ToolCalling,
                        ["structured_output"] = caps.StructuredOutput,
                        ["streaming"] = caps.Streaming,
                        ["vision"] = caps.Vision,
                        ["embeddings"] = caps.Embeddings,
                        ["reasoning"] = caps.Reasoning,
                        ["context_window_tokens"] = caps.ContextWindowTokens,
                    });
                }
                report.Add(new Dictionary<string, object?>
                {
                    ["provider"] = p.Id,
                    ["name"] = p.Name,
                    // Per provider, and honest about which it was: "discovered" means the runtime
                    // itself reported these, "declared" means we inferred them from a name table.
                    // The UI needs the difference — a declared "no tool calling" is a guess worth
                    // second-guessing, a discovered one is fact.
                    ["source"] = useDiscovered ? "discovered" : "declared",
                    // The UI must join this with /ollama/models rather than treating the list as
                    // complete, and it can only know to do that if we say so.
                    ["models_are_dynamic"] = dynamicList,
                    ["dynamic_models_endpoint"] = dynamicList && p.Id == "ollama" ? "/ollama/models" : null,
                    ["models"] = models,
                });
            }
            return ApiJson.Ok(report);
        });

        /*
         * v3.4.0 (ADR-006) — the tool registry, inspectable.
         *
         * The harness is tool-centric and the tool inventory was the one thing about it an operator
         * could not see: which tools exist, what arguments each takes, which roles may call it, and
         * which declared tools have not been built. All of that lived in three source files that
         * never compared themselves to each other.
         *
         * Authorization is REPORTED BY ASKING THE ENFORCER. Every "may this role use this tool" cell
         * comes from ToolAuthorization.Evaluate — the same call RunTool makes — rather than from a
         * copy of its rules. The capability page taught this lesson the hard way: a report derived
         * independently of the code path it describes will eventually describe something else, and a
         * page that disagrees with the runtime is worse than no page.
         *
         * Schemas come from the tools themselves, so this doubles as the operator's view of exactly
         * what a model is offered.
         */
        app.MapGet("/tools", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;

            // Roles that can actually dispatch: the mission agents and specialists. Control-plane
            // identities are omitted because they are permitted everything by design, and a column
            // of unbroken "yes" tells an operator nothing.
            var roles = AntExecutionCatalog.Contracts.Keys
                .Concat(new[] { "researcher", "web", "file", "coder", "builder", "verifier" })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r, StringComparer.Ordinal).ToList();

            var registered = Queen.Tools.Tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

            var tools = new List<Dictionary<string, object?>>();
            foreach (var name in ToolInventory.Implemented.OrderBy(n => n, StringComparer.Ordinal))
            {
                // Implemented but not registered means a config gate is off — a real and common
                // state (file tools disabled), and one an operator needs distinguished from
                // "this tool does not exist", because the remedies are completely different.
                registered.TryGetValue(name, out var tool);

                var allowed = roles.Where(r => ToolAuthorization.Evaluate(r, name).Allowed)
                    .OrderBy(r => r, StringComparer.Ordinal).ToList();

                tools.Add(new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["status"] = tool is not null ? "registered" : "gated_off",
                    ["description"] = tool?.Description,
                    ["parameters"] = tool is null ? null : System.Text.Json.Nodes.JsonNode.Parse(tool.ParametersJson),
                    ["structurally_forbidden"] = ToolAuthorization.MissionAgentForbidden.Contains(name),
                    ["allowed_roles"] = allowed,
                });
            }

            // Declared-but-unbuilt tools are reported as first-class entries, not omitted. A role
            // allowed only these is authorized to dispatch nothing, and that is precisely the fact
            // an operator is trying to discover when a specialist ant runs and produces no work.
            foreach (var name in ToolInventory.Planned.OrderBy(n => n, StringComparer.Ordinal))
                tools.Add(new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["status"] = "planned",
                    ["description"] = "Referenced by an ant contract; not implemented in this build.",
                    ["parameters"] = null,
                    ["structurally_forbidden"] = false,
                    ["allowed_roles"] = AntExecutionCatalog.Contracts
                        .Where(kv => kv.Value.AllowedTools.Contains(name))
                        .Select(kv => kv.Key).OrderBy(r => r, StringComparer.Ordinal).ToList(),
                });

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["tools"] = tools,
                ["roles"] = roles,
                // Computed on every request rather than stored, so it stops being true the moment a
                // planned tool ships instead of outliving the problem it describes.
                ["roles_blocked_by_missing_tools"] =
                    ToolInventory.RolesBlockedByMissingTools(AntExecutionCatalog.Contracts),

                // v3.4.1: operator-defined tools, INCLUDING the ones this run refused to register.
                // A rejected definition is the state an operator most needs to see: it is stored, it
                // is visible in the editor, and it is not callable — which, unreported, looks
                // exactly like the tool being broken.
                ["user_tools"] = Queen.Memory.LoadToolDefinitions().Select(d =>
                {
                    var outcome = Queen.UserTools.FirstOrDefault(r =>
                        string.Equals(r.Name, d.Name, StringComparison.OrdinalIgnoreCase));
                    return new Dictionary<string, object?>
                    {
                        ["name"] = d.Name,
                        ["description"] = d.Description,
                        ["kind"] = d.Kind.ToString().ToLowerInvariant(),
                        ["enabled"] = d.Enabled,
                        // Three states, not two. Collapsing "the operator switched this off" into
                        // "rejected" was found in the browser: a disabled tool rendered as rejected
                        // with an EMPTY problem list, which is indistinguishable from a definition
                        // that failed validation — and the two have opposite remedies. One is
                        // re-enabled in a click; the other has to be rewritten.
                        ["status"] = !d.Enabled ? "disabled"
                            : outcome is { Registered: true } ? "registered" : "rejected",
                        ["problems"] = outcome?.Problems ?? (IReadOnlyList<string>)Array.Empty<string>(),
                        ["config"] = d.Config,
                        // Empty means EVERY dispatching role — the permissive default the operator
                        // chose. Reporting the empty list verbatim would read as "nobody".
                        ["allowed_roles"] = d.AllowedRoles.Count > 0 ? d.AllowedRoles : roles,
                        ["created_by"] = d.CreatedBy,
                        ["created_at"] = d.CreatedAt.ToIso(),
                    };
                }).ToList(),
                ["user_tools_enabled"] = AnthillRuntime.EnableUserTools,
                ["user_tool_allowed_hosts"] = AnthillRuntime.UserToolAllowedHosts,

                // v3.4.2: each contracted role checked against the model it is ACTUALLY routed to.
                // Reported here rather than only at startup because every mismatch fails silently
                // at runtime — a role routed to a model that cannot call tools produces a confident
                // answer that skipped every tool, which in a transcript looks like a weak model
                // rather than a misconfiguration an operator could fix in thirty seconds.
                ["model_fitness"] = Queen.Router is null
                    ? new List<Dictionary<string, object?>>()
                    : AntModelFitness.CheckAll(Queen.Router, AntExecutionCatalog.Contracts)
                        .Select(f => new Dictionary<string, object?>
                        {
                            ["role"] = f.RoleId,
                            ["provider"] = f.Provider,
                            ["model"] = f.Model,
                            ["fit"] = f.Fit,
                            ["unmet"] = f.Unmet,
                        }).ToList(),
            });
        });

        /*
         * v3.7.0 — START a conversation, and set its approval policy.
         *
         * The policy is recorded WITH ITS AUTHOR here, which is what makes a standing permission
         * valid at all: an unattributed AutoApprove or Bypass fails closed back to Ask, so an
         * endpoint that let one be set without naming who set it would produce a conversation whose
         * policy silently does nothing.
         */
        app.MapPost("/conversations", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;

            ConversationRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ConversationRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }

            var policy = Enum.TryParse<EscalationPolicy>(body?.Policy, ignoreCase: true, out var p)
                ? p : EscalationPolicy.Ask;
            var who = CurrentUsername(ctx) ?? "operator";

            var conversation = new Conversation
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Title = (body?.Title ?? "").Trim(),
                Role = string.IsNullOrWhiteSpace(body?.Role) ? "researcher" : body!.Role!.Trim(),
                Policy = policy,
                // Attribution is written for ANY standing permission. Ask needs none — nobody has to
                // sign for the safe default.
                PolicySetBy = policy == EscalationPolicy.Ask ? null : who,
                PolicySetAt = policy == EscalationPolicy.Ask ? null : AnthillTime.NowUtc(),
            };

            Queen.Memory.SaveConversation(conversation);
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["id"] = conversation.Id,
                ["policy"] = conversation.EffectivePolicy.ToString().ToLowerInvariant(),
            }, $"Conversation {conversation.Id} started.");
        });

        /*
         * Run one turn. THE call site that makes the v3.7.0 runtime real.
         *
         * The turn runs INSIDE a ConversationScope, which is what puts the escalation gate on the
         * tool dispatch path: outside a scope ConversationScope.Evaluate returns null and every gate
         * check silently passes. Without this endpoint the whole escalation mechanism was reachable
         * only from tests — which is the "no call site, no feature" rule, failed.
         */
        app.MapPost("/conversations/{id}/turns", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;

            var conversation = Queen.Memory.LoadConversation(id);
            if (conversation is null) return ApiJson.Error($"No conversation '{id}'.", "not_found");

            TurnRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<TurnRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }

            var message = (body?.Message ?? "").Trim();
            if (message.Length == 0) return ApiJson.Error("A message is required.", "bad_request");

            var mode = string.Equals(body?.Mode, "mission", StringComparison.OrdinalIgnoreCase)
                ? ConversationMode.Mission : ConversationMode.Chat;
            var answers = body?.Answers ?? new Dictionary<string, string>();

            // Every tool call this turn makes is now gated, and every decision recorded — the same
            // decision log the transcript endpoint reads back.
            using (ConversationScope.Enter(conversation, answers, Queen.Memory.SaveEscalationDecision))
            {
                var outcome = Queen.Conversations.Run(conversation, message, mode, answers);

                return ApiJson.Ok(new Dictionary<string, object?>
                {
                    ["mode"] = outcome.Mode.ToString().ToLowerInvariant(),
                    ["started"] = outcome.Started,
                    ["mission_id"] = outcome.MissionId,
                    ["summary"] = outcome.Summary,
                    ["decision"] = outcome.Decision is null ? null : new Dictionary<string, object?>
                    {
                        ["action"] = outcome.Decision.Action,
                        ["allowed"] = outcome.Decision.Allowed,
                        ["decided_by"] = outcome.Decision.DecidedBy,
                        ["reason"] = outcome.Decision.Reason,
                    },
                }, outcome.Summary);
            }
        });

        // Cancel: marks the conversation AND signals the work it started. Reports how many live
        // pieces were signalled, so "stopped two missions" is distinguishable from "nothing running".
        app.MapPost("/conversations/{id}/cancel", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "run_mission"); if (auth is not null) return auth;
            if (Queen.Memory.LoadConversation(id) is null)
                return ApiJson.Error($"No conversation '{id}'.", "not_found");

            var stopped = Queen.Conversations.Cancel(id);
            return ApiJson.Ok(new Dictionary<string, object?> { ["signalled"] = stopped },
                stopped == 0 ? "Conversation cancelled; nothing was running."
                             : $"Conversation cancelled; {stopped} running item(s) signalled.");
        });

        /*
         * v3.7.0 — conversations, and what each one is doing.
         *
         * State is DERIVED on request, never stored. A stored status is a second thing to keep in
         * step with reality and it goes wrong exactly where an operator relies on it: a process that
         * died leaves its last write saying "running" forever.
         */
        app.MapGet("/conversations", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["conversations"] = Queen.Memory.LoadConversations().Select(c =>
                {
                    var state = ConversationStateReader.Read(Queen.Memory, c.Id);
                    return new Dictionary<string, object?>
                    {
                        ["id"] = c.Id,
                        ["title"] = c.Title,
                        ["role"] = c.Role,
                        // The EFFECTIVE policy, not the stored one. An unattributed standing
                        // permission falls back to Ask, and reporting the stored value would tell an
                        // operator they had switched approvals off when they had not.
                        ["policy"] = state.Policy.ToString().ToLowerInvariant(),
                        ["policy_set_by"] = c.PolicySetBy,
                        ["policy_attributed"] = c.PolicyIsAttributed,
                        ["cancelled"] = c.Cancelled,
                        ["mission_ids"] = c.MissionIds,
                        ["doing"] = state.Doing,
                        ["waiting_on"] = state.WaitingOn,
                        // Hoisted so a UI can highlight it without re-deriving the rule: this is the
                        // only state where nothing moves until a human acts.
                        ["needs_operator"] = state.NeedsOperator,
                        ["updated_at"] = c.UpdatedAt.ToIso(),
                    };
                }).ToList(),
            });
        });

        // One conversation, with its transcript and its decision log. The two together are the
        // whole audit: what was said, and what was permitted.
        app.MapGet("/conversations/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;

            var conversation = Queen.Memory.LoadConversation(id);
            if (conversation is null) return ApiJson.Error($"No conversation '{id}'.", "not_found");

            var state = ConversationStateReader.Read(Queen.Memory, id);

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["id"] = conversation.Id,
                ["doing"] = state.Doing,
                ["did"] = state.Did,
                ["waiting_on"] = state.WaitingOn,
                ["needs_operator"] = state.NeedsOperator,
                ["policy"] = state.Policy.ToString().ToLowerInvariant(),
                ["mission_ids"] = conversation.MissionIds,
                ["turns"] = Queen.Memory.LoadConversationTurns(id).Select(t => new Dictionary<string, object?>
                {
                    ["ordinal"] = t.Ordinal, ["role"] = t.Role, ["content"] = t.Content,
                    ["provider"] = t.Provider, ["model"] = t.Model,
                    ["tools_offered"] = t.ToolsOffered, ["tools_called"] = t.ToolsCalled,
                    ["mission_id"] = t.MissionId, ["created_at"] = t.CreatedAt.ToIso(),
                }).ToList(),
                // Refusals included. An audit asking "did it try to do X" needs those most, because
                // they are the attempts nobody saw happen.
                ["decisions"] = Queen.Memory.LoadEscalationDecisions(id).Select(d => new Dictionary<string, object?>
                {
                    ["action"] = d.Action, ["allowed"] = d.Allowed,
                    ["policy"] = d.Policy.ToString().ToLowerInvariant(),
                    ["decided_by"] = d.DecidedBy, ["asked_directly"] = d.WasAskedDirectly,
                    ["reason"] = d.Reason, ["decided_at"] = d.DecidedAt.ToIso(),
                }).ToList(),
            });
        });

        /*
         * v3.5.0 — the mission workspaces, and what each change was based on.
         *
         * Reports CLEANED and ORPHANED workspaces alongside live ones, because the row outliving the
         * directory is the point: "what was this merged change based on" is asked long after the
         * files are gone, and a list showing only what currently exists cannot answer it.
         *
         * Orphaned is kept distinct from cleaned in the report for the same reason it is distinct in
         * the model — "we removed it" and "it vanished under us" call for different responses, and a
         * list that shows only "gone" hides the second entirely.
         */
        /*
         * v3.8.0 — durable attempts, and the ones that need a human.
         *
         * Recovery already reports abandoned work to stderr at startup, which is exactly nobody's
         * console. An attempt that MAY have left effects outside the process is, by design, not
         * automatically redeliverable — it waits for an operator who can look — and a decision that
         * waits for a human it never reaches is not a decision, it is a stall.
         */
        app.MapGet("/attempts", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;

            static Dictionary<string, object?> Project(Anthill.Core.Workers.TaskAttempt a) => new()
            {
                ["id"] = a.Id,
                ["task_id"] = a.TaskId,
                ["mission_id"] = a.MissionId,
                ["number"] = a.Number,
                ["worker_id"] = a.WorkerId,
                ["state"] = a.State.ToString().ToLowerInvariant(),
                ["provider"] = a.Provider,
                ["model"] = a.Model,
                ["may_have_side_effects"] = a.MayHaveSideEffects,
                // Reported rather than inferred from the state name, so the console cannot offer a
                // retry the colony would consider unsafe.
                ["safe_to_redeliver"] = a.SafeToRedeliver,
                ["failure_class"] = a.FailureClass,
                ["failure_reason"] = a.FailureReason,
                ["started_at"] = a.StartedAt.ToIso(),
                ["finished_at"] = a.FinishedAt?.ToIso(),
            };

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["recent"] = Queen.Memory.LoadRecentAttempts().Select(Project).ToList(),
                ["needs_review"] = Queen.Memory.LoadAttemptsNeedingReview().Select(Project).ToList(),
                ["worker"] = Anthill.Core.Workers.LocalWorker.Id,
            });
        });

        app.MapGet("/workspaces", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;

            var workspaces = Queen.Workspaces.All().Select(w => new Dictionary<string, object?>
            {
                ["id"] = w.Id,
                ["mission_id"] = w.MissionId,
                ["state"] = w.State.ToString().ToLowerInvariant(),
                ["mode"] = w.Mode,
                ["root"] = w.Root,
                ["base_revision"] = w.BaseRevision,
                ["repository_fingerprint"] = w.RepositoryFingerprint,
                ["branch"] = w.Branch,
                ["retained_by"] = w.RetainedBy,
                ["retain_reason"] = w.RetainReason,
                ["note"] = w.Note,
                // Whether cleanup may take it. Reported rather than inferred from the state name, so
                // the UI cannot draw a delete button the server would refuse.
                ["deletable"] = w.Deletable,
                ["usable"] = w.Usable,
                ["created_at"] = w.CreatedAt.ToIso(),
                ["updated_at"] = w.UpdatedAt.ToIso(),
            }).ToList();

            // What each LIVE workspace can be verified with. Detected on request rather than stored,
            // because a workspace's project types change the moment an agent adds a package.json —
            // and a stored manifest would keep describing the repository as it was when it was made.
            foreach (var entry in workspaces)
            {
                var root = entry["root"]?.ToString() ?? "";
                if (entry["usable"] is not true || root.Length == 0) continue;

                var manifest = Anthill.Core.Workspaces.WorkspaceCapabilityManifest.Detect(root);
                entry["project_types"] = manifest.ProjectTypes;
                entry["adapter_versions"] = manifest.AdapterVersions;
                // The check IDS, not the command lines. An operator needs to know what can be run;
                // publishing the argument strings would invite treating them as editable, and they
                // are declared in the repository precisely so they are not.
                entry["available_checks"] = manifest.Checks.Select(c => c.Id).ToList();
            }

            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["workspaces"] = workspaces,
                ["root"] = Anthill.Core.Workspaces.MissionWorkspaceManager.Root,
            });
        });

        /*
         * v3.4.1 (ADR-006) — define a tool without a rebuild.
         *
         * Validated BEFORE it is stored, by the SAME validator the registrar uses at startup. A
         * definition accepted here and rejected at the next restart would be the worst of both
         * worlds: an operator told it worked, and a colony that quietly does not have it.
         *
         * Registration into the live registry is immediate, so the tool is usable in the next
         * mission rather than after a restart — and it is the same ToolRegistry every built-in lives
         * in. The absence of a separate path IS the feature; see Queen.BuildToolRegistry.
         */
        app.MapPost("/tools/user", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_settings"); if (auth is not null) return auth;
            if (!AnthillRuntime.EnableUserTools)
                return ApiJson.Error("User-defined tools are disabled by config.", "permission_denied");

            UserToolRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<UserToolRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (body is null) return ApiJson.Error("A tool definition is required.", "bad_request");

            var definition = new ToolDefinition
            {
                Name = (body.Name ?? "").Trim().ToLowerInvariant(),
                Description = (body.Description ?? "").Trim(),
                Kind = ToolKinds.Parse(body.Kind),
                ParametersJson = string.IsNullOrWhiteSpace(body.Parameters)
                    ? """{"type":"object","properties":{}}""" : body.Parameters!,
                Config = body.Config ?? new Dictionary<string, string>(),
                AllowedRoles = body.AllowedRoles ?? new List<string>(),
                Enabled = body.Enabled ?? true,
            };

            var problems = UserToolRegistrar.Default().Validate(definition);
            if (problems.Count > 0)
                return ApiJson.Error($"Tool definition rejected: {string.Join("; ", problems)}",
                    "bad_request", new Dictionary<string, object?> { ["problems"] = problems });

            Queen.Memory.SaveToolDefinition(definition);
            // The WHOLE set is re-registered rather than just this one, which keeps the grant table
            // a wholesale replacement — the property that stops a since-removed definition from
            // being granted forever.
            Queen.ReloadUserTools();
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId, "user_tool_registered",
                $"Operator-defined tool '{definition.Name}' registered", null, "operator",
                new() { ["tool_name"] = definition.Name, ["kind"] = definition.Kind.ToString() });

            return ApiJson.Ok(new Dictionary<string, object?> { ["name"] = definition.Name },
                $"Tool '{definition.Name}' registered.");
        });

        // Revoke. DISABLING is the default because the row is evidence — a transcript that called
        // the tool stays explainable. `?purge=true` deletes outright, for one created in error.
        app.MapDelete("/tools/user/{name}", (HttpContext ctx, string name) =>
        {
            var auth = RequireAuth(ctx, "manage_settings"); if (auth is not null) return auth;

            var purge = string.Equals(ctx.Request.Query["purge"], "true", StringComparison.OrdinalIgnoreCase);
            var changed = purge
                ? Queen.Memory.DeleteToolDefinition(name)
                : Queen.Memory.SetToolDefinitionEnabled(name, false);
            if (!changed) return ApiJson.Error($"No user-defined tool named '{name}'.", "not_found");

            // Out of the LIVE registry too. Leaving it registered would keep offering a model a tool
            // whose definition is gone, and every call would fail for a reason no transcript shows.
            Queen.Tools.Unregister(name);
            Queen.ReloadUserTools();
            Queen.Memory.LogEvent(AnthillRuntime.SystemApiMissionId,
                purge ? "user_tool_deleted" : "user_tool_disabled",
                $"Operator-defined tool '{name}' {(purge ? "deleted" : "disabled")}", null, "operator",
                new() { ["tool_name"] = name });

            return ApiJson.Ok(new Dictionary<string, object?> { ["name"] = name },
                purge ? $"Tool '{name}' deleted." : $"Tool '{name}' disabled.");
        });

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
            // v3.2.0: the provider's own status, not a prefix test on its prose. This also closes
            // a real hole — "<provider> returned an empty response." does not start with ERROR:,
            // so a provider that answered with nothing used to be recorded as VERIFIED.
            var ok = reply.Ok;
            Queen.Memory.SetProviderVerification(p, ok, reply.Content);
            return ok
                ? ApiJson.Ok(Queen.Memory.ListProviderConnections(), $"{p} connection verified.")
                : ApiJson.Error(reply.Content, "provider_test_failed");
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
            var o = new Objective
            {
                Title = title, Charter = charter,
                Priority = body!.Priority ?? 0, MaxRuns = Math.Max(0, body.MaxRuns ?? 0),
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
                ["homelab_stop_engaged"] = Anthill.Core.Homelab.Actions.HomelabActionControl.IsStopped,
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

        // Live Ollama probe. v2.4.3: /api/version alone lied by omission — Ollama can be up while
        // the configured model is absent (typical on offline installs), and every ant call then
        // fails although the chip showed green. Now also checks /api/tags for the model.
        bool? ollamaReachable = null;
        bool? ollamaModelPresent = null;
        if (AnthillRuntime.UseOllama)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var baseHost = AnthillRuntime.OllamaHost.TrimEnd('/');
                using var resp = InternalHttp.GetAsync($"{baseHost}/api/version", cts.Token).GetAwaiter().GetResult();
                ollamaReachable = resp.IsSuccessStatusCode;
                if (ollamaReachable == true)
                {
                    try
                    {
                        using var tags = InternalHttp.GetAsync($"{baseHost}/api/tags", cts.Token).GetAwaiter().GetResult();
                        if (tags.IsSuccessStatusCode)
                        {
                            var tagsBody = tags.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
                            using var doc = System.Text.Json.JsonDocument.Parse(tagsBody);
                            var want = AnthillRuntime.OllamaModel;
                            ollamaModelPresent = doc.RootElement.TryGetProperty("models", out var models)
                                && models.ValueKind == System.Text.Json.JsonValueKind.Array
                                && models.EnumerateArray().Any(m =>
                                    m.TryGetProperty("name", out var n)
                                    && (string.Equals(n.GetString(), want, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(n.GetString(), want + ":latest", StringComparison.OrdinalIgnoreCase)
                                        || string.Equals((n.GetString() ?? "").Split(':')[0], want, StringComparison.OrdinalIgnoreCase)));
                        }
                    }
                    catch { /* model check is best-effort; reachability already established */ }
                }
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
            ["ollama_model_present"] = ollamaModelPresent, // v2.4.3: null = unknown/not checked
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
            // v2.16.0: the ANSWER the operator reads is final_result (a plain-English rewrite when
            // synthesis is on). user_result stays available as raw_output so the activity view can
            // show exactly what the winning task emitted, unedited.
            ["final_output"] = mission.GetValueOrDefault("final_result")
                               ?? mission.GetValueOrDefault("user_result"),
            ["raw_output"] = mission.GetValueOrDefault("user_result"),
            ["synthesized"] = mission.GetValueOrDefault("final_result") is string fr
                              && fr != (mission.GetValueOrDefault("user_result") as string ?? ""),
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

    /// <summary>
    /// Ask Ollama which models it is holding and what each can do (/api/tags → capabilities[]).
    ///
    /// Best-effort ON PURPOSE. Ollama frequently lives on another host and is frequently down; a
    /// capabilities page that fails because a local runtime is asleep is worse than one that falls
    /// back to declared values and says so. An empty result therefore means "could not ask", never
    /// "supports nothing" — the caller distinguishes them, and the response reports which it used.
    /// </summary>
    private static async Task<Dictionary<string, List<string>>> DiscoverOllamaModelsAsync()
    {
        var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var host = AnthillRuntime.OllamaHost.TrimEnd('/');
            var resp = await InternalHttp.GetAsync($"{host}/api/tags", cts.Token);
            if (!resp.IsSuccessStatusCode) return found;

            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            var root = System.Text.Json.Nodes.JsonNode.Parse(body)?.AsObject();
            foreach (var entry in root?["models"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
            {
                var name = entry?["name"]?.GetValue<string>() ?? entry?["model"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var caps = new List<string>();
                foreach (var c in entry?["capabilities"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
                {
                    var value = c?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(value)) caps.Add(value!);
                }
                found[name!] = caps;
            }
        }
        catch (Exception)
        {
            // Unreachable, slow, or a shape we do not recognise: fall back to declared. Deliberately
            // silent — this runs on every page load of a settings screen, and an operator with no
            // local runtime configured should not be reading exception noise in their logs.
        }
        return found;
    }

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

    /// <summary>
    /// v2.25.0 Phase F: gather every readiness input from live storage and evaluate. The
    /// evaluation itself is pure (<see cref="V3Readiness.Evaluate"/>) so each threshold rule is
    /// testable without a database; this method is only the plumbing.
    /// </summary>
    private static ReadinessReport EvaluateReadiness()
    {
        var metrics = QualificationScoreboard.Compute(Queen.Memory.LoadScoreableRecommendations(500));
        var stability = Queen.Memory.FaultInjectionStability();
        var (executed, _, unknown) = Homelab.CountExecutedActionLifecycles();
        return V3Readiness.Evaluate(new V3Readiness.Inputs(
            Shadow: metrics,
            ShadowSample: metrics.Sample,
            UnresolvedShadowBacklog: Queen.Memory.CountUnresolvedShadowRecommendations(),
            FaultInjectionRuns: stability.Runs,
            FaultInjectionStableStreak: stability.StableStreak,
            FaultInjectionStable: stability.Stable,
            ExecutedActions: executed,
            ExecutedActionsUnknownLifecycle: unknown,
            MinShadowSample: AnthillRuntime.ReadinessMinShadowSample,
            MinDiagnosisPrecision: AnthillRuntime.ReadinessMinDiagnosisPrecision,
            MinActionAccuracy: AnthillRuntime.ReadinessMinActionAccuracy,
            Attestations: Queen.Memory.LoadReadinessAttestations(),
            BreakGlassKeepWithoutVerify: AnthillRuntime.AutonomyAutoApplyKeepWithoutVerify));
    }

    private static Dictionary<string, object?> ReadinessSnapshot()
    {
        var report = EvaluateReadiness();
        return new Dictionary<string, object?>
        {
            ["ready"] = report.Ready,
            ["statement"] = report.Statement,
            ["satisfied"] = report.SatisfiedCount,
            ["total"] = report.Total,
            // Projected explicitly (same reason as /shadow/json): no serializer naming policy is
            // configured, and the wire shape must not change when a C# property is renamed.
            ["checks"] = report.Checks.Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Id, ["title"] = c.Title, ["kind"] = c.Kind.ToString().ToLowerInvariant(),
                ["satisfied"] = c.Satisfied, ["measured_holds"] = c.MeasuredHolds,
                ["attested"] = c.Attested, ["detail"] = c.Detail,
            }).ToList(),
            ["attestable_ids"] = V3Readiness.AttestableIds.OrderBy(x => x).ToList(),
        };
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

    private static string LoadUi() => LoadUiAsset("index.html", "<h1>ANTHILL</h1><p>UI resource missing.</p>");

    /// <summary>Reads an embedded UI asset (index.html, app.js) by resource-name suffix.</summary>
    private static string LoadUiAsset(string suffix, string fallback = "")
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (name is null) return fallback;
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class ShadowJudgeBody
{
    [System.Text.Json.Serialization.JsonPropertyName("incident_id")] public string? IncidentId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("diagnosis_correct")] public bool DiagnosisCorrect { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("action_was_needed")] public bool ActionWasNeeded { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("action_matched")] public bool ActionMatched { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("would_have_succeeded")] public bool WouldHaveSucceeded { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("note")] public string? Note { get; set; }
}

public sealed class AttestBody
{
    [System.Text.Json.Serialization.JsonPropertyName("threshold_id")] public string? ThresholdId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("satisfied")] public bool Satisfied { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("note")] public string? Note { get; set; }
}

public sealed class MissionRequest { public string Goal { get; set; } = ""; }

/// <summary>
/// v3.4.0: one tool-calling agent run. Budgets are optional and clamped server-side — a client
/// must not be able to ask for an unbounded loop, because the thing on the other end can run tools.
/// </summary>
public sealed class AgentRunRequest
{
    public string Goal { get; set; } = "";
    /// <summary>Which role runs it. The role decides which tools exist for this run.</summary>
    public string? Role { get; set; }
    /// <summary>Optional system framing, prepended to the conversation.</summary>
    public string? System { get; set; }
    public int? MaxTurns { get; set; }
    public int? MaxToolCalls { get; set; }
    /// <summary>Pin this run to a specific model, overriding the role's route.</summary>
    public string? Model { get; set; }
}
/// <summary>
/// v3.4.1: an operator-defined tool, as submitted. Deliberately flat and stringly-typed at the
/// wire: this is a form, and every field is validated by <see cref="UserToolRegistrar.Validate"/>
/// before anything is stored — the same validator startup uses, so "accepted here, rejected at
/// restart" cannot happen.
/// </summary>
public sealed class UserToolRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    /// <summary>http | composite | mcp | command. Only http is buildable in this release.</summary>
    public string? Kind { get; set; }
    /// <summary>JSON Schema for the arguments, as a string. Parsed during validation.</summary>
    public string? Parameters { get; set; }
    /// <summary>Kind-specific settings — for http: url, method, body, content_type, header.*</summary>
    public Dictionary<string, string>? Config { get; set; }
    /// <summary>Empty or absent means every dispatching role may call it.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("allowed_roles")]
    public List<string>? AllowedRoles { get; set; }
    public bool? Enabled { get; set; }
}
/// <summary>v3.7.0: start a conversation. Policy is parsed leniently; anything unknown is Ask.</summary>
public sealed class ConversationRequest
{
    public string? Title { get; set; }
    public string? Role { get; set; }
    /// <summary>ask | autoapprove | bypass. Recorded with its author when it is not ask.</summary>
    public string? Policy { get; set; }
}

/// <summary>v3.7.0: one turn, with the operator's answers for anything it needs permission to do.</summary>
public sealed class TurnRequest
{
    public string? Message { get; set; }
    /// <summary>chat | mission. Mission escalation is gated; chat is not.</summary>
    public string? Mode { get; set; }
    /// <summary>Action name to "approve". Absence is NOT consent.</summary>
    public Dictionary<string, string>? Answers { get; set; }
}
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

    /// <summary>
    /// v2.24.0: a response that carries context ALONGSIDE its data rather than inside it.
    ///
    /// Needed because some payloads cannot be explained by the rows alone. The colony pheromone
    /// view is the case that forced it: after the learning reset every trail sits at neutral
    /// strength, so the display is a wall of identical bars that is indistinguishable from a
    /// broken subsystem. `data` keeps its exact shape — every existing client reads it unchanged —
    /// and the explanation travels beside it.
    /// </summary>
    public static IResult Ok(object? data, Dictionary<string, object?> meta, string message = "ok") =>
        Envelope(new Dictionary<string, object?>
        {
            ["success"] = true,
            ["message"] = TextUtil.SanitizeUtf16(message),
            ["data"] = SanitizeJson(data),
            ["meta"] = SanitizeJson(meta),
        }, 200);

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
