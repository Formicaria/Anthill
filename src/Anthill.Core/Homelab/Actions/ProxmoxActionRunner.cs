using System.Text.Json;
using Anthill.Core.Configuration;

namespace Anthill.Core.Homelab.Actions;

/// <summary>
/// v2.3.1 — the first WRITE-capable infrastructure client. Deliberately separate from
/// <c>ProxmoxApiClient</c> so the read path stays structurally GET-only. This client can reach
/// ONLY the endpoint shapes required by the allowlisted action catalog: guest status changes
/// (start/stop/reboot), snapshot creation, and vzdump backup. Any other path is refused here,
/// structurally, before any network I/O — a compromised caller cannot turn this into a general
/// Proxmox client. Never logs the token secret.
/// </summary>
public sealed class ProxmoxActionClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly Func<string> _tokenProvider;

    /// <param name="tokenProvider">Pulled from the credential store per client (mirrors the
    /// v1.12 read client) — the token is never cached in config or logged.</param>
    public ProxmoxActionClient(string host, int port, Func<string> tokenProvider,
        bool insecureTls = false, string protocol = "https")
    {
        var scheme = string.Equals(protocol, "http", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
        _base = $"{scheme}://{host}:{port}/api2/json";
        _tokenProvider = tokenProvider;
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (insecureTls)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private HttpRequestMessage Req(HttpMethod method, string path, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, _base + path) { Content = content };
        req.Headers.TryAddWithoutValidation("Authorization", $"PVEAPIToken={_tokenProvider()}");
        return req;
    }

    /// <summary>Structural allowlist: the only write shapes this client will ever emit.</summary>
    private static bool IsAllowedWritePath(string path) =>
        System.Text.RegularExpressions.Regex.IsMatch(path,
            @"^/nodes/[^/]+/(qemu|lxc)/\d+/status/(start|stop|shutdown|reboot)$")
        || System.Text.RegularExpressions.Regex.IsMatch(path, @"^/nodes/[^/]+/(qemu|lxc)/\d+/snapshot$")
        || System.Text.RegularExpressions.Regex.IsMatch(path, @"^/nodes/[^/]+/vzdump$");

    public async Task<string> PostAsync(string path, Dictionary<string, string>? form, CancellationToken ct)
    {
        if (!IsAllowedWritePath(path))
            throw new InvalidOperationException($"Refused: '{path}' is not an allowlisted action endpoint.");
        var content = new FormUrlEncodedContent(form ?? new Dictionary<string, string>());
        using var req = Req(HttpMethod.Post, path, content);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Proxmox {(int)resp.StatusCode} on {path}: {Truncate(body)}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() ?? "" : ""; // UPID of the queued task, when Proxmox returns one
    }

    /// <summary>Read-only status probe used ONLY for post-execution verification.</summary>
    public async Task<string> GetGuestStatusAsync(string node, string kind, string vmid, CancellationToken ct)
    {
        var path = $"/nodes/{Uri.EscapeDataString(node)}/{kind}/{Uri.EscapeDataString(vmid)}/status/current";
        using var req = Req(HttpMethod.Get, path);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Proxmox {(int)resp.StatusCode} on {path}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("status", out var s)
            ? s.GetString() ?? "unknown" : "unknown";
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
    public void Dispose() => _http.Dispose();
}

/// <summary>
/// v2.3.1 — runs approved catalog actions against real Proxmox VE. Registered only when BOTH
/// the Proxmox integration and the new opt-in gate <c>homelab_proxmox_write_actions_enabled</c>
/// are on (default OFF — connecting Proxmox alone never grants write capability). Target ids use
/// the inventory form "node/vmid" (e.g. "pve1/104"). Every execution is preceded by the
/// ActionExecutor's kill-switch, approval, catalog, and rollback-note checks; verification reads
/// the guest state back and reports honestly (a submitted-but-unconfirmed backup says so).
/// </summary>
public sealed class ProxmoxActionRunner : IHomelabActionRunner
{
    private static readonly string[] Supported =
    {
        "start_vm", "stop_vm", "restart_vm",
        "start_container", "stop_container", "restart_container",
        "create_snapshot", "run_backup",
    };

    private readonly Func<ProxmoxActionClient> _clientFactory;
    public string Name => "proxmox";

    /// <param name="clientFactory">Supplied by the host with the credential-store token provider
    /// (mirrors how the read-only client is constructed); tests inject a fake.</param>
    public ProxmoxActionRunner(Func<ProxmoxActionClient> clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public bool CanRun(ActionProposal proposal) =>
        Supported.Contains(proposal.ActionType) && TryParseTarget(proposal, out _, out _, out _);

    private static bool TryParseTarget(ActionProposal p, out string node, out string kind, out string vmid)
    {
        node = kind = vmid = "";
        var parts = (p.TargetId ?? "").Split('/');
        if (parts.Length != 2 || parts[0].Length == 0 || !parts[1].All(char.IsDigit)) return false;
        node = parts[0]; vmid = parts[1];
        kind = p.ActionType.Contains("container") ? "lxc"
             : p.ActionType.Contains("vm") ? "qemu"
             : string.Equals(p.TargetKind, "container", StringComparison.OrdinalIgnoreCase) ? "lxc" : "qemu";
        return true;
    }

    private static (string op, string expect) OpFor(string actionType) => actionType switch
    {
        "start_vm" or "start_container" => ("start", "running"),
        "stop_vm" or "stop_container" => ("shutdown", "stopped"), // clean shutdown, never a hard stop
        "restart_vm" or "restart_container" => ("reboot", "running"),
        _ => ("", ""),
    };

    public async Task<ActionRunResult> ExecuteAsync(ActionProposal p, CancellationToken ct = default)
    {
        if (!TryParseTarget(p, out var node, out var kind, out var vmid))
            return new(false, $"Target '{p.TargetId}' is not in node/vmid form.");
        using var client = _clientFactory();
        var (op, _) = OpFor(p.ActionType);
        string upid;
        if (op.Length > 0)
            upid = await client.PostAsync($"/nodes/{node}/{kind}/{vmid}/status/{op}", null, ct);
        else if (p.ActionType == "create_snapshot")
            upid = await client.PostAsync($"/nodes/{node}/{kind}/{vmid}/snapshot",
                new() { ["snapname"] = $"anthill-{DateTime.UtcNow:yyyyMMdd-HHmmss}" }, ct);
        else // run_backup
            upid = await client.PostAsync($"/nodes/{node}/vzdump", new() { ["vmid"] = vmid }, ct);
        return new(true, $"{p.ActionType} submitted for {kind}/{vmid} on {node}"
            + (upid.Length > 0 ? $" (task {upid})" : ""));
    }

    public Task<ActionRunResult> DryRunAsync(ActionProposal p, CancellationToken ct = default)
    {
        if (!TryParseTarget(p, out var node, out var kind, out var vmid))
            return Task.FromResult(new ActionRunResult(false, $"Target '{p.TargetId}' is not in node/vmid form (e.g. pve1/104)."));
        var (op, _) = OpFor(p.ActionType);
        var what = op.Length > 0 ? $"POST /nodes/{node}/{kind}/{vmid}/status/{op}"
            : p.ActionType == "create_snapshot" ? $"POST /nodes/{node}/{kind}/{vmid}/snapshot (timestamped anthill-* name)"
            : $"POST /nodes/{node}/vzdump for vmid {vmid}";
        return Task.FromResult(new ActionRunResult(true, $"Would issue {what}. No other endpoint is reachable by this client."));
    }

    public async Task<ActionRunResult> VerifyAsync(ActionProposal p, CancellationToken ct = default)
    {
        if (!TryParseTarget(p, out var node, out var kind, out var vmid))
            return new(false, "unverifiable target id");
        var (op, expect) = OpFor(p.ActionType);
        if (op.Length == 0)
            return new(true, $"{p.ActionType} task was accepted by Proxmox; completion is tracked in the Proxmox task log (not silently assumed done).");
        using var client = _clientFactory();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            var status = await client.GetGuestStatusAsync(node, kind, vmid, ct);
            if (status == expect) return new(true, $"{kind}/{vmid} is '{status}' as expected");
        }
        var last = await client.GetGuestStatusAsync(node, kind, vmid, ct);
        return new(false, $"{kind}/{vmid} is '{last}', expected '{expect}' after {p.ActionType}");
    }
}
