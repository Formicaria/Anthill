namespace Anthill.Core.Common;

/// <summary>
/// Normalizes the coder's free-text <c>risk</c> string on a patch proposal into one of four
/// canonical levels — <c>low</c>, <c>medium</c>, <c>high</c>, <c>unknown</c> — so the Patch Center
/// can render consistent risk badges and filter by level (v1.8.16). The coder is prompted to state
/// a risk, but it writes prose ("low risk, adds a comment"; "HIGH — touches auth"), so this maps
/// that prose onto a level rather than trusting an exact token.
/// </summary>
public static class RiskLevel
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Unknown = "unknown";

    public static string Normalize(string? risk)
    {
        if (string.IsNullOrWhiteSpace(risk)) return Unknown;
        var t = risk.ToLowerInvariant();
        // High wins over medium/low if multiple words appear — surface the worst case.
        if (t.Contains("high") || t.Contains("critical") || t.Contains("danger") || t.Contains("severe")) return High;
        if (t.Contains("medium") || t.Contains("moderate") || t.Contains("mid")) return Medium;
        if (t.Contains("low") || t.Contains("minor") || t.Contains("trivial") || t.Contains("safe")) return Low;
        return Unknown;
    }
}
