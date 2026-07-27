using System.Text.Json;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.14.2 Stage 1 gate — the persistence matrix from the topology-first dashboard spec §24,
/// implemented where it can actually run: server-side, in xUnit. Every rule that protects the
/// operator's arrangement is proven here rather than asserted in a manual walkthrough.
/// </summary>
public class DashboardWorkspaceStateTests
{
    // Deliberately arbitrary fixture ids: these tests prove the repair logic is id-AGNOSTIC, so
    // they must not be the production list. The production ids live in
    // DashboardWorkspaceState.KnownPanelIds / KnownOverlayIds and are checked against the client's
    // registrations by RegressionGuardTests.Workspace_CanonicalIdsMatchTheClientRegistrations.
    private static readonly string[] Panels =
        { "colony-health", "mission-command", "recent-events", "pending-approvals" };
    private static readonly string[] Overlays =
        { "view_controls", "legend", "inspector", "interaction_hints" };

    private static DashboardWorkspaceState Sane(DashboardWorkspaceState s, int w = 1600, int h = 900)
        => s.Sanitize(Panels, Overlays, w, h);

    private static DashboardWorkspaceState.PanelPlacement Desktop(DashboardWorkspaceState s, string id)
        => s.Profiles["desktop"][id];

    // ---- Defaults and shape ----------------------------------------------------------------------

    [Fact]
    public void MissingState_ProducesUsableDefaults_ForEveryKnownPanelAndOverlay()
    {
        var s = Sane(new DashboardWorkspaceState());
        Assert.Equal(DashboardWorkspaceState.CurrentSchemaVersion, s.SchemaVersion);
        Assert.True(s.Locked); // normal operation, no accidental dragging
        Assert.False(s.FocusMode);
        foreach (var p in Panels) Assert.True(s.Profiles["desktop"].ContainsKey(p));
        foreach (var p in Panels) Assert.True(s.Profiles["compact"].ContainsKey(p));
        foreach (var o in Overlays) Assert.True(s.TopologyOverlays[o].Visible);
    }

    [Fact]
    public void UnknownPanel_IsDropped_KnownPanelIsAdded()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new() { ["ghost-panel"] = new(), ["colony-health"] = new() { X = 500, Y = 300 } };
        Sane(s);
        Assert.False(s.Profiles["desktop"].ContainsKey("ghost-panel")); // no renderer exists
        Assert.Equal(500, Desktop(s, "colony-health").X);              // customization untouched
        Assert.True(s.Profiles["desktop"].ContainsKey("mission-command")); // new default merged in
    }

    [Fact]
    public void NewPanelMergedIn_DoesNotMoveCustomizedPanels()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new() { ["mission-command"] = new() { X = 111, Y = 222, Width = 333, Height = 144 } };
        Sane(s);
        var p = Desktop(s, "mission-command");
        Assert.Equal((111, 222, 333, 144), (p.X, p.Y, p.Width, p.Height));
    }

    // ---- Clamping and recovery -------------------------------------------------------------------

    [Fact]
    public void OffScreenPanel_IsRecovered_WithAGrabbableHeader()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new() { ["colony-health"] = new() { X = 99999, Y = 99999, Width = 400, Height = 200 } };
        Sane(s, 1600, 900);
        var p = Desktop(s, "colony-health");
        Assert.True(p.X <= 1600 - DashboardWorkspaceState.MinVisibleEdge);
        Assert.True(p.Y <= 900 - DashboardWorkspaceState.MinVisibleEdge);
        Assert.True(p.Y >= 0);
    }

    [Fact]
    public void NegativePanel_KeepsPartOfItselfOnScreen()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new() { ["colony-health"] = new() { X = -100000, Y = -5000, Width = 400 } };
        Sane(s);
        var p = Desktop(s, "colony-health");
        Assert.True(p.X >= -(400 - DashboardWorkspaceState.MinVisibleEdge));
        Assert.Equal(0, p.Y);
    }

    [Fact]
    public void AbsurdSizes_AreClampedToUsableBounds()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new()
        {
            ["colony-health"] = new() { Width = -50, Height = 0 },
            ["mission-command"] = new() { Width = 99999, Height = 99999 },
        };
        Sane(s, 1600, 900);
        Assert.True(Desktop(s, "colony-health").Width >= 200);
        Assert.True(Desktop(s, "colony-health").Height >= 80);
        Assert.True(Desktop(s, "mission-command").Width <= 1600);
        Assert.True(Desktop(s, "mission-command").Height <= 900);
    }

    [Fact]
    public void InvalidEnums_FallBackInsteadOfThrowing()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new()
        {
            ["colony-health"] = new() { DisplayState = "exploded", PlacementMode = "teleported", Opacity = "ghost", DockSide = "diagonal" },
        };
        Sane(s);
        var p = Desktop(s, "colony-health");
        Assert.Equal("visible", p.DisplayState);
        Assert.Equal("floating", p.PlacementMode);
        Assert.Equal("solid", p.Opacity);
        Assert.Null(p.DockSide);
    }

    [Fact]
    public void DockedWithoutSide_RevertsToFloating()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new() { ["colony-health"] = new() { PlacementMode = "docked", DockSide = null } };
        Sane(s);
        Assert.Equal("floating", Desktop(s, "colony-health").PlacementMode);
    }

    /// <summary>
    /// Was `ValidDockSurvives` (v2.14.2), which asserted a valid dock is preserved. v2.15.1 removed
    /// docking at the operator's request, so that intent is obsolete — the guarantee now is that a
    /// valid dock MIGRATES predictably instead of being silently dropped or left in a dead mode.
    /// Converted rather than deleted so the lineage of the behaviour stays visible.
    /// </summary>
    [Fact]
    public void ValidDock_MigratesToTheEquivalentSnap()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new() { ["colony-health"] = new() { PlacementMode = "docked", DockSide = "right" } };
        Sane(s, 1600, 900);

        var p = Desktop(s, "colony-health");
        Assert.Equal("floating", p.PlacementMode);   // docking no longer exists as a mode
        Assert.Null(p.DockSide);                     // and the legacy field is cleared

        // "right" becomes the right half — the closest honest equivalent of a right-edge dock.
        var expected = DashboardWorkspaceState.SnapRegion("right", 1600, 900)!.Value;
        Assert.Equal(expected.X, p.X);
        Assert.Equal(expected.Width, p.Width);
        Assert.Equal(expected.Height, p.Height);
    }

    [Fact]
    public void CollapsedPanel_RemembersItsExpandedHeight()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new()
        { ["colony-health"] = new() { DisplayState = "collapsed", Height = 40, ExpandedHeight = 320 } };
        Sane(s);
        Assert.Equal("collapsed", Desktop(s, "colony-health").DisplayState);
        Assert.Equal(320, Desktop(s, "colony-health").ExpandedHeight);
    }

    // ---- Snapping (v2.15.1, replaces docking) ------------------------------------------------------

    /// <summary>
    /// v2.15.1: docking was removed at the operator's request — an edge dock "extends it super
    /// long instead of into a confined space". Halves and quadrants replace it.
    ///
    /// The v2.15.0 dock tests are gone with the feature they covered, not deleted to obtain a
    /// green build; every invariant that still applies is re-asserted here against snapping.
    /// </summary>
    [Fact]
    public void SnapRegions_TileTheViewportExactly()
    {
        // Halves must cover the axis with no dead strip, including on an odd viewport.
        var l = DashboardWorkspaceState.SnapRegion("left", 1601, 901)!.Value;
        var r = DashboardWorkspaceState.SnapRegion("right", 1601, 901)!.Value;
        Assert.Equal(0, l.X);
        Assert.Equal(l.Width, r.X);
        Assert.Equal(1601, l.Width + r.Width);
        Assert.Equal(901, l.Height);

        var t = DashboardWorkspaceState.SnapRegion("top", 1601, 901)!.Value;
        var b = DashboardWorkspaceState.SnapRegion("bottom", 1601, 901)!.Value;
        Assert.Equal(901, t.Height + b.Height);
        Assert.Equal(1601, t.Width);

        // The four quadrants must tile the viewport with no overlap and no gap.
        var area = 0;
        foreach (var z in new[] { "top-left", "top-right", "bottom-left", "bottom-right" })
        {
            var q = DashboardWorkspaceState.SnapRegion(z, 1600, 900)!.Value;
            area += q.Width * q.Height;
        }
        Assert.Equal(1600 * 900, area);
    }

    /// <summary>A viewport too small to halve still yields a usable panel, never a zero-size one.</summary>
    [Fact]
    public void SnapRegions_RespectMinimumsOnATinyViewport()
    {
        foreach (var z in DashboardWorkspaceState.SnapZones)
        {
            var g = DashboardWorkspaceState.SnapRegion(z, 300, 120)!.Value;
            Assert.True(g.Width >= DashboardWorkspaceState.MinPanelWidth, $"{z} width {g.Width}");
            Assert.True(g.Height >= DashboardWorkspaceState.MinPanelHeight, $"{z} height {g.Height}");
        }
    }

    /// <summary>An unknown zone yields nothing rather than a bogus rectangle.</summary>
    [Fact]
    public void SnapRegion_UnknownZone_IsNull()
    {
        Assert.Null(DashboardWorkspaceState.SnapRegion("diagonal", 1600, 900));
        Assert.Null(DashboardWorkspaceState.SnapRegion(null, 1600, 900));
        Assert.Null(DashboardWorkspaceState.SnapRegion("", 1600, 900));
    }

    /// <summary>
    /// A layout saved by v2.15.0 with docked panels must MIGRATE, not break. A docked panel
    /// becomes a floating panel snapped to the same edge, and the legacy dock fields are cleared.
    /// </summary>
    [Fact]
    public void DockedPanelsFrom_v2_15_0_MigrateToSnapped()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new()
        {
            ["colony-health"] = new() { PlacementMode = "docked", DockSide = "left", DockSize = 400, DockOrder = 2 },
            ["recent-events"] = new() { PlacementMode = "docked", DockSide = "bottom", DockSize = 300 },
        };
        Sane(s, 1600, 900);

        var left = Desktop(s, "colony-health");
        Assert.Equal("floating", left.PlacementMode);
        Assert.Null(left.DockSide);
        Assert.Equal(0, left.DockOrder);
        Assert.Equal(0, left.X);
        Assert.Equal(800, left.Width);      // the left half
        Assert.Equal(900, left.Height);

        var bottom = Desktop(s, "recent-events");
        Assert.Equal("floating", bottom.PlacementMode);
        Assert.Equal(450, bottom.Y);        // the bottom half
    }

    /// <summary>A docked panel with a nonsense side still lands somewhere valid.</summary>
    [Fact]
    public void DockedPanel_WithGarbageSide_StillMigratesSafely()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new()
        {
            ["colony-health"] = new() { PlacementMode = "docked", DockSide = "diagonal", Width = 400, Height = 200 },
        };
        Sane(s, 1600, 900);
        var p = Desktop(s, "colony-health");
        Assert.Equal("floating", p.PlacementMode);
        Assert.Null(p.DockSide);
        Assert.InRange(p.Width, DashboardWorkspaceState.MinPanelWidth, 1600);
        Assert.InRange(p.Height, DashboardWorkspaceState.MinPanelHeight, 900);
    }

    // ---- Tab groups --------------------------------------------------------------------------------

    /// <summary>
    /// v2.15.0: groups joined the panel stacking order so a group can be raised above a floating
    /// panel. Z is clamped like every other placement value — a garbage z from a hand-edited or
    /// corrupt ui_state.json must not be able to park a group permanently above the toolbar.
    /// </summary>
    [Fact]
    public void TabGroup_ZOrder_IsClamped()
    {
        var s = new DashboardWorkspaceState();
        s.TabGroups["g1"] = new() { Panels = { "recent-events", "pending-approvals" }, Z = 999999 };
        s.TabGroups["g2"] = new() { Panels = { "colony-health", "mission-command" }, Z = -40 };
        Sane(s);
        Assert.InRange(s.TabGroups["g1"].Z, 1, 9999);
        Assert.InRange(s.TabGroups["g2"].Z, 1, 9999);
    }

    /// <summary>A sane z survives untouched — clamping must not flatten real arrangements.</summary>
    [Fact]
    public void TabGroup_ZOrder_PreservesReasonableValues()
    {
        var s = new DashboardWorkspaceState();
        s.TabGroups["g1"] = new() { Panels = { "recent-events", "pending-approvals" }, Z = 7 };
        Sane(s);
        Assert.Equal(7, s.TabGroups["g1"].Z);
    }

    [Fact]
    public void TabGroup_WithBrokenMemberReference_DropsOnlyTheGhost()
    {
        var s = new DashboardWorkspaceState();
        s.TabGroups["g1"] = new() { Panels = { "recent-events", "ghost", "pending-approvals" }, Active = "ghost" };
        Sane(s);
        Assert.Equal(new[] { "recent-events", "pending-approvals" }, s.TabGroups["g1"].Panels);
        Assert.Equal("recent-events", s.TabGroups["g1"].Active); // invalid active repaired
    }

    [Fact]
    public void TabGroup_ReducedBelowTwoMembers_Dissolves_AndSurvivorFloats()
    {
        var s = new DashboardWorkspaceState();
        s.TabGroups["g1"] = new() { Panels = { "recent-events", "ghost" }, Active = "recent-events" };
        s.Profiles["desktop"] = new()
        { ["recent-events"] = new() { PlacementMode = "tabbed", TabGroup = "g1" } };
        Sane(s);
        Assert.False(s.TabGroups.ContainsKey("g1"));
        Assert.Null(Desktop(s, "recent-events").TabGroup);
        Assert.Equal("floating", Desktop(s, "recent-events").PlacementMode);
    }

    [Fact]
    public void PanelPointingAtMissingTabGroup_ReturnsToFloating()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new()
        { ["mission-command"] = new() { PlacementMode = "tabbed", TabGroup = "nope" } };
        Sane(s);
        Assert.Null(Desktop(s, "mission-command").TabGroup);
        Assert.Equal("floating", Desktop(s, "mission-command").PlacementMode);
    }

    [Fact]
    public void TabGroup_GeometryIsClampedLikeAPanel()
    {
        var s = new DashboardWorkspaceState();
        s.TabGroups["g1"] = new() { Panels = { "recent-events", "pending-approvals" }, X = 99999, Y = 99999, Width = 99999 };
        Sane(s, 1600, 900);
        var g = s.TabGroups["g1"];
        Assert.True(g.X <= 1600 - DashboardWorkspaceState.MinVisibleEdge);
        Assert.True(g.Y <= 900 - DashboardWorkspaceState.MinVisibleEdge);
        Assert.True(g.Width <= 1600);
    }

    // ---- Overlays -----------------------------------------------------------------------------------

    [Fact]
    public void InvalidOverlayAnchor_FallsBack_UnknownOverlayDropped()
    {
        var s = new DashboardWorkspaceState();
        s.TopologyOverlays["legend"] = new() { Visible = false, Anchor = "outer-space" };
        s.TopologyOverlays["not-a-real-overlay"] = new();
        Sane(s);
        Assert.Equal("top-left", s.TopologyOverlays["legend"].Anchor);
        Assert.False(s.TopologyOverlays["legend"].Visible); // the operator's HIDE choice is respected
        Assert.False(s.TopologyOverlays.ContainsKey("not-a-real-overlay"));
    }

    [Fact]
    public void HiddenLegendPreference_SurvivesARoundTrip()
    {
        var s = Sane(new DashboardWorkspaceState());
        s.TopologyOverlays["legend"].Visible = false;
        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<DashboardWorkspaceState>(json)!.Sanitize(Panels, Overlays);
        Assert.False(back.TopologyOverlays["legend"].Visible);
    }

    // ---- Profiles: mobile must not clobber desktop ----------------------------------------------------

    [Fact]
    public void CompactProfile_IsIndependentOfDesktop()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new() { ["mission-command"] = new() { X = 900, Y = 600, Width = 500 } };
        s.Profiles["compact"] = new() { ["mission-command"] = new() { X = 0, Y = 0, Width = 320 } };
        Sane(s, 390, 780); // phone-sized viewport
        Assert.Equal(900, s.Profiles["desktop"]["mission-command"].X);   // desktop coordinates preserved
        Assert.Equal(500, s.Profiles["desktop"]["mission-command"].Width);
        Assert.Equal(320, s.Profiles["compact"]["mission-command"].Width);
    }

    [Fact]
    public void UnknownProfileKey_IsDropped()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["hologram"] = new() { ["colony-health"] = new() };
        Sane(s);
        Assert.False(s.Profiles.ContainsKey("hologram"));
        Assert.True(s.Profiles.ContainsKey("desktop") && s.Profiles.ContainsKey("compact"));
    }

    // ---- The invariant: colony data outranks layout ---------------------------------------------------

    [Fact]
    public void CorruptWorkspace_ResetsLayoutOnly_AntCustomizationSurvives()
    {
        var ui = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["ants"] = new Dictionary<string, object?> { ["queen"] = new Dictionary<string, object?> { ["name"] = "Her Majesty", ["color"] = "#ffcc00" } },
            ["layout"] = new Dictionary<string, object?> { ["zoom"] = 1.4 },
            ["dashboard_workspace"] = "this is not an object at all",
        };
        var result = DashboardWorkspaceState.SanitizeInto(ui, Panels, Overlays);

        var ants = (Dictionary<string, object?>)result["ants"]!;
        var queen = (Dictionary<string, object?>)ants["queen"]!;
        Assert.Equal("Her Majesty", queen["name"]);          // untouched
        Assert.Equal("#ffcc00", queen["color"]);
        Assert.NotNull(result["layout"]);                     // map prefs untouched
        var ws = Assert.IsType<DashboardWorkspaceState>(result["dashboard_workspace"]);
        Assert.Equal(DashboardWorkspaceState.CurrentSchemaVersion, ws.SchemaVersion); // rebuilt safely
    }

    [Fact]
    public void LegacyVersion1State_WithNoWorkspace_GainsDefaults_WithoutLoss()
    {
        var ui = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["ants"] = new Dictionary<string, object?> { ["coder"] = new Dictionary<string, object?> { ["x"] = 12.5 } },
            ["layout"] = new Dictionary<string, object?>(),
        };
        var result = DashboardWorkspaceState.SanitizeInto(ui, Panels, Overlays);
        Assert.True(result.ContainsKey("ants"));
        var ws = (DashboardWorkspaceState)result["dashboard_workspace"]!;
        Assert.True(ws.Profiles["desktop"].Count > 0);
        Assert.True(ws.Locked);
    }

    [Fact]
    public void UnrelatedFutureKeys_SurviveSanitization()
    {
        var ui = new Dictionary<string, object?>
        {
            ["ants"] = new Dictionary<string, object?>(),
            ["some_future_feature"] = "keep me",
        };
        var result = DashboardWorkspaceState.SanitizeInto(ui, Panels, Overlays);
        Assert.Equal("keep me", result["some_future_feature"]);
    }

    [Fact]
    public void SanitizeIsIdempotent()
    {
        var s = Sane(new DashboardWorkspaceState());
        var first = JsonSerializer.Serialize(s);
        var second = JsonSerializer.Serialize(s.Sanitize(Panels, Overlays));
        Assert.Equal(first, second);
    }
}
