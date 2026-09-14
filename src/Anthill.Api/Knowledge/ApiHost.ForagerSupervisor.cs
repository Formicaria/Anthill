using Anthill.Core.Configuration;
using Anthill.SDK.Knowledge;

namespace Anthill.Api;

/// <summary>
/// THE HOST WIRING FOR MANAGED ENGINE SUPERVISION — W3-03.
///
/// <see cref="ForagerSupervisor"/> is the machine; this partial is where the composition root turns
/// it on, feeds it the colony's settings and log channel, hands the knowledge module the endpoint
/// and credential it discovers, and exposes what it is doing on an API route. Kept beside
/// <c>ApiHost.Knowledge.cs</c> because it is the other half of the same integration: that file is
/// how the colony READS knowledge, this is how — in managed mode — the colony RUNS the engine that
/// holds it.
/// </summary>
public static partial class ApiHost
{
    /// <summary>
    /// The managed engine, or null before the host has built it. Held like <see cref="KnowledgeHost"/>
    /// so the knowledge options delegate and the status route reach the one instance. Null in attached
    /// mode and in every test that does not stand up the supervisor.
    /// </summary>
    public static ForagerSupervisor? ForagerEngine { get; private set; }

    /// <summary>
    /// Build the supervisor. No process is started here — <see cref="ForagerSupervisor.StartAsync"/>
    /// does that on a background task after the colony is up, so a slow or failing engine never
    /// delays boot. Constructed unconditionally (it is cheap and inert until started); it only does
    /// anything when knowledge_forager_managed is on, which it reads live.
    /// </summary>
    private static ForagerSupervisor InitForagerSupervisor()
    {
        var stateDir = AnthillRuntime.PathFromScript(AnthillRuntime.DefaultWorkspace);
        ForagerEngine = new ForagerSupervisor(
            settings: () => AnthillRuntime.Knowledge,
            colonyStateDir: stateDir,
            log: (eventType, message, metadata) =>
                Queen?.Memory.LogEvent(
                    AnthillRuntime.SystemApiMissionId, eventType, message,
                    metadata: new Dictionary<string, object?>(metadata)),
            console: Console.WriteLine);
        return ForagerEngine;
    }

    /// <summary>
    /// Resolve which endpoint and credential the knowledge module should use. In attached mode
    /// (the default) it is exactly what the file says. In managed mode the supervisor's discovered
    /// loopback endpoint and per-start credential win when the engine is running, and while it is
    /// still starting the token is blanked so the module reports "starting" (a Null provider with a
    /// reason) rather than 401ing against a half-up engine. Called by <c>InitKnowledge</c>'s options
    /// delegate on every read, so a managed engine that comes up mid-run is picked up on the next
    /// call, and one that goes down falls back to reporting unavailable.
    /// </summary>
    internal static void ResolveKnowledgeTransport(
        KnowledgeSettings settings, ref string endpoint, ref string token, ref bool allowRemote)
    {
        if (!settings.Managed || ForagerEngine is null) return;

        var managedEndpoint = ForagerEngine.ManagedEndpoint;
        if (managedEndpoint.Length > 0)
        {
            endpoint = managedEndpoint;
            token = ForagerEngine.ManagedSecret;
            allowRemote = false; // a managed engine is always loopback; never widen it
        }
        else
        {
            // Managed but not running yet (or failed). Leave the endpoint as configured but blank
            // the token: an empty token makes the client's Unusable() report a missing credential,
            // and /knowledge/engine (below) carries the real reason — starting, or failed with a
            // message. Blanking beats pointing the module at a port nothing owns.
            token = "";
        }
    }

    /// <summary>
    /// <c>GET /knowledge/engine</c> — what the managed engine is doing. Separate from
    /// <c>/knowledge/status</c>, which answers "can I retrieve right now" for both modes; this
    /// answers "is the engine this host manages up, and if not, why", which is meaningful only in
    /// managed mode and is exactly the surface an operator needs while an engine is starting,
    /// backing off, or stopped at its restart ceiling. Read permission, like status.
    /// </summary>
    private static void MapForagerEngineEndpoints(WebApplication app)
    {
        app.MapGet("/knowledge/engine", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, KnowledgePermissions.Read); if (auth is not null) return auth;

            var engine = ForagerEngine;
            if (engine is null || !engine.IsManaged)
                return ApiJson.Ok(new Dictionary<string, object?>
                {
                    ["managed"] = false,
                    ["note"] = "This colony attaches to a FORAGER an operator runs; it does not manage one. "
                             + "Set knowledge_forager_managed to have the host start and supervise its own engine.",
                });

            var s = engine.Status();
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["managed"] = true,
                // Lower-cased state name is the stable wire value; the console maps it to copy.
                ["state"] = s.State.ToString().ToLowerInvariant(),
                ["endpoint"] = s.Endpoint,
                ["instance_id"] = s.InstanceId,
                ["generation"] = s.Generation,
                ["version"] = s.Version,
                // The reason is present exactly when the state is not Running — starting has none,
                // backoff/failed carry the last crash or refusal, which is the actionable half.
                ["reason"] = s.Reason,
                ["ready_since"] = s.ReadySince?.ToString("o"),
                ["restarts_in_window"] = s.RestartsInWindow,
            });
        });
    }
}
