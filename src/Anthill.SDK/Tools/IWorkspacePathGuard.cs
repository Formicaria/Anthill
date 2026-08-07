namespace Anthill.SDK.Tools;

/// <summary>
/// The only directory tree a tool may touch, and the question every file tool asks before acting.
/// v3.8.16.
///
/// WHY THIS IS A CONTRACT AND NOT A MOVED CLASS. The implementation reads the CURRENT MISSION's
/// workspace through an ambient scope, and mission workspaces stay in the core — that decision was
/// measured in phase 5c and holds: a workspace is a property of a mission, and missions are what the
/// core IS. So the guard cannot move out with the tools that use it. It arrives as a contract the
/// core implements and the composition root hands to the module, exactly as
/// <c>IPheromoneMemory</c> does.
///
/// THE NARROWING IS THE POINT. <see cref="Root"/> is what the guard was built with;
/// <see cref="EffectiveRoot"/> is what it enforces right now, which is the mission's workspace when
/// one is in scope. A tool holds ONE guard for the life of the process and still writes into the
/// right workspace for each mission, because the narrowing happens inside the implementation rather
/// than by handing the tool a new guard per mission. A module that had to be re-injected per mission
/// would be a module that knows about missions.
/// </summary>
public interface IWorkspacePathGuard
{
    /// <summary>The root this guard was BUILT with. Not necessarily the one it enforces.</summary>
    string Root { get; }

    /// <summary>
    /// The root actually enforced right now: the current mission's workspace when one is in scope,
    /// otherwise <see cref="Root"/>. It only ever NARROWS.
    /// </summary>
    string EffectiveRoot { get; }

    /// <summary>
    /// Resolve a caller-supplied path against <see cref="EffectiveRoot"/> and refuse anything that
    /// escapes it.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">
    /// The resolved path lies outside the effective root. THROWS rather than returning a flag,
    /// deliberately: every caller must handle it, and a bool that a caller forgets to check is a
    /// path traversal that succeeds.
    /// </exception>
    string ResolveSafePath(string requestedPath);

    /// <summary>
    /// True when the path contains a blocked segment — <c>.git</c>, <c>data</c>, virtualenvs,
    /// caches. Separate from <see cref="ResolveSafePath"/> because containment and blocklisting are
    /// different questions: a path can be perfectly inside the workspace and still be somewhere no
    /// tool should read or write.
    /// </summary>
    bool IsBlockedPath(string path);
}
