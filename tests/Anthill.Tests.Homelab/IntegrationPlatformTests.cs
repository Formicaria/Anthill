using Anthill.Core.Homelab;
using Anthill.Core.Integrations;
using Anthill.Core.Integrations.Arr;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v2.5.1 Console Refit R1 guards: the generic integration platform. Catalog registration,
/// integration_state round-trips with freshness, the *arr compatibility view over the generalized
/// tables, the one-time arr_apps row migration, and the structural allowlist gate on
/// ArrIntegrationDefinition.SyncAsync. No network in tests.
/// </summary>
public class IntegrationPlatformTests : IDisposable
{
    private readonly string _dir;
    private string NewDbPath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");

    public IntegrationPlatformTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_intg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ArrIntegrationDefinition.RegisterAll(); // idempotent — the host does the same at init
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private sealed class FakeGuard : IHomelabTargetGuard
    {
        public bool Allow { get; init; } = true;
        public bool IsAllowed(string hostOrIp) => Allow;
    }

    // ---- Catalog -------------------------------------------------------------------------------

    [Fact]
    public void Catalog_RegisterAll_CoversArrFamilyWithContractMetadata()
    {
        foreach (var kind in new[] { "sonarr", "radarr", "lidarr", "readarr", "whisparr", "prowlarr", "bazarr" })
        {
            var def = IntegrationCatalog.Get(kind);
            Assert.NotNull(def);
            Assert.Equal("media", def!.Category);
            Assert.Equal("api_key", def.AuthMode);
            Assert.Contains("health", def.WidgetKinds);
        }
        Assert.Contains("queue", IntegrationCatalog.Get("sonarr")!.WidgetKinds);
        Assert.DoesNotContain("queue", IntegrationCatalog.Get("prowlarr")!.WidgetKinds);
        Assert.DoesNotContain("queue", IntegrationCatalog.Get("bazarr")!.WidgetKinds);
    }

    // ---- integration_state ---------------------------------------------------------------------

    [Fact]
    public void IntegrationState_RoundTrip_UpsertReplacesAndStampsFreshness()
    {
        using var repo = new HomelabRepository(NewDbPath());
        repo.UpsertIntegrationState("intg-1", "health", """{"status":"ok"}""");
        repo.UpsertIntegrationState("intg-1", "queue", """{"total":3}""");
        repo.UpsertIntegrationState("intg-1", "queue", """{"total":7}"""); // replaces, not duplicates

        var queue = repo.GetIntegrationState("intg-1", "queue");
        Assert.NotNull(queue);
        Assert.Contains("7", queue!.PayloadJson);
        Assert.False(string.IsNullOrWhiteSpace(queue.UpdatedAt)); // freshness marker

        Assert.Equal(2, repo.ListIntegrationStates("intg-1").Count);
        Assert.Null(repo.GetIntegrationState("intg-1", "logs"));
        Assert.Empty(repo.ListIntegrationStates("no-such-integration"));
    }

    // ---- instances + removal hygiene -----------------------------------------------------------

    [Fact]
    public void IntegrationInstances_RoundTrip_AndRemoveDeletesWidgetState()
    {
        using var repo = new HomelabRepository(NewDbPath());
        var inst = new IntegrationInstanceRecord { Kind = "sonarr", Name = "tv", Url = "http://192.168.1.5:8989", CredentialId = "intg-sonarr-abc" };
        repo.UpsertIntegrationInstance(inst);
        repo.UpsertIntegrationState(inst.Id, "health", """{"status":"ok"}""");

        var stored = Assert.Single(repo.ListIntegrationInstances());
        Assert.Equal("intg-sonarr-abc", stored.CredentialId); // id only — the secret lives in the credential store

        repo.RemoveIntegrationInstance(inst.Id, "tester");
        Assert.Empty(repo.ListIntegrationInstances());
        Assert.Empty(repo.ListIntegrationStates(inst.Id)); // no orphaned widget payloads
        Assert.Contains(repo.RecentChanges(5), c => c.ChangeKind == "removed" && c.SubjectKind == "integration");
    }

    // ---- *arr compatibility view ---------------------------------------------------------------

    [Fact]
    public void ArrCompatView_UpsertArrApp_IsVisibleAsInstanceAndWidgetState()
    {
        using var repo = new HomelabRepository(NewDbPath());
        repo.UpsertArrApp(new ArrAppRecord
        {
            Id = "a1", Kind = "sonarr", Name = "tv", Url = "http://192.168.1.5:8989",
            CredentialId = "arr-sonarr-a1", Status = "ok", Version = "4.0.9", HealthWarnings = 2, QueueCount = 5,
        });

        // Legacy read surface reconstructs arr fields from the generalized tables.
        var app = Assert.Single(repo.ListArrApps());
        Assert.Equal("4.0.9", app.Version);
        Assert.Equal(2, app.HealthWarnings);
        Assert.Equal(5, app.QueueCount);

        // The same row is a first-class integration instance with typed widget payloads.
        var inst = Assert.Single(repo.ListIntegrationInstances());
        Assert.Equal("sonarr", inst.Kind);
        var health = ArrWidgetPayloads.ParseHealth(repo.GetIntegrationState("a1", "health")!.PayloadJson);
        Assert.Equal("4.0.9", health.Version);
        Assert.Equal(2, health.Warnings);
        Assert.Equal(5, ArrWidgetPayloads.ParseQueueTotal(repo.GetIntegrationState("a1", "queue")!.PayloadJson));
    }

    // ---- Legacy row migration ------------------------------------------------------------------

    [Fact]
    public void LegacyArrAppsRows_MigrateToIntegrationTables_OnOpen()
    {
        var dbPath = NewDbPath();
        using (var repo = new HomelabRepository(dbPath)) { } // create schema (arr_apps still exists, empty)

        // Simulate a pre-2.5.1 deployment: a raw row written to the legacy table.
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO arr_apps (id,kind,name,url,credential_id,enabled,status,version,health_warnings,queue_count,last_message,last_checked)
                VALUES ('legacy1','radarr','movies','http://192.168.1.10:7878','arr-radarr-leg',1,'ok','5.14.0',1,4,'','2026-07-01T00:00:00Z')";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using (var repo = new HomelabRepository(dbPath)) // reopen → InitDb migrates
        {
            var app = Assert.Single(repo.ListArrApps());
            Assert.Equal("radarr", app.Kind);
            Assert.Equal("5.14.0", app.Version);
            Assert.Equal(1, app.HealthWarnings);
            Assert.Equal(4, app.QueueCount);
            Assert.Equal("arr-radarr-leg", app.CredentialId);
            Assert.Single(repo.ListIntegrationInstances());

            // The legacy table is emptied — the migration cannot double-run or resurrect rows.
            using var conn = new SqliteConnection($"Data Source={repo.DbPath}");
            conn.Open();
            using var count = conn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM arr_apps";
            Assert.Equal(0L, (long)count.ExecuteScalar()!);
        }

        using (var repo = new HomelabRepository(dbPath)) // third open is a no-op
            Assert.Single(repo.ListArrApps());
    }

    // ---- Structural discipline on the contract -------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Definition_SyncAsync_RefusesNonAllowlistedHost_BeforeAnyIo()
    {
        var def = IntegrationCatalog.Get("sonarr")!;
        var ctx = new IntegrationContext("http://sonarr.lan:8989", new FakeGuard { Allow = false }, () => "k");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => def.SyncAsync(ctx, default));
        Assert.Contains("allowlist", ex.Message);
    }

    [Fact]
    public async System.Threading.Tasks.Task SyncProvider_SkipsDisabledAndUnregisteredKinds()
    {
        using var repo = new HomelabRepository(NewDbPath());
        repo.UpsertIntegrationInstance(new IntegrationInstanceRecord { Kind = "sonarr", Name = "off", Url = "http://192.168.1.5:8989", CredentialId = "c", Enabled = false });
        repo.UpsertIntegrationInstance(new IntegrationInstanceRecord { Kind = "not-registered", Name = "future", Url = "http://192.168.1.6:1", CredentialId = "c" });

        var provider = new IntegrationSyncProvider(repo, new FakeGuard(), _ => "k");
        var result = await provider.RunAsync(default);
        Assert.True(result.Ok);
        Assert.Contains("0 ok, 0 failed of 0", result.Message); // nothing eligible, nothing touched
    }
}
