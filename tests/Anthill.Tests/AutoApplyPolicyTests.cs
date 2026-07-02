using Anthill.Core.Autonomy;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Phase 5 gated auto-apply eligibility (v1.8.15). The policy is the fail-closed front-line gate:
/// a patch is eligible only when the master switch is on, the change is add/modify, its path
/// matches an operator glob, and it's within the size cap. Everything else denies. Runtime knobs
/// are saved/restored around every test.
/// </summary>
[Collection("Autonomy")]
public class AutoApplyPolicyTests : IDisposable
{
    private readonly bool _enabled;
    private readonly System.Collections.Generic.List<string> _paths;
    private readonly int _maxLines;

    public AutoApplyPolicyTests()
    {
        AnthillRuntime.Initialize();
        _enabled = AnthillRuntime.AutonomyAutoApplyEnabled;
        _paths = AnthillRuntime.AutonomyAutoApplyPaths;
        _maxLines = AnthillRuntime.AutonomyAutoApplyMaxLines;

        AnthillRuntime.AutonomyAutoApplyEnabled = true;
        AnthillRuntime.AutonomyAutoApplyPaths = new() { "docs/**", "src/**/*.cs" };
        AnthillRuntime.AutonomyAutoApplyMaxLines = 40;
    }

    public void Dispose()
    {
        AnthillRuntime.AutonomyAutoApplyEnabled = _enabled;
        AnthillRuntime.AutonomyAutoApplyPaths = _paths;
        AnthillRuntime.AutonomyAutoApplyMaxLines = _maxLines;
    }

    private static PatchProposal Patch(string path, PatchChangeType type = PatchChangeType.Modify, int lines = 3)
    {
        var content = string.Join("\n", Enumerable.Range(0, lines).Select(i => $"line {i}"));
        return new PatchProposal { FilePath = path, ChangeType = type, OldContent = "x", NewContent = content };
    }

    [Fact]
    public void EligibleWhenAllConditionsMet()
    {
        Assert.True(AutoApplyPolicy.Evaluate(Patch("docs/AUTONOMY.md")).Eligible);
        Assert.True(AutoApplyPolicy.Evaluate(Patch("src/Anthill.Api/ApiHost.cs")).Eligible);
        Assert.True(AutoApplyPolicy.Evaluate(Patch("docs/AUTONOMY.md", PatchChangeType.Add)).Eligible);
    }

    [Fact]
    public void DeniedWhenDisabled()
    {
        AnthillRuntime.AutonomyAutoApplyEnabled = false;
        Assert.False(AutoApplyPolicy.Evaluate(Patch("docs/AUTONOMY.md")).Eligible);
    }

    [Fact]
    public void DeniedWhenAllowlistEmpty()
    {
        AnthillRuntime.AutonomyAutoApplyPaths = new();
        var d = AutoApplyPolicy.Evaluate(Patch("docs/AUTONOMY.md"));
        Assert.False(d.Eligible);
        Assert.Contains("allowlist", d.Reason);
    }

    [Fact]
    public void DeniedWhenPathNotAllowlisted()
    {
        Assert.False(AutoApplyPolicy.Evaluate(Patch("deploy/lxc/setup.sh")).Eligible);      // not under docs/ or src/**/*.cs
        Assert.False(AutoApplyPolicy.Evaluate(Patch("src/Anthill.Api/Ui/index.html")).Eligible); // .html, not .cs
    }

    [Fact]
    public void DeniedForDeleteAndRename()
    {
        Assert.False(AutoApplyPolicy.Evaluate(Patch("docs/AUTONOMY.md", PatchChangeType.Delete)).Eligible);
        Assert.False(AutoApplyPolicy.Evaluate(Patch("docs/AUTONOMY.md", PatchChangeType.Rename)).Eligible);
    }

    [Fact]
    public void DeniedWhenOverSizeCap()
    {
        var d = AutoApplyPolicy.Evaluate(Patch("src/Anthill.Api/Big.cs", lines: 41));
        Assert.False(d.Eligible);
        Assert.Contains("cap", d.Reason);
        Assert.True(AutoApplyPolicy.Evaluate(Patch("src/Anthill.Api/Ok.cs", lines: 40)).Eligible);
    }

    [Theory]
    [InlineData("docs/**", "docs/AUTONOMY.md", true)]
    [InlineData("docs/**", "docs/sub/deep.md", true)]
    [InlineData("docs/**", "docs", true)]
    [InlineData("docs/**", "src/x.cs", false)]
    [InlineData("src/**/*.cs", "src/a/b/c.cs", true)]
    [InlineData("src/**/*.cs", "src/a.cs", true)]
    [InlineData("src/**/*.cs", "src/a/b.txt", false)]
    [InlineData("*.md", "README.md", true)]
    [InlineData("*.md", "docs/x.md", false)] // single * does not cross '/'
    public void GlobMatching(string glob, string path, bool expected)
    {
        Assert.Equal(expected, AutoApplyPolicy.GlobMatches(glob, path));
    }
}
