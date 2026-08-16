using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The release notes describe THIS release. v0.3.8.67.
///
/// WHAT HAPPENED. `RELEASE_MSG.txt` is a hand-maintained file that the release procedure pipes into
/// `git commit -F` and `gh release create --notes-file`. Preparing v0.3.8.67 updated the changelog,
/// all four version markers and the plan — and not that file. It still held the v0.3.8.60 text, so
/// the squash commit that shipped v0.3.8.67 carries a message describing a release that had already
/// gone out three weeks of work earlier, and the PR was titled the same.
///
/// Nothing about the SHIPPED ARTIFACT was wrong: the tag points at the right commit and the code in
/// it is v0.3.8.67's. What was wrong is every human-readable account of it — which is the kind of
/// error that survives, because the build is green and the tests pass and only a person reading the
/// history ever notices.
///
/// WHY A GUARD RATHER THAN MORE CARE. Every other version marker in this repository is checked
/// against `AnthillRuntime.Version` — `Directory.Build.props`, the README, the changelog entry — and
/// the release notes were the one that fed the commit message and had no check at all. The file is
/// consumed BLIND by a script; a stale one cannot announce itself. So it is checked here, beside the
/// markers it belongs with.
///
/// The file is also now DERIVED from the changelog's top entry rather than written twice. Two copies
/// of the release's story is one copy that eventually disagrees — the same rule this repository
/// applies to containment checks and test collections, applied to prose.
/// </summary>
public class ReleaseNotesTests
{
    private static string Path_ => System.IO.Path.Combine(SourceText.RepoRoot(), "RELEASE_MSG.txt");

    /// <summary>
    /// If the notes file exists, its first line names the version being shipped.
    ///
    /// Absent is fine and deliberately so: the file is a release-time artifact, not a tracked part of
    /// the source, and requiring it would fail every ordinary build. What must never happen is a
    /// PRESENT file describing a different release, because that is the state that gets committed.
    /// </summary>
    [Fact]
    public void TheReleaseNotes_NameTheVersionBeingShipped()
    {
        if (!File.Exists(Path_)) return;

        var first = File.ReadLines(Path_).FirstOrDefault() ?? "";

        Assert.True(first.Contains(AnthillRuntime.Version, StringComparison.Ordinal),
            $"RELEASE_MSG.txt opens with \"{first}\" but the runtime version is "
          + $"{AnthillRuntime.Version}. That file becomes the commit message and the GitHub release "
          + "notes, so a stale one ships a correct release under a previous release's name — which "
          + "is exactly what happened to v0.3.8.67. Regenerate it from the changelog's top entry.");
    }

    /// <summary>
    /// And it agrees with the changelog, which is the source it should be derived from. A notes file
    /// that names the right version while telling a different story is the same defect one step in.
    /// </summary>
    [Fact]
    public void TheReleaseNotes_MatchTheChangelogsTopEntry()
    {
        if (!File.Exists(Path_)) return;

        var top = File.ReadLines(System.IO.Path.Combine(SourceText.RepoRoot(), "CHANGELOG.md"))
            .FirstOrDefault(l => l.StartsWith("## ", StringComparison.Ordinal))?["## ".Length..].Trim();
        Assert.NotNull(top);

        var first = (File.ReadLines(Path_).FirstOrDefault() ?? "").Trim();

        Assert.Equal(top, first);
    }
}
