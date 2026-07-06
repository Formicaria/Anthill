using System.Text.Json;
using Anthill.Core.Common;
using Anthill.Core.Health;
using Anthill.Core.Homelab;

namespace Anthill.Core.Integrations.VSphere;

/// <summary>
/// Read-only vSphere / ESXi client over the vCenter REST API (v2.1.0). Every INVENTORY read is a GET;
/// the ONLY non-GET is a single POST to <c>/api/session</c>, which is authentication (it mints a session
/// token) and changes no infrastructure — no VM/host power, snapshot, migrate, reconfigure, or delete
/// call exists anywhere in this class, so control actions are structurally impossible. Discipline mirrors
/// every homelab provider: the host must pass the target allowlist before any request; the credential
/// (a <c>username:password</c> string) comes from the credential store per sync and never leaves this
/// class; strict per-request timeout; deterministic C#.
///
/// Enterprise transport: HTTPS to vCenter/ESXi (default 443). Self-signed certs → homelab_esxi_insecure_tls.
/// A read-only role (e.g. the built-in <c>Read-only</c> vSphere role) is all this needs.
/// </summary>
public sealed class VSphereApiClient
{
    private readonly IHomelabTargetGuard _targetGuard;
    private readonly Func<string?> _credentialProvider; // "username:password", or null
    private readonly bool _insecureTls;
    private readonly TimeSpan _timeout;
    private string? _sessionId;

    public string BaseUrl { get; }
    public string Host { get; }

    public VSphereApiClient(string host, int port, IHomelabTargetGuard targetGuard,
        Func<string?> credentialProvider, bool insecureTls = false, TimeSpan? timeout = null)
    {
        Host = (host ?? "").Trim();
        BaseUrl = $"https://{Host}:{(port > 0 ? port : 443)}";
        _targetGuard = targetGuard;
        _credentialProvider = credentialProvider;
        _insecureTls = insecureTls;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>Test seam: mock servers speak plain HTTP on loopback. Production stays https.</summary>
    internal VSphereApiClient(string baseUrl, IHomelabTargetGuard targetGuard, Func<string?> credentialProvider, TimeSpan? timeout = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        Host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : "";
        _targetGuard = targetGuard;
        _credentialProvider = credentialProvider;
        _insecureTls = false;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    private void GuardHost()
    {
        if (Host.Length == 0) throw new InvalidOperationException("vSphere/ESXi host is not configured (homelab_esxi_host).");
        if (!_targetGuard.IsAllowed(Host))
            throw new InvalidOperationException($"vSphere host '{Host}' is not on the homelab target allowlist — add it under /homelab/allowlist.");
    }

    /// <summary>Authenticate (POST /api/session with Basic) → session token. Auth only; no state change.</summary>
    private async System.Threading.Tasks.Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_sessionId is not null) return;
        var cred = _credentialProvider();
        if (string.IsNullOrWhiteSpace(cred) || !cred.Contains(':'))
            throw new InvalidOperationException("vSphere credential is not configured — save 'username:password' under /homelab/credentials and set homelab_esxi_credential_id.");
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/session");
        req.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cred.Trim())));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        using var resp = await HomelabHttpClients.Pick(_insecureTls).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"vSphere authentication failed: HTTP {(int)resp.StatusCode} from POST /api/session.");
        var body = (await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false)).Trim();
        // vCenter returns the session id as a quoted JSON string.
        try { using var doc = JsonDocument.Parse(body); _sessionId = doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() : body.Trim('"'); }
        catch { _sessionId = body.Trim('"'); }
        if (string.IsNullOrWhiteSpace(_sessionId))
            throw new InvalidOperationException("vSphere authentication returned an empty session id.");
    }

    /// <summary>Authenticated GET returning the parsed JSON root (vCenter 8 REST returns the array/object directly).</summary>
    public async System.Threading.Tasks.Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        GuardHost();
        await EnsureSessionAsync(ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + (path.StartsWith('/') ? path : "/" + path));
        req.Headers.TryAddWithoutValidation("vmware-api-session-id", _sessionId);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        using var resp = await HomelabHttpClients.Pick(_insecureTls).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"vSphere API returned HTTP {(int)resp.StatusCode} for GET {path}.");
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
        // Some deployments wrap list results in {"value":[...]}; unwrap when present.
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("value", out var value)
            ? value.Clone() : doc.RootElement.Clone();
    }

    public System.Threading.Tasks.Task<JsonElement> GetHostsAsync(CancellationToken ct) => GetAsync("/api/vcenter/host", ct);
    public System.Threading.Tasks.Task<JsonElement> GetVmsAsync(CancellationToken ct) => GetAsync("/api/vcenter/vm", ct);
    public System.Threading.Tasks.Task<JsonElement> GetDatastoresAsync(CancellationToken ct) => GetAsync("/api/vcenter/datastore", ct);
}

/// <summary>Read-only vSphere inventory sync (v2.1.0): hosts → hypervisor nodes, VMs, datastores.</summary>
public sealed class VSphereInventoryProvider : IInventoryProvider, IIntegrationStatusProvider
{
    private readonly VSphereApiClient _client;
    private readonly IHomelabRepository _repository;
    private readonly object _lock = new();
    private string _state = "not_configured", _lastRun = "", _lastResult = "";

    public string Name => "esxi-inventory";
    public VSphereInventoryProvider(VSphereApiClient client, IHomelabRepository repository)
    {
        _client = client;
        _repository = repository;
    }

    public IntegrationStatus GetStatus()
    {
        lock (_lock) return new IntegrationStatus
        {
            Name = Name, Kind = "esxi", Enabled = true, State = _state, LastRun = _lastRun, LastResult = _lastResult,
        };
    }

    public async System.Threading.Tasks.Task<HomelabProviderResult> SyncInventoryAsync(CancellationToken ct)
    {
        try
        {
            var items = 0;
            string firstHostId = "";
            foreach (var host in Arr(await _client.GetHostsAsync(ct).ConfigureAwait(false)))
            {
                var hostMoid = Str(host, "host");
                var name = Str(host, "name");
                if (hostMoid.Length == 0 && name.Length == 0) continue;
                var nodeId = $"esxi-host:{_client.Host}:{(hostMoid.Length > 0 ? hostMoid : name)}";
                if (firstHostId.Length == 0) firstHostId = nodeId;
                _repository.UpsertNode(new HomelabNode
                {
                    Id = nodeId, Name = name.Length > 0 ? name : hostMoid, Kind = "hypervisor", Address = _client.Host,
                    Os = "VMware ESXi", RoleTags = new() { "esxi", "vmware" },
                    Notes = $"connection={Str(host, "connection_state")} power={Str(host, "power_state")}",
                }, changedBy: Name);
                items++;
            }
            // If the endpoint exposed no host list (single-ESXi REST), anchor VMs to a synthetic node.
            if (firstHostId.Length == 0)
            {
                firstHostId = $"esxi-host:{_client.Host}";
                _repository.UpsertNode(new HomelabNode
                {
                    Id = firstHostId, Name = _client.Host, Kind = "hypervisor", Address = _client.Host,
                    Os = "VMware ESXi", RoleTags = new() { "esxi", "vmware" }, Notes = "vSphere endpoint",
                }, changedBy: Name);
                items++;
            }

            foreach (var vm in Arr(await _client.GetVmsAsync(ct).ConfigureAwait(false)))
            {
                var moid = Str(vm, "vm");
                if (moid.Length == 0) continue;
                _repository.UpsertVm(new VmRecord
                {
                    Id = $"esxi-vm:{_client.Host}:{moid}", VmId = moid, Name = Str(vm, "name"), NodeId = firstHostId,
                    Status = Str(vm, "power_state") == "POWERED_ON" ? "running" : "stopped",
                    CpuCores = (int)Num(vm, "cpu_count"), MemoryMb = Num(vm, "memory_size_MiB"), UptimeSeconds = 0,
                });
                items++;
            }

            foreach (var ds in Arr(await _client.GetDatastoresAsync(ct).ConfigureAwait(false)))
            {
                var dsName = Str(ds, "name");
                if (dsName.Length == 0) continue;
                var capacity = Num(ds, "capacity");
                _repository.UpsertStoragePool(new StoragePoolRecord
                {
                    Id = $"esxi-ds:{_client.Host}:{Str(ds, "datastore")}", Name = dsName, NodeId = firstHostId,
                    Kind = "datastore (" + Str(ds, "type") + ")", TotalBytes = capacity, UsedBytes = capacity - Num(ds, "free_space"),
                });
                items++;
            }

            lock (_lock) { _state = "ok"; _lastRun = AnthillTime.NowUtc().ToIso(); _lastResult = $"ok: {items} item(s)"; }
            return HomelabProviderResult.Success($"esxi sync ok ({items} items)", items);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Fail("esxi sync timed out"); }
        catch (Exception ex) { return Fail(ex.GetBaseException().Message); }

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
            ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Number => v.GetRawText(), _ => "" }
            : "";
    private static long Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? (long)Math.Round(v.GetDouble()) : 0;
}

/// <summary>Read-only vSphere reachability check: GET /api/vcenter/host → healthy.</summary>
public sealed class VSphereHealthProvider : IHealthCheckProvider
{
    private readonly VSphereApiClient _client;
    public string Name => "esxi-health";
    public VSphereHealthProvider(VSphereApiClient client) => _client = client;

    public async System.Threading.Tasks.Task<HealthCheckResult> CheckAsync(string target, CancellationToken ct)
    {
        var result = new HealthCheckResult
        {
            CheckKind = "esxi_api", Target = target.Length > 0 ? target : _client.Host, CheckedAt = AnthillTime.NowUtc().ToIso(),
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var hosts = await _client.GetHostsAsync(ct).ConfigureAwait(false);
            var n = hosts.ValueKind == JsonValueKind.Array ? hosts.GetArrayLength() : 0;
            result.Status = HealthStatus.Healthy;
            result.Detail = $"vSphere reachable ({n} host(s))";
        }
        catch (Exception ex) { result.Status = HealthStatus.Failed; result.Detail = ex.GetBaseException().Message; }
        result.LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        return result;
    }
}
