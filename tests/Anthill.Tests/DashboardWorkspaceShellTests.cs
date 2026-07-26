using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.14.3 Stage 2 gate — static integrity of the workspace shell. Interaction itself is verified
/// by the manual walkthrough (this repo has no browser harness, and adding one would contradict
/// the no-build-system constraint), so these tests pin the properties that CAN be proven from the
/// source: CSP compliance, wiring completeness, delegated dispatch, and a11y affordances.
/// </summary>
public class DashboardWorkspaceShellTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Ui(string file) => File.ReadAllText(Path.Combine(Root(), "src", "Anthill.Api", "Ui", file));
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Root() }.Concat(parts).ToArray()));

    // ---- Files exist and are wired end to end ---------------------------------------------------

    [Fact]
    public void WorkspaceAssets_Exist()
    {
        Assert.True(File.Exists(Path.Combine(Root(), "src", "Anthill.Api", "Ui", "dashboard-workspace.js")));
        Assert.True(File.Exists(Path.Combine(Root(), "src", "Anthill.Api", "Ui", "dashboard-workspace.css")));
    }

    [Fact]
    public void WorkspaceAssets_AreEmbedded_Served_AndReferencedByThePage()
    {
        var csproj = Src("src", "Anthill.Api", "Anthill.Api.csproj");
        Assert.Contains("Ui\\dashboard-workspace.js", csproj);
        Assert.Contains("Ui\\dashboard-workspace.css", csproj);

        var host = Src("src", "Anthill.Api", "ApiHost.cs");
        Assert.Contains("/ui/dashboard-workspace.js", host);
        Assert.Contains("/ui/dashboard-workspace.css", host);
        Assert.Contains("LoadUiAsset(\"dashboard-workspace.js\")", host);

        var page = Ui("index.html");
        Assert.Contains("/ui/dashboard-workspace.js", page);
        Assert.Contains("/ui/dashboard-workspace.css", page);
    }

    [Fact]
    public void FeatureFlag_IsExposedToTheClient_AndDefaultsOff()
    {
        Assert.Contains("dashboard_workspace_enabled", Src("src", "Anthill.Api", "ApiHost.cs"));
        Assert.Contains("EnableDashboardWorkspace = false", Src("src", "Anthill.Core", "Configuration", "AnthillRuntime.cs"));
        Assert.Contains("\"dashboard_workspace_enabled\": false", Src("config.example.json"));
    }

    // ---- CSP: the console must carry no inline JavaScript -----------------------------------------

    [Fact]
    public void WorkspaceRuntime_UsesNoInlineHandlers()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.DoesNotContain("onclick=", js);
        Assert.DoesNotContain("innerHTML", js);          // no HTML injection surface either
        Assert.Contains("data-wsact", js);               // delegated dispatch instead
        Assert.Contains("document.addEventListener('click'", js);
    }

    [Fact]
    public void CspRemains_ScriptSrcSelf_WithoutUnsafeInline()
    {
        var host = Src("src", "Anthill.Api", "ApiHost.cs");
        Assert.Contains("script-src 'self'", host);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", host);
    }

    // ---- Shell capabilities -------------------------------------------------------------------------

    [Theory]
    [InlineData("toggle-collapse")] [InlineData("toggle-pin")] [InlineData("minimize")]
    [InlineData("hide")] [InlineData("restore")] [InlineData("toggle-visible")]
    [InlineData("toggle-lock")] [InlineData("toggle-focus")] [InlineData("toggle-modules")]
    [InlineData("reset-layout")]
    public void EveryDeclaredAction_HasAHandler(string action)
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Matches(new Regex(@"'" + Regex.Escape(action) + @"'\s*:\s*function"), js);
    }

    [Fact]
    public void CollapseMinimizeHide_AreDistinctStates()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Contains("'collapsed'", js);
        Assert.Contains("'minimized'", js);
        Assert.Contains("'hidden'", js);
        // Hidden panels leave the tray entirely; minimized ones populate it.
        Assert.Contains("display_state === 'minimized'", js);
    }

    [Fact]
    public void SaveIsDebounced_NotPerInteractionFrame()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Contains("setTimeout", js);
        Assert.Contains("clearTimeout", js);
    }

    [Fact]
    public void ResetLayout_TouchesOnlyTheWorkspaceKey()
    {
        var js = Ui("dashboard-workspace.js");
        var reset = js[js.IndexOf("'reset-layout'", StringComparison.Ordinal)..];
        reset = reset[..Math.Min(reset.Length, 600)];
        Assert.Contains("profiles", reset);
        Assert.DoesNotContain("ants", reset);      // colony customization is never in scope
        Assert.DoesNotContain("positions", reset);
    }

    [Fact]
    public void SavePreservesOtherUiStateKeys()
    {
        var js = Ui("dashboard-workspace.js");
        // The save path reads the current document and replaces ONLY dashboard_workspace.
        Assert.Contains("doc.dashboard_workspace = W.state", js);
        Assert.Contains("await window.api('/ui/state')", js);
    }

    // ---- Accessibility --------------------------------------------------------------------------------

    [Fact]
    public void ControlsAreRealButtons_WithLabelsAndState()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Contains("createElement('button')", js.Replace("el('button'", "createElement('button')"));
        Assert.Contains("aria-label", js);
        Assert.Contains("aria-pressed", js);
        Assert.Contains("aria-expanded", js);
        Assert.Contains("b.type = 'button'", js);   // never a submit inside a form
    }

    [Fact]
    public void FocusStyles_AndReducedMotion_ArePresent()
    {
        var css = Ui("dashboard-workspace.css");
        Assert.Contains(":focus-visible", css);
        Assert.Contains("prefers-reduced-motion", css);
    }

    [Fact]
    public void OpacityPresets_DimTheScrim_NotTheText()
    {
        var css = Ui("dashboard-workspace.css");
        // Presets adjust panel background alpha; there is no rule making text translucent.
        Assert.Contains("[data-opacity=\"low\"]", css);
        Assert.DoesNotContain(".ws-body { opacity", css);
        Assert.DoesNotContain(".ws-title { opacity", css);
    }

    [Fact]
    public void CompactProfile_DisablesFreeDragging()
    {
        var css = Ui("dashboard-workspace.css");
        Assert.Contains("@media (max-width: 899px)", css);
        Assert.Contains("position: static !important", css);
    }

    [Fact]
    public void ProfileBreakpoint_MatchesTheServerConstant()
    {
        Assert.Contains("PROFILE_BREAKPOINT = 900", Ui("dashboard-workspace.js"));
        Assert.Contains("CompactBreakpoint = 900", Src("src", "Anthill.Core", "Configuration", "DashboardWorkspaceState.cs"));
    }
}
