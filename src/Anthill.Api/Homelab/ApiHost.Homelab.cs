using Anthill.Core.Configuration;
using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Scheduling;
using Anthill.Core.Homelab.Security;
using Anthill.Core.Integrations;

namespace Anthill.Api;

/// <summary>
/// Homelab foundation endpoints (v1.9.0, NORTH_STAR Phase 4). Read-only visibility plus
/// operator-managed configuration (target allowlist, write-only credentials). There are NO
/// infrastructure action endpoints in this file by design — actions arrive in V2.1 behind
/// IApprovable proposals, approval permissions, and the HOMELAB_STOP kill switch.
/// Permissions: reads require read_homelab; configuration writes require
/// manage_homelab_integrations. Secrets are never returned by any endpoint.
/// </summary>
public static partial class ApiHost
{
    public static HomelabRepository Homelab { get; private set; } = null!;
    public static HomelabCredentialStore HomelabCredentials { get; private set; } = null!;
    public static HomelabTargetGuard HomelabTargets { get; private set; } = null!;
    public static HomelabScheduler HomelabJobs { get; private set; } = null!;

    private sealed record AllowlistUpsertRequest(string? Target, string? Note, bool? Enabled);
    private sealed record CredentialUpsertRequest(string? Id, string? Kind, string? TargetHost, string? Secret);
    private sealed record NodeUpsertRequest(string? Id, string? Name, string? Kind, string? Address, string? Os, List<string>? RoleTags, string? Notes);
    private sealed record ServiceUpsertRequest(string? Id, string? Name, string? NodeId, string? Url, List<int>? Ports, string? Protocol, string? Owner, string? Criticality, bool? InternetExposed, string? Notes);

    public static IReadOnlyList<FakeHomelabProvider> HomelabProviders { get; private set; } = Array.Empty<FakeHomelabProvider>();

    private static void InitHomelab()
    {
        Homelab = new HomelabRepository();
        HomelabCredentials = new HomelabCredentialStore(Homelab);
        HomelabTargets = new HomelabTargetGuard(Homelab);
        HomelabJobs = new HomelabScheduler(Homelab, AnthillRuntime.HomelabMaxConcurrentChecks);

        // v1.9.1: the mock-provider harness — the shared execution pattern every real provider
        // (v1.10+) follows. Mocks are deterministic and network-free; they only run when BOTH
        // homelab_scheduler_enabled AND homelab_mock_providers_enabled are true (off by default).
        HomelabProviders = new FakeHomelabProvider[]
        {
            new FakeProxmoxProvider(Homelab, HomelabTargets),
            new FakeDnsProvider(Homelab, HomelabTargets),
            new FakeDhcpProvider(Homelab, HomelabTargets),
            new FakeFirewallProvider(Homelab, HomelabTargets),
            new FakeHealthProvider(Homelab, HomelabTargets),
        };
        foreach (var provider in HomelabProviders)
            HomelabJobs.Register(new HomelabScheduledJob(provider.Name, TimeSpan.FromMinutes(5), provider.RunAsync));

        if (AnthillRuntime.EnableHomelabScheduler && AnthillRuntime.EnableHomelabMockProviders)
        {
            HomelabJobs.Start();
            Console.WriteLine($"Homelab scheduler started with {HomelabProviders.Count} mock providers (no real network calls).");
        }
        else if (AnthillRuntime.EnableHomelabScheduler)
        {
            Console.WriteLine("Homelab scheduler gate is enabled but mock providers are off (homelab_mock_providers_enabled=false); no jobs running.");
        }
    }

    private static void MapHomelabEndpoints(WebApplication app)
    {
        // ---- Summary (read_homelab) ---------------------------------------------------------

        app.MapGet("/homelab/summary", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_homelab"); if (auth is not null) return auth;
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["enabled"] = AnthillRuntime.EnableHomelab,
                ["scheduler_enabled"] = AnthillRuntime.EnableHomelabScheduler,
                ["scheduler_running"] = HomelabJobs.Running,
                ["providers"] = HomelabProviders.Select(p => p.Status()).ToList(),
                ["table_counts"] = Homelab.TableCounts(),
                ["allowlist_entries"] = Homelab.ListAllowlist().Count,
                ["credentials"] = HomelabCredentials.ListStatuses(), // secret-free by construction
                ["recent_events"] = Homelab.RecentEvents(10),
                ["recent_changes"] = Homelab.RecentChanges(10),
            });
        });

        // ---- Inventory (read_homelab; manual registration needs manage_homelab_integrations) --

        app.MapGet("/homelab/hosts", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(Homelab.ListNodes()));

        app.MapPost("/homelab/hosts", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            NodeUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<NodeUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.Name)) return ApiJson.Error("Node name is required.", "bad_request");
            var node = new HomelabNode
            {
                Id = string.IsNullOrWhiteSpace(body.Id) ? Guid.NewGuid().ToString() : body.Id!.Trim(),
                Name = body.Name!.Trim(), Kind = (body.Kind ?? "host").Trim(),
                Address = (body.Address ?? "").Trim(), Os = (body.Os ?? "").Trim(),
                RoleTags = body.RoleTags ?? new(), Notes = (body.Notes ?? "").Trim(),
            };
            Homelab.UpsertNode(node, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(node, $"Node '{node.Name}' saved.");
        });

        app.MapGet("/homelab/services", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(Homelab.ListServices()));

        app.MapPost("/homelab/services", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            ServiceUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ServiceUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.Name)) return ApiJson.Error("Service name is required.", "bad_request");
            var service = new ServiceRecord
            {
                Id = string.IsNullOrWhiteSpace(body.Id) ? Guid.NewGuid().ToString() : body.Id!.Trim(),
                Name = body.Name!.Trim(), NodeId = (body.NodeId ?? "").Trim(), Url = (body.Url ?? "").Trim(),
                Ports = body.Ports ?? new(), Protocol = (body.Protocol ?? "").Trim(),
                Owner = (body.Owner ?? "").Trim(), Criticality = (body.Criticality ?? "normal").Trim(),
                InternetExposed = body.InternetExposed ?? false, Notes = (body.Notes ?? "").Trim(),
            };
            Homelab.UpsertService(service, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(service, $"Service '{service.Name}' saved.");
        });

        app.MapGet("/homelab/events", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(Homelab.RecentEvents(50)));

        // v1.9.1: secret-free provider statuses (mock harness now; real providers from v1.10+).
        app.MapGet("/homelab/providers", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(HomelabProviders.Select(p => p.Status()).ToList()));

        app.MapGet("/homelab/changes", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(Homelab.RecentChanges(50)));

        // ---- Target allowlist (D1) -------------------------------------------------------------

        app.MapGet("/homelab/allowlist", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(Homelab.ListAllowlist()));

        app.MapPost("/homelab/allowlist", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            AllowlistUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<AllowlistUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.Target)) return ApiJson.Error("Target (hostname, IP, or IPv4 CIDR) is required.", "bad_request");
            var entry = new TargetAllowlistRecord
            {
                Target = body.Target!.Trim(), Note = (body.Note ?? "").Trim(),
                Enabled = body.Enabled ?? true, AddedBy = CurrentUsername(ctx) ?? "operator",
            };
            Homelab.AddAllowlistEntry(entry);
            return ApiJson.Ok(Homelab.ListAllowlist(), $"Allowlist target '{entry.Target}' added. This affects deterministic homelab providers only — the general SSRF guard for AI tools is unchanged.");
        });

        app.MapDelete("/homelab/allowlist/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            Homelab.RemoveAllowlistEntry(id, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(Homelab.ListAllowlist(), "Allowlist entry removed.");
        });

        // ---- Credentials (D2) — write-only secrets, secret-free statuses -------------------------

        app.MapGet("/homelab/credentials", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(HomelabCredentials.ListStatuses()));

        app.MapPost("/homelab/credentials", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            CredentialUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<CredentialUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.Id)) return ApiJson.Error("Credential id is required.", "bad_request");
            if (string.IsNullOrWhiteSpace(body.Secret)) return ApiJson.Error("Credential secret is required.", "bad_request");
            HomelabCredentials.SaveCredential(body.Id!, body.Kind ?? "other", body.TargetHost ?? "", body.Secret!, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(HomelabCredentials.ListStatuses(), $"Credential '{body.Id!.Trim().ToLowerInvariant()}' saved. Secrets are write-only and never returned.");
        });

        app.MapDelete("/homelab/credentials/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            HomelabCredentials.RemoveCredential(id, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(HomelabCredentials.ListStatuses(), "Credential removed.");
        });
    }
}
