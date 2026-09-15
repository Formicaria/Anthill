using System.Reflection;
using Anthill.Core.Configuration;
using Anthill.Core.Updates;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE FIELD FAILURE THAT MOTIVATED v0.3.8.152, PINNED SO IT CANNOT COME BACK.
///
/// A desktop install would not start. The colony's database, backups, logs and exports were all
/// present and writable under %LOCALAPPDATA%; what killed the process was `agent_workspace_dir`
/// pointing at C:\Program Files\Anthill\workspace — a directory the file ant is merely allowed to
/// READ from. It was being created at boot in the same loop as the colony's storage, so a setting
/// that governs a capability took down the whole colony, and the operator was shown a stack trace
/// naming a path but not the key that produced it.
///
/// These tests hold three things still: which paths are fatal and which are not, that a fatal one
/// names its key, and that a copy under Program Files knows it cannot update itself.
/// </summary>
public sealed class WorkspaceScopeTests
{
    // A file standing where a directory must go. Deterministic on every OS and under every user —
    // unlike a permission bit, which does nothing when the suite happens to run as root.
    private static string BlockedDirectoryPath(string root, string name)
    {
        var blocker = Path.Combine(root, name);
        File.WriteAllText(blocker, "not a directory");
        return Path.Combine(blocker, "child");
    }

    private static (string Root, IDisposable Scope) FreshHome()
    {
        var root = Path.Combine(Path.GetTempPath(), "anthill-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return (root, new HomeScope(root));
    }

    /// <summary>Points ANTHILL_HOME at a scratch directory and forces a fresh Initialize.</summary>
    private sealed class HomeScope : IDisposable
    {
        private readonly string? _previousHome = Environment.GetEnvironmentVariable("ANTHILL_HOME");
        private readonly string _root;

        public HomeScope(string root)
        {
            _root = root;
            Environment.SetEnvironmentVariable("ANTHILL_HOME", root);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ANTHILL_HOME", _previousHome);
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void EnsureWorkspace(AnthillConfig config)
    {
        var method = typeof(AnthillRuntime).GetMethod(
            "EnsureWorkspace", BindingFlags.NonPublic | BindingFlags.Static)!;
        try { method.Invoke(null, new object?[] { config }); }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is not null)
        {
            throw wrapped.InnerException;
        }
    }

    [Fact]
    public void AReadingScopeItCannotCreate_IsAProblemReported_NotAColonyThatWillNotStart()
    {
        var (root, scope) = FreshHome();
        using (scope)
        {
            var config = new AnthillConfig { AgentWorkspaceDir = BlockedDirectoryPath(root, "blocked-scope") };

            // The whole point: this returns.
            EnsureWorkspace(config);

            Assert.False(string.IsNullOrEmpty(AnthillRuntime.AgentWorkspaceProblem));
            Assert.Contains("agent_workspace_dir", AnthillRuntime.AgentWorkspaceProblem);
            Assert.Contains("colony's own data is unaffected", AnthillRuntime.AgentWorkspaceProblem);

            // And the colony's own storage was still made, because it was never the thing at fault.
            Assert.True(Directory.Exists(AnthillRuntime.WorkspaceRootPath));
            Assert.True(Directory.Exists(Path.GetDirectoryName(AnthillRuntime.DbPath)!));
        }
    }

    [Fact]
    public void AWorkingReadingScope_LeavesNoProblemBehind()
    {
        var (root, scope) = FreshHome();
        using (scope)
        {
            EnsureWorkspace(new AnthillConfig());
            Assert.Equal("", AnthillRuntime.AgentWorkspaceProblem);
        }
    }

    [Theory]
    [InlineData("workspace_root")]
    [InlineData("db_path")]
    [InlineData("backup_dir")]
    [InlineData("logs_dir")]
    [InlineData("exports_dir")]
    public void StorageTheColonyCannotRunWithout_StillRefusesTheBoot_AndNamesTheKey(string key)
    {
        var (root, scope) = FreshHome();
        using (scope)
        {
            var blocked = BlockedDirectoryPath(root, "blocked-" + key);
            var config = new AnthillConfig();
            switch (key)
            {
                case "workspace_root": config.WorkspaceRoot = blocked; break;
                case "db_path": config.DbPath = Path.Combine(blocked, "anthill.db"); break;
                case "backup_dir": config.BackupDir = blocked; break;
                case "logs_dir": config.LogsDir = blocked; break;
                case "exports_dir": config.ExportsDir = blocked; break;
            }

            var refused = Assert.Throws<ColonyStorageException>(() => EnsureWorkspace(config));

            Assert.Equal(key, refused.Key);
            Assert.Contains(key, refused.Message);
            // The message has to be actionable, which means naming the file to edit and the escape
            // hatch that moves the whole colony — not just the path that lost.
            Assert.Contains("ANTHILL_HOME", refused.Message);
            Assert.NotNull(refused.InnerException);
        }
    }

    [Fact]
    public void TheRefusal_RepeatsTheResolvedPath_OnlyWhenItDiffersFromWhatWasTyped()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "anthill-absolute-" + Guid.NewGuid().ToString("N"));
        var rooted = new ColonyStorageException(
            "backup_dir", absolute, absolute, "/etc/anthill/config.json", new IOException("no"));
        Assert.DoesNotContain("resolves to", rooted.Message);

        var relative = new ColonyStorageException(
            "backup_dir", ".anthill/backups", "/opt/anthill/.anthill/backups",
            "/etc/anthill/config.json", new IOException("no"));
        Assert.Contains("resolves to", relative.Message);
        Assert.Contains(".anthill/backups", relative.Message);
        Assert.Contains("/opt/anthill/.anthill/backups", relative.Message);
        Assert.Contains("/etc/anthill/config.json", relative.Message);
    }

    [Fact]
    public void TheValidator_ReportsAnUnusableScope_AsAnErrorFinding_WithoutRefusingTheBoot()
    {
        var (root, scope) = FreshHome();
        using (scope)
        {
            EnsureWorkspace(new AnthillConfig { AgentWorkspaceDir = BlockedDirectoryPath(root, "blocked-validator") });

            var findings = RuntimeConfigValidator.Validate();
            var finding = Assert.Single(findings, f => f.Combination == "agent_workspace_unusable");
            Assert.Equal("error", finding.Severity);

            // The older, weaker finding must not also fire: one fact, one report. Being told the
            // scope "does not exist" alongside being told why it could not be made is two editors
            // narrating one failure.
            Assert.DoesNotContain(findings, f => f.Combination == "sandbox_without_workspace");
        }
    }

    // ---- A copy under Program Files knows what it cannot do -------------------------------------

    private const string PF = "/programfiles";

    private static InstallSite DetectAt(string programDirectory, bool marker = false)
    {
        if (marker)
        {
            Directory.CreateDirectory(programDirectory);
            File.WriteAllText(Path.Combine(programDirectory, InstallDetector.InstalledMarker), "");
        }
        return InstallDetector.Detect(
            programDirectory, Path.Combine(programDirectory, ".anthill"),
            PlatformID.Win32NT, new[] { PF });
    }

    [Fact]
    public void AMachineWideCopy_IsNotOfferedSilentUpdates_EvenWithTheInstallerMarker()
    {
        var underProgramFiles = Path.Combine(PF, "Anthill");

        var bare = DetectAt(underProgramFiles);

        Assert.True(bare.MachineWide);
        Assert.False(bare.CanSelfUpdate);
        Assert.Contains("administrator approval", bare.Explanation);

        // The pre-v0.3.8.149 copy carries no marker file. Before this release it fell through to
        // WindowsPortable and was told it could replace its own program files — and that is the
        // copy an operator upgrading from an older install is most likely to still be running.
        Assert.NotEqual(InstallShape.WindowsPortable, bare.Shape);
        Assert.Equal(InstallShape.WindowsInstalled, bare.Shape);
    }

    [Fact]
    public void AMarkerInsideProgramFiles_DoesNotUnlockSelfUpdating()
    {
        var root = Path.Combine(Path.GetTempPath(), "anthill-pf-" + Guid.NewGuid().ToString("N"));
        var program = Path.Combine(root, "Anthill");
        try
        {
            Directory.CreateDirectory(program);
            File.WriteAllText(Path.Combine(program, InstallDetector.InstalledMarker), "");

            var site = InstallDetector.Detect(
                program, Path.Combine(program, ".anthill"), PlatformID.Win32NT, new[] { root });

            Assert.True(site.MachineWide);
            Assert.False(site.CanSelfUpdate);
            // It still knows which asset it WOULD take. Refusing to act is not the same as not
            // knowing what it is, and the migration offer needs the installer's name.
            Assert.Equal("anthill-setup-0.3.8.152.exe", site.AssetFor("0.3.8.152"));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void APerUserInstall_KeepsSilentUpdating_AndIsNotMachineWide()
    {
        var root = Path.Combine(Path.GetTempPath(), "anthill-user-" + Guid.NewGuid().ToString("N"));
        var program = Path.Combine(root, "Programs", "Anthill");
        try
        {
            Directory.CreateDirectory(program);
            File.WriteAllText(Path.Combine(program, InstallDetector.InstalledMarker), "");

            var site = InstallDetector.Detect(
                program, Path.Combine(root, "Anthill"), PlatformID.Win32NT, new[] { PF });

            Assert.False(site.MachineWide);
            Assert.True(site.CanSelfUpdate);
            Assert.Equal(InstallShape.WindowsInstalled, site.Shape);
            Assert.Equal("anthill-setup-0.3.8.152.exe", site.AssetFor("0.3.8.152"));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void MachineWideIsDecidedInOnePlace_AndTheDesktopAsksIt_RatherThanRepeatingIt()
    {
        // v0.3.8.152 moved this out of UpdateService, where it ran only on the tray path and so
        // was never consulted by ApplyStagedIfAny — the code that actually launches the installer.
        var updater = File.ReadAllText(RepoFile("src/Anthill.Desktop/UpdateService.cs"));
        Assert.DoesNotContain("private static bool IsMachineWide", updater);
        Assert.Contains("site.MachineWide", updater);
        Assert.DoesNotContain("SpecialFolder.ProgramFiles", updater);
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Could not locate {relative} from {AppContext.BaseDirectory}.");
    }
}
