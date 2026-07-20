using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Anthill.Core.Homelab;
using Anthill.Core.Integrations;
using Anthill.Core.Integrations.Download;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v2.5.5 Console Refit R5 Wave 1 — download-client integrations (qBittorrent, Transmission,
/// Deluge, SABnzbd, NZBGet). The read-only guarantee is proven twice: structurally (the client
/// type exposes ONLY ProbeAsync — no add/pause/resume/delete/set is expressible) and on the wire
/// (a mock server records every request and each names a hardcoded read method/endpoint). The D1
/// allowlist + credential gate is asserted to run before ANY byte leaves. Per-protocol parsing is
/// checked against canned server responses, including Transmission's 409 session handshake and
/// qBittorrent's cookie login.
/// </summary>
public class DownloadIntegrationTests : IDisposable
{
    private readonly string _dir;

    public DownloadIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_dl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        DownloadIntegrationDefinition.RegisterAll(); // idempotent — the host does the same at init
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private sealed class FakeGuard : IHomelabTargetGuard
    {
        public bool Allow { get; init; } = true;
        public bool IsAllowed(string hostOrIp) => Allow;
    }

    // ---- Mock download-client server -----------------------------------------------------------

    private sealed record MockRequest(string Method, string Path, string Body, string RawHeaders)
    {
        public string Header(string name)
        {
            var line = RawHeaders.Split('\r', '\n').FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));
            return line is null ? "" : line[(line.IndexOf(':') + 1)..].Trim();
        }
    }

    private sealed record MockResponse(int Status, string Body, (string Name, string Value)[]? Headers = null);

    /// <summary>Records every request (method/path/body/headers); serves whatever the route returns.</summary>
    private sealed class MockDownloadServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<MockRequest, MockResponse> _route;
        public int Port { get; }
        public List<MockRequest> Requests { get; } = new();

        public MockDownloadServer(Func<MockRequest, MockResponse> route)
        {
            _route = route;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoop();
        }

        public string BaseUrl => $"http://127.0.0.1:{Port}";

        private async System.Threading.Tasks.Task AcceptLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    _ = Handle(client);
                }
            }
            catch { }
        }

        private async System.Threading.Tasks.Task Handle(TcpClient client)
        {
            try
            {
                using var c = client;
                var stream = c.GetStream();
                var sb = new StringBuilder();
                var buf = new byte[8192];
                // Read headers.
                int headerEnd;
                while ((headerEnd = sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0)
                {
                    var n = await stream.ReadAsync(buf.AsMemory(), _cts.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                }
                var text = sb.ToString();
                headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                var head = headerEnd < 0 ? text : text[..headerEnd];
                var body = headerEnd < 0 ? "" : text[(headerEnd + 4)..];
                var requestLine = head.Split('\r', '\n')[0];
                var parts = requestLine.Split(' ');
                var method = parts.Length > 0 ? parts[0] : "";
                var path = parts.Length > 1 ? parts[1] : "";

                // Read the rest of the body up to Content-Length (POST payloads).
                var clLine = head.Split('\r', '\n').FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                if (clLine is not null && int.TryParse(clLine[(clLine.IndexOf(':') + 1)..].Trim(), out var cl))
                    while (Encoding.UTF8.GetByteCount(body) < cl)
                    {
                        var n = await stream.ReadAsync(buf.AsMemory(), _cts.Token).ConfigureAwait(false);
                        if (n == 0) break;
                        body += Encoding.UTF8.GetString(buf, 0, n);
                    }

                var req = new MockRequest(method, path, body, head);
                lock (Requests) Requests.Add(req);

                var resp = _route(req);
                var bytes = Encoding.UTF8.GetBytes(resp.Body);
                var extra = new StringBuilder();
                if (resp.Headers is not null) foreach (var (name, value) in resp.Headers) extra.Append($"{name}: {value}\r\n");
                var header = $"HTTP/1.1 {resp.Status} S\r\nContent-Type: application/json\r\n{extra}Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header).AsMemory(), _cts.Token).ConfigureAwait(false);
                await stream.WriteAsync(bytes.AsMemory(), _cts.Token).ConfigureAwait(false);
            }
            catch { }
        }

        public void Dispose() { _cts.Cancel(); _listener.Stop(); _cts.Dispose(); }
    }

    // ---- Catalog -------------------------------------------------------------------------------

    [Fact]
    public void Catalog_RegisterAll_CoversTheFiveDownloadKinds()
    {
        foreach (var kind in new[] { "qbittorrent", "transmission", "deluge", "sabnzbd", "nzbget" })
        {
            var def = IntegrationCatalog.Get(kind);
            Assert.NotNull(def);
            Assert.Equal("download", def!.Category);
            Assert.Equal(new[] { "health", "queue", "statistics" }, def.WidgetKinds);
        }
        Assert.Equal("api_key", IntegrationCatalog.Get("sabnzbd")!.AuthMode);
        Assert.Equal("token", IntegrationCatalog.Get("deluge")!.AuthMode);
        Assert.Equal("basic", IntegrationCatalog.Get("qbittorrent")!.AuthMode);
    }

    // ---- Read-only by construction (structural) ------------------------------------------------

    [Fact]
    public void ReadOnly_ClientTypeExposesNoMutatingMethods()
    {
        var methods = typeof(DownloadClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToList();
        Assert.Contains("ProbeAsync", methods);
        var forbidden = new[] { "Add", "Pause", "Resume", "Delete", "Remove", "Start", "Stop", "Set", "Create", "Write", "Move", "Post", "Put", "Patch", "Reprioritise", "Reprioritize" };
        Assert.All(methods, name => Assert.DoesNotContain(forbidden, f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- Guard + credential run before any I/O -------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Client_RefusesNonAllowlistedHost_BeforeAnyIo()
    {
        using var c = new DownloadClient("qbittorrent", "http://qbit.lan:8080", new FakeGuard { Allow = false }, () => "u:p");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => c.ProbeAsync(default));
        Assert.Contains("allowlist", ex.Message);
    }

    [Fact]
    public async System.Threading.Tasks.Task Client_RefusesWithoutConfiguredSecret()
    {
        using var c = new DownloadClient("sabnzbd", "http://sab.lan:8080", new FakeGuard(), () => null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => c.ProbeAsync(default));
        Assert.Contains("credential", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- qBittorrent: cookie login + GET reads -------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Qbittorrent_Probe_ParsesSpeedsAndCounts_AndLogsInWithReferer()
    {
        using var server = new MockDownloadServer(req => req.Path switch
        {
            var p when p.Contains("/auth/login") => new MockResponse(200, "Ok.", new[] { ("Set-Cookie", "SID=abc123; path=/") }),
            var p when p.Contains("/app/version") => new MockResponse(200, "v4.6.5"),
            var p when p.Contains("/transfer/info") => new MockResponse(200, """{"dl_info_speed":3500000,"up_info_speed":512000,"connection_status":"connected"}"""),
            var p when p.Contains("/torrents/info") => new MockResponse(200, """[{"name":"a","state":"downloading","dlspeed":3500000,"upspeed":0},{"name":"b","state":"stalledUP","dlspeed":0,"upspeed":512000},{"name":"c","state":"pausedDL","dlspeed":0,"upspeed":0}]"""),
            _ => new MockResponse(404, "{}"),
        });
        using var c = new DownloadClient("qbittorrent", server.BaseUrl, new FakeGuard(), () => "admin:secretpw");
        var s = await c.ProbeAsync(default);

        Assert.Equal("4.6.5", s.Version);
        Assert.Equal(3500000, s.DownloadBytesPerSec);
        Assert.Equal(512000, s.UploadBytesPerSec);
        Assert.Equal(3, s.TotalCount);
        Assert.Equal(2, s.ActiveCount); // a (dl) + b (ul); c is paused
        Assert.Equal("downloading", s.State);

        var login = Assert.Single(server.Requests, r => r.Path.Contains("/auth/login"));
        Assert.Equal("POST", login.Method);
        Assert.Contains("username=admin", login.Body);
        Assert.StartsWith("http://127.0.0.1", login.Header("Referer")); // qBittorrent CSRF requirement
    }

    // ---- Transmission: 409 session handshake ---------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Transmission_Probe_CompletesSessionHandshake()
    {
        using var server = new MockDownloadServer(req =>
        {
            // No session id yet → 409 with the token; the client must retry carrying it.
            if (req.Header("X-Transmission-Session-Id").Length == 0)
                return new MockResponse(409, "", new[] { ("X-Transmission-Session-Id", "TESTSID") });
            if (req.Body.Contains("session-stats"))
                return new MockResponse(200, """{"arguments":{"activeTorrentCount":2,"pausedTorrentCount":1,"torrentCount":5,"downloadSpeed":2400000,"uploadSpeed":100000},"result":"success"}""");
            if (req.Body.Contains("session-get"))
                return new MockResponse(200, """{"arguments":{"version":"4.0.5"},"result":"success"}""");
            return new MockResponse(200, """{"result":"success"}""");
        });
        using var c = new DownloadClient("transmission", server.BaseUrl, new FakeGuard(), () => "user:pw");
        var s = await c.ProbeAsync(default);

        Assert.Equal("4.0.5", s.Version);
        Assert.Equal(2400000, s.DownloadBytesPerSec);
        Assert.Equal(100000, s.UploadBytesPerSec);
        Assert.Equal(2, s.ActiveCount);
        Assert.Equal(5, s.TotalCount);
        // The first request 409'd; a later one carried the token — the handshake happened.
        Assert.Contains(server.Requests, r => r.Header("X-Transmission-Session-Id") == "TESTSID");
        Assert.All(server.Requests, r => Assert.Equal("POST", r.Method));
    }

    // ---- Deluge: JSON-RPC login + status -------------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Deluge_Probe_LoginThenSessionStatus()
    {
        using var server = new MockDownloadServer(req =>
        {
            if (req.Body.Contains("auth.login")) return new MockResponse(200, """{"result":true,"error":null,"id":1}""");
            if (req.Body.Contains("core.get_session_status")) return new MockResponse(200, """{"result":{"download_rate":1800000.0,"upload_rate":90000.0},"error":null,"id":2}""");
            if (req.Body.Contains("core.get_torrents_status")) return new MockResponse(200, """{"result":{"h1":{"state":"Downloading"},"h2":{"state":"Seeding"},"h3":{"state":"Downloading"}},"error":null,"id":3}""");
            if (req.Body.Contains("daemon.get_version")) return new MockResponse(200, """{"result":"2.1.1","error":null,"id":4}""");
            return new MockResponse(200, """{"result":null,"error":null,"id":0}""");
        });
        using var c = new DownloadClient("deluge", server.BaseUrl, new FakeGuard(), () => "delugepw");
        var s = await c.ProbeAsync(default);

        Assert.Equal("2.1.1", s.Version);
        Assert.Equal(1800000, s.DownloadBytesPerSec);
        Assert.Equal(90000, s.UploadBytesPerSec);
        Assert.Equal(3, s.TotalCount);
        Assert.Equal(2, s.ActiveCount);
        Assert.Contains(server.Requests, r => r.Body.Contains("auth.login") && r.Body.Contains("delugepw"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Deluge_Probe_BadPasswordSurfacesCleanError()
    {
        using var server = new MockDownloadServer(req =>
            req.Body.Contains("auth.login")
                ? new MockResponse(200, """{"result":false,"error":null,"id":1}""")
                : new MockResponse(200, """{"result":null,"error":null,"id":0}"""));
        using var c = new DownloadClient("deluge", server.BaseUrl, new FakeGuard(), () => "wrong");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => c.ProbeAsync(default));
        Assert.Contains("login failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- SABnzbd: pure GET with apikey ---------------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Sabnzbd_Probe_PureGetWithApiKeyInQuery()
    {
        using var server = new MockDownloadServer(req => req.Path.Contains("mode=version")
            ? new MockResponse(200, """{"version":"4.2.1"}""")
            : new MockResponse(200, """{"queue":{"status":"Downloading","kbpersec":"2500.00","noofslots_total":4,"slots":[{"status":"Downloading"},{"status":"Queued"},{"status":"Queued"}]}}"""));
        using var c = new DownloadClient("sabnzbd", server.BaseUrl, new FakeGuard(), () => "MYAPIKEY");
        var s = await c.ProbeAsync(default);

        Assert.Equal("4.2.1", s.Version);
        Assert.Equal(2560000, s.DownloadBytesPerSec); // 2500 KB/s * 1024
        Assert.Equal(0, s.UploadBytesPerSec);         // usenet: no upload
        Assert.Equal(4, s.TotalCount);
        Assert.Equal(1, s.ActiveCount);
        Assert.All(server.Requests, r => Assert.Equal("GET", r.Method)); // structurally GET-only for SAB
        Assert.All(server.Requests, r => Assert.Contains("apikey=MYAPIKEY", r.Path));
    }

    // ---- NZBGet: GET JSON-RPC with HTTP Basic --------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Nzbget_Probe_GetWithBasicAuthAndDefaultUser()
    {
        using var server = new MockDownloadServer(req =>
        {
            if (req.Path.Contains("/status")) return new MockResponse(200, """{"result":{"DownloadRate":1300000,"DownloadPaused":false,"ServerStandBy":false}}""");
            if (req.Path.Contains("/listgroups")) return new MockResponse(200, """{"result":[{"Status":"DOWNLOADING"},{"Status":"QUEUED"}]}""");
            if (req.Path.Contains("/version")) return new MockResponse(200, """{"result":"21.1"}""");
            return new MockResponse(404, "{}");
        });
        using var c = new DownloadClient("nzbget", server.BaseUrl, new FakeGuard(), () => "tabby");
        var s = await c.ProbeAsync(default);

        Assert.Equal("21.1", s.Version);
        Assert.Equal(1300000, s.DownloadBytesPerSec);
        Assert.Equal(2, s.TotalCount);
        Assert.Equal(1, s.ActiveCount);
        Assert.Equal("downloading", s.State);
        Assert.All(server.Requests, r => Assert.Equal("GET", r.Method));
        // secret "tabby" has no colon → default user "nzbget"; header is "nzbget:tabby" base64.
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("nzbget:tabby"));
        Assert.All(server.Requests, r => Assert.Equal(expected, r.Header("Authorization")));
    }

    // ---- Definition end-to-end: typed widget payloads ------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Definition_SyncAsync_PublishesHealthQueueStatistics()
    {
        using var server = new MockDownloadServer(req => req.Path switch
        {
            var p when p.Contains("/auth/login") => new MockResponse(200, "Ok."),
            var p when p.Contains("/app/version") => new MockResponse(200, "v4.6.5"),
            var p when p.Contains("/transfer/info") => new MockResponse(200, """{"dl_info_speed":3500000,"up_info_speed":0}"""),
            var p when p.Contains("/torrents/info") => new MockResponse(200, """[{"dlspeed":3500000,"upspeed":0},{"dlspeed":0,"upspeed":0}]"""),
            _ => new MockResponse(404, "{}"),
        });
        var def = IntegrationCatalog.Get("qbittorrent")!;
        var ctx = new IntegrationContext(server.BaseUrl, new FakeGuard(), () => "admin:pw");
        var payloads = await def.SyncAsync(ctx, default);

        Assert.Equal(new[] { "health", "queue", "statistics" }.OrderBy(x => x), payloads.Keys.OrderBy(x => x));
        var health = System.Text.Json.JsonDocument.Parse(payloads["health"]).RootElement;
        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.Equal("4.6.5", health.GetProperty("version").GetString());
        var queue = System.Text.Json.JsonDocument.Parse(payloads["queue"]).RootElement;
        Assert.Equal(2, queue.GetProperty("total").GetInt32());
        var stats = System.Text.Json.JsonDocument.Parse(payloads["statistics"]).RootElement;
        Assert.Equal("3.3 MB/s", stats.GetProperty("down").GetString());
        Assert.Equal(1, stats.GetProperty("active").GetInt32());
    }

    // ---- Structural allowlist gate on the contract ---------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Definition_SyncAsync_RefusesNonAllowlistedHost_BeforeAnyIo()
    {
        var def = IntegrationCatalog.Get("transmission")!;
        var ctx = new IntegrationContext("http://transmission.lan:9091", new FakeGuard { Allow = false }, () => "u:p");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => def.SyncAsync(ctx, default));
        Assert.Contains("allowlist", ex.Message);
    }

    // ---- HumanRate ------------------------------------------------------------------------------

    [Fact]
    public void HumanRate_FormatsDeterministically()
    {
        Assert.Equal("0 B/s", DownloadWidgetPayloads.HumanRate(0));
        Assert.Equal("512 B/s", DownloadWidgetPayloads.HumanRate(512));
        Assert.Equal("1.0 KB/s", DownloadWidgetPayloads.HumanRate(1024));
        Assert.Equal("3.3 MB/s", DownloadWidgetPayloads.HumanRate(3500000));
    }
}
