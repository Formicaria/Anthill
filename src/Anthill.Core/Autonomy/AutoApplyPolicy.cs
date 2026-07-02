using System.Text.RegularExpressions;
using Anthill.Core.Configuration;
using Anthill.Core.Domain;

namespace Anthill.Core.Autonomy;

/// <summary>Result of the auto-apply eligibility check for one patch proposal.</summary>
public sealed record AutoApplyDecision(bool Eligible, string Reason);

/// <summary>
/// Phase 5 gated auto-apply — the eligibility gate that decides whether the Director may apply a
/// coder patch WITHOUT human review. This is intentionally strict and fail-closed: a patch is
/// eligible only when every condition holds, and any doubt denies. Actually applying + verifying +
/// rolling back lives in the API layer (<c>AutoApplyRunner</c>); this class is the pure,
/// side-effect-free decision so it can be unit-tested exhaustively.
///
/// Conditions (all required):
/// <list type="bullet">
/// <item>the <c>autonomy_autoapply_enabled</c> master switch is on;</item>
/// <item>the change is <c>add</c> or <c>modify</c> only — never delete/rename;</item>
/// <item>the file path matches at least one operator glob in <c>autonomy_autoapply_paths</c>
///   (an EMPTY allowlist makes nothing eligible, so the feature is inert until deliberately widened);</item>
/// <item>the change is no larger than <c>autonomy_autoapply_max_lines</c> lines.</item>
/// </list>
/// The write gates and the "must build+test green afterward" check are enforced separately by the
/// runner; this gate is the front-line filter on <em>what</em> is even a candidate.
/// </summary>
public static class AutoApplyPolicy
{
    public static AutoApplyDecision Evaluate(PatchProposal patch)
    {
        if (!AnthillRuntime.AutonomyAutoApplyEnabled)
            return new AutoApplyDecision(false, "Auto-apply is disabled (autonomy_autoapply_enabled=false).");

        if (patch.ChangeType is not (PatchChangeType.Add or PatchChangeType.Modify))
            return new AutoApplyDecision(false, $"Change type '{patch.ChangeType.Value()}' is never auto-applied (add/modify only).");

        var path = NormalizePath(patch.FilePath);
        if (path.Length == 0)
            return new AutoApplyDecision(false, "Empty file path.");

        var globs = AnthillRuntime.AutonomyAutoApplyPaths;
        if (globs.Count == 0)
            return new AutoApplyDecision(false, "No auto-apply path allowlist configured — nothing is eligible.");
        if (!globs.Any(g => GlobMatches(NormalizePath(g), path)))
            return new AutoApplyDecision(false, $"Path '{path}' matches no auto-apply allowlist glob.");

        var lines = CountLines(patch.NewContent);
        if (lines > AnthillRuntime.AutonomyAutoApplyMaxLines)
            return new AutoApplyDecision(false, $"Change is {lines} lines; exceeds the {AnthillRuntime.AutonomyAutoApplyMaxLines}-line auto-apply cap.");

        return new AutoApplyDecision(true, $"Eligible: {path} ({lines} lines, within allowlist and size cap).");
    }

    private static string NormalizePath(string p) =>
        (p ?? "").Trim().Replace('\\', '/').TrimStart('.', '/');

    private static int CountLines(string? content)
    {
        if (string.IsNullOrEmpty(content)) return 0;
        // Count newlines + 1 for the final line if the content doesn't end in a newline.
        var n = content.Count(c => c == '\n');
        return content.EndsWith('\n') ? n : n + 1;
    }

    /// <summary>
    /// Matches a workspace-relative path against a glob. Supports <c>**</c> (any number of path
    /// segments, including none), <c>*</c> (any run of non-<c>/</c> characters), and <c>?</c>
    /// (one non-<c>/</c> character). Case-sensitive, anchored at both ends.
    /// </summary>
    internal static bool GlobMatches(string glob, string path)
    {
        var rx = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            // "/**" — an optional subtree, so "docs/**" matches "docs" itself and everything under it.
            if (c == '/' && i + 2 < glob.Length && glob[i + 1] == '*' && glob[i + 2] == '*')
            {
                rx.Append("(?:/.*)?");
                i += 2; // consumed '/', '*', '*'
            }
            else if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                rx.Append(".*"); // leading/standalone '**' — any characters including '/'
                i++;
            }
            else if (c == '*') rx.Append("[^/]*"); // single '*' — anything except a path separator
            else if (c == '?') rx.Append("[^/]");
            else rx.Append(Regex.Escape(c.ToString()));
        }
        rx.Append('$');
        return Regex.IsMatch(path, rx.ToString());
    }
}
