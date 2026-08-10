using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// A changelog entry that has SHIPPED is frozen. v0.3.8.37.
///
/// THE MISTAKE THIS EXISTS FOR, MADE THREE TIMES
/// ---------------------------------------------
/// While a release is in flight, new work gets written into the changelog's top entry. Then that
/// entry ships — and now the tagged release describes code it does not contain. It happened at
/// v3.8.33, at v0.3.8.34 and again at v0.3.8.35, each time caught by hand afterwards, twice only
/// because someone went looking.
///
/// It is the same defect this repository keeps finding everywhere else, turned on the record itself:
/// a document that confidently describes something that is not true. `CHANGELOG.md` is the only
/// place that answers "what is in this release", and an entry edited after tagging answers it
/// wrongly and looks deliberate doing so.
///
/// Process notes did not fix it. Three releases of "be careful" produced three failures, so this is
/// the executable version.
///
/// WHY THIS ONE NEEDS GIT
/// ----------------------
/// Every other guard here reads the working tree, because the property is visible in the tree. This
/// property is a DIFFERENCE — entry text now versus entry text at the tag — and nothing in the
/// working tree records it. So the check shells out to git and SKIPS cleanly when git or the tags
/// are unavailable (a shallow CI clone, an exported archive).
///
/// A skip is honest here rather than a hole: the mistake is made while editing locally, which is
/// exactly where the tags exist and the check runs.
/// </summary>
public class ShippedChangelogTests
{
    private static string Root() => SourceText.RepoRoot();

    /// <summary>Run a git command at the repo root. Null when git is unavailable or the command failed.</summary>
    private static string? Git(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = Root(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;

            var stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(15_000)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? stdout : null;
        }
        catch { return null; }   // git missing, sandboxed, or not a repository
    }

    /// <summary>
    /// Entries corrected after shipping, for a reason, with the reason.
    ///
    /// The first run of this guard reported twelve. Nine were archive-link maintenance — when a
    /// document moves to `docs/archive/v3/`, every reference has to follow or
    /// `EveryDocumentationLink_PointsAtAFileThatExists` fails. Those are normalised away below
    /// rather than allow-listed, because they are not content changes.
    ///
    /// These three are real prose edits, and all three are the RIGHT kind: a claim that became false
    /// being corrected. Freezing a false statement is not integrity, it is just immutability, and
    /// this suite has always distinguished the two.
    /// </summary>
    private static readonly Dictionary<string, string> CorrectedAfterShipping = new(StringComparer.Ordinal)
    {
        ["3.8.18"] = "the no-UI gate claim was WITHDRAWN — the original entry said a gate shipped, the "
                   + "gate did not hold, and the criterion went back to NOT PROVEN. Correcting a false "
                   + "claim is the one edit a shipped entry should get",
        ["3.8.0"] = "restructured to 'Preceded by v3.7.2' when the release-note convention changed",
        ["3.0.0"] = "the forward-looking range said 'v3.0.0 through v3.9.0'; the line actually closed "
                  + "at v3.8.3, and leaving the wrong range would misdescribe the record",
    };

    /// <summary>
    /// Compare CONTENT, not formatting.
    ///
    /// Archive moves rewrite `docs/X.md` to `docs/archive/v3/X.md` across every entry that mentions
    /// the document — link maintenance forced by another guard, not a change to what the release did.
    /// Re-wrapping a paragraph is likewise not a content change. Both are normalised so this guard
    /// fires only on words.
    /// </summary>
    private static string Content(string entry) =>
        Regex.Replace(Regex.Replace(entry, @"docs/archive/v\d+/", "docs/"), @"\s+", " ").Trim();

    /// <summary>The `## vX` entries in a changelog, keyed by version, text and all.</summary>
    private static Dictionary<string, string> Entries(string changelog)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var matches = Regex.Matches(changelog, @"^##\s+v(?<v>\d+(?:\.\d+)+).*$", RegexOptions.Multiline);

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : changelog.Length;
            // Last write wins is fine: duplicate headings are a separate guard's problem.
            result[matches[i].Groups["v"].Value] = changelog[start..end];
        }
        return result;
    }

    /// <summary>
    /// THE GUARD. For every version that has a tag, the entry in the working tree must match the
    /// entry as it was at that tag, byte for byte.
    ///
    /// Scoped to the maintained era to keep the git calls bounded and because v1/v2 headings are
    /// frozen history the suite already declines to police (see `DocsConsistencyTests`).
    /// </summary>
    [Fact]
    public void AShippedChangelogEntry_IsNeverEditedAfterItsTag()
    {
        var head = Git("rev-parse --git-dir");
        if (head is null) return;   // not a git checkout — nothing to compare against

        var current = File.ReadAllText(Path.Combine(Root(), "CHANGELOG.md"));
        var entries = Entries(current);

        var drifted = new List<string>();
        var compared = 0;

        foreach (var (version, text) in entries)
        {
            // Only the era this suite maintains: the v3 lineage and its v0.3.x renumbering.
            var major = int.Parse(version.Split('.')[0]);
            if (major is not (0 or 3)) continue;
            if (CorrectedAfterShipping.ContainsKey(version)) continue;

            var tagged = Git($"show v{version}:CHANGELOG.md");
            if (tagged is null) continue;   // unreleased, or the tag was never fetched

            compared++;
            if (!Entries(tagged).TryGetValue(version, out var shipped)) continue;

            if (!string.Equals(Content(shipped), Content(text), StringComparison.Ordinal))
                drifted.Add($"v{version}");
        }

        Assert.True(drifted.Count == 0,
            "These changelog entries have been edited since they were tagged, so the released "
            + "version now describes code it does not contain. That has happened three times in this "
            + "repository, always by writing new work into the top entry while a release was in "
            + "flight. Move the new text into the entry for the release being prepared: "
            + string.Join(", ", drifted)
            + $" (compared {compared} tagged entries)");
    }

    /// <summary>
    /// The guard must actually be comparing something. A silent skip on every entry would make the
    /// test above permanently, uselessly green — which is the failure mode of every check in this
    /// suite that was scoped too narrowly.
    /// </summary>
    [Fact]
    public void TheShippedEntryComparison_ActuallyRunsWhenGitIsAvailable()
    {
        if (Git("rev-parse --git-dir") is null) return;   // legitimately unavailable

        // If git works, at least one release tag must be readable, or the guard above is inert and
        // nobody would know.
        var anyTag = Git("tag --list v0.3.* v3.8.*");
        if (string.IsNullOrWhiteSpace(anyTag)) return;    // tags not fetched (shallow clone)

        var firstTag = anyTag.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        Assert.NotNull(Git($"show {firstTag}:CHANGELOG.md"));
    }

    /// <summary>
    /// The correction list may not name a version with no entry. A stale name excuses nothing and
    /// reads as a decision about a release that does not exist — the same rule the status-field and
    /// route ledgers carry.
    /// </summary>
    [Fact]
    public void TheCorrectionList_NamesOnlyReleasesThatExist()
    {
        var entries = Entries(File.ReadAllText(Path.Combine(Root(), "CHANGELOG.md")));

        var unknown = CorrectedAfterShipping.Keys
            .Where(v => !entries.ContainsKey(v))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        Assert.True(unknown.Count == 0,
            "These versions are listed as corrected after shipping, but the changelog has no entry "
            + "for them: " + string.Join(", ", unknown));
    }
}
