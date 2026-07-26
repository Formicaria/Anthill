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

    [Fact]
    public void ValidDockSurvives()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new() { ["colony-health"] = new() { PlacementMode = "docked", DockSide = "right" } };
        Sane(s);
        Assert.Equal("docked", Desktop(s, "colony-health").PlacementMode);
        Assert.Equal("right", Desktop(s, "colony-health").DockSide);
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

    // ---- Docking (v2.15.0) -------------------------------------------------------------------------

    /// <summary>
    /// Docking must never be able to hide the colony map. The topology is the persistent
    /// background of this dashboard; a dock strip allowed to reach the full viewport would let an
    /// operator (or a corrupt state file) bury it with no obvious way back.
    /// </summary>
    [Fact]
    public void DockedPanel_CannotSwallowTheViewport()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new()
        {
            ["colony-health"] = new() { PlacementMode = "docked", DockSide = "left", DockSize = 999999 },
            ["recent-events"] = new() { PlacementMode = "docked", DockSide = "top", DockSize = 999999 },
        };
        Sane(s, 1600, 900);

        var left = Desktop(s, "colony-health");
        var top = Desktop(s, "recent-events");
        Assert.True(left.DockSize <= (int)(1600 * DashboardWorkspaceState.MaxDockFraction),
            $"left dock {left.DockSize} exceeds the max fraction of a 1600px viewport");
        Assert.True(top.DockSize <= (int)(900 * DashboardWorkspaceState.MaxDockFraction),
            $"top dock {top.DockSize} exceeds the max fraction of a 900px viewport");
    }

    /// <summary>Left/right clamp against WIDTH, top/bottom against HEIGHT — not one shared axis.</summary>
    [Fact]
    public void DockSize_ClampsAgainstTheCorrectAxis()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new()
        {
            ["colony-health"] = new() { PlacementMode = "docked", DockSide = "left", DockSize = 900 },
            ["recent-events"] = new() { PlacementMode = "docked", DockSide = "bottom", DockSize = 900 },
        };
        Sane(s, 2000, 800);   // wide and short: the two sides must clamp differently
        Assert.Equal(900, Desktop(s, "colony-health").DockSize);              // 900 < 60% of 2000
        Assert.True(Desktop(s, "recent-events").DockSize <= 480);             // 60% of 800
    }

    /// <summary>A panel cannot be docked and tabbed at once — it would render in two places.</summary>
    [Fact]
    public void DockedPanel_IsNeverAlsoTabbed()
    {
        var s = new DashboardWorkspaceState();
        s.TabGroups["g1"] = new() { Panels = { "colony-health", "recent-events" }, Active = "colony-health" };
        s.Profiles["desktop"] = new()
        {
            ["colony-health"] = new() { PlacementMode = "docked", DockSide = "right", TabGroup = "g1" },
        };
        Sane(s);
        Assert.Null(Desktop(s, "colony-health").TabGroup);
    }

    /// <summary>Dock fields are cleared when a panel returns to floating, so nothing lingers.</summary>
    [Fact]
    public void UndockedPanel_LosesItsDockFields()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new()
        {
            ["colony-health"] = new() { PlacementMode = "floating", DockSide = "left", DockOrder = 4 },
        };
        Sane(s);
        var p = Desktop(s, "colony-health");
        Assert.Null(p.DockSide);
        Assert.Equal(0, p.DockOrder);
    }

    /// <summary>A dock side that is not a real edge falls back to floating rather than throwing.</summary>
    [Fact]
    public void GarbageDockSide_FallsBackToFloating()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new()
        {
            ["colony-health"] = new() { PlacementMode = "docked", DockSide = "diagonal" },
        };
        Sane(s);
        var p = Desktop(s, "colony-health");
        Assert.Equal("floating", p.PlacementMode);
        Assert.Null(p.DockSide);
    }

    /// <summary>
    /// Opposing rails must fit the axis TOGETHER. Clamping each edge to 60% independently still
    /// lets left+right reach 120% of the width, which overlaps the two rails and buries the map.
    /// </summary>
    [Fact]
    public void OpposingDockRails_CannotCombineToBuryTheMap()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new()
        {
            ["colony-health"] = new() { PlacementMode = "docked", DockSide = "left", DockSize = 900 },
            ["recent-events"] = new() { PlacementMode = "docked", DockSide = "right", DockSize = 900 },
        };
        Sane(s, 1600, 900);

        var left = Desktop(s, "colony-health").DockSize;
        var right = Desktop(s, "recent-events").DockSize;
        var budget = (int)(1600 * DashboardWorkspaceState.MaxDockFraction);
        Assert.True(left + right <= budget,
            $"left({left}) + right({right}) = {left + right} exceeds the {budget}px budget for a 1600px viewport");
        Assert.True(left > 0 && right > 0, "neither rail should be erased outright");
    }

    /// <summary>Top and bottom clamp against height, independently of the horizontal pair.</summary>
    [Fact]
    public void OpposingDockRails_ClampPerAxis()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new()
        {
            ["colony-health"] = new() { PlacementMode = "docked", DockSide = "top", DockSize = 600 },
            ["recent-events"] = new() { PlacementMode = "docked", DockSide = "bottom", DockSize = 600 },
            // Fixture ids only — this file's Panels list is deliberately arbitrary, so a real
            // production id like "missions" is "unknown" here and Sanitize correctly drops it.
            ["pending-approvals"] = new() { PlacementMode = "docked", DockSide = "left", DockSize = 300 },
        };
        Sane(s, 1600, 900);

        var vertical = Desktop(s, "colony-health").DockSize + Desktop(s, "recent-events").DockSize;
        Assert.True(vertical <= (int)(900 * DashboardWorkspaceState.MaxDockFraction),
            $"top+bottom = {vertical} exceeds the vertical budget");
        // The lone horizontal rail was already within budget and must be left alone.
        Assert.Equal(300, Desktop(s, "pending-approvals").DockSize);
    }

    /// <summary>A pair already within budget is not shrunk — the clamp only intervenes when needed.</summary>
    [Fact]
    public void OpposingDockRails_WithinBudget_AreUntouched()
    {
        var s = new DashboardWorkspaceState();
        s.Profiles["desktop"] = new()
        {
            ["colony-health"] = new() { PlacementMode = "docked", DockSide = "left", DockSize = 300 },
            ["recent-events"] = new() { PlacementMode = "docked", DockSide = "right", DockSize = 400 },
        };
        Sane(s, 1600, 900);          // 700 <= 960 budget
        Assert.Equal(300, Desktop(s, "colony-health").DockSize);
        Assert.Equal(400, Desktop(s, "recent-events").DockSize);
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
