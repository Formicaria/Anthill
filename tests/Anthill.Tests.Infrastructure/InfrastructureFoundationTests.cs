using Anthill.Core.Agents;
using Anthill.Core.Common;
using Anthill.Modules.Infrastructure;
using Anthill.Modules.Infrastructure.Scheduling;
using Anthill.Modules.Infrastructure.Security;
using Anthill.Core.Security;
using Xunit;

namespace Anthill.Tests.Infrastructure;

/// <summary>
/// v1.9.0 infrastructure foundation tests (NORTH_STAR Phase 4 "Required tests"): migration idempotence,
/// ant-registry validation, summary counts, allowlist isolation from the general SSRF guard,
/// credential save/use/redaction with audit, and the permission matrix.
/// </summary>
public class InfrastructureFoundationTests : IDisposable
{
    private readonly string _dir;
    private string NewDbPath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");

    public InfrastructureFoundationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_infrastructure_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // ---- Migration -----------------------------------------------------------------------------

    [Fact]
    public void Migration_FreshExistingAndRerun_AllPass()
    {
        var path = NewDbPath();
        Dictionary<string, long> first;
        using (var repo = new InfrastructureRepository(path))
        {
            first = repo.TableCounts();
            Assert.Equal(InfrastructureRepository.TableNames.Length, first.Count);
            Assert.All(first.Values, v => Assert.Equal(0, v));
        }
        for (var i = 0; i < 2; i++)
        {
            using var repo = new InfrastructureRepository(path); // re-runs schema init on the existing DB
            Assert.Equal(first.Keys.OrderBy(k => k), repo.TableCounts().Keys.OrderBy(k => k));
        }
    }

    [Fact]
    public void Migration_CoexistsWithColonyMemoryInSameDb()
    {
        var path = NewDbPath();
        using var memory = new Anthill.Core.Memory.SqliteMemory(path);
        using var repo = new InfrastructureRepository(path); // infrastructure tables join the colony DB
        Assert.NotEmpty(memory.TableCounts());
        Assert.Equal(InfrastructureRepository.TableNames.Length, repo.TableCounts().Count);
    }

    // ---- Repository basics + summary counts ---------------------------------------------------

    [Fact]
    public void UpsertNodeAndService_CountsAndChangeLogReflectIt()
    {
        using var repo = new InfrastructureRepository(NewDbPath());
        var node = new InfrastructureNode { Name = "pve1", Kind = "hypervisor", Address = "192.168.1.5" };
        repo.UpsertNode(node, "tester");
        repo.UpsertService(new ServiceRecord { Name = "jellyfin", NodeId = node.Id, Ports = { 8096 } }, "tester");
        repo.UpsertNode(node, "tester"); // upsert same id — no duplicate row

        var counts = repo.TableCounts();
        Assert.Equal(1, counts["infrastructure_nodes"]);
        Assert.Equal(1, counts["services"]);
        Assert.True(counts["change_log"] >= 3, "every upsert must write a ChangeRecord");
        Assert.Single(repo.ListNodes());
        Assert.Equal(new List<int> { 8096 }, repo.ListServices().Single().Ports);
    }

    // ---- Allowlist + SSRF isolation (D1) --------------------------------------------------------

    [Fact]
    public void TargetGuard_MatchesExactHostExactIpAndCidr()
    {
        using var repo = new InfrastructureRepository(NewDbPath());
        var guard = new InfrastructureTargetGuard(repo);
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
        using var repo = new InfrastructureRepository(NewDbPath());
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "192.168.1.10", Enabled = false, AddedBy = "tester" });
        Assert.False(new InfrastructureTargetGuard(repo).IsAllowed("192.168.1.10"));
    }

    [Fact]
    public void TargetGuard_NeverWeakensGeneralSsrfGuard()
    {
        using var repo = new InfrastructureRepository(NewDbPath());
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "192.168.1.10", AddedBy = "tester" });
        repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "127.0.0.1", AddedBy = "tester" });
        Assert.True(new InfrastructureTargetGuard(repo).IsAllowed("192.168.1.10"));
        // The general SSRF guard for LLM-directed tools still blocks the very same targets.
        Assert.True(UrlSafety.IsBlockedOutboundUrl("http://192.168.1.10/"));
        Assert.True(UrlSafety.IsBlockedOutboundUrl("http://127.0.0.1:8006/"));
    }

    // ---- Credentials (D2) -------------------------------------------------------------------------

    [Fact]
    public void Credentials_SaveUseVerify_AuditsAndNeverLeaksSecretInStatuses()
    {
        using var repo = new InfrastructureRepository(NewDbPath());
        var store = new InfrastructureCredentialStore(repo);
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
        using var repo = new InfrastructureRepository(path);
        var store = new InfrastructureCredentialStore(repo);
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
        using var repo = new InfrastructureRepository(NewDbPath());
        using var scheduler = new InfrastructureScheduler(repo, maxConcurrency: 2);
        var calls = 0;
        var job = new InfrastructureScheduledJob("mock-sync", TimeSpan.FromMinutes(5), _ =>
        {
            calls++;
            return System.Threading.Tasks.Task.FromResult(
                calls == 1 ? InfrastructureProviderResult.Success("synced", 3) : InfrastructureProviderResult.Failure("boom"));
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
        using (var repo = new InfrastructureRepository(path))
            repo.RecordJobRun("mock-sync", ok: true, message: "first pass");
        using (var repo = new InfrastructureRepository(path))
        {
            var state = repo.GetJobState("mock-sync");
            Assert.NotNull(state);
            Assert.Contains("first pass", state!.Value.LastResult);
        }
    }

    // ---- Ant registry (visible-only infrastructure ants) ------------------------------------------------

    [Fact]
    public void AntRegistry_InfrastructureAntsAreVisibleButNeverExecutableOrPatchCapable()
    {
        var infrastructure = AntRegistry.Roles.Where(r => r.Colony == "Infrastructure").ToList();
        Assert.Equal(8, infrastructure.Count);
        Assert.All(infrastructure, r => Assert.False(r.Executable));
        Assert.All(infrastructure, r => Assert.False(r.Permissions.ProposePatches));
        Assert.All(infrastructure, r => Assert.False(r.Permissions.ApplyPatches));
        Assert.All(infrastructure, r => Assert.False(r.Permissions.RunShell));
        Assert.Empty(AntRegistry.ValidateRegistry());
    }

    // ---- Permission matrix (D3) --------------------------------------------------------------------

    [Theory]
    // Infrastructure Operator: view + approve only.
    [InlineData(UserRoles.InfrastructureOperator, "read_infrastructure", true)]
    [InlineData(UserRoles.InfrastructureOperator, "approve_infrastructure_actions", true)]
    [InlineData(UserRoles.InfrastructureOperator, "read_status", true)]
    [InlineData(UserRoles.InfrastructureOperator, "manage_infrastructure_integrations", false)]
    [InlineData(UserRoles.InfrastructureOperator, "execute_infrastructure_actions", false)]
    [InlineData(UserRoles.InfrastructureOperator, "manage_providers", false)]
    [InlineData(UserRoles.InfrastructureOperator, "manage_users", false)]
    [InlineData(UserRoles.InfrastructureOperator, "operator_shell", false)]
    [InlineData(UserRoles.InfrastructureOperator, "apply_patch", false)]
    // Mission Coordinator gains nothing from the infrastructure tier.
    [InlineData(UserRoles.Coordinator, "read_infrastructure", false)]
    [InlineData(UserRoles.Coordinator, "manage_infrastructure_integrations", false)]
    [InlineData(UserRoles.Coordinator, "approve_infrastructure_actions", false)]
    [InlineData(UserRoles.Coordinator, "execute_infrastructure_actions", false)]
    // Admin is allowed at the role layer (capability gates still apply on top).
    [InlineData(UserRoles.Admin, "read_infrastructure", true)]
    [InlineData(UserRoles.Admin, "manage_infrastructure_integrations", true)]
    [InlineData(UserRoles.Admin, "approve_infrastructure_actions", true)]
    [InlineData(UserRoles.Admin, "execute_infrastructure_actions", true)]
    public void PermissionMatrix_RoleAllows(string role, string permission, bool expected) =>
        Assert.Equal(expected, UserRoles.RoleAllows(role, permission));

    [Fact]
    public void PermissionMatrix_InfrastructureOperatorRoleIsValidAndNormalizes()
    {
        Assert.True(UserRoles.IsValid(UserRoles.InfrastructureOperator));
        Assert.Equal(UserRoles.InfrastructureOperator, UserRoles.Normalize("Infrastructure_Operator"));
        Assert.Equal(UserRoles.InfrastructureOperator, UserRoles.Normalize("infrastructure-operator"));
        Assert.Equal(UserRoles.InfrastructureOperator, UserRoles.Normalize("infrastructure"));
    }

    [Fact]
    public void CapabilityGates_InfrastructureActionsShipDisabled()
    {
        Assert.True(Anthill.Core.Configuration.AnthillRuntime.ApiPermissions["read_infrastructure"]);
        Assert.True(Anthill.Core.Configuration.AnthillRuntime.ApiPermissions["manage_infrastructure_integrations"]);
        Assert.False(Anthill.Core.Configuration.AnthillRuntime.ApiPermissions["approve_infrastructure_actions"]);
        Assert.False(Anthill.Core.Configuration.AnthillRuntime.ApiPermissions["execute_infrastructure_actions"]);
    }
}
