using System.Net.Http.Headers;
using System.Xml.Linq;
using Anthill.Core.Common;
using Anthill.Core.Health;
using Anthill.Core.Homelab;

namespace Anthill.Core.Integrations.Hyperv;

/// <summary>One virtual machine read back from a Hyper-V host over WinRM.</summary>
public sealed record HypervVm(string Name, string State);

/// <summary>
/// Read-only Hyper-V client over WinRM / WS-Management (v2.1.0). Windows exposes no HTTP GET API for
/// Hyper-V, so this speaks WS-Man to the WinRM endpoint (HTTPS 5986) — but ONLY the WS-Enumeration
/// <c>Enumerate</c> operation against read-only WMI classes (<c>Msvm_ComputerSystem</c> in
/// <c>root/virtualization/v2</c>). There is no <c>Invoke</c> (RequestStateChange / power), no
/// <c>Put</c>/<c>Create</c>/<c>Delete</c>, and no command-shell (<c>wsman .../cmd</c>) anywhere in this
/// class — so start/stop/checkpoint/reconfigure are structurally impossible, matching the read-only
/// discipline of the other integrations. Host must pass the target allowlist; the credential
/// (<c>DOMAIN\\user:password</c> or <c>user:password</c>) comes from the credential store per sync.
///
/// Enterprise setup: HTTPS WinRM listener with Basic auth over TLS (a read-only account is enough), or
/// front with a WinRM gateway. Never enable unencrypted Basic.
/// </summary>
public sealed class HypervWinRmClient
{
    private const string WmiBase = "http://schemas.microsoft.com/wbem/wsman/1/wmi/root/virtualization/v2/";
    private const string EnumAction = "http://schemas.xmlsoap.org/ws/2004/09/enumeration/Enumerate";

    private readonly IHomelabTargetGuard _targetGuard;
    private readonly Func<string?> _credentialProvider; // "user:password" / "DOMAIN\\user:password"
    private readonly bool _insecureTls;
    private readonly TimeSpan _timeout;

    public string Endpoint { get; }
    public string Host { get; }

    public HypervWinRmClient(string host, int port, IHomelabTargetGuard targetGuard,
        Func<string?> credentialProvider, bool insecureTls = false, TimeSpan? timeout = null)
    {
        Host = (host ?? "").Trim();
        Endpoint = $"https://{Host}:{(port > 0 ? port : 5986)}/wsman";
        _targetGuard = targetGuard;
        _credentialProvider = credentialProvider;
        _insecureTls = insecureTls;
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>Test seam: mock WinRM endpoint over plain HTTP on loopback.</summary>
    internal HypervWinRmClient(string endpoint, IHomelabTargetGuard targetGuard, Func<string?> credentialProvider, TimeSpan? timeout = null)
    {
        Endpoint = endpoint;
        Host = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "";
        _targetGuard = targetGuard;
        _credentialProvider = credentialProvider;
        _insecureTls = false;
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>The ONLY wire operation: a read-only WS-Man Enumerate of Hyper-V VMs.</summary>
    public async System.Threading.Tasks.Task<IReadOnlyList<HypervVm>> EnumerateVmsAsync(CancellationToken ct)
    {
        if (Host.Length == 0) throw new InvalidOperationException("Hyper-V host is not configured (homelab_hyperv_host).");
        if (!_targetGuard.IsAllowed(Host))
            throw new InvalidOperationException($"Hyper-V host '{Host}' is not on the homelab target allowlist — add it under /homelab/allowlist.");
        var cred = _credentialProvider();
        if (string.IsNullOrWhiteSpace(cred) || !cred.Contains(':'))
            throw new InvalidOperationException("Hyper-V credential is not configured — save 'user:password' under /homelab/credentials and set homelab_hyperv_credential_id.");

        var envelope = EnumerateEnvelope(WmiBase + "Msvm_ComputerSystem");
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(envelope, System.Text.Encoding.UTF8),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "UTF-8" };
        req.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cred.Trim())));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        using var resp = await HomelabHttpClients.Pick(_insecureTls).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Hyper-V WinRM returned HTTP {(int)resp.StatusCode} for Enumerate.");
        return ParseVms(body);
    }

    private static string EnumerateEnvelope(string resourceUri) =>
        $"""
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:w="http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd" xmlns:n="http://schemas.xmlsoap.org/ws/2004/09/enumeration">
          <s:Header>
            <a:Action s:mustUnderstand="true">{EnumAction}</a:Action>
            <a:To s:mustUnderstand="true">{System.Security.SecurityElement.Escape(resourceUri)}</a:To>
            <w:ResourceURI s:mustUnderstand="true">{System.Security.SecurityElement.Escape(resourceUri)}</w:ResourceURI>
            <a:MessageID>uuid:{Guid.NewGuid()}</a:MessageID>
            <a:ReplyTo><a:Address s:mustUnderstand="true">http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:Address></a:ReplyTo>
            <w:MaxEnvelopeSize s:mustUnderstand="true">512000</w:MaxEnvelopeSize>
            <w:OperationTimeout>PT60.000S</w:OperationTimeout>
          </s:Header>
          <s:Body>
            <n:Enumerate><w:OptimizeEnumeration/><w:MaxElements>32000</w:MaxElements></n:Enumerate>
          </s:Body>
        </s:Envelope>
        """;

    /// <summary>Parses Msvm_ComputerSystem items by local-name (namespace-agnostic, robust to WMI namespace drift).</summary>
    internal static IReadOnlyList<HypervVm> ParseVms(string soap)
    {
        var vms = new List<HypervVm>();
        XDocument doc;
        try { doc = XDocument.Parse(soap); } catch { return vms; }
        foreach (var cs in doc.Descendants().Where(e => e.Name.LocalName == "Msvm_ComputerSystem"))
        {
            string Child(string local) => cs.Elements().FirstOrDefault(e => e.Name.LocalName == local)?.Value?.Trim() ?? "";
            // Skip the host's own management OS (Caption "Hosting Computer System"); keep real VMs.
            var caption = Child("Caption");
            if (caption.Length > 0 && !caption.Contains("Virtual Machine", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Child("ElementName");
            if (name.Length == 0) name = Child("Name");
            if (name.Length == 0) continue;
            vms.Add(new HypervVm(name, MapState(Child("EnabledState"))));
        }
        return vms;
    }

    private static string MapState(string enabledState) => enabledState switch
    {
        "2" => "running", "3" => "stopped", "32768" => "paused", "32769" => "suspended",
        _ => enabledState.Length > 0 ? "state_" + enabledState : "unknown",
    };
}

/// <summary>Read-only Hyper-V inventory sync (v2.1.0): the host node + its VMs.</summary>
public sealed class HypervInventoryProvider : IInventoryProvider, IIntegrationStatusProvider
{
    private readonly HypervWinRmClient _client;
    private readonly IHomelabRepository _repository;
    private readonly object _lock = new();
    private string _state = "not_configured", _lastRun = "", _lastResult = "";

    public string Name => "hyperv-inventory";
    public HypervInventoryProvider(HypervWinRmClient client, IHomelabRepository repository)
    {
        _client = client;
        _repository = repository;
    }

    public IntegrationStatus GetStatus()
    {
        lock (_lock) return new IntegrationStatus
        {
            Name = Name, Kind = "hyperv", Enabled = true, State = _state, LastRun = _lastRun, LastResult = _lastResult,
        };
    }

    public async System.Threading.Tasks.Task<HomelabProviderResult> SyncInventoryAsync(CancellationToken ct)
    {
        try
        {
            var vms = await _client.EnumerateVmsAsync(ct).ConfigureAwait(false);
            var nodeId = $"hyperv-host:{_client.Host}";
            _repository.UpsertNode(new HomelabNode
            {
                Id = nodeId, Name = _client.Host, Kind = "hypervisor", Address = _client.Host,
                Os = "Windows / Hyper-V", RoleTags = new() { "hyperv", "windows" },
                Notes = $"vms={vms.Count}",
            }, changedBy: Name);
            var items = 1;
            foreach (var vm in vms)
            {
                _repository.UpsertVm(new VmRecord
                {
                    Id = $"hyperv-vm:{_client.Host}:{vm.Name}", VmId = vm.Name, Name = vm.Name,
                    NodeId = nodeId, Status = vm.State, CpuCores = 0, MemoryMb = 0, UptimeSeconds = 0,
                });
                items++;
            }
            lock (_lock) { _state = "ok"; _lastRun = AnthillTime.NowUtc().ToIso(); _lastResult = $"ok: {items} item(s)"; }
            return HomelabProviderResult.Success($"hyperv sync ok ({items} items)", items);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Fail("hyperv sync timed out"); }
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
}

/// <summary>Read-only Hyper-V reachability check: a VM enumerate that returns → healthy.</summary>
public sealed class HypervHealthProvider : IHealthCheckProvider
{
    private readonly HypervWinRmClient _client;
    public string Name => "hyperv-health";
    public HypervHealthProvider(HypervWinRmClient client) => _client = client;

    public async System.Threading.Tasks.Task<HealthCheckResult> CheckAsync(string target, CancellationToken ct)
    {
        var result = new HealthCheckResult
        {
            CheckKind = "hyperv_winrm", Target = target.Length > 0 ? target : _client.Host, CheckedAt = AnthillTime.NowUtc().ToIso(),
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var vms = await _client.EnumerateVmsAsync(ct).ConfigureAwait(false);
            result.Status = HealthStatus.Healthy;
            result.Detail = $"Hyper-V reachable ({vms.Count} VM(s))";
        }
        catch (Exception ex) { result.Status = HealthStatus.Failed; result.Detail = ex.GetBaseException().Message; }
        result.LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        return result;
    }
}
