using System.Text.Json;
using Anthill.Core.Common;
using Anthill.Core.Health;
using Anthill.Core.Homelab;

namespace Anthill.Core.Integrations.Docker;

/// <summary>
/// Read-only Docker Engine API client (v2.1.0). SAFETY BY CONSTRUCTION: only HTTP GET requests exist
/// here — no /containers/{id}/start|stop|kill|restart, no POST/PUT/DELETE — so run/stop/remove/exec
/// are STRUCTURALLY impossible, exactly like the Proxmox client. Discipline mirrors every homelab
/// provider: the host must pass the target allowlist before any request; an optional bearer credential
/// (for socket-proxy setups that require one) comes from the credential store per call and never leaves
/// this class; strict per-request timeout; deterministic C#, never routed through the model router.
///
/// Enterprise transport: TLS to the Docker API port (default 2376). For sockets, front the engine with a
/// read-only TCP proxy (e.g. tecnativa/docker-socket-proxy with only CONTAINERS/IMAGES/INFO enabled).
/// </summary>
public sealed class DockerApiClient
{
    private readonly IHomelabTargetGuard _targetGuard;
    private readonly Func<string?> _tokenProvider; // optional bearer, or null
    private readonly bool _insecureTls;
    private readonly TimeSpan _timeout;

    public string BaseUrl { get; }
    public string Host { get; }

    public DockerApiClient(string host, int port, IHomelabTargetGuard targetGuard,
        Func<string?> tokenProvider, bool insecureTls = false, TimeSpan? timeout = null)
    {
        Host = (host ?? "").Trim();
        BaseUrl = $"https://{Host}:{(port > 0 ? port : 2376)}";
        _targetGuard = targetGuard;
        _tokenProvider = tokenProvider;
        _insecureTls = insecureTls;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Test seam: mock servers speak plain HTTP on loopback. Production stays https.</summary>
    internal DockerApiClient(string baseUrl, IHomelabTargetGuard targetGuard, Func<string?> tokenProvider, TimeSpan? timeout = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        Host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : "";
        _targetGuard = targetGuard;
        _tokenProvider = tokenProvider;
        _insecureTls = false;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>The ONLY wire method: an authenticated GET returning the parsed JSON root.</summary>
    public async System.Threading.Tasks.Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        if (Host.Length == 0) throw new InvalidOperationException("Docker host is not configured (homelab_docker_host).");
        if (!_targetGuard.IsAllowed(Host))
            throw new InvalidOperationException($"Docker host '{Host}' is not on the homelab target allowlist — add it under /homelab/allowlist.");

        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + (path.StartsWith('/') ? path : "/" + path));
        var token = _tokenProvider();
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token.Trim());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        using var resp = await HomelabHttpClients.Pick(_insecureTls).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Docker API returned HTTP {(int)resp.StatusCode} for GET {path}.");
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    public System.Threading.Tasks.Task<JsonElement> GetVersionAsync(CancellationToken ct) => GetAsync("/version", ct);
    public System.Threading.Tasks.Task<JsonElement> GetInfoAsync(CancellationToken ct) => GetAsync("/info", ct);
    public System.Threading.Tasks.Task<JsonElement> GetContainersAsync(CancellationToken ct) => GetAsync("/containers/json?all=1", ct);
    public System.Threading.Tasks.Task<JsonElement> GetImagesAsync(CancellationToken ct) => GetAsync("/images/json", ct);
    public System.Threading.Tasks.Task<JsonElement> GetVolumesAsync(CancellationToken ct) => GetAsync("/volumes", ct);
}

/// <summary>
/// Read-only Docker inventory sync (v2.1.0). Registers the engine as a container-host node and upserts
/// its containers and volumes into the shared homelab inventory (stable ids → re-sync is idempotent).
/// Deterministic C#; no write path to Docker exists anywhere in this integration.
/// </summary>
public sealed class DockerInventoryProvider : IInventoryProvider, IIntegrationStatusProvider
{
    private readonly DockerApiClient _client;
    private readonly IHomelabRepository _repository;
    private readonly object _lock = new();
    private string _state = "not_configured", _lastRun = "", _lastResult = "";

    public string Name => "docker-inventory";
    public DockerInventoryProvider(DockerApiClient client, IHomelabRepository repository)
    {
        _client = client;
        _repository = repository;
    }

    public IntegrationStatus GetStatus()
    {
        lock (_lock) return new IntegrationStatus
        {
            Name = Name, Kind = "docker", Enabled = true, State = _state, LastRun = _lastRun, LastResult = _lastResult,
        };
    }

    public async System.Threading.Tasks.Task<HomelabProviderResult> SyncInventoryAsync(CancellationToken ct)
    {
        try
        {
            var items = 0;
            var info = await _client.GetInfoAsync(ct).ConfigureAwait(false);
            var engineName = Str(info, "Name");
            if (engineName.Length == 0) engineName = _client.Host;
            var nodeId = $"docker-host:{_client.Host}";
            _repository.UpsertNode(new HomelabNode
            {
                Id = nodeId, Name = engineName, Kind = "container_host", Address = _client.Host,
                Os = (Str(info, "OperatingSystem") + " / Docker " + Str(info, "ServerVersion")).Trim(' ', '/'),
                RoleTags = new() { "docker" },
                Notes = $"containers={Num(info, "Containers")} running={Num(info, "ContainersRunning")} images={Num(info, "Images")} cpus={Num(info, "NCPU")}",
            }, changedBy: Name);
            items++;

            foreach (var c in Arr(await _client.GetContainersAsync(ct).ConfigureAwait(false)))
            {
                var id = Str(c, "Id");
                if (id.Length == 0) continue;
                var name = FirstName(c);
                _repository.UpsertContainer(new ContainerRecord
                {
                    Id = $"docker-ct:{_client.Host}:{id[..Math.Min(12, id.Length)]}",
                    ContainerId = id[..Math.Min(12, id.Length)], Kind = "docker",
                    Name = name.Length > 0 ? name : Str(c, "Image"), NodeId = nodeId, Status = Str(c, "State"),
                });
                items++;
            }

            foreach (var v in Arr(VolumesArray(await _client.GetVolumesAsync(ct).ConfigureAwait(false))))
            {
                var vname = Str(v, "Name");
                if (vname.Length == 0) continue;
                _repository.UpsertStoragePool(new StoragePoolRecord
                {
                    Id = $"docker-vol:{_client.Host}:{vname}", Name = vname, NodeId = nodeId,
                    Kind = "docker volume (" + Str(v, "Driver") + ")", TotalBytes = 0, UsedBytes = 0,
                });
                items++;
            }

            lock (_lock) { _state = "ok"; _lastRun = AnthillTime.NowUtc().ToIso(); _lastResult = $"ok: {items} item(s)"; }
            return HomelabProviderResult.Success($"docker sync ok ({items} items)", items);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Fail("docker sync timed out"); }
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

    // /volumes returns {"Volumes":[...]}; guard for either an object-wrapper or a bare array.
    private static JsonElement VolumesArray(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Volumes", out var v) ? v : root;

    private static string FirstName(JsonElement container)
    {
        if (container.ValueKind == JsonValueKind.Object && container.TryGetProperty("Names", out var names)
            && names.ValueKind == JsonValueKind.Array)
            foreach (var n in names.EnumerateArray())
                return (n.GetString() ?? "").TrimStart('/');
        return "";
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

/// <summary>Read-only Docker reachability check: GET /version → healthy. One GET, nothing else.</summary>
public sealed class DockerHealthProvider : IHealthCheckProvider
{
    private readonly DockerApiClient _client;
    public string Name => "docker-health";
    public DockerHealthProvider(DockerApiClient client) => _client = client;

    public async System.Threading.Tasks.Task<HealthCheckResult> CheckAsync(string target, CancellationToken ct)
    {
        var result = new HealthCheckResult
        {
            CheckKind = "docker_api", Target = target.Length > 0 ? target : _client.Host, CheckedAt = AnthillTime.NowUtc().ToIso(),
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var version = await _client.GetVersionAsync(ct).ConfigureAwait(false);
            result.Status = HealthStatus.Healthy;
            result.Detail = "Docker " + (version.ValueKind == JsonValueKind.Object && version.TryGetProperty("Version", out var v) ? v.GetString() ?? "?" : "?");
        }
        catch (Exception ex) { result.Status = HealthStatus.Failed; result.Detail = ex.GetBaseException().Message; }
        result.LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        return result;
    }
}
