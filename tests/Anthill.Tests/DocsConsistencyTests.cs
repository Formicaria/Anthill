using System.Text.RegularExpressions;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v2.15.0: enforces the documentation guarantees NORTH_STAR already claims to enforce.
///
/// NORTH_STAR §9 states that automated tests "must verify ... required canonical documents exist".
/// No such test was ever written, and the list drifted far enough that FIVE of the nine documents
/// it named — TOOLS.md, VERIFICATION.md, SKILLS.md, RECOVERY.md, QUALIFICATION.md — did not exist
/// at all. Anyone following the roadmap was being sent to files that were never created.
///
/// Same failure shape the console track hit twice: a documented guarantee with nothing checking it
/// (v2.14.12, functions whose call sites shipped without definitions; v2.14.14, a validator with 20
/// passing tests and no call site). The fix is always the same — make the claim executable.
/// </summary>
public class DocsConsistencyTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Read(string rel) =>
        File.ReadAllText(Path.Combine(Root(), rel.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Every `docs/*.md` path listed in NORTH_STAR's canonical block must be a real file.</summary>
    [Fact]
    public void CanonicalDocuments_AllExist()
    {
        var northStar = Read("docs/NORTH_STAR.md");

        var block = Regex.Match(northStar, @"##\s*Canonical documents\s*```text(.*?)```", RegexOptions.Singleline);
        Assert.True(block.Success, "NORTH_STAR.md no longer has a '## Canonical documents' code block.");

        var listed = Regex.Matches(block.Groups[1].Value, @"^\s*(docs/[A-Za-z0-9_]+\.md)", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Distinct().ToList();

        Assert.True(listed.Count >= 5,
            $"Expected the canonical list to name several documents, found {listed.Count}.");

        var missing = listed
            .Where(rel => !File.Exists(Path.Combine(Root(), rel.Replace('/', Path.DirectorySeparatorChar))))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            "NORTH_STAR.md lists canonical documents that do not exist: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The roadmap documents must mention the version that is actually shipping.
    ///
    /// Deliberately an "is it mentioned at all" check rather than "is the newest mention current":
    /// these documents legitimately reference FUTURE releases in their next-steps sections, so a
    /// newest-version rule would fail on every forward-looking line. Mentioning the current version
    /// is the weakest condition that still catches the real drift — NORTH_STAR and ROADMAP sat at
    /// v2.14.13 while v2.14.15 shipped, which is what prompted this test.
    /// </summary>
    [Fact]
    public void RoadmapDocuments_MentionTheShippingVersion()
    {
        var current = "v" + AnthillRuntime.Version;
        var stale = new List<string>();

        foreach (var rel in new[] { "docs/NORTH_STAR.md", "docs/ROADMAP.md", "docs/DASHBOARD_WORKSPACE.md" })
            if (!Read(rel).Contains(current, StringComparison.Ordinal)) stale.Add(rel);

        Assert.True(stale.Count == 0,
            $"These documents never mention the shipping version {current}, so they have fallen behind "
            + "the release they are supposed to describe: " + string.Join(", ", stale));
    }
}
