using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Modules.Infrastructure;
using Anthill.Modules.Infrastructure.Scheduling;
using Anthill.Modules.Infrastructure.Integrations.Arr;
using Anthill.Modules.Infrastructure.Integrations.Download;
using Anthill.Modules.Infrastructure.Integrations.MediaRequests;
using Anthill.Modules.Infrastructure.Integrations.Media;
using Anthill.Modules.Infrastructure.Integrations.Monitoring;

namespace Anthill.Api;

/// <summary>
/// v2.3.3 — *arr-stack integration endpoints (Homarr-style apps) + node metrics. Reads need
/// read_infrastructure; configuration writes need manage_infrastructure_integrations. API keys go through the
/// credential store (write-only; referenced by id) and are never returned by any endpoint.
/// </summary>
public static partial class ApiHost
{
    public static IntegrationSyncProvider InfrastructureArr { get; private set; } = null!;

    private sealed record ArrUpsertRequest(string? Id, string? Kind, string? Name, string? Url, string? ApiKey, bool? Enabled);

    private static void InitInfrastructureArr()
    {
        // v2.5.1 Console Refit R1: the *arr kinds register in the generic IntegrationCatalog and
        // the sync job generalizes to every registered kind (job name kept for meta continuity).
        ArrIntegrationDefinition.RegisterAll();
        // v2.5.5 Console Refit R5 Wave 1: download clients (qBittorrent/Transmission/Deluge/
        // SABnzbd/NZBGet) register into the same catalog; the generic sync job below sweeps them
        // too — no per-kind wiring, endpoints, or UI pages.
        DownloadIntegrationDefinition.RegisterAll();
        // v3.0.1 Homarr-parity: media-request servers (Overseerr/Jellyseerr) register into the same
        // catalog and are swept by the generic sync job — no per-kind endpoints or UI pages.
        OverseerrIntegrationDefinition.RegisterAll();
        PlexIntegrationDefinition.RegisterAll();
        UptimeKumaIntegrationDefinition.RegisterAll();
        InfrastructureArr = new IntegrationSyncProvider(Infrastructure, InfrastructureTargets,
            credId => InfrastructureCredentials.GetSecret(credId, usedBy: "IntegrationSyncProvider"));
        if (AnthillRuntime.EnableInfrastructure)
            InfrastructureJobs.Register(new InfrastructureScheduledJob("arr-sync",
                TimeSpan.FromSeconds(AnthillRuntime.InfrastructureArrSyncIntervalSeconds), InfrastructureArr.RunAsync));
    }

    private static void MapInfrastructureArrEndpoints(WebApplication app)
    {
        // ---- Node metrics (v2.3.3: deck CPU/RAM/storage bars) ---------------------------------
        app.MapGet("/infrastructure/metrics/nodes", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(Infrastructure.ListNodeMetrics()));

        // ---- *arr apps ------------------------------------------------------------------------
        app.MapGet("/infrastructure/arr", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_infrastructure"); if (auth is not null) return auth;
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["items"] = Infrastructure.ListArrApps(), // credential ids only — never secrets
                ["kinds"] = ArrClient.Kinds.Keys.OrderBy(k => k).ToList(),
            });
        });

        app.MapPost("/infrastructure/arr", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            ArrUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ArrUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (body is null || string.IsNullOrWhiteSpace(body.Kind) || string.IsNullOrWhiteSpace(body.Url))
                return ApiJson.Error("Kind and Url are required.", "bad_request");
            if (!ArrClient.Kinds.ContainsKey(body.Kind.Trim()))
                return ApiJson.Error($"Unknown kind '{body.Kind}'. Supported: {string.Join(", ", ArrClient.Kinds.Keys.OrderBy(k => k))}.", "bad_request");
            if (!Uri.TryCreate(body.Url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                return ApiJson.Error("Url must be an absolute http(s) URL.", "bad_request");

            var by = CurrentUsername(ctx) ?? "operator";
            var record = new ArrAppRecord
            {
                Id = string.IsNullOrWhiteSpace(body.Id) ? Guid.NewGuid().ToString() : body.Id!.Trim(),
                Kind = body.Kind!.Trim().ToLowerInvariant(),
                Name = string.IsNullOrWhiteSpace(body.Name) ? body.Kind!.Trim() : body.Name!.Trim(),
                Url = body.Url!.Trim().TrimEnd('/'),
                Enabled = body.Enabled ?? true,
            };
            record.CredentialId = $"arr-{record.Kind}-{record.Id[..8]}";
            // Existing app being edited without a new key keeps its credential.
            var existing = Infrastructure.ListArrApps().FirstOrDefault(a => a.Id == record.Id);
            if (existing is not null && string.IsNullOrWhiteSpace(body.ApiKey)) record.CredentialId = existing.CredentialId;
            else if (string.IsNullOrWhiteSpace(body.ApiKey)) return ApiJson.Error("An API key is required for a new app (stored write-only in the credential store).", "bad_request");
            else InfrastructureCredentials.SaveCredential(record.CredentialId, "api_key", uri.Host, body.ApiKey!, by);

            Infrastructure.UpsertArrApp(record);
            Infrastructure.RecordChange(new ChangeRecord { SubjectKind = "arr_app", SubjectId = record.Id, ChangeKind = existing is null ? "created" : "updated", Summary = $"{record.Kind} '{record.Name}' @ {uri.Host}", ChangedBy = by });
            EnsureHostAllowlisted(uri.Host, by, $"{record.Kind} app '{record.Name}'"); // v2.4.2: adding the app IS the intent
            return ApiJson.Ok(Infrastructure.ListArrApps(), $"{record.Kind} '{record.Name}' saved. Host '{uri.Host}' is on the allowlist; sync will pick it up.");
        });

        app.MapDelete("/infrastructure/arr/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            var existing = Infrastructure.ListArrApps().FirstOrDefault(a => a.Id == id);
            if (existing is not null && IsManagedCredential(existing.CredentialId))
                InfrastructureCredentials.RemoveCredential(existing.CredentialId, CurrentUsername(ctx) ?? "operator");
            Infrastructure.RemoveArrApp(id, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(Infrastructure.ListArrApps(), "App removed (its stored API key was deleted too).");
        });

        app.MapPost("/infrastructure/arr/sync", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            var result = await InfrastructureArr.RunAsync(ctx.RequestAborted);
            return result.Ok ? ApiJson.Ok(Infrastructure.ListArrApps(), result.Message)
                             : ApiJson.Error(result.Message, "sync_failed");
        });
    }
}
