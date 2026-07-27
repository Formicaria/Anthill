namespace Anthill.Core.Outcomes;

/// <summary>
/// v2.19.0. The verifier's verdict, as a value both the ant and the mission gate agree on.
///
/// Before this existed, VerifierAnt returned prose through the default text wrapper, so every
/// verdict — including "Verification Failed" — was recorded with StatusCode "succeeded" and a
/// Complete task. <see cref="MissionVerification"/> then asked only whether a verification task
/// had completed, so a mission whose own verifier said it failed still graded completed_verified,
/// reinforced learning positively, and satisfied the auto-apply precondition.
///
/// The canonical phrases below are the contract: VerifierAnt writes them, this parses them, and
/// the gate reads the parsed value. Parsing is deliberately FAIL CLOSED — anything unrecognised,
/// absent, or ambiguous is <see cref="Unknown"/>, which is not a pass. A gate that decides whether
/// generated code may auto-apply must treat "I could not tell" as "no".
/// </summary>
public static class VerificationVerdict
{
    public const string Passed = "passed";
    public const string NeedsImprovement = "needs_improvement";
    public const string Failed = "failed";
    public const string Unknown = "unknown";

    /// <summary>The exact phrases VerifierAnt emits, mapped to their verdict.</summary>
    private static readonly (string Phrase, string Verdict)[] Phrases =
    {
        ("verification passed", Passed),
        ("needs improvement", NeedsImprovement),
        ("verification failed", Failed),
    };

    /// <summary>
    /// Classify verifier output. Returns <see cref="Unknown"/> when the text contains no verdict
    /// OR more than one distinct verdict.
    ///
    /// The ambiguity rule is not theoretical: the verifier prompt lists all three options on one
    /// line ("Verdict: Verification Passed / Needs Improvement / Verification Failed"). A model
    /// that echoes that line would otherwise be read as whichever verdict the parser happened to
    /// check first — a coin flip deciding whether work may auto-apply. Ambiguous means unknown.
    /// </summary>
    public static string Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Unknown;

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (phrase, verdict) in Phrases)
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                found.Add(verdict);

        return found.Count == 1 ? found.First() : Unknown;
    }

    /// <summary>Only an unambiguous pass is a pass. Everything else — including Unknown.</summary>
    public static bool IsPass(string? verdict) =>
        string.Equals(verdict, Passed, StringComparison.Ordinal);

    /// <summary>Convenience for callers holding raw verifier text rather than a parsed verdict.</summary>
    public static bool TextIsPass(string? text) => IsPass(Parse(text));

    /// <summary>Operator-readable explanation of why a verdict did or did not satisfy the gate.</summary>
    public static string Explain(string? verdict) => verdict switch
    {
        Passed => "verifier returned Verification Passed",
        NeedsImprovement => "verifier returned Needs Improvement — the mission is not verified",
        Failed => "verifier returned Verification Failed",
        _ => "verifier output contained no single recognisable verdict — treated as unverified",
    };
}
