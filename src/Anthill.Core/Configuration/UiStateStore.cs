using System.Text.Json;

namespace Anthill.Core.Configuration;

/// <summary>
/// Persists the colony console's display state — how the human has arranged and named their
/// anthill. This is presentation only (custom ant names, accent colours, node positions, and
/// free-form layout prefs); it never touches model routing or security gates, so it lives in a
/// plain JSON file next to the database rather than in the schema-versioned config.
///
/// The store is deliberately schema-light: the UI owns the shape and we round-trip it verbatim.
/// A corrupt or missing file simply yields an empty state, so the dashboard always loads.
/// </summary>
public static class UiStateStore
{
    private static readonly object _lock = new();

    private static string FilePath() =>
        Path.Combine(AnthillRuntime.WorkspaceRootPath, "ui_state.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Reads the saved console state, or an empty object on first run / parse failure.</summary>
    public static Dictionary<string, object?> Load()
    {
        lock (_lock)
        {
            var path = FilePath();
            if (!File.Exists(path)) return DefaultState();
            try
            {
                var text = File.ReadAllText(path);
                var doc = JsonSerializer.Deserialize<Dictionary<string, object?>>(text);
                return doc is null || doc.Count == 0 ? DefaultState() : doc;
            }
            catch
            {
                return DefaultState();
            }
        }
    }

    /// <summary>Overwrites the saved console state with the supplied document.</summary>
    public static Dictionary<string, object?> Save(JsonElement state)
    {
        lock (_lock)
        {
            var path = FilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state, Options));
            return Load();
        }
    }

    /// <summary>
    /// The starting layout the console falls back to before the human customises anything: the
    /// six worker castes plus the Queen, with their default labels. Positions are left null so the
    /// client lays them out on its radial default until the user drags them.
    /// </summary>
    private static Dictionary<string, object?> DefaultState() => new()
    {
        ["version"] = 1,
        ["ants"] = new Dictionary<string, object?>(),   // antId -> {name,color,x,y}
        ["layout"] = new Dictionary<string, object?>(),
    };

    /// <summary>
    /// v2.14.2: repair the topology-first dashboard workspace inside a loaded state document.
    /// Called by the UI-state endpoint with the panel/overlay ids the client actually supports, so
    /// validation, clamping, and recovery happen server-side where they are unit-tested. Every
    /// other key — ant names, colours, positions, map preferences — is returned untouched: a
    /// corrupt panel layout must never cost the operator their colony customization.
    /// </summary>
    public static Dictionary<string, object?> WithSanitizedWorkspace(
        Dictionary<string, object?> state,
        IReadOnlyCollection<string> knownPanelIds,
        IReadOnlyCollection<string> knownOverlayIds,
        int viewportWidth = DashboardWorkspaceState.DefaultViewportWidth,
        int viewportHeight = DashboardWorkspaceState.DefaultViewportHeight)
        => DashboardWorkspaceState.SanitizeInto(state, knownPanelIds, knownOverlayIds, viewportWidth, viewportHeight);
}
