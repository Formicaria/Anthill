using Anthill.Core.Configuration;

namespace Anthill.Core.Security;

/// <summary>
/// Confines every file operation to the configured agent workspace root.
///
/// <see cref="ResolveSafePath"/> resolves a requested path against the root, fully
/// canonicalises it, and refuses anything that escapes the root — the .NET equivalent
/// of the Python guard's <c>Path.resolve().relative_to(root)</c> check, which is what
/// stops <c>../</c> traversal and absolute-path breakouts.
/// </summary>
public sealed class WorkspacePathGuard : IWorkspacePathGuard
{
    /// <summary>The root this guard was BUILT with. Not necessarily the one it enforces — see <see cref="EffectiveRoot"/>.</summary>
    public string Root { get; }

    private readonly IToolRuntimeOptions? _options;

    /// <param name="options">
    /// The gates this guard enforces. v3.8.18 — added because <see cref="IsBlockedPath"/> read
    /// <c>AnthillRuntime.BlockedPathParts</c> directly, so a host composed from explicit options
    /// still had its blocked-path list answered by process-global state. A guard built for one host
    /// must not consult another's configuration.
    ///
    /// Optional, and <c>null</c> keeps the previous behaviour exactly: it resolves through
    /// <see cref="SafetyPolicy"/>, which the core installs from a module initializer. Every one of
    /// the thirty existing call sites passes a root and nothing else, and none of them needed to
    /// change.
    /// </param>
    public WorkspacePathGuard(string? root = null, IToolRuntimeOptions? options = null)
    {
        _options = options;
        var raw = root ?? AnthillRuntime.AllowedWorkspaceRoot;
        Root = Path.IsPathRooted(raw)
            ? Path.GetFullPath(raw)
            : Path.GetFullPath(Path.Combine(AnthillRuntime.ScriptDir, raw));
    }

    /// <summary>
    /// v3.5.0 — the root actually enforced right now: the current mission's workspace when one is
    /// in scope, otherwise the configured root.
    ///
    /// This is what closes the exit gate "a code mission cannot modify the active checkout through
    /// any agent path". Every write tool is a startup-constructed singleton rooted at the live
    /// checkout, so before this the active checkout was the only thing they could write to. A
    /// mission's workspace is a property of the MISSION, not of the process — two parallel missions
    /// have different ones — so it arrives ambiently rather than as a constructor argument, the same
    /// mechanism <c>ModelCallScope</c> already uses for mission cancellation.
    ///
    /// It only ever NARROWS. Outside a scope this is the configured root and behaviour is unchanged,
    /// which is why the CLI, operator tooling and existing tests are unaffected.
    /// </summary>
    public string EffectiveRoot
    {
        get
        {
            var scoped = Workspaces.MissionWorkspaceScope.CurrentRoot;
            return scoped is null ? Root : Path.GetFullPath(scoped);
        }
    }

    /// <summary>
    /// The canonical form of the last effective root this guard saw, so a caller resolving thousands
    /// of paths against one root does not re-walk it every time.
    ///
    /// Per INSTANCE, not static, and that is the whole safety argument: a guard's life is one
    /// operation — `RepositoryIndex` builds one and walks a workspace with it — so nothing here
    /// outlives the work it was created for, and no two operations share a stale answer. A process-
    /// wide cache would have to reason about a root being replaced between missions; this does not.
    ///
    /// Keyed by the effective root's raw string because <see cref="EffectiveRoot"/> changes when a
    /// mission workspace scope opens or closes, and the same guard is used on both sides of that.
    /// </summary>
    private string? _cachedRootKey;
    private string? _cachedCanonicalRoot;

    /// <summary>
    /// Resolved against the EFFECTIVE root, so a relative path an agent supplies lands inside the
    /// mission workspace rather than in the live checkout — and an absolute path pointing at the
    /// live checkout fails containment, which is the whole point.
    ///
    /// v0.3.8.59 (PLAN.md §1b S1): the containment rule moved to <see cref="PathContainment"/> and
    /// is no longer implemented here. The separator check this method already had was correct; what
    /// it did not do was resolve LINKS. <see cref="Path.GetFullPath(string)"/> is lexical — it strips
    /// <c>..</c> and knows nothing of the filesystem — so a symlink or junction inside the workspace
    /// pointing outside it produced a path still textually under the root, and every caller passed.
    /// Twenty call sites resolve through this method, so all twenty were escapable by anything that
    /// could create a link in the workspace, which includes the coding agent working in it.
    ///
    /// Throwing is kept deliberately: those twenty callers catch
    /// <see cref="UnauthorizedAccessException"/> and treat it as refusal, and changing a security
    /// boundary's failure MODE in the same commit that changes its logic is how a refusal quietly
    /// becomes a null somebody dereferences into a pass.
    /// </summary>
    public string ResolveSafePath(string requestedPath)
    {
        var root = EffectiveRoot;

        if (!string.Equals(_cachedRootKey, root, StringComparison.Ordinal))
        {
            try { _cachedCanonicalRoot = PathContainment.CanonicalRoot(root); }
            catch (Exception error)
            {
                // A root that will not resolve refuses everything, exactly as PathContainment.Resolve
                // does — the cache must not become a route to a weaker answer.
                _cachedRootKey = null;
                throw new UnauthorizedAccessException(
                    $"the workspace root could not be resolved: {error.Message}");
            }
            _cachedRootKey = root;
        }

        var decision = PathContainment.ResolveUnder(_cachedCanonicalRoot!, requestedPath);
        if (!decision.Allowed) throw new UnauthorizedAccessException(decision.Reason);
        return decision.Path;
    }

    public bool IsBlockedPath(string path)
    {
        // v3.8.18 — the injected gates when this guard was given any, the installed policy otherwise.
        // Previously this read AnthillRuntime directly, which meant a host built from explicit
        // options answered "is this path blocked" from global state regardless.
        var blocked = (_options ?? SafetyPolicy.RequiredToolOptions).BlockedPathParts;
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Select(p => p.ToLowerInvariant());
        return parts.ToHashSet().Overlaps(blocked);
    }
}
