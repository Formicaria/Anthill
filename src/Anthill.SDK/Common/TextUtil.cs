using System.Text.RegularExpressions;
using Anthill.SDK.Tools;

namespace Anthill.SDK.Common;

/// <summary>
/// Pure text helpers: truncation, token estimation, whitespace compaction, summary
/// creation, keyword extraction, and HTML stripping. Direct ports of the matching
/// free functions in the Python runtime, kept allocation-light for the hot context paths.
///
/// v3.8.14 — moved from <c>Anthill.Core.Common</c>, the second half of phase 5c step 2. The widest
/// of the three helper moves by consumer count (18 files in src plus one test) and the narrowest by
/// configuration: of thirteen methods, exactly one reads a mutable setting.
/// <see cref="ShouldUseWebSearch"/> takes an optional <see cref="IToolRuntimeOptions"/>, which
/// already carried <see cref="IToolRuntimeOptions.WebSearchEnabled"/> and is therefore where the
/// keyword list belongs. The two caps were <c>const</c> and are declared here for the reason
/// <see cref="Validation"/> gives: a value that cannot vary should not be dressed as one that can.
/// </summary>
public static partial class TextUtil
{
    /// <summary>Default cap for <see cref="CreateResultSummary"/>. Was <c>AnthillRuntime.MaxResultSummaryChars</c>.</summary>
    public const int MaxResultSummaryChars = 900;

    /// <summary>Chars per token for the cheap estimate. Was <c>AnthillRuntime.TokenEstimateCharsPerToken</c>.</summary>
    public const int TokenEstimateCharsPerToken = 4;

    public static string Truncate(string? text, int maxChars, string suffix = "...[truncated]")
    {
        if (text is null) return "";
        if (text.Length <= maxChars) return text;
        return text[..maxChars].TrimEnd() + $"\n{suffix}";
    }

    /// <summary>
    /// Replaces invalid UTF-16 (lone/unpaired surrogate chars) with U+FFFD so the string can be
    /// serialized as JSON. <see cref="System.Text.Json"/> throws "Cannot transcode invalid UTF-16"
    /// on a lone surrogate, which — during response serialization, after an endpoint handler has
    /// returned — surfaces as an uncatchable empty HTTP 500. LLM-generated text (patch reasons,
    /// summaries, goals) occasionally contains lone surrogates, so we scrub before serializing.
    /// Fast path: strings with no surrogate chars are returned unchanged (no allocation).
    /// </summary>
    public static string SanitizeUtf16(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        var needsFix = false;
        for (var i = 0; i < s.Length; i++) if (char.IsSurrogate(s[i])) { needsFix = true; break; }
        if (!needsFix) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; }
                else sb.Append('�'); // high surrogate not followed by a low surrogate
            }
            else if (char.IsLowSurrogate(c)) sb.Append('�'); // low surrogate with no preceding high
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static int EstimateTokenCount(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return Math.Max(1, text.Length / TokenEstimateCharsPerToken);
    }

    /// <summary>v2.26.0: the same estimate straight from a character count — callers that only
    /// know a length must not allocate a fake string to divide it by four.</summary>
    public static int EstimateTokenCountFromChars(int chars) =>
        chars <= 0 ? 0 : Math.Max(1, chars / TokenEstimateCharsPerToken);

    public static string CompactWhitespace(string text) =>
        MultiNewline().Replace((text ?? "").Trim(), "\n\n");

    public static string CreateResultSummary(string? text, int maxChars = -1)
    {
        if (maxChars < 0) maxChars = MaxResultSummaryChars;
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

    /// <summary>
    /// The planner's type NORMALISER: when a model invents a task_type a role's contract refuses,
    /// this supplies the role's own default instead. v0.3.8.61 — the six specialist roles were
    /// missing, so the normaliser "repaired" an invalid tester type to "general", which the tester
    /// contract ALSO refuses. Watched live: every Director mission that planned a tester task
    /// blocked on it, the medic burned the full repair bound on a defect no repair can fix (the
    /// mismatch is in the PLAN), and the mission's score halved. The safety net was feeding the
    /// failure it existed to prevent. Every role with a contract now maps to a type INSIDE that
    /// contract, and a guard test holds the two in agreement structurally.
    /// </summary>
    public static string InferTaskType(string assignedAnt, string title = "", string description = "") => assignedAnt switch
    {
        "researcher" => "research",
        "file" => "file_inspection",
        "coder" => "patch_proposal",
        "builder" => "build_answer",
        "verifier" => "verification",
        "web" => "external_research",
        "tester" => "validation_check",
        "soldier" => "security_review",
        "medic" => "failure_diagnosis",
        "archivist" => "memory_consolidation",
        "ui_cartographer" => "ui_mapping",
        "scribe" => "operator_documentation",
        _ => "general",
    };

    /// <param name="options">
    /// The keyword list to match against. Null — every call site in the colony today — reads
    /// <see cref="SafetyPolicy.ToolOptions"/> live, so a keyword added at runtime is honoured on the
    /// next call. Falls back to the built-in list below if no composition root has run.
    /// </param>
    public static bool ShouldUseWebSearch(string goal, IToolRuntimeOptions? options = null)
    {
        var lowered = (goal ?? "").ToLowerInvariant();
        var keywords = (options ?? SafetyPolicy.ToolOptions)?.WebSearchKeywords ?? DefaultWebSearchKeywords;
        return keywords.Any(k => lowered.Contains(k));
    }

    // Mirrors AnthillRuntime.WebSearchKeywords as declared, so an unconfigured process behaves as a
    // configured one does at rest. The core overwrites this via SafetyPolicy.Configure at load.
    private static readonly IReadOnlySet<string> DefaultWebSearchKeywords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "latest", "current", "today", "news", "recent", "web", "internet", "search", "lookup", "look up",
            "online", "price", "version", "docs", "documentation", "advisory", "security advisory", "cve", "release",
        };

    [GeneratedRegex(@"\n{3,}")] private static partial Regex MultiNewline();
    [GeneratedRegex(@"[a-zA-Z0-9_]+")] private static partial Regex WordToken();
    [GeneratedRegex("<script.*?</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex ScriptTag();
    [GeneratedRegex("<style.*?</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex StyleTag();
    [GeneratedRegex("<[^>]+>")] private static partial Regex AnyTag();
}
