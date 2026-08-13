namespace Anthill.Modules.Reasoning;

/// <summary>
/// The LOCAL runtime — Ollama — installable from the same page as the agent CLIs. v0.3.8.53.
///
/// The Windows field report's sentence was "this is the first thing people are going to do", and
/// the first thing a fresh Windows install could actually do was nothing: the agents page offered
/// five vendor CLIs that all need an account, and the local, no-account path (Ollama, which the
/// colony already knows how to route to) had no install story at all. This closes that gap.
///
/// Deliberately NOT an <see cref="AgentCli"/> catalogue entry: catalogue ids are reasoning
/// provider ids the router routes to, and Ollama is already a provider (`ollama`) with its own
/// configuration. Forcing it into the catalogue would have listed it twice and routed it wrong.
/// One shape difference, one class.
///
/// Install mechanics per platform:
///   • Windows — winget, the OS's own package manager: no account, silent, user scope,
///     Add/Remove-visible, upgradeable. The one prerequisite (winget itself) ships with Windows
///     10 1809+ and every Windows 11.
///   • Everywhere else — Anthill's Linux shapes (Docker, LXC) provision Ollama in deploy, and a
///     bare Linux host needs the vendor's script under root, which Anthill does not run by rule
///     (no sudo, ever — v0.3.8.41's installer lesson). The refusal names the exact command.
/// </summary>
public static class LocalRuntimeInstaller
{
    public const string DisplayName = "Ollama (local models)";
    public const string DocsUrl = "https://ollama.com/download";

    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Where the Windows installer puts ollama.exe. Probed directly because a just-installed
    /// Ollama updates PATH for FUTURE processes only — this one would keep saying "not installed"
    /// until Anthill restarted, right after telling the operator the install succeeded.
    /// </summary>
    private static string WindowsInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ollama");

    /// <summary>What the operator would run themselves, for display and refusals.</summary>
    public static string InstallHint =>
        OperatingSystem.IsWindows()
            ? "winget install --id Ollama.Ollama"
            : "curl -fsSL https://ollama.com/install.sh | sh   # the vendor's own script; needs root";

    public static (bool Installed, string? Version) Probe()
    {
        var binary = "ollama";
        if (OperatingSystem.IsWindows())
        {
            var direct = Path.Combine(WindowsInstallDir, "ollama.exe");
            if (File.Exists(direct)) binary = direct;
        }
        var (started, stdout, _, exit) =
            AgentCliDiscovery.Run(binary, new[] { "--version" }, ProbeTimeout);
        if (!started || exit != 0) return (false, null);
        var version = stdout.Replace("\r", "").Split('\n').FirstOrDefault()?.Trim();
        return (true, string.IsNullOrWhiteSpace(version) ? "installed" : version);
    }

    public static AgentInstallResult Install()
    {
        if (!OperatingSystem.IsWindows())
            return new AgentInstallResult(false,
                "On this platform Anthill does not install Ollama itself — the vendor's installer "
                + $"needs root, which Anthill never uses. Run it yourself: {InstallHint} "
                + "(Anthill's Docker and LXC deployments already provision it.)", -1, "");

        // winget present? Named prerequisite first, the AgentCliInstaller rule: its absence is a
        // different problem from a failed install and needs a different sentence.
        var (wingetOk, _, _, wingetExit) =
            AgentCliDiscovery.Run("winget", new[] { "--version" }, ProbeTimeout);
        if (!wingetOk || wingetExit != 0)
            return new AgentInstallResult(false,
                "winget (Windows' own package manager) did not answer. It ships with Windows 10 "
                + "1809+ and Windows 11 as 'App Installer' — update it from the Microsoft Store, "
                + $"or install Ollama yourself from {DocsUrl}.", -1, "");

        var (started, stdout, stderr, exit) = AgentCliDiscovery.Run("winget",
            new[]
            {
                "install", "--id", "Ollama.Ollama", "--exact", "--silent",
                "--accept-package-agreements", "--accept-source-agreements",
                "--disable-interactivity",
            },
            InstallTimeout);

        if (!started)
            return new AgentInstallResult(false, "Could not start winget.", -1, "");

        var output = string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + "\n" + stderr;
        if (exit != 0)
        {
            // winget's "already installed" family is a success wearing an error code.
            var (installed, version) = Probe();
            if (installed)
                return new AgentInstallResult(true,
                    $"{DisplayName} is already installed ({version}).", 0, Tail(output));
            return new AgentInstallResult(false, Tail(output), exit, Tail(output));
        }

        var after = Probe();
        return new AgentInstallResult(true,
            after.Installed
                ? $"{DisplayName} installed ({after.Version}). Pull a model next — for example: ollama pull llama3.1:8b"
                : $"{DisplayName} installed. If it is not detected yet, restart Anthill so it sees the new PATH.",
            0, Tail(output));
    }

    private static string Tail(string s)
    {
        s = s.Trim();
        return s.Length <= 2000 ? s : "…" + s[^2000..];
    }
}
