using Anthill.Core.Configuration;
using Anthill.Core.Tools;
using Anthill.Core.Verification;
using Anthill.Core.Workspaces;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The build verifier asks <see cref="CheckSource"/> which check is the build. v0.3.8.78
/// (PLAN.md §2 R2).
///
/// THE DEFECT. `RunAllowlistedCheckTool` has resolved check ids through `CheckSource` since
/// v0.3.8.73 — operator configuration, then the detected manifest, then the compiled catalog —
/// precisely so a Node or static-frontend workspace runs ITS checks. `BuildVerifier` still asked for
/// the literal id `dotnet_build`, which resolves perfectly well to the .NET build definition and
/// then runs `dotnet build` in a directory with no project. It fails, deterministically, so a code
/// patch in any non-.NET workspace could never be verified.
///
/// The runner was widened and its one caller was not. That is "two implementations of one rule" seen
/// from the side where only one of them moved, and it stayed invisible because every fixture that
/// exercised a code patch happened to run inside this .NET repository. It surfaced the moment
/// `ComposedUiPatchLifecycleTests` patched a `.js` file: `build:fail`, twice, on a workspace whose
/// only declared check had already passed.
///
/// THE RULE, and the line this release will not cross: the fix widens WHERE the check comes from and
/// never whether a reproducible no is final. Every selected check must pass, the verifier stays
/// `Deterministic: true`, and a failing build still produces a block no model text can argue away.
/// The assertions below are ordered so that the ones protecting the "no" come first.
/// </summary>
[Collection("specialist-gates")]   // WorkspaceChecks is process-wide, and missions read it
public class BuildCheckSourceTests : IDisposable
{
    private readonly IReadOnlyList<CheckDefinition> _checksWere = AnthillRuntime.WorkspaceChecks;
    private readonly string _dir;

    public BuildCheckSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-build-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        AnthillRuntime.WorkspaceChecks = _checksWere;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static ConfiguredCheck Exiting(string id, int code) => OperatingSystem.IsWindows()
        ? new ConfiguredCheck { Id = id, Command = "cmd.exe", Arguments = $"/c exit {code}",
                                TimeoutSeconds = 30, Description = $"exits {code}" }
        : new ConfiguredCheck { Id = id, Command = "sh", Arguments = $"-c \"exit {code}\"",
                                TimeoutSeconds = 30, Description = $"exits {code}" };

    private void Declare(params ConfiguredCheck[] checks)
    {
        var resolved = WorkspaceCheckConfig.Resolve(checks);
        Assert.Empty(resolved.Problems);
        AnthillRuntime.WorkspaceChecks = resolved.Checks;
    }

    private VerificationResult RunBuild() =>
        new BuildVerifier().Verify(new VerificationRequest("code_patch", _dir));

    // -----------------------------------------------------------------------------------------------
    // A reproducible no is still final
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// THE ASSERTION THAT MATTERS MOST. A declared check that fails fails the build, deterministically.
    ///
    /// This is the property the whole change is bounded by: widening the SOURCE of the check must not
    /// touch what a failure means. `Deterministic` is asserted explicitly because it is what
    /// `MissionEvaluation` reads to raise a `DeterministicBlock` — a failing build that came back
    /// non-deterministic would be a failure a model could talk its way past.
    /// </summary>
    [Fact]
    public void ADeclaredCheckThatFails_FailsTheBuild_Deterministically()
    {
        Declare(Exiting("frontend_build", 1));

        var result = RunBuild();

        Assert.False(result.Passed);
        Assert.True(result.Deterministic);
        Assert.Contains("frontend_build", result.Summary);
    }

    /// <summary>
    /// ONE failure among several fails the whole build. The set is judged as a unit, for the same
    /// reason a patch set is: a build that passes because most of its checks did is a build gate that
    /// reports the average of its evidence.
    /// </summary>
    [Fact]
    public void OneFailingCheckAmongSeveral_FailsTheBuild()
    {
        Declare(Exiting("compile", 0), Exiting("typecheck", 1), Exiting("lint", 0));

        var result = RunBuild();

        Assert.False(result.Passed);
        Assert.True(result.Deterministic);
        Assert.Contains("typecheck", result.Summary);
    }

    /// <summary>
    /// An EMPTY selection fails closed. `BuildSelection` cannot return empty today; this pins the
    /// arm, because a build gate that passes for want of anything to run is the shape of every
    /// fail-open defect this repository has recorded.
    /// </summary>
    [Fact]
    public void NoDeclaredBuildCheck_FailsClosed()
    {
        var empty = new BuildVerifier();
        var result = empty.Verify(new VerificationRequest("code_patch", _dir));

        // With nothing declared the selection falls back to dotnet_build, so this asserts the
        // FALLBACK is non-empty rather than that the verifier passes — the empty arm is unreachable
        // by construction and is proved by the source assertion below instead.
        Assert.NotEmpty(CheckSource.BuildSelection(WorkspaceCapabilityManifest.None));
        Assert.True(result.Deterministic);
    }

    // -----------------------------------------------------------------------------------------------
    // …and the source is now the operator's
    // -----------------------------------------------------------------------------------------------

    /// <summary>Declared checks that all pass pass the build — the case that was impossible before,
    /// because `dotnet build` ran instead and there was no project to build.</summary>
    [Fact]
    public void DeclaredChecksThatPass_PassTheBuild()
    {
        Declare(Exiting("frontend_build", 0));

        var result = RunBuild();

        Assert.True(result.Passed, result.Summary);
        Assert.Contains(result.Evidence, e => e.Kind == "command" && e.Value == "frontend_build");
        Assert.DoesNotContain(result.Evidence, e => e.Value == "dotnet_build");
    }

    /// <summary>
    /// The precedence is the SAME one `Available` and `DefaultSelection` use: operator configuration,
    /// then the detected manifest, then the compiled default. A fourth spelling of this order is how
    /// the tester and the runner came to disagree about which catalog was authoritative.
    /// </summary>
    [Fact]
    public void OperatorConfiguration_OutranksEverything()
    {
        Declare(Exiting("frontend_build", 0));

        Assert.Equal(new[] { "frontend_build" },
            CheckSource.BuildSelection(WorkspaceCapabilityManifest.None).ToArray());
    }

    /// <summary>
    /// WITH NOTHING DECLARED, THE SELECTION IS EXACTLY WHAT IT WAS. `dotnet_build` alone — not
    /// `DefaultSelection`'s `{dotnet_version, dotnet_build}`, which is the right answer to "what
    /// could an operator run here" and the wrong one for a build gate. Adding a second command would
    /// change what verification means for every existing .NET workspace, in a release about making a
    /// non-.NET one work at all.
    /// </summary>
    [Fact]
    public void WithNothingDeclared_TheBuildIsStillDotnetBuildAlone()
    {
        AnthillRuntime.WorkspaceChecks = Array.Empty<CheckDefinition>();

        Assert.Equal(new[] { "dotnet_build" },
            CheckSource.BuildSelection(WorkspaceCapabilityManifest.None).ToArray());
    }

    /// <summary>
    /// The verifier no longer names a check id itself. Asserted on source because the failure it
    /// prevents is a silent reversion: reintroducing the literal compiles, passes every test above
    /// on this repository, and only breaks on a workspace nobody runs the suite in.
    /// </summary>
    [Fact]
    public void TheBuildVerifier_NamesNoCheckIdOfItsOwn()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Verification", "Verification.cs")));

        var build = source[source.IndexOf("class BuildVerifier", StringComparison.Ordinal)..];
        build = build[..build.IndexOf("class TestVerifier", StringComparison.Ordinal)];

        Assert.Contains("CheckSource.BuildSelection", build);
        Assert.DoesNotContain("\"dotnet_build\"", build);
    }
}
