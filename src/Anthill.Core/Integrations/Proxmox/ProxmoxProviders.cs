using System.Text.Json;
using System.Text.Json.Serialization;
using Anthill.Core.Common;
using Anthill.Core.Health;
using Anthill.Core.Homelab;

namespace Anthill.Core.Integrations.Proxmox;

/// <summary>One failed Proxmox task worth surfacing (v1.12.0). Recorded as a homelab event.</summary>
public sealed class ProxmoxTaskRecord
{
    [JsonPropertyName("upid")] public string Upid { get; set; } = "";
    [JsonPropertyName("node")] public string Node { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("start_time")] public long StartTime { get; set; }
}

/// <summary>
/// Read-only Proxmox inventory sync (v1.12.0, NORTH_STAR Phase 8). Pulls nodes, QEMU VMs, LXC
/// containers, storage, and failed tasks through the GET-only <see cref="ProxmoxApiClient"/> and
/// upserts them into the homelab inventory (stable ids — re-sync is idempotent). Deterministic C#;
/// no write path to Proxmox exists anywhere in this integration.
/// </summary>
public sealed class ProxmoxInventoryProvider : IInventoryProvider, IIntegrationStatusProvider
{
    private readonly ProxmoxApiClient _client;
    private readonly IHomelabRepository _repository;
    private readonly object _lock = new();
    private string _state = "not_configured", _lastRun = "", _lastResult = "";

    public string Name => "proxmox-inventory";

    public ProxmoxInventoryProvider(ProxmoxApiClient client, IHomelabRepository repository)
    {
        _client = client;
        _repository = repository;
    }

    public IntegrationStatus GetStatus()
    {
        lock (_lock) return new IntegrationStatus
        {
            Name = Name, Kind = "proxmox", Enabled = true,
            State = _state, LastRun = _lastRun, LastResult = _lastResult,
        };
    }

    public async System.Threading.Tasks.Task<HomelabProviderResult> SyncInventoryAsync(CancellationToken ct)
    {
        try
        {
            var items = 0;
            var nodes = await _client.GetNodesAsync(ct).ConfigureAwait(false);
            foreach (var node in Arr(nodes))
            {
                var nodeName = Str(node, "node");
                if (nodeName.Length == 0) continue;
                var nodeId = $"pve-node:{_client.Host}:{nodeName}";
                _repository.UpsertNode(new HomelabNode
                {
                    Id = nodeId, Name = nodeName, Kind = "hypervisor", Address = _client.Host,
                    Os = "Proxmox VE", RoleTags = new() { "proxmox" },
                    Notes = $"status={Str(node, "status")} cpu={Num(node, "maxcpu")} mem={Num(node, "maxmem")} uptime={Num(node, "uptime")}s",
                }, changedBy: Name);
                items++;

                // v2.3.3: the /nodes payload already carries live resource usage — persist it so
                // the Service Deck can show CPU/RAM/storage bars per hypervisor node.
                _repository.UpsertNodeMetric(new NodeMetricRecord
                {
                    NodeId = nodeId, NodeName = nodeName, Source = "proxmox",
                    CpuPercent = Frac(node, "cpu") >= 0 ? Frac(node, "cpu") * 100.0 : -1, CpuCores = (int)Num(node, "maxcpu"),
                    MemUsedBytes = Num(node, "mem"), MemTotalBytes = Num(node, "maxmem"),
                    DiskUsedBytes = Num(node, "disk"), DiskTotalBytes = Num(node, "maxdisk"),
                    UptimeSeconds = Num(node, "uptime"),
                });

                foreach (var vm in Arr(await _client.GetQemuAsync(nodeName, ct).ConfigureAwait(false)))
                {
                    var vmid = Str(vm, "vmid");
                    _repository.UpsertVm(new VmRecord
                    {
                        Id = $"pve-vm:{_client.Host}:{vmid}", VmId = vmid, Name = Str(vm, "name"),
                        NodeId = nodeId, Status = Str(vm, "status"),
                        CpuCores = (int)Num(vm, "cpus"), MemoryMb = Num(vm, "maxmem") / (1024 * 1024),
                        UptimeSeconds = Num(vm, "uptime"),
                    });
                    items++;
                }

                foreach (var ctr in Arr(await _client.GetLxcAsync(nodeName, ct).ConfigureAwait(false)))
                {
                    var ctid = Str(ctr, "vmid");
                    _repository.UpsertContainer(new ContainerRecord
                    {
                        Id = $"pve-lxc:{_client.Host}:{ctid}", ContainerId = ctid, Kind = "lxc",
                        Name = Str(ctr, "name"), NodeId = nodeId, Status = Str(ctr, "status"),
                    });
                    items++;
                }

                foreach (var st in Arr(await _client.GetStorageAsync(nodeName, ct).ConfigureAwait(false)))
                {
                    var storage = Str(st, "storage");
                    if (storage.Length == 0) continue;
                    var holdsBackups = Str(st, "content").Contains("backup", StringComparison.OrdinalIgnoreCase);
                    _repository.UpsertStoragePool(new StoragePoolRecord
                    {
                        Id = $"pve-storage:{_client.Host}:{nodeName}:{storage}", Name = storage,
                        NodeId = nodeId, Kind = Str(st, "type") + (holdsBackups ? " (backups)" : ""),
                        TotalBytes = Num(st, "total"), UsedBytes = Num(st, "used"),
                    });
                    items++;
                }

                foreach (var task in Arr(await _client.GetFailedTasksAsync(nodeName, ct).ConfigureAwait(false)))
                {
                    var record = new ProxmoxTaskRecord
                    {
                        Upid = Str(task, "upid"), Node = nodeName, Type = Str(task, "type"),
                        Status = Str(task, "status"), StartTime = Num(task, "starttime"),
                    };
                    if (record.Upid.Length == 0) continue;
                    _repository.RecordEvent(new HomelabEvent
                    {
                        Id = $"pve-task:{record.Upid}", // stable id → INSERT dedupes on re-sync
                        EventType = "proxmox_task_failed", SubjectKind = "proxmox_task", SubjectId = record.Upid,
                        Severity = "warning",
                        Message = $"{record.Type} on {record.Node}: {record.Status}",
                    });
                }
            }

            lock (_lock) { _state = "ok"; _lastRun = AnthillTime.NowUtc().ToIso(); _lastResult = $"ok: {items} item(s)"; }
            return HomelabProviderResult.Success($"proxmox sync ok ({items} items)", items);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail("proxmox sync timed out");
        }
        catch (Exception ex)
        {
            return Fail(ex.GetBaseException().Message);
        }

        HomelabProviderResult Fail(string message)
        {
            lock (_lock) { _state = "failing"; _lastRun = AnthillTime.NowUtc().ToIso(); _lastResult = "failed: " + message; }
            _repository.RecordEvent(new HomelabEvent
            {
                EventType = "provider_run", SubjectKind = "provider", SubjectId = Name,
                Severity = "warning", Message = $"{Name}: {message}",
            });
            return HomelabProviderResult.Failure(message);
        }
    }

    private static IEnumerable<JsonElement> Arr(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : Enumerable.Empty<JsonElement>();
    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _ => "",
            }
            : "";
    private static long Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? (long)Math.Round(v.GetDouble())
            : 0;
    /// <summary>v2.3.3: fractional values (e.g. Proxmox node cpu = 0.0431) that Num would round away.</summary>
    private static double Frac(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : -1;
}

/// <summary>Read-only Proxmox reachability check: GET /version → healthy. One GET, nothing else.</summary>
public sealed class ProxmoxHealthProvider : IHealthCheckProvider
{
    private readonly ProxmoxApiClient _client;
    public string Name => "proxmox-health";
    public ProxmoxHealthProvider(ProxmoxApiClient client) => _client = client;

    public async System.Threading.Tasks.Task<HealthCheckResult> CheckAsync(string target, CancellationToken ct)
    {
        var result = new HealthCheckResult
        {
            CheckKind = "proxmox_api", Target = target.Length > 0 ? target : _client.Host,
            CheckedAt = AnthillTime.NowUtc().ToIso(),
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var version = await _client.GetVersionAsync(ct).ConfigureAwait(false);
            result.Status = HealthStatus.Healthy;
            result.Detail = "PVE " + (version.ValueKind == JsonValueKind.Object && version.TryGetProperty("version", out var v) ? v.GetString() ?? "?" : "?");
        }
        catch (Exception ex)
        {
            result.Status = HealthStatus.Failed;
            result.Detail = ex.GetBaseException().Message;
        }
        result.LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        return result;
    }
}
