using Anthill.Core.Workspaces;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v3.5.0 — what a workspace can be verified with, detected rather than invented.
///
/// The exit gate: "verification commands come from the manifest or operator configuration, never
/// model invention." A model may choose WHICH declared check to run; it can never contribute the
/// command, the arguments or the timeout.
///
/// The direction of information is the property worth protecting and the one worth stating twice:
/// DETECTION reads the project under modification (is there a .sln, a package.json). EXECUTION reads
/// only <see cref="WorkspaceAdapters"/>, which lives in this repository under review. In a harness
/// whose stated purpose is self-improvement, the project under modification is a set of files an
/// agent can edit — so a design that read commands out of it would let an agent rewrite its own
/// verification step, with the check that was meant to catch that carrying it out.
/// </summary>
public class WorkspaceCapabilityTests : IDisposable
{
    private readonly string _dir;

    public WorkspaceCapabilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-manifest-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Project(params string[] files)
    {
        var root = Path.Combine(_dir, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        foreach (var file in files)
        {
            var full = Path.Combine(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "{}");
        }
        return root;
    }

    // ---- detection ------------------------------------------------------------------------------

    [Theory]
    [InlineData("Anthill.sln", "dotnet")]
    [InlineData("Thing.csproj", "dotnet")]
    [InlineData("package.json", "node")]
    [InlineData("pyproject.toml", "python")]
    [InlineData("requirements.txt", "python")]
    public void AProjectMarker_SelectsItsAdapter(string marker, string expected)
    {
        var manifest = WorkspaceCapabilityManifest.Detect(Project(marker));

        Assert.Contains(expected, manifest.ProjectTypes);
        Assert.NotEmpty(manifest.Checks);
    }

    /// <summary>Markers one level down are found — a solution in src/ is still a .NET repository.</summary>
    [Fact]
    public void AMarkerOneLevelDown_IsStillDetected()
    {
        var manifest = WorkspaceCapabilityManifest.Detect(Project("src/Anthill.sln"));
        Assert.Contains("dotnet", manifest.ProjectTypes);
    }

    /// <summary>
    /// A package.json inside node_modules describes a DEPENDENCY, not this workspace. Without this
    /// exclusion every repository with any JS dependency would be classified a Node project and
    /// offered checks that make no sense for it.
    /// </summary>
    [Fact]
    public void AMarkerInsideNodeModules_IsNotDetection()
    {
        var manifest = WorkspaceCapabilityManifest.Detect(Project("node_modules/left-pad/package.json"));
        Assert.DoesNotContain("node", manifest.ProjectTypes);
    }

    /// <summary>
    /// A repository with a .NET backend and a Node frontend genuinely has both. Picking one would
    /// leave half of a full-stack change unverified while reporting success.
    /// </summary>
    [Fact]
    public void AFullStackRepository_GetsBothAdaptersChecks()
    {
        var manifest = WorkspaceCapabilityManifest.Detect(Project("Anthill.sln", "ui/package.json"));

        Assert.Contains("dotnet", manifest.ProjectTypes);
        Assert.Contains("node", manifest.ProjectTypes);
        Assert.Contains(manifest.Checks, c => c.Id == "dotnet_test");
        Assert.Contains(manifest.Checks, c => c.Id == "node_test");
    }

    /// <summary>An unrecognised directory is EMPTY, which is honest — not an error, and not a guess.</summary>
    [Fact]
    public void AnUnrecognisedProject_HasAnEmptyManifest()
    {
        var manifest = WorkspaceCapabilityManifest.Detect(Project("notes.txt"));

        Assert.True(manifest.IsEmpty);
        Assert.Empty(manifest.ProjectTypes);
    }

    [Fact]
    public void AMissingDirectory_IsNone() =>
        Assert.True(WorkspaceCapabilityManifest.Detect("/no/such/path/anywhere").IsEmpty);

    /// <summary>Adapter versions are reported, so a manifest can be compared across runs.</summary>
    [Fact]
    public void TheManifestRecordsAdapterVersions()
    {
        var manifest = WorkspaceCapabilityManifest.Detect(Project("Anthill.sln"));
        Assert.Equal("1", manifest.AdapterVersions["dotnet"]);
    }

    // ---- the commands are ours, not the project's -----------------------------------------------

    /// <summary>
    /// The gate, asserted directly. Every declared command is a fixed filename and a fixed argument
    /// string with no placeholder of any kind: the moment an argument becomes a template, "declared
    /// commands" is command construction with extra steps, and whatever fills the template is the
    /// new attack surface.
    /// </summary>
    [Fact]
    public void NoDeclaredCommand_IsATemplate()
    {
        var templated = WorkspaceAdapters.All
            .SelectMany(a => a.Checks)
            .Where(c => c.Arguments.Contains('{') || c.Arguments.Contains("$(")
                     || c.FileName.Contains('{') || c.FileName.Contains("$("))
            .Select(c => c.Id)
            .ToList();

        Assert.True(templated.Count == 0,
            "These checks interpolate into their command line, so something outside this file "
          + "decides what runs: " + string.Join(", ", templated));
    }

    /// <summary>
    /// And no check invokes a shell. A shell turns a fixed argument string back into an expression
    /// language, which is precisely what the allowlist exists to avoid.
    /// </summary>
    [Fact]
    public void NoDeclaredCommand_InvokesAShell()
    {
        var shells = new[] { "sh", "bash", "zsh", "cmd", "cmd.exe", "powershell", "pwsh" };
        var offenders = WorkspaceAdapters.All.SelectMany(a => a.Checks)
            .Where(c => shells.Contains(c.FileName, StringComparer.OrdinalIgnoreCase))
            .Select(c => c.Id).ToList();

        Assert.True(offenders.Count == 0,
            "These checks run through a shell, restoring the arbitrary execution the catalog "
          + "exists to prevent: " + string.Join(", ", offenders));
    }

    /// <summary>Every check is bounded. An unbounded verification hangs a mission until its deadline.</summary>
    [Fact]
    public void EveryDeclaredCheck_HasATimeout() =>
        Assert.All(WorkspaceAdapters.All.SelectMany(a => a.Checks),
            c => Assert.True(c.TimeoutSeconds > 0, $"check '{c.Id}' has no timeout"));

    /// <summary>
    /// Node installs use `npm ci`, not `npm install`. ci is reproducible from the lockfile and fails
    /// when the lockfile disagrees with package.json — exactly the signal wanted before verifying.
    /// install would quietly REWRITE the lockfile, making the verification step a source change.
    /// </summary>
    [Fact]
    public void TheNodeInstall_IsReproducible_AndDoesNotRewriteTheLockfile()
    {
        var install = WorkspaceAdapters.All.Single(a => a.Id == "node")
            .Checks.Single(c => c.Id == "node_install");

        Assert.StartsWith("ci", install.Arguments);
        Assert.DoesNotContain("install", install.Arguments);
    }

    /// <summary>Check ids are unique across adapters, so adding one cannot change what an id runs.</summary>
    [Fact]
    public void CheckIdsAreUniqueAcrossAdapters()
    {
        var duplicates = WorkspaceAdapters.All.SelectMany(a => a.Checks)
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.True(duplicates.Count == 0, "duplicate check ids: " + string.Join(", ", duplicates));
    }

    // ---- scoping ----------------------------------------------------------------------------------

    /// <summary>
    /// Outside a mission there is no manifest — deliberately NOT falling back to the live checkout.
    /// Detecting there would describe a directory the mission is forbidden to touch, and a check run
    /// in it would verify the wrong files while reporting success.
    /// </summary>
    [Fact]
    public void OutsideAMission_ThereIsNoManifest() =>
        Assert.True(WorkspaceCapabilityManifest.ForCurrentMission().IsEmpty);

    /// <summary>Inside a mission, the manifest describes THAT mission's workspace.</summary>
    [Fact]
    public void InsideAMission_TheManifestDescribesTheWorkspace()
    {
        var root = Project("package.json");
        var workspace = new MissionWorkspace
        {
            Id = "ws1", MissionId = "m1", Root = root, State = WorkspaceState.Active,
        };

        using (MissionWorkspaceScope.Enter(workspace))
        {
            var manifest = WorkspaceCapabilityManifest.ForCurrentMission();

            Assert.Equal(Path.GetFullPath(root), manifest.Root);
            Assert.Contains("node", manifest.ProjectTypes);
            Assert.NotNull(manifest.Find("node_test"));
        }
    }
}
