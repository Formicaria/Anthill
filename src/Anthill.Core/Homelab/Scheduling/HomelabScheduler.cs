namespace Anthill.Core.Homelab.Scheduling;

/// <summary>
/// Homelab Scheduler skeleton (v1.9.0, NORTH_STAR D4). The single interval background runner that
/// will own health checks, Proxmox syncs, and network polls from v1.9.1 onward — subsystems must
/// register jobs here instead of spinning their own timers (NORTH_STAR §6 rule 2).
///
/// v1.9.0 scope: skeleton only. Disabled by default (homelab_scheduler_enabled config gate), only
/// no-op/mock jobs may be registered, no real network calls. Provides: per-job intervals with
/// jitter (no check stampede), exponential backoff on consecutive failures, a global concurrency
/// cap, and last-run/last-result persistence through the repository so state survives restart.
/// </summary>
public sealed record HomelabScheduledJob(
    string Name,
    TimeSpan Interval,
    Func<CancellationToken, System.Threading.Tasks.Task<HomelabProviderResult>> Runner);

public sealed class HomelabScheduler : IDisposable
{
    private readonly IHomelabRepository _repository;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly List<HomelabScheduledJob> _jobs = new();
    private readonly Dictionary<string, int> _consecutiveFailures = new();
    private readonly object _lock = new();
    private readonly double _jitterFraction;
    private CancellationTokenSource? _cts;
    private readonly List<System.Threading.Tasks.Task> _loops = new();

    public bool Running { get; private set; }
    public int MaxConcurrency { get; }
    public IReadOnlyList<HomelabScheduledJob> Jobs { get { lock (_lock) return _jobs.ToList(); } }

    public HomelabScheduler(IHomelabRepository repository, int maxConcurrency = 2, double jitterFraction = 0.1)
    {
        _repository = repository;
        MaxConcurrency = Math.Clamp(maxConcurrency, 1, 16);
        _concurrencyGate = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        _jitterFraction = Math.Clamp(jitterFraction, 0.0, 0.5);
    }

    public void Register(HomelabScheduledJob job)
    {
        lock (_lock)
        {
            if (_jobs.Any(j => string.Equals(j.Name, job.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Duplicate homelab job name: {job.Name}");
            _jobs.Add(job);
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (Running) return;
            _cts = new CancellationTokenSource();
            Running = true;
            foreach (var job in _jobs)
                _loops.Add(RunLoop(job, _cts.Token));
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!Running) return;
            _cts?.Cancel();
            Running = false;
        }
        try { System.Threading.Tasks.Task.WhenAll(_loops).Wait(TimeSpan.FromSeconds(5)); } catch { }
        _loops.Clear();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _concurrencyGate.Dispose();
    }

    /// <summary>Runs one job immediately (test/manual path) honoring the concurrency cap.</summary>
    public async System.Threading.Tasks.Task<HomelabProviderResult> RunOnceAsync(string jobName, CancellationToken ct = default)
    {
        HomelabScheduledJob? job;
        lock (_lock) job = _jobs.FirstOrDefault(j => string.Equals(j.Name, jobName, StringComparison.OrdinalIgnoreCase));
        if (job is null) return HomelabProviderResult.Failure($"Unknown job: {jobName}");
        return await ExecuteAsync(job, ct).ConfigureAwait(false);
    }

    /// <summary>Delay before the next run: base interval, doubled per consecutive failure (capped at 8x), plus or minus jitter.</summary>
    internal TimeSpan NextDelay(HomelabScheduledJob job)
    {
        int failures;
        lock (_lock) failures = _consecutiveFailures.GetValueOrDefault(job.Name, 0);
        var multiplier = Math.Min(Math.Pow(2, failures), 8.0);
        var baseMs = job.Interval.TotalMilliseconds * multiplier;
        var jitter = baseMs * _jitterFraction;
        var ms = baseMs + ((Random.Shared.NextDouble() * 2.0) - 1.0) * jitter;
        return TimeSpan.FromMilliseconds(Math.Max(50, ms));
    }

    private async System.Threading.Tasks.Task RunLoop(HomelabScheduledJob job, CancellationToken ct)
    {
        // Initial spread: start each loop at a random fraction of its interval so all jobs never
        // fire at once on boot (the "check stampede" guard).
        try { await System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * job.Interval.TotalMilliseconds), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            await ExecuteAsync(job, ct).ConfigureAwait(false);
            try { await System.Threading.Tasks.Task.Delay(NextDelay(job), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async System.Threading.Tasks.Task<HomelabProviderResult> ExecuteAsync(HomelabScheduledJob job, CancellationToken ct)
    {
        await _concurrencyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            HomelabProviderResult result;
            try { result = await job.Runner(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result = HomelabProviderResult.Failure(ex.Message); }

            lock (_lock)
                _consecutiveFailures[job.Name] = result.Ok ? 0 : _consecutiveFailures.GetValueOrDefault(job.Name, 0) + 1;
            _repository.RecordJobRun(job.Name, result.Ok, result.Message);
            return result;
        }
        finally { _concurrencyGate.Release(); }
    }
}
