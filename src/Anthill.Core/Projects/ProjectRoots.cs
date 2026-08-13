using Anthill.Core.Configuration;

namespace Anthill.Core.Projects;

/// <summary>
/// Where a project's work actually happens. v0.3.8.52 (field report: "all the projects have the
/// same set working directory").
///
/// A project with no explicit path used to FALL BACK to the one shared workspace root — the files
/// pane showed every project the same tree, and the chat lane's agent ran every project in the
/// same directory, reporting the same branch to an operator who had just created a brand-new
/// project and said nothing but hello. Which is exactly backwards: projects should share one
/// PARENT, and each own its own tree beneath it.
///
/// The rule, one place:
///   • An explicit <see cref="Project.Path"/> wins — the operator pointed at a real tree.
///   • Otherwise the project gets ITS OWN directory under &lt;workspace root&gt;/projects/,
///     named slug-id (the slug for the operator's eyes, the id for uniqueness — two projects
///     named "Website" must not share a tree), created lazily on first use.
///   • No resolvable workspace root at all → null, and the caller says so; a null root is a
///     refusal to invent one, not a licence to roam (the AgentWorkspaceRoot rule).
///
/// Every consumer — the files pane, the repo badge, the chat lane's agent confinement, the
/// direct-edit sweep — resolves through here. Two copies of a boundary rule is one copy that
/// eventually disagrees.
/// </summary>
public static class ProjectRoots
{
    /// <summary>The one parent directory all defaulted project trees live under.</summary>
    public static string? SharedRoot
    {
        get
        {
            var root = AnthillRuntime.AllowedWorkspaceRoot;
            if (string.IsNullOrWhiteSpace(root) || root == ".") return null;
            try { return Path.Combine(AnthillRuntime.PathFromScript(root), "projects"); }
            catch { return null; }
        }
    }

    /// <summary>This project's own default directory. Null when no workspace root is configured.</summary>
    public static string? DefaultFor(Project project) =>
        SharedRoot is null ? null : Path.Combine(SharedRoot, $"{Slug(project.Name)}-{project.Id}");

    /// <summary>
    /// The directory this project's work happens in: its explicit path, else its own default
    /// tree, created on first use when <paramref name="create"/> is set. Null only when nothing
    /// is resolvable — which the caller must surface, never paper over with a shared root.
    /// </summary>
    public static string? Resolve(Project project, bool create = true)
    {
        if (!string.IsNullOrWhiteSpace(project.Path)) return project.Path;
        var fallback = DefaultFor(project);
        if (fallback is null) return null;
        if (!Directory.Exists(fallback))
        {
            if (!create) return fallback;   // callers that only NAME the tree need not create it
            try { Directory.CreateDirectory(fallback); }
            catch { return null; }          // an uncreatable default is no default
        }
        return fallback;
    }

    /// <summary>Filesystem-safe, human-readable: lowercase alphanumerics and dashes, bounded.</summary>
    internal static string Slug(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in (name ?? "").Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if ((c == ' ' || c == '-' || c == '_' || c == '.') && sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
            if (sb.Length >= 40) break;
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "project" : slug;
    }
}
