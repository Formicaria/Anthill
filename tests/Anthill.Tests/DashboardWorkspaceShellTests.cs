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
        Assert.Single(Regex.Matches(body, @"\.render\(body\)"));
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

    // ---- Snapping + full-bleed layout (v2.15.1) ----------------------------------------------------

    /// <summary>
    /// v2.15.1: docking is gone from the client entirely. Leaving half of it behind is how a
    /// codebase ends up with two overlapping concepts and a menu full of dead entries.
    /// </summary>
    [Fact]
    public void DockingIsFullyRemovedFromTheClient()
    {
        var js = Ui("dashboard-workspace.js");
        var css = Ui("dashboard-workspace.css");
        foreach (var token in new[] { "DOCK_SIDES", "dockedOn", "renderDockRail", "dockPanel",
                                      "dockZoneAt", "railDrag", "data-wsdockresize", "'dock-to'", "'undock'" })
            Assert.False(js.Contains(token), $"dashboard-workspace.js still references removed docking: {token}");
        Assert.DoesNotContain(".ws-dock", css);
    }

    /// <summary>The client's snap arithmetic must match the server's, or a snap jumps on reload.</summary>
    [Fact]
    public void ClientSnapRegions_MatchTheServer()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Contains("function snapRegion(zone, vw, vh)", js);
        Assert.Contains("MIN_PANEL_W = 200", js);
        Assert.Contains("MIN_PANEL_H = 80", js);
        Assert.Equal(200, DashboardWorkspaceState.MinPanelWidth);
        Assert.Equal(80, DashboardWorkspaceState.MinPanelHeight);

        // Every zone the server knows must be handled by the client and vice versa.
        var body = BodyOf(js, "function snapRegion(zone, vw, vh)");
        foreach (var zone in DashboardWorkspaceState.SnapZones)
            Assert.Contains("case '" + zone + "'", body);
        Assert.Contains("SNAP_ZONES = ['left', 'right', 'top', 'bottom', 'top-left', 'top-right', 'bottom-left', 'bottom-right']", js);
    }

    /// <summary>A corner is a deliberate aim at a quadrant, and sits inside both edge bands.</summary>
    [Fact]
    public void CornerSnapZones_WinOverEdges()
    {
        var js = Ui("dashboard-workspace.js");
        var hit = BodyOf(js, "function snapZoneAt(pt)");
        var firstCorner = hit.IndexOf("'top-left'", StringComparison.Ordinal);
        var firstEdge = hit.IndexOf("return 'left'", StringComparison.Ordinal);
        Assert.True(firstCorner > 0 && firstEdge > 0, "snapZoneAt must resolve both corners and edges");
        Assert.True(firstCorner < firstEdge, "corners must be tested before edges or they are unreachable");
    }

    /// <summary>Every snap zone reachable without a drag (accessibility rule).</summary>
    [Fact]
    public void SnappingIsReachableWithoutADrag()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.Contains("'snap-to'", js);
        Assert.Contains("snap-to", BodyOf(js, "function renderModules"));
    }

    /// <summary>
    /// v2.15.1: with the workspace live the Dashboard is not a scrolling document. Every classic
    /// section must be taken out of flow by ONE rule — enumerating them is how the six hud-panel
    /// cards were missed in v2.15.0 and left rendering below the map.
    /// </summary>
    [Fact]
    public void WorkspaceDashboard_TakesAllClassicContentOutOfFlow()
    {
        var html = Ui("index.html");
        Assert.Contains("#page-overview.ws-active", html);
        // v2.15.3: the rule excludes by class now, not by an id list — see
        // EveryWorkspaceLayer_SurvivesTheClassicPageHideRule for why. The property asserted here
        // is unchanged: ONE rule takes every classic section out of flow, rather than an
        // enumeration that can miss a card.
        Assert.Contains("#page-overview.ws-active > *:not(#ws-root):not(.ws-layer){display:none !important;}", html);
        Assert.Contains("classList.add('ws-active')", Ui("app.js"));
    }

    /// <summary>
    /// The status bar belongs above the colony view controls, not stranded mid-page below the map,
    /// and it must be re-parented rather than duplicated.
    /// </summary>
    [Fact]
    public void StatusBar_IsPinnedAboveTheColonyViewControls()
    {
        var app = Ui("app.js");
        var html = Ui("index.html");
        Assert.Contains("ws-topbar", app);
        Assert.Contains("topbar.appendChild(tb)", app);          // moved, not cloned
        Assert.Contains("#ws-topbar", html);
        // Top-anchored topology overlays must clear the bar instead of hiding under it.
        Assert.Contains("#ws-topology .topo-ov-slot[data-ovslot=\"top-left\"]", html);
    }

    /// <summary>
    /// The colony view bar rendered starting at "Handoffs" because the anchor slot capped width at
    /// 260px and clipped it. Width belongs to the overlays that need it, not to the slot.
    /// </summary>
    [Fact]
    public void ColonyViewBar_IsNotClippedByItsAnchorSlot()
    {
        var html = Ui("index.html");
        Assert.Contains(".topo-ov-slot{position:absolute;z-index:6;display:flex;flex-direction:column;gap:8px;max-width:none;", html);
        Assert.Contains("#chud-legend,#chud-phero{max-width:260px;}", html);
        Assert.Contains("#colony-viewbar{flex-wrap:nowrap;", html);
    }

    /// <summary>
    /// v2.15.2: the workspace's absolute layers need a containing block on #page-overview.
    ///
    /// Neither #main-area nor .page carries a position, so without this the layers resolved against
    /// the INITIAL containing block — the whole viewport — and rendered underneath the nav sidebar
    /// and past the bottom edge. Symptoms were the caste legend and colony view bar clipped on the
    /// left and the mission directive box pushed off screen. Nothing about that is obvious from
    /// reading the rules individually, which is why it is pinned here.
    /// </summary>
    [Fact]
    public void WorkspaceLayers_HaveAContainingBlock()
    {
        var html = Ui("index.html");
        Assert.Contains("#page-overview.ws-active{position:relative;", html);

        // The layers that depend on it.
        foreach (var layer in new[] { "#ws-topbar{position:absolute", "#ws-bottombar{position:absolute" })
            Assert.Contains(layer, html);
        Assert.Contains(".ws-topology { position: absolute; inset: 0;", Ui("dashboard-workspace.css"));
    }

    /// <summary>
    /// The fixed chrome bands must not overlap. The status bar outranks the toolbar on z-index, so
    /// an overlap does not look like an overlap — the toolbar simply vanishes.
    /// </summary>
    [Fact]
    public void FixedChromeBands_DoNotOverlap()
    {
        var html = Ui("index.html");
        // status bar occupies 0-52, toolbar starts at 58, overlay slots start at 96.
        Assert.Contains("#page-overview.ws-active .ws-toolbar{top:58px;}", html);
        Assert.Contains("#page-overview.ws-active .ws-modules{top:94px;", html);
        Assert.Contains("#ws-topology .topo-ov-slot[data-ovslot=\"top-right\"]{top:96px;}", html);
        // and the bottom band clears the mission bar.
        Assert.Contains("#ws-topology .topo-ov-slot[data-ovslot=\"bottom-right\"]{bottom:62px;}", html);
    }

    /// <summary>
    /// The mission directive box starts work, so it must never be coverable by a floating panel.
    /// It is re-parented into its own bar above the panel layer — moved, not duplicated.
    /// </summary>
    [Fact]
    public void MissionDirective_IsAboveThePanelLayer()
    {
        var app = Ui("app.js");
        var html = Ui("index.html");
        Assert.Contains("bottombar.appendChild(mb)", app);
        Assert.Contains("#ws-bottombar{position:absolute;left:0;right:0;bottom:0;z-index:46", html);
        Assert.Single(Regex.Matches(html, @"id=""mission-bar"""));           // one instance, re-parented
    }

    /// <summary>
    /// v2.15.2: overlay show/hide and anchoring live in the Modules menu, not a separate button.
    ///
    /// Two surfaces controlling what is on screen is how they drift. The menu sits in the
    /// always-present toolbar, which preserves the one property the standalone button existed for:
    /// hiding every overlay must stay recoverable.
    /// </summary>
    [Fact]
    public void TopologyOverlays_AreControlledFromTheModulesMenu()
    {
        var app = Ui("app.js");
        var ws = Ui("dashboard-workspace.js");
        var html = Ui("index.html");

        // The standalone control is gone from every surface.
        foreach (var token in new[] { "topo-ov-btn", "topo-ov-menu", "toggleOverlayMenu", "renderOverlayMenu" })
        {
            Assert.False(app.Contains(token), $"app.js still references the removed overlay menu: {token}");
            Assert.False(html.Contains(token), $"index.html still references the removed overlay menu: {token}");
        }

        // A single shared bridge, so neither module keeps its own copy of overlay state.
        Assert.Contains("window.AnthillTopologyOverlays", app);
        Assert.Contains("window.AnthillTopologyOverlays", ws);

        // Hide/show AND re-anchor, both from the menu.
        Assert.Contains("'toggle-overlay'", ws);
        Assert.Contains("'reset-overlays'", ws);
        Assert.Contains("data-wsanchor", ws);
        var menu = BodyOf(ws, "function renderModules");
        Assert.Contains("toggle-overlay", menu);
        Assert.Contains("ws-module-ovanchor", menu);
        Assert.Contains("aria-pressed", menu);
    }

    /// <summary>
    /// v2.15.3: every workspace layer appended to #page-overview must survive the "hide the
    /// classic page" rule.
    ///
    /// v2.15.2 shipped with the status bar and the mission directive box invisible: the rule was
    /// an id allow-list (`:not(#ws-root):not(#ws-topology)`) and both bars are direct children of
    /// #page-overview, so both were display:none. Nothing caught it, because each rule reads
    /// correctly on its own — the bug only exists in the relationship between them.
    ///
    /// The rule now excludes by class, and this test checks the two halves agree: anything the
    /// init code attaches to the page must carry .ws-layer.
    /// </summary>
    [Fact]
    public void EveryWorkspaceLayer_SurvivesTheClassicPageHideRule()
    {
        var html = Ui("index.html");
        var app = Ui("app.js");

        Assert.Contains("#page-overview.ws-active > *:not(#ws-root):not(.ws-layer){display:none !important;}", html);

        // Find everything attached directly to `page` during workspace init.
        var init = BodyOf(app, "async function initDashboardWorkspace()");
        var attached = Regex.Matches(init, @"page\.(?:appendChild|insertBefore)\(\s*([A-Za-z_$][\w$]*)")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.True(attached.Count >= 3,
            "Expected the init to attach the topology, status bar and mission bar; found: " + string.Join(", ", attached));

        foreach (var varName in attached)
        {
            // #ws-root is matched by id because the workspace module rewrites its className.
            if (varName == "root") continue;
            Assert.True(Regex.IsMatch(app, Regex.Escape(varName) + @"\.className\s*=\s*'[^']*\bws-layer\b"),
                $"'{varName}' is attached to #page-overview but never gets the .ws-layer class, so the "
                + "classic-page hide rule will make it invisible.");
        }
    }

    /// <summary>
    /// v2.16.0: chamber layout gives every role its own angular sector.
    ///
    /// Before this, roles sat on a ring capped at 46px while their workers were placed 72px out,
    /// and the worker bearing came from colonyAngleFor() — which in chamber mode derives from the
    /// CHAMBER's index and is identical for every role in it. Each role's workers therefore landed
    /// on the neighbouring role, and the view read as a smudge.
    ///
    /// Sectors make cross-role collision geometrically impossible rather than merely unlikely, so
    /// this test pins the three properties that guarantee it. The radii must stay DERIVED from the
    /// arc length required; reintroducing a constant cap is what broke it the first time.
    /// </summary>
    [Fact]
    public void ChamberLayout_GivesEachRoleItsOwnSector()
    {
        var js = Ui("app.js");
        var build = BodyOf(js, "function buildNodes()");

        // 1. Sector width is derived from the member count.
        Assert.Contains("const sector = (Math.PI*2)/Math.max(1,n);", build);
        // 2. The role sits at its sector's CENTRE, so neighbouring sectors cannot touch.
        Assert.Contains("(k+0.5)*sector", build);
        // 3. Both radii come from required arc length, not a magic cap.
        Assert.Contains("(n*CHAMBER_ROLE_GAP)/(Math.PI*2)", build);
        Assert.Contains("(ws.length*CHAMBER_WORKER_GAP)/(slot.sector*CHAMBER_SECTOR_USE)", build);

        // Workers are positioned from the CHAMBER centre along their own role's bearing. If this
        // reverts to the role-relative global fan, the sectors stop containing anything.
        Assert.Contains("slot.cx+Math.cos(cr)*r2", build);
        Assert.DoesNotContain("roleAngleInChamber", js);

        // The sector must keep a margin, or adjacent fans meet at the boundary.
        var use = Regex.Match(js, @"CHAMBER_SECTOR_USE\s*=\s*([0-9.]+)");
        Assert.True(use.Success, "CHAMBER_SECTOR_USE not found");
        var fraction = double.Parse(use.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(fraction, 0.5, 0.9);
    }

    /// <summary>
    /// Chambers must not collide with each other either — the contents got larger, so the ring
    /// they sit on had to grow with them. Six chambers ring a central one, so adjacent centres are
    /// exactly R apart; the largest chamber measured 136px, needing 272px of clearance.
    /// </summary>
    [Fact]
    public void ChamberRing_LeavesRoomForTheLargestChamber()
    {
        var js = Ui("app.js");
        var centres = BodyOf(js, "function chamberCentres(names)");
        var m = Regex.Match(centres, @"Math\.min\(W,H\)\s*\*\s*([0-9.]+)");
        Assert.True(m.Success, "chamberCentres no longer derives its radius from the viewport");
        var factor = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        // On the shortest supported axis the ring must still clear two chamber radii.
        const int shortestAxis = 900;
        var adjacentGap = shortestAxis * factor;   // 2*R*sin(pi/6) == R for six ring positions
        Assert.True(adjacentGap >= 272,
            $"ring factor {factor} gives only {adjacentGap:F0}px between adjacent chamber centres; "
            + "the largest chamber needs 272px to avoid overlapping its neighbour.");
    }

    // ---- Missions as a conversation (v2.16.0) ------------------------------------------------------

    /// <summary>
    /// v2.16.0: the answer is the default content of a mission response; the trace is one
    /// disclosure away. The colony always stored both — this asserts the UI leads with the right
    /// one, which is the whole point of the change.
    /// </summary>
    [Fact]
    public void MissionThread_LeadsWithTheAnswer_NotTheTrace()
    {
        var js = Ui("app.js");
        var mt = Ui("mission-thread.js");

        // v2.18.1: the answer selection moved into the reconciler (answerOf) and the row patcher,
        // because rendering is no longer a single string-building pass. The property asserted is
        // unchanged: the synthesized answer is preferred, falling back to the raw best output.
        Assert.Contains("m.final_result || m.user_result", mt);

        var patch = BodyOf(js, "function msPatchExchange(row,m)");
        Assert.Contains("MT.answerOf(m)", patch);
        Assert.Contains("Working — no answer recorded yet.", patch);

        // The trace must never be rendered inline — it belongs behind the disclosure.
        Assert.DoesNotContain("debug_result", patch);
        Assert.DoesNotContain("debug_result", BodyOf(js, "function renderMissionThread()"));
        Assert.Contains("Show activity", BodyOf(js, "function msBuildExchange(m)"));
    }

    /// <summary>
    /// Forty missions in a thread must not fetch forty reports. Activity loads on first expand.
    /// </summary>
    [Fact]
    public void MissionActivity_LoadsLazily_OnFirstExpandOnly()
    {
        var js = Ui("app.js");
        Assert.Contains("data-msact", js);

        // v2.18.1: the DOM latch (dataset.loaded) was replaced by msActivity, because a latch set
        // before the request resolved could never be retried. Laziness is now expressed by
        // begin() refusing a second fetch — see MissionActivity_TracksStateOutsideTheDom.
        Assert.Contains("msActivity.begin(missionId)", BodyOf(js, "async function msLoadActivity(missionId,det)"));

        // Detail rendering still reuses the Results page implementation rather than a second copy.
        Assert.Contains("renderMissionReport(missionId,body)", js);

        // The list render must never fetch a report — forty exchanges must not mean forty requests.
        Assert.DoesNotContain("renderMissionReport", BodyOf(js, "function renderMissionThread()"));
        Assert.DoesNotContain("renderMissionReport", BodyOf(js, "function msBuildExchange(m)"));
        Assert.DoesNotContain("renderMissionReport", BodyOf(js, "async function loadMissionThread(opts)"));
    }

    /// <summary>
    /// The thread must not yank the view while history is being read.
    ///
    /// v2.18.1: this used to also assert `aria-live="polite"` on #ms-thread. That assertion was
    /// itself describing the bug — a live region on the thread re-announced all forty exchanges
    /// on every three-second poll. The announcement contract now lives in
    /// MissionThread_AnnouncesOneResult_NotTheWholeThread, which asserts the opposite and is
    /// correct. The scroll contract below is unchanged, only renamed.
    /// </summary>
    [Fact]
    public void MissionThread_IsPoliteAboutScrolling()
    {
        var js = Ui("app.js");
        var render = BodyOf(js, "function renderMissionThread()");

        // Measured BEFORE the update and applied after, so reading history is never interrupted.
        Assert.Contains("MT.shouldFollowBottom(", render);
        Assert.Contains("if(follow) thread.scrollTop=thread.scrollHeight;", render);
        Assert.Contains("else thread.scrollTop=prevTop;", render);
        Assert.Contains("const prevTop=thread.scrollTop;", render);

        // The threshold itself is exercised in tests/ui/mission-thread.test.js, both directions.
        Assert.Contains("FOLLOW_THRESHOLD_PX", Ui("mission-thread.js"));

        var html = Ui("index.html");
        Assert.Contains("id=\"ms-thread\"", html);
        Assert.Contains("role=\"log\"", html);
        // The composer stays pinned: the THREAD scrolls, not the page.
        Assert.Contains("#page-missions.page-scroll{overflow:hidden;}", html);
    }

    /// <summary>
    /// The report endpoint must expose the answer and the raw output separately, or the activity
    /// view cannot show what the winning task actually emitted before it was rewritten.
    /// </summary>
    [Fact]
    public void MissionReport_ExposesAnswerAndRawOutputSeparately()
    {
        var api = Src("src", "Anthill.Api", "ApiHost.cs");
        Assert.Contains("[\"final_output\"] = mission.GetValueOrDefault(\"final_result\")", api);
        Assert.Contains("[\"raw_output\"] = mission.GetValueOrDefault(\"user_result\")", api);
    }

    // ---- Missions conversation stability (v2.17.1) -------------------------------------------------

    /// <summary>
    /// v2.17.1: the thread must never be rebuilt wholesale.
    ///
    /// v2.16.0 rendered with `thread.innerHTML = rows.map(...).join('')` on every jobs poll — every
    /// three seconds — which destroyed open activity disclosures, loaded reports, focus, selection
    /// and scroll position, and re-announced all forty exchanges. Replacing innerHTML also clamps
    /// scrollTop to 0, so reading history threw you to the top of the thread on every poll.
    ///
    /// The behavioural proof lives in tests/ui/mission-thread.test.js (node --test). This guards
    /// the one thing C# can see: that the destructive pattern has not come back.
    /// </summary>
    [Fact]
    public void MissionThread_IsNeverRebuiltWholesale()
    {
        var js = Ui("app.js");
        var render = BodyOf(js, "function renderMissionThread()");

        // The only innerHTML writes allowed here would be a full-thread replacement.
        Assert.DoesNotContain("thread.innerHTML", render);
        Assert.DoesNotContain("rows.map(", render);

        // Unchanged data must short-circuit before touching the DOM at all.
        Assert.Contains("if(!plan.changed", render);
        Assert.Contains("reconcileThread", render);

        var load = BodyOf(js, "async function loadMissionThread(opts)");
        Assert.DoesNotContain("thread.innerHTML", load);
    }

    /// <summary>
    /// Server text reaches the thread through textContent, never parsed as HTML. Escaping by hand
    /// into an innerHTML string is what this replaces.
    /// </summary>
    [Fact]
    public void MissionThread_WritesServerTextAsTextNotMarkup()
    {
        var js = Ui("app.js");
        var patch = BodyOf(js, "function msPatchExchange(row,m)");
        Assert.DoesNotContain("innerHTML", patch);
        Assert.Contains(".textContent=m.goal", patch);
        Assert.Contains("say.textContent", patch);
    }

    /// <summary>
    /// Stale responses must be rejected. Page entry and the three-second poll can both have a
    /// request in flight; without a generation token a slow older one overwrites newer state.
    /// </summary>
    [Fact]
    public void MissionThread_RejectsStaleResponses()
    {
        var js = Ui("app.js");
        var load = BodyOf(js, "async function loadMissionThread(opts)");
        Assert.Contains("msGate.next()", load);
        Assert.Contains("if(!msGate.isCurrent(token)) return;", load);
    }

    /// <summary>
    /// Activity state lives outside the DOM so it survives updates, and a report is marked loaded
    /// only after it succeeds — v2.16.0 set dataset.loaded before the request resolved, stranding
    /// any report that failed.
    /// </summary>
    [Fact]
    public void MissionActivity_TracksStateOutsideTheDom_AndIsRetryable()
    {
        var js = Ui("app.js");
        Assert.Contains("msActivity", js);
        Assert.Contains("MT.ActivityStore()", js);

        var load = BodyOf(js, "async function msLoadActivity(missionId,det)");
        Assert.Contains("msActivity.begin(missionId)", load);   // duplicate-request guard
        Assert.Contains("msActivity.succeed(missionId)", load); // only after success
        Assert.Contains("msActivity.fail(missionId", load);
        Assert.Contains("data-msretry", js);                    // a visible retry control

        // The Missions path must not use a DOM latch at all — its state is in msActivity, which
        // is what lets it survive a re-render and be retried. (The Results page still uses
        // dataset.loaded, but only after success; that is asserted separately.)
        Assert.DoesNotContain("dataset.loaded", load);
    }

    /// <summary>
    /// renderMissionReport reports success so callers can decide whether to latch. Both callers —
    /// the Missions thread and the Results page — must honour it.
    /// </summary>
    [Fact]
    public void MissionReport_ReportsSuccess_AndBothCallersHonourIt()
    {
        var js = Ui("app.js");
        var report = BodyOf(js, "async function renderMissionReport(missionId, body)");
        Assert.Contains("return false;", report);
        Assert.Contains("return true;", report);

        var toggle = BodyOf(js, "async function onResultToggle(det)");
        Assert.Contains("if(ok) det.dataset.loaded='1';", toggle);
    }

    /// <summary>
    /// The live region must not be the thread itself, or every poll re-announces all forty
    /// exchanges. One dedicated status element announces one finished mission.
    /// </summary>
    [Fact]
    public void MissionThread_AnnouncesOneResult_NotTheWholeThread()
    {
        var html = Ui("index.html");
        var threadTag = Regex.Match(html, @"<div id=""ms-thread""[^>]*>").Value;
        Assert.False(string.IsNullOrEmpty(threadTag), "#ms-thread not found");
        Assert.DoesNotContain("aria-live", threadTag);

        Assert.Contains("id=\"ms-announce\"", html);
        Assert.Contains("aria-live=\"polite\"", html);
        Assert.Contains("announcementFor", Ui("app.js"));
    }

    /// <summary>
    /// A rejected dispatch must show the error and hand the directive back, not clear the box and
    /// go quiet. v2.16.0 did `input.value=''` before the request and swallowed the failure.
    /// </summary>
    [Fact]
    public void Dispatch_SurfacesFailures_AndKeepsTheTypedDirective()
    {
        var js = Ui("app.js");
        var dispatch = BodyOf(js, "async function dispatchMission(inputId)");
        Assert.Contains("const typed=input.value;", dispatch);
        Assert.Contains("else { input.value=typed; }", dispatch);
        Assert.Contains("msDispatchInFlight", dispatch);          // double-submit guard

        var submit = BodyOf(js, "async function submitMissionGoal(goal)");
        Assert.Contains("msShowDispatchError", submit);
        Assert.Contains("return true;", submit);
        Assert.Contains("return false;", submit);

        Assert.Contains("id=\"ms-error\"", Ui("index.html"));
    }

    /// <summary>The reconciler is a served asset, embedded and loaded before app.js.</summary>
    [Fact]
    public void MissionThreadModule_IsShippedAndLoaded()
    {
        Assert.Contains("Ui\\mission-thread.js", Src("src", "Anthill.Api", "Anthill.Api.csproj"));
        var host = Src("src", "Anthill.Api", "ApiHost.cs");
        Assert.Contains("LoadUiAsset(\"mission-thread.js\")", host);
        Assert.Contains("/ui/mission-thread.js", host);

        var html = Ui("index.html");
        Assert.Contains("/ui/mission-thread.js", html);
        Assert.True(html.IndexOf("/ui/mission-thread.js", StringComparison.Ordinal)
                    < html.IndexOf("/ui/app.js", StringComparison.Ordinal),
            "mission-thread.js must load before app.js, which consumes it at parse time.");
    }

    /// <summary>The node --test suite must actually run in CI, or it proves nothing.</summary>
    [Fact]
    public void MissionThreadTests_RunInCiAndValidate()
    {
        Assert.Contains("node --test tests/ui/mission-thread.test.js", Src(".github", "workflows", "ci.yml"));
        Assert.Contains("node --test tests/ui/mission-thread.test.js", Src("scripts", "validate.ps1"));
        Assert.True(File.Exists(Path.Combine(Root(), "tests", "ui", "mission-thread.test.js")));
    }

    /// <summary>
    /// v2.18.2: /missions/json must carry the ANSWER.
    ///
    /// It projected only id/goal/status/success_score/created_at/saved_at — final_result and
    /// user_result were never in the payload — so the conversation view had nothing to display and
    /// every finished exchange read "Working — no answer recorded yet" indefinitely. The bug was
    /// present from v2.16.0 and survived the v2.18.1 rewrite because the client faithfully
    /// reproduced it: both versions read fields the endpoint does not return.
    ///
    /// This is a server-side contract, which is why it is asserted here rather than only in the
    /// JS suite — no amount of client testing would have caught a missing column.
    /// </summary>
    [Fact]
    public void MissionsJson_CarriesTheAnswer()
    {
        var api = Src("src", "Anthill.Api", "ApiHost.cs");
        var idx = api.IndexOf("MapGet(\"/missions/json\"", StringComparison.Ordinal);
        Assert.True(idx > 0, "/missions/json endpoint not found");
        var handler = api.Substring(idx, Math.Min(2200, api.Length - idx));

        Assert.Contains("[\"answer\"]", handler);
        Assert.Contains("[\"answer_truncated\"]", handler);
        // Prefers the synthesized answer, falls back to the raw best-task output.
        Assert.Contains("final_result", handler);
        Assert.Contains("user_result", handler);

        // Bounded: this endpoint serves up to 100 rows and a raw result can be a whole diff.
        Assert.Contains("MissionAnswerPreviewChars", handler);
        Assert.Contains("public const int MissionAnswerPreviewChars", api);

        // The client must read the field the endpoint actually returns.
        var mt = Ui("mission-thread.js");
        Assert.Contains("m.answer === 'string'", mt);
        Assert.Contains("'answer'", mt);              // included in the render fingerprint
        Assert.Contains("'answer_truncated'", mt);
    }

    /// <summary>
    /// v2.19.0: the Modules checklist collapses and stays collapsed.
    ///
    /// It was hidden on render but then force-reopened by toggle-visible after every checkbox
    /// click, and unlike the topology overlay menu it had no outside-click or Escape close. Once
    /// the operator touched it the list reappeared on every interaction and obscured the
    /// right-hand third of the map with no obvious way to dismiss it.
    /// </summary>
    [Fact]
    public void ModulesChecklist_IsCollapsedByDefault_AndDismissable()
    {
        var js = Ui("dashboard-workspace.js");

        // One authoritative flag, defaulting closed, rather than re-derived per render.
        Assert.Contains("modulesOpen: false", js);
        Assert.Contains("menu.hidden = !W.modulesOpen", js);   // v2.22.0: focus mode ORs in after this
        Assert.DoesNotContain("menu.hidden = true;", js);

        // The force-reopen is gone; toggling a module goes through the flag instead.
        Assert.DoesNotContain("if (m) m.hidden = false;", js);
        Assert.Contains("W.modulesOpen = true;", js);

        // Dismissable by clicking away and by keyboard.
        Assert.Contains("function setModulesOpen", js);
        Assert.Contains("e.target.closest('#ws-modules')", js);
        var esc = BodyOf(js, "document.addEventListener('keydown', function (e) {");
        Assert.Contains("Escape", esc);
        Assert.Contains("setModulesOpen(false)", esc);

        // Collapsing must not rebuild every panel — a re-render mid-drag would be visible.
        var setter = BodyOf(js, "function setModulesOpen(open)");
        Assert.DoesNotContain("render()", setter);
    }

    /// <summary>
    /// v2.22.0: the toggle reports the state it is actually in.
    ///
    /// `aria-expanded` was hardcoded to 'false' when the button was built, so every re-render
    /// while the menu was open told assistive technology it was collapsed, and nothing on the
    /// control indicated the list could be closed again — it read as permanent furniture.
    /// </summary>
    [Fact]
    public void ModulesToggle_ReportsItsActualState()
    {
        var js = Ui("dashboard-workspace.js");
        Assert.DoesNotContain("modules.setAttribute('aria-expanded', 'false')", js);
        Assert.Contains("modules.setAttribute('aria-expanded', W.modulesOpen ? 'true' : 'false')", js);
        // And it is visible on the control, not only to a screen reader.
        Assert.Contains("W.modulesOpen ? '▾' : '▸'", js);
    }

    /// <summary>
    /// v2.22.0: focus mode closes the module list and keeps it closed.
    ///
    /// Focus mode hides every unpinned panel. Leaving the checklist open on top of that is the
    /// opposite of focus, and the list would be enumerating panels that are all hidden anyway.
    /// The rule is enforced in the setter as well as at render, so no caller can reopen the list
    /// behind focus mode's back.
    /// </summary>
    [Fact]
    public void FocusMode_ClosesTheModuleList_AndKeepsItClosed()
    {
        var js = Ui("dashboard-workspace.js");

        var focus = BodyOf(js, "'toggle-focus': function () {");
        Assert.Contains("W.modulesOpen = false", focus);

        // Render-time: focus mode is authoritative regardless of the flag.
        Assert.Contains("W.state.focus_mode", BodyOf(js, "function renderModules"));

        // Setter-level: opening is refused while focus mode is on.
        var setter = BodyOf(js, "function setModulesOpen(open)");
        Assert.Contains("focus_mode", setter);
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
        Assert.Single(Regex.Matches(end, @"setPlacement\("));

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
