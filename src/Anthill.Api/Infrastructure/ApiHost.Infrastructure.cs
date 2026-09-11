using Anthill.Core.Configuration;
using Anthill.Core.Shadow;
using Anthill.Modules.Infrastructure.Health;
using Anthill.Modules.Infrastructure;
using Anthill.Modules.Infrastructure.Approvals;
using Anthill.Modules.Infrastructure.Incidents;
using Anthill.Modules.Infrastructure.Notifications;
using Anthill.Modules.Infrastructure.Scheduling;
using Anthill.Modules.Infrastructure.Security;
using Anthill.Modules.Infrastructure.Integrations;
using Anthill.Modules.Infrastructure.Integrations.Proxmox;

namespace Anthill.Api;

/// <summary>
/// Infrastructure foundation endpoints (v1.9.0, NORTH_STAR Phase 4). Read-only visibility plus
/// operator-managed configuration (target allowlist, write-only credentials). There are NO
/// infrastructure action endpoints in this file by design — actions arrive in V2.1 behind
/// IApprovable proposals, approval permissions, and the INFRASTRUCTURE_STOP kill switch.
/// Permissions: reads require read_infrastructure; configuration writes require
/// manage_infrastructure_integrations. Secrets are never returned by any endpoint.
/// </summary>
public static partial class ApiHost
{
    public static InfrastructureRepository Infrastructure { get; private set; } = null!;
    public static InfrastructureCredentialStore InfrastructureCredentials { get; private set; } = null!;
    public static InfrastructureTargetGuard InfrastructureTargets { get; private set; } = null!;
    public static InfrastructureScheduler InfrastructureJobs { get; private set; } = null!;

    private sealed record AllowlistUpsertRequest(string? Id, string? Target, string? Kind, string? Note, bool? Enabled);
    private sealed record AllowlistBulkRequest(string? Action, List<string>? Ids); // v2.5.4 R4: enable | disable | remove
    /* v0.3.9.5 — `target_host` IS THE WIRE NAME, because that is what the console sends.
       Found by `RequestWireNameTests`, which pairs each console POST body with the record the
       route deserializes: the page posts `target_host`, this record spelled it `targetHost` under
       the default camelCase policy, and case-insensitive matching does not bridge an underscore —
       so `TargetHost` arrived null and every credential saved here was stored against an EMPTY
       host. Silently, with a success envelope, exactly as `knowledge_base` did.

       The rest of this module's records are correct as they stand: `infrastructure.js` sends
       camelCase everywhere else (`nodeId`, `fromKind`, `internetExposed`), which the default policy
       matches. This one field was the odd one out, and that is precisely the kind of thing a
       reviewer does not see and a scan does. */
    private sealed record CredentialUpsertRequest(
        string? Id, string? Kind,
        [property: System.Text.Json.Serialization.JsonPropertyName("target_host")] string? TargetHost,
        string? Secret);
    private sealed record NodeUpsertRequest(string? Id, string? Name, string? Kind, string? Address, string? Os, List<string>? RoleTags, string? Notes);
    private sealed record ServiceUpsertRequest(string? Id, string? Name, string? NodeId, string? Url, List<int>? Ports, string? Protocol, string? Owner, string? Criticality, bool? InternetExposed, string? Notes);
    private sealed record DependencyUpsertRequest(string? Id, string? FromKind, string? FromId, string? ToKind, string? ToId, string? DependencyKind, string? Notes);
    private sealed record HealthScheduleUpsertRequest(string? Id, string? CheckKind, string? Target, string? ServiceId, string? NodeId, bool? Enabled, int? TimeoutMs);
    private sealed record DeviceUpsertRequest(string? Id, string? Name, string? Kind, string? Mac, string? Ip, string? Vlan, bool? Known, string? Notes);
    private sealed record IncidentOpenRequest(string? Title, string? SubjectKind, string? SubjectId, string? Severity);
    private sealed record IncidentStatusRequest(string? Status, string? RootCause);

    public static IReadOnlyList<FakeInfrastructureProvider> InfrastructureProviders { get; private set; } = Array.Empty<FakeInfrastructureProvider>();
    public static NotificationService InfrastructureNotifier { get; private set; } = null!;
    public static HealthCheckRunner InfrastructureHealth { get; private set; } = null!;
    public static ProxmoxInventoryProvider? InfrastructureProxmox { get; private set; }
    public static RiskAnalyzer InfrastructureRisks { get; private set; } = null!;
    public static IncidentManager InfrastructureIncidents { get; private set; } = null!;

    private static void InitInfrastructure()
    {
        Infrastructure = new InfrastructureRepository();
        // v3.8.7 — the infrastructure joins the colony's live event stream.
        //
        // Its nineteen RecordEvent call sites have always been durable and never visible: an
        // operator watching a VM restart, a credential being used or an inventory drifting saw
        // nothing on the console stream until they went looking in a different panel. Wired to the
        // SAME bus the mission log publishes to, so one stream carries the whole colony.
        Infrastructure.EventBus = Queen.Events;
        InfrastructureCredentials = new InfrastructureCredentialStore(Infrastructure);
        InfrastructureTargets = new InfrastructureTargetGuard(Infrastructure);
        InfrastructureJobs = new InfrastructureScheduler(Infrastructure, AnthillRuntime.InfrastructureMaxConcurrentChecks);
        InfrastructureNotifier = new NotificationService(Infrastructure);
        InfrastructureHealth = new HealthCheckRunner(Infrastructure, InfrastructureTargets, InfrastructureNotifier);
        InfrastructureRisks = new RiskAnalyzer(Infrastructure);
        // v2.24.0 Phase E: shadow mode observes real incidents here — the composition root, where
        // the incident layer and colony memory both exist. Shadow NEVER executes; it records what
        // it would have done so the recommendation can be scored against what the operator
        // actually did. Gated off by default (`shadow_observation_enabled`).
        InfrastructureIncidents = new IncidentManager(Infrastructure, incident =>
            LiveIncidentObserver.Observe(Queen.Memory, Queen.Memory.LoadSkillRegistry(), incident));
        // v2.3.0 (NORTH_STAR Phase 12): the approval-gated action pipeline. Local + mock runners
        // only in this release; both action capability gates remain OFF by default (fail closed).
        InitInfrastructureActions();
        InitInfrastructureAutomation(); // v2.5.0 Phase 14 (needs InfrastructureActions, so after actions init)
        // v2.3.3: *arr-stack app sync (read-only, credential-store keys, allowlist-gated).
        InitInfrastructureArr();

        // v1.9.1: the mock-provider harness — the shared execution pattern every real provider
        // follows. Mocks are deterministic and network-free; registered only when the mock gate
        // is on (infrastructure_mock_providers_enabled, off by default).
        if (AnthillRuntime.EnableInfrastructureMockProviders)
        {
            InfrastructureProviders = new FakeInfrastructureProvider[]
            {
                new FakeProxmoxProvider(Infrastructure, InfrastructureTargets),
                new FakeDnsProvider(Infrastructure, InfrastructureTargets),
                new FakeDhcpProvider(Infrastructure, InfrastructureTargets),
                new FakeFirewallProvider(Infrastructure, InfrastructureTargets),
                new FakeHealthProvider(Infrastructure, InfrastructureTargets),
            };
            foreach (var provider in InfrastructureProviders)
                InfrastructureJobs.Register(new InfrastructureScheduledJob(provider.Name, TimeSpan.FromMinutes(5), provider.RunAsync));
        }

        // v1.11.0: real health checks ride the same scheduler (NORTH_STAR: one scheduler, no
        // per-subsystem timers). Gated by infrastructure_enabled; checks themselves only touch hosts on
        // the target allowlist and run under strict timeouts.
        if (AnthillRuntime.EnableInfrastructure)
        {
            InfrastructureJobs.Register(new InfrastructureScheduledJob("health-checks",
                TimeSpan.FromSeconds(AnthillRuntime.InfrastructureHealthIntervalSeconds), InfrastructureHealth.RunAllAsync));
            // v1.13.0: deterministic risk analysis over existing inventory — zero network I/O.
            InfrastructureJobs.Register(new InfrastructureScheduledJob("risk-analysis",
                TimeSpan.FromSeconds(AnthillRuntime.InfrastructureRiskIntervalSeconds), InfrastructureRisks.RunAsync));
            // v1.14.0: incident sweep — turns incident_candidate events into deduped incidents.
            InfrastructureJobs.Register(new InfrastructureScheduledJob("incident-sweep",
                TimeSpan.FromSeconds(AnthillRuntime.InfrastructureIncidentSweepSeconds), InfrastructureIncidents.SweepAsync));
            // v2.25.0: the fault-injection catalog runs on the shared scheduler (NORTH_STAR §6
            // rule 2 — no private timers) and every run is recorded, because the V3 threshold
            // "repeated fault-injection runs stable" is a property of recorded history. The run is
            // pure computation over the catalog + current skill registry: no network, no state
            // outside its own results table, safe at any cadence.
            InfrastructureJobs.Register(new InfrastructureScheduledJob("fault-injection",
                TimeSpan.FromHours(24), _ =>
                {
                    var report = ShadowSimulation.RunAll(Queen.Memory.LoadSkillRegistry());
                    Queen.Memory.SaveFaultInjectionRun(report);
                    return System.Threading.Tasks.Task.FromResult(InfrastructureProviderResult.Success(
                        $"fault injection: {report.Passed}/{report.Total} scenarios passed", report.Total));
                }));
        }

        // v1.12.0: Proxmox read-only sync. GET-only client; token pulled from the credential
        // store per run (never cached in config); host must be on the target allowlist.
        if (AnthillRuntime.EnableInfrastructure && AnthillRuntime.EnableInfrastructureProxmox
            && !string.IsNullOrWhiteSpace(AnthillRuntime.InfrastructureProxmoxHost))
        {
            var pveClient = new ProxmoxApiClient(
                AnthillRuntime.InfrastructureProxmoxHost, AnthillRuntime.InfrastructureProxmoxPort, InfrastructureTargets,
                () => InfrastructureCredentials.GetSecret(AnthillRuntime.InfrastructureProxmoxCredentialId, usedBy: "ProxmoxInventoryProvider"),
                AnthillRuntime.InfrastructureProxmoxInsecureTls,
                protocol: AnthillRuntime.InfrastructureProxmoxProtocol);
            InfrastructureProxmox = new ProxmoxInventoryProvider(pveClient, Infrastructure);
            InfrastructureJobs.Register(new InfrastructureScheduledJob("proxmox-sync",
                TimeSpan.FromSeconds(AnthillRuntime.InfrastructureProxmoxSyncIntervalSeconds), InfrastructureProxmox.SyncInventoryAsync));
        }

        // v2.1.0: the other read-only virtualization integrations (ESXi/vSphere, Docker, Hyper-V) ride the
        // same scheduler. Providers are built on demand from current config so a UI-edited connection works
        // without a restart; each client is read-only by construction.
        RegisterVirtJobs();

        if (AnthillRuntime.EnableInfrastructureScheduler && InfrastructureJobs.Jobs.Count > 0)
        {
            InfrastructureJobs.Start();
            Console.WriteLine($"Infrastructure scheduler started with {InfrastructureJobs.Jobs.Count} job(s)"
                + (AnthillRuntime.EnableInfrastructureMockProviders ? $" incl. {InfrastructureProviders.Count} mock provider(s)" : "")
                + (AnthillRuntime.EnableInfrastructure ? " incl. health-checks" : "") + ".");
        }
        else if (AnthillRuntime.EnableInfrastructureScheduler)
        {
            Console.WriteLine("Infrastructure scheduler gate is enabled but no jobs are registered (enable infrastructure_enabled and/or infrastructure_mock_providers_enabled).");
        }
    }

    /// <summary>
    /// v2.4.2: operator-initiated registrations auto-allowlist their host. Safe because every
    /// caller already passed the manage_infrastructure_integrations permission check — registering a
    /// host/app IS the declaration of intent, so the separate manual allowlist step was pure
    /// friction. Deliberately NOT called from provider sync paths: a sync must never widen D1.
    /// </summary>
    private static void EnsureHostAllowlisted(string hostOrIp, string by, string context)
    {
        var target = (hostOrIp ?? "").Trim();
        if (target.Length == 0 || InfrastructureTargets.IsAllowed(target)) return;
        Infrastructure.AddAllowlistEntry(new TargetAllowlistRecord
        {
            Target = target, Enabled = true, AddedBy = by,
            Note = $"auto-added when registering {context}",
        });
    }

    private static void MapInfrastructureEndpoints(WebApplication app)
    {
        // v2.1.0: unified read-only virtualization endpoints (Proxmox/ESXi/Docker/Hyper-V status + sync).
        MapVirtualizationEndpoints(app);

        // ---- Summary (read_infrastructure) ---------------------------------------------------------

        app.MapGet("/infrastructure/summary", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_infrastructure"); if (auth is not null) return auth;
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["enabled"] = AnthillRuntime.EnableInfrastructure,
                ["scheduler_enabled"] = AnthillRuntime.EnableInfrastructureScheduler,
                ["scheduler_running"] = InfrastructureJobs.Running,
                ["providers"] = InfrastructureProviders.Select(p => p.Status()).ToList(),
                ["table_counts"] = Infrastructure.TableCounts(),
                ["allowlist_entries"] = Infrastructure.ListAllowlist().Count,
                ["credentials"] = InfrastructureCredentials.ListStatuses(), // secret-free by construction
                ["recent_events"] = Infrastructure.RecentEvents(10),
                ["recent_changes"] = Infrastructure.RecentChanges(10),
            });
        });

        // ---- Inventory (read_infrastructure; manual registration needs manage_infrastructure_integrations) --

        app.MapGet("/infrastructure/hosts", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(Infrastructure.ListNodes()));

        app.MapPost("/infrastructure/hosts", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            NodeUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<NodeUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.Name)) return ApiJson.Error("Node name is required.", "bad_request");
            var node = new InfrastructureNode
            {
                Id = string.IsNullOrWhiteSpace(body.Id) ? Guid.NewGuid().ToString() : body.Id!.Trim(),
                Name = body.Name!.Trim(), Kind = (body.Kind ?? "host").Trim(),
                Address = (body.Address ?? "").Trim(), Os = (body.Os ?? "").Trim(),
                RoleTags = body.RoleTags ?? new(), Notes = (body.Notes ?? "").Trim(),
            };
            var hostBy = CurrentUsername(ctx) ?? "operator";
            Infrastructure.UpsertNode(node, hostBy);
            EnsureHostAllowlisted(node.Address, hostBy, $"host '{node.Name}'"); // v2.4.2
            return ApiJson.Ok(node, $"Node '{node.Name}' saved." + (node.Address.Length > 0 ? $" Host '{node.Address}' is on the allowlist." : ""));
        });

        app.MapGet("/infrastructure/services", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(Infrastructure.ListServices()));

        app.MapPost("/infrastructure/services", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
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
            Infrastructure.UpsertService(service, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(service, $"Service '{service.Name}' saved.");
        });

        // v1.10.0 (NORTH_STAR Phase 6): explicit-id updates, dependency mapping, import/export.

        app.MapPut("/infrastructure/hosts/{id}", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            NodeUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<NodeUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.Name)) return ApiJson.Error("Node name is required.", "bad_request");
            var node = new InfrastructureNode
            {
                Id = id.Trim(), Name = body.Name!.Trim(), Kind = (body.Kind ?? "host").Trim(),
                Address = (body.Address ?? "").Trim(), Os = (body.Os ?? "").Trim(),
                RoleTags = body.RoleTags ?? new(), Notes = (body.Notes ?? "").Trim(),
            };
            var hostBy2 = CurrentUsername(ctx) ?? "operator";
            Infrastructure.UpsertNode(node, hostBy2);
            EnsureHostAllowlisted(node.Address, hostBy2, $"host '{node.Name}'"); // v2.4.2
            return ApiJson.Ok(node, $"Node '{node.Name}' updated." + (node.Address.Length > 0 ? $" Host '{node.Address}' is on the allowlist." : ""));
        });

        app.MapPut("/infrastructure/services/{id}", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
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
            Infrastructure.UpsertService(service, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(service, $"Service '{service.Name}' updated.");
        });

        app.MapGet("/infrastructure/dependencies", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(Infrastructure.ListDependencies()));

        app.MapPost("/infrastructure/dependencies", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
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
            Infrastructure.UpsertDependency(dependency, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(Infrastructure.ListDependencies(), "Dependency saved.");
        });

        app.MapDelete("/infrastructure/dependencies/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            Infrastructure.RemoveDependency(id, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(Infrastructure.ListDependencies(), "Dependency removed.");
        });

        app.MapGet("/infrastructure/export", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(Infrastructure.ExportInventory(), "Inventory export (nodes, services, dependencies — never secrets)."));

        app.MapPost("/infrastructure/import", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            InfrastructureInventoryExport? bundle;
            try { bundle = await ctx.Request.ReadFromJsonAsync<InfrastructureInventoryExport>(); }
            catch { return ApiJson.Error("Invalid inventory bundle.", "bad_request"); }
            if (bundle is null) return ApiJson.Error("Invalid inventory bundle.", "bad_request");
            var (nodes, services, deps) = Infrastructure.ImportInventory(bundle, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(new Dictionary<string, object?> { ["nodes"] = nodes, ["services"] = services, ["dependencies"] = deps },
                $"Imported {nodes} node(s), {services} service(s), {deps} dependency(ies).");
        });

        app.MapGet("/infrastructure/events", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(Infrastructure.RecentEvents(50)));

        // v1.9.1: secret-free provider statuses (mock harness now; real providers from v1.10+).
        app.MapGet("/infrastructure/providers", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(InfrastructureProviders.Select(p => p.Status()).ToList()));

        // ---- Proxmox read-only integration (v1.12.0, NORTH_STAR Phase 8) --------------------------

        app.MapGet("/infrastructure/vms", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(Infrastructure.ListVms()));

        app.MapGet("/infrastructure/containers", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(Infrastructure.ListContainers()));

        app.MapGet("/infrastructure/storage", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(Infrastructure.ListStoragePools()));

        app.MapGet("/infrastructure/proxmox/status", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_infrastructure"); if (auth is not null) return auth;
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["enabled"] = AnthillRuntime.EnableInfrastructureProxmox,
                ["host"] = AnthillRuntime.InfrastructureProxmoxHost,
                ["credential_id"] = AnthillRuntime.InfrastructureProxmoxCredentialId, // id only — never the token
                ["credential_configured"] = InfrastructureCredentials.ListStatuses()
                    .Any(c => c.Id == AnthillRuntime.InfrastructureProxmoxCredentialId.Trim().ToLowerInvariant() && c.Configured),
                ["status"] = InfrastructureProxmox?.GetStatus(),
                ["vms"] = Infrastructure.ListVms().Count,
                ["containers"] = Infrastructure.ListContainers().Count,
                ["storage_pools"] = Infrastructure.ListStoragePools().Count,
                ["read_only"] = true, // structural: the client has no write methods
            });
        });

        // v2.2.0: connection test with actionable diagnostics — never prints token material.
        app.MapPost("/infrastructure/proxmox/test", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            if (string.IsNullOrWhiteSpace(AnthillRuntime.InfrastructureProxmoxHost))
                return ApiJson.Error("Proxmox host is not configured (infrastructure_proxmox_host).", "not_configured");
            var testClient = new ProxmoxApiClient(
                AnthillRuntime.InfrastructureProxmoxHost, AnthillRuntime.InfrastructureProxmoxPort, InfrastructureTargets,
                () => InfrastructureCredentials.GetSecret(AnthillRuntime.InfrastructureProxmoxCredentialId, usedBy: "proxmox-connection-test"),
                AnthillRuntime.InfrastructureProxmoxInsecureTls,
                protocol: AnthillRuntime.InfrastructureProxmoxProtocol);
            try
            {
                var version = await testClient.GetVersionAsync(ctx.RequestAborted);
                var pve = version.ValueKind == System.Text.Json.JsonValueKind.Object && version.TryGetProperty("version", out var v) ? v.GetString() : "?";
                InfrastructureCredentials.MarkVerified(AnthillRuntime.InfrastructureProxmoxCredentialId);
                return ApiJson.Ok(new Dictionary<string, object?>
                {
                    ["reachable"] = true, ["pve_version"] = pve,
                    ["protocol"] = AnthillRuntime.InfrastructureProxmoxProtocol,
                    ["tls_verified"] = AnthillRuntime.InfrastructureProxmoxProtocol == "https" && !AnthillRuntime.InfrastructureProxmoxInsecureTls,
                }, $"Connected — Proxmox VE {pve} over {AnthillRuntime.InfrastructureProxmoxProtocol}.");
            }
            catch (Exception ex)
            {
                var msg = ex.GetBaseException().Message;
                var hint =
                    msg.Contains("401") ? "Invalid credentials or the API token lacks permissions (PVEAuditor role is enough for read-only)." :
                    msg.Contains("403") ? "Permission denied — the token authenticated but lacks the required role." :
                    msg.Contains("allowlist") ? msg :
                    (msg.Contains("SSL") || msg.Contains("certificate") || msg.Contains("TLS")) ? "TLS/certificate issue — set infrastructure_proxmox_insecure_tls=true for self-signed certs, or infrastructure_proxmox_protocol=http if PVE has no TLS." :
                    (msg.Contains("refused") || msg.Contains("timed out") || msg.Contains("No such host") || msg.Contains("unreachable")) ? "Host unreachable on this protocol/port — check infrastructure_proxmox_host/port and whether PVE expects http vs https." :
                    "Connection failed — check protocol (http vs https), port, and credentials.";
                return ApiJson.Error($"Proxmox test failed: {hint}", "test_failed");
            }
        });

        app.MapPost("/infrastructure/proxmox/sync", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            if (InfrastructureProxmox is null)
                return ApiJson.Error("Proxmox integration is not active — set infrastructure_enabled, infrastructure_proxmox_enabled, and infrastructure_proxmox_host, then restart.", "disabled");
            var result = await InfrastructureProxmox.SyncInventoryAsync(ctx.RequestAborted);
            return result.Ok
                ? ApiJson.Ok(new Dictionary<string, object?> { ["items"] = result.ItemCount }, result.Message)
                : ApiJson.Error("Proxmox sync failed: " + result.Message, "sync_failed");
        });

        // ---- Command Center (v2.0.0, NORTH_STAR Phase 11) -----------------------------------------
        // ONE aggregation endpoint: everything the dashboard needs, assembled by the testable
        // CommandCenter builder. No fabricated values — missing data arrives as 0/empty and the UI
        // labels it ("no data yet" / "not configured").

        app.MapGet("/infrastructure/dashboard", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_infrastructure"); if (auth is not null) return auth;
            var pending = -1;
            try { pending = Queen.Memory.CountPendingApprovals(); } catch { /* stays -1 = unavailable */ }
            return ApiJson.Ok(CommandCenter.Build(Infrastructure, InfrastructureHealth, pending));
        });

        app.MapGet("/infrastructure/graph/dependents/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "read_infrastructure"); if (auth is not null) return auth;
            var dashboard = CommandCenter.Build(Infrastructure, InfrastructureHealth);
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["node"] = id,
                ["dependents"] = CommandCenter.Dependents(id, dashboard.GraphEdges),
            });
        });

        // ---- Incident + change memory (v1.14.0, NORTH_STAR Phase 10) ------------------------------

        app.MapGet("/infrastructure/incidents", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(Infrastructure.ListIncidents()));

        app.MapPost("/infrastructure/incidents", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            IncidentOpenRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<IncidentOpenRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.Title)) return ApiJson.Error("Incident title is required.", "bad_request");
            var incident = InfrastructureIncidents.Open(body.Title!.Trim(), (body.SubjectKind ?? "manual").Trim(),
                (body.SubjectId ?? body.Title!).Trim(), (body.Severity ?? "warning").Trim(), CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(incident, $"Incident '{incident.Title}' is {incident.Status}.");
        });

        app.MapGet("/infrastructure/incidents/{id}/timeline", (HttpContext ctx, string id) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(InfrastructureIncidents.Timeline(id)));

        app.MapGet("/infrastructure/incidents/{id}/similar", (HttpContext ctx, string id) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(InfrastructureIncidents.Similar(id)));

        app.MapPost("/infrastructure/incidents/{id}/status", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            IncidentStatusRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<IncidentStatusRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var ok = InfrastructureIncidents.SetStatus(id, (body?.Status ?? "").Trim().ToLowerInvariant(),
                (body?.RootCause ?? "").Trim(), CurrentUsername(ctx) ?? "operator");
            return ok
                ? ApiJson.Ok(Infrastructure.ListIncidents(), "Incident updated." +
                    (string.IsNullOrWhiteSpace(body?.RootCause) ? "" : " Root cause recorded — similar future incidents will surface it as a suggested fix."))
                : ApiJson.Error("Unknown incident or invalid status (open|investigating|resolved).", "bad_request");
        });

        // v1.14.0: the ONE pending-approvals view (IApprovable). v2.3.0: infrastructure action proposals
        // join the patch projections in the same queue; V2.6 network changes will be next.
        app.MapGet("/infrastructure/approvals/unified", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "read_approvals"); if (auth is not null) return auth;
            // v0.3.8.113 — the store hands back a TYPED approval and the module still takes a row,
            // deliberately. `ApprovableProjections` lives in `Anthill.Modules.Infrastructure`, which may
            // reference the SDK and nothing else of ours — `ModuleBoundaryTests` enforces that — so
            // `Anthill.Core.Domain.ApprovalRequest` cannot cross into it. The projection happens
            // HERE, at the composition edge, which is where a boundary translation belongs. Every
            // key below is one `FromPatchApproval` reads; the guard in `TypedRowMigrationTests`
            // pins that correspondence so a renamed field cannot silently empty the queue.
            var views = Queen.Memory.Approvals(null, 100)
                .Select(a => ApprovableProjections.FromPatchApproval(new Dictionary<string, object?>
                {
                    ["id"] = a.Id,
                    ["title"] = a.Title,
                    ["description"] = a.Description,
                    // The wire spellings, through `EnumExtensions` called as a STATIC rather than as
                    // an extension: this file does not import `Anthill.Core.Domain`, and adding the
                    // using to reach one method risks CS8933 against a global one — a warning that
                    // is a build failure since `.112`.
                    ["status"] = Anthill.Core.Domain.EnumExtensions.Value(a.Status),
                    ["action_type"] = Anthill.Core.Domain.EnumExtensions.Value(a.ActionType),
                    ["target_id"] = a.TargetId,
                    ["requested_by"] = a.RequestedBy,
                    // `ToString("o")` rather than the `ToIso()` extension: this file does not import the
                    // namespace that carries it, and a display projection has no reason to acquire a
                    // using for one call.
                    ["created_at"] = a.CreatedAt.ToString("o"),
                    ["metadata_json"] = Anthill.SDK.Common.Json.SafeDumps(a.Metadata),
                }))
                .Concat(Infrastructure.ListActionProposals(100).Select(ApprovableProjections.FromActionProposal));
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["items"] = ApprovableProjections.DedupePending(views),
                ["kinds"] = new[] { "patch", "infrastructure_action" }, // "network_change" arrives with the network control layer
                ["design"] = "docs/APPROVALS.md",
            });
        });

        // v2.3.0 (NORTH_STAR Phase 12): approval-gated action endpoints + kill switch.
        MapInfrastructureActionEndpoints(app);
        MapInfrastructureBackupEndpoints(app); // v2.4.0 Phase 13
        MapInfrastructureAutomationEndpoints(app); // v2.5.0 Phase 14

        // v2.3.3: *arr apps + node metrics.
        MapInfrastructureArrEndpoints(app);

        // v2.5.1 Console Refit R1: generic integration platform (catalog + instances + widgets).
        MapInfrastructureIntegrationEndpoints(app);

        // ---- Network + security awareness (v1.13.0, NORTH_STAR Phase 9) ---------------------------

        app.MapGet("/infrastructure/devices", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(Infrastructure.ListNetworkDevices()));

        app.MapPost("/infrastructure/devices", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            DeviceUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<DeviceUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.Name) && string.IsNullOrWhiteSpace(body?.Mac))
                return ApiJson.Error("A device needs at least a name or a MAC address.", "bad_request");
            var device = new NetworkDevice
            {
                Id = string.IsNullOrWhiteSpace(body!.Id) ? Guid.NewGuid().ToString() : body.Id!.Trim(),
                Name = (body.Name ?? "").Trim(), Kind = (body.Kind ?? "unknown").Trim(),
                Mac = (body.Mac ?? "").Trim(), Ip = (body.Ip ?? "").Trim(), Vlan = (body.Vlan ?? "").Trim(),
                Known = body.Known ?? true, Notes = (body.Notes ?? "").Trim(),
            };
            Infrastructure.UpsertNetworkDevice(device, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(Infrastructure.ListNetworkDevices(), $"Device '{(device.Name.Length > 0 ? device.Name : device.Mac)}' saved.");
        });

        app.MapDelete("/infrastructure/devices/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            Infrastructure.RemoveNetworkDevice(id, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(Infrastructure.ListNetworkDevices(), "Device removed.");
        });

        app.MapGet("/infrastructure/risks", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(Infrastructure.ListRiskRecords()));

        app.MapPost("/infrastructure/risks/analyze", (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            var (open, resolved) = InfrastructureRisks.Analyze(CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(Infrastructure.ListRiskRecords(), $"Risk analysis complete: {open} open finding(s), {resolved} resolved.");
        });

        app.MapPost("/infrastructure/risks/{id}/ack", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            Infrastructure.SetRiskStatus(id, "acknowledged", CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(Infrastructure.ListRiskRecords(), "Finding acknowledged — it stays visible but won't be re-flagged as open.");
        });

        // ---- Health checks + notifications (v1.11.0, NORTH_STAR Phase 7) --------------------------

        app.MapGet("/infrastructure/health/summary", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(InfrastructureHealth.Summarize()));

        app.MapGet("/infrastructure/health/results", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(Infrastructure.RecentHealthResults(100)));

        app.MapGet("/infrastructure/health/schedules", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(Infrastructure.ListHealthSchedules()));

        app.MapPost("/infrastructure/health/schedules", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
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
            Infrastructure.UpsertHealthSchedule(schedule, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(Infrastructure.ListHealthSchedules(), $"Health check '{schedule.CheckKind} {schedule.Target}' saved. The target host must be on the infrastructure allowlist to actually run.");
        });

        app.MapDelete("/infrastructure/health/schedules/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            Infrastructure.RemoveHealthSchedule(id, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(Infrastructure.ListHealthSchedules(), "Health check schedule removed.");
        });

        app.MapPost("/infrastructure/health/run", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            var result = await InfrastructureHealth.RunAllAsync(ctx.RequestAborted);
            return ApiJson.Ok(new Dictionary<string, object?>
            {
                ["ok"] = result.Ok, ["message"] = result.Message,
                ["summary"] = InfrastructureHealth.Summarize(),
            }, result.Ok ? "Health checks completed." : $"Health checks completed with failures: {result.Message}");
        });

        app.MapPost("/infrastructure/notifications/test", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            if (!NotificationService.Enabled)
                return ApiJson.Error("Notifications are disabled — set infrastructure_notifications_enabled=true and configure a webhook first.", "disabled");
            var delivered = await InfrastructureNotifier.SendAsync(new Anthill.Modules.Infrastructure.Health.AlertRecord
            {
                Kind = "test", Severity = "info",
                Message = $"ANTHILL v{AnthillRuntime.Version} notification test from {CurrentUsername(ctx) ?? "operator"}",
            }, ctx.RequestAborted);
            return delivered > 0
                ? ApiJson.Ok(new Dictionary<string, object?> { ["delivered"] = delivered }, $"Test alert delivered to {delivered} webhook(s).")
                : ApiJson.Error("No webhook accepted the test alert — check the configured URLs (see infrastructure events for the audit trail).", "delivery_failed");
        });

        app.MapGet("/infrastructure/changes", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(Infrastructure.RecentChanges(50)));

        // ---- Target allowlist (D1) -------------------------------------------------------------

        app.MapGet("/infrastructure/allowlist", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(Infrastructure.ListAllowlist()));

        app.MapPost("/infrastructure/allowlist", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            AllowlistUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<AllowlistUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.Target)) return ApiJson.Error("Target (hostname, IP, or IPv4 CIDR) is required.", "bad_request");
            var kind = (body.Kind ?? "allow").Trim().ToLowerInvariant();
            if (kind != "allow" && kind != "deny") return ApiJson.Error("Kind must be 'allow' or 'deny'.", "bad_request");
            var entry = new TargetAllowlistRecord
            {
                Id = string.IsNullOrWhiteSpace(body.Id) ? Guid.NewGuid().ToString() : body.Id!.Trim(),
                Target = body.Target!.Trim(), Kind = kind, Note = (body.Note ?? "").Trim(),
                Enabled = body.Enabled ?? true, AddedBy = CurrentUsername(ctx) ?? "operator",
            };
            Infrastructure.AddAllowlistEntry(entry);
            var msg = kind == "deny"
                ? $"Blocklist target '{entry.Target}' added — deny beats allow, so it is refused even if an allow entry also matches."
                : $"Allowlist target '{entry.Target}' added. This affects deterministic infrastructure providers only — the general SSRF guard for AI tools is unchanged.";
            return ApiJson.Ok(Infrastructure.ListAllowlist(), msg);
        });

        // v2.5.4 R4: edit in place (note / enabled / kind / target) — audited as 'updated'.
        app.MapPut("/infrastructure/allowlist/{id}", async (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            var existing = Infrastructure.ListAllowlist().FirstOrDefault(e => e.Id == id);
            if (existing is null) return ApiJson.Error($"No target entry with id '{id}'.", "not_found");
            AllowlistUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<AllowlistUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (body is null) return ApiJson.Error("Invalid request body.", "bad_request");
            if (body.Kind is not null)
            {
                var kind = body.Kind.Trim().ToLowerInvariant();
                if (kind != "allow" && kind != "deny") return ApiJson.Error("Kind must be 'allow' or 'deny'.", "bad_request");
                existing.Kind = kind;
            }
            if (!string.IsNullOrWhiteSpace(body.Target)) existing.Target = body.Target!.Trim();
            if (body.Note is not null) existing.Note = body.Note.Trim();
            if (body.Enabled is not null) existing.Enabled = body.Enabled.Value;
            existing.AddedBy = existing.AddedBy is { Length: > 0 } ? existing.AddedBy : (CurrentUsername(ctx) ?? "operator");
            Infrastructure.AddAllowlistEntry(existing); // upsert by id → audited 'updated'
            return ApiJson.Ok(Infrastructure.ListAllowlist(), $"Target '{existing.Target}' updated.");
        });

        // v2.5.4 R4: bulk enable / disable / remove — one request, one audited change record.
        app.MapPost("/infrastructure/allowlist/bulk", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            AllowlistBulkRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<AllowlistBulkRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            var ids = (body?.Ids ?? new List<string>()).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList();
            if (ids.Count == 0) return ApiJson.Error("Ids are required.", "bad_request");
            var by = CurrentUsername(ctx) ?? "operator";
            var n = (body!.Action ?? "").Trim().ToLowerInvariant() switch
            {
                "enable" => Infrastructure.SetAllowlistEnabled(ids, true, by),
                "disable" => Infrastructure.SetAllowlistEnabled(ids, false, by),
                "remove" => Infrastructure.RemoveAllowlistEntries(ids, by),
                _ => -1,
            };
            if (n < 0) return ApiJson.Error("Action must be 'enable', 'disable', or 'remove'.", "bad_request");
            return ApiJson.Ok(Infrastructure.ListAllowlist(), $"Bulk {body.Action}: {n} entr{(n == 1 ? "y" : "ies")} affected.");
        });

        app.MapDelete("/infrastructure/allowlist/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            Infrastructure.RemoveAllowlistEntry(id, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(Infrastructure.ListAllowlist(), "Target entry removed.");
        });

        // ---- Credentials (D2) — write-only secrets, secret-free statuses -------------------------

        app.MapGet("/infrastructure/credentials", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(InfrastructureCredentials.ListStatuses()));

        app.MapPost("/infrastructure/credentials", async (HttpContext ctx) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            CredentialUpsertRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<CredentialUpsertRequest>(); }
            catch { return ApiJson.Error("Invalid request body.", "bad_request"); }
            if (string.IsNullOrWhiteSpace(body?.Id)) return ApiJson.Error("Credential id is required.", "bad_request");
            if (string.IsNullOrWhiteSpace(body.Secret)) return ApiJson.Error("Credential secret is required.", "bad_request");
            InfrastructureCredentials.SaveCredential(body.Id!, body.Kind ?? "other", body.TargetHost ?? "", body.Secret!, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(InfrastructureCredentials.ListStatuses(), $"Credential '{body.Id!.Trim().ToLowerInvariant()}' saved. Secrets are write-only and never returned.");
        });

        app.MapDelete("/infrastructure/credentials/{id}", (HttpContext ctx, string id) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            InfrastructureCredentials.RemoveCredential(id, CurrentUsername(ctx) ?? "operator");
            return ApiJson.Ok(InfrastructureCredentials.ListStatuses(), "Credential removed.");
        });
    }
}
