using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Security;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v2.5.4 Console Refit R4 guards: blocklist as first-class (deny beats allow) in the D1 target
/// guard, kind round-trip + bulk operations in the repository, and the idempotent list_kind
/// column migration for pre-2.5.4 databases. No network in tests.
/// </summary>
public class TargetBlocklistTests : IDisposable
{
    private readonly string _dir;
    private string NewDbPath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");

    public TargetBlocklistTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_tgt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // ---- Deny beats allow ----------------------------------------------------------------------

    [Fact]
    public void Guard_DenyBeatsAllow_ExactHost()
    {
        using var repo = new HomelabRepository(NewDbPath());
        var guard = new HomelabTargetGuard(repo);
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "nas.lan", Kind = "allow", AddedBy = "tester" });
        Assert.True(guard.IsAllowed("nas.lan"));
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "nas.lan", Kind = "deny", AddedBy = "tester" });
        Assert.False(guard.IsAllowed("nas.lan")); // one matching deny refuses, no matter the allows
    }

    [Fact]
    public void Guard_DenyCidr_BlocksHostAllowedByBroaderCidr()
    {
        using var repo = new HomelabRepository(NewDbPath());
        var guard = new HomelabTargetGuard(repo);
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "10.0.0.0/16", Kind = "allow", AddedBy = "tester" });
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "10.0.9.0/24", Kind = "deny", AddedBy = "tester" });
        Assert.True(guard.IsAllowed("10.0.1.5"));   // allowed by the /16
        Assert.False(guard.IsAllowed("10.0.9.5"));  // carved out by the deny /24
    }

    [Fact]
    public void Guard_DenyAlone_DoesNotImplyAllowForOthers()
    {
        using var repo = new HomelabRepository(NewDbPath());
        var guard = new HomelabTargetGuard(repo);
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "bad.lan", Kind = "deny", AddedBy = "tester" });
        Assert.False(guard.IsAllowed("bad.lan"));
        Assert.False(guard.IsAllowed("other.lan")); // default is still closed
    }

    [Fact]
    public void Guard_DisabledDenyEntry_IsIgnored()
    {
        using var repo = new HomelabRepository(NewDbPath());
        var guard = new HomelabTargetGuard(repo);
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "nas.lan", Kind = "allow", AddedBy = "tester" });
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "nas.lan", Kind = "deny", Enabled = false, AddedBy = "tester" });
        Assert.True(guard.IsAllowed("nas.lan"));
    }

    // ---- Repository round-trip + bulk ----------------------------------------------------------

    [Fact]
    public void Repo_KindRoundTrips_AndUnknownKindNormalizesToAllow()
    {
        using var repo = new HomelabRepository(NewDbPath());
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Id = "d1", Target = "bad.lan", Kind = "DENY", AddedBy = "tester" });
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Id = "a1", Target = "ok.lan", Kind = "whatever", AddedBy = "tester" });
        var byId = repo.ListAllowlist().ToDictionary(e => e.Id);
        Assert.Equal("deny", byId["d1"].Kind);
        Assert.Equal("allow", byId["a1"].Kind); // fail closed to the stricter default semantics
    }

    [Fact]
    public void Repo_UpsertById_AuditsUpdateNotCreate()
    {
        using var repo = new HomelabRepository(NewDbPath());
        var entry = new TargetAllowlistRecord { Id = "e1", Target = "nas.lan", AddedBy = "tester" };
        repo.AddAllowlistEntry(entry);
        entry.Note = "edited";
        entry.Kind = "deny";
        repo.AddAllowlistEntry(entry);
        Assert.Single(repo.ListAllowlist()); // still one row
        Assert.Contains(repo.RecentChanges(5), c => c.SubjectKind == "allowlist" && c.ChangeKind == "updated");
    }

    [Fact]
    public void Repo_BulkEnableDisableRemove_WithOneAuditRecordEach()
    {
        using var repo = new HomelabRepository(NewDbPath());
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Id = "b1", Target = "a.lan", AddedBy = "t" });
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Id = "b2", Target = "b.lan", AddedBy = "t" });
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Id = "b3", Target = "c.lan", AddedBy = "t" });

        Assert.Equal(2, repo.SetAllowlistEnabled(new[] { "b1", "b2" }, false, "t"));
        Assert.Equal(2, repo.ListAllowlist().Count(e => !e.Enabled));

        Assert.Equal(2, repo.SetAllowlistEnabled(new[] { "b1", "b2", "no-such-id" }, true, "t"));
        Assert.All(repo.ListAllowlist(), e => Assert.True(e.Enabled));

        Assert.Equal(2, repo.RemoveAllowlistEntries(new[] { "b1", "b3" }, "t"));
        Assert.Single(repo.ListAllowlist());
        Assert.Contains(repo.RecentChanges(10), c => c.SubjectKind == "allowlist" && c.Summary.StartsWith("Bulk removed 2"));
    }

    // ---- Migration -----------------------------------------------------------------------------

    [Fact]
    public void Migration_ListKindColumn_AddedToPre254Databases_DefaultingAllow()
    {
        var dbPath = NewDbPath();
        // Simulate a pre-2.5.4 database: old table shape (no list_kind), one legacy row.
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"CREATE TABLE homelab_target_allowlist (
                id TEXT PRIMARY KEY, target TEXT NOT NULL, note TEXT,
                enabled INTEGER NOT NULL DEFAULT 1, added_by TEXT, created_at TEXT NOT NULL);
                INSERT INTO homelab_target_allowlist (id,target,note,enabled,added_by,created_at)
                VALUES ('legacy','nas.lan','old row',1,'tester','2026-01-01T00:00:00Z')";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var repo = new HomelabRepository(dbPath); // InitDb adds list_kind idempotently
        var legacy = Assert.Single(repo.ListAllowlist());
        Assert.Equal("allow", legacy.Kind); // legacy rows stay allows — behavior unchanged by upgrade
        Assert.True(new HomelabTargetGuard(repo).IsAllowed("nas.lan"));

        // Re-open twice more: the ALTER guard must be idempotent.
        repo.Dispose();
        using (var again = new HomelabRepository(dbPath)) Assert.Single(again.ListAllowlist());
    }
}
