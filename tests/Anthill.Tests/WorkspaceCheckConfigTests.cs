using Anthill.Core.Agents;
using Anthill.Core.Configuration;
using Anthill.Core.Tools;
using Anthill.Core.Workspaces;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The operator half of the verification contract. v0.3.8.73 — PLAN.md §2 item 3's blocker.
///
/// `WorkspaceCapabilityManifest`'s header has stated the exit gate since v3.5.0: "verification
/// commands come from the manifest or operator configuration, never model invention." The manifest
/// half was built then. **The operator half never existed** — and v0.3.8.71 established what that
/// cost: a workspace the adapters do not recognise has no usable checks at all, because
/// `CheckCatalog.Register` is documented as the operator extension point and is reachable only by
/// naming a check id in task text that `ExecutionService` writes. Qualification scenarios 3 and 15's
/// last edge have both been blocked on it.
///
/// WHAT IS BEING TESTED, in order of how much it matters:
///   1. the precedence (operator → detection → compiled catalog) is ONE function, not two spellings;
///   2. a declaration that cannot run is refused at LOAD, not at dispatch;
///   3. the source of truth is the operator's configuration and never the workspace being modified;
///   4. a patch proposing to edit it is a blocking security finding.
/// </summary>
[Collection("specialist-gates")]   // AnthillRuntime.WorkspaceChecks is a mutable static
public class WorkspaceCheckConfigTests : IDisposable
{
    private readonly IReadOnlyList<CheckDefinition> _checksWere = AnthillRuntime.WorkspaceChecks;

    public void Dispose() => AnthillRuntime.WorkspaceChecks = _checksWere;

    private static ConfiguredCheck Declared(string id, string command = "echo", string args = "ok",
        int timeout = 30, bool enabled = true) =>
        new() { Id = id, Command = command, Arguments = args, TimeoutSeconds = timeout, Enabled = enabled };

    // -----------------------------------------------------------------------------------------------
    // Validation — a declaration that cannot run is refused where a refusal can still be read
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void AWellFormedDeclaration_BecomesARunnableCheck()
    {
        var result = WorkspaceCheckConfig.Resolve(new[] { Declared("colony_probe", "python3", "-V", 45) });

        Assert.Empty(result.Problems);
        var check = Assert.Single(result.Checks);
        Assert.Equal("colony_probe", check.Id);
        Assert.Equal("python3", check.FileName);
        Assert.Equal("-V", check.Arguments);
        Assert.Equal(45, check.TimeoutSeconds);
        Assert.True(check.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(check.Description));
    }

    /// <summary>
    /// One bad entry costs its own place and nothing else. Throwing would take away every other
    /// check the operator declared, which turns a typo into an unverified installation — and
    /// dropping it silently would be worse still, because the tester would then report PASS over a
    /// check set nobody chose.
    /// </summary>
    [Fact]
    public void OneBadEntry_DoesNotCostTheOthers()
    {
        var result = WorkspaceCheckConfig.Resolve(new[]
        {
            Declared("good_one"),
            new ConfiguredCheck { Id = "no_command", Command = "  " },
            Declared("good_two"),
        });

        Assert.Equal(new[] { "good_one", "good_two" }, result.Checks.Select(c => c.Id));
        Assert.Single(result.Problems);
        Assert.Contains("no_command", result.Problems[0]);
    }

    [Theory]
    [InlineData("", "no id")]
    [InlineData("has space", "whitespace or quotes")]
    [InlineData("has\"quote", "whitespace or quotes")]
    public void ADeclarationTheTesterCouldNotName_IsRefused(string id, string expected)
    {
        var result = WorkspaceCheckConfig.Resolve(new[] { Declared(id) });

        Assert.Empty(result.Checks);
        Assert.Contains(expected, Assert.Single(result.Problems));
    }

    /// <summary>
    /// A built-in id may not be redefined. `dotnet_build` means one thing across the auto-apply
    /// verify path, the graduation record and every changelog entry that names it; configuration
    /// that kept the name while changing the command is how a report comes to describe a check that
    /// did not run.
    /// </summary>
    [Theory]
    [InlineData("dotnet_build")]
    [InlineData("dotnet_test")]
    [InlineData("DOTNET_VERSION")]
    public void ABuiltInId_CannotBeRedefined(string id)
    {
        var result = WorkspaceCheckConfig.Resolve(new[] { Declared(id, "cmd", "/c exit 0") });

        Assert.Empty(result.Checks);
        Assert.Contains("built-in", Assert.Single(result.Problems));
        // …and the built-in still means what it meant.
        Assert.Equal("dotnet", CheckCatalog.Get("dotnet_build")!.FileName);
    }

    [Fact]
    public void ARepeatedId_KeepsTheFirst_AndSaysSo()
    {
        var result = WorkspaceCheckConfig.Resolve(new[]
        {
            Declared("twice", "first"), Declared("twice", "second"),
        });

        Assert.Equal("first", Assert.Single(result.Checks).FileName);
        Assert.Contains("repeats", Assert.Single(result.Problems));
    }

    /// <summary>
    /// A timeout of zero is a check that cannot pass; an unbounded one is a hung mission the
    /// operator reads as a slow one. Clamped rather than refused — the check is still meaningful and
    /// the operator is told.
    /// </summary>
    [Theory]
    [InlineData(0, WorkspaceCheckConfig.MinTimeoutSeconds)]
    [InlineData(-5, WorkspaceCheckConfig.MinTimeoutSeconds)]
    [InlineData(999_999, WorkspaceCheckConfig.MaxTimeoutSeconds)]
    public void AnImpossibleTimeout_IsClampedAndReported(int declared, int expected)
    {
        var result = WorkspaceCheckConfig.Resolve(new[] { Declared("t", timeout: declared) });

        Assert.Equal(expected, Assert.Single(result.Checks).TimeoutSeconds);
        Assert.Contains("timeout_seconds", Assert.Single(result.Problems));
    }

    [Fact]
    public void NoDeclarationAtAll_IsNotAProblem()
    {
        Assert.Empty(WorkspaceCheckConfig.Resolve(null).Checks);
        Assert.Empty(WorkspaceCheckConfig.Resolve(null).Problems);
        Assert.Empty(WorkspaceCheckConfig.Resolve(Array.Empty<ConfiguredCheck>()).Problems);
    }

    // -----------------------------------------------------------------------------------------------
    // Precedence — one function, and it is the one both callers use
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void WithNothingConfigured_TheCompiledCatalogStillAnswers()
    {
        AnthillRuntime.WorkspaceChecks = Array.Empty<CheckDefinition>();

        var available = CheckSource.Available(WorkspaceCapabilityManifest.None).Select(c => c.Id).ToList();

        Assert.Contains("dotnet_build", available);
        Assert.Equal(new[] { "dotnet_version", "dotnet_build" },
            CheckSource.DefaultSelection(WorkspaceCapabilityManifest.None));
    }

    /// <summary>
    /// THE POINT OF THE RELEASE. With checks configured, an undetected workspace has checks — which
    /// is the state qualification scenarios 3 and 15 need and could not reach.
    /// </summary>
    [Fact]
    public void WithChecksConfigured_AnUndetectedWorkspaceHasChecks()
    {
        AnthillRuntime.WorkspaceChecks = WorkspaceCheckConfig
            .Resolve(new[] { Declared("colony_probe"), Declared("colony_lint") }).Checks;

        Assert.Equal(new[] { "colony_probe", "colony_lint" },
            CheckSource.Available(WorkspaceCapabilityManifest.None).Select(c => c.Id));
        Assert.Equal(new[] { "colony_probe", "colony_lint" },
            CheckSource.DefaultSelection(WorkspaceCapabilityManifest.None));
        Assert.NotNull(CheckSource.Find(WorkspaceCapabilityManifest.None, "colony_probe"));
        Assert.Null(CheckSource.Find(WorkspaceCapabilityManifest.None, "never_declared"));
    }

    /// <summary>
    /// Configuration REPLACES detection rather than adding to it. An operator whose repository is
    /// .NET but who verifies it some other way is stating a fact about their project; appending the
    /// detected checks back on would make the setting advisory.
    /// </summary>
    [Fact]
    public void ConfiguredChecks_ReplaceDetection_RatherThanJoiningIt()
    {
        var detected = new WorkspaceCapabilityManifest(
            Root: "/tmp/x", ProjectTypes: new[] { "dotnet" },
            Checks: new[] { CheckCatalog.Get("dotnet_build")! },
            AdapterVersions: new Dictionary<string, string>());

        AnthillRuntime.WorkspaceChecks = Array.Empty<CheckDefinition>();
        Assert.Equal(new[] { "dotnet_build" }, CheckSource.Available(detected).Select(c => c.Id));

        AnthillRuntime.WorkspaceChecks = WorkspaceCheckConfig.Resolve(new[] { Declared("colony_probe") }).Checks;
        Assert.Equal(new[] { "colony_probe" }, CheckSource.Available(detected).Select(c => c.Id));
        Assert.Null(CheckSource.Find(detected, "dotnet_build"));
    }

    /// <summary>
    /// Which source answered is RECORDED. "0 failures" against a check set nobody can identify is
    /// the shape of claim this repository keeps finding, so the runner's refusal message and the
    /// startup line both name the source.
    /// </summary>
    [Fact]
    public void TheSourceOfTheChecks_IsNameable()
    {
        AnthillRuntime.WorkspaceChecks = Array.Empty<CheckDefinition>();
        Assert.Contains("compiled catalog", CheckSource.Describe(WorkspaceCapabilityManifest.None));

        AnthillRuntime.WorkspaceChecks = WorkspaceCheckConfig.Resolve(new[] { Declared("p") }).Checks;
        Assert.Contains("operator configuration", CheckSource.Describe(WorkspaceCapabilityManifest.None));
    }

    /// <summary>
    /// ONE DECISION FUNCTION, asserted structurally — the invariant the two old spellings broke.
    ///
    /// `RunAllowlistedCheckTool` resolved with `manifest.Find(id) ?? CheckCatalog.Get(id)` while
    /// `TesterAnt` selected with `manifest.IsEmpty ? CheckCatalog.Ids : manifest.Checks`, and the
    /// runner's own comment names the failure that invites: "Two components disagreeing about which
    /// catalog is authoritative is how a tester selects an id the runner then refuses." Adding a
    /// third source to both by hand would have been a third chance to disagree.
    /// </summary>
    [Fact]
    public void NeitherCallSite_SpellsThePrecedenceItself()
    {
        var offenders = new List<string>();

        foreach (var (file, path) in new[]
                 {
                     ("CheckRunner.cs", Path.Combine("src", "Anthill.Core", "Tools", "CheckRunner.cs")),
                     ("SpecialistAnts.cs", Path.Combine("src", "Anthill.Core", "Agents", "SpecialistAnts.cs")),
                 })
        {
            // Comments blanked: both files explain the old spelling in order to document it.
            var code = SourceText.CodeOnly(File.ReadAllText(Path.Combine(SourceText.RepoRoot(), path)));

            if (code.Contains("manifest.IsEmpty", StringComparison.Ordinal)
             || code.Contains("manifest.Find(", StringComparison.Ordinal)
             || code.Contains("CheckCatalog.Ids", StringComparison.Ordinal))
                offenders.Add(file);
        }

        Assert.True(offenders.Count == 0,
            "these files decide which checks exist instead of asking CheckSource: "
          + string.Join(", ", offenders)
          + ". Selection and resolution must read one function, or a tester selects an id the "
          + "runner refuses — which is what the runner's own comment warns about, and what "
          + "operator configuration would have made a three-way disagreement.");
    }

    // -----------------------------------------------------------------------------------------------
    // The security direction
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// THE CHECKS COME FROM THE OPERATOR'S CONFIGURATION, NOT THE WORKSPACE. This is the whole reason
    /// there is no `.anthill-checks.json`: `WorkspaceAdapter`'s doc says keeping detection and
    /// execution apart is "what stops an agent that can edit a repository from editing the thing that
    /// checks it", and a check file inside the tree would have handed every coding agent the power to
    /// rewrite its own exam. The convenient design was the unsafe one.
    /// </summary>
    [Fact]
    public void NothingReadsCheckDeclarations_FromTheWorkspaceUnderModification()
    {
        var source = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "Tools", "CheckSource.cs")));

        // It reads configuration and the manifest. It does not touch the filesystem.
        Assert.Contains("AnthillRuntime.WorkspaceChecks", source);
        foreach (var forbidden in new[] { "File.ReadAllText", "File.Exists", "Directory.", "Path.Combine" })
            Assert.False(source.Contains(forbidden, StringComparison.Ordinal),
                $"CheckSource reads the filesystem ({forbidden}). Check declarations must come from "
              + "the operator's configuration; a declaration read out of the workspace is one the "
              + "agent modifying that workspace can rewrite.");
    }

    /// <summary>
    /// And a patch that proposes editing the setting is a BLOCKING finding, like every other
    /// allowlist edit. The configuration lives outside the workspace so a mission cannot reach it;
    /// this is the second lock, for a mission proposing a patch against the colony's own tree.
    /// </summary>
    [Fact]
    public void APatchTouchingTheCheckConfiguration_IsABlockingFinding()
    {
        var finding = PolicyScan
            .Scan("--- a/anthill.config.json\n+  \"workspace_checks\": [ { \"id\": \"always_green\" } ]")
            .FirstOrDefault(f => f.RuleId == "allowlist_tampering");

        Assert.True(finding is not null,
            "a patch editing workspace_checks produced no allowlist_tampering finding. Operator "
          + "checks REPLACE detection, so editing them edits what verifies the colony's own work — "
          + "a strictly larger prize than the allowlists this rule already names.");
        Assert.True(finding!.Blocking);
    }
}
