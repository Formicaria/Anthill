namespace Anthill.Core.Security;

/// <summary>
/// THE ONE ANSWER to "is this path inside that root". v0.3.8.59, PLAN.md §1b S1.
///
/// Every filesystem boundary in the colony resolves through here: the workspace guard (and so the
/// file tools, the patch applier, the sandbox, verification and repository indexing), and the Files
/// pane. Two implementations of a containment rule is one implementation that eventually disagrees
/// with the other, and the disagreement is always discovered from the outside.
///
/// WHAT WAS WRONG, in two independent ways.
///
/// 1. PREFIX WITHOUT A SEPARATOR. The Files pane asked
///    <c>full.StartsWith(root, StringComparison.Ordinal)</c>. A project rooted at <c>/srv/project</c>
///    therefore served <c>../project-secret/key.txt</c>, which normalises to
///    <c>/srv/project-secret/key.txt</c> — a SIBLING whose name merely begins with the root string.
///    It is not traversal in the <c>..</c> sense the check was written against; the <c>..</c> is
///    gone by the time the comparison runs. That one helper fed read, create and edit alike.
///
/// 2. LINKS ARE NOT RESOLVED. <see cref="Path.GetFullPath(string)"/> is a LEXICAL operation. It
///    removes <c>..</c> and <c>.</c> and normalises separators, and it knows nothing about the
///    filesystem — so a symlink or Windows junction inside the root, pointing outside it, produced a
///    resolved path still textually under the root, which every containment check then passed. The
///    workspace guard had the separator check right and this wrong, so it was escapable by any
///    process that could create a link in the workspace — including the coding agent working there.
///
///    `RepositoryIndex` stated the opposite in a comment: "a symlink pointing out of the workspace
///    resolves outside the root and is refused here — the one traversal case a hand-rolled walk gets
///    wrong, and the reason this does not roll its own." The walk deferred to the guard precisely
///    because the guard was believed to handle this. It did not. A declaration that disagrees with
///    the runtime, sitting in the security boundary, reasoning correctly from a false premise.
///
/// HOW THIS RESOLVES. Lexical normalisation is not enough and neither is resolving the last
/// component: an intermediate component can be the link. So the path is walked from the volume root
/// downward and EVERY component is resolved, each following its own chain of links, bounded by
/// <see cref="MaxLinkDepth"/>. Components that do not exist cannot be links and are appended
/// literally — which is what lets a file be CREATED at a path whose parent is real and whose leaf is
/// not, while still resolving that parent honestly.
///
/// WHAT THIS DOES NOT CLOSE, said plainly because a security note that overstates its reach is worse
/// than none. This is resolution-time containment. It does not close the TOCTOU race in which a
/// component is replaced by a link BETWEEN this check and the caller's open — closing that needs
/// handle-relative, no-follow syscalls (<c>openat</c> with <c>O_NOFOLLOW</c>) that .NET does not
/// expose portably. The window is narrow and it is real, it requires an attacker already able to
/// write inside the workspace, and it is recorded in PLAN.md §1b S1 as remaining rather than
/// described here as handled.
/// </summary>
public static class PathContainment
{
    /// <summary>
    /// How many links one component may chain through before the path is refused. Matches the
    /// conventional POSIX <c>MAXSYMLINKS</c>. A cycle (<c>a -&gt; b -&gt; a</c>) is not an unusual
    /// accident — it is the obvious way to make a resolver hang, so the bound is the defence and the
    /// refusal is deliberate rather than a stack overflow.
    /// </summary>
    public const int MaxLinkDepth = 40;

    /// <param name="Path">The fully resolved path when allowed; the root when refused, so a caller
    /// that ignores <paramref name="Allowed"/> fails safe rather than reaching for a null.</param>
    /// <param name="Reason">Operator-facing, and null exactly when allowed.</param>
    public sealed record Result(bool Allowed, string Path, string? Reason);

    /// <summary>Windows paths are case-insensitive; every other filesystem the colony runs on is not.
    /// Comparing with the wrong one is an escape on Windows and a false refusal on Linux.</summary>
    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Resolve <paramref name="requested"/> against <paramref name="root"/> and say whether it is
    /// contained.
    ///
    /// A rooted <paramref name="requested"/> is taken as-is (and then has to survive containment on
    /// its own merits); a relative one is joined to the root. That asymmetry is deliberate and is the
    /// existing contract — an agent passing an absolute path outside the workspace must be refused,
    /// not silently re-based into it, because silently re-basing writes a file somewhere nobody asked
    /// for and reports success.
    /// </summary>
    public static Result Resolve(string root, string? requested)
    {
        if (string.IsNullOrWhiteSpace(root))
            return new Result(false, root ?? "", "no workspace root is configured");

        string realRoot;
        try { realRoot = CanonicalRoot(root); }
        catch (Exception error)
        {
            // A root that cannot be resolved refuses everything. Falling back to the lexical root
            // would mean an unreadable or looping root quietly downgrades to the weaker check.
            return new Result(false, root, $"the workspace root could not be resolved: {error.Message}");
        }

        return ResolveUnder(realRoot, requested);
    }

    /// <summary>
    /// The root, fully resolved. Separated from <see cref="ResolveUnder"/> so a caller resolving
    /// MANY paths against ONE root pays for the root once — see <c>WorkspacePathGuard</c>, which
    /// memoizes it per instance.
    ///
    /// v0.3.8.59, second round: this split is a PERFORMANCE FIX with correctness consequences, so it
    /// is worth being precise about. The first cut walked the whole absolute path per call, probing
    /// two <c>FileSystemInfo</c> objects per component. `RepositoryIndex` pushes up to
    /// <c>MaxFiles</c> = 20,000 paths through the guard at ~10 components each — about 380,000
    /// filesystem probes where the old lexical check did string arithmetic. A composed acceptance
    /// mission went from fast to twenty seconds and failed on time. A security check nobody can
    /// afford to run is a security check that gets removed.
    /// </summary>
    public static string CanonicalRoot(string root) =>
        Canonicalize(System.IO.Path.GetFullPath(root));

    /// <summary>
    /// Containment against a root that is ALREADY canonical.
    ///
    /// The saving is not a shortcut past the check: when the candidate is lexically under the real
    /// root, only the components BELOW it can still be links, because the root's own components have
    /// already been resolved. A candidate that is lexically elsewhere gets the full walk, since it
    /// may be a link pointing back INTO the root and that is legitimately allowed.
    /// </summary>
    public static Result ResolveUnder(string realRoot, string? requested)
    {
        var raw = requested ?? "";
        string candidate;
        try
        {
            candidate = System.IO.Path.IsPathRooted(raw)
                ? System.IO.Path.GetFullPath(raw)
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(realRoot, raw));
        }
        catch (Exception error) { return new Result(false, realRoot, $"the path is not usable: {error.Message}"); }

        string resolved;
        try
        {
            resolved = IsWithin(candidate, realRoot)
                ? CanonicalizeTail(realRoot, candidate[realRoot.Length..])
                : Canonicalize(candidate);
        }
        catch (Exception error) { return new Result(false, realRoot, error.Message); }

        return IsWithin(resolved, realRoot)
            ? new Result(true, resolved, null)
            : new Result(false, realRoot, $"Access denied. Path is outside allowed workspace root: {realRoot}");
    }

    /// <summary>
    /// Containment: the path IS the root, or it is under the root with a separator between them.
    ///
    /// The separator is the whole of the sibling-prefix fix. Without it <c>/srv/project-secret</c>
    /// is "inside" <c>/srv/project</c>, and no amount of link resolution above catches that, because
    /// nothing about the path is malformed — the two roots are simply different directories whose
    /// names share a prefix.
    /// </summary>
    public static bool IsWithin(string resolved, string realRoot)
    {
        var root = realRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

        // A volume root ("/" or "C:\") trims to "" or "C:", and re-adding the separator below would
        // produce a root nothing is under. Contain against the untrimmed form in that case.
        if (root.Length == 0 || root.EndsWith(':')) root = realRoot;

        if (resolved.Equals(root, Comparison)) return true;

        var withSeparator = root.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? root
            : root + System.IO.Path.DirectorySeparatorChar;
        return resolved.StartsWith(withSeparator, Comparison);
    }

    /// <summary>
    /// Walk the path from the volume root and resolve EVERY component through its links.
    ///
    /// Resolving only the final component is the tempting shortcut and it is wrong: given
    /// <c>&lt;root&gt;/escape/secrets.txt</c> where <c>escape</c> is the link, the leaf is an
    /// ordinary file and a leaf-only check reports containment for a path that reads outside the
    /// root. The escape is always available one directory up from wherever the check stops.
    /// </summary>
    public static string Canonicalize(string absolutePath)
    {
        var volumeRoot = System.IO.Path.GetPathRoot(absolutePath);
        if (string.IsNullOrEmpty(volumeRoot))
            throw new ArgumentException($"not an absolute path: {absolutePath}", nameof(absolutePath));

        return CanonicalizeTail(volumeRoot, absolutePath[volumeRoot.Length..]);
    }

    /// <summary>
    /// Resolve <paramref name="tail"/>'s components beneath an already-resolved
    /// <paramref name="resolvedBase"/>. <see cref="Canonicalize"/> is this with the volume root as
    /// the base, which is why there is one implementation rather than two that drift.
    /// </summary>
    private static string CanonicalizeTail(string resolvedBase, string tail)
    {
        var parts = tail.Split(
            new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = resolvedBase;
        var hops = 0;

        foreach (var part in parts)
        {
            var next = System.IO.Path.Combine(current, part);

            while (LinkTargetOf(next) is { } target)
            {
                if (++hops > MaxLinkDepth)
                    throw new UnauthorizedAccessException(
                        $"the path passes through more than {MaxLinkDepth} links and was refused as a "
                      + "possible link cycle");

                // A link target may be relative, and it is relative to the link's OWN directory —
                // not to the process working directory, which is what Path.GetFullPath(string) would
                // assume and is a real way to resolve a link to somewhere it does not point.
                next = System.IO.Path.IsPathRooted(target)
                    ? System.IO.Path.GetFullPath(target)
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(next) ?? resolvedBase, target));
            }

            current = next;
        }

        return current;
    }

    /// <summary>
    /// The link target of one component, or null when it is not a link — including when it does not
    /// exist, which is the case that lets a new file be created inside the root.
    ///
    /// Both types are tried because a component may be a link to a directory or to a file, and the
    /// wrong <c>FileSystemInfo</c> subtype reports nothing. A DANGLING link is still a link and still
    /// resolves: its target may not exist yet, and treating it as an ordinary missing entry would let
    /// a link be planted now and pointed at afterwards.
    /// </summary>
    private static string? LinkTargetOf(string path)
    {
        // ONE cheap syscall decides for the overwhelming majority of components. Almost nothing in a
        // repository is a reparse point, and the previous shape constructed two FileSystemInfo
        // objects and read two properties for every component of every path — the cost that made a
        // 20,000-file index walk unaffordable. GetAttributes throws for a path that does not exist,
        // which is the same answer as "not a link" and is handled by the catch.
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) return null;
        }
        catch { return null; }

        try
        {
            var directory = new DirectoryInfo(path);
            if (directory.LinkTarget is { } directoryTarget) return directoryTarget;
        }
        catch { /* unreadable is not "not a link"; the file probe below still gets its turn */ }

        try
        {
            var file = new FileInfo(path);
            if (file.LinkTarget is { } fileTarget) return fileTarget;
        }
        catch { /* same */ }

        return null;
    }
}
