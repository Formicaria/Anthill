namespace Anthill.Core.Integrations;

/// <summary>
/// Shared HTTP plumbing for the read-only virtualization integrations (v2.1.0: ESXi/vSphere, Docker;
/// Hyper-V reuses the handlers for its WinRM transport). Two process-wide <see cref="HttpClient"/>s —
/// verified and insecure-TLS — both with <c>AllowAutoRedirect = false</c>, the same SSRF hardening the
/// Proxmox client uses: the target-allowlist gate validates the configured host, but a 3xx from a
/// compromised or misconfigured node would otherwise bounce an authenticated request to a Location that
/// was never allowlist-checked. Never construct per-request clients (socket exhaustion).
/// </summary>
public static class HomelabHttpClients
{
    public static readonly HttpClient Verified = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = Timeout.InfiniteTimeSpan };

    public static readonly HttpClient Insecure = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        // Homelab hypervisors almost always run self-signed certs. Config-controlled per integration
        // (homelab_<kind>_insecure_tls, default false = verify). Same posture as the Proxmox client.
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    public static HttpClient Pick(bool insecureTls) => insecureTls ? Insecure : Verified;
}
