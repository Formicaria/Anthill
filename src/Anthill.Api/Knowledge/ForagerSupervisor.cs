using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthill.Core.Common;
using Anthill.Core.Configuration;

namespace Anthill.Api;

/// <summary>
/// THE HOST HALF OF MANAGED ENGINE SUPERVISION — W3-03.
///
/// The engine half lives in FORAGER and is done: it takes a host credential from a file, reports an
/// instance identity and a bound port on startup, refuses to be a second writer, and shuts down on
/// a request or on the end of its stdin. This class is the other side of that contract — the thing
/// that starts the engine, verifies it is the one it started, keeps it alive within a ceiling, and
/// stops it cleanly — and until it exists "managed mode" is a set of environment variables nothing
/// sets.
///
/// WHAT THIS IS NOT. It is not a general process manager and it is not in the module. It lives in
/// Anthill.Api beside <c>AutoApplyRunner</c> and <c>OperatorShell</c>, the other two places the
/// colony starts a child process, because supervising a bundled engine is a composition-root
/// concern: it mints a credential, writes a file, and holds a <see cref="Process"/> — none of which
/// a module may do (a module references only the SDK). The knowledge MODULE still speaks HTTP to the
/// engine exactly as it does in attached mode; this only decides, in managed mode, which endpoint
/// and which credential it speaks with.
///
/// THREE PROPERTIES IT KEEPS, and each is a defect it prevents:
///   1. AN UNREACHABLE ENGINE NEVER STOPS THE COLONY BOOTING. The start runs on a background task
///      and every failure is a state, never a throw across the boot path. Knowledge reports
///      "starting" or "unavailable" until the engine is up, the same as an attached engine that is
///      not running yet. This is the whole reason <see cref="KnowledgeModule.Register"/> does no I/O.
///   2. NEVER TWO WRITERS ON ONE STORE. The engine's own lock enforces it (exit 11); the host reads
///      that exit and the identity on the log line and decides attach-or-refuse. It does not take
///      over an instance it did not record as its own.
///   3. A CRASH-LOOPING ENGINE DOES NOT SPIN FOREVER. Restarts back off and stop at a ceiling, and
///      hitting the ceiling is a state an operator is told about, not a silent give-up.
///
/// TESTABLE WITHOUT NODE. Everything that talks to the outside world is behind two seams — a
/// <see cref="IEngineLauncher"/> that starts a process and a <see cref="IEngineProber"/> that makes
/// the HTTP calls — so the state machine, the version gate, the credential shape, the exit-code
/// reading and the backoff schedule are all exercised against fakes. The real seams
/// (<see cref="NodeEngineLauncher"/>, <see cref="HttpEngineProber"/>) are thin by design: a bug in
/// them is a bug the integration test on a real engine catches, a bug above them is a bug a unit
/// test catches, and the line between the two is this interface.
/// </summary>
public sealed class ForagerSupervisor : IDisposable
{
    /// <summary>Where a managed engine is in its lifecycle. Reported on <c>/knowledge/engine</c>.</summary>
    public enum EngineState
    {
        /// <summary>Managed mode is off, or the supervisor has not been asked to start.</summary>
        Disabled,

        /// <summary>Starting: the process is up, or about to be, and has not reported ready yet.</summary>
        Starting,

        /// <summary>Running and verified: identity matches, version is in range, the store answers.</summary>
        Running,

        /// <summary>Died and is waiting out a backoff before the next start.</summary>
        Backoff,

        /// <summary>Stopped at the restart ceiling, or by a refusal that a restart cannot fix.
        /// Terminal until an operator acts — the state that must be visible rather than a quiet stop.</summary>
        Failed,

        /// <summary>Asked to stop and stopping. Set by <see cref="StopAsync"/>.</summary>
        Stopping,
    }

    /// <summary>
    /// The restart policy. The defaults are the numbers W3-03 §8 PROPOSED and nobody has ratified —
    /// 1s doubling to 30s, five restarts inside ten minutes stops it, a 30s wait for the listening
    /// line — read from the environment (ANTHILL_KNOWLEDGE_FORAGER_RESTART_*) so a deployment tunes
    /// them without a config-catalog key. It is a policy the release owns, not a per-install
    /// preference; W2-06's release manifest will supply it. Injectable so a test runs the loop in
    /// milliseconds rather than minutes.
    /// </summary>
    public sealed record RestartPolicy
    {
        public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(1);
        public TimeSpan BackoffCap { get; init; } = TimeSpan.FromSeconds(30);
        public int CeilingCount { get; init; } = 5;
        public TimeSpan CeilingWindow { get; init; } = TimeSpan.FromMinutes(10);
        public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>The shipping policy, with the environment overrides applied.</summary>
        public static RestartPolicy Default => new()
        {
            BackoffBase = EnvSpan("ANTHILL_KNOWLEDGE_FORAGER_RESTART_BASE_MS", 1_000),
            BackoffCap = EnvSpan("ANTHILL_KNOWLEDGE_FORAGER_RESTART_CAP_MS", 30_000),
            CeilingCount = EnvInt("ANTHILL_KNOWLEDGE_FORAGER_RESTART_CEILING", 5),
            CeilingWindow = EnvSpan("ANTHILL_KNOWLEDGE_FORAGER_RESTART_WINDOW_MS", 600_000),
            StartupTimeout = EnvSpan("ANTHILL_KNOWLEDGE_FORAGER_STARTUP_TIMEOUT_MS", 30_000),
        };
    }

    // The version window, checked against what the engine DECLARES. Managed mode is stricter than
    // attached: the host bundled this engine, so an engine below the floor means the host is starting
    // the wrong binary — a deploy error, refused clearly rather than started and failed strangely
    // later (W3-03 item 6). The floor is 0.7.0, the first FORAGER that authenticates every route,
    // reports an instance identity, and honours a host credential and a shutdown request — i.e. the
    // first one this contract can manage at all. There is no hard ceiling yet: an engine at or above
    // the floor is used, and one ABOVE a known-good ceiling is used with a note, matching the
    // knowledge provider's additive rule (tolerate what you do not recognise, refuse what you cannot
    // speak to). W2-06's release manifest will replace this pair with an exact pinned version.
    public const string MinManagedEngineVersion = "0.7.0";

    private readonly Func<KnowledgeSettings> _settings;
    private readonly IEngineLauncher _launcher;
    private readonly IEngineProber _prober;
    private readonly RestartPolicy _policy;
    private readonly Action<string, string, IReadOnlyDictionary<string, object?>> _log;
    private readonly Action<string> _console;
    private readonly string _stateDir;
    private readonly string _installRecordPath;

    private readonly object _gate = new();
    private readonly List<DateTime> _restarts = new();
    private CancellationTokenSource? _life;
    private IEngineProcess? _process;

    // Live facts a caller reads without taking the process down with it. Guarded by _gate.
    private EngineState _state = EngineState.Disabled;
    private string _endpoint = "";
    private string _secret = "";
    private string? _instanceId;
    private string? _generation;
    private string? _version;
    private string? _reason;
    private DateTime? _readySince;

    public ForagerSupervisor(
        Func<KnowledgeSettings> settings,
        string colonyStateDir,
        Action<string, string, IReadOnlyDictionary<string, object?>>? log = null,
        Action<string>? console = null,
        IEngineLauncher? launcher = null,
        IEngineProber? prober = null,
        RestartPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _log = log ?? ((_, _, _) => { });
        _console = console ?? (_ => { });
        _launcher = launcher ?? new NodeEngineLauncher();
        _prober = prober ?? new HttpEngineProber();
        _policy = policy ?? RestartPolicy.Default;
        _stateDir = colonyStateDir;
        // The install record lives in the HOST's state directory, never in the engine's data
        // directory — the whole point is that the host owns this fact independently of the store it
        // is verifying. `.anthill/forager/install.json`.
        _installRecordPath = Path.Combine(colonyStateDir, "forager", "install.json");
    }

    /// <summary>What the engine is doing right now, for <c>/knowledge/engine</c>. A snapshot under
    /// the lock; never a live handle to the process.</summary>
    public EngineStatus Status()
    {
        lock (_gate)
        {
            return new EngineStatus
            {
                Managed = _state != EngineState.Disabled,
                State = _state,
                Endpoint = _endpoint,
                InstanceId = _instanceId,
                Generation = _generation,
                Version = _version,
                Reason = _reason,
                ReadySince = _readySince,
                RestartsInWindow = RestartsInWindow(DateTime.UtcNow),
            };
        }
    }

    /// <summary>The endpoint the knowledge module should use, or empty when there is no managed
    /// engine to talk to yet. Read live by <c>InitKnowledge</c>'s options delegate.</summary>
    public string ManagedEndpoint { get { lock (_gate) return _state == EngineState.Running ? _endpoint : ""; } }

    /// <summary>The credential the knowledge module should present, or empty until the engine is
    /// running. Empty is correct while starting: the module gets a NullKnowledgeProvider reason
    /// rather than a token that would 401.</summary>
    public string ManagedSecret { get { lock (_gate) return _state == EngineState.Running ? _secret : ""; } }

    /// <summary>Whether managed mode is on at all. Cheap; reads settings, takes no lock.</summary>
    public bool IsManaged => _settings().Managed;

    /// <summary>
    /// Start supervising, if managed mode is on. Returns immediately; the work runs on a background
    /// task so a slow or failing engine never delays boot. Calling it when not managed, or twice, is
    /// a no-op. The returned task completes when supervision STOPS, so a caller can await it on
    /// shutdown; nothing needs to await it to boot.
    /// </summary>
    public Task StartAsync()
    {
        var settings = _settings();
        if (!settings.Managed)
        {
            SetState(EngineState.Disabled, reason: null);
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            if (_life is not null) return Task.CompletedTask; // already supervising
            _life = new CancellationTokenSource();
        }
        var token = _life.Token;
        return Task.Run(() => SuperviseLoop(token), token);
    }

    /// <summary>
    /// Stop the engine and the supervision loop. Cancels the loop so no restart races the stop,
    /// then asks the engine to drain — <c>POST /api/shutdown</c> first, and closing its held stdin
    /// as the fallback that works when the API cannot be reached. Waits up to the drain budget for
    /// the process to exit before giving up on a clean stop. Idempotent.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? life;
        IEngineProcess? process;
        string endpoint, secret;
        lock (_gate)
        {
            life = _life;
            _life = null;
            process = _process;
            endpoint = _endpoint;
            secret = _secret;
            if (_state is not EngineState.Disabled) SetStateLocked(EngineState.Stopping, reason: "stop requested");
        }
        life?.Cancel();

        if (process is null)
        {
            // Nothing to drain: the loop had already stopped on its own (Failed) or never started.
            // Settle to Disabled rather than leaving the stop request parked in Stopping, which is a
            // state the route documents as transient.
            lock (_gate) { if (_state == EngineState.Stopping) SetStateLocked(EngineState.Disabled, reason: null); }
            return;
        }

        // The polite path: ask over the API and let the engine run its own drain. A 202 means it
        // accepted; anything else and we fall through to closing stdin, which the engine treats as
        // the supervisor having gone.
        var asked = false;
        if (endpoint.Length > 0 && secret.Length > 0)
        {
            try { asked = await _prober.RequestShutdownAsync(endpoint, secret, cancellationToken).ConfigureAwait(false); }
            catch { asked = false; }
        }
        if (!asked)
        {
            try { process.CloseStdin(); } catch { /* best effort */ }
        }

        var exited = await process.WaitForExitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (!exited)
        {
            // A managed engine that will not drain in time is killed rather than left holding the
            // store — startup's own recoverInterrupted is what makes that safe, and a store lock a
            // dead process leaves behind is reclaimed by the engine's same-host staleness check.
            try { process.Kill(); } catch { /* it may have exited between the wait and here */ }
        }
        lock (_gate) { _process = null; if (_state == EngineState.Stopping) SetStateLocked(EngineState.Disabled, reason: null); }
    }

    // ---------------------------------------------------------------- the loop

    private async Task SuperviseLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var settings = _settings();
            var config = BuildStartConfig(settings);
            if (config.Refusal is not null)
            {
                // A configuration a restart cannot fix — no entry path, an unreadable runtime.
                // Failed, named, and no retry: spinning on a missing file helps nobody.
                Fail(config.Refusal);
                return;
            }

            SetState(EngineState.Starting, reason: null);
            _console($"Knowledge engine (managed): starting {config.EntryPath}");

            EngineOutcome outcome;
            try { outcome = await RunOnce(config, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception e) { outcome = EngineOutcome.Crashed($"supervisor error: {e.Message}"); }

            if (token.IsCancellationRequested) return;

            // A refusal the engine stated (bad config, unowned store, incompatible version) is not
            // something a restart mends — stop and say why. A crash is, up to the ceiling.
            if (outcome.Kind == EngineOutcome.Result.Refused)
            {
                Fail(outcome.Reason);
                return;
            }

            RecordRestart(DateTime.UtcNow);
            if (RestartsInWindow(DateTime.UtcNow) > _policy.CeilingCount)
            {
                Fail($"the knowledge engine restarted more than {_policy.CeilingCount} times in "
                   + $"{_policy.CeilingWindow.TotalMinutes:0} minutes and has been left stopped ({outcome.Reason}). "
                   + "Start it manually or fix what is crashing it; it will not be restarted automatically.");
                return;
            }

            var delay = NextBackoff();
            SetState(EngineState.Backoff, reason: outcome.Reason);
            _log(Anthill.SDK.Events.EventTypes.KnowledgeEngineCrashed,
                $"Knowledge engine stopped ({outcome.Reason}); restarting in {delay.TotalSeconds:0}s.",
                new Dictionary<string, object?> { ["reason"] = outcome.Reason, ["restart_in_s"] = (int)delay.TotalSeconds, ["restarts_in_window"] = RestartsInWindow(DateTime.UtcNow) });
            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One life of the engine: start it, wait for the port, verify it, then watch until it exits.
    /// Returns why it ended. Never throws for an operational failure — that is the loop's contract.
    /// </summary>
    private async Task<EngineOutcome> RunOnce(StartConfig config, CancellationToken token)
    {
        IEngineProcess process;
        try { process = _launcher.Start(config.Spec); }
        catch (Exception e) { return EngineOutcome.Crashed($"could not start the engine process: {e.Message}"); }

        lock (_gate) { _process = process; }

        try
        {
            // Wait for either the "listening on http://host:port" line or an early exit. An engine
            // that exits before it listens told us why through its exit code; one that listens hands
            // us the port it actually bound (we asked for 0).
            var started = await process.WaitForListeningAsync(_policy.StartupTimeout, token).ConfigureAwait(false);
            if (started is null)
            {
                if (process.HasExited)
                    return InterpretExit(process.ExitCode, process.DrainStderr());
                // No line and no exit within the budget: hung. Kill and treat as a crash.
                try { process.Kill(); } catch { }
                return EngineOutcome.Crashed($"the engine did not report listening within {_policy.StartupTimeout.TotalSeconds:0}s");
            }

            var endpoint = $"http://127.0.0.1:{started.Value}";

            // VERIFY before anyone uses it (W3-03 items 2 and 6): identity we recorded for this
            // install, a version in range, and a store that answers with the host credential live.
            var verify = await Verify(endpoint, config.Secret, token).ConfigureAwait(false);
            if (verify is not null)
            {
                // A verification failure at the identity or version level is a refusal, not a crash:
                // restarting the same binary against the same directory produces the same answer.
                try { await _prober.RequestShutdownAsync(endpoint, config.Secret, token).ConfigureAwait(false); } catch { }
                try { process.CloseStdin(); } catch { }
                await process.WaitForExitAsync(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
                return EngineOutcome.Refused(verify);
            }

            MarkRunning(endpoint, config.Secret);
            _console($"Knowledge engine (managed): ready at {endpoint} — instance {_instanceId}, v{_version}");
            _log(Anthill.SDK.Events.EventTypes.KnowledgeEngineReady,
                $"Knowledge engine ready at {endpoint} (instance {_instanceId}, v{_version}).",
                new Dictionary<string, object?> { ["endpoint"] = endpoint, ["instance_id"] = _instanceId, ["version"] = _version, ["generation"] = _generation });

            // Watch until it exits. Nothing polls health here on a timer — the knowledge probe the
            // console already runs is the health check, and duplicating it would be a second source
            // of truth about whether the engine is up. We wake on the process ending.
            await process.WaitForExitAsync(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return EngineOutcome.Stopped();
            return InterpretExit(process.ExitCode, process.DrainStderr());
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_process, process)) _process = null; }
            process.Dispose();
        }
    }

    /// <summary>
    /// Read an exit code against FORAGER's documented contract (see its entrypoint):
    ///   0  clean shutdown — either we asked or a signal did.
    ///   2  configuration refusal — a restart will not fix it.
    ///   11 the store is open by another writer — attach if it is the instance we recorded, refuse
    ///      if it is not. A managed host that finds an engine it OWNS already up (an orphan from a
    ///      previous host that did not exit) should adopt it rather than fight it; one it does not
    ///      own it must not take over.
    ///   1  a startup error (credential file, migrations) — surface the message.
    /// </summary>
    private EngineOutcome InterpretExit(int code, string stderr)
    {
        var tail = Tail(stderr);
        return code switch
        {
            0 => EngineOutcome.Stopped(),
            2 => EngineOutcome.Refused($"the engine refused its configuration and exited (2): {tail}"),
            11 => InterpretStoreOpen(stderr, tail),
            1 => EngineOutcome.Crashed($"the engine failed to start (1): {tail}"),
            _ => EngineOutcome.Crashed($"the engine exited with code {code}: {tail}"),
        };
    }

    private EngineOutcome InterpretStoreOpen(string stderr, string tail)
    {
        // The engine's exit-11 log names the holder's pid and the store's instance id. If that
        // instance is the one we recorded for this install, an earlier engine of ours is still up
        // (a host that did not stop it cleanly) — adopt it: stop trying to start a second and let
        // the running one serve. If we recorded a DIFFERENT id, or none, this store belongs to
        // something we did not start and we refuse to take it over.
        var recorded = ReadInstallRecord()?.InstanceId;
        var seen = ExtractInstanceId(stderr);
        if (recorded is not null && seen is not null && string.Equals(recorded, seen, StringComparison.Ordinal))
            return EngineOutcome.Refused(
                $"the knowledge engine for this install (instance {seen}) is already running under another host process; "
              + "not starting a second. Stop the other host, or point this one at a different data directory.");
        return EngineOutcome.Refused(
            "the configured data directory is already open by a knowledge engine this host did not start"
          + (seen is not null ? $" (instance {seen})" : "")
          + ". Refusing to take over a store this install does not own; choose a data directory this colony owns.");
    }

    /// <summary>
    /// Verify identity, version and readiness. Returns null when good, or the reason it is not.
    /// Records the install's instance id the first time, and refuses a different one thereafter —
    /// W3-03 item 2's "record it once, verify it every time".
    /// </summary>
    private async Task<string?> Verify(string endpoint, string secret, CancellationToken token)
    {
        EngineIdentity identity;
        try { identity = await _prober.ReadIdentityAsync(endpoint, secret, token).ConfigureAwait(false); }
        catch (Exception e) { return $"could not read the engine's identity: {e.Message}"; }

        if (string.IsNullOrEmpty(identity.InstanceId))
            return "the engine did not report an instance id; this build is too old to manage (needs FORAGER " + MinManagedEngineVersion + " or newer)";

        if (!identity.HostCredential)
            return "the engine came up without the host credential the supervisor provisioned; the credential file was not read as expected";

        if (identity.Database is not null && !string.Equals(identity.Database, "ok", StringComparison.OrdinalIgnoreCase))
            return $"the engine's store is not ready ({identity.Database})";

        var versionProblem = CheckVersion(identity.Version);
        if (versionProblem is not null) return versionProblem;

        // Identity pin against the install record. First sight records; a mismatch refuses.
        var record = ReadInstallRecord();
        if (record is null)
        {
            WriteInstallRecord(new InstallRecord(identity.InstanceId!, DateTime.UtcNow.ToString("o")));
        }
        else if (!string.Equals(record.InstanceId, identity.InstanceId, StringComparison.Ordinal))
        {
            return $"the engine's instance id ({identity.InstanceId}) is not the one recorded for this install "
                 + $"({record.InstanceId}); a different knowledge store is answering at this endpoint. "
                 + "Refusing to use it. If the store was deliberately replaced, remove " + _installRecordPath + ".";
        }

        lock (_gate)
        {
            _instanceId = identity.InstanceId;
            _generation = identity.Generation;
            _version = identity.Version;
        }
        return null;
    }

    /// <summary>
    /// Compare a declared version against the managed floor. Below the floor is refused; at or above
    /// is accepted (an unrecognised newer version is tolerated, per the additive rule). A version we
    /// cannot parse is accepted with the parse left to the release manifest — refusing on a string we
    /// do not understand would be stricter than the contract and would break on a legitimate
    /// pre-release tag. Public and static so a unit test can exercise the boundary directly.
    /// </summary>
    public static string? CheckVersion(string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared)) return null; // an engine that does not say is tolerated
        if (!TryParseVersion(declared, out var v)) return null;
        if (!TryParseVersion(MinManagedEngineVersion, out var floor)) return null;
        return Compare(v, floor) < 0
            ? $"the knowledge engine is version {declared}; managed mode needs {MinManagedEngineVersion} or newer "
              + "(the first that authenticates every route and honours a host credential). Bundle a newer engine."
            : null;
    }

    private StartConfig BuildStartConfig(KnowledgeSettings settings)
    {
        var entry = settings.EntryPath?.Trim() ?? "";
        if (entry.Length == 0)
            return StartConfig.Refuse("knowledge_forager_managed is on but knowledge_forager_entry_path is empty; "
                + "name the bundled FORAGER server entry (server.mjs) so the host knows what to start.");

        var runtime = settings.RuntimePath?.Trim() ?? "";
        if (runtime.Length == 0) runtime = OperatingSystem.IsWindows() ? "node.exe" : "node";

        var dataDir = settings.DataDir?.Trim() ?? "";
        if (dataDir.Length == 0)
            dataDir = Path.Combine(_stateDir, "forager-data");

        // The credential the host owns: fgr_ + url-safe random, matching FORAGER's HOST_SECRET
        // regex. Generated fresh per start — rotation is rewrite-and-restart, and an ephemeral
        // per-start secret is strictly better than one stored anywhere, because there is nothing at
        // rest to steal (W1-06 §9.3's sealed-in-database was for the attached-mode config token,
        // which the operator holds; a managed host holds nothing between starts).
        var secret = GenerateHostSecret();
        string credentialFile;
        try { credentialFile = WriteCredentialFile(dataDir, secret); }
        catch (Exception e) { return StartConfig.Refuse($"could not write the host credential file: {e.Message}"); }

        var env = new Dictionary<string, string>
        {
            ["FORAGER_DATA_DIR"] = dataDir,
            ["FORAGER_HOST"] = "127.0.0.1",
            ["FORAGER_PORT"] = "0", // learn the bound port from the startup line
            ["FORAGER_HOST_CREDENTIAL_FILE"] = credentialFile,
            ["FORAGER_SUPERVISED"] = "1", // stdin's end is our second off switch
        };

        return new StartConfig
        {
            Spec = new EngineSpec(runtime, entry, env),
            Secret = secret,
            EntryPath = entry,
            Refusal = null,
        };
    }

    // ---------------------------------------------------------------- credential + install record

    /// <summary>fgr_ followed by 43 url-safe characters (32 random bytes, base64url, no padding) —
    /// satisfies FORAGER's <c>/^fgr_[A-Za-z0-9_-]{32,}$/</c> with margin. Public and static so a
    /// test can assert the shape.</summary>
    public static string GenerateHostSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var b64 = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "fgr_" + b64;
    }

    private string WriteCredentialFile(string dataDir, string secret)
    {
        // Beside the engine's data directory, in a host-owned subfolder the engine only reads. The
        // engine re-registers it as tok_host at every start, so it is fine for it to be replaced
        // each time — and it must be, because the secret is new each start.
        var dir = Path.Combine(dataDir, ".host");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "credential");
        File.WriteAllText(file, secret);
        FileSecurity.HardenFilePermissions(file); // owner-only on POSIX; advisory on Windows
        return file;
    }

    private InstallRecord? ReadInstallRecord()
    {
        try
        {
            if (!File.Exists(_installRecordPath)) return null;
            return JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(_installRecordPath));
        }
        catch { return null; }
    }

    private void WriteInstallRecord(InstallRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_installRecordPath)!);
            File.WriteAllText(_installRecordPath, JsonSerializer.Serialize(record));
            FileSecurity.HardenFilePermissions(_installRecordPath);
        }
        catch { /* recording is best-effort; a lost record re-pins on the next start */ }
    }

    // ---------------------------------------------------------------- small state helpers

    private void MarkRunning(string endpoint, string secret)
    {
        lock (_gate)
        {
            _endpoint = endpoint;
            _secret = secret;
            _reason = null;
            _readySince = DateTime.UtcNow;
            SetStateLocked(EngineState.Running, reason: null);
        }
    }

    private void Fail(string? reason)
    {
        SetState(EngineState.Failed, reason);
        _console($"Knowledge engine (managed): not running — {reason}");
        _log(Anthill.SDK.Events.EventTypes.KnowledgeEngineFailed,
            $"Knowledge engine is not running: {reason}",
            new Dictionary<string, object?> { ["reason"] = reason });
    }

    private void SetState(EngineState state, string? reason)
    {
        lock (_gate) SetStateLocked(state, reason);
    }

    private void SetStateLocked(EngineState state, string? reason)
    {
        _state = state;
        _reason = reason;
        if (state != EngineState.Running) { _readySince = null; }
        if (state is EngineState.Disabled or EngineState.Failed or EngineState.Stopping)
        {
            _endpoint = state == EngineState.Stopping ? _endpoint : "";
            _secret = "";
        }
    }

    private void RecordRestart(DateTime at) { lock (_gate) { _restarts.Add(at); Trim(at); } }

    private int RestartsInWindow(DateTime now)
    {
        lock (_gate) { Trim(now); return _restarts.Count; }
    }

    private void Trim(DateTime now)
    {
        var cutoff = now - _policy.CeilingWindow;
        _restarts.RemoveAll(t => t < cutoff);
    }

    private TimeSpan NextBackoff()
    {
        // Doubling from base to cap, by how many restarts sit in the window. A jitter would be
        // over-engineering for a single local child; the cap is what bounds it.
        var n = Math.Max(0, RestartsInWindow(DateTime.UtcNow) - 1);
        var ms = _policy.BackoffBase.TotalMilliseconds * Math.Pow(2, n);
        return TimeSpan.FromMilliseconds(Math.Min(ms, _policy.BackoffCap.TotalMilliseconds));
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { /* disposing */ }
        _life?.Dispose();
    }

    // ---------------------------------------------------------------- version parse (no dependency)

    private static bool TryParseVersion(string s, out (int, int, int) v)
    {
        v = (0, 0, 0);
        var core = s.Trim().TrimStart('v');
        var dash = core.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) core = core[..dash];
        var parts = core.Split('.');
        if (parts.Length == 0) return false;
        int Read(int i) => i < parts.Length && int.TryParse(parts[i], out var n) ? n : 0;
        if (!int.TryParse(parts[0], out _)) return false;
        v = (Read(0), Read(1), Read(2));
        return true;
    }

    private static int Compare((int, int, int) a, (int, int, int) b)
    {
        if (a.Item1 != b.Item1) return a.Item1.CompareTo(b.Item1);
        if (a.Item2 != b.Item2) return a.Item2.CompareTo(b.Item2);
        return a.Item3.CompareTo(b.Item3);
    }

    private static string? ExtractInstanceId(string text)
    {
        // The engine logs "instance fgi_...." on its exit-11 lines. Pull the first fgi_ token.
        var idx = text.IndexOf("fgi_", StringComparison.Ordinal);
        if (idx < 0) return null;
        var end = idx;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '-')) end++;
        return text[idx..end];
    }

    private static string Tail(string s, int max = 400)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : s[^max..];
    }

    private static TimeSpan EnvSpan(string name, int defaultMs)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return TimeSpan.FromMilliseconds(v is not null && int.TryParse(v, out var n) && n > 0 ? n : defaultMs);
    }

    private static int EnvInt(string name, int fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return v is not null && int.TryParse(v, out var n) && n > 0 ? n : fallback;
    }

    // ---------------------------------------------------------------- records the seams trade in

    private sealed record InstallRecord(string InstanceId, string FirstSeen);

    private sealed class StartConfig
    {
        public EngineSpec Spec { get; init; } = null!;
        public string Secret { get; init; } = "";
        public string EntryPath { get; init; } = "";
        public string? Refusal { get; init; }
        public static StartConfig Refuse(string reason) => new() { Refusal = reason };
    }

    private readonly struct EngineOutcome
    {
        public enum Result { Stopped, Crashed, Refused }
        public Result Kind { get; }
        public string Reason { get; }
        private EngineOutcome(Result kind, string reason) { Kind = kind; Reason = reason; }
        public static EngineOutcome Stopped() => new(Result.Stopped, "stopped");
        public static EngineOutcome Crashed(string reason) => new(Result.Crashed, reason);
        public static EngineOutcome Refused(string reason) => new(Result.Refused, reason);
    }
}

/// <summary>What <c>/knowledge/engine</c> reports. A value snapshot, safe to serialize.</summary>
public sealed record EngineStatus
{
    public required bool Managed { get; init; }
    public required ForagerSupervisor.EngineState State { get; init; }
    public string Endpoint { get; init; } = "";
    public string? InstanceId { get; init; }
    public string? Generation { get; init; }
    public string? Version { get; init; }
    public string? Reason { get; init; }
    public DateTime? ReadySince { get; init; }
    public int RestartsInWindow { get; init; }
}

/// <summary>What the engine says about itself, read from /health and /ready. The prober fills it.</summary>
public sealed record EngineIdentity
{
    public string? InstanceId { get; init; }
    public string? Generation { get; init; }
    public string? Version { get; init; }
    public bool HostCredential { get; init; }
    public string? Database { get; init; }
}

/// <summary>How to start the engine: a runtime, an entry, and the environment it inherits on top of
/// the current process's. The launcher turns this into a real process.</summary>
public sealed record EngineSpec(string RuntimePath, string EntryPath, IReadOnlyDictionary<string, string> Env);

/// <summary>The process seam. The real one wraps <see cref="Process"/>; the test one is a fake.</summary>
public interface IEngineProcess : IDisposable
{
    /// <summary>Wait for the "listening on http://host:port" line and return the port, or null if
    /// the process exited or the timeout elapsed first.</summary>
    Task<int?> WaitForListeningAsync(TimeSpan timeout, CancellationToken token);
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken token);
    bool HasExited { get; }
    int ExitCode { get; }
    void CloseStdin();
    void Kill();
    string DrainStderr();
}

/// <summary>Starts an engine process from a spec. Seam so the supervisor is testable without node.</summary>
public interface IEngineLauncher
{
    IEngineProcess Start(EngineSpec spec);
}

/// <summary>The HTTP the supervisor makes to the engine it started. Seam for the same reason.</summary>
public interface IEngineProber
{
    Task<EngineIdentity> ReadIdentityAsync(string endpoint, string secret, CancellationToken token);
    /// <summary>POST /api/shutdown with the host credential. True when the engine accepted (202).</summary>
    Task<bool> RequestShutdownAsync(string endpoint, string secret, CancellationToken token);
}
