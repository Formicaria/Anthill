namespace Anthill.Modules.Reasoning;

/// <summary>Outcome of one install attempt. Typed, and never an exception across the boundary.</summary>
public sealed record AgentInstallResult(bool Ok, string Message, int ExitCode, string Output);

/// <summary>
/// Installs a catalogued agent into Anthill's own directory. v0.3.8.41.
///
/// THE FIX THIS FILE EXISTS FOR. The first version ran the catalogue's shell line verbatim, and
/// every npm one was `npm install -g`. On a normal Linux host npm's global prefix is /usr, whose
/// lib/node_modules is root-owned, so every install failed with EACCES for anyone not running
/// Anthill as root — and the honest options at that point were "run the console as root" or "give
/// up", neither of which is acceptable for a first-run experience.
///
/// Agents now install into <see cref="AgentHome"/>, a directory Anthill owns:
///
///   • no sudo, because nothing outside the user's own home is written
///   • no system mutation, so an Anthill install cannot break a system-wide toolchain
///   • uninstall is deleting a folder
///   • two Anthill installs cannot fight over one global node_modules
///
/// The trade is that agents installed here are not on the operator's PATH, which is why
/// <see cref="AgentCliDiscovery"/> searches this directory as well. The two are one mechanism and
/// changing either alone breaks it.
///
/// The PREREQUISITE is checked before anything runs. Without it, a host with no Node produced
/// "exit 127" and an operator had to know that meant npm was missing — a package manager that is
/// not installed is a different problem from an install that failed, and it needs a different
/// sentence.
/// </summary>
public static class AgentCliInstaller
{
    /// <summary>
    /// Installs are slow: a global npm install pulls a dependency tree and pip may build a wheel.
    /// Ten minutes is generous enough that a slow network does not look like a hang, and short
    /// enough that a stuck install eventually reports rather than pinning a request forever.
    /// </summary>
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Where Anthill keeps the agents it installed. Under the user's home rather than the workspace
    /// so it survives a workspace reset — reinstalling five CLI tools because a mission workspace
    /// was cleaned would be a poor trade.
    /// </summary>
    public static string AgentHome =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".anthill", "agents");

    /// <summary>
    /// Directories holding agent binaries, in search order. Exposed so discovery looks in the same
    /// places the installer writes to — the two halves of one mechanism.
    ///
    /// `~/.local/bin` is included because pip --user puts scripts there, and it is frequently absent
    /// from PATH on a fresh account, which is its own quiet source of "installed but not found".
    /// </summary>
    public static IReadOnlyList<string> BinDirectories() => new[]
    {
        Path.Combine(AgentHome, "bin"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
    };

    public static AgentInstallResult Install(AgentCli agent)
    {
        if (string.IsNullOrWhiteSpace(agent.PackageManager))
            return new AgentInstallResult(false,
                $"{agent.DisplayName} cannot be installed by Anthill. See {agent.DocsUrl}", -1, "");

        // Prerequisite FIRST, and named. "npm: command not found" surfaced as exit 127, which tells
        // an operator nothing about what to install or where to get it.
        var (mgrOk, mgrWhy) = PackageManagerAvailable(agent.PackageManager);
        if (!mgrOk) return new AgentInstallResult(false, mgrWhy, -1, "");

        try { Directory.CreateDirectory(Path.Combine(AgentHome, "bin")); }
        catch (Exception ex)
        {
            return new AgentInstallResult(false,
                $"Could not create Anthill's agent directory at {AgentHome}: {ex.Message}", -1, "");
        }

        var (binary, args) = InstallCommandFor(agent);
        var (started, stdout, stderr, exit) =
            AgentCliDiscovery.Run(binary, args, InstallTimeout, environment: InstallEnvironment());

        if (!started)
            return new AgentInstallResult(false, $"Could not start '{binary}'.", -1, "");

        var output = Combine(stdout, stderr);

        if (exit != 0)
        {
            // The TAIL, not the head. A failing package manager prints its diagnosis last, under
            // pages of resolution noise, and truncating from the front reliably keeps the part
            // nobody needs.
            var detail = Tail(output, 400);
            if (detail.Contains("EACCES", StringComparison.OrdinalIgnoreCase))
                detail += $"\n(Anthill installs into {AgentHome} and needs no elevated permission — "
                        + "this looks like a permission problem inside that directory.)";
            return new AgentInstallResult(false, detail, exit, Tail(output, 8000));
        }

        return new AgentInstallResult(true, $"{agent.DisplayName} installed.", 0, Tail(output, 8000));
    }

    /// <summary>
    /// Is the package manager present at all? Answered by running its version probe, because
    /// "on PATH" and "actually runnable" differ on a host with a broken Node install.
    /// </summary>
    private static (bool Ok, string Why) PackageManagerAvailable(string manager)
    {
        var (binary, args, install) = manager switch
        {
            "npm" => ("npm", new[] { "--version" },
                      "Install Node.js (which includes npm) from https://nodejs.org, or your package "
                    + "manager — for example: sudo apt install nodejs npm"),
            "pip" => (PythonBinary(), new[] { "-m", "pip", "--version" },
                      "Install Python 3 and pip — for example: sudo apt install python3 python3-pip"),
            _ => ("", Array.Empty<string>(), ""),
        };

        if (binary.Length == 0) return (false, $"Anthill does not know how to install with '{manager}'.");

        var (started, _, _, exit) = AgentCliDiscovery.Run(binary, args, ProbeTimeout);
        if (!started) return (false, $"{manager} is not installed on this machine. {install}");
        if (exit != 0) return (false, $"{manager} is installed but did not run. {install}");
        return (true, "");
    }

    private static string PythonBinary() => OperatingSystem.IsWindows() ? "python" : "python3";

    /// <summary>
    /// The install command, targeting Anthill's own directory.
    ///
    /// Built as an argument vector rather than a shell string. Nothing here is operator input — the
    /// package name is a constant from this repository — but the argv form removes the question
    /// entirely, and it is what lets the destination be a path with spaces in it, which a home
    /// directory on Windows routinely is.
    /// </summary>
    private static (string Binary, string[] Args) InstallCommandFor(AgentCli agent) => agent.PackageManager switch
    {
        // --prefix is what replaces `-g`'s root-owned destination with one the operator owns.
        "npm" => ("npm", new[] { "install", "-g", "--prefix", AgentHome, agent.Package }),
        // --user puts scripts in ~/.local/bin, which BinDirectories() searches for the same reason.
        "pip" => (PythonBinary(), new[] { "-m", "pip", "install", "--user", agent.Package }),
        _ => ("", Array.Empty<string>()),
    };

    /// <summary>
    /// npm resolves its own binaries relative to PATH during an install. Prepending Anthill's bin
    /// directory keeps a previously installed agent visible to a later install that depends on it.
    /// </summary>
    private static Dictionary<string, string> InstallEnvironment()
    {
        var sep = OperatingSystem.IsWindows() ? ";" : ":";
        var existing = Environment.GetEnvironmentVariable("PATH") ?? "";
        return new Dictionary<string, string>
        {
            ["PATH"] = string.Join(sep, BinDirectories()) + sep + existing,
        };
    }

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
