using Anthill.Core.Configuration;

namespace Anthill.Core.Updates;

/// <summary>
/// HOW THIS COPY OF ANTHILL WAS INSTALLED, because every safety rule about updating it follows
/// from that and from nothing else. v0.3.8.149.
///
/// The update feature exists because operators hate clicking through an installer, and the honest
/// way to serve that is to know exactly what is being replaced. The four shapes do not merely
/// differ in convenience — they differ in whether an in-place update is SAFE AT ALL:
///
///   · <see cref="WindowsInstalled"/> — the desktop app under a per-user install. Data lives in
///     %LOCALAPPDATA%\Anthill, which the installer is forbidden to touch, so replacing the program
///     directory cannot lose a colony. The one shape that can update itself completely silently.
///   · <see cref="WindowsPortable"/> — an unzipped folder. The database sits BESIDE the binary, in
///     `.anthill`, so a careless "replace the folder" is data loss. The updater may replace only
///     the paths the new archive actually contains, and never the folder itself.
///   · <see cref="LinuxService"/> — the LXC/systemd install. `bin/` is read-only to the running
///     service by its own unit (`ProtectSystem=strict`), so the process cannot overwrite itself
///     while running: it stages, and the swap happens before the next start.
///   · <see cref="Docker"/> — a container is REPLACED, not updated. An in-place update would be
///     undone by the next `docker compose up` and would diverge the image from its tag, so this
///     shape never self-updates and says so.
///
/// WHY DETECTION IS EVIDENCE-BASED AND FAILS TO <see cref="Unknown"/>. Every branch below tests
/// something the environment can actually show — a file that exists, a variable systemd itself
/// sets, the directory the process is running from. When nothing matches, the answer is Unknown
/// and the updater declines to act rather than guessing at a shape and replacing the wrong files.
/// A wrong guess here is not a bad user experience; on the portable shape it is a deleted colony.
/// </summary>
public enum InstallShape
{
    /// <summary>Nothing identified this install. The updater refuses to act.</summary>
    Unknown = 0,

    /// <summary>The Windows desktop app, installed by the Inno Setup package (per-user).</summary>
    WindowsInstalled,

    /// <summary>An unzipped Windows folder, run from a terminal. Data sits beside the binary.</summary>
    WindowsPortable,

    /// <summary>The LXC/systemd install under /opt/anthill, running as the `anthill` service user.</summary>
    LinuxService,

    /// <summary>Inside a container. Never self-updates.</summary>
    Docker,

    /// <summary>A source checkout being developed against (bin/Debug, bin/Release, a .git beside it).</summary>
    Development,
}

/// <summary>
/// What the updater knows about this installation: the shape, where the program files live, where
/// a staged update may be written, and — when it cannot act — WHY, in words an operator can use.
/// </summary>
public sealed record InstallSite(
    InstallShape Shape,
    string ProgramDirectory,
    string DataDirectory,
    bool CanSelfUpdate,
    string Explanation)
{
    /// <summary>
    /// This copy lives under Program Files, so replacing it needs elevation no matter how quiet the
    /// installer is. v0.3.8.152.
    ///
    /// This question was already being asked and answered — in <c>UpdateService.IsMachineWide</c>,
    /// inside the desktop tray. That put it in the wrong layer twice over. It ran only on the path
    /// that shows a menu, so <c>ApplyStagedIfAny</c> — which runs before any window exists and is
    /// the code that actually launches setup — never consulted it and would have run an installer
    /// silently against a directory the user cannot write. And it required
    /// <see cref="InstallShape.WindowsInstalled"/>, which is decided by a marker file the installer
    /// drops; a machine-wide copy from BEFORE v0.3.8.149 has no marker, reads as
    /// <see cref="InstallShape.WindowsPortable"/>, and was therefore reported as able to update
    /// itself. That is exactly the copy an operator is most likely to still be running.
    ///
    /// It belongs on the site because it is a property of WHERE THIS COPY IS, which is the one
    /// thing an <see cref="InstallSite"/> exists to know.
    /// </summary>
    public bool MachineWide { get; init; }

    /// <summary>
    /// Where a download is staged. Always under the DATA directory, never beside the binaries:
    /// the data directory is the one place every shape agrees is writable by the running process
    /// (the systemd unit's `ReadWritePaths` names exactly it), and a half-finished download must
    /// never land where a program file is expected to be.
    /// </summary>
    public string StagingDirectory => Path.Combine(DataDirectory, "updates");

    /// <summary>The asset name this shape consumes, given a version. Null when it consumes none.</summary>
    public string? AssetFor(string version) => Shape switch
    {
        InstallShape.WindowsInstalled => $"anthill-setup-{version}.exe",
        InstallShape.WindowsPortable => $"anthill-{version}-win-x64.zip",
        InstallShape.LinuxService => $"anthill-{version}-linux-x64.tar.gz",
        _ => null,
    };
}

/// <summary>Resolves the <see cref="InstallSite"/> from the running environment.</summary>
public static class InstallDetector
{
    /// <summary>
    /// Set by the Inno Setup package so the running app can recognise its own install without
    /// guessing from a path. A marker file is used rather than a registry read because the same
    /// code has to answer on Linux, and because an operator who copies the folder somewhere else
    /// copies the marker with it — which is the truth: it is still that install.
    /// </summary>
    public const string InstalledMarker = ".anthill-installed";

    /// <summary>Overrides detection outright. For tests, and for an operator whose layout we did not foresee.</summary>
    public const string ShapeOverrideVariable = "ANTHILL_INSTALL_SHAPE";

    public static InstallSite Detect() => Detect(
        programDirectory: AppContext.BaseDirectory,
        dataDirectory: AnthillRuntime.PathFromScript(AnthillRuntime.DefaultWorkspace),
        os: Environment.OSVersion.Platform);

    /// <summary>
    /// The testable form: everything it reads is a parameter or a file it is told about.
    ///
    /// <paramref name="programFilesRoots"/> is the one addition that is not a fact of this process:
    /// <c>Environment.GetFolderPath(ProgramFiles)</c> returns the empty string off Windows, so a
    /// Linux test host could not otherwise reach the machine-wide branch at all — and the CI that
    /// runs this suite is Linux. Passing the roots keeps the promise made above.
    /// </summary>
    public static InstallSite Detect(
        string programDirectory, string dataDirectory, PlatformID os,
        IEnumerable<string>? programFilesRoots = null)
    {
        var program = Path.GetFullPath(programDirectory);
        var data = Path.GetFullPath(dataDirectory);

        InstallSite Site(InstallShape shape, bool can, string why) => new(shape, program, data, can, why);

        var overridden = Environment.GetEnvironmentVariable(ShapeOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden)
            && Enum.TryParse<InstallShape>(overridden.Trim(), ignoreCase: true, out var forced))
            return Site(forced, forced is InstallShape.WindowsInstalled or InstallShape.WindowsPortable or InstallShape.LinuxService,
                $"{ShapeOverrideVariable} names this install as {forced}.");

        // A CONTAINER IS REPLACED, NOT UPDATED — checked first, because a container can otherwise
        // look exactly like the Linux service or a portable extract, and being wrong in that
        // direction is the one that writes into an image layer the next `up` discards.
        if (IsContainer())
            return Site(InstallShape.Docker, false,
                "This colony runs in a container. Containers are replaced rather than updated: pull the "
              + "new image and recreate (`docker compose pull && docker compose up -d`). An in-place "
              + "update would be undone by the next recreate and would leave the image disagreeing "
              + "with its tag.");

        // A SOURCE CHECKOUT IS SOMEBODY'S WORKING TREE. Replacing binaries under `bin/Debug` while
        // a developer builds into it is not an update, it is a race with their compiler.
        if (LooksLikeDevelopment(program))
            return Site(InstallShape.Development, false,
                "This looks like a source checkout (running from a build output directory). Update it "
              + "with git and a rebuild; the updater does not overwrite a working tree.");

        var windows = os is PlatformID.Win32NT;

        if (windows)
        {
            // A COPY UNDER PROGRAM FILES CANNOT REPLACE ITSELF, WHATEVER ELSE IS TRUE OF IT.
            // v0.3.8.152. Checked before the marker, because it outranks it: a machine-wide
            // install that DOES carry the marker is still one Windows will not let this user
            // overwrite, and answering "installed, can self-update" would send the updater to run
            // setup.exe silently against a directory it will be refused by.
            if (IsUnderProgramFiles(program, programFilesRoots))
                return Site(InstallShape.WindowsInstalled, false,
                    "Installed for all users, under Program Files. Windows will not let Anthill "
                  + "replace its own files there without administrator approval, so it will not try. "
                  + "Move to a per-user install to get silent updates — Anthill offers this once, and "
                  + "your colony's memory and settings are kept either way.") with { MachineWide = true };

            // The installer drops a marker beside the exe. Its presence is the difference between
            // "an install that owns its directory" and "a folder somebody unzipped", and that
            // difference decides whether the whole directory may be replaced.
            if (File.Exists(Path.Combine(program, InstalledMarker)))
                return Site(InstallShape.WindowsInstalled, true,
                    "Installed for this user. Updates download, verify and apply on the next launch, "
                  + "with no prompt and no administrator approval.");

            return Site(InstallShape.WindowsPortable, true,
                "Portable copy. Updates replace only the program files this release ships and never "
              + "touch the colony's data directory beside them.");
        }

        // systemd sets INVOCATION_ID for every unit it starts — a fact about the environment rather
        // than a guess from a path, and it is absent when an operator runs the same binary by hand.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INVOCATION_ID"))
            || File.Exists("/run/systemd/system"))
            return Site(InstallShape.LinuxService, true,
                "Managed by systemd. Updates download and verify while the service runs, then swap in "
              + "before the next start.");

        return Site(InstallShape.Unknown, false,
            "This installation's shape could not be identified, so the updater will not replace "
          + "anything. Update it the way you installed it.");
    }

    private static bool IsContainer()
    {
        try
        {
            if (File.Exists("/.dockerenv")) return true;
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHILL_IN_CONTAINER"))) return true;
            // The cgroup line names the container runtime on every engine that matters here.
            if (File.Exists("/proc/1/cgroup"))
            {
                var cgroup = File.ReadAllText("/proc/1/cgroup");
                if (cgroup.Contains("docker", StringComparison.OrdinalIgnoreCase)
                    || cgroup.Contains("containerd", StringComparison.OrdinalIgnoreCase)
                    || cgroup.Contains("kubepods", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* an unreadable /proc is not evidence of a container */ }
        return false;
    }

    /// <summary>
    /// Both Program Files roots, because a 32-bit package on a 64-bit machine lands in the x86 one
    /// and is no more writable for it. Compared with the same containment test the updater uses to
    /// decide whether an archive entry escapes its target, so "inside" means one thing in this
    /// codebase rather than two nearly-identical string comparisons.
    /// </summary>
    public static bool IsUnderProgramFiles(string programDirectory, IEnumerable<string>? roots = null)
    {
        foreach (var root in roots ?? DefaultProgramFilesRoots())
            if (!string.IsNullOrEmpty(root) && UpdateApplier.IsInside(programDirectory, root)) return true;
        return false;
    }

    private static IEnumerable<string> DefaultProgramFilesRoots()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string root;
            try { root = Environment.GetFolderPath(folder); }
            catch { continue; }
            if (!string.IsNullOrEmpty(root)) yield return root;
        }
    }

    private static bool LooksLikeDevelopment(string program)
    {
        var normalized = program.Replace('\\', '/');
        if (normalized.Contains("/bin/Debug/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/bin/Release/", StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            // A repository checkout two or three levels up is the other reliable tell.
            var dir = new DirectoryInfo(program);
            for (var i = 0; i < 4 && dir is not null; i++, dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                    && File.Exists(Path.Combine(dir.FullName, "Anthill.sln")))
                    return true;
        }
        catch { /* best effort */ }
        return false;
    }
}
