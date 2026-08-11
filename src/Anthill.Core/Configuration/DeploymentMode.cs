namespace Anthill.Core.Configuration;

/// <summary>
/// How this colony is deployed. v0.3.8.40.
///
/// Anthill runs two genuinely different ways, and until now it could not say which. On a laptop it
/// is a personal assistant with one operator and no infrastructure to touch. In an LXC or a Docker
/// host it is a shared control plane, and the operations that would be reckless on the first are
/// the entire point of the second.
///
/// The distinction is declared rather than inferred at each call site. A feature that asks "am I
/// in a container" for itself will ask differently from the next one, and the two will disagree on
/// exactly the host where it matters.
/// </summary>
public enum DeploymentMode
{
    /// <summary>Personal machine, one operator, no infrastructure control plane.</summary>
    Desktop,

    /// <summary>A container or VM host. Shared, and expected to manage infrastructure.</summary>
    Server,
}

/// <summary>
/// Resolves the deployment mode from an explicit setting, or detects it.
///
/// The DECISION is a pure function over facts, and the PROBING that gathers those facts is
/// separate. That split is the only reason this is testable at all: asserting the container rules
/// otherwise needs a container, so the rules would be verified on a developer laptop by not being
/// verified.
/// </summary>
public static class DeploymentModeResolver
{
    public const string Auto = "auto";

    /// <summary>Facts about the host, gathered once. Every field is something a probe can observe.</summary>
    public readonly record struct HostFacts(
        bool DockerEnvFileExists,
        string CgroupText,
        string InitEnviron,
        bool RunningOnWindows);

    /// <summary>
    /// The rule. Explicit configuration always wins — an operator who has said which way this runs
    /// is not overruled by a heuristic, and a heuristic is what every one of these signals is.
    ///
    /// Returns the mode AND why, because "why" is what an operator needs when the answer surprises
    /// them. A mode with no stated reason is a mode nobody can argue with or correct.
    /// </summary>
    public static (DeploymentMode Mode, string Reason, bool Detected) Resolve(string? configured, HostFacts facts)
    {
        if (!string.IsNullOrWhiteSpace(configured) &&
            !string.Equals(configured, Auto, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(configured, "server", StringComparison.OrdinalIgnoreCase))
                return (DeploymentMode.Server, "Set to 'server' in configuration.", false);
            if (string.Equals(configured, "desktop", StringComparison.OrdinalIgnoreCase))
                return (DeploymentMode.Desktop, "Set to 'desktop' in configuration.", false);

            // An unreadable value is NOT silently treated as auto. A typo like "sever" would then
            // pick a mode by accident and report it as detected, and the operator would have no way
            // to tell their setting was being ignored.
            return (Detect(facts).Mode,
                $"Configuration says '{configured}', which is not 'desktop', 'server' or 'auto' — detected instead.",
                true);
        }

        var d = Detect(facts);
        return (d.Mode, d.Reason, true);
    }

    /// <summary>
    /// Detection, in order of how much each signal actually proves.
    ///
    /// `/.dockerenv` is written by Docker itself, so it is the strongest and is checked first.
    /// Cgroup paths name the runtime that created them and cover Docker, Podman and LXC alike.
    /// PID 1's environment carries `container=lxc` under LXC and systemd-nspawn, which is the case
    /// the cgroup check misses on cgroup v2 hosts where the path is bare.
    /// </summary>
    private static (DeploymentMode Mode, string Reason) Detect(HostFacts f)
    {
        // Windows is not a container host in the sense that matters here. A Windows desktop is the
        // deployment this distinction exists to protect, and treating it as a server because some
        // path happened to match would be the worst possible default.
        if (f.RunningOnWindows) return (DeploymentMode.Desktop, "Running on Windows — treated as a desktop install.");

        if (f.DockerEnvFileExists) return (DeploymentMode.Server, "Running in Docker (/.dockerenv is present).");

        var cg = f.CgroupText ?? "";
        if (cg.Contains("docker", StringComparison.OrdinalIgnoreCase)) return (DeploymentMode.Server, "Running in Docker (cgroup).");
        if (cg.Contains("lxc", StringComparison.OrdinalIgnoreCase)) return (DeploymentMode.Server, "Running in LXC (cgroup).");
        if (cg.Contains("containerd", StringComparison.OrdinalIgnoreCase)) return (DeploymentMode.Server, "Running in a containerd container (cgroup).");
        if (cg.Contains("kubepods", StringComparison.OrdinalIgnoreCase)) return (DeploymentMode.Server, "Running in Kubernetes (cgroup).");

        var env = f.InitEnviron ?? "";
        if (env.Contains("container=lxc", StringComparison.OrdinalIgnoreCase)) return (DeploymentMode.Server, "Running in LXC (PID 1 environment).");
        if (env.Contains("container=", StringComparison.OrdinalIgnoreCase)) return (DeploymentMode.Server, "Running in a container (PID 1 environment).");

        return (DeploymentMode.Desktop, "No container signals found — treated as a desktop install.");
    }

    /// <summary>
    /// Reads the host. The ONLY I/O in this file, and every read is guarded: a colony must not fail
    /// to start because a proc file was unreadable, and "could not tell" resolves to Desktop, which
    /// is the answer that grants the least.
    /// </summary>
    public static HostFacts Probe()
    {
        static string SafeRead(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch { return ""; }
        }

        static bool SafeExists(string path)
        {
            try { return File.Exists(path); }
            catch { return false; }
        }

        return new HostFacts(
            DockerEnvFileExists: SafeExists("/.dockerenv"),
            CgroupText: SafeRead("/proc/1/cgroup"),
            // NUL-separated; replaced so a plain Contains works on it.
            InitEnviron: SafeRead("/proc/1/environ").Replace('\0', '\n'),
            RunningOnWindows: OperatingSystem.IsWindows());
    }
}
