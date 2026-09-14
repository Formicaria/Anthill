using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anthill.Api;

/// <summary>
/// THE REAL PROCESS SEAM — starts a bundled FORAGER under Node and reads its stdio.
///
/// Kept thin on purpose (see <see cref="ForagerSupervisor"/>'s note on the seams): everything with a
/// decision in it lives above the interface and is unit-tested against a fake; this file only does
/// the parts that need a real OS process, which the integration test on a real engine covers. The
/// one piece of parsing here — the "listening on" line — is the single fact the host cannot get any
/// other way (it asked for port 0), so it is here rather than guessed.
/// </summary>
public sealed class NodeEngineLauncher : IEngineLauncher
{
    public IEngineProcess Start(EngineSpec spec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.RuntimePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // held open; closing it is the supervised off switch
            StandardOutputEncoding = Encoding.UTF8,   // v0.3.8.55: children emit UTF-8, not the OS codepage
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(spec.EntryPath);
        // Layer our variables on top of the inherited environment rather than replacing it — the
        // engine still needs PATH, and on Windows the system variables a spawned process expects.
        foreach (var (k, v) in spec.Env) psi.Environment[k] = v;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        return new NodeEngineProcess(process);
    }
}

/// <summary>One running engine. Reads stdout for the port, buffers stderr for a failure message,
/// and exposes the exit facts the supervisor reads its contract from.</summary>
internal sealed class NodeEngineProcess : IEngineProcess
{
    // The line FORAGER prints once it has bound: "... listening on http://127.0.0.1:8790". The host
    // asked for port 0, so this is where it learns the real one.
    private static readonly Regex Listening =
        new(@"listening on https?://[^:/\s]+:(?<port>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Process _process;
    private readonly TaskCompletionSource<int?> _listening =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _stderr = new();
    private readonly object _stderrGate = new();
    private volatile bool _started;

    public NodeEngineProcess(Process process)
    {
        _process = process;
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var m = Listening.Match(e.Data);
            if (m.Success && int.TryParse(m.Groups["port"].Value, out var port))
                _listening.TrySetResult(port);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderrGate) { if (_stderr.Length < 8192) _stderr.AppendLine(e.Data); }
        };
        _process.Start();
        _started = true;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public async Task<int?> WaitForListeningAsync(TimeSpan timeout, CancellationToken token)
    {
        // Whichever comes first: the line, the process ending, the timeout, or cancellation.
        var exit = _process.WaitForExitAsync(token);
        var delay = Task.Delay(timeout, token);
        var done = await Task.WhenAny(_listening.Task, exit, delay).ConfigureAwait(false);
        if (done == _listening.Task) return await _listening.Task.ConfigureAwait(false);
        return null; // exited before listening, or timed out
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken token)
    {
        if (_process.HasExited) return true;
        try
        {
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                await _process.WaitForExitAsync(token).ConfigureAwait(false);
                return true;
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return _process.HasExited;
        }
    }

    public bool HasExited => _started && _process.HasExited;
    public int ExitCode => _process.HasExited ? _process.ExitCode : -1;

    public void CloseStdin()
    {
        try { _process.StandardInput.Close(); } catch { /* already gone */ }
    }

    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
    }

    public string DrainStderr()
    {
        lock (_stderrGate) return _stderr.ToString();
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        _process.Dispose();
    }
}

/// <summary>
/// THE REAL HTTP SEAM — the calls the supervisor makes to the engine it owns, on loopback.
///
/// A second, tiny HTTP surface separate from the knowledge MODULE's <c>ForagerClient</c>, and that
/// duplication is deliberate: this one is the HOST talking to a process it started, before any
/// knowledge call, to verify identity and to ask for a drain. It reads only the public
/// <c>/api/ready</c> (which carries identity, version, host_credential and database state) and posts
/// the host-credential-only <c>/api/shutdown</c>. One <see cref="HttpClient"/> for the supervisor's
/// life, loopback only.
/// </summary>
public sealed class HttpEngineProber : IEngineProber
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AllowAutoRedirect = false,
    })
    { Timeout = TimeSpan.FromSeconds(5) };

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<EngineIdentity> ReadIdentityAsync(string endpoint, string secret, CancellationToken token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/api/ready");
        if (!string.IsNullOrEmpty(secret))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        // /ready answers 200 when the store is up and 503 when it is not, with the same body shape.
        // Either way the identity fields are present; we read them and let Database carry the state.
        var ready = JsonSerializer.Deserialize<ReadyBody>(body, Json) ?? new ReadyBody();
        return new EngineIdentity
        {
            InstanceId = ready.instance_id,
            Generation = ready.generation,
            Version = ready.version,
            HostCredential = ready.host_credential,
            Database = ready.database ?? (res.StatusCode == HttpStatusCode.OK ? "ok" : "error"),
        };
    }

    public async Task<bool> RequestShutdownAsync(string endpoint, string secret, CancellationToken token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.TrimEnd('/')}/api/shutdown");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req, token).ConfigureAwait(false);
        return res.StatusCode == HttpStatusCode.Accepted; // 202 = drain accepted
    }

    private sealed class ReadyBody
    {
        public string? status { get; set; }
        public string? version { get; set; }
        public string? instance_id { get; set; }
        public string? generation { get; set; }
        public bool host_credential { get; set; }
        public string? database { get; set; }
    }
}
