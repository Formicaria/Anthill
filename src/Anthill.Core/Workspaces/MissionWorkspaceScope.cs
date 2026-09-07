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
    /// The ambient workspace ONLY when something may write into it. v0.3.8.132.
    ///
    /// <see cref="Current"/> answers "what tree am I looking at"; this answers "what tree may I
    /// change". They were the same question until a read-only project scope existed, and the
    /// consumers that must not confuse them are the ones that run an agent CLI, harvest a diff as
    /// a patch set, or summarise changes — each of which, given the operator's live checkout,
    /// produces a confident and completely wrong result rather than an error.
    /// </summary>
    public static MissionWorkspace? CurrentWritable =>
        Ambient.Value is { Writable: true } writable ? writable : null;

    /// <summary>
    /// <see cref="CurrentRoot"/>, but null when the ambient scope is read-only. The property a
    /// caller wants whenever the root is about to be handed to something that writes.
    /// </summary>
    public static string? CurrentWritableRoot =>
        Ambient.Value is { Writable: true, Usable: true } w && w.Root.Length > 0 ? w.Root : null;

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
