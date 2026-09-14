using System.Diagnostics;
using System.Text.RegularExpressions;
using Anthill.Api;
using Anthill.Core.Configuration;
using Xunit;

// GlobalUsings.cs binds the bare `Task` to Anthill.Core.Domain.Task — the MISSION task — so the
// threading one must be named. Same per-file alias KnowledgeIntegrationTests and EventBusTests use.
// The generic form (Task<T>) resolves on its own: a non-generic alias never shadows an arity-1 lookup.
using ThreadingTask = System.Threading.Tasks.Task;

namespace Anthill.Tests;

/// <summary>
/// W3-03 — the host half of managed engine supervision, exercised without a real engine.
///
/// Everything the supervisor does to the outside world is behind two seams (<see cref="IEngineLauncher"/>,
/// <see cref="IEngineProber"/>), so these drive the whole state machine — start, verify, refuse,
/// crash, back off, hit the ceiling, attach-or-refuse on a busy store, and stop — against a fake
/// process and a fake prober. What is NOT tested here is the two real seams talking to a real
/// FORAGER; that is the integration case, and the line between the two is exactly this interface.
///
/// The tests use a fast <see cref="ForagerSupervisor.RestartPolicy"/> (millisecond backoff) so the
/// loop runs in real time without waiting out the shipping 1s-to-30s schedule, and each test gets
/// its own temp state and data directory (the supervisor writes a credential file and an install
/// record), cleaned up on <see cref="Dispose"/>.
/// </summary>
public sealed class ForagerSupervisorTests : IDisposable
{
    private static readonly ForagerSupervisor.RestartPolicy Fast = new()
    {
        BackoffBase = TimeSpan.FromMilliseconds(5),
        BackoffCap = TimeSpan.FromMilliseconds(20),
        CeilingCount = 3,
        CeilingWindow = TimeSpan.FromSeconds(30),
        StartupTimeout = TimeSpan.FromMilliseconds(500),
    };

    private readonly List<string> _temp = new();

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "forager-sup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _temp.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _temp)
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { }
    }

    // ---------------------------------------------------------------- pure statics

    [Fact]
    public void GenerateHostSecret_MatchesForagerHostCredentialContract()
    {
        // FORAGER's HOST_SECRET regex, verbatim: /^fgr_[A-Za-z0-9_-]{32,}$/
        var rx = new Regex("^fgr_[A-Za-z0-9_-]{32,}$");
        for (var i = 0; i < 50; i++)
            Assert.Matches(rx, ForagerSupervisor.GenerateHostSecret());

        // And it is actually random, not a constant that happens to match.
        Assert.NotEqual(ForagerSupervisor.GenerateHostSecret(), ForagerSupervisor.GenerateHostSecret());
    }

    [Theory]
    [InlineData("0.6.9", false)] // below the managed floor -> refused
    [InlineData("0.6.99", false)]
    [InlineData("0.7.0", true)]  // the floor itself is fine
    [InlineData("0.7.1", true)]
    [InlineData("1.2.3", true)]  // newer is tolerated, not refused
    [InlineData("v0.7.0", true)] // a leading v parses
    [InlineData("0.8.0-rc1", true)] // pre-release suffix ignored for the compare
    public void CheckVersion_RefusesOnlyBelowTheFloor(string version, bool ok)
    {
        var problem = ForagerSupervisor.CheckVersion(version);
        if (ok) Assert.Null(problem); else Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-version")]
    public void CheckVersion_ToleratesWhatItCannotParse(string? version)
    {
        // An engine that does not state a version, or states an unparseable one, is not refused —
        // refusing on a string we do not understand would be stricter than the contract.
        Assert.Null(ForagerSupervisor.CheckVersion(version));
    }

    // ---------------------------------------------------------------- the loop

    [Fact]
    public async ThreadingTask ManagedOff_StaysDisabled_AndStartsNothing()
    {
        var h = new Harness();
        var sup = Build(h, new KnowledgeSettings { Managed = false });

        await sup.StartAsync();

        Assert.Equal(ForagerSupervisor.EngineState.Disabled, sup.Status().State);
        Assert.Equal(0, h.StartCalls);
        Assert.Equal("", sup.ManagedEndpoint);
        Assert.Equal("", sup.ManagedSecret);
    }

    [Fact]
    public async ThreadingTask HappyPath_VerifiesAndReachesRunning_ExposingEndpointAndSecret()
    {
        var h = new Harness { Identity = Good("fgi_alpha") };
        h.Enqueue(FakeProcess.Listening(8801));
        var sup = Build(h, Managed());

        _ = sup.StartAsync();
        await WaitFor(sup, s => s == ForagerSupervisor.EngineState.Running);

        var status = sup.Status();
        Assert.Equal(ForagerSupervisor.EngineState.Running, status.State);
        Assert.Equal("http://127.0.0.1:8801", status.Endpoint);
        Assert.Equal("fgi_alpha", status.InstanceId);
        Assert.Equal("0.7.0", status.Version);
        Assert.Equal("http://127.0.0.1:8801", sup.ManagedEndpoint);
        Assert.StartsWith("fgr_", sup.ManagedSecret); // the module gets the minted credential

        await sup.StopAsync();
    }

    [Fact]
    public async ThreadingTask ManagedButNotYetRunning_ExposesNoCredential()
    {
        // While starting/failed the module must get an empty secret so it reports "unavailable"
        // rather than 401ing against a half-up engine.
        var h = new Harness();
        h.Enqueue(FakeProcess.Crashes(exitCode: 2, stderr: "bad config")); // refuses immediately
        var sup = Build(h, Managed());

        _ = sup.StartAsync();
        await WaitFor(sup, s => s == ForagerSupervisor.EngineState.Failed);

        Assert.Equal("", sup.ManagedEndpoint);
        Assert.Equal("", sup.ManagedSecret);
    }

    [Fact]
    public async ThreadingTask ConfigRefusal_Exit2_FailsWithoutRestarting()
    {
        var h = new Harness();
        h.Enqueue(FakeProcess.Crashes(exitCode: 2, stderr: "FORAGER_ALLOWED_HOSTS must be set"));
        var sup = Build(h, Managed());

        _ = sup.StartAsync();
        await WaitFor(sup, s => s == ForagerSupervisor.EngineState.Failed);

        Assert.Equal(1, h.StartCalls); // a config refusal is not retried
        Assert.Contains("refused its configuration", sup.Status().Reason!);
    }

    [Fact]
    public async ThreadingTask BusyStore_Exit11_UnownedInstance_Refuses()
    {
        var h = new Harness();
        h.Enqueue(FakeProcess.Crashes(exitCode: 11,
            stderr: "already open by another Forager (pid 4242 on box, standalone mode); instance fgi_someoneelse."));
        var sup = Build(h, Managed()); // fresh state dir -> no recorded instance

        _ = sup.StartAsync();
        await WaitFor(sup, s => s == ForagerSupervisor.EngineState.Failed);

        Assert.Equal(1, h.StartCalls);
        Assert.Contains("does not own", sup.Status().Reason!);
        Assert.Contains("fgi_someoneelse", sup.Status().Reason!);
    }

    [Fact]
    public async ThreadingTask HostCredentialMissing_Refuses()
    {
        // The engine came up but did not read our credential file — provisioned=false on /ready.
        var h = new Harness { Identity = Good("fgi_alpha") with { HostCredential = false } };
        h.Enqueue(FakeProcess.Listening(8802));
        var sup = Build(h, Managed());

        _ = sup.StartAsync();
        await WaitFor(sup, s => s == ForagerSupervisor.EngineState.Failed);

        Assert.Contains("host credential", sup.Status().Reason!);
    }

    [Fact]
    public async ThreadingTask VersionBelowFloor_Refuses()
    {
        var h = new Harness { Identity = Good("fgi_alpha") with { Version = "0.6.9" } };
        h.Enqueue(FakeProcess.Listening(8803));
        var sup = Build(h, Managed());

        _ = sup.StartAsync();
        await WaitFor(sup, s => s == ForagerSupervisor.EngineState.Failed);

        Assert.Contains("managed mode needs", sup.Status().Reason!);
    }

    [Fact]
    public async ThreadingTask FirstRun_RecordsInstance_ThenADifferentInstanceIsRefused()
    {
        var state = TempDir();
        var data = TempDir();

        // First run: records fgi_first.
        var h1 = new Harness { Identity = Good("fgi_first") };
        h1.Enqueue(FakeProcess.Listening(8804));
        var sup1 = new ForagerSupervisor(() => Managed(dataDir: data), state, launcher: h1, prober: h1, policy: Fast);
        _ = sup1.StartAsync();
        await WaitFor(sup1, s => s == ForagerSupervisor.EngineState.Running);
        await sup1.StopAsync();
        Assert.True(File.Exists(Path.Combine(state, "forager", "install.json")));

        // Second run over the SAME state dir: a different instance answers -> refused.
        var h2 = new Harness { Identity = Good("fgi_second") };
        h2.Enqueue(FakeProcess.Listening(8805));
        var sup2 = new ForagerSupervisor(() => Managed(dataDir: data), state, launcher: h2, prober: h2, policy: Fast);
        _ = sup2.StartAsync();
        await WaitFor(sup2, s => s == ForagerSupervisor.EngineState.Failed);
        Assert.Contains("not the one recorded", sup2.Status().Reason!);
        await sup2.StopAsync();
    }

    [Fact]
    public async ThreadingTask CrashLoop_StopsAtTheCeiling()
    {
        var h = new Harness();
        for (var i = 0; i < 10; i++) h.Enqueue(FakeProcess.Crashes(exitCode: 1, stderr: "boom"));
        var sup = Build(h, Managed());

        _ = sup.StartAsync();
        await WaitFor(sup, s => s == ForagerSupervisor.EngineState.Failed, ms: 5000);

        // CeilingCount is 3, so the 4th restart in the window is the one that gives up.
        Assert.True(h.StartCalls >= 4, $"expected at least 4 starts before the ceiling, saw {h.StartCalls}");
        Assert.Contains("restarted more than", sup.Status().Reason!);
    }

    [Fact]
    public async ThreadingTask Crash_ThenRecovers_ReachesRunning()
    {
        var h = new Harness { Identity = Good("fgi_alpha") };
        h.Enqueue(FakeProcess.Crashes(exitCode: 1, stderr: "transient"));
        h.Enqueue(FakeProcess.Listening(8806));
        var sup = Build(h, Managed());

        _ = sup.StartAsync();
        await WaitFor(sup, s => s == ForagerSupervisor.EngineState.Running, ms: 5000);

        Assert.Equal(ForagerSupervisor.EngineState.Running, sup.Status().State);
        Assert.True(h.StartCalls >= 2);
        await sup.StopAsync();
    }

    [Fact]
    public async ThreadingTask Stop_AsksTheEngineToDrain_ThenGoesDisabled()
    {
        var h = new Harness { Identity = Good("fgi_alpha") };
        h.Enqueue(FakeProcess.Listening(8807));
        var sup = Build(h, Managed());

        _ = sup.StartAsync();
        await WaitFor(sup, s => s == ForagerSupervisor.EngineState.Running);

        await sup.StopAsync();

        Assert.True(h.ShutdownCalls >= 1); // POST /api/shutdown was attempted
        Assert.Equal(ForagerSupervisor.EngineState.Disabled, sup.Status().State);
    }

    // ---------------------------------------------------------------- helpers + fakes

    private ForagerSupervisor Build(Harness h, KnowledgeSettings settings) =>
        new(() => settings, TempDir(), launcher: h, prober: h, policy: Fast);

    private static KnowledgeSettings Managed(string entry = "server.mjs", string dataDir = "") =>
        new() { Managed = true, EntryPath = entry, DataDir = dataDir };

    private static EngineIdentity Good(string id) =>
        new() { InstanceId = id, Generation = "gen_1", Version = "0.7.0", HostCredential = true, Database = "ok" };

    private static async ThreadingTask WaitFor(ForagerSupervisor sup, Func<ForagerSupervisor.EngineState, bool> pred, int ms = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (pred(sup.Status().State)) return;
            await ThreadingTask.Delay(10);
        }
        // Fall through: the assertion in the test reports the actual state.
    }

    /// <summary>Both seams in one object so a shutdown request can retire the running fake process.</summary>
    private sealed class Harness : IEngineLauncher, IEngineProber
    {
        private readonly Queue<FakeProcess> _queue = new();
        public FakeProcess? Last;
        public EngineIdentity Identity = new() { InstanceId = "fgi_default", Version = "0.7.0", HostCredential = true, Database = "ok" };
        public int StartCalls;
        public int ShutdownCalls;

        public void Enqueue(FakeProcess p) => _queue.Enqueue(p);

        public IEngineProcess Start(EngineSpec spec)
        {
            StartCalls++;
            var p = _queue.Count > 0 ? _queue.Dequeue() : FakeProcess.Listening(9999);
            Last = p;
            return p;
        }

        public Task<EngineIdentity> ReadIdentityAsync(string endpoint, string secret, CancellationToken token)
            => ThreadingTask.FromResult(Identity);

        public Task<bool> RequestShutdownAsync(string endpoint, string secret, CancellationToken token)
        {
            ShutdownCalls++;
            Last?.SignalExit(0); // a real engine drains and exits; the fake exits at once
            return ThreadingTask.FromResult(true);
        }
    }

    private sealed class FakeProcess : IEngineProcess
    {
        private readonly int? _port;
        private readonly string _stderr;
        private int _exitCode;
        private volatile bool _exited;
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private FakeProcess(int? port, int exitCode, string stderr)
        {
            _port = port;
            _exitCode = exitCode;
            _stderr = stderr;
            if (port is null) { _exited = true; _exit.TrySetResult(); }
        }

        public static FakeProcess Listening(int port) => new(port, 0, "");
        public static FakeProcess Crashes(int exitCode, string stderr) => new(null, exitCode, stderr);

        public Task<int?> WaitForListeningAsync(TimeSpan timeout, CancellationToken token)
            => ThreadingTask.FromResult(_exited ? (int?)null : _port);

        public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken token)
        {
            if (_exited) return true;
            try
            {
                if (timeout == Timeout.InfiniteTimeSpan) await _exit.Task.WaitAsync(token).ConfigureAwait(false);
                else await _exit.Task.WaitAsync(timeout, token).ConfigureAwait(false);
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
            return _exited;
        }

        public bool HasExited => _exited;
        public int ExitCode => _exitCode;
        public void CloseStdin() => SignalExit(0);
        public void Kill() => SignalExit(137);
        public void SignalExit(int code) { if (_exited) return; _exitCode = code; _exited = true; _exit.TrySetResult(); }
        public string DrainStderr() => _stderr;
        public void Dispose() { }
    }
}
