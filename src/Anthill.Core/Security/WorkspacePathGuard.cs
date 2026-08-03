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
public sealed class WorkspacePathGuard
{
    /// <summary>The root this guard was BUILT with. Not necessarily the one it enforces — see <see cref="EffectiveRoot"/>.</summary>
    public string Root { get; }

    public WorkspacePathGuard(string? root = null)
    {
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

    public string ResolveSafePath(string requestedPath)
    {
        // Resolved against the EFFECTIVE root, so a relative path an agent supplies lands inside the
        // mission workspace rather than in the live checkout — and an absolute path pointing at the
        // live checkout fails the containment check below, which is the whole point.
        var root = EffectiveRoot;

        var requested = requestedPath;
        if (!Path.IsPathRooted(requested)) requested = Path.Combine(root, requested);
        var resolved = Path.GetFullPath(requested);

        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!resolved.Equals(root, StringComparison.Ordinal) &&
            !resolved.StartsWith(rootWithSep, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Access denied. Path is outside allowed workspace root: {root}");
        return resolved;
    }

    public bool IsBlockedPath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Select(p => p.ToLowerInvariant());
        return parts.ToHashSet().Overlaps(AnthillRuntime.BlockedPathParts);
    }
}
