using System.Text.RegularExpressions;
using Anthill.Core.Configuration;

namespace Anthill.Core.Common;

/// <summary>
/// Pure text helpers: truncation, token estimation, whitespace compaction, summary
/// creation, keyword extraction, and HTML stripping. Direct ports of the matching
/// free functions in the Python runtime, kept allocation-light for the hot context paths.
/// </summary>
public static partial class TextUtil
{
    public static string Truncate(string? text, int maxChars, string suffix = "...[truncated]")
    {
        if (text is null) return "";
        if (text.Length <= maxChars) return text;
        return text[..maxChars].TrimEnd() + $"\n{suffix}";
    }

    public static int EstimateTokenCount(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return Math.Max(1, text.Length / AnthillRuntime.TokenEstimateCharsPerToken);
    }

    public static string CompactWhitespace(string text) =>
        MultiNewline().Replace((text ?? "").Trim(), "\n\n");

    public static string CreateResultSummary(string? text, int maxChars = -1)
    {
        if (maxChars < 0) maxChars = AnthillRuntime.MaxResultSummaryChars;
        if (string.IsNullOrEmpty(text)) return "";
        var cleaned = CompactWhitespace(text);
        // Prefer leading content because most ants put summaries first; a later version
        // can swap this for a model-generated or embedding-backed summary.
        return Truncate(cleaned, maxChars, "...[summary truncated]");
    }

    public static HashSet<string> ExtractKeywords(string text)
    {
        var words = WordToken().Matches((text ?? "").ToLowerInvariant()).Select(m => m.Value);
        var stop = new HashSet<string>
        {
            "the", "and", "for", "with", "this", "that", "from", "into", "have", "what", "when",
            "where", "which", "would", "should", "could", "mission", "task", "result", "about",
            "your", "you", "are", "was", "were", "how",
        };
        return words.Where(w => w.Length > 3 && !stop.Contains(w)).ToHashSet();
    }

    public static string StripHtmlTags(string html)
    {
        var text = ScriptTag().Replace(html ?? "", " ");
        text = StyleTag().Replace(text, " ");
        text = AnyTag().Replace(text, " ");
        text = text.Replace("&amp;", "&").Replace("&quot;", "\"").Replace("&#x27;", "'")
                   .Replace("&lt;", "<").Replace("&gt;", ">");
        return CompactWhitespace(text);
    }

    public static string ExtractVerdict(string text)
    {
        var lowered = (text ?? "").ToLowerInvariant();
        foreach (var rawLine in lowered.Split('\n'))
        {
            var clean = rawLine.Trim().Replace("*", "").Replace("-", "").Trim();
            if (clean.StartsWith("verdict:"))
            {
                var verdict = clean["verdict:".Length..].Trim();
                if (verdict.Contains("verification failed") || verdict.Contains("failed")) return "failed";
                if (verdict.Contains("needs improvement") || verdict.Contains("improvement")) return "needs_improvement";
                if (verdict.Contains("verification passed") || verdict.Contains("passed")) return "passed";
            }
        }
        if (lowered.Contains("verification failed") || lowered.Contains("failed verification")) return "failed";
        if (lowered.Contains("needs improvement")) return "needs_improvement";
        if (lowered.Contains("verification passed") || lowered.Contains("passed verification")) return "passed";
        return "unknown";
    }

    public static string InferTaskType(string assignedAnt, string title = "", string description = "") => assignedAnt switch
    {
        "researcher" => "research",
        "file" => "file_inspection",
        "coder" => "patch_proposal",
        "builder" => "build_answer",
        "verifier" => "verification",
        "web" => "external_research",
        _ => "general",
    };

    public static bool ShouldUseWebSearch(string goal)
    {
        var lowered = (goal ?? "").ToLowerInvariant();
        return AnthillRuntime.WebSearchKeywords.Any(k => lowered.Contains(k));
    }

    [GeneratedRegex(@"\n{3,}")] private static partial Regex MultiNewline();
    [GeneratedRegex(@"[a-zA-Z0-9_]+")] private static partial Regex WordToken();
    [GeneratedRegex("<script.*?</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex ScriptTag();
    [GeneratedRegex("<style.*?</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex StyleTag();
    [GeneratedRegex("<[^>]+>")] private static partial Regex AnyTag();
}
