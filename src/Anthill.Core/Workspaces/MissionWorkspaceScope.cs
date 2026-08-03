namespace Anthill.Core.Workspaces;

/// <summary>
/// v3.5.0 — the workspace the CURRENT mission may write into, ambient and async-flow-local.
///
/// The problem it solves, stated exactly. Every write tool in the colony is constructed once, at
/// startup, against a single <c>WorkspacePathGuard</c> rooted at the live checkout. So
/// <c>write_text_file</c> and <c>apply_patch</c> write into the operator's working tree, and the
/// v3.5.0 exit gate — "a code mission cannot modify the active checkout through any agent path" —
/// is not merely unmet, it is inverted: the active checkout is the ONLY thing they can write to.
///
/// The root cannot be a constructor argument because it is a property of the MISSION, not of the
/// process: two missions running in parallel have different workspaces, and the tools are shared
/// singletons. Threading a workspace through every tool constructor, every ant, and every dispatch
/// would be a large refactor of code that has no other reason to change.
///
/// So it is ambient, following <c>ModelCallScope</c> — the same shape, for the same reason, already
/// proven here for mission cancellation. <see cref="AsyncLocal{T}"/> flows across the
/// <c>Task.Run</c> continuations parallel task execution uses, while staying isolated per mission.
///
/// OUTSIDE a scope there is no workspace and the guard keeps its configured root. That default
/// matters: the CLI, the operator's own tools, and every existing test behave exactly as they did
/// before this existed. This mechanism narrows what a mission may reach; it never widens it.
/// </summary>
public static class MissionWorkspaceScope
{
    private static readonly AsyncLocal<MissionWorkspace?> Ambient = new();

    /// <summary>The current mission's workspace, or null outside any scope.</summary>
    public static MissionWorkspace? Current => Ambient.Value;

    /// <summary>
    /// The root every file operation in the current flow is confined to, or null when unscoped.
    ///
    /// Null for a workspace that is not <see cref="MissionWorkspace.Usable"/> — a cleaned or
    /// orphaned workspace has no directory, and confining writes to a path that does not exist would
    /// turn every write into a confusing filesystem error rather than a clear refusal.
    /// </summary>
    public static string? CurrentRoot =>
        Ambient.Value is { } workspace && workspace.Usable && workspace.Root.Length > 0
            ? workspace.Root
            : null;

    /// <summary>
    /// Enter a scope binding <paramref name="workspace"/> as the ambient mission workspace.
    /// Disposing restores the previous one, so scopes nest safely.
    /// </summary>
    public static IDisposable Enter(MissionWorkspace? workspace)
    {
        var previous = Ambient.Value;
        Ambient.Value = workspace;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly MissionWorkspace? _previous;
        private bool _disposed;
        public Scope(MissionWorkspace? previous) => _previous = previous;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = _previous;
        }
    }
}
