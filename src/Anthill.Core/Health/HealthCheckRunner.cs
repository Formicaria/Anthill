using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Anthill.Core.Common;
using Anthill.Core.Configuration;
using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Notifications;

namespace Anthill.Core.Health;

/// <summary>
/// Deterministic health-check execution (v1.11.0, NORTH_STAR Phase 7). Plain C# — never routed
/// through the model router. Every check: (1) resolves its target host, (2) must pass the Homelab
/// Target Allowlist (D1) before any I/O happens, (3) runs under a strict timeout so a hung host
/// can never hang the app, (4) persists a HealthCheckResult, and (5) on failure raises alerts
/// (health_check_failure; incident_candidate at N consecutive failures) through the config-gated
/// NotificationService. Check kinds: ping, http, tcp (host:port), service_url, and the disk /
/// uptime placeholders (always "unknown" until agent support lands in a later phase).
/// </summary>
public sealed class HealthCheckRunner
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    { Timeout = Timeout.InfiniteTimeSpan };

    private readonly IHomelabRepository _repository;
    private readonly IHomelabTargetGuard _targetGuard;
    private readonly NotificationService? _notifier;

    /// <summary>Consecutive failures of one target that promote it to an incident candidate.</summary>
    public const int IncidentCandidateThreshold = 3;

    public HealthCheckRunner(IHomelabRepository repository, IHomelabTargetGuard targetGuard, NotificationService? notifier = null)
    {
        _repository = repository;
        _targetGuard = targetGuard;
        _notifier = notifier;
    }

    private static TimeSpan TimeoutFor(HealthCheckSchedule s) =>
        TimeSpan.FromMilliseconds(s.TimeoutMs > 0 ? Math.Clamp(s.TimeoutMs, 250, 60_000) : AnthillRuntime.HomelabHealthTimeoutMs);

    /// <summary>Extracts the host the allowlist must approve. URL kinds parse the URI; tcp strips :port.</summary>
    internal static string HostOf(HealthCheckSchedule s)
    {
        var t = (s.Target ?? "").Trim();
        if (t.Length == 0) return "";
        if (s.CheckKind is "http" or "service_url")
            return Uri.TryCreate(t, UriKind.Absolute, out var uri) ? uri.Host : "";
        var idx = t.LastIndexOf(':');
        return idx > 0 && int.TryParse(t[(idx + 1)..], out _) ? t[..idx] : t;
    }

    /// <summary>Runs every enabled schedule sequentially (the shared scheduler bounds overall concurrency).</summary>
    public async System.Threading.Tasks.Task<HomelabProviderResult> RunAllAsync(CancellationToken ct)
    {
        var schedules = _repository.ListHealthSchedules().Where(s => s.Enabled).ToList();
        var failed = 0;
        foreach (var schedule in schedules)
        {
            if (ct.IsCancellationRequested) break;
            var result = await RunCheckAsync(schedule, ct).ConfigureAwait(false);
            if (result.Status == HealthStatus.Failed) failed++;
        }
        return failed == 0
            ? HomelabProviderResult.Success($"{schedules.Count} check(s) run", schedules.Count)
            : HomelabProviderResult.Failure($"{failed}/{schedules.Count} check(s) failed");
    }

    /// <summary>Runs one check, persists the result, and raises failure alerts. Never throws.</summary>
    public async System.Threading.Tasks.Task<HealthCheckResult> RunCheckAsync(HealthCheckSchedule schedule, CancellationToken ct = default)
    {
        var result = new HealthCheckResult
        {
            CheckKind = schedule.CheckKind, Target = schedule.Target,
            ServiceId = schedule.ServiceId, NodeId = schedule.NodeId,
            Status = HealthStatus.Unknown, CheckedAt = AnthillTime.NowUtc().ToIso(),
        };

        var host = HostOf(schedule);
        var sw = Stopwatch.StartNew();
        try
        {
            if (schedule.CheckKind is "disk" or "uptime")
            {
                result.Detail = $"{schedule.CheckKind} checks are placeholders until agent support lands (see docs/NORTH_STAR.md)";
            }
            else if (host.Length == 0)
            {
                result.Status = HealthStatus.Failed;
                result.Detail = "target is empty or not a valid host/URL";
            }
            else if (!_targetGuard.IsAllowed(host))
            {
                // D1: no I/O of any kind against a host the operator has not allowlisted.
                result.Status = HealthStatus.Failed;
                result.Detail = $"host '{host}' is not on the homelab target allowlist — add it under /homelab/allowlist";
            }
            else
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeoutFor(schedule));
                switch (schedule.CheckKind)
                {
                    case "ping": await RunPing(host, result, TimeoutFor(schedule)).ConfigureAwait(false); break;
                    case "tcp": await RunTcp(schedule.Target.Trim(), host, result, cts.Token).ConfigureAwait(false); break;
                    case "http":
                    case "service_url": await RunHttp(schedule.Target.Trim(), result, cts.Token).ConfigureAwait(false); break;
                    default:
                        result.Status = HealthStatus.Failed;
                        result.Detail = $"unknown check kind '{schedule.CheckKind}'";
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Status = HealthStatus.Failed;
            result.Detail = $"timed out after {TimeoutFor(schedule).TotalMilliseconds:F0}ms";
        }
        catch (Exception ex)
        {
            result.Status = HealthStatus.Failed;
            result.Detail = ex.GetBaseException().Message;
        }
        result.LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);

        _repository.SaveHealthResult(result);
        if (result.Status == HealthStatus.Failed)
            await RaiseFailureAlerts(result, ct).ConfigureAwait(false);
        return result;
    }

    private static async System.Threading.Tasks.Task RunPing(string host, HealthCheckResult result, TimeSpan timeout)
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(host, (int)timeout.TotalMilliseconds).ConfigureAwait(false);
        result.Status = reply.Status == IPStatus.Success ? HealthStatus.Healthy : HealthStatus.Failed;
        result.Detail = $"ping {reply.Status}";
    }

    private static async System.Threading.Tasks.Task RunTcp(string target, string host, HealthCheckResult result, CancellationToken ct)
    {
        var idx = target.LastIndexOf(':');
        if (idx <= 0 || !int.TryParse(target[(idx + 1)..], out var port) || port is < 1 or > 65535)
        {
            result.Status = HealthStatus.Failed;
            result.Detail = "tcp checks need target in host:port form";
            return;
        }
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        result.Status = HealthStatus.Healthy;
        result.Detail = $"tcp connect ok on port {port}";
    }

    private static async System.Threading.Tasks.Task RunHttp(string url, HealthCheckResult result, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        result.Status = code < 400 ? HealthStatus.Healthy : code < 500 ? HealthStatus.Degraded : HealthStatus.Failed;
        result.Detail = $"HTTP {code}";
    }

    private async System.Threading.Tasks.Task RaiseFailureAlerts(HealthCheckResult result, CancellationToken ct)
    {
        _repository.RecordEvent(new HomelabEvent
        {
            EventType = "health_check_failed", SubjectKind = "health_check", SubjectId = result.Id,
            Severity = "warning", Message = $"{result.CheckKind} {result.Target}: {result.Detail}",
        });

        // Incident candidate: exactly N consecutive failures for this target (fires once per streak).
        var streak = _repository.RecentHealthResultsForTarget(result.Target, IncidentCandidateThreshold + 1)
            .TakeWhile(r => r.Status == HealthStatus.Failed).Count();
        var incidentCandidate = streak == IncidentCandidateThreshold;
        if (incidentCandidate)
            _repository.RecordEvent(new HomelabEvent
            {
                EventType = "incident_candidate", SubjectKind = "health_check", SubjectId = result.Target,
                Severity = "error", Message = $"{result.Target} has failed {streak} consecutive {result.CheckKind} checks",
            });

        if (_notifier is not null)
        {
            await _notifier.SendAsync(new AlertRecord
            {
                Kind = incidentCandidate ? "incident_candidate" : "health_check_failure",
                Target = result.Target, Severity = incidentCandidate ? "error" : "warning",
                Message = $"{result.CheckKind} check: {result.Detail}",
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Latest-result-per-target rollup for the summary endpoint and UI.</summary>
    public HealthSummary Summarize(int recentWindow = 500)
    {
        var latest = _repository.RecentHealthResults(recentWindow)
            .Where(r => r.CheckKind is not ("disk" or "uptime"))
            .GroupBy(r => $"{r.CheckKind}|{r.Target}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.CheckedAt).First())
            .ToList();
        return new HealthSummary
        {
            Healthy = latest.Count(r => r.Status == HealthStatus.Healthy),
            Degraded = latest.Count(r => r.Status == HealthStatus.Degraded),
            Failed = latest.Count(r => r.Status == HealthStatus.Failed),
            Unknown = latest.Count(r => r.Status == HealthStatus.Unknown),
            Targets = latest.Count,
            FailingTargets = latest.Where(r => r.Status is HealthStatus.Failed or HealthStatus.Degraded)
                .OrderBy(r => r.Target).ToList(),
            ComputedAt = AnthillTime.NowUtc().ToIso(),
        };
    }
}
