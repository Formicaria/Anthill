using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Security;
using Anthill.Core.Integrations.Docker;
using Anthill.Core.Integrations.Hyperv;
using Anthill.Core.Integrations.VSphere;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v2.1.0 read-only virtualization integrations (Docker, vSphere/ESXi, Hyper-V). The no-write guarantee
/// is proven the same way as Proxmox: structurally (the client types expose only read-shaped methods —
/// GET / Enumerate) and on the wire (a mock server records every request and none of them mutate).
/// </summary>
public class VirtualizationIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly HomelabRepository _repo;
    private readonly HomelabTargetGuard _guard;

    public VirtualizationIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_virt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _repo = new HomelabRepository(Path.Combine(_dir, "virt.db"));
        _guard = new HomelabTargetGuard(_repo);
    }

    public void Dispose()
    {
        _repo.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void AllowLoopback() => _repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "127.0.0.1", AddedBy = "test" });

    /// <summary>Minimal mock HTTP server: routes by (method, path), records every request line.</summary>
    private sealed class MockServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        public int Port { get; }
        public List<string> RequestLines { get; } = new();
        public Func<string, string, string, (int Status, string Body)> Route { get; }

        public MockServer(Func<string, string, string, (int, string)> route)
        {
            Route = route;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoop();
        }

        private async System.Threading.Tasks.Task AcceptLoop()
        {
            try { while (!_cts.IsCancellationRequested) { var c = await _listener.AcceptTcpClientAsync(_cts.Token); _ = Handle(c); } }
            catch { }
        }

        private async System.Threading.Tasks.Task Handle(TcpClient client)
        {
            try
            {
                using var c = client;
                var stream = c.GetStream();
                var buf = new byte[32768];
                var read = await stream.ReadAsync(buf.AsMemory(), _cts.Token);
                var text = Encoding.UTF8.GetString(buf, 0, read);
                var line0 = text.Split('\r', '\n')[0];
                lock (RequestLines) RequestLines.Add(line0);
                var parts = line0.Split(' ');
                var method = parts.Length > 0 ? parts[0] : "";
                var path = parts.Length > 1 ? parts[1] : "";
                var body = text.Contains("\r\n\r\n") ? text[(text.IndexOf("\r\n\r\n") + 4)..] : "";
                var (status, respBody) = Route(method, path, body);
                var bytes = Encoding.UTF8.GetBytes(respBody);
                var loc = status is >= 300 and < 400 ? "Location: http://127.0.0.1:1/off\r\n" : "";
                var head = $"HTTP/1.1 {status} S\r\nContent-Type: application/json\r\n{loc}Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head).AsMemory(), _cts.Token);
                await stream.WriteAsync(bytes.AsMemory(), _cts.Token);
            }
            catch { }
        }

        public void Dispose() { _cts.Cancel(); _listener.Stop(); _cts.Dispose(); }
    }

    // ---- Docker ---------------------------------------------------------------------------------------

    private static (int, string) DockerRoute(string method, string path, string body) => path switch
    {
        var p when p.Contains("/info") => (200, """{"Name":"dockerhost","ServerVersion":"24.0.5","OperatingSystem":"Ubuntu 22.04","Containers":2,"ContainersRunning":1,"Images":10,"NCPU":8}"""),
        var p when p.Contains("/containers/json") => (200, """[{"Id":"abc123def456","Names":["/web"],"Image":"nginx","State":"running"},{"Id":"deadbeef0001","Names":["/db"],"Image":"postgres","State":"exited"}]"""),
        var p when p.Contains("/volumes") => (200, """{"Volumes":[{"Name":"pgdata","Driver":"local"}]}"""),
        var p when p.Contains("/version") => (200, """{"Version":"24.0.5"}"""),
        _ => (404, "{}"),
    };

    [Fact]
    public void Docker_ClientTypeExposesOnlyGetShapedMethods()
    {
        var methods = typeof(DockerApiClient).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName).Select(m => m.Name).ToList();
        Assert.NotEmpty(methods);
        Assert.All(methods, name => Assert.StartsWith("Get", name));
        var forbidden = new[] { "Post", "Put", "Delete", "Start", "Stop", "Kill", "Remove", "Exec", "Create" };
        Assert.All(methods, name => Assert.All(forbidden, f => Assert.DoesNotContain(f, name)));
    }

    [Fact]
    public async System.Threading.Tasks.Task Docker_SyncEveryRequestIsGet_AndUpsertsInventory()
    {
        AllowLoopback();
        using var server = new MockServer(DockerRoute);
        var client = new DockerApiClient($"http://127.0.0.1:{server.Port}", _guard, () => null, TimeSpan.FromSeconds(5));
        var provider = new DockerInventoryProvider(client, _repo);
        Assert.True((await provider.SyncInventoryAsync(CancellationToken.None)).Ok);
        Assert.NotEmpty(server.RequestLines);
        Assert.All(server.RequestLines, l => Assert.StartsWith("GET ", l));
        Assert.Contains(_repo.ListContainers(), c => c.Name == "web" && c.Kind == "docker");
        Assert.Contains(_repo.ListNodes(), n => n.Kind == "container_host");
    }

    [Fact]
    public async System.Threading.Tasks.Task Docker_UnallowlistedHostBlocked_NoRequestSent()
    {
        using var server = new MockServer(DockerRoute); // loopback NOT allowlisted
        var provider = new DockerInventoryProvider(new DockerApiClient($"http://127.0.0.1:{server.Port}", _guard, () => null), _repo);
        var r = await provider.SyncInventoryAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("allowlist", r.Message);
        Assert.Empty(server.RequestLines);
    }

    // ---- vSphere / ESXi -------------------------------------------------------------------------------

    private static (int, string) VSphereRoute(string method, string path, string body)
    {
        if (path.EndsWith("/api/session") && method == "POST") return (200, "\"sess-token-xyz\"");
        if (path.Contains("/api/vcenter/host")) return (200, """[{"host":"host-1","name":"esxi01","connection_state":"CONNECTED","power_state":"POWERED_ON"}]""");
        if (path.Contains("/api/vcenter/vm")) return (200, """[{"vm":"vm-101","name":"app01","power_state":"POWERED_ON","cpu_count":4,"memory_size_MiB":8192}]""");
        if (path.Contains("/api/vcenter/datastore")) return (200, """[{"datastore":"ds-1","name":"datastore1","type":"VMFS","capacity":1000000,"free_space":400000}]""");
        return (404, "{}");
    }

    [Fact]
    public async System.Threading.Tasks.Task VSphere_AuthenticatesThenReadsOnly_UpsertsInventory()
    {
        AllowLoopback();
        using var server = new MockServer(VSphereRoute);
        var client = new VSphereApiClient($"http://127.0.0.1:{server.Port}", _guard, () => "admin@vsphere.local:pw", TimeSpan.FromSeconds(5));
        var provider = new VSphereInventoryProvider(client, _repo);
        Assert.True((await provider.SyncInventoryAsync(CancellationToken.None)).Ok);
        // Exactly one POST — the session (auth only) — and everything else a GET.
        Assert.Single(server.RequestLines, l => l.StartsWith("POST ") && l.Contains("/api/session"));
        Assert.All(server.RequestLines.Where(l => !l.Contains("/api/session")), l => Assert.StartsWith("GET ", l));
        Assert.Contains(_repo.ListVms(), v => v.Name == "app01" && v.Status == "running");
        Assert.Contains(_repo.ListNodes(), n => n.Kind == "hypervisor" && n.Os.Contains("ESXi"));
    }

    [Fact]
    public async System.Threading.Tasks.Task VSphere_MissingCredential_FailsClean_NoDataRequest()
    {
        AllowLoopback();
        using var server = new MockServer(VSphereRoute);
        var provider = new VSphereInventoryProvider(new VSphereApiClient($"http://127.0.0.1:{server.Port}", _guard, () => null), _repo);
        var r = await provider.SyncInventoryAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Empty(server.RequestLines); // never even authenticated
    }

    // ---- Hyper-V (WinRM WMI Enumerate) ----------------------------------------------------------------

    private const string HypervSoap = """
    <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:w="http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd" xmlns:p="http://schemas.microsoft.com/wbem/wsman/1/wmi/root/virtualization/v2/Msvm_ComputerSystem">
      <s:Body><w:EnumerateResponse><w:Items>
        <p:Msvm_ComputerSystem><p:Caption>Virtual Machine</p:Caption><p:ElementName>WIN-DC01</p:ElementName><p:EnabledState>2</p:EnabledState></p:Msvm_ComputerSystem>
        <p:Msvm_ComputerSystem><p:Caption>Virtual Machine</p:Caption><p:ElementName>WIN-APP</p:ElementName><p:EnabledState>3</p:EnabledState></p:Msvm_ComputerSystem>
        <p:Msvm_ComputerSystem><p:Caption>Hosting Computer System</p:Caption><p:ElementName>HYPERV-HOST</p:ElementName><p:EnabledState>2</p:EnabledState></p:Msvm_ComputerSystem>
      </w:Items><w:EndOfSequence/></w:EnumerateResponse></s:Body></s:Envelope>
    """;

    [Fact]
    public void Hyperv_ParsesVms_SkipsHostManagementOs()
    {
        var vms = HypervWinRmClient.ParseVms(HypervSoap);
        Assert.Equal(2, vms.Count); // the "Hosting Computer System" (host OS) is excluded
        Assert.Contains(vms, v => v.Name == "WIN-DC01" && v.State == "running");
        Assert.Contains(vms, v => v.Name == "WIN-APP" && v.State == "stopped");
        Assert.DoesNotContain(vms, v => v.Name == "HYPERV-HOST");
    }

    [Fact]
    public async System.Threading.Tasks.Task Hyperv_EnumerateOverWinrm_UpsertsVms()
    {
        AllowLoopback();
        // The client reads the POST response body as SOAP XML regardless of content-type header.
        using var soapServer = new MockServer((method, path, body) => (200, HypervSoap));
        var client = new HypervWinRmClient($"http://127.0.0.1:{soapServer.Port}/wsman", _guard, () => "admin:pw", TimeSpan.FromSeconds(5));
        var provider = new HypervInventoryProvider(client, _repo);
        Assert.True((await provider.SyncInventoryAsync(CancellationToken.None)).Ok);
        Assert.Single(soapServer.RequestLines, l => l.StartsWith("POST ") && l.Contains("/wsman"));
        Assert.Contains(_repo.ListVms(), v => v.Name == "WIN-DC01");
        Assert.Contains(_repo.ListNodes(), n => n.Kind == "hypervisor" && n.Os.Contains("Hyper-V"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Hyperv_UnallowlistedHostBlocked_NoRequestSent()
    {
        using var server = new MockServer((method, path, body) => (200, HypervSoap));
        var client = new HypervWinRmClient($"http://127.0.0.1:{server.Port}/wsman", _guard, () => "admin:pw");
        var provider = new HypervInventoryProvider(client, _repo);
        var r = await provider.SyncInventoryAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Empty(server.RequestLines);
    }

    // ---- Redirect hardening (shared HttpClients: AllowAutoRedirect=false) -----------------------------

    [Fact]
    public async System.Threading.Tasks.Task Docker_DoesNotFollowRedirects_OffAllowlist()
    {
        AllowLoopback();
        using var server = new MockServer((method, path, body) => path.Contains("/info") ? (302, "") : (200, "{}"));
        var client = new DockerApiClient($"http://127.0.0.1:{server.Port}", _guard, () => null, TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetInfoAsync(CancellationToken.None));
        Assert.Contains("302", ex.Message);
        Assert.All(server.RequestLines, l => Assert.Contains("/info", l)); // never chased the Location
    }
}
