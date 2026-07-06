using Anthill.Core.Homelab;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v1.10.0 inventory + service registry tests (NORTH_STAR Phase 6 validation list: repository,
/// import/export round-trip, change records). Dependency mapping answers "what runs where?" /
/// "what depends on this?"; import is upsert-based so re-importing an export is idempotent.
/// </summary>
public class InventoryRegistryTests : IDisposable
{
    private readonly string _dir;
    private string NewDbPath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");

    public InventoryRegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_inv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static (HomelabNode Node, ServiceRecord Service, DependencyRecord Dep) Seed(HomelabRepository repo)
    {
        var node = new HomelabNode { Name = "pve1", Kind = "hypervisor", Address = "192.168.1.5", RoleTags = { "hypervisor" } };
        repo.UpsertNode(node, "tester");
        var service = new ServiceRecord { Name = "jellyfin", NodeId = node.Id, Ports = { 8096 }, Owner = "media", Criticality = "high" };
        repo.UpsertService(service, "tester");
        var dep = new DependencyRecord { FromKind = "service", FromId = service.Id, ToKind = "host", ToId = node.Id, DependencyKind = "runs_on" };
        repo.UpsertDependency(dep, "tester");
        return (node, service, dep);
    }

    [Fact]
    public void Dependencies_UpsertListRemove_WithChangeRecords()
    {
        using var repo = new HomelabRepository(NewDbPath());
        var (_, service, dep) = Seed(repo);

        var listed = Assert.Single(repo.ListDependencies());
        Assert.Equal(service.Id, listed.FromId);
        Assert.Equal("runs_on", listed.DependencyKind);

        dep.DependencyKind = "needs"; // same id — update, not duplicate
        repo.UpsertDependency(dep, "tester");
        Assert.Equal("needs", Assert.Single(repo.ListDependencies()).DependencyKind);

        repo.RemoveDependency(dep.Id, "tester");
        Assert.Empty(repo.ListDependencies());
        Assert.Contains(repo.RecentChanges(20), c => c.SubjectKind == "dependency" && c.ChangeKind == "removed");
    }

    [Fact]
    public void ExportImport_RoundTripsIntoEmptyDb()
    {
        using var source = new HomelabRepository(NewDbPath());
        var (node, service, dep) = Seed(source);
        var bundle = source.ExportInventory();
        Assert.NotEqual("", bundle.ExportedAt);
        Assert.Single(bundle.Nodes); Assert.Single(bundle.Services); Assert.Single(bundle.Dependencies);

        using var target = new HomelabRepository(NewDbPath());
        var (n, s, d) = target.ImportInventory(bundle, "importer");
        Assert.Equal((1, 1, 1), (n, s, d));

        var importedNode = Assert.Single(target.ListNodes());
        Assert.Equal(node.Id, importedNode.Id);
        Assert.Equal("pve1", importedNode.Name);
        Assert.Equal(new List<string> { "hypervisor" }, importedNode.RoleTags);
        var importedSvc = Assert.Single(target.ListServices());
        Assert.Equal(service.Id, importedSvc.Id);
        Assert.Equal(new List<int> { 8096 }, importedSvc.Ports);
        Assert.Equal(dep.Id, Assert.Single(target.ListDependencies()).Id);
        Assert.Contains(target.RecentChanges(30), c => c.ChangeKind == "imported" && c.SubjectKind == "inventory");
    }

    [Fact]
    public void Import_IsIdempotent_ReimportingAnExportCreatesNoDuplicates()
    {
        using var repo = new HomelabRepository(NewDbPath());
        Seed(repo);
        var bundle = repo.ExportInventory();

        repo.ImportInventory(bundle, "importer");
        repo.ImportInventory(bundle, "importer"); // twice on purpose

        Assert.Single(repo.ListNodes());
        Assert.Single(repo.ListServices());
        Assert.Single(repo.ListDependencies());
    }

    [Fact]
    public void Import_SkipsInvalidRecords()
    {
        using var repo = new HomelabRepository(NewDbPath());
        var bundle = new HomelabInventoryExport
        {
            Nodes = { new HomelabNode { Name = "" } },                                  // nameless — skipped
            Services = { new ServiceRecord { Name = "  " } },                            // blank — skipped
            Dependencies = { new DependencyRecord { FromId = "", ToId = "x" } },         // missing endpoint — skipped
        };
        var (n, s, d) = repo.ImportInventory(bundle, "importer");
        Assert.Equal((0, 0, 0), (n, s, d));
        Assert.Empty(repo.ListNodes());
        Assert.Empty(repo.ListServices());
        Assert.Empty(repo.ListDependencies());
    }

    [Fact]
    public void Export_NeverContainsCredentialOrAllowlistMaterial()
    {
        using var repo = new HomelabRepository(NewDbPath());
        Seed(repo);
        var store = new Anthill.Core.Homelab.Security.HomelabCredentialStore(repo);
        const string secret = "proxmox-secret-QWERTY99";
        store.SaveCredential("prox", "proxmox_api_token", "192.168.1.5", secret, "tester");
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "10.0.0.0/24", AddedBy = "tester" });

        var json = System.Text.Json.JsonSerializer.Serialize(repo.ExportInventory());
        Assert.DoesNotContain(secret, json);
        Assert.DoesNotContain("QWERTY99", json);
        Assert.DoesNotContain("10.0.0.0/24", json);
    }
}
