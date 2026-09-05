namespace Anthill.Core.Projects;

/// <summary>
/// WHICH PROJECT'S MODEL ROUTING GOVERNS THE CURRENT FLOW. v0.3.8.124.
///
/// WHAT THIS EXISTS FOR. Routing was colony-global: one priority model and fourteen per-role routes
/// in `config.json`, edited on the Ant Inspector page. An operator running one project against a
/// local model and another against Claude had no way to say so — every change was to the whole
/// colony, and the only workaround was rewriting the routes between missions.
///
/// The plumbing was already half-built and had been for two releases. `Project.DefaultProvider` and
/// `Project.DefaultModel` have existed since v0.3.8.48, are persisted by `SqliteMemory.Projects`,
/// are written by `PATCH /projects/{id}`, and are read by NOTHING — a settable, saved, never-honoured
/// preference, which is the shape of a feature that was designed and then not connected. The same is
/// true of `ProjectSchedule.Provider` / `.Model`. This is that connection, plus the per-role half
/// the fields alone could not express.
///
/// AMBIENT, FOR THE REASON <see cref="Conversations.ConversationScope"/> AND
/// `MissionWorkspaceScope` ARE. `ModelRouter` is one object per Queen, shared by every ant, and the
/// project is a property of the FLOW rather than of the router. Threading a project id through
/// `ModelRouter.SendCore` would mean threading it through every `Generate`, `GenerateTyped` and
/// `SendTyped` overload, then through every ant that calls one, then through `ToolCallingLoop` — a
/// large refactor of code with no other reason to change, to carry a value that is constant for the
/// whole mission. `SendCore` does already receive a `missionId`, but it uses it only for event
/// logging, and resolving mission → project → routes on every model call would put two database
/// reads in front of every generation.
///
/// OUTSIDE A SCOPE NOTHING CHANGES. A mission with no project, a conversation outside one, an
/// autonomy objective, a scheduled run — all resolve exactly as they did before this file existed,
/// through the colony-wide route in `config.json`. That is the property that makes this safe to add
/// to a shared singleton: it can only ever NARROW to a project's own choice, and its absence is the
/// previous behaviour rather than a gap.
///
/// PRECEDENCE, and it mirrors the global chain rather than inventing a second grammar:
///
///     project priority  →  project role route  →  colony priority  →  colony role route
///                                              →  colony `fallback` route  →  built-in local model
///
/// A project priority outranks a project role route for the same reason the colony priority
/// outranks a colony role route — "use this model for everything here" is one decision, and
/// expressing it by rewriting every role is how half of them go stale. The role's own route is not
/// discarded, only outranked: it stays the failover target when the priority route is unhealthy.
/// </summary>
public static class ProjectRoutingScope
{
    /// <summary>
    /// One project's routing, resolved once and carried for the flow.
    /// </summary>
    /// <param name="ProjectId">Whose routing this is. Recorded so an event can say which project.</param>
    /// <param name="PriorityProvider">The project-wide priority provider, or empty for none.</param>
    /// <param name="PriorityModel">The project-wide priority model, or empty for none.</param>
    /// <param name="Routes">
    /// role → (provider, model), for the roles this project overrides. A role absent here is not
    /// "no route" — it falls through to the colony's, which is what makes a project a set of
    /// overrides rather than a replacement an operator has to fill in completely.
    /// </param>
    public sealed record Routing(
        string ProjectId,
        string PriorityProvider,
        string PriorityModel,
        IReadOnlyDictionary<string, (string Provider, string Model)> Routes)
    {
        /// <summary>Both halves, or neither. A provider with no model is not a route.</summary>
        public bool HasPriority =>
            !string.IsNullOrWhiteSpace(PriorityProvider) && !string.IsNullOrWhiteSpace(PriorityModel);

        /// <summary>True when this project overrides nothing — indistinguishable from no scope.</summary>
        public bool IsEmpty => !HasPriority && Routes.Count == 0;

        public static Routing None(string projectId) =>
            new(projectId, "", "", new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase));
    }

    private static readonly AsyncLocal<Routing?> Ambient = new();

    /// <summary>The routing in force, or null when this flow belongs to no project.</summary>
    public static Routing? Current => Ambient.Value;

    /// <summary>
    /// The project-wide priority route, or null when there is no project or it names none.
    ///
    /// Returned as a nullable tuple rather than as two out-parameters plus a bool because every
    /// caller wants the pair or nothing, and a caller that reads a provider without checking the
    /// flag is the way a half-set priority becomes a route to an empty model.
    /// </summary>
    public static (string Provider, string Model)? Priority =>
        Ambient.Value is { HasPriority: true } r ? (r.PriorityProvider, r.PriorityModel) : null;

    /// <summary>This project's own route for a role, or null when it does not override that role.</summary>
    public static (string Provider, string Model)? RouteFor(string? role)
    {
        var routing = Ambient.Value;
        if (routing is null || string.IsNullOrWhiteSpace(role)) return null;
        return routing.Routes.TryGetValue(role!, out var route) ? route : null;
    }

    /// <summary>Enter a scope. Disposing restores the previous one, so scopes nest safely.</summary>
    public static IDisposable Enter(Routing? routing)
    {
        var previous = Ambient.Value;
        Ambient.Value = routing;
        return new Scope(previous);
    }

    private sealed class Scope(Routing? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = previous;
        }
    }
}
