namespace Anthill.Modules.Reasoning;

/// <summary>Outcome of one install attempt. Typed, and never an exception across the boundary.</summary>
public sealed record AgentInstallResult(bool Ok, string Message, int ExitCode, string Output);

/// <summary>
/// Runs a catalogued agent's install command. v3.8.39.
///
/// THE COMMAND IS NEVER OPERATOR INPUT. It is looked up from <see cref="AgentCliCatalog"/> by id,
/// so there is no request shape and no field an operator can set that makes this run something
/// else. That is what makes running it through a shell defensible at all — and a shell IS needed
/// here, because the install commands are genuine shell lines ("pip install X &amp;&amp; X") rather
/// than a single binary with arguments.
///
/// The contrast with <see cref="AgentCliProvider"/> is the point. There, the payload is operator
/// prose and must never reach a shell, so it goes as a discrete argv vector. Here, the payload is
/// a fixed constant from this repository and the argv treatment would break it. Same file, two
/// rules, because the trust of the input differs — not because one path was written more carefully
/// than the other.
/// </summary>
public static class AgentCliInstaller
{
    /// <summary>
    /// Installs are slow: a global npm install pulls a dependency tree, and pip may build a wheel.
    /// Ten minutes is generous enough that a legitimately slow network does not look like a hang,
    /// and short enough that a genuinely stuck install eventually reports rather than pinning a
    /// request forever.
    /// </summary>
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    public static AgentInstallResult Install(AgentCli agent)
    {
        var (shell, args) = ShellFor(agent.InstallCommand);

        var (started, stdout, stderr, exit) =
            AgentCliDiscovery.Run(shell, args, InstallTimeout);

        if (!started)
            return new AgentInstallResult(false, $"Could not start '{shell}' to run the install.", -1, "");

        var output = Combine(stdout, stderr);

        if (exit != 0)
        {
            // The tail, not the head. A failing package manager prints its diagnosis last, under
            // pages of resolution noise, and truncating from the front would reliably keep the
            // part nobody needs.
            return new AgentInstallResult(false, Tail(output, 400), exit, Tail(output, 8000));
        }

        return new AgentInstallResult(true, $"{agent.DisplayName} installed.", 0, Tail(output, 8000));
    }

    /// <summary>
    /// The platform's shell. Windows hosts are a first-class target — the desktop deployment is
    /// most of the point — so this cannot assume /bin/sh.
    /// </summary>
    private static (string Shell, IReadOnlyList<string> Args) ShellFor(string command) =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", command })
            : ("/bin/sh", new[] { "-lc", command });

    private static string Combine(string stdout, string stderr) =>
        string.IsNullOrWhiteSpace(stderr) ? stdout
        : string.IsNullOrWhiteSpace(stdout) ? stderr
        : stdout + "\n" + stderr;

    private static string Tail(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : "…" + s[^max..];
    }
}
