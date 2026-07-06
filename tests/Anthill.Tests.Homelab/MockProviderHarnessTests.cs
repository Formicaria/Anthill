using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Scheduling;
using Anthill.Core.Homelab.Security;
using Anthill.Core.Integrations;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v1.9.1 shared mock-provider harness (NORTH_STAR Phase 5). One fixture pattern exercises every
/// fake provider identically — the same assertions real Proxmox/DNS/DHCP/firewall/health providers
/// must pass from v1.10+ (swap the factory, keep the tests). Covers the Phase 5 validation list:
/// scheduler run, backoff, concurrency cap, persistence, fake-provider fixture, allowlist wiring.
/// </summary>
public class MockProviderHarnessTests : IDisposable
{
    private readonly string _dir;
    private readonly HomelabRepository _repo;

    public MockProviderHarnessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_mock_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _repo = new HomelabRepository(Path.Combine(_dir, "mock.db"));
    }

    public void Dispose()
    {
        _repo.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>The one factory list — real providers join the same harness later by being added here.</summary>
    public static IEnumerable<object[]> ProviderKinds() =>
        new[] { "proxmox", "dns", "dhcp", "firewall", "health" }.Select(k => new object[] { k });

    private FakeHomelabProvider Create(string kind, IHomelabTargetGuard? guard = null, string host = "") => kind switch
    {
        "proxmox" => new FakeProxmoxProvider(_repo, guard, host),
        "dns" => new FakeDnsProvider(_repo, guard, host),
        "dhcp" => new FakeDhcpProvider(_repo, guard, host),
        "firewall" => new FakeFirewallProvider(_repo, guard, host),
        "health" => new FakeHealthProvider(_repo, guard, host),
        _ => throw new ArgumentException(kind),
    };

    // ---- Fixture: every provider behaves identically ---------------------------------------------

    [Theory]
    [MemberData(nameof(ProviderKinds))]
    public async System.Threading.Tasks.Task Harness_RunSucceeds_WithConsistentStatusAndAuditEvent(string kind)
    {
        var provider = Create(kind);
        var result = await provider.RunAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(provider.ItemCount, result.ItemCount);
        var status = provider.Status();
        Assert.Equal(1, status.Runs);
        Assert.Equal("ok", status.State);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Equal(provider.ItemCount, status.LastItemCount);
        Assert.NotEqual("", status.LastRun);
        Assert.Contains(_repo.RecentEvents(20), e => e.EventType == "provider_run" && e.SubjectId == provider.Name);
    }

    [Theory]
    [MemberData(nameof(ProviderKinds))]
    public async System.Threading.Tasks.Task Harness_FailureInjection_TracksStreakThenRecovers(string kind)
    {
        var provider = Create(kind);
        provider.FailNextRuns(2);

        Assert.False((await provider.RunAsync(CancellationToken.None)).Ok);
        Assert.False((await provider.RunAsync(CancellationToken.None)).Ok);
        var failing = provider.Status();
        Assert.Equal("failing", failing.State);
        Assert.Equal(2, failing.ConsecutiveFailures);

        Assert.True((await provider.RunAsync(CancellationToken.None)).Ok);
        var recovered = provider.Status();
        Assert.Equal("ok", recovered.State);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Equal(3, recovered.Runs);
    }

    [Theory]
    [MemberData(nameof(ProviderKinds))]
    public async System.Threading.Tasks.Task Harness_TargetGuardBlocksUnlistedHost_AllowsAfterAllowlisting(string kind)
    {
        var guard = new HomelabTargetGuard(_repo);
        var provider = Create(kind, guard, host: "192.168.7.50");

        var blocked = await provider.RunAsync(CancellationToken.None);
        Assert.False(blocked.Ok);
        Assert.Contains("allowlist", blocked.Message);

        _repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "192.168.7.0/24", AddedBy = "harness" });
        Assert.True((await provider.RunAsync(CancellationToken.None)).Ok);
    }

    [Theory]
    [MemberData(nameof(ProviderKinds))]
    public async System.Threading.Tasks.Task Harness_DisabledProviderFails_AndIntegrationStatusMirrors(string kind)
    {
        var provider = Create(kind);
        Assert.Equal("idle", provider.GetStatus().State); // never run yet
        provider.Enabled = false;
        Assert.False((await provider.RunAsync(CancellationToken.None)).Ok);
        var integration = provider.GetStatus();
        Assert.Equal(provider.Status().Name, integration.Name);
        Assert.Equal("failing", integration.State);
    }

    // ---- Scheduler + providers -----------------------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Scheduler_RunsAllMockProviders_AndPersistsJobState()
    {
        using var scheduler = new HomelabScheduler(_repo, maxConcurrency: 2);
        var providers = new[] { "proxmox", "dns", "dhcp", "firewall", "health" }.Select(k => Create(k)).ToList();
        foreach (var p in providers)
            scheduler.Register(new HomelabScheduledJob(p.Name, TimeSpan.FromMinutes(5), p.RunAsync));

        foreach (var p in providers)
            Assert.True((await scheduler.RunOnceAsync(p.Name)).Ok);

        foreach (var p in providers)
        {
            var state = _repo.GetJobState(p.Name);
            Assert.NotNull(state);
            Assert.StartsWith("ok", state!.Value.LastResult);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Scheduler_BackoffGrowsWithProviderFailures_AndResetsOnSuccess()
    {
        using var scheduler = new HomelabScheduler(_repo, maxConcurrency: 2, jitterFraction: 0.0);
        var provider = Create("dns");
        var job = new HomelabScheduledJob(provider.Name, TimeSpan.FromMinutes(10), provider.RunAsync);
        scheduler.Register(job);

        await scheduler.RunOnceAsync(provider.Name);
        var healthy = scheduler.NextDelay(job);

        provider.FailNextRuns(2);
        await scheduler.RunOnceAsync(provider.Name);
        var afterOne = scheduler.NextDelay(job);
        await scheduler.RunOnceAsync(provider.Name);
        var afterTwo = scheduler.NextDelay(job);

        Assert.True(afterOne > healthy * 1.5, $"one failure should ~double the delay ({healthy} -> {afterOne})");
        Assert.True(afterTwo > afterOne * 1.5, $"two failures should ~quadruple the delay ({afterOne} -> {afterTwo})");

        await scheduler.RunOnceAsync(provider.Name); // success resets the streak
        Assert.True(scheduler.NextDelay(job) <= healthy * 1.01);
    }

    [Fact]
    public async System.Threading.Tasks.Task Scheduler_ConcurrencyCapIsRespected_NoCheckStampede()
    {
        using var scheduler = new HomelabScheduler(_repo, maxConcurrency: 2);
        var concurrent = 0; var maxConcurrent = 0; var gate = new object();
        for (var i = 0; i < 5; i++)
        {
            var name = $"slow-job-{i}";
            scheduler.Register(new HomelabScheduledJob(name, TimeSpan.FromMinutes(5), async ct =>
            {
                lock (gate) { concurrent++; maxConcurrent = Math.Max(maxConcurrent, concurrent); }
                await System.Threading.Tasks.Task.Delay(150, ct);
                lock (gate) concurrent--;
                return HomelabProviderResult.Success("slow ok");
            }));
        }
        var runs = Enumerable.Range(0, 5).Select(i => scheduler.RunOnceAsync($"slow-job-{i}"));
        var results = await System.Threading.Tasks.Task.WhenAll(runs);
        Assert.All(results, r => Assert.True(r.Ok));
        Assert.True(maxConcurrent <= 2, $"concurrency cap violated: {maxConcurrent} > 2");
    }

    [Fact]
    public async System.Threading.Tasks.Task Scheduler_StartRunsJobsInBackground_AndStopHalts()
    {
        using var scheduler = new HomelabScheduler(_repo, maxConcurrency: 2);
        var runs = 0;
        scheduler.Register(new HomelabScheduledJob("ticker", TimeSpan.FromMilliseconds(120), _ =>
        {
            Interlocked.Increment(ref runs);
            return System.Threading.Tasks.Task.FromResult(HomelabProviderResult.Success("tick"));
        }));

        scheduler.Start();
        Assert.True(scheduler.Running);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref runs) < 1 && DateTime.UtcNow < deadline)
            await System.Threading.Tasks.Task.Delay(50);
        scheduler.Stop();

        Assert.True(runs >= 1, "scheduler never ran the registered job");
        Assert.False(scheduler.Running);
        var settled = Volatile.Read(ref runs);
        await System.Threading.Tasks.Task.Delay(300);
        Assert.Equal(settled, Volatile.Read(ref runs)); // no runs after Stop
    }

    [Fact]
    public async System.Threading.Tasks.Task FakeHealthProvider_CheckAsyncIsDeterministicAndNetworkFree()
    {
        var health = new FakeHealthProvider(_repo);
        var result = await health.CheckAsync("service-x", CancellationToken.None);
        Assert.Equal("healthy", result.Status);
        Assert.Equal("service-x", result.Target);
        Assert.Equal("mock", result.CheckKind);
        Assert.NotEqual("", result.CheckedAt);
    }
}
