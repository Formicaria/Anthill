namespace Anthill.Core.Homelab;

/// <summary>
/// Homelab foundation interfaces (v1.9.0, NORTH_STAR Phase 4). Every future integration
/// (Proxmox, DNS, DHCP, firewall, health) implements these so v1.9.1's shared mock-provider
/// harness can test them all the same way. All implementations must be deterministic C# —
/// polling never routes through the model router (NORTH_STAR §3.2 rule 5).
/// </summary>
public sealed record HomelabProviderResult(bool Ok, string Message, int ItemCount = 0)
{
    public static HomelabProviderResult Success(string message = "ok", int itemCount = 0) => new(true, message, itemCount);
    public static HomelabProviderResult Failure(string message) => new(false, message);
}

/// <summary>Collects inventory (nodes, VMs, containers, storage) from one source, read-only.</summary>
public interface IInventoryProvider
{
    string Name { get; }
    System.Threading.Tasks.Task<HomelabProviderResult> SyncInventoryAsync(CancellationToken ct);
}

/// <summary>Runs one kind of health check (ping/http/tcp/...), read-only, with strict timeouts.</summary>
public interface IHealthCheckProvider
{
    string Name { get; }
    System.Threading.Tasks.Task<HealthCheckResult> CheckAsync(string target, CancellationToken ct);
}

/// <summary>Sink for homelab events so every subsystem writes the same audit/event stream.</summary>
public interface IHomelabEventSink
{
    void RecordEvent(HomelabEvent evt);
}

/// <summary>Secret-free status for one integration, surfaced on the summary endpoint and UI.</summary>
public interface IIntegrationStatusProvider
{
    IntegrationStatus GetStatus();
}

/// <summary>
/// Decides whether a deterministic homelab provider may contact a target host. Backed by the
/// operator-maintained homelab_target_allowlist table. This is intentionally SEPARATE from the
/// general SSRF guard (UrlSafety): allowlisting a private host here must never loosen what
/// LLM-directed web tools may reach.
/// </summary>
public interface IHomelabTargetGuard
{
    bool IsAllowed(string hostOrIp);
}

/// <summary>
/// Typed credential access for homelab providers. Secrets are write-only through the API:
/// SaveCredential stores encrypted, GetSecret is for deterministic providers only and audits
/// every use, and ListStatuses never contains secret material.
/// </summary>
public interface ICredentialProvider
{
    void SaveCredential(string id, string kind, string targetHost, string secret, string savedBy);
    string? GetSecret(string id, string usedBy);
    void MarkVerified(string id);
    void RemoveCredential(string id, string removedBy);
    IReadOnlyList<CredentialRecord> ListStatuses();
}

/// <summary>
/// Persistence for the homelab foundation tables. One SQLite home (the existing colony DB) so
/// memory, missions, and homelab knowledge can be linked and searched together.
/// </summary>
public interface IHomelabRepository : IHomelabEventSink
{
    void UpsertNode(HomelabNode node, string changedBy);
    IReadOnlyList<HomelabNode> ListNodes();
    void UpsertService(ServiceRecord service, string changedBy);
    IReadOnlyList<ServiceRecord> ListServices();

    // Virtualization + storage inventory (v1.12.0, filled by the Proxmox read-only sync)
    void UpsertVm(VmRecord vm);
    IReadOnlyList<VmRecord> ListVms();
    void UpsertContainer(ContainerRecord container);
    IReadOnlyList<ContainerRecord> ListContainers();
    void UpsertStoragePool(StoragePoolRecord pool);
    IReadOnlyList<StoragePoolRecord> ListStoragePools();
    IReadOnlyList<HomelabEvent> RecentEvents(int limit = 50);
    void RecordChange(ChangeRecord change);
    IReadOnlyList<ChangeRecord> RecentChanges(int limit = 50);
    void SaveHealthResult(HealthCheckResult result);
    IReadOnlyList<HealthCheckResult> RecentHealthResults(int limit = 50);

    // Health-check schedules + per-target history (v1.11.0, NORTH_STAR Phase 7)
    IReadOnlyList<HealthCheckResult> RecentHealthResultsForTarget(string target, int limit = 10);
    void UpsertHealthSchedule(Anthill.Core.Health.HealthCheckSchedule schedule, string changedBy);
    void RemoveHealthSchedule(string id, string removedBy);
    IReadOnlyList<Anthill.Core.Health.HealthCheckSchedule> ListHealthSchedules();

    // Dependency mapping (v1.10.0): "what runs where?" / "what depends on this?"
    void UpsertDependency(DependencyRecord dependency, string changedBy);
    void RemoveDependency(string id, string removedBy);
    IReadOnlyList<DependencyRecord> ListDependencies();

    // Inventory import/export (v1.10.0) — nodes + services + dependencies, never secrets.
    HomelabInventoryExport ExportInventory();
    (int Nodes, int Services, int Dependencies) ImportInventory(HomelabInventoryExport bundle, string importedBy);

    // Target allowlist (D1)
    void AddAllowlistEntry(TargetAllowlistRecord entry);
    void RemoveAllowlistEntry(string id, string removedBy);
    IReadOnlyList<TargetAllowlistRecord> ListAllowlist();

    // Scheduler job state (D4): last-run/last-result must survive restart.
    void RecordJobRun(string jobName, bool ok, string message);
    (string LastRun, string LastResult)? GetJobState(string jobName);

    /// <summary>Row counts for every homelab table — summary endpoint + migration tests.</summary>
    Dictionary<string, long> TableCounts();
}
