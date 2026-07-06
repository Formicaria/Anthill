using System.Text.Json;
using Anthill.Core.Homelab;

namespace Anthill.Core.Integrations.Proxmox;

/// <summary>
/// Read-only Proxmox VE API client (v1.12.0, NORTH_STAR Phase 8).
///
/// SAFETY BY CONSTRUCTION: this class can only issue HTTP GET requests — there is no POST, PUT,
/// or DELETE anywhere in it, so start/stop/reboot/migrate/delete/clone/resize/config writes are
/// STRUCTURALLY impossible, not merely forbidden by policy. Tests assert both the type surface
/// and the wire traffic. Control actions, if they ever come, arrive in V2.1+ behind IApprovable.
///
/// Discipline (same as every homelab provider):
/// - The target host must pass the Homelab Target Allowlist (D1) before ANY request.
/// - The API token comes from the credential store per call and never leaves this class.
/// - Strict timeout per request; deterministic C# — never routed through the model router.
/// </summary>
public sealed class ProxmoxApiClient
{
    // TLS trust: homelab Proxmox nodes almost always run self-signed certs. Verification is
    // config-controlled (homelab_proxmox_insecure_tls, default false = verify). Two shared
    // handlers/clients — never per-request clients (socket exhaustion).
    //
    // AllowAutoRedirect=false: the allowlist gate in GetAsync() validates the configured Host, but
    // HttpClient follows 3xx redirects by default — a compromised or misconfigured node could bounce
    // this authenticated GET to a Location that was NEVER allowlist-checked (an SSRF hole straight
    // through the "safety by construction" premise). The PVE API never legitimately redirects, so a
    // redirect now surfaces as a clean non-success status instead of being chased off-allowlist.
    private static readonly HttpClient Verified = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly HttpClient Insecure = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly IHomelabTargetGuard _targetGuard;
    private readonly Func<string?> _tokenProvider; // returns "user@realm!tokenid=secret" or null
    private readonly bool _insecureTls;
    private readonly TimeSpan _timeout;

    public string BaseUrl { get; }
    public string Host { get; }

    public ProxmoxApiClient(string host, int port, IHomelabTargetGuard targetGuard,
        Func<string?> tokenProvider, bool insecureTls = false, TimeSpan? timeout = null)
    {
        Host = (host ?? "").Trim();
        BaseUrl = $"https://{Host}:{(port > 0 ? port : 8006)}/api2/json";
        _targetGuard = targetGuard;
        _tokenProvider = tokenProvider;
        _insecureTls = insecureTls;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Test seam: mock servers speak plain HTTP on loopback. Production stays https.</summary>
    internal ProxmoxApiClient(string baseUrl, IHomelabTargetGuard targetGuard, Func<string?> tokenProvider, TimeSpan? timeout = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        Host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : "";
        _targetGuard = targetGuard;
        _tokenProvider = tokenProvider;
        _insecureTls = false;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// The ONLY wire method in this class: an authenticated GET returning the PVE "data" element.
    /// Throws InvalidOperationException with a clean operator message on guard/credential problems.
    /// </summary>
    public async System.Threading.Tasks.Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        if (Host.Length == 0) throw new InvalidOperationException("Proxmox host is not configured (homelab_proxmox_host).");
        if (!_targetGuard.IsAllowed(Host))
            throw new InvalidOperationException($"Proxmox host '{Host}' is not on the homelab target allowlist — add it under /homelab/allowlist.");
        var token = _tokenProvider();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Proxmox API token credential is not configured (save it under /homelab/credentials and set homelab_proxmox_credential_id).");

        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + (path.StartsWith('/') ? path : "/" + path));
        req.Headers.TryAddWithoutValidation("Authorization", "PVEAPIToken=" + token.Trim());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        var http = _insecureTls ? Insecure : Verified;
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Proxmox API returned HTTP {(int)resp.StatusCode} for GET {path}.");
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("data", out var data)
            ? data.Clone()
            : doc.RootElement.Clone();
    }

    // ---- Typed read-only reads (each is one GET) ---------------------------------------------

    public System.Threading.Tasks.Task<JsonElement> GetVersionAsync(CancellationToken ct) => GetAsync("/version", ct);
    public System.Threading.Tasks.Task<JsonElement> GetNodesAsync(CancellationToken ct) => GetAsync("/nodes", ct);
    public System.Threading.Tasks.Task<JsonElement> GetQemuAsync(string node, CancellationToken ct) => GetAsync($"/nodes/{Uri.EscapeDataString(node)}/qemu", ct);
    public System.Threading.Tasks.Task<JsonElement> GetLxcAsync(string node, CancellationToken ct) => GetAsync($"/nodes/{Uri.EscapeDataString(node)}/lxc", ct);
    public System.Threading.Tasks.Task<JsonElement> GetStorageAsync(string node, CancellationToken ct) => GetAsync($"/nodes/{Uri.EscapeDataString(node)}/storage", ct);
    public System.Threading.Tasks.Task<JsonElement> GetFailedTasksAsync(string node, CancellationToken ct) => GetAsync($"/nodes/{Uri.EscapeDataString(node)}/tasks?errors=1&limit=25", ct);
}
