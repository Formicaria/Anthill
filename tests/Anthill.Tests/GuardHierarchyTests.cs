using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE RULES GUARDS ARE HELD TO, HELD TO BY A GUARD. v0.3.8.112, PLAN.md §2b `.112` — R0's last
/// sub-item, "the guard hierarchy written down".
///
/// WRITTEN DOWN IS NOT ENOUGH, and this project has the receipts. `docs/AUTONOMY.md` said the
/// autonomy roadmap was complete for sixty releases while `PLAN.md` treated it as gated and not
/// started; `README.md` described a release sixty-six versions old as current, beside a version
/// number that tests pinned correctly. A rule a document states and nothing checks describes its
/// author's intention rather than the tree — which is the sentence `EventVocabularyTests` opens
/// with, applied to the document that now states the rules.
///
/// So `docs/GUARDS.md` is the prose, and this is the part of it a test can carry.
/// </summary>
public class GuardHierarchyTests
{
    private static string GuardsDoc() =>
        Path.Combine(SourceText.RepoRoot(), "docs", "GUARDS.md");

    private static IEnumerable<string> TestFiles() =>
        Directory.GetFiles(Path.Combine(SourceText.RepoRoot(), "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>
    /// The hierarchy is documented, and the document still says the things the tests below enforce.
    /// A doc that quietly loses a rule leaves the enforcement looking arbitrary to whoever hits it.
    /// </summary>
    [Fact]
    public void TheGuardHierarchy_IsWrittenDown()
    {
        Assert.True(File.Exists(GuardsDoc()),
            "docs/GUARDS.md is gone. R0's last item was to write the guard hierarchy down, and the "
          + "checks in this file enforce two of its rules — without the document they refuse a "
          + "pattern for a reason nobody can look up.");

        var doc = File.ReadAllText(GuardsDoc());

        foreach (var required in new[]
        {
            "may never depend on a character count",
            "resolve a named constant",
            "vacuity floor",
        })
            Assert.Contains(required, doc, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// NO GUARD SLICES SOURCE BY A CHARACTER BUDGET.
    ///
    /// THE TWO FAILURES THIS ENDS, both already paid for. v0.3.8.91 shipped a guard reading a
    /// 4,000-character window whose marker sat 27 characters inside it on Linux and outside it on a
    /// CRLF checkout: every local run green, `main` red, on a property that had not changed. And
    /// v0.3.8.97 hit the mirror image — adding an explanatory paragraph INSIDE a guarded member
    /// pushed the marker past the budget, so a guard whose subject was unchanged and still true
    /// reported the strictness gone.
    ///
    /// The reflex a false failure invites is to relax the rule being guarded. That is the real cost,
    /// and it is why this is enforced rather than remembered.
    ///
    /// READS `CodeOnly`, so the paragraphs in `SourceText` and in this file that QUOTE the offending
    /// shape while explaining it are not themselves instances of it — the trap this repository has
    /// re-found often enough to have extracted a helper for.
    /// </summary>
    [Fact]
    public void NoGuard_SlicesSourceByACharacterBudget()
    {
        // `code[start..start + 4000]`, `source[i..(i + 2000)]`, `text.Substring(at, 4000)`.
        var budgeted = new Regex(
            @"\.\.\s*\(?\s*[A-Za-z_]\w*\s*\+\s*(?<n>\d{3,}|\d[\d_]*\d)\s*\)?\s*\]"
          + @"|Substring\(\s*[A-Za-z_]\w*\s*,\s*(?<m>\d{3,})\s*\)",
            RegexOptions.Compiled);

        var offenders = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in TestFiles())
        {
            // This file carries the shape as a STRING LITERAL, in the fixture that proves the
            // detector still detects. `CodeOnly` blanks comments and keeps literals, correctly, so
            // the one file whose subject is this pattern is named rather than reworded — rewording
            // it to dodge the guard is the trap `SourceText`'s own remarks were written about.
            if (Path.GetFileName(file) == "GuardHierarchyTests.cs") continue;

            var code = SourceText.CodeOnly(File.ReadAllText(file));
            var match = budgeted.Match(code);
            if (match.Success) offenders.TryAdd(Path.GetFileName(file), match.Value.Trim());
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} guard(s) slice source by a character budget:\n  "
          + string.Join("\n  ", offenders.Select(kv => $"{kv.Key}  {kv.Value}"))
          + "\nA budget is a proxy for \"inside this thing\" and a bad one: the guess is invisible "
          + "when it is wrong, it means something different on a CRLF checkout, and it drifts every "
          + "time the code grows. Read the delimiters — SourceText.MemberBody bounds a member and "
          + "SourceText.CallSites bounds a call. See docs/GUARDS.md.");
    }

    /// <summary>
    /// AND THE SWEEP SEES THE SUITE. The assertion above passes over an empty file list exactly as
    /// it passes over a clean one — the vacuity failure `docs/GUARDS.md` names, applied to the test
    /// that enforces `docs/GUARDS.md`.
    /// </summary>
    [Fact]
    public void TheHierarchySweep_SeesTheTestSuite()
    {
        var files = TestFiles().ToList();

        Assert.True(files.Count >= 100,
            $"only {files.Count} test files were swept. This suite is far larger than that, so the "
          + "sweep has stopped seeing it and every assertion in this file is now vacuous.");

        // And the detector still detects. A regex that stopped matching would be indistinguishable
        // from a clean suite, which is the whole failure mode being guarded against.
        Assert.Matches(
            new Regex(@"\.\.\s*\(?\s*[A-Za-z_]\w*\s*\+\s*\d{3,}\s*\)?\s*\]"),
            "var window = code[start..(start + 4000)];");
    }
}
