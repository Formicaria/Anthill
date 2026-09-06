using System.Text.Json;
using Anthill.Modules.Infrastructure;
using Anthill.Modules.Infrastructure.Integrations;
using Anthill.Modules.Infrastructure.Integrations.Arr;

namespace Anthill.Api;

/// <summary>
/// v2.5.1 Console Refit R1 (docs/CONSOLE_REFIT.md) — the generic integration platform endpoints.
/// One surface for every IIntegrationDefinition kind: list (instances + catalog), configure,
/// remove, sync one, and read a typed widget payload with freshness. Reads need read_infrastructure;
/// configuration writes need manage_infrastructure_integrations. Secrets go through the credential store
/// (write-only, referenced by id) and are never returned. The legacy /infrastructure/arr endpoints stay
/// as a compatibility view over the same tables.
/// </summary>
public static partial class ApiHost
{
    private sealed record IntegrationUpsertRequest(
        string? Id, string? Kind, string? Name, string? Url, string? Secret, bool? Enabled);

    /// <summary>Credential ids this platform owns (deleted with the instance): intg- and legacy arr-.</summary>
    private static bool IsManagedCredential(string credentialId) =>
        credentialId.StartsWith("intg-") || credentialId.StartsWith("arr-");

    private static void MapInfrastructureIntegrationEndpoints(WebApplication app)
    {
        // ---- List: configured instances + the registered kind catalog -------------------------
        app.MapGet("/infrastructure/integrations", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_infrastructure"); if (auth is not null) return auth;
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["items"] = Infrastructure.ListIntegrationInstances(), // credential ids only — never secrets
                ["kinds"] = IntegrationCatalog.All.OrderBy(d => d.Kind).Select(d => new Dictionary<string, object?>
                {
                    ["kind"] = d.Kind, ["category"] = d.Category, ["auth_mode"] = d.AuthMode,
                    ["widget_kinds"] = d.WidgetKinds,
                }).ToList(),
            });
        });

        // ---- Configure (create or edit) -------------------------------------------------------
        app.MapPost("/infrastructure/integrations", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            IntegrationUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<IntegrationUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (body is null || string.IsNullOrWhiteSpace(body.Kind) || string.IsNullOrWhiteSpace(body.Url))
                return ApiJson.Error("Kind and Url are required.", "bad_request");
            var definition = IntegrationCatalog.Get(body.Kind.Trim());
            if (definition is null)
                return ApiJson.Error($"Unknown integration kind '{body.Kind}'. Registered: {string.Join(", ", IntegrationCatalog.All.Select(d => d.Kind).OrderBy(k => k))}.", "bad_request");
            if (!Uri.TryCreate(body.Url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                return ApiJson.Error("Url must be an absolute http(s) URL.", "bad_request");

            var by = CurrentUsername(ctx) ?? "operator";
            var record = new IntegrationInstanceRecord
            {
                Id = string.IsNullOrWhiteSpace(body.Id) ? Guid.NewGuid().ToString() : body.Id!.Trim(),
                Kind = definition.Kind,
                Name = string.IsNullOrWhiteSpace(body.Name) ? definition.Kind : body.Name!.Trim(),
                Url = body.Url!.Trim().TrimEnd('/'),
                Enabled = body.Enabled ?? true,
            };
            var existing = Infrastructure.ListIntegrationInstances().FirstOrDefault(i => i.Id == record.Id);
            if (definition.AuthMode == "none")
                record.CredentialId = existing?.CredentialId ?? "";
            else
            {
                record.CredentialId = $"intg-{record.Kind}-{(record.Id.Length >= 8 ? record.Id[..8] : record.Id)}";
                // Existing instance edited without a new secret keeps its credential.
                if (existing is not null && string.IsNullOrWhiteSpace(body.Secret)) record.CredentialId = existing.CredentialId;
                else if (string.IsNullOrWhiteSpace(body.Secret)) return ApiJson.Error($"A secret ({definition.AuthMode}) is required for a new {definition.Kind} integration (stored write-only in the credential store).", "bad_request");
                else InfrastructureCredentials.SaveCredential(record.CredentialId, definition.AuthMode, uri.Host, body.Secret!, by);
            }

            Infrastructure.UpsertIntegrationInstance(record);
            Infrastructure.RecordChange(new ChangeRecord { SubjectKind = "integration", SubjectId = record.Id, ChangeKind = existing is null ? "created" : "updated", Summary = $"{record.Kind} '{record.Name}' @ {uri.Host}", ChangedBy = by });
            EnsureHostAllowlisted(uri.Host, by, $"{record.Kind} integration '{record.Name}'"); // v2.4.2: adding it IS the intent
            return ApiJson.Ok(Infrastructure.ListIntegrationInstances(), $"{record.Kind} '{record.Name}' saved. Host '{uri.Host}' is on the allowlist; sync will pick it up.");
        });

        // ---- Remove (instance + its widget state + its managed credential) --------------------
        app.MapDelete("/infrastructure/integrations/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            var existing = Infrastructure.ListIntegrationInstances().FirstOrDefault(i => i.Id == id);
            if (existing is not null && IsManagedCredential(existing.CredentialId))
                InfrastructureCredentials.RemoveCredential(existing.CredentialId, CurrentUsername(ctx) ?? "operator");
            Infrastructure.RemoveIntegrationInstance(id, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(Infrastructure.ListIntegrationInstances(), "Integration removed (its stored secret was deleted too).");
        });

        // ---- Sync one -------------------------------------------------------------------------
        app.MapPost("/infrastructure/integrations/{id}/sync", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            var instance = Infrastructure.ListIntegrationInstances().FirstOrDefault(i => i.Id == id);
            if (instance is null) return ApiJson.Error($"No integration with id '{id}'.", "not_found");
            try
            {
                await InfrastructureArr.SyncOneAsync(instance, ctx.RequestAborted);
                return ApiJson.Ok(instance, $"{instance.Kind} '{instance.Name}' synced.");
            }
            catch (Exception ex)
            {
                return ApiJson.Error($"Sync failed: {ex.GetBaseException().Message}", "sync_failed");
            }
        });

        // ---- Read one widget payload (+ freshness) --------------------------------------------
        app.MapGet("/infrastructure/integrations/{id}/widgets/{kind}", (HttpContext ctx, string id, string kind) =>
        {
            var auth = RequireAuth(ctx, "read_infrastructure"); if (auth is not null) return auth;
            var state = Infrastructure.GetIntegrationState(id, kind);
            if (state is null) return ApiJson.Error($"No '{kind}' state for integration '{id}' (not synced yet, or the kind feeds no such widget).", "not_found");
            JsonElement payload;
            try { payload = JsonDocument.Parse(state.PayloadJson).RootElement.Clone(); }
            catch { payload = JsonSerializer.SerializeToElement<object?>(null); } // never a broken response over a broken row
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["integration_id"] = state.IntegrationId,
                ["widget_kind"] = state.WidgetKind,
                ["payload"] = payload,
                ["updated_at"] = state.UpdatedAt, // freshness — the widget runtime renders staleness from this
            });
        });
    }
}
