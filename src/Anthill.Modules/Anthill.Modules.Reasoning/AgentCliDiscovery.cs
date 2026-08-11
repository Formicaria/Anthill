using System.Diagnostics;

namespace Anthill.Modules.Reasoning;

/// <summary>What the host actually has, for one catalogued agent.</summary>
public sealed record AgentCliStatus
{
    public required AgentCli Agent { get; init; }

    /// <summary>Found on PATH and it answered its version probe.</summary>
    public required bool Installed { get; init; }

    /// <summary>Whatever the tool printed for its version, trimmed. Null when it is not installed.</summary>
    public string? Version { get; init; }

    /// <summary>
    /// Why it is not usable, in a sentence an operator can act on. Null when it is usable.
    ///
    /// Separate from <see cref="Installed"/> because "not installed" and "installed but it would
    /// not answer" need different instructions, and collapsing them prints the wrong one.
    /// </summary>
    public string? Unavailable { get; init; }
}

/// <summary>
/// Finds which catalogued agents are present on this host. v3.8.39.
///
/// Every method here does I/O and none of it may move into a factory or a provider constructor:
/// <see cref="Anthill.SDK.Reasoning.IReasoningProviderFactory"/> forbids I/O in Create precisely
/// because providers are built on the mission hot path, and probing five binaries there would put
/// five process launches in front of every keyed call.
///
/// Results are cached for <see cref="CacheFor"/>. An operator installing an agent expects the
/// console to notice within a reasonable time, and re-probing on every dashboard poll would
/// otherwise spawn processes continuously for as long as the console was open.
/// </summary>
public static class AgentCliDiscovery
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly object Gate = new();

    private static List<AgentCliStatus>? _cached;
    private static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>Status for every catalogued agent, cached. Never throws.</summary>
    public static IReadOnlyList<AgentCliStatus> Scan(bool force = false)
    {
        lock (Gate)
        {
            if (!force && _cached is not null && DateTime.UtcNow - _cachedAt < CacheFor) return _cached;
            _cached = AgentCliCatalog.All.Select(Probe).ToList();
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    /// <summary>Drop the cache, so the next Scan re-probes. Called after an install.</summary>
    public static void Invalidate()
    {
        lock (Gate) { _cached = null; _cachedAt = DateTime.MinValue; }
    }

    public static bool IsInstalled(string agentId) =>
        Scan().Any(s => s.Installed && string.Equals(s.Agent.Id, agentId, StringComparison.OrdinalIgnoreCase));

    private static AgentCliStatus Probe(AgentCli agent)
    {
        try
        {
            var (ok, stdout, stderr, exit) = Run(agent.Binary, agent.VersionArgs, ProbeTimeout);
            if (!ok)
            {
                return new AgentCliStatus
                {
                    Agent = agent,
                    Installed = false,
                    Unavailable = $"'{agent.Binary}' was not found on PATH or in {AgentCliInstaller.AgentHome}. "
                                + $"Install it from this page, or yourself with: {AgentCliCatalog.InstallHint(agent)}",
                };
            }

            // A non-zero exit from --version means the binary exists but is broken or half-installed
            // — a different problem from absence, and it needs a different sentence.
            if (exit != 0)
            {
                var why = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return new AgentCliStatus
                {
                    Agent = agent,
                    Installed = false,
                    Unavailable = $"'{agent.Binary}' is on PATH but did not report a version"
                                + (string.IsNullOrWhiteSpace(why) ? "." : $": {Trim(why)}"),
                };
            }

            return new AgentCliStatus { Agent = agent, Installed = true, Version = Trim(stdout) };
        }
        catch (Exception ex)
        {
            // Probing must never be able to take the colony down. An agent that cannot be asked is
            // reported as unavailable with the reason, exactly like one that is not installed.
            return new AgentCliStatus
            {
                Agent = agent,
                Installed = false,
                Unavailable = $"Could not probe '{agent.Binary}': {ex.Message}",
            };
        }
    }

    /// <summary>
    /// Where this binary actually lives.
    ///
    /// A bare name is looked for in Anthill's own bin directories first, then handed to the OS to
    /// resolve against PATH as before. A name that is already a path is returned untouched, so a
    /// caller that knows exactly what it wants is never second-guessed.
    ///
    /// This is the other half of installing outside the global prefix: the installer writes here,
    /// so discovery must look here. Changing one without the other silently breaks both.
    /// </summary>
    internal static string Resolve(string binary)
    {
        if (binary.Contains('/') || binary.Contains('\\')) return binary;

        foreach (var dir in AgentCliInstaller.BinDirectories())
        {
            foreach (var candidate in Candidates(binary))
            {
                try
                {
                    var full = Path.Combine(dir, candidate);
                    if (File.Exists(full)) return full;
                }
                catch { /* an unreadable directory is not a reason to fail the lookup */ }
            }
        }
        return binary;
    }

    /// <summary>On Windows the executable carries an extension; npm ships .cmd shims.</summary>
    private static IEnumerable<string> Candidates(string binary) =>
        OperatingSystem.IsWindows()
            ? new[] { binary + ".cmd", binary + ".exe", binary + ".bat", binary }
            : new[] { binary };

    private static string Trim(string s) =>
        s.Replace("\r", "", StringComparison.Ordinal).Split('\n').FirstOrDefault()?.Trim() ?? "";

    /// <summary>
    /// Start a process directly — never through a shell.
    ///
    /// <c>UseShellExecute = false</c> with a discrete argument list is what makes operator text
    /// safe to pass through: there is no shell to interpret a quote, a semicolon or a backtick, so
    /// a prompt cannot become a command. Building one string and handing it to /bin/sh would be
    /// the same feature with a command-injection hole in it.
    /// </summary>
    internal static (bool Started, string Stdout, string Stderr, int ExitCode) Run(
        string binary, IReadOnlyList<string> args, TimeSpan timeout, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo
        {
            // v0.3.8.41: resolve against Anthill's own bin directories before falling back to PATH.
            // Agents are installed into ~/.anthill/agents rather than a root-owned global prefix,
            // so they are deliberately NOT on the operator's PATH — without this they would install
            // successfully and then be reported as missing, which is the worst of both.
            FileName = Resolve(binary),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        if (environment is not null)
            foreach (var (k, v) in environment) psi.Environment[k] = v;

        using var p = new Process { StartInfo = psi };

        try { if (!p.Start()) return (false, "", "", -1); }
        catch (System.ComponentModel.Win32Exception) { return (false, "", "", -1); }  // not on PATH
        catch (System.IO.FileNotFoundException) { return (false, "", "", -1); }

        // Read both pipes concurrently. Draining one and then the other deadlocks the moment the
        // child fills the pipe it is not being read from, which for an agent writing a long answer
        // to stdout is the normal case rather than the rare one.
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();

        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (true, "", $"timed out after {timeout.TotalSeconds:0}s", -1);
        }

        return (true, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult(), p.ExitCode);
    }
}
