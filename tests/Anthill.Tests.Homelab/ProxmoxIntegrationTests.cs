using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Anthill.Core.Health;
using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Security;
using Anthill.Core.Integrations.Proxmox;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v1.12.0 Proxmox read-only integration (NORTH_STAR Phase 8 validation list: mock Proxmox API,
/// config validation, no-write permission, credential redaction). The no-write guarantee is
/// proven twice: structurally (the client type exposes only GET-shaped methods) and on the wire
/// (a mock PVE server records every request line and all of them are GETs).
/// </summary>
public class ProxmoxIntegrationTests : IDisposable
{
    private const string Token = "anthill@pam!inventory=SECRET-TOKEN-VALUE-XyZ";
    private readonly string _dir;
    private readonly HomelabRepository _repo;
    private readonly HomelabTargetGuard _guard;

    public ProxmoxIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_pve_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _repo = new HomelabRepository(Path.Combine(_dir, "pve.db"));
        _guard = new HomelabTargetGuard(_repo);
    }

    public void Dispose()
    {
        _repo.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void AllowLoopback() =>
        _repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "127.0.0.1", AddedBy = "test" });

    /// <summary>Mock PVE API: serves canned JSON per path, records every raw request line.</summary>
    private sealed class MockPveServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        public int Port { get; }
        public List<string> RequestLines { get; } = new();
        public List<string> AuthHeaders { get; } = new();
        public Func<string, (int Status, string Body)> Route { get; set; }

        public MockPveServer(Func<string, (int, string)>? route = null)
        {
            Route = route ?? DefaultRoute;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoop();
        }

        public static (int, string) DefaultRoute(string path) => path switch
        {
            var p when p.Contains("/version") => (200, """{"data":{"version":"8.2.4","release":"8.2"}}"""),
            var p when p.EndsWith("/nodes") => (200, """{"data":[{"node":"pve1","status":"online","maxcpu":16,"maxmem":67108864000,"uptime":123456}]}"""),
            var p when p.Contains("/qemu") => (200, """{"data":[{"vmid":100,"name":"media-vm","status":"running","cpus":4,"maxmem":8589934592,"uptime":9999},{"vmid":101,"name":"lab-vm","status":"stopped","cpus":2,"maxmem":4294967296,"uptime":0}]}"""),
            var p when p.Contains("/lxc") => (200, """{"data":[{"vmid":200,"name":"anthill-ct","status":"running"}]}"""),
            var p when p.Contains("/storage") => (200, """{"data":[{"storage":"local","type":"dir","total":100000000000,"used":40000000000,"content":"iso,vztmpl"},{"storage":"pbs","type":"pbs","total":2000000000000,"used":500000000000,"content":"backup"}]}"""),
            var p when p.Contains("/tasks") => (200, """{"data":[{"upid":"UPID:pve1:0001:vzdump:100:root@pam:","type":"vzdump","status":"stopped: job errors","starttime":1750000000}]}"""),
            _ => (404, """{"data":null}"""),
        };

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
                var buffer = new byte[16384];
                var read = await stream.ReadAsync(buffer.AsMemory(), _cts.Token).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(buffer, 0, read);
                var requestLine = text.Split('\r', '\n')[0];
                lock (RequestLines) RequestLines.Add(requestLine);
                var auth = text.Split('\r', '\n').FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) ?? "";
                lock (AuthHeaders) AuthHeaders.Add(auth);

                var path = requestLine.Split(' ').Skip(1).FirstOrDefault() ?? "";
                var (status, body) = path.Contains("hang") ? (-1, "") : Route(path);
                if (status < 0) { await System.Threading.Tasks.Task.Delay(30000, _cts.Token).ConfigureAwait(false); return; }
                var bytes = Encoding.UTF8.GetBytes(body);
                // For a 3xx, emit a Location pointing off-host (a dead port) so a client that WRONGLY
                // followed redirects would chase it there and fail with a connection error — letting the
                // redirect-hardening test distinguish "not followed" (clean HTTP 302) from "followed".
                var location = status is >= 300 and < 400 ? "Location: http://127.0.0.1:1/off-allowlist\r\n" : "";
                var head = $"HTTP/1.1 {status} S\r\nContent-Type: application/json\r\n{location}Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head).AsMemory(), _cts.Token).ConfigureAwait(false);
                await stream.WriteAsync(bytes.AsMemory(), _cts.Token).ConfigureAwait(false);
            }
            catch { }
        }

        public void Dispose() { _cts.Cancel(); _listener.Stop(); _cts.Dispose(); }
    }

    private ProxmoxApiClient Client(MockPveServer server, string? token = Token, int timeoutMs = 5000) =>
        new($"http://127.0.0.1:{server.Port}/api2/json", _guard, () => token, TimeSpan.FromMilliseconds(timeoutMs));

    // ---- Protocol selection (v2.2.0 no-TLS fix) ----------------------------------------------------

    [Fact]
    public void Protocol_HttpModeBuildsHttpBaseUrl_HttpsRemainsDefault()
    {
        var https = new ProxmoxApiClient("pve.lan", 8006, _guard, () => Token);
        Assert.StartsWith("https://pve.lan:8006", https.BaseUrl);
        var http = new ProxmoxApiClient("pve.lan", 8006, _guard, () => Token, insecureTls: false, timeout: null, protocol: "http");
        Assert.StartsWith("http://pve.lan:8006", http.BaseUrl);
        var junk = new ProxmoxApiClient("pve.lan", 8006, _guard, () => Token, insecureTls: false, timeout: null, protocol: "gopher");
        Assert.StartsWith("https://", junk.BaseUrl); // unknown protocol falls back to https, never breaks
    }

    [Fact]
    public async System.Threading.Tasks.Task Protocol_HttpMode_StillAttachesAuthHeader()
    {
        // The whole test suite runs the client over plain http against the loopback mock — the
        // Redaction test already proves the PVEAPIToken header rides http requests. This asserts
        // it explicitly for the no-TLS fix.
        AllowLoopback();
        using var server = new MockPveServer();
        var provider = new ProxmoxInventoryProvider(Client(server), _repo);
        Assert.True((await provider.SyncInventoryAsync(CancellationToken.None)).Ok);
        Assert.Contains(server.AuthHeaders, h => h.StartsWith("Authorization: PVEAPIToken=", StringComparison.OrdinalIgnoreCase));
    }

    // ---- No-write permission (structural + wire) --------------------------------------------------

    [Fact]
    public void NoWrite_ClientTypeExposesOnlyGetShapedMethods()
    {
        var methods = typeof(ProxmoxApiClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName) // skip property getters
            .Select(m => m.Name)
            .ToList();
        Assert.NotEmpty(methods);
        Assert.All(methods, name => Assert.StartsWith("Get", name));
        var forbidden = new[] { "Post", "Put", "Delete", "Patch", "Start", "Stop", "Reboot", "Migrate", "Clone", "Resize", "Create", "Write" };
        Assert.All(methods, name => Assert.DoesNotContain(forbidden, f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async System.Threading.Tasks.Task NoWrite_EveryWireRequestIsAGet()
    {
        AllowLoopback();
        using var server = new MockPveServer();
        var provider = new ProxmoxInventoryProvider(Client(server), _repo);
        Assert.True((await provider.SyncInventoryAsync(CancellationToken.None)).Ok);
        Assert.NotEmpty(server.RequestLines);
        Assert.All(server.RequestLines, line => Assert.StartsWith("GET ", line));
    }

    [Fact]
    public async System.Threading.Tasks.Task Redirect_NotFollowedOffAllowlist_SurfacesCleanFailure()
    {
        // Regression (v1.12.0.1): the client must NOT follow 3xx redirects — otherwise a compromised or
        // misconfigured node could bounce the authenticated GET to a Location the target-allowlist never
        // vetted (SSRF). The mock returns a 302 whose Location points off-host to a dead port; with
        // AllowAutoRedirect=false the client returns the 302 as a clean InvalidOperationException("HTTP
        // 302"). If it followed, it would instead throw a connection-error (wrong type) — so the strict
        // exception-type + "302" assertions fail closed if the hardening ever regresses.
        AllowLoopback();
        using var server = new MockPveServer(path => path.Contains("/version") ? (302, "") : (200, "{\"data\":{}}"));
        var client = Client(server);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetVersionAsync(CancellationToken.None));
        Assert.Contains("302", ex.Message);
        Assert.All(server.RequestLines, line => Assert.Contains("/version", line)); // never chased the Location
    }

    // ---- Config validation / guard / credential --------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Guard_UnallowlistedHostBlocked_NoRequestSent()
    {
        using var server = new MockPveServer(); // loopback NOT allowlisted
        var provider = new ProxmoxInventoryProvider(Client(server), _repo);
        var result = await provider.SyncInventoryAsync(CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("allowlist", result.Message);
        Assert.Empty(server.RequestLines);
    }

    [Fact]
    public async System.Threading.Tasks.Task Config_MissingCredentialFailsClean_NoRequestSent()
    {
        AllowLoopback();
        using var server = new MockPveServer();
        var provider = new ProxmoxInventoryProvider(Client(server, token: null), _repo);
        var result = await provider.SyncInventoryAsync(CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("credential", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(server.RequestLines);
        Assert.Equal("failing", provider.GetStatus().State);
    }

    // ---- Sync behavior ---------------------------------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Sync_PopulatesNodesVmsContainersStorageAndFailedTasks()
    {
        AllowLoopback();
        using var server = new MockPveServer();
        var provider = new ProxmoxInventoryProvider(Client(server), _repo);

        var result = await provider.SyncInventoryAsync(CancellationToken.None);
        Assert.True(result.Ok, result.Message);

        var node = Assert.Single(_repo.ListNodes());
        Assert.Equal("pve1", node.Name);
        Assert.Equal("hypervisor", node.Kind);
        Assert.Contains("proxmox", node.RoleTags);

        var vms = _repo.ListVms();
        Assert.Equal(2, vms.Count);
        var media = Assert.Single(vms, v => v.VmId == "100");
        Assert.Equal("media-vm", media.Name);
        Assert.Equal("running", media.Status);
        Assert.Equal(4, media.CpuCores);
        Assert.Equal(8192, media.MemoryMb);

        var ct = Assert.Single(_repo.ListContainers());
        Assert.Equal("anthill-ct", ct.Name);
        Assert.Equal("lxc", ct.Kind);

        var pools = _repo.ListStoragePools();
        Assert.Equal(2, pools.Count);
        Assert.Contains(pools, p => p.Name == "pbs" && p.Kind.Contains("backups"));

        Assert.Single(_repo.RecentEvents(50), e => e.EventType == "proxmox_task_failed");
        Assert.Equal("ok", provider.GetStatus().State);
    }

    [Fact]
    public async System.Threading.Tasks.Task Sync_IsIdempotent_NoDuplicatesOnResync()
    {
        AllowLoopback();
        using var server = new MockPveServer();
        var provider = new ProxmoxInventoryProvider(Client(server), _repo);
        Assert.True((await provider.SyncInventoryAsync(CancellationToken.None)).Ok);
        Assert.True((await provider.SyncInventoryAsync(CancellationToken.None)).Ok);

        Assert.Single(_repo.ListNodes());
        Assert.Equal(2, _repo.ListVms().Count);
        Assert.Single(_repo.ListContainers());
        Assert.Equal(2, _repo.ListStoragePools().Count);
        Assert.Single(_repo.RecentEvents(100), e => e.EventType == "proxmox_task_failed"); // stable UPID id
    }

    [Fact]
    public async System.Threading.Tasks.Task Sync_ServerErrorFailsSoft()
    {
        AllowLoopback();
        using var server = new MockPveServer(_ => (500, """{"data":null}"""));
        var provider = new ProxmoxInventoryProvider(Client(server), _repo);
        var result = await provider.SyncInventoryAsync(CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("500", result.Message);
        Assert.Equal("failing", provider.GetStatus().State);
    }

    [Fact]
    public async System.Threading.Tasks.Task Sync_HungServerTimesOutWithinBound()
    {
        AllowLoopback();
        using var server = new MockPveServer(_ => (-1, "")); // never answers
        var provider = new ProxmoxInventoryProvider(Client(server, timeoutMs: 500), _repo);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await provider.SyncInventoryAsync(CancellationToken.None);
        sw.Stop();
        Assert.False(result.Ok);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"timeout took {sw.Elapsed}");
    }

    // ---- Credential redaction ---------------------------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Redaction_TokenReachesOnlyTheAuthHeader_NeverStorageOrEvents()
    {
        AllowLoopback();
        using var server = new MockPveServer();
        var provider = new ProxmoxInventoryProvider(Client(server), _repo);
        Assert.True((await provider.SyncInventoryAsync(CancellationToken.None)).Ok);

        // The token flows to the PVE Authorization header (that's its job)…
        Assert.Contains(server.AuthHeaders, h => h.Contains("SECRET-TOKEN-VALUE-XyZ"));
        // …and nowhere else: not in events, changes, inventory, status, or the export bundle.
        var dump = System.Text.Json.JsonSerializer.Serialize(_repo.RecentEvents(200))
                 + System.Text.Json.JsonSerializer.Serialize(_repo.RecentChanges(200))
                 + System.Text.Json.JsonSerializer.Serialize(_repo.ListNodes())
                 + System.Text.Json.JsonSerializer.Serialize(_repo.ListVms())
                 + System.Text.Json.JsonSerializer.Serialize(provider.GetStatus())
                 + System.Text.Json.JsonSerializer.Serialize(_repo.ExportInventory());
        Assert.DoesNotContain("SECRET-TOKEN-VALUE-XyZ", dump);
    }

    // ---- Health provider ---------------------------------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task HealthProvider_VersionEndpointHealthy_UnreachableFailed()
    {
        AllowLoopback();
        using var server = new MockPveServer();
        var healthy = await new ProxmoxHealthProvider(Client(server)).CheckAsync("", CancellationToken.None);
        Assert.Equal(HealthStatus.Healthy, healthy.Status);
        Assert.Contains("8.2", healthy.Detail);

        var deadClient = new ProxmoxApiClient($"http://127.0.0.1:1/api2/json", _guard, () => Token, TimeSpan.FromMilliseconds(800));
        var failed = await new ProxmoxHealthProvider(deadClient).CheckAsync("", CancellationToken.None);
        Assert.Equal(HealthStatus.Failed, failed.Status);
    }
}
