using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Scheduling;
using Anthill.Core.Integrations.Arr;

namespace Anthill.Api;

/// <summary>
/// v2.3.3 — *arr-stack integration endpoints (Homarr-style apps) + node metrics. Reads need
/// read_homelab; configuration writes need manage_homelab_integrations. API keys go through the
/// credential store (write-only; referenced by id) and are never returned by any endpoint.
/// </summary>
public static partial class ApiHost
{
    public static ArrSyncProvider HomelabArr { get; private set; } = null!;

    private sealed record ArrUpsertRequest(string? Id, string? Kind, string? Name, string? Url, string? ApiKey, bool? Enabled);

    private static void InitHomelabArr()
    {
        HomelabArr = new ArrSyncProvider(Homelab, HomelabTargets,
            credId => HomelabCredentials.GetSecret(credId, usedBy: "ArrSyncProvider"));
        if (AnthillRuntime.EnableHomelab)
            HomelabJobs.Register(new HomelabScheduledJob("arr-sync",
                TimeSpan.FromSeconds(AnthillRuntime.HomelabArrSyncIntervalSeconds), HomelabArr.RunAsync));
    }

    private static void MapHomelabArrEndpoints(WebApplication app)
    {
        // ---- Node metrics (v2.3.3: deck CPU/RAM/storage bars) ---------------------------------
        app.MapGet("/homelab/metrics/nodes", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(Homelab.ListNodeMetrics()));

        // ---- *arr apps ------------------------------------------------------------------------
        app.MapGet("/homelab/arr", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_homelab"); if (auth is not null) return auth;
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["items"] = Homelab.ListArrApps(), // credential ids only — never secrets
                ["kinds"] = ArrClient.Kinds.Keys.OrderBy(k => k).ToList(),
            });
        });

        app.MapPost("/homelab/arr", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
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
            var existing = Homelab.ListArrApps().FirstOrDefault(a => a.Id == record.Id);
            if (existing is not null && string.IsNullOrWhiteSpace(body.ApiKey)) record.CredentialId = existing.CredentialId;
            else if (string.IsNullOrWhiteSpace(body.ApiKey)) return ApiJson.Error("An API key is required for a new app (stored write-only in the credential store).", "bad_request");
            else HomelabCredentials.SaveCredential(record.CredentialId, "api_key", uri.Host, body.ApiKey!, by);

            Homelab.UpsertArrApp(record);
            Homelab.RecordChange(new ChangeRecord { SubjectKind = "arr_app", SubjectId = record.Id, ChangeKind = existing is null ? "created" : "updated", Summary = $"{record.Kind} '{record.Name}' @ {uri.Host}", ChangedBy = by });
            var hint = HomelabTargets.IsAllowed(uri.Host) ? "" : $" NOTE: '{uri.Host}' is not on the homelab allowlist yet — add it or the sync will refuse to connect.";
            return ApiJson.Ok(Homelab.ListArrApps(), $"{record.Kind} '{record.Name}' saved.{hint}");
        });

        app.MapDelete("/homelab/arr/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            var existing = Homelab.ListArrApps().FirstOrDefault(a => a.Id == id);
            if (existing is not null && existing.CredentialId.StartsWith("arr-"))
                HomelabCredentials.RemoveCredential(existing.CredentialId, CurrentUsername(ctx) ?? "operator");
            Homelab.RemoveArrApp(id, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(Homelab.ListArrApps(), "App removed (its stored API key was deleted too).");
        });

        app.MapPost("/homelab/arr/sync", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            var result = await HomelabArr.RunAsync(ctx.RequestAborted);
            return result.Ok ? ApiJson.Ok(Homelab.ListArrApps(), result.Message)
                             : ApiJson.Error(result.Message, "sync_failed");
        });
    }
}
