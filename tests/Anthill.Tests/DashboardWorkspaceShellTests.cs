using System.Text.RegularExpressions;
using Anthill.Core.Configuration;
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

    /// <summary>
    /// v2.15.0: this assertion INVERTS deliberately. It previously required the workspace to
    /// default OFF, which was correct while the track was mid-build. The track is now complete and
    /// the topology-first workspace is the console, so the default is ON.
    ///
    /// This is a behaviour change that was asked for, not a test relaxed to get a green build — so
    /// the guard is strengthened rather than dropped: the flag must still be exposed to the client,
    /// must still be settable, and turning it off must still be a real rollback path.
    /// </summary>
    [Fact]
    public void FeatureFlag_IsExposedToTheClient_AndDefaultsOn()
    {
        Assert.Contains("dashboard_workspace_enabled", Src("src", "Anthill.Api", "ApiHost.cs"));
        Assert.Contains("EnableDashboardWorkspace = true", Src("src", "Anthill.Core", "Configuration", "AnthillRuntime.cs"));
        Assert.Contains("\"dashboard_workspace_enabled\": true", Src("config.example.json"));

        // The switch must remain a switch. If these disappear, "default on" has quietly become
        // "always on" and the documented instant rollback no longer exists.
        var runtime = Src("src", "Anthill.Core", "Configuration", "AnthillRuntime.cs");
        Assert.Contains("config.DashboardWorkspaceEnabled ?? true", runtime);
        Assert.Contains("dashboard_workspace_enabled", Src("src", "Anthill.Core", "Configuration", "AnthillConfig.cs"));
    }

    /// <summary>
    /// An operator who deliberately turned the workspace off must not have it switched back on by
    /// an upgrade. The config property is nullable so "absent" (pre-dates the setting, take the
    /// new default) is distinguishable from "explicitly false" (respect it).
    /// </summary>
    [Fact]
    public void ExplicitlyDisabledWorkspace_SurvivesTheDefaultFlip()
    {
        var cfg = Src("src", "Anthill.Core", "Configuration", "AnthillConfig.cs");
        Assert.Contains("public bool? DashboardWorkspaceEnabled", cfg);

        var config = new AnthillConfig { DashboardWorkspaceEnabled = false };
        Assert.False(config.DashboardWorkspaceEnabled ?? true);      // explicit off stays off
        var unset = new AnthillConfig();
        Assert.Null(unset.DashboardWorkspaceEnabled);                // unset means unset
        Assert.True(unset.DashboardWorkspaceEnabled ?? true);        // and resolves to the new default
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
        // Target the ACTION HANDLER specifically — the same literal also appears on the toolbar button.
        var reset = BodyOf(js, "'reset-layout': function");
        // Assert against CODE, not prose: the handler's comment legitimately names the things it
        // must never touch, and a comment mentioning them is not the same as code touching them.
        var code = string.Join("\n", reset.Split('\n')
            .Select(l => { var i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l[..i] : l; }));
        Assert.Contains("profiles", code);
        Assert.DoesNotContain("ants", code);       // colony customization is never in scope
        Assert.DoesNotContain("positions", code);
    }

    [Fact]
    public void SavePreservesOtherUiStateKeys()
    {
        var js = Ui("dashboard-workspace.js");
        var save = BodyOf(js, "function save()");

        // v2.15.0: save() no longer runs its own GET/PUT cycle — it registers a mutator with the
        // single writer in app.js, because two independent debounced writers on the same document
        // raced and the later PUT discarded the earlier one's change.
        //
        // The invariant this test protects is unchanged, and now checked more strictly than the
        // old literal-match allowed: only the dashboard_workspace key is written, keys already
        // inside it survive (topology_overlays belongs to app.js), and the document is never
        // replaced wholesale on any path.
        Assert.Contains("window.AnthillUiState", save);
        Assert.Contains("doc.dashboard_workspace = Object.assign({}, doc.dashboard_workspace, W.state)", save);
        Assert.DoesNotContain("doc = W.state", save);
        Assert.Contains("await window.api('/ui/state')", js);   // the fallback still reads before writing
    }

    // ---- Stage 4: tab groups ------------------------------------------------------------------------

    /// <summary>
    /// v2.15.0: groups reuse the Stage 3 gesture machinery through a "g:&lt;id&gt;" reference rather
    /// than getting a second drag/resize/snap implementation. If placement()/setPlacement() stop
    /// translating those refs, groups become undraggable and unresizable in a way no unit test of
    /// the C# state model would notice.
    /// </summary>
    [Fact]
    public void TabGroups_ReuseTheExistingGestureMachinery()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Contains("function isGroupRef", js);
        Assert.Contains("'g:' + gid", js);
        // Both translators must handle the ref, or dragging a group silently writes nowhere.
        var pl = js[js.IndexOf("function placement(id)", StringComparison.Ordinal)..];
        Assert.Contains("isGroupRef(id)", pl[..400]);
        var sp = js[js.IndexOf("function setPlacement(id, changes)", StringComparison.Ordinal)..];
        Assert.Contains("isGroupRef(id)", sp[..400]);
        // z-order must consider groups, else a group can never be raised above a floating panel.
        var bf = js[js.IndexOf("function bringToFront(id)", StringComparison.Ordinal)..];
        Assert.Contains("tabGroups()", bf[..400]);
    }

    /// <summary>
    /// Only the active tab's body is rendered. Rendering hidden tabs would quietly break the
    /// refreshPolicy:'visible' contract — a stacked panel must be genuinely paused, not just
    /// covered up, or grouping panels would multiply polling instead of reducing it.
    /// </summary>
    [Fact]
    public void TabGroups_RenderOnlyTheActivePanel()
    {
        var js = Ui("dashboard-workspace.js");
        var body = BodyOf(js, "function renderTabGroup");
        Assert.Contains("var def = W.panels[active]", body);
        Assert.Contains("def.render(body)", body);
        // Exactly one render call in the group frame: the active panel's.
        Assert.Equal(1, Regex.Matches(body, @"\.render\(body\)").Count);
    }

    /// <summary>
    /// Every drag-only capability needs a non-drag equivalent (accessibility rule), and the
    /// tablist must follow the WAI-ARIA tabs pattern.
    /// </summary>
    [Fact]
    public void TabGroups_AreKeyboardOperable()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Contains("'role', 'tablist'", js);
        Assert.Contains("'role', 'tab'", js);
        Assert.Contains("'aria-selected'", js);
        Assert.Contains("'role', 'tabpanel'", js);
        Assert.Contains("tabindex", js);                       // roving tabindex
        Assert.Contains("function onTabKeydown", js);
        Assert.Contains("ArrowLeft", js);
        Assert.Contains("ArrowRight", js);
        // Grouping and detaching must both be reachable without a pointer drag.
        Assert.Contains("'group-with'", js);
        Assert.Contains("'tab-detach'", js);
        Assert.Contains("ws-module-sub", js);                  // the menu entries that expose them
    }

    /// <summary>
    /// The client must mirror the server rule that a group below two members dissolves, rather
    /// than leaving a one-tab stack on screen until the next reload repairs it.
    /// </summary>
    [Fact]
    public void TabGroups_DissolveBelowTwoMembers_OnTheClientToo()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Contains("function dissolveGroup", js);
        var detach = js[js.IndexOf("'tab-detach': function", StringComparison.Ordinal)..];
        Assert.Contains("dissolveGroup(gid)", detach[..900]);
        Assert.Contains("g.panels.length < 2", detach[..900]);
    }

    // ---- Docking (v2.15.0) -------------------------------------------------------------------------

    /// <summary>
    /// Docking was deferred in the original plan precisely because hand-rolled dock geometry is
    /// where window managers accumulate bugs. It shipped only because the geometry that matters —
    /// how thick a rail may get — lives in tested C#. The client must therefore keep its own
    /// arithmetic thin: rails lay out with flexbox and the only stored number is dock_size.
    /// </summary>
    [Fact]
    public void DockRails_KeepGeometryInTheServer()
    {
        var js = Ui("dashboard-workspace.js");
        var rail = BodyOf(js, "function renderDockRail");
        // The rail sets ONE dimension from state; everything else is flex.
        Assert.Contains("railSize(side)", rail);
        Assert.Contains("rail.style.width", rail);
        Assert.Contains("rail.style.height", rail);
        // Docked panels must not carry absolute placement — the rail positions them.
        Assert.DoesNotContain("frame.style.left", rail);
        Assert.DoesNotContain("frame.style.top", rail);
        var css = Ui("dashboard-workspace.css");
        Assert.Contains(".ws-panel.ws-docked", css);
        Assert.Contains("position: relative", css);
    }

    /// <summary>
    /// A dock rail must never be able to bury the topology. The server clamp is authoritative, but
    /// the client mirrors it during the drag so the rail feels bounded instead of snapping back
    /// after the round trip — and the two limits must agree.
    /// </summary>
    [Fact]
    public void DockRailResize_MirrorsTheServerFractionLimit()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Contains("0.60", js);
        Assert.Contains("Math.max(180", js);
        // The same numbers the server enforces.
        Assert.Equal(0.60, DashboardWorkspaceState.MaxDockFraction);
        Assert.Equal(180, DashboardWorkspaceState.MinDockSize);
    }

    /// <summary>
    /// Docking is offered on an explicit aim at an edge, and must be resolved BEFORE tab grouping:
    /// a panel header sitting under the edge zone is incidental, the edge is deliberate.
    /// </summary>
    [Fact]
    public void DropIntoEdgeZone_DocksBeforeItConsidersTabGrouping()
    {
        var js = Ui("dashboard-workspace.js");
        var end = BodyOf(js, "function endGesture");
        var dockAt = end.IndexOf("dockZoneAt(", StringComparison.Ordinal);
        var groupAt = end.IndexOf("dropTargetPanel(", StringComparison.Ordinal);
        Assert.True(dockAt > 0 && groupAt > 0, "endGesture must consider both docking and grouping");
        Assert.True(dockAt < groupAt, "the dock zone must be checked before the tab-group target");
    }

    /// <summary>Every drag-only capability needs a non-drag equivalent (accessibility rule).</summary>
    [Fact]
    public void DockingIsReachableWithoutADrag()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Contains("'dock-to'", js);
        Assert.Contains("'undock'", js);
        var menu = BodyOf(js, "function renderModules");
        Assert.Contains("dock-to", menu);
        Assert.Contains("undock", menu);
    }

    /// <summary>Drop hints appear only during a drag, and are cleared on every exit path.</summary>
    [Fact]
    public void DockHints_AreTransient()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Contains("function clearDockHint", js);
        Assert.Contains("clearDockHint()", BodyOf(js, "function endGesture"));
        var css = Ui("dashboard-workspace.css");
        Assert.Contains(".ws-dockzones { position: absolute; inset: 0;", css.Replace("\r", ""));
        Assert.Contains("opacity: 0", css);          // invisible until a drag turns them on
        Assert.Contains(".ws-dockzones.on", css);
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

    // ---- Stage 3: drag, resize, pointer arbitration ---------------------------------------------------

    [Fact]
    public void Gestures_UsePointerEvents_NotSeparateMouseAndTouchPaths()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Contains("pointerdown", js);
        Assert.Contains("pointermove", js);
        Assert.Contains("pointerup", js);
        Assert.Contains("pointercancel", js);          // a cancelled gesture must not strand state
        Assert.DoesNotContain("mousedown", js);        // one code path only — no double-fire
        Assert.DoesNotContain("touchstart", js);
    }

    [Fact]
    public void LockedLayout_EngagesNoGesture_SoTheMapKeepsIt()
    {
        var js = Ui("dashboard-workspace.js");
        var begin = BodyOf(js, "function beginGesture");
        Assert.Contains("W.state.locked", begin);
        Assert.Contains("return", begin);
    }

    [Fact]
    public void DragStoppsPropagation_SoTheTopologyNeverPans()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Contains("stopPropagation", js);
        Assert.Contains("preventDefault", js);
    }

    [Fact]
    public void HeaderButtons_AreExcludedFromDragging()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Contains("closest('button')", js);      // a control click is never a drag
    }

    /// <summary>
    /// Slice a braced body (function OR block) out of dashboard-workspace.js by matching braces
    /// from the signature, rather than by a fixed character count.
    ///
    /// v2.15.0: this used to take a magic 600/1400 characters, which silently stopped covering the
    /// intended function the moment anything was added to it — the assertion then passed or failed
    /// on where the window happened to land rather than on the behaviour it names.
    /// </summary>
    private static string BodyOf(string js, string signature)
    {
        var start = js.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found");
        var open = js.IndexOf('{', start);
        Assert.True(open > 0, signature + " has no body");
        var depth = 0;
        for (var i = open; i < js.Length; i++)
        {
            if (js[i] == '{') depth++;
            else if (js[i] == '}' && --depth == 0) return js[start..(i + 1)];
        }
        Assert.Fail(signature + " body is unbalanced");
        return string.Empty;
    }

    [Fact]
    public void MovementUsesAnimationFrame_AndPersistsOnceAtEnd()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Contains("requestAnimationFrame", js);
        Assert.Contains("cancelAnimationFrame", js);

        // Saved at pointerup, exactly once — never per frame.
        var end = BodyOf(js, "function endGesture");
        Assert.Contains("setPlacement", end);
        Assert.Equal(1, Regex.Matches(end, @"setPlacement\(").Count);

        var move = BodyOf(js, "function moveGesture");
        Assert.DoesNotContain("setPlacement", move);
        Assert.DoesNotContain("save(", move);          // nor any other write path
    }

    [Fact]
    public void SnappingCanBeBypassedWithAModifier()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Contains("altKey", js);
        Assert.Contains("metaKey", js);
        Assert.Contains("bypass", js);
    }

    [Fact]
    public void DraggedPanel_CannotBeLostOffScreen()
    {
        var js = Ui("dashboard-workspace.js");
        // The clamp lives in moveGesture's move branch; BodyOf brace-matches that block.
        var move = BodyOf(js, "if (drag.mode === 'move')");
        Assert.Contains("Math.max", move);
        Assert.Contains("Math.min", move);
        Assert.Contains("64", move);                   // grabbable header edge stays reachable
    }

    [Fact]
    public void ResizeRespectsMinimums()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Contains("MIN_W", js);
        Assert.Contains("MIN_H", js);
    }

    [Fact]
    public void ResizeHandlesAppearOnlyInCustomizeMode()
    {
        var css = Ui("dashboard-workspace.css");
        Assert.Contains(".ws-resize", css);
        Assert.Contains(".ws-customize .ws-resize { display: block; }", css);
        Assert.Contains("display: none", css);
        Assert.Contains("touch-action: none", css);    // browser gestures don't fight the drag
    }

    [Fact]
    public void ProfileBreakpoint_MatchesTheServerConstant()
    {
        Assert.Contains("PROFILE_BREAKPOINT = 900", Ui("dashboard-workspace.js"));
        Assert.Contains("CompactBreakpoint = 900", Src("src", "Anthill.Core", "Configuration", "DashboardWorkspaceState.cs"));
    }
}
