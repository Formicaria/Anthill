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

        // v0.3.8.52, the Windows field report ("all installers must work"): CreateProcess's own
        // PATH search only ever appends .exe — and on Windows npm IS npm.cmd, and so is every
        // npm-installed agent. Handing the bare name to the OS therefore reported "npm is not
        // installed" on a machine with a working Node, which is the exact wrong sentence. Walk
        // PATH ourselves with the Windows candidate extensions so a .cmd is FOUND; how a .cmd is
        // then STARTED is Invocation()'s problem, one mechanism below.
        if (OperatingSystem.IsWindows())
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var candidate in Candidates(binary))
                {
                    try
                    {
                        var full = Path.Combine(dir.Trim(), candidate);
                        if (File.Exists(full)) return full;
                    }
                    catch { /* an unreadable PATH entry is not a reason to fail the lookup */ }
                }
            }
        }
        return binary;
    }

    /// <summary>On Windows the executable carries an extension; npm ships .cmd shims.</summary>
    private static IEnumerable<string> Candidates(string binary) =>
        OperatingSystem.IsWindows()
            ? new[] { binary + ".cmd", binary + ".exe", binary + ".bat", binary }
            : new[] { binary };

    // ---- v0.3.8.52: starting what Resolve found, on Windows ------------------------------------
    //
    // CreateProcess cannot start a .cmd — cmd.exe has to interpret it. But putting cmd.exe in
    // front of OPERATOR TEXT would reopen the exact command-injection hole this file's Run()
    // comment forbids: inside a cmd line, %VAR% expands and ^ & | " all mean things, even inside
    // quotes. So the two cases are split by what the arguments ARE:
    //
    //   • An npm cmd-shim (claude.cmd, codex.cmd…) is one known line of batch whose only job is
    //     `node <its .js> %*`. The shim is READ and the .js target extracted, and node runs it
    //     directly with a discrete argv — the prompt path stays shell-free on every OS.
    //   • Anything else (npm.cmd itself, driven only by this repository's constant install/probe
    //     vectors) may ride through cmd.exe, but only after every argument passes ShellSafeArg —
    //     an argument cmd would interpret is REFUSED, not escaped, because the callers that could
    //     ever carry operator text are the ones the shim rewrite already diverted.

    /// <summary>The file name and argv to actually start, plus the raw cmd line when cmd.exe must
    /// interpret (ArgumentList's backslash-escaping of quotes is C runtime grammar, not cmd's, so
    /// the cmd.exe form has to bypass it).</summary>
    internal sealed record Invocation(string FileName, IReadOnlyList<string> Args, string? RawCmdLine = null);

    internal static Invocation BuildInvocation(string binary, IReadOnlyList<string> args)
    {
        var resolved = Resolve(binary);
        if (!OperatingSystem.IsWindows()) return new Invocation(resolved, args);

        var ext = Path.GetExtension(resolved);
        var isBatch = ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);
        if (!isBatch) return new Invocation(resolved, args);

        var script = NpmShimTarget(resolved);
        if (script is not null)
            return new Invocation(Resolve("node"), new[] { script }.Concat(args).ToList());

        foreach (var a in args)
            if (!ShellSafeArg(a))
                return new Invocation("", Array.Empty<string>(),
                    RawCmdLine: $"REFUSED:{Path.GetFileName(resolved)}");  // sentinel; Run reports honestly

        var line = string.Join(' ',
            new[] { Quote(resolved) }.Concat(args.Select(Quote)));
        return new Invocation("cmd.exe", Array.Empty<string>(), RawCmdLine: "/d /s /c \"" + line + "\"");

        static string Quote(string s) => s.Length > 0 && !s.Any(char.IsWhiteSpace) ? s : "\"" + s + "\"";
    }

    /// <summary>
    /// True when cmd.exe cannot possibly interpret the argument: no quotes to escape from, no
    /// %expansion%, no metacharacters, no line breaks. Deliberately a DENY of the dangerous set
    /// rather than an allow-list of ASCII — install prefixes live under the operator's profile,
    /// and home directories carry accents and spaces as a matter of course.
    /// </summary>
    internal static bool ShellSafeArg(string a) =>
        !a.Any(c => c is '"' or '%' or '^' or '&' or '|' or '<' or '>' or '!' or '`' or '\r' or '\n');

    /// <summary>
    /// The .js a Windows npm cmd-shim runs, or null when the file is not one. Every npm shim since
    /// cmd-shim@3 contains a `"%dp0%\<relative>.js"` (or `%~dp0`) token pointing into node_modules;
    /// the extraction is anchored to that shape plus the target actually existing on disk, so an
    /// arbitrary .cmd an operator wrote can never be misread as one.
    /// </summary>
    internal static string? NpmShimTarget(string cmdPath)
    {
        try
        {
            var rel = NpmShimRelativeTarget(File.ReadAllText(cmdPath));
            if (rel is null) return null;
            var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cmdPath) ?? ".", rel));
            return File.Exists(full) ? full : null;
        }
        catch { return null; }
    }

    /// <summary>The parsing half of <see cref="NpmShimTarget"/>, pure so the suite can check the
    /// extraction on every OS — the file-existence half is Windows-shaped by nature.</summary>
    internal static string? NpmShimRelativeTarget(string shimText)
    {
        if (!shimText.Contains("node", StringComparison.OrdinalIgnoreCase)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(shimText,
            @"%~?dp0%?\\(?<rel>[^""\r\n]+?\.(?:js|cjs|mjs))""");
        return m.Success ? m.Groups["rel"].Value : null;
    }

    private static ProcessStartInfo BuildPsi(
        string binary, IReadOnlyList<string> args, string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment, out string? refused)
    {
        var inv = BuildInvocation(binary, args);
        refused = inv.RawCmdLine?.StartsWith("REFUSED:", StringComparison.Ordinal) == true
            ? inv.RawCmdLine["REFUSED:".Length..] : null;

        var psi = new ProcessStartInfo
        {
            FileName = inv.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (inv.RawCmdLine is not null && refused is null) psi.Arguments = inv.RawCmdLine;
        else foreach (var a in inv.Args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        if (environment is not null)
            foreach (var (k, v) in environment) psi.Environment[k] = v;
        return psi;
    }

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
        // v0.3.8.41: resolve against Anthill's own bin directories before falling back to PATH.
        // Agents are installed into ~/.anthill/agents rather than a root-owned global prefix,
        // so they are deliberately NOT on the operator's PATH — without this they would install
        // successfully and then be reported as missing, which is the worst of both.
        // v0.3.8.52: BuildPsi additionally translates Windows .cmd shims into something
        // CreateProcess can start — see BuildInvocation for the two cases and the injection rule.
        var psi = BuildPsi(binary, args, workingDirectory, environment, out var refused);
        if (refused is not null)
            return (false, "", $"Refusing to run {refused} through cmd.exe with an argument cmd would interpret.", -1);

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

    /// <summary>
    /// v0.3.8.47 — the same run, with stdout delivered line by line as the agent produces it.
    /// Lines rather than characters because that is what a pipe actually delivers from a child
    /// process; pretending to a finer grain would be a fake trickle over real chunks. The
    /// cancellation token (the caller passes ModelCallScope's) KILLS the process — an operator's
    /// stop must reach the agent, not merely the reader. Stderr is still drained concurrently and
    /// returned whole: it is diagnosis, not answer, and does not stream to the operator.
    /// </summary>
    internal static (bool Started, string Stdout, string Stderr, int ExitCode) RunStreaming(
        string binary, IReadOnlyList<string> args, TimeSpan timeout, Action<string> onLine,
        CancellationToken cancel, string? workingDirectory = null)
    {
        // Same Windows .cmd translation as Run — the streaming path carries the PROMPT, so it is
        // precisely the path the npm-shim rewrite exists for (node + argv, never cmd.exe).
        var psi = BuildPsi(binary, args, workingDirectory, environment: null, out var refused);
        if (refused is not null)
            return (false, "", $"Refusing to run {refused} through cmd.exe with an argument cmd would interpret.", -1);

        using var p = new Process { StartInfo = psi };

        try { if (!p.Start()) return (false, "", "", -1); }
        catch (System.ComponentModel.Win32Exception) { return (false, "", "", -1); }
        catch (System.IO.FileNotFoundException) { return (false, "", "", -1); }

        using var reg = cancel.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });

        var collected = new System.Text.StringBuilder();
        var stderr = p.StandardError.ReadToEndAsync();
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            collected.AppendLine(line);
            try { onLine(line + "\n"); } catch { /* a broken sink must not kill the agent's run */ }
        }

        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (true, collected.ToString(), $"timed out after {timeout.TotalSeconds:0}s", -1);
        }
        if (cancel.IsCancellationRequested)
            return (true, collected.ToString(), "cancelled by the operator", -1);

        return (true, collected.ToString(), stderr.GetAwaiter().GetResult(), p.ExitCode);
    }
}
