using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Anthill.Core.Common;
using Anthill.Core.Homelab;

namespace Anthill.Core.Integrations.Download;

/// <summary>
/// v2.5.5 Console Refit R5 Wave 1 (docs/CONSOLE_REFIT.md) — download-client integrations:
/// qBittorrent, Transmission, Deluge (torrent) and SABnzbd, NZBGet (usenet). Homarr/Homepage are
/// the UX reference, never copied.
///
/// READ-ONLY BY CONSTRUCTION. Unlike the *arr/Proxmox GET-only clients, three of these five speak
/// RPC-over-POST even to READ state (Transmission's session handshake, Deluge's JSON-RPC, and
/// qBittorrent's cookie login). So "GET-only" is impossible at the protocol level — instead the
/// guarantee is enforced structurally a different way: the ONLY public operation on this client is
/// <see cref="ProbeAsync"/>, and every request it issues names a hardcoded READ method. There is no
/// public (or private) code path that pauses, resumes, deletes, adds, reprioritises, or otherwise
/// mutates a transfer — a torrent/nzb write is not expressible on this type. Control actions, if
/// they ever come, arrive later behind the approval-gated action pipeline (NORTH_STAR), exactly as
/// planned for Proxmox. Tests assert the public surface (no mutating verbs) and the D1 allowlist +
/// credential gate that runs before ANY I/O.
///
/// Discipline is identical to every other integration: the target host must be on the D1 homelab
/// allowlist before a single byte leaves; the secret lives write-only in the credential store and
/// is fetched per probe (never logged); strict timeout; redirects are never followed (SSRF); the
/// sync is deterministic C# — never the model router.
/// </summary>
public sealed class DownloadClient : IDisposable
{
    /// <summary>kind → (category, auth mode, secret hint). The catalog of supported download clients.</summary>
    public static readonly IReadOnlyDictionary<string, (string AuthMode, string SecretHint)> Kinds =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["qbittorrent"] = ("basic", "username:password"),
            ["transmission"] = ("basic", "username:password"),
            ["deluge"] = ("token", "web password"),
            ["sabnzbd"] = ("api_key", "API key"),
            ["nzbget"] = ("basic", "username:password"),
        };

    private readonly string _kind;
    private readonly Uri _base;
    private readonly IHomelabTargetGuard _guard;
    private readonly Func<string?> _secretProvider;
    private readonly HttpClient _http;
    private string _transmissionSessionId = ""; // set by the 409 handshake; read-only bookkeeping

    public DownloadClient(string kind, string url, IHomelabTargetGuard guard, Func<string?> secretProvider, TimeSpan? timeout = null)
    {
        _kind = (kind ?? "").Trim().ToLowerInvariant();
        _base = new Uri(url.TrimEnd('/') + "/", UriKind.Absolute);
        _guard = guard;
        _secretProvider = secretProvider;
        // Per-instance handler: a CookieContainer is required because qBittorrent (SID) and Deluge
        // (_session_id) authenticate by cookie across the probe's requests. AllowAutoRedirect=false
        // so a compromised/misconfigured host can never bounce an authenticated request to a
        // Location the allowlist never vetted (same SSRF hardening as the Proxmox client).
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() })
        { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// The ONLY wire operation on this client: authenticate if the protocol needs it, then read a
    /// normalized snapshot (version, state, down/up rate, active/total counts). Throws
    /// InvalidOperationException with a clean operator message on guard/credential/protocol problems.
    /// </summary>
    public async System.Threading.Tasks.Task<DownloadSnapshot> ProbeAsync(CancellationToken ct)
    {
        if (!_guard.IsAllowed(_base.Host))
            throw new InvalidOperationException($"Refused: '{_base.Host}' is not on the homelab target allowlist.");
        if (string.IsNullOrWhiteSpace(_secretProvider()))
            throw new InvalidOperationException($"{_kind} credential is not configured (stored write-only in the credential store).");

        return _kind switch
        {
            "qbittorrent" => await ProbeQbittorrentAsync(ct).ConfigureAwait(false),
            "transmission" => await ProbeTransmissionAsync(ct).ConfigureAwait(false),
            "deluge" => await ProbeDelugeAsync(ct).ConfigureAwait(false),
            "sabnzbd" => await ProbeSabnzbdAsync(ct).ConfigureAwait(false),
            "nzbget" => await ProbeNzbgetAsync(ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported download client kind '{_kind}'."),
        };
    }

    // ---- Shared read transport (private — no public write path can exist) ----------------------

    private async System.Threading.Tasks.Task<(HttpStatusCode Status, string Body, HttpResponseHeaders Headers)> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using (req)
        using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (resp.StatusCode, body, resp.Headers);
        }
    }

    private System.Threading.Tasks.Task<(HttpStatusCode, string, HttpResponseHeaders)> GetAsync(string relative, CancellationToken ct, string? basicAuth = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, relative.TrimStart('/')));
        if (basicAuth is not null) req.Headers.TryAddWithoutValidation("Authorization", "Basic " + basicAuth);
        return SendAsync(req, ct);
    }

    private static JsonElement ParseJson(string body)
    {
        try { return JsonDocument.Parse(body).RootElement.Clone(); }
        catch { throw new InvalidOperationException("Response was not valid JSON (is the URL/port pointing at the right service?)."); }
    }

    private static (string User, string Pass) SplitBasic(string? secret)
    {
        var s = (secret ?? "").Trim();
        var i = s.IndexOf(':');
        return i < 0 ? ("", s) : (s[..i], s[(i + 1)..]);
    }

    private static string BasicHeader(string user, string pass) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

    /// <summary>SABnzbd reports numbers as strings ("2500.00") but other builds as JSON numbers.</summary>
    private static double AsDouble(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.TryGetDouble(out var d) ? d : 0,
        JsonValueKind.String => double.TryParse(e.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0,
        _ => 0,
    };

    private static int AsInt(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.TryGetInt32(out var n) ? n : 0,
        JsonValueKind.String => int.TryParse(e.GetString(), out var n) ? n : 0,
        _ => 0,
    };

    // ---- qBittorrent (Web API v2): cookie login, then GET reads --------------------------------

    private async System.Threading.Tasks.Task<DownloadSnapshot> ProbeQbittorrentAsync(CancellationToken ct)
    {
        var (user, pass) = SplitBasic(_secretProvider());
        // Login is a POST but changes no transfer state — it only mints a session cookie. qBittorrent
        // requires a Referer matching the host or it rejects the request as CSRF.
        var login = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "api/v2/auth/login"))
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = user, ["password"] = pass }) };
        login.Headers.TryAddWithoutValidation("Referer", _base.GetLeftPart(UriPartial.Authority));
        var (ls, lb, _) = await SendAsync(login, ct).ConfigureAwait(false);
        if (ls == HttpStatusCode.Forbidden) throw new InvalidOperationException("qBittorrent refused login (IP temporarily banned after failed attempts).");
        if (ls != HttpStatusCode.OK || !lb.Trim().StartsWith("Ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("qBittorrent login failed (check username/password).");

        var version = "";
        try { var (_, vb, _) = await GetAsync("api/v2/app/version", ct).ConfigureAwait(false); version = vb.Trim().TrimStart('v'); } catch { }

        long down = 0, up = 0;
        var (ts, tb, _) = await GetAsync("api/v2/transfer/info", ct).ConfigureAwait(false);
        if (ts == HttpStatusCode.OK)
        {
            var t = ParseJson(tb);
            down = t.TryGetProperty("dl_info_speed", out var d) && d.TryGetInt64(out var dn) ? dn : 0;
            up = t.TryGetProperty("up_info_speed", out var u) && u.TryGetInt64(out var un) ? un : 0;
        }

        int total = 0, active = 0;
        var (qs, qb, _) = await GetAsync("api/v2/torrents/info", ct).ConfigureAwait(false);
        if (qs == HttpStatusCode.OK)
        {
            var arr = ParseJson(qb);
            if (arr.ValueKind == JsonValueKind.Array)
            {
                total = arr.GetArrayLength();
                foreach (var it in arr.EnumerateArray())
                {
                    var dl = it.TryGetProperty("dlspeed", out var ds) && ds.TryGetInt64(out var dv) ? dv : 0;
                    var ul = it.TryGetProperty("upspeed", out var us) && us.TryGetInt64(out var uv) ? uv : 0;
                    if (dl > 0 || ul > 0) active++;
                }
            }
        }
        var state = down > 0 ? "downloading" : (total > 0 ? "idle" : "empty");
        return new DownloadSnapshot("qbittorrent", version, state, down, up, active, total);
    }

    // ---- Transmission (RPC): 409 session handshake, session-stats + session-get ----------------

    private async System.Threading.Tasks.Task<DownloadSnapshot> ProbeTransmissionAsync(CancellationToken ct)
    {
        var (user, pass) = SplitBasic(_secretProvider());
        var stats = ParseJson(await TransmissionRpcAsync("""{"method":"session-stats"}""", user, pass, ct).ConfigureAwait(false));
        var args = stats.TryGetProperty("arguments", out var a) ? a : default;
        long down = 0, up = 0; int active = 0, total = 0;
        if (args.ValueKind == JsonValueKind.Object)
        {
            down = args.TryGetProperty("downloadSpeed", out var d) && d.TryGetInt64(out var dn) ? dn : 0;
            up = args.TryGetProperty("uploadSpeed", out var u) && u.TryGetInt64(out var un) ? un : 0;
            active = args.TryGetProperty("activeTorrentCount", out var ac) && ac.TryGetInt32(out var an) ? an : 0;
            total = args.TryGetProperty("torrentCount", out var tc) && tc.TryGetInt32(out var tn) ? tn : 0;
        }
        var version = "";
        try
        {
            var ver = ParseJson(await TransmissionRpcAsync("""{"method":"session-get","arguments":{"fields":["version"]}}""", user, pass, ct).ConfigureAwait(false));
            if (ver.TryGetProperty("arguments", out var va) && va.TryGetProperty("version", out var v)) version = v.GetString() ?? "";
        }
        catch { }
        var state = down > 0 ? "downloading" : (active > 0 ? "active" : (total > 0 ? "idle" : "empty"));
        return new DownloadSnapshot("transmission", version, state, down, up, active, total);
    }

    private async System.Threading.Tasks.Task<string> TransmissionRpcAsync(string json, string user, string pass, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "transmission/rpc"))
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (!string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(pass))
                req.Headers.TryAddWithoutValidation("Authorization", "Basic " + BasicHeader(user, pass));
            if (_transmissionSessionId.Length > 0)
                req.Headers.TryAddWithoutValidation("X-Transmission-Session-Id", _transmissionSessionId);
            var (status, body, headers) = await SendAsync(req, ct).ConfigureAwait(false);
            if (status == HttpStatusCode.Conflict && headers.TryGetValues("X-Transmission-Session-Id", out var ids))
            { _transmissionSessionId = ids.FirstOrDefault() ?? ""; continue; } // handshake, then retry once
            if (status == HttpStatusCode.Unauthorized) throw new InvalidOperationException("Transmission rejected credentials (RPC username/password).");
            if (status != HttpStatusCode.OK) throw new InvalidOperationException($"Transmission RPC returned HTTP {(int)status}.");
            return body;
        }
        throw new InvalidOperationException("Transmission RPC session handshake did not complete.");
    }

    // ---- Deluge (JSON-RPC /json): auth.login, core.get_session_status, core.get_torrents_status -

    private async System.Threading.Tasks.Task<DownloadSnapshot> ProbeDelugeAsync(CancellationToken ct)
    {
        var pw = (_secretProvider() ?? "").Trim();
        var login = ParseJson(await DelugeRpcAsync("auth.login", $"[{JsonSerializer.Serialize(pw)}]", ct).ConfigureAwait(false));
        if (!(login.TryGetProperty("result", out var lr) && lr.ValueKind == JsonValueKind.True))
            throw new InvalidOperationException("Deluge login failed (check the web password).");

        long down = 0, up = 0;
        var ss = ParseJson(await DelugeRpcAsync("core.get_session_status", """[["download_rate","upload_rate"]]""", ct).ConfigureAwait(false));
        if (ss.TryGetProperty("result", out var sr) && sr.ValueKind == JsonValueKind.Object)
        {
            down = sr.TryGetProperty("download_rate", out var d) && d.TryGetDouble(out var dv) ? (long)dv : 0;
            up = sr.TryGetProperty("upload_rate", out var u) && u.TryGetDouble(out var uv) ? (long)uv : 0;
        }

        int total = 0, active = 0;
        var ts = ParseJson(await DelugeRpcAsync("core.get_torrents_status", """[{},["state"]]""", ct).ConfigureAwait(false));
        if (ts.TryGetProperty("result", out var tr) && tr.ValueKind == JsonValueKind.Object)
            foreach (var t in tr.EnumerateObject())
            {
                total++;
                if (t.Value.TryGetProperty("state", out var st) && string.Equals(st.GetString(), "Downloading", StringComparison.OrdinalIgnoreCase)) active++;
            }

        var version = "";
        try { var v = ParseJson(await DelugeRpcAsync("daemon.get_version", "[]", ct).ConfigureAwait(false)); if (v.TryGetProperty("result", out var vr)) version = vr.GetString() ?? ""; } catch { }
        var state = down > 0 ? "downloading" : (total > 0 ? "idle" : "empty");
        return new DownloadSnapshot("deluge", version, state, down, up, active, total);
    }

    private int _delugeId = 0;
    private async System.Threading.Tasks.Task<string> DelugeRpcAsync(string method, string paramsJson, CancellationToken ct)
    {
        var body = $"{{\"method\":{JsonSerializer.Serialize(method)},\"params\":{paramsJson},\"id\":{++_delugeId}}}";
        var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "json"))
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        var (status, respBody, _) = await SendAsync(req, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK) throw new InvalidOperationException($"Deluge JSON-RPC returned HTTP {(int)status}.");
        var e = ParseJson(respBody);
        if (e.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            throw new InvalidOperationException($"Deluge JSON-RPC error on {method}: {(err.TryGetProperty("message", out var m) ? m.GetString() : "unknown")}.");
        return respBody;
    }

    // ---- SABnzbd (usenet): pure GET with apikey query -------------------------------------------

    private async System.Threading.Tasks.Task<DownloadSnapshot> ProbeSabnzbdAsync(CancellationToken ct)
    {
        var key = Uri.EscapeDataString((_secretProvider() ?? "").Trim());
        var (qs, qb, _) = await GetAsync($"api?mode=queue&output=json&limit=250&apikey={key}", ct).ConfigureAwait(false);
        if (qs != HttpStatusCode.OK) throw new InvalidOperationException($"SABnzbd returned HTTP {(int)qs}.");
        var root = ParseJson(qb);
        if (root.TryGetProperty("error", out var apiErr) && apiErr.ValueKind == JsonValueKind.String)
            throw new InvalidOperationException($"SABnzbd API error: {apiErr.GetString()} (check the API key).");
        var q = root.TryGetProperty("queue", out var qe) ? qe : default;
        long down = 0; int total = 0, active = 0; var status = "idle";
        if (q.ValueKind == JsonValueKind.Object)
        {
            if (q.TryGetProperty("kbpersec", out var kb)) down = (long)(AsDouble(kb) * 1024);
            if (q.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String) status = s.GetString()?.ToLowerInvariant() ?? "idle";
            if (q.TryGetProperty("noofslots_total", out var n)) total = AsInt(n);
            if (q.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
                foreach (var slot in slots.EnumerateArray())
                    if (slot.TryGetProperty("status", out var ss) && string.Equals(ss.GetString(), "Downloading", StringComparison.OrdinalIgnoreCase)) active++;
        }
        var version = "";
        try { var (_, vb, _) = await GetAsync($"api?mode=version&output=json&apikey={key}", ct).ConfigureAwait(false); var v = ParseJson(vb); if (v.TryGetProperty("version", out var vv)) version = vv.GetString() ?? ""; } catch { }
        return new DownloadSnapshot("sabnzbd", version, status, down, 0, active, total);
    }

    // ---- NZBGet (usenet): GET JSON-RPC with HTTP Basic ------------------------------------------

    private async System.Threading.Tasks.Task<DownloadSnapshot> ProbeNzbgetAsync(CancellationToken ct)
    {
        var (user, pass) = SplitBasic(_secretProvider());
        if (user.Length == 0) user = "nzbget"; // NZBGet's default control username
        var auth = BasicHeader(user, pass);

        var (ss, sb, _) = await GetAsync("jsonrpc/status", ct, auth).ConfigureAwait(false);
        if (ss == HttpStatusCode.Unauthorized) throw new InvalidOperationException("NZBGet rejected credentials (control username/password).");
        if (ss != HttpStatusCode.OK) throw new InvalidOperationException($"NZBGet returned HTTP {(int)ss}.");
        var status = ParseJson(sb);
        var r = status.TryGetProperty("result", out var re) ? re : default;
        long down = 0; var paused = false;
        if (r.ValueKind == JsonValueKind.Object)
        {
            down = r.TryGetProperty("DownloadRate", out var d) && d.TryGetInt64(out var dn) ? dn : 0;
            paused = r.TryGetProperty("DownloadPaused", out var p) && p.ValueKind == JsonValueKind.True;
        }
        int total = 0, active = 0;
        try
        {
            var (gs, gb, _) = await GetAsync("jsonrpc/listgroups", ct, auth).ConfigureAwait(false);
            if (gs == HttpStatusCode.OK && ParseJson(gb).TryGetProperty("result", out var groups) && groups.ValueKind == JsonValueKind.Array)
            {
                total = groups.GetArrayLength();
                foreach (var g in groups.EnumerateArray())
                    if (g.TryGetProperty("Status", out var gst) && (gst.GetString() ?? "").Contains("DOWNLOADING", StringComparison.OrdinalIgnoreCase)) active++;
            }
        }
        catch { }
        var version = "";
        try { var (_, vb, _) = await GetAsync("jsonrpc/version", ct, auth).ConfigureAwait(false); if (ParseJson(vb).TryGetProperty("result", out var vr)) version = vr.GetString() ?? ""; } catch { }
        var state = paused ? "paused" : (down > 0 ? "downloading" : (total > 0 ? "idle" : "empty"));
        return new DownloadSnapshot("nzbget", version, state, down, 0, active, total);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Normalized read-only snapshot every download client reduces to — protocol-agnostic.</summary>
public sealed record DownloadSnapshot(
    string Kind, string Version, string State,
    long DownloadBytesPerSec, long UploadBytesPerSec, int ActiveCount, int TotalCount);

/// <summary>
/// v2.5.5 — the typed widget payloads download clients publish into integration_state. Shapes are
/// deliberately the same the *arr family and the widget runtime already render: <c>health</c>
/// (wgtRenderHealth), <c>queue</c> (wgtRenderQueue), <c>statistics</c> (wgtRenderKv). One
/// builder/parser pair so sync and any consumer can never drift on keys.
/// </summary>
public static class DownloadWidgetPayloads
{
    public static string Health(string version, string checkedAt) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        { ["status"] = "ok", ["version"] = version, ["health_warnings"] = 0, ["checked_at"] = checkedAt });

    public static string Queue(int total, string checkedAt) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["total"] = total, ["checked_at"] = checkedAt });

    public static string Statistics(DownloadSnapshot s, string checkedAt) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["state"] = s.State,
            ["down"] = HumanRate(s.DownloadBytesPerSec),
            ["up"] = HumanRate(s.UploadBytesPerSec),
            ["active"] = s.ActiveCount,
            ["total"] = s.TotalCount,
            ["checked_at"] = checkedAt,
        });

    /// <summary>Human-readable transfer rate, e.g. "3.4 MB/s". Deterministic, invariant culture.</summary>
    public static string HumanRate(long bytesPerSec)
    {
        if (bytesPerSec <= 0) return "0 B/s";
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
        double v = bytesPerSec; var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, i == 0 ? "{0:0} {1}" : "{0:0.0} {1}", v, units[i]);
    }
}

/// <summary>
/// v2.5.5 Console Refit R5 Wave 1 — the download-client family as IIntegrationDefinition
/// implementations. One class covers all five kinds; SyncAsync probes the normalized snapshot
/// through the read-only DownloadClient (allowlist + credential-store discipline unchanged) and
/// returns typed health/queue/statistics payloads for integration_state. Registry-only change:
/// no new tables, endpoints, or UI pages — the generic /homelab/integrations surface and the R2
/// widget runtime pick these up automatically.
/// </summary>
public sealed class DownloadIntegrationDefinition : IIntegrationDefinition
{
    public string Kind { get; }
    public string Category => "download";
    public string AuthMode { get; }
    public IReadOnlyList<string> WidgetKinds => new[] { "health", "queue", "statistics" };

    public DownloadIntegrationDefinition(string kind)
    {
        Kind = kind.ToLowerInvariant();
        AuthMode = DownloadClient.Kinds[Kind].AuthMode;
    }

    /// <summary>Registers all five download-client kinds in the catalog (idempotent — Register upserts).</summary>
    public static void RegisterAll()
    {
        foreach (var kind in DownloadClient.Kinds.Keys)
            IntegrationCatalog.Register(new DownloadIntegrationDefinition(kind));
    }

    public async System.Threading.Tasks.Task<IReadOnlyDictionary<string, string>> SyncAsync(IntegrationContext context, CancellationToken ct)
    {
        using var client = new DownloadClient(Kind, context.BaseUrl, context.TargetGuard, context.CredentialProvider);
        var snap = await client.ProbeAsync(ct).ConfigureAwait(false);
        var now = AnthillTime.NowUtc().ToIso();
        return new Dictionary<string, string>
        {
            ["health"] = DownloadWidgetPayloads.Health(snap.Version, now),
            ["queue"] = DownloadWidgetPayloads.Queue(snap.TotalCount, now),
            ["statistics"] = DownloadWidgetPayloads.Statistics(snap, now),
        };
    }
}
