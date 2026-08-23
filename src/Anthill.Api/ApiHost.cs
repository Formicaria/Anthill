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

    /// <summary>
    /// The modules loaded into this process. v3.8.6.
    ///
    /// Held rather than discarded because "which capabilities does this build actually have" is an
    /// operator question, and the honest answer is this list — not a config flag saying a provider
    /// is enabled, which describes intent rather than what loaded.
    /// </summary>
    public static ModuleHost Modules { get; private set; } = null!;

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
    // v0.3.8.52: the app.js split — one field per domain asset.
    private static string UiHomelabJs = "";
    // v0.3.8.55: two more split assets — inspector routing and themes (the app.js size guard).
    private static string UiInspectorRoutingJs = "";
    private static string UiThemesJs = "";
    private static string UiConsoleExtrasJs = "";
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
        // v3.8.6 — modules are composed HERE, and before the colony is built.
        //
        // Before it, so that a Queen constructed below already has reasoning available: loading
        // after composition would leave the startup fitness report, and any mission arriving in
        // that window, looking at a colony with no providers.
        //
        // Which forces the memory and the bus to exist first, and the Queen to ADOPT them rather
        // than build her own — otherwise the events these modules publish while registering would
        // be announced to a bus she then replaced.
        //
        // This is the whole of the module boundary in practice. `new ReasoningModule(...)` is the
        // only place in the process where Anthill.Modules.Reasoning is named; delete that one
        // argument and the core still builds, still boots, still plans and dispatches, and every
        // model call returns UnavailableProvider's typed refusal instead of an answer.
        var memory = new SqliteMemory();
        var events = new InProcessEventBus();
        memory.EventBus = events;

        // v3.8.33 — the composition root teaches the core how to ASK a local host what it holds.
        //
        // The core needs the answer (to resolve "which model" when the operator has not chosen one)
        // and must not own the transport, so this is registered here rather than implemented in
        // Anthill.Core. Unregistered stays a real state: it resolves to a refusal naming the host,
        // never to a built-in model name.
        ReasoningProviders.RegisterLocalModelLister(InstalledOllamaModels);

        Modules = new ModuleHost(memory, events);
#if MICROMOUND
        // MICROMOUND is an OPTIONAL integration, twice over: this block only compiles when the
        // wire-contract checkout exists beside the repo, and only runs when the operator has set
        // micromound_enabled. A colony without hardware carries no micromound — not a disabled
        // copy of it, none of it. Configuration only at registration, for a sharper version of
        // the homelab's reason: a mound may be a Pi in a shed on a dead battery, and mounds dial
        // IN; the colony never reaches out.
        if (AnthillRuntime.EnableMicromound)
            Modules.LoadAll(new MicromoundModule(
                new MicromoundOptions(
                    DatabasePath: Path.IsPathRooted(AnthillRuntime.DbPath)
                        ? AnthillRuntime.DbPath
                        : Path.Combine(AnthillRuntime.ScriptDir, AnthillRuntime.DbPath),
                    StopFileName: "MICROMOUND_STOP",
                    WorkspaceRootPath: AnthillRuntime.WorkspaceRootPath,
                    ColonyVersion: AnthillRuntime.Version),
                FieldCipher.CreateDefault()));
#endif
        Modules.LoadAll(
            new ReasoningModule(AnthillRuntime.OllamaHost),
            // v3.8.7: the homelab is configuration-only at registration, so an asleep Proxmox
            // node cannot stop the colony booting. InitHomelab() below still builds the
            // repository and scheduler; this only tells the module what it is running inside.
            new HomelabModule(
                new HomelabOptions(
                    DatabasePath: Path.IsPathRooted(AnthillRuntime.DbPath)
                        ? AnthillRuntime.DbPath
                        : Path.Combine(AnthillRuntime.ScriptDir, AnthillRuntime.DbPath),
                    StopFileName: AnthillRuntime.HomelabStopFileName,
                    HealthTimeoutMs: AnthillRuntime.HomelabHealthTimeoutMs,
                    NotificationsEnabled: AnthillRuntime.EnableHomelabNotifications,
                    SlackWebhook: AnthillRuntime.HomelabSlackWebhook,
                    DiscordWebhook: AnthillRuntime.HomelabDiscordWebhook,
                    GenericWebhook: AnthillRuntime.HomelabGenericWebhook,
                    ColonyVersion: AnthillRuntime.Version,
                    WorkspaceRootPath: AnthillRuntime.WorkspaceRootPath),
                FieldCipher.CreateDefault()),
            // v3.8.16: the six tools that act on the machine. The guard is built here rather than
            // inside the module because it reads the current mission's workspace through an ambient
            // scope, and missions are core. Same root the Queen builds hers from.
            new ToolsModule(new WorkspacePathGuard(AnthillRuntime.AllowedWorkspaceRoot, ToolRuntime.Live),
                ToolRuntime.Live, SsrfRuntime.Live));

        Host = RuntimeHost.Create(memory);
        Queen = Host.Queen;

        // v3.8.10 — hand module-contributed tools to the registry the Queen just built. Modules load
        // before she exists, so a tool registered during Register() has nowhere to go until now;
        // ModuleHost buffers them and this is the drain.
        //
        // v3.8.16: no longer empty. Six of the colony's eleven tools arrive through here, so this
        // line is now load-bearing rather than a live-but-unused path — remove it and the colony
        // boots without file, shell, web or patch tools.
        //
        // AdoptModuleTools rather than a foreach over Tools.Register, because the Queen's runtime
        // profile is resolved from the registry as it stood at construction: registering without
        // re-resolving would leave /status reporting five tool grants for an eleven-tool colony.
        Queen.AdoptModuleTools(Modules.ContributedTools);
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
        UiHomelabJs = LoadUiAsset("homelab.js");
        UiInspectorRoutingJs = LoadUiAsset("inspector-routing.js");
        UiThemesJs = LoadUiAsset("themes.js");
        UiConsoleExtrasJs = LoadUiAsset("console-extras.js");
        UiGridCss = LoadUiAsset("dashboard-grid.css");
        InitHomelab(); // v1.9.0 homelab foundation (read-only; see Homelab/ApiHost.Homelab.cs)

        // v0.3.8.62 (S4): the startup half of write durability. A batch interrupted by a crash
        // left an open journal; every one is rolled back here under the same hash rule as a live
        // rollback, and an unrecoverable one leaves the durable marker that halts auto-apply.
        try
        {
            foreach (var line in Anthill.SDK.Common.ApplyTransaction.Recover(AnthillRuntime.AllowedWorkspaceRoot))
                Console.WriteLine($"[apply-recovery] {line}");
        }
        catch (Exception e) { Console.Error.WriteLine($"[apply-recovery] recovery scan failed: {e.Message}"); }

        // v0.3.8.91: mint the first-run bootstrap secret, BEFORE the listener opens. On a
        // network-reachable bind with no administrator yet, /auth/setup requires it — see
        // SetupAuthority for why the rule is written on the bind rather than the caller's address.
        // The console line is the primary channel: a service or LXC operator reads it in the log,
        // and it is the only channel that works when the workspace is read-only.
        var setupSecret = SetupAuthority.Arm(
            AnthillRuntime.ApiHost,
            AnthillRuntime.RequireSetupToken,
            Queen.Memory.CountUsers() > 0,
            AnthillRuntime.WorkspaceRootPath);

        if (setupSecret.Length > 0)
        {
            Console.WriteLine("");
            Console.WriteLine("  ANTHILL FIRST-RUN SETUP TOKEN");
            Console.WriteLine($"    {setupSecret}");
            Console.WriteLine("  No administrator exists yet and this instance is reachable from the");
            Console.WriteLine("  network, so creating the first account requires this token. Single use.");
            if (SetupAuthority.SecretPath.Length > 0)
                Console.WriteLine($"  Also written to: {SetupAuthority.SecretPath}");
            Console.WriteLine("");
        }
#if MICROMOUND
        if (AnthillRuntime.EnableMicromound)
            InitMicromound(); // MICROMOUND M1 (read-only; see Micromound/ApiHost.Micromound.cs)
#endif

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
#if MICROMOUND
        if (AnthillRuntime.EnableMicromound) MapMicromoundEndpoints(app);
#endif
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

        // v0.3.8.48: schedules execute while the HOST runs — started here, said plainly in the
        // UI, and never claimed to be a cloud.
        Queen.Scheduler.Start();
        Console.WriteLine("Project scheduler started (runs execute while this host is running).");

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

    private static string LoadUi() => LoadUiAsset("index.html", "<h1>ANTHILL</h1><p>UI resource missing.</p>");

    /// <summary>
    /// Reads an embedded UI asset (index.html, app.js) by resource-name suffix.
    ///
    /// v3.8.17 — internal rather than private so <c>UiAbsenceTests</c> can assert phase 6's exit
    /// gate: the API serves when the console assets are absent. That gate was written as a manual
    /// step ("boot the API with the UI assets absent"), which is a step nobody performs twice. The
    /// degradation is right here in one method, so it can be a test instead.
    /// </summary>
    internal static string LoadUiAsset(string suffix, string fallback = "")
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

/// <summary>
/// v0.3.8.38: `IdempotencyKey` is accepted here AND as the `Idempotency-Key` header.
///
/// `ApiJobRegistry.Submit(goal, idempotencyKey)` and the durable store's insert-or-replay have
/// supported this since v2.8.0, and `POST /missions` never passed one — so the protection existed,
/// was tested, and could not be reached. A client whose request timed out and retried submitted the
/// mission twice.
/// </summary>
public sealed class MissionRequest
{
    public string Goal { get; set; } = "";
    public string? IdempotencyKey { get; set; }
}

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
    /// <summary>v0.3.8.47: an existing project to join. Absent = a new project is created.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }
}

public sealed class AttachmentBody
{
    public string? Filename { get; set; }
    public string? Content { get; set; }
}

/// <summary>v0.3.8.48: one role's route. Both halves required; nothing else is touched.</summary>
public sealed class RouteBody
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
}

/// <summary>v0.3.8.48: create or update a project schedule. Null on PATCH = leave unchanged.</summary>
public sealed class ScheduleRequest
{
    public string? Name { get; set; }
    public string? Prompt { get; set; }
    public string? Trigger { get; set; }
    public string? Cron { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("one_time_at")]
    public string? OneTimeAt { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("local_time")]
    public string? LocalTime { get; set; }
    public string? Timezone { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("approval_mode")]
    public string? ApprovalMode { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public bool? Enabled { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("overlap_policy")]
    public string? OverlapPolicy { get; set; }
}

/// <summary>v0.3.8.47: import a transcript. Turns become history; nothing is invented for them.</summary>
public sealed class ImportRequest
{
    public string? Title { get; set; }
    public List<ImportTurn>? Turns { get; set; }
}
public sealed class ImportTurn
{
    public string? Role { get; set; }
    public string? Content { get; set; }
}

/// <summary>v0.3.8.47: create or update a project. Null fields on PATCH mean "leave unchanged".</summary>
public sealed class ProjectRequest
{
    public string? Name { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("description_md")]
    public string? DescriptionMd { get; set; }
    public string? Path { get; set; }
    public bool? Archived { get; set; }
    /// <summary>v0.3.8.48: ask | autoapprove | bypass. Attributed to the caller when changed.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("default_policy")]
    public string? DefaultPolicy { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("default_provider")]
    public string? DefaultProvider { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("default_model")]
    public string? DefaultModel { get; set; }
}

/// <summary>v3.7.0: one turn, with the operator's answers for anything it needs permission to do.</summary>
public sealed class TurnRequest
{
    public string? Message { get; set; }
    /// <summary>chat | mission. Mission escalation is gated; chat is not.</summary>
    public string? Mode { get; set; }
    /// <summary>Action name to "approve". Absence is NOT consent.</summary>
    public Dictionary<string, string>? Answers { get; set; }
    /// <summary>v0.3.8.47: text files handed to this turn. Capped and text-only, enforced loudly.</summary>
    public List<AttachmentBody>? Attachments { get; set; }
    /// <summary>v0.3.8.44: deliver the reply as SSE deltas while it is produced. The recorded turn
    /// and the final outcome are identical either way — streaming is presentation, not contract.</summary>
    public bool Stream { get; set; }
}
public sealed class LoginRequest { public string? Username { get; set; } public string? Password { get; set; } }

/// <summary>
/// First-run setup: credentials plus the bootstrap token. v0.3.8.91.
///
/// A separate type from <see cref="LoginRequest"/> on purpose. The token is meaningful on exactly
/// one endpoint, and putting it on the login shape would offer every caller a field that does
/// nothing — which is how a credential ends up in a log line somebody thought was a login.
/// </summary>
public sealed class SetupRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("setup_token")]
    public string? SetupToken { get; set; }
}
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
    /// <summary>v0.3.8.48: the owning project. A named project must exist; absence = unassigned.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }
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
