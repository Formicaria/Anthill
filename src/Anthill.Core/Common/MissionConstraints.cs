namespace Anthill.Core.Common;

/// <summary>
/// Explicit constraints parsed out of a mission goal / objective charter before planning
/// (v1.8.16). The Planner reads these so a "verification-only" or "do not modify files" mission
/// never has coder patch-proposal tasks planned for it, and the objective lifecycle reads them so a
/// verification-only objective can end cleanly instead of looping. Pure, side-effect free, and
/// deliberately conservative: a mission is only treated as no-patch when the operator's intent is
/// explicit, so ordinary code-change missions keep the full coder/builder/verifier workflow.
/// </summary>
public sealed record MissionConstraints(bool NoPatches, bool VerificationOnly, bool ReadOnly, bool OneShot)
{
    /// <summary>True when the plan must not include any file-modifying (coder patch-proposal) tasks.</summary>
    public bool BlocksPatches => NoPatches || VerificationOnly || ReadOnly;

    public static readonly MissionConstraints None = new(false, false, false, false);

    // Phrases that forbid file changes outright. Matched on a lowercased, whitespace-normalized copy.
    private static readonly string[] NoPatchPhrases =
    {
        "do not create patches", "don't create patches", "do not propose patches", "no patches",
        "do not modify files", "don't modify files", "do not modify any files", "do not change files",
        "don't change files", "do not edit files", "don't edit files", "do not write files",
        "don't write files", "do not write to disk", "without modifying", "without changing any files",
        "no file changes", "no code changes", "make no changes",
    };

    private static readonly string[] VerificationOnlyPhrases =
    {
        "verification only", "verification-only", "verify only", "verify-only", "only verify",
        "just verify", "verification pass", "audit only", "review only", "review-only",
    };

    private static readonly string[] ReadOnlyPhrases =
    {
        "read only", "read-only", "inspect only", "inspection only", "inspect-only",
        "look only", "only inspect", "only look", "do not take action", "without making changes",
    };

    private static readonly string[] OneShotPhrases =
    {
        "one-shot", "one shot", "run once", "do this once", "single run", "exactly once",
        "one time", "one-time",
    };

    /// <summary>Parses the goal/charter text into a constraint set. Empty/null → <see cref="None"/>.</summary>
    public static MissionConstraints Parse(string? goal)
    {
        if (string.IsNullOrWhiteSpace(goal)) return None;
        // Normalize whitespace so "do not   modify\nfiles" still matches "do not modify files".
        var text = System.Text.RegularExpressions.Regex.Replace(goal.ToLowerInvariant(), @"\s+", " ");
        bool Has(string[] phrases) => phrases.Any(text.Contains);
        var noPatches = Has(NoPatchPhrases);
        var verification = Has(VerificationOnlyPhrases);
        var readOnly = Has(ReadOnlyPhrases);
        var oneShot = Has(OneShotPhrases);
        return new MissionConstraints(noPatches, verification, readOnly, oneShot);
    }
}
