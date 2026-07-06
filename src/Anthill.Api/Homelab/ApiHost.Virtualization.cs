using Anthill.Core.Configuration;
using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Scheduling;
using Anthill.Core.Integrations.Docker;
using Anthill.Core.Integrations.Hyperv;
using Anthill.Core.Integrations.Proxmox;
using Anthill.Core.Integrations.VSphere;

namespace Anthill.Api;

/// <summary>
/// Unified read-only virtualization layer (v2.1.0). Proxmox, ESXi/vSphere, Docker, and Hyper-V all
/// project into ONE inventory (nodes/VMs/containers/storage) through the same
/// <see cref="Anthill.Core.Homelab.IInventoryProvider"/> shape. Providers are built ON DEMAND from
/// current config, so a connection edited in the UI (host / credential id / enable) takes effect on the
/// next sync WITHOUT a restart. Every client is read-only by construction (no start/stop/delete exists).
/// </summary>
public static partial class ApiHost
{
    internal static readonly string[] VirtKinds = { "proxmox", "esxi", "docker", "hyperv" };

    /// <summary>Builds the inventory provider for one kind from CURRENT config; null when disabled/unset.</summary>
    internal static Anthill.Core.Homelab.IInventoryProvider? BuildVirtProvider(string kind)
    {
        switch (kind)
        {
            case "proxmox":
                if (!AnthillRuntime.EnableHomelabProxmox || string.IsNullOrWhiteSpace(AnthillRuntime.HomelabProxmoxHost)) return null;
                return new ProxmoxInventoryProvider(new ProxmoxApiClient(
                    AnthillRuntime.HomelabProxmoxHost, AnthillRuntime.HomelabProxmoxPort, HomelabTargets,
                    () => HomelabCredentials.GetSecret(AnthillRuntime.HomelabProxmoxCredentialId, "proxmox-sync"),
                    AnthillRuntime.HomelabProxmoxInsecureTls), Homelab);
            case "esxi":
                if (!AnthillRuntime.EnableHomelabEsxi || string.IsNullOrWhiteSpace(AnthillRuntime.HomelabEsxiHost)) return null;
                return new VSphereInventoryProvider(new VSphereApiClient(
                    AnthillRuntime.HomelabEsxiHost, AnthillRuntime.HomelabEsxiPort, HomelabTargets,
                    () => HomelabCredentials.GetSecret(AnthillRuntime.HomelabEsxiCredentialId, "esxi-sync"),
                    AnthillRuntime.HomelabEsxiInsecureTls), Homelab);
            case "docker":
                if (!AnthillRuntime.EnableHomelabDocker || string.IsNullOrWhiteSpace(AnthillRuntime.HomelabDockerHost)) return null;
                return new DockerInventoryProvider(new DockerApiClient(
                    AnthillRuntime.HomelabDockerHost, AnthillRuntime.HomelabDockerPort, HomelabTargets,
                    () => HomelabCredentials.GetSecret(AnthillRuntime.HomelabDockerCredentialId, "docker-sync"),
                    AnthillRuntime.HomelabDockerInsecureTls), Homelab);
            case "hyperv":
                if (!AnthillRuntime.EnableHomelabHyperv || string.IsNullOrWhiteSpace(AnthillRuntime.HomelabHypervHost)) return null;
                return new HypervInventoryProvider(new HypervWinRmClient(
                    AnthillRuntime.HomelabHypervHost, AnthillRuntime.HomelabHypervPort, HomelabTargets,
                    () => HomelabCredentials.GetSecret(AnthillRuntime.HomelabHypervCredentialId, "hyperv-sync"),
                    AnthillRuntime.HomelabHypervInsecureTls), Homelab);
            default: return null;
        }
    }

    private static (bool Enabled, string Host, int Port, string CredentialId, bool InsecureTls, int Interval) VirtConfig(string kind) => kind switch
    {
        "proxmox" => (AnthillRuntime.EnableHomelabProxmox, AnthillRuntime.HomelabProxmoxHost, AnthillRuntime.HomelabProxmoxPort, AnthillRuntime.HomelabProxmoxCredentialId, AnthillRuntime.HomelabProxmoxInsecureTls, AnthillRuntime.HomelabProxmoxSyncIntervalSeconds),
        "esxi" => (AnthillRuntime.EnableHomelabEsxi, AnthillRuntime.HomelabEsxiHost, AnthillRuntime.HomelabEsxiPort, AnthillRuntime.HomelabEsxiCredentialId, AnthillRuntime.HomelabEsxiInsecureTls, AnthillRuntime.HomelabEsxiSyncIntervalSeconds),
        "docker" => (AnthillRuntime.EnableHomelabDocker, AnthillRuntime.HomelabDockerHost, AnthillRuntime.HomelabDockerPort, AnthillRuntime.HomelabDockerCredentialId, AnthillRuntime.HomelabDockerInsecureTls, AnthillRuntime.HomelabDockerSyncIntervalSeconds),
        "hyperv" => (AnthillRuntime.EnableHomelabHyperv, AnthillRuntime.HomelabHypervHost, AnthillRuntime.HomelabHypervPort, AnthillRuntime.HomelabHypervCredentialId, AnthillRuntime.HomelabHypervInsecureTls, AnthillRuntime.HomelabHypervSyncIntervalSeconds),
        _ => (false, "", 0, "", false, 300),
    };

    internal static Dictionary<string, object?> VirtStatus(string kind)
    {
        var (enabled, host, port, credId, insecure, _) = VirtConfig(kind);
        var configured = HomelabCredentials.ListStatuses()
            .Any(c => c.Id == (credId ?? "").Trim().ToLowerInvariant() && c.Configured);
        return new Dictionary<string, object?>
        {
            ["kind"] = kind, ["enabled"] = enabled, ["host"] = host, ["port"] = port,
            ["credential_id"] = credId,             // id only — never the secret
            ["credential_configured"] = configured,
            ["insecure_tls"] = insecure,
            ["active"] = enabled && !string.IsNullOrWhiteSpace(host),
            ["read_only"] = true,                   // structural: no write methods in any client
        };
    }

    private static System.Threading.Tasks.Task<HomelabProviderResult> VirtSyncJob(string kind, CancellationToken ct)
    {
        var provider = BuildVirtProvider(kind);
        return provider is null
            ? System.Threading.Tasks.Task.FromResult(HomelabProviderResult.Failure($"{kind} integration not active"))
            : provider.SyncInventoryAsync(ct);
    }

    /// <summary>Register scheduled sync jobs for the NEW integrations (Proxmox keeps its own in InitHomelab).</summary>
    internal static void RegisterVirtJobs()
    {
        if (!AnthillRuntime.EnableHomelab) return;
        foreach (var kind in new[] { "esxi", "docker", "hyperv" })
        {
            var (enabled, host, _, _, _, interval) = VirtConfig(kind);
            if (enabled && !string.IsNullOrWhiteSpace(host))
                HomelabJobs.Register(new HomelabScheduledJob($"{kind}-sync",
                    TimeSpan.FromSeconds(interval), ct => VirtSyncJob(kind, ct)));
        }
    }

    internal static void MapVirtualizationEndpoints(WebApplication app)
    {
        // Unified status for all four integrations (secret-free) + total inventory counts.
        app.MapGet("/homelab/virtualization/status", (HttpContext ctx) =>
            RequireAuth(ctx, "read_homelab") ?? ApiJson.Ok(new Dictionary<string, object?>
            {
                ["integrations"] = VirtKinds.Select(VirtStatus).ToList(),
                ["vms"] = Homelab.ListVms().Count,
                ["containers"] = Homelab.ListContainers().Count,
                ["storage_pools"] = Homelab.ListStoragePools().Count,
                ["read_only"] = true,
            }));

        // Manual sync for any kind — builds the provider from current config, so a connection just
        // saved in the UI works immediately (no restart).
        app.MapPost("/homelab/virtualization/{kind}/sync", async (HttpContext ctx, string kind) =>
        {
            var auth = RequireAuth(ctx, "manage_homelab_integrations"); if (auth is not null) return auth;
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
