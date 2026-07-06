using Anthill.Core.Common;
using Anthill.Core.Homelab;

namespace Anthill.Core.Integrations;

/// <summary>
/// Secret-free, network-free status of one homelab provider (v1.9.1, NORTH_STAR Phase 5).
/// Richer than <see cref="IntegrationStatus"/>: adds run counters and failure streaks so the
/// harness (and later the UI) can assert consistent provider behavior.
/// </summary>
public sealed class HomelabProviderStatus
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string TargetHost { get; set; } = "";
    public bool Enabled { get; set; }
    public string State { get; set; } = "idle"; // idle | ok | failing
    public int Runs { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int LastItemCount { get; set; }
    public string LastRun { get; set; } = "";
    public string LastResult { get; set; } = "";
}

/// <summary>
/// Base class for the v1.9.1 mock providers — the shared execution/testing pattern every real
/// homelab provider follows from v1.10+ (NORTH_STAR Phase 5 "one shared pattern"):
///
/// - NO real network calls, ever. Runs are deterministic: a fixed item count, an optional
///   simulated delay, and scriptable failure injection (<see cref="FailNextRuns"/>).
/// - Target-guard discipline: if a TargetHost is set, the run consults
///   <see cref="IHomelabTargetGuard"/> first and fails without "contacting" anything when the
///   host is not allowlisted — the exact D1 wiring real providers must copy.
/// - Every run records a homelab event; status is available secret-free at any time.
/// - Thread-safe so the scheduler's concurrency cap can be exercised in tests.
/// </summary>
public abstract class FakeHomelabProvider : IIntegrationStatusProvider
{
    private readonly object _lock = new();
    private readonly IHomelabRepository _repository;
    private readonly IHomelabTargetGuard? _targetGuard;
    private int _runs, _consecutiveFailures, _lastItemCount, _failNextRuns;
    private string _lastRun = "", _lastResult = "", _state = "idle";

    public string Name { get; }
    public string Kind { get; }
    public string TargetHost { get; }
    public bool Enabled { get; set; } = true;
    /// <summary>Deterministic number of items each successful run "collects".</summary>
    public int ItemCount { get; }
    /// <summary>Simulated work duration per run — lets tests exercise the concurrency cap.</summary>
    public TimeSpan SimulatedLatency { get; set; } = TimeSpan.Zero;

    protected FakeHomelabProvider(string name, string kind, int itemCount, IHomelabRepository repository,
        IHomelabTargetGuard? targetGuard = null, string targetHost = "")
    {
        Name = name; Kind = kind; ItemCount = itemCount;
        _repository = repository; _targetGuard = targetGuard; TargetHost = targetHost;
    }

    /// <summary>Make the next <paramref name="count"/> runs fail (backoff/failing-state tests).</summary>
    public void FailNextRuns(int count) { lock (_lock) _failNextRuns = Math.Max(0, count); }

    public async System.Threading.Tasks.Task<HomelabProviderResult> RunAsync(CancellationToken ct)
    {
        if (!Enabled) return Record(HomelabProviderResult.Failure("provider disabled"));
        if (SimulatedLatency > TimeSpan.Zero)
            await System.Threading.Tasks.Task.Delay(SimulatedLatency, ct).ConfigureAwait(false);

        // D1 discipline: a provider with a target host may only proceed when allowlisted.
        if (TargetHost.Length > 0 && _targetGuard is not null && !_targetGuard.IsAllowed(TargetHost))
            return Record(HomelabProviderResult.Failure($"target '{TargetHost}' is not on the homelab allowlist"));

        bool injectedFailure;
        lock (_lock)
        {
            injectedFailure = _failNextRuns > 0;
            if (injectedFailure) _failNextRuns--;
        }
        return Record(injectedFailure
            ? HomelabProviderResult.Failure("injected failure (mock)")
            : HomelabProviderResult.Success($"{Kind} sync ok (mock)", ItemCount));
    }

    private HomelabProviderResult Record(HomelabProviderResult result)
    {
        lock (_lock)
        {
            _runs++;
            _consecutiveFailures = result.Ok ? 0 : _consecutiveFailures + 1;
            _lastItemCount = result.Ok ? result.ItemCount : _lastItemCount;
            _lastRun = AnthillTime.NowUtc().ToIso();
            _lastResult = (result.Ok ? "ok: " : "failed: ") + result.Message;
            _state = result.Ok ? "ok" : "failing";
        }
        _repository.RecordEvent(new HomelabEvent
        {
            EventType = "provider_run", SubjectKind = "provider", SubjectId = Name,
            Severity = result.Ok ? "info" : "warning",
            Message = $"{Name}: {_lastResult}",
        });
        return result;
    }

    public HomelabProviderStatus Status()
    {
        lock (_lock) return new HomelabProviderStatus
        {
            Name = Name, Kind = Kind, TargetHost = TargetHost, Enabled = Enabled, State = _state,
            Runs = _runs, ConsecutiveFailures = _consecutiveFailures, LastItemCount = _lastItemCount,
            LastRun = _lastRun, LastResult = _lastResult,
        };
    }

    public IntegrationStatus GetStatus()
    {
        var s = Status();
        return new IntegrationStatus
        {
            Name = s.Name, Kind = s.Kind, Enabled = s.Enabled,
            State = s.Runs == 0 ? "idle" : s.State, LastRun = s.LastRun, LastResult = s.LastResult,
        };
    }
}

/// <summary>Mock Proxmox sync: pretends to inventory a small cluster. Also an IInventoryProvider.</summary>
public sealed class FakeProxmoxProvider : FakeHomelabProvider, IInventoryProvider
{
    public FakeProxmoxProvider(IHomelabRepository repository, IHomelabTargetGuard? guard = null, string targetHost = "")
        : base("fake-proxmox", "proxmox", itemCount: 7, repository, guard, targetHost) { }
    public System.Threading.Tasks.Task<HomelabProviderResult> SyncInventoryAsync(CancellationToken ct) => RunAsync(ct);
}

/// <summary>Mock DNS provider: pretends to read a zone. Shape shared with the V2.4 IDnsProvider.</summary>
public sealed class FakeDnsProvider : FakeHomelabProvider
{
    public FakeDnsProvider(IHomelabRepository repository, IHomelabTargetGuard? guard = null, string targetHost = "")
        : base("fake-dns", "dns", itemCount: 12, repository, guard, targetHost) { }
}

/// <summary>Mock DHCP provider: pretends to read leases.</summary>
public sealed class FakeDhcpProvider : FakeHomelabProvider
{
    public FakeDhcpProvider(IHomelabRepository repository, IHomelabTargetGuard? guard = null, string targetHost = "")
        : base("fake-dhcp", "dhcp", itemCount: 9, repository, guard, targetHost) { }
}

/// <summary>Mock firewall provider: pretends to read the ruleset. Read-only, like everything pre-V2.4.</summary>
public sealed class FakeFirewallProvider : FakeHomelabProvider
{
    public FakeFirewallProvider(IHomelabRepository repository, IHomelabTargetGuard? guard = null, string targetHost = "")
        : base("fake-firewall", "firewall", itemCount: 15, repository, guard, targetHost) { }
}

/// <summary>Mock health provider: deterministic healthy results, no sockets. Also an IHealthCheckProvider.</summary>
public sealed class FakeHealthProvider : FakeHomelabProvider, IHealthCheckProvider
{
    public FakeHealthProvider(IHomelabRepository repository, IHomelabTargetGuard? guard = null, string targetHost = "")
        : base("fake-health", "health", itemCount: 1, repository, guard, targetHost) { }

    public System.Threading.Tasks.Task<HealthCheckResult> CheckAsync(string target, CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult(new HealthCheckResult
        {
            CheckKind = "mock", Target = target, Status = "healthy",
            LatencyMs = 1.0, Detail = "mock check - no network I/O",
            CheckedAt = AnthillTime.NowUtc().ToIso(),
        });
}
