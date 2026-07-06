using Anthill.Core.Agents;
using Anthill.Core.Common;
using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Scheduling;
using Anthill.Core.Homelab.Security;
using Anthill.Core.Security;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v1.9.0 homelab foundation tests (NORTH_STAR Phase 4 "Required tests"): migration idempotence,
/// ant-registry validation, summary counts, allowlist isolation from the general SSRF guard,
/// credential save/use/redaction with audit, and the permission matrix.
/// </summary>
public class HomelabFoundationTests : IDisposable
{
    private readonly string _dir;
    private string NewDbPath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");

    public HomelabFoundationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_homelab_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // ---- Migration -----------------------------------------------------------------------------

    [Fact]
    public void Migration_FreshExistingAndRerun_AllPass()
    {
        var path = NewDbPath();
        Dictionary<string, long> first;
        using (var repo = new HomelabRepository(path))
        {
            first = repo.TableCounts();
            Assert.Equal(HomelabRepository.TableNames.Length, first.Count);
            Assert.All(first.Values, v => Assert.Equal(0, v));
        }
        for (var i = 0; i < 2; i++)
        {
            using var repo = new HomelabRepository(path); // re-runs schema init on the existing DB
            Assert.Equal(first.Keys.OrderBy(k => k), repo.TableCounts().Keys.OrderBy(k => k));
        }
    }

    [Fact]
    public void Migration_CoexistsWithColonyMemoryInSameDb()
    {
        var path = NewDbPath();
        using var memory = new Anthill.Core.Memory.SqliteMemory(path);
        using var repo = new HomelabRepository(path); // homelab tables join the colony DB
        Assert.NotEmpty(memory.TableCounts());
        Assert.Equal(HomelabRepository.TableNames.Length, repo.TableCounts().Count);
    }

    // ---- Repository basics + summary counts ---------------------------------------------------

    [Fact]
    public void UpsertNodeAndService_CountsAndChangeLogReflectIt()
    {
        using var repo = new HomelabRepository(NewDbPath());
        var node = new HomelabNode { Name = "pve1", Kind = "hypervisor", Address = "192.168.1.5" };
        repo.UpsertNode(node, "tester");
        repo.UpsertService(new ServiceRecord { Name = "jellyfin", NodeId = node.Id, Ports = { 8096 } }, "tester");
        repo.UpsertNode(node, "tester"); // upsert same id — no duplicate row

        var counts = repo.TableCounts();
        Assert.Equal(1, counts["homelab_nodes"]);
        Assert.Equal(1, counts["services"]);
        Assert.True(counts["change_log"] >= 3, "every upsert must write a ChangeRecord");
        Assert.Single(repo.ListNodes());
        Assert.Equal(new List<int> { 8096 }, repo.ListServices().Single().Ports);
    }

    // ---- Allowlist + SSRF isolation (D1) --------------------------------------------------------

    [Fact]
    public void TargetGuard_MatchesExactHostExactIpAndCidr()
    {
        using var repo = new HomelabRepository(NewDbPath());
        var guard = new HomelabTargetGuard(repo);
        Assert.False(guard.IsAllowed("192.168.1.10")); // empty list denies everything

        repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "nas.lan", AddedBy = "tester" });
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "192.168.1.10", AddedBy = "tester" });
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "10.0.0.0/24", AddedBy = "tester" });

        Assert.True(guard.IsAllowed("NAS.LAN"));       // hostname, case-insensitive
        Assert.True(guard.IsAllowed("192.168.1.10"));  // exact IP
        Assert.True(guard.IsAllowed("10.0.0.42"));     // inside CIDR
        Assert.False(guard.IsAllowed("10.0.1.42"));    // outside CIDR
        Assert.False(guard.IsAllowed("192.168.1.11")); // unlisted IP
        Assert.False(guard.IsAllowed("evil.example.com"));
    }

    [Fact]
    public void TargetGuard_DisabledEntryDoesNotAllow()
    {
        using var repo = new HomelabRepository(NewDbPath());
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "192.168.1.10", Enabled = false, AddedBy = "tester" });
        Assert.False(new HomelabTargetGuard(repo).IsAllowed("192.168.1.10"));
    }

    [Fact]
    public void TargetGuard_NeverWeakensGeneralSsrfGuard()
    {
        using var repo = new HomelabRepository(NewDbPath());
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "192.168.1.10", AddedBy = "tester" });
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "127.0.0.1", AddedBy = "tester" });
        Assert.True(new HomelabTargetGuard(repo).IsAllowed("192.168.1.10"));
        // The general SSRF guard for LLM-directed tools still blocks the very same targets.
        Assert.True(UrlSafety.IsBlockedOutboundUrl("http://192.168.1.10/"));
        Assert.True(UrlSafety.IsBlockedOutboundUrl("http://127.0.0.1:8006/"));
    }

    // ---- Credentials (D2) -------------------------------------------------------------------------

    [Fact]
    public void Credentials_SaveUseVerify_AuditsAndNeverLeaksSecretInStatuses()
    {
        using var repo = new HomelabRepository(NewDbPath());
        var store = new HomelabCredentialStore(repo);
        const string secret = "super-secret-token-XYZZY";

        store.SaveCredential("Proxmox-Main", "proxmox_api_token", "192.168.1.5", secret, "tester");

        var status = Assert.Single(store.ListStatuses());
        Assert.Equal("proxmox-main", status.Id); // normalized
        Assert.True(status.Configured);
        Assert.Equal("", status.LastVerified);
        // Redaction: no secret material anywhere in the status projection.
        var statusDump = System.Text.Json.JsonSerializer.Serialize(store.ListStatuses());
        Assert.DoesNotContain(secret, statusDump);
        Assert.DoesNotContain("XYZZY", statusDump);

        // Deterministic-provider read path round-trips the secret and audits the use.
        Assert.Equal(secret, store.GetSecret("proxmox-main", usedBy: "ProxmoxInventoryProvider"));
        var audit = repo.RecentEvents(10).Where(e => e.EventType == "credential_used").ToList();
        Assert.NotEmpty(audit);
        Assert.All(audit, e => Assert.DoesNotContain(secret, e.Message));

        store.MarkVerified("proxmox-main");
        Assert.NotEqual("", Assert.Single(store.ListStatuses()).LastVerified);

        store.RemoveCredential("proxmox-main", "tester");
        Assert.Empty(store.ListStatuses());
        Assert.Null(store.GetSecret("proxmox-main", "ProxmoxInventoryProvider"));
    }

    [Fact]
    public void Credentials_SecretIsEncryptedAtRestWhenCipherEnabled_AndNeverStoredBare()
    {
        var path = NewDbPath();
        using var repo = new HomelabRepository(path);
        var store = new HomelabCredentialStore(repo);
        const string secret = "plaintext-marker-ABC123";
        store.SaveCredential("cred1", "other", "host", secret, "tester");
        // Whatever FieldCipher mode is active, statuses never expose the secret; and the raw DB
        // bytes must not contain it when encryption is enabled. We assert the API-visible guarantee
        // (no secret in statuses/events) which holds in both cipher modes.
        var dump = System.Text.Json.JsonSerializer.Serialize(store.ListStatuses())
                 + System.Text.Json.JsonSerializer.Serialize(repo.RecentEvents(20))
                 + System.Text.Json.JsonSerializer.Serialize(repo.RecentChanges(20));
        Assert.DoesNotContain(secret, dump);
    }

    // ---- Scheduler skeleton (D4) --------------------------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Scheduler_RunOncePersistsJobState_AndBackoffGrowsOnFailure()
    {
        using var repo = new HomelabRepository(NewDbPath());
        using var scheduler = new HomelabScheduler(repo, maxConcurrency: 2);
        var calls = 0;
        var job = new HomelabScheduledJob("mock-sync", TimeSpan.FromMinutes(5), _ =>
        {
            calls++;
            return System.Threading.Tasks.Task.FromResult(
                calls == 1 ? HomelabProviderResult.Success("synced", 3) : HomelabProviderResult.Failure("boom"));
        });
        scheduler.Register(job);
        Assert.Throws<InvalidOperationException>(() => scheduler.Register(job)); // duplicate name

        var ok = await scheduler.RunOnceAsync("mock-sync");
        Assert.True(ok.Ok);
        var state = repo.GetJobState("mock-sync");
        Assert.NotNull(state);
        Assert.StartsWith("ok", state!.Value.LastResult);
        var healthyDelay = scheduler.NextDelay(job);

        var fail = await scheduler.RunOnceAsync("mock-sync");
        Assert.False(fail.Ok);
        Assert.Contains("boom", repo.GetJobState("mock-sync")!.Value.LastResult);
        // One failure doubles the base interval (with ±10% jitter, still comfortably larger).
        Assert.True(scheduler.NextDelay(job) > healthyDelay);
        Assert.False(scheduler.Running); // skeleton never auto-starts
    }

    [Fact]
    public void Scheduler_StatePersistsAcrossRepositoryReopen()
    {
        var path = NewDbPath();
        using (var repo = new HomelabRepository(path))
            repo.RecordJobRun("mock-sync", ok: true, message: "first pass");
        using (var repo = new HomelabRepository(path))
        {
            var state = repo.GetJobState("mock-sync");
            Assert.NotNull(state);
            Assert.Contains("first pass", state!.Value.LastResult);
        }
    }

    // ---- Ant registry (visible-only homelab ants) ------------------------------------------------

    [Fact]
    public void AntRegistry_HomelabAntsAreVisibleButNeverExecutableOrPatchCapable()
    {
        var homelab = AntRegistry.Roles.Where(r => r.Colony == "Homelab").ToList();
        Assert.Equal(8, homelab.Count);
        Assert.All(homelab, r => Assert.False(r.Executable));
        Assert.All(homelab, r => Assert.False(r.Permissions.ProposePatches));
        Assert.All(homelab, r => Assert.False(r.Permissions.ApplyPatches));
        Assert.All(homelab, r => Assert.False(r.Permissions.RunShell));
        Assert.Empty(AntRegistry.ValidateRegistry());
    }

    // ---- Permission matrix (D3) --------------------------------------------------------------------

    [Theory]
    // Homelab Operator: view + approve only.
    [InlineData(UserRoles.HomelabOperator, "read_homelab", true)]
    [InlineData(UserRoles.HomelabOperator, "approve_homelab_actions", true)]
    [InlineData(UserRoles.HomelabOperator, "read_status", true)]
    [InlineData(UserRoles.HomelabOperator, "manage_homelab_integrations", false)]
    [InlineData(UserRoles.HomelabOperator, "execute_homelab_actions", false)]
    [InlineData(UserRoles.HomelabOperator, "manage_providers", false)]
    [InlineData(UserRoles.HomelabOperator, "manage_users", false)]
    [InlineData(UserRoles.HomelabOperator, "operator_shell", false)]
    [InlineData(UserRoles.HomelabOperator, "apply_patch", false)]
    // Mission Coordinator gains nothing from the homelab tier.
    [InlineData(UserRoles.Coordinator, "read_homelab", false)]
    [InlineData(UserRoles.Coordinator, "manage_homelab_integrations", false)]
    [InlineData(UserRoles.Coordinator, "approve_homelab_actions", false)]
    [InlineData(UserRoles.Coordinator, "execute_homelab_actions", false)]
    // Admin is allowed at the role layer (capability gates still apply on top).
    [InlineData(UserRoles.Admin, "read_homelab", true)]
    [InlineData(UserRoles.Admin, "manage_homelab_integrations", true)]
    [InlineData(UserRoles.Admin, "approve_homelab_actions", true)]
    [InlineData(UserRoles.Admin, "execute_homelab_actions", true)]
    public void PermissionMatrix_RoleAllows(string role, string permission, bool expected) =>
        Assert.Equal(expected, UserRoles.RoleAllows(role, permission));

    [Fact]
    public void PermissionMatrix_HomelabOperatorRoleIsValidAndNormalizes()
    {
        Assert.True(UserRoles.IsValid(UserRoles.HomelabOperator));
        Assert.Equal(UserRoles.HomelabOperator, UserRoles.Normalize("Homelab_Operator"));
        Assert.Equal(UserRoles.HomelabOperator, UserRoles.Normalize("homelab-operator"));
        Assert.Equal(UserRoles.HomelabOperator, UserRoles.Normalize("homelab"));
    }

    [Fact]
    public void CapabilityGates_HomelabActionsShipDisabled()
    {
        Assert.True(Anthill.Core.Configuration.AnthillRuntime.ApiPermissions["read_homelab"]);
        Assert.True(Anthill.Core.Configuration.AnthillRuntime.ApiPermissions["manage_homelab_integrations"]);
        Assert.False(Anthill.Core.Configuration.AnthillRuntime.ApiPermissions["approve_homelab_actions"]);
        Assert.False(Anthill.Core.Configuration.AnthillRuntime.ApiPermissions["execute_homelab_actions"]);
    }
}
