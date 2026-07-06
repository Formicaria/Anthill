using System.Net;
using System.Net.Sockets;
using System.Text;
using Anthill.Core.Configuration;
using Anthill.Core.Health;
using Anthill.Core.Homelab;
using Anthill.Core.Homelab.Notifications;
using Anthill.Core.Homelab.Security;
using Xunit;

namespace Anthill.Tests.Homelab;

/// <summary>
/// v1.11.0 health checks + notifications (NORTH_STAR Phase 7 validation list: health
/// classification, mock HTTP/TCP, timeout, notification, plus allowlist routing and schedule
/// persistence). All servers are local loopback sockets — no external network is ever touched.
/// </summary>
[Collection("HomelabRuntimeConfig")] // serialize: these tests mutate AnthillRuntime notification statics
public class HealthAndNotificationTests : IDisposable
{
    private readonly string _dir;
    private readonly HomelabRepository _repo;
    private readonly HomelabTargetGuard _guard;
    private readonly bool _savedEnabled;
    private readonly string _savedGeneric, _savedSlack, _savedDiscord;

    public HealthAndNotificationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill_health_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _repo = new HomelabRepository(Path.Combine(_dir, "health.db"));
        _guard = new HomelabTargetGuard(_repo);
        _savedEnabled = AnthillRuntime.EnableHomelabNotifications;
        _savedGeneric = AnthillRuntime.HomelabGenericWebhook;
        _savedSlack = AnthillRuntime.HomelabSlackWebhook;
        _savedDiscord = AnthillRuntime.HomelabDiscordWebhook;
    }

    public void Dispose()
    {
        AnthillRuntime.EnableHomelabNotifications = _savedEnabled;
        AnthillRuntime.HomelabGenericWebhook = _savedGeneric;
        AnthillRuntime.HomelabSlackWebhook = _savedSlack;
        AnthillRuntime.HomelabDiscordWebhook = _savedDiscord;
        _repo.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void AllowLoopback() =>
        _repo.AddAllowlistEntry(new TargetAllowlistRecord { Target = "127.0.0.1", AddedBy = "test" });

    /// <summary>Minimal loopback HTTP server: scripted status per request, or hang (status &lt; 0).</summary>
    private sealed class MiniHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        public int Port { get; }
        public List<string> Requests { get; } = new();

        public MiniHttpServer(Func<string, int> statusFor)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoop(statusFor);
        }

        private async System.Threading.Tasks.Task AcceptLoop(Func<string, int> statusFor)
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    _ = Handle(client, statusFor);
                }
            }
            catch { /* listener stopped */ }
        }

        private async System.Threading.Tasks.Task Handle(TcpClient client, Func<string, int> statusFor)
        {
            try
            {
                using var c = client;
                var stream = c.GetStream();
                var buffer = new byte[16384];
                var read = await stream.ReadAsync(buffer.AsMemory(), _cts.Token).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(buffer, 0, read);
                lock (Requests) Requests.Add(text);
                var status = statusFor(text);
                if (status < 0) { await System.Threading.Tasks.Task.Delay(30000, _cts.Token).ConfigureAwait(false); return; }
                var resp = $"HTTP/1.1 {status} S\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(resp).AsMemory(), _cts.Token).ConfigureAwait(false);
            }
            catch { /* client gone / cancelled */ }
        }

        public void Dispose() { _cts.Cancel(); _listener.Stop(); _cts.Dispose(); }
    }

    // ---- Classification -----------------------------------------------------------------------

    [Theory]
    [InlineData("http", "http://192.168.9.9:8080/x", "192.168.9.9")]
    [InlineData("service_url", "https://nas.lan/ui", "nas.lan")]
    [InlineData("tcp", "nas.lan:445", "nas.lan")]
    [InlineData("ping", "nas.lan", "nas.lan")]
    [InlineData("http", "not a url", "")]
    public void HostOf_ExtractsTheAllowlistHost(string kind, string target, string expected) =>
        Assert.Equal(expected, HealthCheckRunner.HostOf(new HealthCheckSchedule { CheckKind = kind, Target = target }));

    [Fact]
    public async System.Threading.Tasks.Task Check_BlockedWithoutAllowlist_NoIoHappens()
    {
        using var server = new MiniHttpServer(_ => 200);
        var runner = new HealthCheckRunner(_repo, _guard);
        var result = await runner.RunCheckAsync(new HealthCheckSchedule { CheckKind = "http", Target = $"http://127.0.0.1:{server.Port}/" });
        Assert.Equal(HealthStatus.Failed, result.Status);
        Assert.Contains("allowlist", result.Detail);
        Assert.Empty(server.Requests); // D1: not even one packet before the allowlist check
    }

    [Fact]
    public async System.Threading.Tasks.Task Http_Classifies_200Healthy_404Degraded_500Failed()
    {
        AllowLoopback();
        using var server = new MiniHttpServer(req => req.Contains("/bad") ? 404 : req.Contains("/boom") ? 500 : 200);
        var runner = new HealthCheckRunner(_repo, _guard);

        Assert.Equal(HealthStatus.Healthy, (await runner.RunCheckAsync(Sched("http", $"http://127.0.0.1:{server.Port}/ok"))).Status);
        Assert.Equal(HealthStatus.Degraded, (await runner.RunCheckAsync(Sched("http", $"http://127.0.0.1:{server.Port}/bad"))).Status);
        Assert.Equal(HealthStatus.Failed, (await runner.RunCheckAsync(Sched("http", $"http://127.0.0.1:{server.Port}/boom"))).Status);
        Assert.Equal(3, _repo.RecentHealthResults(10).Count); // every run persisted
    }

    [Fact]
    public async System.Threading.Tasks.Task Tcp_OpenPortHealthy_ClosedPortFailed()
    {
        AllowLoopback();
        using var server = new MiniHttpServer(_ => 200); // any listening socket works for tcp connect
        var runner = new HealthCheckRunner(_repo, _guard);

        Assert.Equal(HealthStatus.Healthy, (await runner.RunCheckAsync(Sched("tcp", $"127.0.0.1:{server.Port}"))).Status);
        var closed = await runner.RunCheckAsync(Sched("tcp", "127.0.0.1:1")); // port 1: nothing listens
        Assert.Equal(HealthStatus.Failed, closed.Status);
        var malformed = await runner.RunCheckAsync(Sched("tcp", "127.0.0.1"));
        Assert.Equal(HealthStatus.Failed, malformed.Status);
        Assert.Contains("host:port", malformed.Detail);
    }

    [Fact]
    public async System.Threading.Tasks.Task Timeout_HungServerFailsFast_NeverHangsTheApp()
    {
        AllowLoopback();
        using var server = new MiniHttpServer(_ => -1); // accepts, never answers
        var runner = new HealthCheckRunner(_repo, _guard);
        var sched = Sched("http", $"http://127.0.0.1:{server.Port}/"); sched.TimeoutMs = 500;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunCheckAsync(sched);
        sw.Stop();
        Assert.Equal(HealthStatus.Failed, result.Status);
        Assert.Contains("timed out", result.Detail);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"timeout took {sw.Elapsed} — strict timeouts must bound every check");
    }

    [Fact]
    public async System.Threading.Tasks.Task Placeholders_DiskAndUptime_ReportUnknown()
    {
        var runner = new HealthCheckRunner(_repo, _guard);
        Assert.Equal(HealthStatus.Unknown, (await runner.RunCheckAsync(Sched("disk", "pve1"))).Status);
        Assert.Equal(HealthStatus.Unknown, (await runner.RunCheckAsync(Sched("uptime", "pve1"))).Status);
    }

    private static HealthCheckSchedule Sched(string kind, string target) => new() { CheckKind = kind, Target = target };

    // ---- Failure alerts + incident candidate -----------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task ThreeConsecutiveFailures_RaiseOneIncidentCandidate()
    {
        AllowLoopback();
        var runner = new HealthCheckRunner(_repo, _guard);
        var sched = Sched("tcp", "127.0.0.1:1");
        for (var i = 0; i < 4; i++) await runner.RunCheckAsync(sched);

        var events = _repo.RecentEvents(50);
        Assert.Equal(4, events.Count(e => e.EventType == "health_check_failed"));
        Assert.Single(events, e => e.EventType == "incident_candidate"); // fires once per streak, at N=3
    }

    // ---- Notifications ---------------------------------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Notifications_DisabledByDefault_SendsNothing()
    {
        AnthillRuntime.EnableHomelabNotifications = false;
        AnthillRuntime.HomelabGenericWebhook = "http://127.0.0.1:9/never";
        var notifier = new NotificationService(_repo);
        Assert.Equal(0, await notifier.SendAsync(new AlertRecord { Kind = "test", Message = "x" }));
    }

    [Fact]
    public async System.Threading.Tasks.Task Notifications_DeliverToGenericWebhook_AndAuditWithoutUrl()
    {
        using var hook = new MiniHttpServer(_ => 200);
        AnthillRuntime.EnableHomelabNotifications = true;
        AnthillRuntime.HomelabGenericWebhook = $"http://127.0.0.1:{hook.Port}/hook-secret-path";
        AnthillRuntime.HomelabSlackWebhook = ""; AnthillRuntime.HomelabDiscordWebhook = "";
        var notifier = new NotificationService(_repo);

        var delivered = await notifier.SendAsync(new AlertRecord { Kind = "health_check_failure", Target = "nas.lan", Severity = "warning", Message = "tcp check: refused" });
        Assert.Equal(1, delivered);
        Assert.Contains(hook.Requests, r => r.Contains("health_check_failure") && r.Contains("nas.lan"));

        var audit = _repo.RecentEvents(10).Where(e => e.EventType == "notification_sent").ToList();
        Assert.NotEmpty(audit);
        Assert.All(audit, e => Assert.DoesNotContain("hook-secret-path", e.Message)); // URL never audited
    }

    [Fact]
    public async System.Threading.Tasks.Task Notifications_UnreachableWebhook_FailsSoftAndAudits()
    {
        AnthillRuntime.EnableHomelabNotifications = true;
        AnthillRuntime.HomelabGenericWebhook = "http://127.0.0.1:1/void"; // refused instantly
        AnthillRuntime.HomelabSlackWebhook = ""; AnthillRuntime.HomelabDiscordWebhook = "";
        var notifier = new NotificationService(_repo, TimeSpan.FromSeconds(2));
        Assert.Equal(0, await notifier.SendAsync(new AlertRecord { Kind = "test", Message = "x" }));
        Assert.Contains(_repo.RecentEvents(10), e => e.EventType == "notification_failed");
    }

    // ---- Summary + schedule persistence ---------------------------------------------------------

    [Fact]
    public void Summary_UsesLatestResultPerTarget()
    {
        var runner = new HealthCheckRunner(_repo, _guard);
        _repo.SaveHealthResult(new HealthCheckResult { CheckKind = "tcp", Target = "a:1", Status = HealthStatus.Failed, CheckedAt = "2026-07-01T00:00:00Z" });
        _repo.SaveHealthResult(new HealthCheckResult { CheckKind = "tcp", Target = "a:1", Status = HealthStatus.Healthy, CheckedAt = "2026-07-02T00:00:00Z" }); // recovered
        _repo.SaveHealthResult(new HealthCheckResult { CheckKind = "http", Target = "http://b/", Status = HealthStatus.Failed, CheckedAt = "2026-07-02T00:00:00Z" });

        var summary = runner.Summarize();
        Assert.Equal(2, summary.Targets);
        Assert.Equal(1, summary.Healthy);
        Assert.Equal(1, summary.Failed);
        Assert.Single(summary.FailingTargets);
        Assert.Equal("http://b/", summary.FailingTargets[0].Target);
    }

    [Fact]
    public void Schedules_CrudAndPersistenceAcrossReopen()
    {
        var path = Path.Combine(_dir, "sched.db");
        string id;
        using (var repo = new HomelabRepository(path))
        {
            var schedule = new HealthCheckSchedule { CheckKind = "tcp", Target = "nas.lan:445", TimeoutMs = 1500 };
            id = schedule.Id;
            repo.UpsertHealthSchedule(schedule, "tester");
            schedule.Enabled = false;
            repo.UpsertHealthSchedule(schedule, "tester"); // update, same id
        }
        using (var repo = new HomelabRepository(path))
        {
            var loaded = Assert.Single(repo.ListHealthSchedules());
            Assert.Equal(id, loaded.Id);
            Assert.False(loaded.Enabled);
            Assert.Equal(1500, loaded.TimeoutMs);
            repo.RemoveHealthSchedule(id, "tester");
            Assert.Empty(repo.ListHealthSchedules());
        }
    }
}
