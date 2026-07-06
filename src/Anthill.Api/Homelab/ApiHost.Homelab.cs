using Anthill.Core.Configuration;
using Anthill.Core.Health;
using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Notifications;
using Anthill.Core.Homelab.Scheduling;
using Anthill.Core.Homelab.Security;
using Anthill.Core.Integrations;
using Anthill.Core.Integrations.Proxmox;

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
    private sealed record DependencyUpsertRequest(string? Id, string? FromKind, string? FromId, string? ToKind, string? ToId, string? DependencyKind, string? Notes);
    private sealed record HealthScheduleUpsertRequest(string? Id, string? CheckKind, string? Target, string? ServiceId, string? NodeId, bool? Enabled, int? TimeoutMs);

    public static IReadOnlyList<FakeHomelabProvider> HomelabProviders { get; private set; } = Array.Empty<FakeHomelabProvider>();
    public static NotificationService HomelabNotifier { get; private set; } = null!;
    public static HealthCheckRunner HomelabHealth { get; private set; } = null!;
    public static ProxmoxInventoryProvider? HomelabProxmox { get; private set; }

    private static void InitHomelab()
    {
        Homelab = new HomelabRepository();
        HomelabCredentials = new HomelabCredentialStore(Homelab);
        HomelabTargets = new HomelabTargetGuard(Homelab);
        HomelabJobs = new HomelabScheduler(Homelab, AnthillRuntime.HomelabMaxConcurrentChecks);
        HomelabNotifier = new NotificationService(Homelab);
        HomelabHealth = new HealthCheckRunner(Homelab, HomelabTargets, HomelabNotifier);

        // v1.9.1: the mock-provider harness — the shared execution pattern every real provider
        // follows. Mocks are deterministic and network-free; registered only when the mock gate
        // is on (homelab_mock_providers_enabled, off by default).
        if (AnthillRuntime.EnableHomelabMockProviders)
        {
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
        }

        // v1.11.0: real health checks ride the same scheduler (NORTH_STAR: one scheduler, no
        // per-subsystem timers). Gated by homelab_enabled; checks themselves only touch hosts on
        // the target allowlist and run under strict timeouts.
        if (AnthillRuntime.EnableHomelab)
            HomelabJobs.Register(new HomelabScheduledJob("health-checks",
                TimeSpan.FromSeconds(AnthillRuntime.HomelabHealthIntervalSeconds), HomelabHealth.RunAllAsync));

        // v1.12.0: Proxmox read-only sync. GET-only client; token pulled from the credential
        // store per run (never cached in config); host must be on the target allowlist.
        if (AnthillRuntime.EnableHomelab && AnthillRuntime.EnableHomelabProxmox
            && !string.IsNullOrWhiteSpace(AnthillRuntime.HomelabProxmoxHost))
        {
            var pveClient = new ProxmoxApiClient(
                AnthillRuntime.HomelabProxmoxHost, AnthillRuntime.HomelabProxmoxPort, HomelabTargets,
                () => HomelabCredentials.GetSecret(AnthillRuntime.HomelabProxmoxCredentialId, usedBy: "ProxmoxInventoryProvider"),
                AnthillRuntime.HomelabProxmoxInsecureTls);
            HomelabProxmox = new ProxmoxInventoryProvider(pveClient, Homelab);
            HomelabJobs.Register(new HomelabScheduledJob("proxmox-sync",
                TimeSpan.FromSeconds(AnthillRuntime.HomelabProxmoxSyncIntervalSeconds), HomelabProxmox.SyncInventoryAsync));
        }

        if (AnthillRuntime.EnableHomelabScheduler && HomelabJobs.Jobs.Count > 0)
        {
            HomelabJobs.Start();
            Console.WriteLine($"Homelab scheduler started with {HomelabJobs.Jobs.Count} job(s)"
                + (AnthillRuntime.EnableHomelabMockProviders ? $" incl. {HomelabProviders.Count} mock provider(s)" : "")
                + (AnthillRuntime.EnableHomelab ? " incl. health-checks" : "") + ".");
        }
        else if (AnthillRuntime.EnableHomelabScheduler)
        {
            Console.WriteLine("Homelab scheduler gate is enabled but no jobs are registered (enable homelab_enabled and/or homelab_mock_providers_enabled).");
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

        // v1.10.0 (NORTH_STAR Phase 6): explicit-id updates, dependency mapping, import/export.

        app.MapPut("/homelab/hosts/{id}", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            NodeUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<NodeUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.Name)) return ApiJson.Error("Node name is required.", "bad_request");
            var node = new HomelabNode
            {
                Id = id.Trim(), Name = body.Name!.Trim(), Kind = (body.Kind ?? "host").Trim(),
                Address = (body.Address ?? "").Trim(), Os = (body.Os ?? "").Trim(),
                RoleTags = body.RoleTags ?? new(), Notes = (body.Notes ?? "").Trim(),
            };
            Homelab.UpsertNode(node, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(node, $"Node '{node.Name}' updated.");
        });

        app.MapPut("/homelab/services/{id}", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            ServiceUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<ServiceUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.Name)) return ApiJson.Error("Service name is required.", "bad_request");
            var service = new ServiceRecord
            {
                Id = id.Trim(), Name = body.Name!.Trim(), NodeId = (body.NodeId ?? "").Trim(),
                Url = (body.Url ?? "").Trim(), Ports = body.Ports ?? new(), Protocol = (body.Protocol ?? "").Trim(),
                Owner = (body.Owner ?? "").Trim(), Criticality = (body.Criticality ?? "normal").Trim(),
                InternetExposed = body.InternetExposed ?? false, Notes = (body.Notes ?? "").Trim(),
            };
            Homelab.UpsertService(service, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(service, $"Service '{service.Name}' updated.");
        });

        app.MapGet("/homelab/dependencies", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(Homelab.ListDependencies()));

        app.MapPost("/homelab/dependencies", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            DependencyUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<DependencyUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.FromId) || string.IsNullOrWhiteSpace(body?.ToId))
                return ApiJson.Error("FromId and ToId are required.", "bad_request");
            var dependency = new DependencyRecord
            {
                Id = string.IsNullOrWhiteSpace(body.Id) ? Guid.NewGuid().ToString() : body.Id!.Trim(),
                FromKind = (body.FromKind ?? "service").Trim(), FromId = body.FromId!.Trim(),
                ToKind = (body.ToKind ?? "host").Trim(), ToId = body.ToId!.Trim(),
                DependencyKind = (body.DependencyKind ?? "runs_on").Trim(), Notes = (body.Notes ?? "").Trim(),
            };
            Homelab.UpsertDependency(dependency, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(Homelab.ListDependencies(), "Dependency saved.");
        });

        app.MapDelete("/homelab/dependencies/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            Homelab.RemoveDependency(id, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(Homelab.ListDependencies(), "Dependency removed.");
        });

        app.MapGet("/homelab/export", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(Homelab.ExportInventory(), "Inventory export (nodes, services, dependencies — never secrets)."));

        app.MapPost("/homelab/import", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            HomelabInventoryExport? bundle;
            try { bundle = await ctx.Request.ReadFromJsonAsync<HomelabInventoryExport>(); }
            catch { return ApiJson.Error("Invalid inventory bundle.", "bad_request"); }
            if (bundle is null) return ApiJson.Error("Invalid inventory bundle.", "bad_request");
            var (nodes, services, deps) = Homelab.ImportInventory(bundle, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(new Dictionary<string, object?> { ["nodes"] = nodes, ["services"] = services, ["dependencies"] = deps },
                $"Imported {nodes} node(s), {services} service(s), {deps} dependency(ies).");
        });

        app.MapGet("/homelab/events", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(Homelab.RecentEvents(50)));

        // v1.9.1: secret-free provider statuses (mock harness now; real providers from v1.10+).
        app.MapGet("/homelab/providers", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(HomelabProviders.Select(p => p.Status()).ToList()));

        // ---- Proxmox read-only integration (v1.12.0, NORTH_STAR Phase 8) --------------------------

        app.MapGet("/homelab/vms", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(Homelab.ListVms()));

        app.MapGet("/homelab/containers", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(Homelab.ListContainers()));

        app.MapGet("/homelab/storage", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(Homelab.ListStoragePools()));

        app.MapGet("/homelab/proxmox/status", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_homelab"); if (auth is not null) return auth;
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["enabled"] = AnthillRuntime.EnableHomelabProxmox,
                ["host"] = AnthillRuntime.HomelabProxmoxHost,
                ["credential_id"] = AnthillRuntime.HomelabProxmoxCredentialId, // id only — never the token
                ["credential_configured"] = HomelabCredentials.ListStatuses()
                    .Any(c => c.Id == AnthillRuntime.HomelabProxmoxCredentialId.Trim().ToLowerInvariant() && c.Configured),
                ["status"] = HomelabProxmox?.GetStatus(),
                ["vms"] = Homelab.ListVms().Count,
                ["containers"] = Homelab.ListContainers().Count,
                ["storage_pools"] = Homelab.ListStoragePools().Count,
                ["read_only"] = true, // structural: the client has no write methods
            });
        });

        app.MapPost("/homelab/proxmox/sync", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            if (HomelabProxmox is null)
                return ApiJson.Error("Proxmox integration is not active — set homelab_enabled, homelab_proxmox_enabled, and homelab_proxmox_host, then restart.", "disabled");
            var result = await HomelabProxmox.SyncInventoryAsync(ctx.RequestAborted);
            return result.Ok
                ? ApiJson.Ok(new Dictionary<string, object?> { ["items"] = result.ItemCount }, result.Message)
                : ApiJson.Error("Proxmox sync failed: " + result.Message, "sync_failed");
        });

        // ---- Health checks + notifications (v1.11.0, NORTH_STAR Phase 7) --------------------------

        app.MapGet("/homelab/health/summary", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(HomelabHealth.Summarize()));

        app.MapGet("/homelab/health/results", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(Homelab.RecentHealthResults(100)));

        app.MapGet("/homelab/health/schedules", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(Homelab.ListHealthSchedules()));

        app.MapPost("/homelab/health/schedules", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            HealthScheduleUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<HealthScheduleUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.Target)) return ApiJson.Error("Target is required (host, host:port, or URL depending on kind).", "bad_request");
            var schedule = new HealthCheckSchedule
            {
                Id = string.IsNullOrWhiteSpace(body.Id) ? Guid.NewGuid().ToString() : body.Id!.Trim(),
                CheckKind = (body.CheckKind ?? "http").Trim().ToLowerInvariant(),
                Target = body.Target!.Trim(), ServiceId = (body.ServiceId ?? "").Trim(),
                NodeId = (body.NodeId ?? "").Trim(), Enabled = body.Enabled ?? true,
                TimeoutMs = Math.Clamp(body.TimeoutMs ?? 0, 0, 60000),
            };
            Homelab.UpsertHealthSchedule(schedule, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(Homelab.ListHealthSchedules(), $"Health check '{schedule.CheckKind} {schedule.Target}' saved. The target host must be on the homelab allowlist to actually run.");
        });

        app.MapDelete("/homelab/health/schedules/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            Homelab.RemoveHealthSchedule(id, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(Homelab.ListHealthSchedules(), "Health check schedule removed.");
        });

        app.MapPost("/homelab/health/run", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            var result = await HomelabHealth.RunAllAsync(ctx.RequestAborted);
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["ok"] = result.Ok, ["message"] = result.Message,
                ["summary"] = HomelabHealth.Summarize(),
            }, result.Ok ? "Health checks completed." : $"Health checks completed with failures: {result.Message}");
        });

        app.MapPost("/homelab/notifications/test", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
            if (!NotificationService.Enabled)
                return ApiJson.Error("Notifications are disabled — set homelab_notifications_enabled=true and configure a webhook first.", "disabled");
            var delivered = await HomelabNotifier.SendAsync(new Anthill.Core.Health.AlertRecord
            {
                Kind = "test", Severity = "info",
                Message = $"ANTHILL v{AnthillRuntime.Version} notification test from {CurrentUsername(ctx) ?? "operator"}",
            }, ctx.RequestAborted);
            return delivered > 0
                ? ApiJson.Ok(new Dictionary<string, object?> { ["delivered"] = delivered }, $"Test alert delivered to {delivered} webhook(s).")
                : ApiJson.Error("No webhook accepted the test alert — check the configured URLs (see homelab events for the audit trail).", "delivery_failed");
        });

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
