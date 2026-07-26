using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anthill.Core.Configuration;

/// <summary>
/// v2.14.2 — Topology-first Dashboard, Stage 1: the workspace layout state model.
///
/// Deliberately lives in C# rather than the browser so every rule that protects the operator's
/// work is unit-testable: validation, clamping, migration, and recovery from corrupt state. The
/// browser owns interaction; this owns correctness.
///
/// Hard invariants:
///  - Ant customization (names, colours, positions) and map preferences are NEVER touched by
///    workspace repair. A corrupt panel layout can never cost the operator their colony.
///  - Desktop and compact (mobile) placements are separate profiles — visiting on a phone cannot
///    overwrite the desktop arrangement.
///  - Unknown/future fields survive a round trip; unknown PANELS are dropped (they have no
///    renderer), unknown overlay anchors fall back rather than throwing.
///  - Every panel is validated independently: one bad entry never discards the whole layout.
/// </summary>
public sealed class DashboardWorkspaceState
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Workspace bounds used for clamping when the client hasn't reported its size.</summary>
    public const int DefaultViewportWidth = 1600;
    public const int DefaultViewportHeight = 900;
    /// <summary>Bounds used to sanity-check the compact profile when it is NOT the active one.</summary>
    public const int DefaultCompactWidth = 430;
    public const int DefaultCompactHeight = 900;
    /// <summary>Viewports narrower than this are the compact profile.</summary>
    public const int CompactBreakpoint = 900;
    /// <summary>A panel must keep at least this much of its header reachable on screen.</summary>
    public const int MinVisibleEdge = 64;

    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>Locked = normal operation (no accidental dragging). Unlocked = customize mode.</summary>
    [JsonPropertyName("locked")] public bool Locked { get; set; } = true;
    [JsonPropertyName("focus_mode")] public bool FocusMode { get; set; }
    [JsonPropertyName("minimal_topology_chrome")] public bool MinimalTopologyChrome { get; set; }
    /// <summary>Placements keyed by profile: "desktop" | "compact". Never merged into each other.</summary>
    [JsonPropertyName("profiles")] public Dictionary<string, Dictionary<string, PanelPlacement>> Profiles { get; set; } = new();
    [JsonPropertyName("tab_groups")] public Dictionary<string, TabGroup> TabGroups { get; set; } = new();
    [JsonPropertyName("topology_overlays")] public Dictionary<string, OverlayState> TopologyOverlays { get; set; } = new();

    public static readonly string[] Profileses = { "desktop", "compact" };
    public static readonly string[] DisplayStates = { "visible", "collapsed", "minimized", "hidden" };
    public static readonly string[] PlacementModes = { "floating", "docked", "tabbed" };
    public static readonly string[] DockSides = { "left", "right", "top", "bottom" };
    /// <summary>
    /// v2.15.0: the largest share of the viewport a single docked edge may consume.
    ///
    /// This is a product invariant, not a style preference. The whole premise of the topology-first
    /// dashboard is that the colony map is the persistent background; a dock strip allowed to grow
    /// to 100% would let the operator hide the map completely and leave no obvious way back. The
    /// clamp is enforced here rather than in CSS so a hand-edited ui_state.json cannot bypass it.
    /// </summary>
    public const double MaxDockFraction = 0.60;
    public const int MinDockSize = 180;

    public static readonly string[] Anchors =
        { "top-left", "top-center", "top-right", "bottom-left", "bottom-center", "bottom-right" };

    /// <summary>
    /// v2.14.14: the canonical panel ids, owned here rather than only in JavaScript.
    ///
    /// Until now nothing in the API called <c>SanitizeInto</c> at all — the /ui/state endpoints
    /// read and wrote the file verbatim — so every one of this class's guarantees was inert in the
    /// running system, and the tests validated against a panel set that did not exist
    /// ("mission-command", "pending-approvals"). Making the server own the list is what lets the
    /// endpoint sanitize, and lets a regression guard prove the client registers exactly these.
    /// </summary>
    public static readonly string[] KnownPanelIds =
    {
        "colony-health", "system-core", "missions", "approvals",
        "resource-usage", "recent-events", "operator-attention",
        // v2.14.15: the Colony page's inspector and jobs list, so the dashboard can host
        // everything the Colony page does and the topology no longer has to be left behind.
        "agent-inspector", "colony-jobs",
    };

    /// <summary>
    /// Topology chrome that can be independently hidden and re-anchored (Stage 7).
    /// The inspector is deliberately NOT here: on the Colony page it is a sidebar card rather than
    /// a canvas overlay, so anchoring it belongs with Stage 9's route consolidation.
    /// </summary>
    public static readonly string[] KnownOverlayIds =
        { "viewbar", "legend", "signals", "hints" };

    public sealed class PanelPlacement
    {
        [JsonPropertyName("display_state")] public string DisplayState { get; set; } = "visible";
        [JsonPropertyName("placement_mode")] public string PlacementMode { get; set; } = "floating";
        [JsonPropertyName("x")] public int X { get; set; }
        [JsonPropertyName("y")] public int Y { get; set; }
        [JsonPropertyName("width")] public int Width { get; set; } = 380;
        [JsonPropertyName("height")] public int Height { get; set; } = 240;
        /// <summary>Height to restore to when a collapsed panel expands again.</summary>
        [JsonPropertyName("expanded_height")] public int ExpandedHeight { get; set; } = 240;
        [JsonPropertyName("z")] public int Z { get; set; } = 1;
        [JsonPropertyName("pinned")] public bool Pinned { get; set; }
        [JsonPropertyName("dock_side")] public string? DockSide { get; set; }
        /// <summary>
        /// Thickness of the docked strip: width for left/right, height for top/bottom. Clamped so
        /// docking can never swallow the whole viewport — see MaxDockFraction.
        /// </summary>
        [JsonPropertyName("dock_size")] public int DockSize { get; set; } = 320;
        /// <summary>Position among the panels sharing this edge, low to high.</summary>
        [JsonPropertyName("dock_order")] public int DockOrder { get; set; }
        [JsonPropertyName("tab_group")] public string? TabGroup { get; set; }
        [JsonPropertyName("opacity")] public string Opacity { get; set; } = "solid"; // scrim strength, never text
    }

    public sealed class TabGroup
    {
        [JsonPropertyName("panels")] public List<string> Panels { get; set; } = new();
        [JsonPropertyName("active")] public string? Active { get; set; }
        [JsonPropertyName("x")] public int X { get; set; }
        [JsonPropertyName("y")] public int Y { get; set; }
        [JsonPropertyName("width")] public int Width { get; set; } = 460;
        [JsonPropertyName("height")] public int Height { get; set; } = 280;
        // v2.15.0: groups share the panel stacking order, so a group can be raised above a panel
        // and vice versa. Clamped like any other placement value rather than trusted from JSON.
        [JsonPropertyName("z")] public int Z { get; set; } = 1;
    }

    public sealed class OverlayState
    {
        [JsonPropertyName("visible")] public bool Visible { get; set; } = true;
        [JsonPropertyName("anchor")] public string Anchor { get; set; } = "top-left";
    }

    /// <summary>
    /// Repairs any state into something safe to render. Never throws, never returns null, and
    /// never discards more than the specific entry that is broken.
    /// </summary>
    /// <param name="knownPanelIds">Panels the client actually has renderers for. Unknown ids are
    /// dropped (they cannot be drawn); missing known panels are added from defaults.</param>
    public DashboardWorkspaceState Sanitize(
        IReadOnlyCollection<string> knownPanelIds,
        IReadOnlyCollection<string> knownOverlayIds,
        int viewportWidth = DefaultViewportWidth,
        int viewportHeight = DefaultViewportHeight)
    {
        SchemaVersion = CurrentSchemaVersion;
        var vw = viewportWidth < 320 ? DefaultViewportWidth : viewportWidth;
        var vh = viewportHeight < 240 ? DefaultViewportHeight : viewportHeight;

        Profiles ??= new();
        foreach (var profile in Profileses)
            if (!Profiles.ContainsKey(profile)) Profiles[profile] = new();
        // Unknown profile keys are dropped — they would never be selected by the client.
        foreach (var stale in Profiles.Keys.Where(k => !Profileses.Contains(k)).ToList())
            Profiles.Remove(stale);

        TabGroups ??= new();
        TopologyOverlays ??= new();

        foreach (var (profile, panels) in Profiles)
        {
            // Each profile is clamped against ITS OWN form factor, never the caller's. Loading the
            // console on a phone must not squash the desktop arrangement into 390px.
            var (pw, ph) = BoundsFor(profile, vw, vh);
            foreach (var id in panels.Keys.ToList())
            {
                if (!knownPanelIds.Contains(id)) { panels.Remove(id); continue; } // no renderer
                var p = panels[id] ?? new PanelPlacement();
                panels[id] = SanitizePanel(p, pw, ph);
            }
            // Newly shipped panels join the layout WITHOUT disturbing customized ones.
            foreach (var id in knownPanelIds.Where(k => !panels.ContainsKey(k)))
                panels[id] = SanitizePanel(new PanelPlacement { X = 40, Y = 40 }, pw, ph);
        }

        SanitizeTabGroups(knownPanelIds, vw, vh);
        SanitizeDockRails(vw, vh);

        foreach (var id in TopologyOverlays.Keys.ToList())
        {
            if (!knownOverlayIds.Contains(id)) { TopologyOverlays.Remove(id); continue; }
            var o = TopologyOverlays[id] ?? new OverlayState();
            if (!Anchors.Contains(o.Anchor ?? "")) o.Anchor = "top-left";
            TopologyOverlays[id] = o;
        }
        foreach (var id in knownOverlayIds.Where(k => !TopologyOverlays.ContainsKey(k)))
            TopologyOverlays[id] = new OverlayState();

        return this;
    }

    /// <summary>
    /// Clamping bounds for a profile. The profile matching the caller's viewport uses the real
    /// measurements; the other profile is checked against its own defaults so switching device
    /// never rewrites the layout you built on the other one.
    /// </summary>
    private static (int W, int H) BoundsFor(string profile, int vw, int vh)
    {
        var viewportIsCompact = vw < CompactBreakpoint;
        var profileIsCompact = profile == "compact";
        if (profileIsCompact == viewportIsCompact) return (vw, vh);
        return profileIsCompact
            ? (DefaultCompactWidth, DefaultCompactHeight)
            : (DefaultViewportWidth, DefaultViewportHeight);
    }

    private static PanelPlacement SanitizePanel(PanelPlacement p, int vw, int vh)
    {
        if (!DisplayStates.Contains(p.DisplayState ?? "")) p.DisplayState = "visible";
        if (!PlacementModes.Contains(p.PlacementMode ?? "")) p.PlacementMode = "floating";
        if (p.DockSide is not null && !DockSides.Contains(p.DockSide)) p.DockSide = null;
        if (p.PlacementMode == "docked" && p.DockSide is null) p.PlacementMode = "floating";
        if (p.PlacementMode != "docked") { p.DockSide = null; p.DockOrder = 0; }
        else
        {
            // A panel cannot be docked AND tabbed: it would render in two places at once.
            p.TabGroup = null;
            var axis = p.DockSide is "left" or "right" ? vw : vh;
            p.DockSize = Clamp(p.DockSize, MinDockSize, Math.Max(MinDockSize, (int)(axis * MaxDockFraction)));
            p.DockOrder = Clamp(p.DockOrder, 0, 99);
        }

        p.Width = Clamp(p.Width, 200, Math.Max(200, vw));
        p.Height = Clamp(p.Height, 80, Math.Max(80, vh));
        p.ExpandedHeight = p.ExpandedHeight <= 0 ? p.Height : Clamp(p.ExpandedHeight, 80, Math.Max(80, vh));

        // Recover panels dragged (or migrated) off-screen: always keep a grabbable header edge.
        p.X = Clamp(p.X, -(p.Width - MinVisibleEdge), Math.Max(0, vw - MinVisibleEdge));
        p.Y = Clamp(p.Y, 0, Math.Max(0, vh - MinVisibleEdge));
        p.Z = Clamp(p.Z, 0, 9999);
        if (p.Opacity is not ("solid" or "high" or "medium" or "low")) p.Opacity = "solid";
        return p;
    }

    /// <summary>
    /// v2.15.0: clamp OPPOSING dock rails together, not just individually.
    ///
    /// Per-panel clamping caps each edge at MaxDockFraction, which is not enough: left at 60% plus
    /// right at 60% is 120% of the width, so the two rails overlap and the topology — the entire
    /// point of this dashboard — disappears with no visible way back. The pair on each axis must
    /// therefore fit within MaxDockFraction combined.
    ///
    /// When a pair is over budget both rails are scaled down proportionally rather than one being
    /// truncated, so the operator's relative sizing survives instead of one edge being punished
    /// for being processed second.
    /// </summary>
    private void SanitizeDockRails(int vw, int vh)
    {
        foreach (var (_, panels) in Profiles)
        {
            ClampAxis(panels, "left", "right", vw);
            ClampAxis(panels, "top", "bottom", vh);
        }

        static void ClampAxis(Dictionary<string, PanelPlacement> panels, string a, string b, int extent)
        {
            var budget = Math.Max(MinDockSize * 2, (int)(extent * MaxDockFraction));
            var sizeA = RailSize(panels, a);
            var sizeB = RailSize(panels, b);
            var total = sizeA + sizeB;
            if (total <= budget || total <= 0) return;

            // Proportional scale-down, with each surviving rail kept at or above MinDockSize.
            var scale = (double)budget / total;
            var newA = Math.Max(MinDockSize, (int)(sizeA * scale));
            var newB = Math.Max(MinDockSize, (int)(sizeB * scale));
            // If both floors together still exceed the budget the viewport is simply too small for
            // two opposing rails; the second axis-end gives way so at least one stays usable.
            if (newA + newB > budget) newB = Math.Max(0, budget - newA);

            SetRail(panels, a, newA);
            SetRail(panels, b, newB);
        }

        static int RailSize(Dictionary<string, PanelPlacement> panels, string side)
        {
            var max = 0;
            foreach (var (_, p) in panels)
                if (p.PlacementMode == "docked" && p.DockSide == side) max = Math.Max(max, p.DockSize);
            return max;
        }

        static void SetRail(Dictionary<string, PanelPlacement> panels, string side, int size)
        {
            foreach (var (_, p) in panels)
                if (p.PlacementMode == "docked" && p.DockSide == side) p.DockSize = size;
        }
    }

    private void SanitizeTabGroups(IReadOnlyCollection<string> knownPanelIds, int vw, int vh)
    {
        foreach (var gid in TabGroups.Keys.ToList())
        {
            var g = TabGroups[gid];
            if (g is null) { TabGroups.Remove(gid); continue; }
            g.Panels = (g.Panels ?? new()).Where(knownPanelIds.Contains).Distinct().ToList();
            // A group needs at least two members to be a group; otherwise it dissolves and the
            // survivor floats free rather than being stranded in a phantom stack.
            if (g.Panels.Count < 2)
            {
                foreach (var (_, panels) in Profiles)
                    foreach (var pid in g.Panels)
                        if (panels.TryGetValue(pid, out var sp) && sp.TabGroup == gid)
                        { sp.TabGroup = null; sp.PlacementMode = "floating"; }
                TabGroups.Remove(gid);
                continue;
            }
            if (g.Active is null || !g.Panels.Contains(g.Active)) g.Active = g.Panels[0];
            g.Width = Clamp(g.Width, 200, Math.Max(200, vw));
            g.Height = Clamp(g.Height, 80, Math.Max(80, vh));
            g.X = Clamp(g.X, -(g.Width - MinVisibleEdge), Math.Max(0, vw - MinVisibleEdge));
            g.Y = Clamp(g.Y, 0, Math.Max(0, vh - MinVisibleEdge));
            g.Z = Clamp(g.Z, 1, 9999);
        }

        // Panels pointing at a group that no longer exists return to floating.
        foreach (var (_, panels) in Profiles)
            foreach (var (_, p) in panels)
                if (p.TabGroup is not null && !TabGroups.ContainsKey(p.TabGroup))
                { p.TabGroup = null; if (p.PlacementMode == "tabbed") p.PlacementMode = "floating"; }
    }

    private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

    /// <summary>
    /// Extracts, repairs, and re-attaches the workspace inside a full ui_state document, leaving
    /// every other key (ants, map preferences, unrelated layout) exactly as it was. A workspace
    /// that cannot be parsed at all is replaced with defaults — the colony data still survives.
    /// </summary>
    public static Dictionary<string, object?> SanitizeInto(
        Dictionary<string, object?> uiState,
        IReadOnlyCollection<string> knownPanelIds,
        IReadOnlyCollection<string> knownOverlayIds,
        int viewportWidth = DefaultViewportWidth,
        int viewportHeight = DefaultViewportHeight)
    {
        DashboardWorkspaceState workspace;
        try
        {
            workspace = uiState.TryGetValue("dashboard_workspace", out var raw) && raw is not null
                ? JsonSerializer.Deserialize<DashboardWorkspaceState>(
                      raw is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(raw)) ?? new()
                : new();
        }
        catch { workspace = new(); } // corrupt workspace: reset THIS key only
        uiState["dashboard_workspace"] = workspace.Sanitize(knownPanelIds, knownOverlayIds, viewportWidth, viewportHeight);
        return uiState;
    }
}
