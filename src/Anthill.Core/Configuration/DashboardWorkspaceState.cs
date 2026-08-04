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
    /// <summary>Retained for v2.15.0 documents only — docking was replaced by snapping in
    /// v2.15.1 and these values now feed the one-way migration in SanitizePanel.</summary>
    public static readonly string[] DockSides = { "left", "right", "top", "bottom" };
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
        // v2.15.1: the six cards that used to render in normal flow beneath the map. Promoting
        // them to panels is what lets the topology occupy the whole page.
        "colony-vitals", "recent-missions", "patch-activity", "objectives", "recent-jobs",
        "live-telemetry",
        // v3.1.1: the Mission Composer. Its absence is why the plan-preview review step had no
        // reachable control once the topology workspace became the default console in v2.15.0 —
        // the endpoint, the renderer and the button all existed; nothing could reach them.
        "mission-composer",
        // v3.3.0: the Colony. Under the floating workspace it was the page BACKGROUND and so was
        // never a panel; in the grid it is a first-class widget like any other and must be known
        // to the server, or the ids guard reports a disagreement that is really just this gap.
        "colony",
        // v3.7.1: Conversations. The consequence of omitting it is precise and quiet: Sanitize()
        // treats an unknown id as a panel to DELETE, so an operator who moved or hid this widget
        // would have that choice silently discarded on the next /ui/state round trip, and the
        // widget would spring back to its default placement with no error anywhere.
        "conversations",
        // v3.7.2: the panels for operator-defined tools, model routing fitness and mission
        // workspaces. Added HERE at the same time as the client registration, deliberately — the
        // v3.7.1 sweep found Conversations registered in one place and not the other, and the only
        // symptom was an operator's layout choice quietly failing to stick.
        "tools", "workspaces",
        // v3.8.0: durable task attempts, including the ones that ended unobserved after touching
        // something and are waiting on a person.
        "attempts",
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
        /// Legacy: thickness of a v2.15.0 dock strip. Docking was replaced by snapping in
        /// v2.15.1; this survives only so documents written by v2.15.0 still deserialize.
        /// </summary>
        [JsonPropertyName("dock_size")] public int DockSize { get; set; } = 320;
        /// <summary>Legacy: order among panels sharing a v2.15.0 dock edge. See DockSize.</summary>
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
        // v2.15.1: docking is gone, replaced by edge/corner snapping. Any layout saved by v2.15.0
        // is MIGRATED rather than discarded: a docked panel becomes a floating panel snapped to
        // the same edge, which is the closest honest equivalent. Dock fields are then cleared, and
        // remain in the schema only so old documents keep deserializing.
        if (p.PlacementMode == "docked")
        {
            var region = SnapRegion(p.DockSide, vw, vh);
            if (region is { } r) { p.X = r.X; p.Y = r.Y; p.Width = r.Width; p.Height = r.Height; }
            p.PlacementMode = "floating";
            p.TabGroup = null;   // a docked panel was never tabbed; keep it that way through the move
        }
        p.DockSide = null;
        p.DockOrder = 0;

        p.Width = Clamp(p.Width, MinPanelWidth, Math.Max(MinPanelWidth, vw));
        p.Height = Clamp(p.Height, MinPanelHeight, Math.Max(MinPanelHeight, vh));
        p.ExpandedHeight = p.ExpandedHeight <= 0 ? p.Height : Clamp(p.ExpandedHeight, 80, Math.Max(80, vh));

        // Recover panels dragged (or migrated) off-screen: always keep a grabbable header edge.
        p.X = Clamp(p.X, -(p.Width - MinVisibleEdge), Math.Max(0, vw - MinVisibleEdge));
        p.Y = Clamp(p.Y, 0, Math.Max(0, vh - MinVisibleEdge));
        p.Z = Clamp(p.Z, 0, 9999);
        if (p.Opacity is not ("solid" or "high" or "medium" or "low")) p.Opacity = "solid";
        return p;
    }

    /// <summary>
    /// v2.15.1: the geometry a panel takes when snapped to an edge or corner.
    ///
    /// Replaces v2.15.0's dock rails, which stretched a panel the full length of an edge — the
    /// operator's words: "it extends it super long instead of into a confined space". Halves and
    /// quadrants give a predictable, bounded region instead.
    ///
    /// Computed here rather than in JavaScript for the same reason the dock budgets were: this is
    /// arithmetic with edge cases (odd viewport sizes, minimum panel sizes on a small screen), and
    /// this repo has no browser test harness. Returning a plain tuple keeps it trivially testable.
    /// </summary>
    /// <param name="zone">left, right, top, bottom, or a corner such as top-left.</param>
    public static (int X, int Y, int Width, int Height)? SnapRegion(string? zone, int vw, int vh)
    {
        if (string.IsNullOrWhiteSpace(zone)) return null;

        var halfW = Math.Max(MinPanelWidth, vw / 2);
        var halfH = Math.Max(MinPanelHeight, vh / 2);
        // Right/bottom halves take the remainder so an odd viewport leaves no dead pixel column.
        var restW = Math.Max(MinPanelWidth, vw - halfW);
        var restH = Math.Max(MinPanelHeight, vh - halfH);

        return zone switch
        {
            "left"         => (0,     0,     halfW, vh),
            "right"        => (halfW, 0,     restW, vh),
            "top"          => (0,     0,     vw,    halfH),
            "bottom"       => (0,     halfH, vw,    restH),
            "top-left"     => (0,     0,     halfW, halfH),
            "top-right"    => (halfW, 0,     restW, halfH),
            "bottom-left"  => (0,     halfH, halfW, restH),
            "bottom-right" => (halfW, halfH, restW, restH),
            _ => null,
        };
    }

    public static readonly string[] SnapZones =
    {
        "left", "right", "top", "bottom",
        "top-left", "top-right", "bottom-left", "bottom-right",
    };

    public const int MinPanelWidth = 200;
    public const int MinPanelHeight = 80;

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
