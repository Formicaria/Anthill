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
/// The rule, one place (third field round — the operator's correction of the second):
///   • The working directory is the OPERATOR'S explicit act, made in the files pane BEFORE the
///     first chat. The colony only SUGGESTS — <see cref="DefaultFor"/> names each project its
///     own tree under &lt;workspace root&gt;/projects/, slug-id (the slug for the operator's
///     eyes, the id for uniqueness — two projects named "Website" must not share a tree) — and
///     nothing is created until the operator accepts.
///   • No resolvable workspace root → null suggestion, and the caller says so; a null root is a
///     refusal to invent one, not a licence to roam (the AgentWorkspaceRoot rule).
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
    /// v0.3.8.52 (third field round): the colony's OWN source tree — the ANTHILL checkout this
    /// server runs from, when it runs from one. It "lives alongside the project directory no
    /// matter what" (operator's rule): granted as reach on every conversation so the colony can
    /// self-improve before, during or after any project's work. Null on an installed binary with
    /// no checkout — a colony without source simply has nothing extra to reach.
    /// </summary>
    public static string? ColonySource()
    {
        try { return RepoOps.TopLevel(AppContext.BaseDirectory); }
        catch { return null; }
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
