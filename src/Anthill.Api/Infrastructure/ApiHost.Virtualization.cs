using Anthill.Core.Configuration;
using Anthill.Modules.Infrastructure;
using Anthill.Modules.Infrastructure.Scheduling;
using Anthill.Modules.Infrastructure.Integrations.Docker;
using Anthill.Modules.Infrastructure.Integrations.Hyperv;
using Anthill.Modules.Infrastructure.Integrations.Proxmox;
using Anthill.Modules.Infrastructure.Integrations.VSphere;

namespace Anthill.Api;

/// <summary>
/// Unified read-only virtualization layer (v2.1.0). Proxmox, ESXi/vSphere, Docker, and Hyper-V all
/// project into ONE inventory (nodes/VMs/containers/storage) through the same
/// <see cref="Anthill.Modules.Infrastructure.IInventoryProvider"/> shape. Providers are built ON DEMAND from
/// current config, so a connection edited in the UI (host / credential id / enable) takes effect on the
/// next sync WITHOUT a restart. Every client is read-only by construction (no start/stop/delete exists).
/// </summary>
public static partial class ApiHost
{
    internal static readonly string[] VirtKinds = { "proxmox", "esxi", "docker", "hyperv" };

    /// <summary>Builds the inventory provider for one kind from CURRENT config; null when disabled/unset.</summary>
    internal static Anthill.Modules.Infrastructure.IInventoryProvider? BuildVirtProvider(string kind)
    {
        switch (kind)
        {
            case "proxmox":
                if (!AnthillRuntime.EnableInfrastructureProxmox || string.IsNullOrWhiteSpace(AnthillRuntime.InfrastructureProxmoxHost)) return null;
                return new ProxmoxInventoryProvider(new ProxmoxApiClient(
                    AnthillRuntime.InfrastructureProxmoxHost, AnthillRuntime.InfrastructureProxmoxPort, InfrastructureTargets,
                    () => InfrastructureCredentials.GetSecret(AnthillRuntime.InfrastructureProxmoxCredentialId, "proxmox-sync"),
                    AnthillRuntime.InfrastructureProxmoxInsecureTls), Infrastructure);
            case "esxi":
                if (!AnthillRuntime.EnableInfrastructureEsxi || string.IsNullOrWhiteSpace(AnthillRuntime.InfrastructureEsxiHost)) return null;
                return new VSphereInventoryProvider(new VSphereApiClient(
                    AnthillRuntime.InfrastructureEsxiHost, AnthillRuntime.InfrastructureEsxiPort, InfrastructureTargets,
                    () => InfrastructureCredentials.GetSecret(AnthillRuntime.InfrastructureEsxiCredentialId, "esxi-sync"),
                    AnthillRuntime.InfrastructureEsxiInsecureTls), Infrastructure);
            case "docker":
                if (!AnthillRuntime.EnableInfrastructureDocker || string.IsNullOrWhiteSpace(AnthillRuntime.InfrastructureDockerHost)) return null;
                return new DockerInventoryProvider(new DockerApiClient(
                    AnthillRuntime.InfrastructureDockerHost, AnthillRuntime.InfrastructureDockerPort, InfrastructureTargets,
                    () => InfrastructureCredentials.GetSecret(AnthillRuntime.InfrastructureDockerCredentialId, "docker-sync"),
                    AnthillRuntime.InfrastructureDockerInsecureTls), Infrastructure);
            case "hyperv":
                if (!AnthillRuntime.EnableInfrastructureHyperv || string.IsNullOrWhiteSpace(AnthillRuntime.InfrastructureHypervHost)) return null;
                return new HypervInventoryProvider(new HypervWinRmClient(
                    AnthillRuntime.InfrastructureHypervHost, AnthillRuntime.InfrastructureHypervPort, InfrastructureTargets,
                    () => InfrastructureCredentials.GetSecret(AnthillRuntime.InfrastructureHypervCredentialId, "hyperv-sync"),
                    AnthillRuntime.InfrastructureHypervInsecureTls), Infrastructure);
            default: return null;
        }
    }

    private static (bool Enabled, string Host, int Port, string CredentialId, bool InsecureTls, int Interval) VirtConfig(string kind) => kind switch
    {
        "proxmox" => (AnthillRuntime.EnableInfrastructureProxmox, AnthillRuntime.InfrastructureProxmoxHost, AnthillRuntime.InfrastructureProxmoxPort, AnthillRuntime.InfrastructureProxmoxCredentialId, AnthillRuntime.InfrastructureProxmoxInsecureTls, AnthillRuntime.InfrastructureProxmoxSyncIntervalSeconds),
        "esxi" => (AnthillRuntime.EnableInfrastructureEsxi, AnthillRuntime.InfrastructureEsxiHost, AnthillRuntime.InfrastructureEsxiPort, AnthillRuntime.InfrastructureEsxiCredentialId, AnthillRuntime.InfrastructureEsxiInsecureTls, AnthillRuntime.InfrastructureEsxiSyncIntervalSeconds),
        "docker" => (AnthillRuntime.EnableInfrastructureDocker, AnthillRuntime.InfrastructureDockerHost, AnthillRuntime.InfrastructureDockerPort, AnthillRuntime.InfrastructureDockerCredentialId, AnthillRuntime.InfrastructureDockerInsecureTls, AnthillRuntime.InfrastructureDockerSyncIntervalSeconds),
        "hyperv" => (AnthillRuntime.EnableInfrastructureHyperv, AnthillRuntime.InfrastructureHypervHost, AnthillRuntime.InfrastructureHypervPort, AnthillRuntime.InfrastructureHypervCredentialId, AnthillRuntime.InfrastructureHypervInsecureTls, AnthillRuntime.InfrastructureHypervSyncIntervalSeconds),
        _ => (false, "", 0, "", false, 300),
    };

    internal static Dictionary<string, object?> VirtStatus(string kind)
    {
        var (enabled, host, port, credId, insecure, _) = VirtConfig(kind);
        var configured = InfrastructureCredentials.ListStatuses()
            .Any(c => c.Id == (credId ?? "").Trim().ToLowerInvariant() && c.Configured);
        var hostSet = !string.IsNullOrWhiteSpace(host);
        return new Dictionary<string, object?>
        {
            ["kind"] = kind, ["enabled"] = enabled, ["host"] = host, ["port"] = port,
            ["credential_id"] = credId,             // id only — never the secret
            ["credential_configured"] = configured,
            ["insecure_tls"] = insecure,
            // The allowlist is a hard gate in front of every request: an active connection whose host is
            // not allowlisted fails before it reaches the target. Surfacing it lets the UI offer a fix.
            ["host_allowlisted"] = hostSet && InfrastructureTargets.IsAllowed(host.Trim()),
            ["active"] = enabled && hostSet,
            ["read_only"] = true,                   // structural: no write methods in any client
        };
    }

    private static System.Threading.Tasks.Task<InfrastructureProviderResult> VirtSyncJob(string kind, CancellationToken ct)
    {
        var provider = BuildVirtProvider(kind);
        return provider is null
            ? System.Threading.Tasks.Task.FromResult(InfrastructureProviderResult.Failure($"{kind} integration not active"))
            : provider.SyncInventoryAsync(ct);
    }

    /// <summary>Register scheduled sync jobs for the NEW integrations (Proxmox keeps its own in InitInfrastructure).</summary>
    internal static void RegisterVirtJobs()
    {
        if (!AnthillRuntime.EnableInfrastructure) return;
        foreach (var kind in new[] { "esxi", "docker", "hyperv" })
        {
            var (enabled, host, _, _, _, interval) = VirtConfig(kind);
            if (enabled && !string.IsNullOrWhiteSpace(host))
                InfrastructureJobs.Register(new InfrastructureScheduledJob($"{kind}-sync",
                    TimeSpan.FromSeconds(interval), ct => VirtSyncJob(kind, ct)));
        }
    }

    internal static void MapVirtualizationEndpoints(WebApplication app)
    {
        // Unified status for all four integrations (secret-free) + total inventory counts.
        app.MapGet("/infrastructure/virtualization/status", (HttpContext ctx) =>
            RequireAuth(ctx, "read_infrastructure") ?? ApiJson.Ok(new Dictionary<string, object?>
            {
                ["integrations"] = VirtKinds.Select(VirtStatus).ToList(),
                ["vms"] = Infrastructure.ListVms().Count,
                ["containers"] = Infrastructure.ListContainers().Count,
                ["storage_pools"] = Infrastructure.ListStoragePools().Count,
                // Master gates so the connections panel can show/flip them without editing config.json.
                ["infrastructure_enabled"] = AnthillRuntime.EnableInfrastructure,
                ["scheduler_enabled"] = AnthillRuntime.EnableInfrastructureScheduler,
                ["read_only"] = true,
            }));

        // Manual sync for any kind — builds the provider from current config, so a connection just
        // saved in the UI works immediately (no restart).
        app.MapPost("/infrastructure/virtualization/{kind}/sync", async (HttpContext ctx, string kind) =>
        {
            var auth = RequireAuth(ctx, "manage_infrastructure_integrations"); if (auth is not null) return auth;
            kind = (kind ?? "").Trim().ToLowerInvariant();
            if (!VirtKinds.Contains(kind)) return ApiJson.Error($"Unknown virtualization integration '{kind}'.", "bad_request");
            var provider = BuildVirtProvider(kind);
            if (provider is null)
                return ApiJson.Error($"The {kind} integration is not active — enable it and set its host, save, then sync.", "disabled");
            var result = await provider.SyncInventoryAsync(ctx.RequestAborted);
            return result.Ok
                ? ApiJson.Ok(new Dictionary<string, object?> { ["items"] = result.ItemCount, ["kind"] = kind }, result.Message)
                : ApiJson.Error($"{kind} sync failed: {result.Message}", "sync_failed");
        });
    }
}
