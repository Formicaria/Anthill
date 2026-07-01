using System.Reflection;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Diagnostics;
using Anthill.Core.Domain;
using Anthill.Core.Memory;
using Anthill.Core.Models;
using Anthill.Core.Orchestration;
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
public static class ApiHost
{
    public static Queen Queen { get; private set; } = null!;
    public static ApiJobRegistry Jobs { get; private set; } = null!;
    public static ColonyDirector Director { get; private set; } = null!;
    private static RateLimiter MissionLimiter = null!;
    private static RateLimiter AuthLimiter = null!;
    private static string UiHtml = "";

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
        Jobs = new ApiJobRegistry(Queen, AnthillRuntime.ApiJobWorkers);
        Director = new ColonyDirector(Queen, Jobs);
        MissionLimiter = new RateLimiter(AnthillRuntime.RateLimitMissionWindow, AnthillRuntime.RateLimitMissionMax);
        AuthLimiter = new RateLimiter(AnthillRuntime.RateLimitAuthWindow, AnthillRuntime.RateLimitAuthMax);
        UiHtml = LoadUi();

        var app = builder.Build();

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

        Console.WriteLine($"ANTHILL v{AnthillRuntime.Version} API listening on http://{AnthillRuntime.ApiHost}:{AnthillRuntime.ApiPort}");
        Console.WriteLine($"Open the colony console at http://{AnthillRuntime.ApiHost}:{AnthillRuntime.ApiPort}/ui");

        if (autostart)
        {
            if (Director.Start()) Console.WriteLine("Autonomous Colony Director started (--autonomous).");
            else Console.Error.WriteLine("--autonomous ignored: set autonomy_enabled=true in config to start the Director.");
        }

        app.Run();
        return 0;
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/", () => ApiJson.Ok(new Dictionary<string, object?>
        {
            ["name"] = "ANTHILL Core", ["version"] = AnthillRuntime.Version, ["ui"] = "/ui",
        }, "ANTHILL local API. Authenticate with X-Anthill-Token for colony endpoints."));

        app.MapGet("/ui", () => Results.Content(UiHtml, "text/html"));

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
        ProtectedText(app, "/sources", "read_sources", () => Queen.FormatSources());
        ProtectedText(app, "/source-quality", "read_sources", () => Queen.FormatSourceQuality());
        ProtectedText(app, "/patches", "read_patches", () => Queen.FormatPatchList());
        ProtectedText(app, "/approvals", "read_approvals", () => Queen.FormatApprovals());
        ProtectedText(app, "/missions", "read_status", () => Queen.FormatMissionHistory());

        ProtectedJson(app, "/graph", "read_graph", ctx =>
        {
            var includeResults = ctx.Request.Query["include_results"] == "true";
            if (includeResults && !ApiPermissionAllowed("read_graph_results"))
                return ApiJson.Error("Permission denied: read_graph_results is disabled.", "permission_denied");
            return ApiJson.Ok(Queen.BuildTaskGraphData(includeResults: includeResults));
        });

        app.MapGet("/missions/{id}", (HttpContext ctx, string id) =>
            RequireAuth(ctx, "read_status") ?? Results.Text(Queen.FormatMissionDetail(id), "text/plain"));
        app.MapGet("/missions/{id}/graph", (HttpContext ctx, string id) =>
            RequireAuth(ctx, "read_graph") ?? ApiJson.Ok(Queen.BuildTaskGraphData(id)));
        app.MapGet("/sources/{id}", (HttpContext ctx, string id) =>
            RequireAuth(ctx, "read_sources") ?? Results.Text(Queen.FormatSourceDetail(id), "text/plain"));
        app.MapGet("/patches/{id}", (HttpContext ctx, string id) =>
            RequireAuth(ctx, "read_patches") ?? Results.Text(Queen.FormatPatchDetail(id), "text/plain"));
        app.MapGet("/approvals/{id}", (HttpContext ctx, string id) =>
            RequireAuth(ctx, "read_approvals") ?? Results.Text(Queen.FormatApprovalDetail(id), "text/plain"));

        app.MapGet("/jobs", (HttpContext ctx) => RequireAuth(ctx, "read_status") ?? ApiJson.Ok(Jobs.ListJobs()));
        app.MapGet("/jobs/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_status"); if (auth is not null) return auth;
            var job = Jobs.GetJob(id);
            return job is null ? ApiJson.Error($"No job found with id: {id}", "not_found") : ApiJson.Ok(job.ToDict());
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

        // Proxy Ollama /api/tags so the UI can list available models without a direct connection
        app.MapGet("/ollama/models", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_models"); if (auth is not null) return auth;
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                var host = AnthillRuntime.OllamaHost.TrimEnd('/');
                var resp = await http.GetAsync($"{host}/api/tags");
                var body = await resp.Content.ReadAsStringAsync();
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
                limit: limit <= 0 ? 200 : limit);
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
            return ApiJson.Ok(Queen.Memory.ListPheromoneTrails(limit <= 0 ? 300 : limit));
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
            return ApiJson.Ok(Queen.Memory.ListAutonomyRuns(string.IsNullOrEmpty(objectiveId) ? null : objectiveId));
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
                Queen.Memory.UpdateObjectiveStatus(id, EnumExtensions.ParseObjectiveStatus(body.Status));
            return ApiJson.Ok(ObjectiveDict(Queen.Memory.GetObjective(id)!), "Objective updated.");
        });

        app.MapDelete("/objectives/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_objectives"); if (auth is not null) return auth;
            if (Queen.Memory.GetObjective(id) is null) return ApiJson.Error($"No objective found with id: {id}", "not_found");
            Queen.Memory.DeleteObjective(id);
            return ApiJson.Ok(new Dictionary<string, object?> { ["id"] = id }, "Objective removed.");
        });
    }

    private static Dictionary<string, object?> ObjectiveDict(Objective o) => new()
    {
        ["id"] = o.Id, ["title"] = o.Title, ["charter"] = o.Charter, ["priority"] = o.Priority,
        ["status"] = o.Status.Value(), ["max_runs"] = o.MaxRuns, ["run_count"] = o.RunCount,
        ["consecutive_failures"] = o.ConsecutiveFailures, ["parent_objective_id"] = o.ParentObjectiveId,
        ["created_at"] = o.CreatedAt.ToIso(), ["last_run_at"] = o.LastRunAt.ToIsoOrNull(),
    };

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
    public static IResult Ok(object? data = null, string message = "ok") =>
        Results.Json(new Dictionary<string, object?> { ["success"] = true, ["message"] = message, ["data"] = data });

    public static IResult Error(string message, string? error = null, object? data = null) =>
        Results.Json(new Dictionary<string, object?> { ["success"] = false, ["message"] = message, ["error"] = error, ["data"] = data },
            statusCode: error switch { "unauthorized" => 401, "permission_denied" => 403, "rate_limited" => 429, "not_found" => 404, _ => 400 });
}
