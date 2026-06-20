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
    public string Root { get; }

    public WorkspacePathGuard(string? root = null)
    {
        var raw = root ?? AnthillRuntime.AllowedWorkspaceRoot;
        Root = Path.IsPathRooted(raw)
            ? Path.GetFullPath(raw)
            : Path.GetFullPath(Path.Combine(AnthillRuntime.ScriptDir, raw));
    }

    public string ResolveSafePath(string requestedPath)
    {
        var requested = requestedPath;
        if (!Path.IsPathRooted(requested)) requested = Path.Combine(Root, requested);
        var resolved = Path.GetFullPath(requested);

        var rootWithSep = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        if (!resolved.Equals(Root, StringComparison.Ordinal) &&
            !resolved.StartsWith(rootWithSep, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Access denied. Path is outside allowed workspace root: {Root}");
        return resolved;
    }

    public bool IsBlockedPath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Select(p => p.ToLowerInvariant());
        return parts.ToHashSet().Overlaps(AnthillRuntime.BlockedPathParts);
    }
}
