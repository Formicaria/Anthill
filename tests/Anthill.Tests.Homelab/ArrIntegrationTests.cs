using Anthill.Core.Homelab;
using Anthill.Core.Integrations.Arr;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v2.3.3 *arr integration guards: structural GET-only client with D1 allowlist enforcement
/// before any I/O, kind catalog completeness, and repository round-trips for arr_apps and
/// node_metrics. No network in tests.
/// </summary>
public class ArrIntegrationTests : IDisposable
{
    private readonly string _dir;
    private string NewDbPath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");

    public ArrIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_arr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private sealed class FakeGuard : IHomelabTargetGuard
    {
        public bool Allow { get; init; } = true;
        public bool IsAllowed(string hostOrIp) => Allow;
    }

    [Fact]
    public void KindCatalog_CoversTheMainstreamArrFamily()
    {
        foreach (var kind in new[] { "sonarr", "radarr", "lidarr", "readarr", "whisparr", "prowlarr", "bazarr" })
            Assert.True(ArrClient.Kinds.ContainsKey(kind), $"missing kind: {kind}");
        // Prowlarr and bazarr have no download queue; the grabbers do.
        Assert.False(ArrClient.Kinds["prowlarr"].HasQueue);
        Assert.False(ArrClient.Kinds["bazarr"].HasQueue);
        Assert.True(ArrClient.Kinds["sonarr"].HasQueue);
    }

    [Fact]
    public async System.Threading.Tasks.Task Client_RefusesNonAllowlistedHost_BeforeAnyIo()
    {
        using var c = new ArrClient("http://sonarr.lan:8989", new FakeGuard { Allow = false }, () => "k");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => c.GetAsync("api/v3/system/status", default));
        Assert.Contains("allowlist", ex.Message);
    }

    [Fact]
    public async System.Threading.Tasks.Task Client_RefusesWithoutConfiguredApiKey()
    {
        using var c = new ArrClient("http://sonarr.lan:8989", new FakeGuard(), () => null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => c.GetAsync("api/v3/system/status", default));
        Assert.Contains("credential", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArrApps_RoundTrip_NeverStoresSecrets()
    {
        using var repo = new HomelabRepository(NewDbPath());
        var app = new ArrAppRecord { Kind = "radarr", Name = "radarr-main", Url = "http://192.168.1.10:7878", CredentialId = "arr-radarr-abc" };
        repo.UpsertArrApp(app);
        var stored = Assert.Single(repo.ListArrApps());
        Assert.Equal("radarr", stored.Kind);
        Assert.Equal("arr-radarr-abc", stored.CredentialId); // id only — the key lives in the credential store
        repo.RemoveArrApp(app.Id, "tester");
        Assert.Empty(repo.ListArrApps());
        Assert.Contains(repo.RecentChanges(5), c => c.ChangeKind == "removed" && c.SubjectKind == "arr_app");
    }

    [Fact]
    public void NodeMetrics_RoundTrip_AndUpsertReplaces()
    {
        using var repo = new HomelabRepository(NewDbPath());
        repo.UpsertNodeMetric(new NodeMetricRecord { NodeId = "pve-node:h:pve1", NodeName = "pve1", Source = "proxmox", CpuPercent = 12.5, CpuCores = 8, MemUsedBytes = 4, MemTotalBytes = 16 });
        repo.UpsertNodeMetric(new NodeMetricRecord { NodeId = "pve-node:h:pve1", NodeName = "pve1", Source = "proxmox", CpuPercent = 50, CpuCores = 8, MemUsedBytes = 8, MemTotalBytes = 16 });
        var m = Assert.Single(repo.ListNodeMetrics());
        Assert.Equal(50, m.CpuPercent);
        Assert.Equal(8, m.MemUsedBytes);
        Assert.Equal(-1, m.DiskUsedBytes); // unreported metric stays explicitly unknown, never fabricated
    }
}
