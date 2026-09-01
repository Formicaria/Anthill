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

    /// <summary>
    /// Run a git command at the repo root. Null when git is unavailable or the command failed.
    ///
    /// <b>The output is decoded as UTF-8, explicitly.</b> v0.3.8.41 — without this the guard was
    /// broken on Windows and green on Linux, which is the worst arrangement available: CI never saw
    /// the problem, while the machine releases are actually cut from reported 37 of 41 shipped
    /// entries as having been edited after their tag.
    ///
    /// <c>RedirectStandardOutput</c> defaults to <c>Console.OutputEncoding</c>, which on an English
    /// Windows console is code page 437 or 1252 rather than UTF-8. <c>git show</c> emits the blob's
    /// bytes, and this changelog is full of em-dashes and curly quotes — so every entry containing
    /// one decoded to mojibake and compared unequal to the same entry read from disk by
    /// <c>File.ReadAllText</c>, which does detect UTF-8. The four entries that passed were the ones
    /// that happen to be pure ASCII.
    ///
    /// Worth stating because of what the failure LOOKED like: a guard confidently naming thirty-seven
    /// releases as tampered with. A check that is wrong on every run is one people learn to ignore,
    /// which costs nothing until the day it is right — the same argument v3.8.2 made about the
    /// model-fitness alarm.
    /// </summary>
    private static string? Git(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = Root(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
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
        ["0.3.8.45"] = "the entry as tagged had accidentally swallowed the '## v0.3.8.44' heading, so "
                     + "it appeared to contain the .44 release notes as its own. Restoring the heading "
                     + "(PR #232) shrank the .45 entry back to only what .45 shipped — the entry's own "
                     + "words are untouched",
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
    /// EVERY release commit on the active line has a changelog entry. v0.3.8.41.
    ///
    /// THE GAP THIS CLOSES. `VersionMarkers_ChangelogHasEntryForRuntimeVersion` checks that the
    /// version the runtime reports has an entry — the CURRENT one, and only that one. A release that
    /// ships and is never written down passes it, passes the ordering guard, passes the frozen-entry
    /// guard, and leaves every version marker in agreement.
    ///
    /// That is not hypothetical. v0.3.8.39 shipped as commit `aecc926` (#223) with no changelog
    /// entry and no tag; `.38` and `.40` sat next to each other and nothing said a release was
    /// missing. It was found by hand, during an audit that was looking for something else. Two
    /// commits also both claimed `v0.3.8.40`, with one tag between them.
    ///
    /// The subject is the RELEASE COMMIT SUBJECT, because that is the one place a shipped version
    /// records itself independently of the documents describing it. Skips cleanly without git, like
    /// its neighbours above, and for the same reason: the mistake is made while releasing locally,
    /// which is exactly where git is.
    /// </summary>
    [Fact]
    public void EveryReleaseCommitOnTheActiveLine_HasAChangelogEntry()
    {
        var log = Git("log --format=%s -n 400");
        if (log is null) return;   // git unavailable — see the class comment

        // "v0.3.8.39: the console says what it means ..." — the shape every release commit here uses.
        var released = Regex.Matches(log, @"^v(0\.\d+(?:\.\d+)+)\s*[:\-]", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (released.Count == 0) return;   // shallow clone, or a squash history without subjects

        var entries = Entries(File.ReadAllText(Path.Combine(Root(), "CHANGELOG.md")));

        var undocumented = released
            .Where(v => !entries.ContainsKey(v))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        Assert.True(undocumented.Count == 0,
            "These versions have a release commit on this branch and NO changelog entry, so the "
            + "repository contains releases whose contents are unrecorded. That is how v0.3.8.39 "
            + "shipped unwritten: the current-version guard only ever checks the current version, so "
            + "a skipped entry in between passes everything. Write the entry (reconstructing it from "
            + "the commit is fine, and should say so): " + string.Join(", ", undocumented));
    }

    /// <summary>
    /// One release commit per version. v0.3.8.41.
    ///
    /// Two commits claimed `v0.3.8.40` (#224 and #225) and one tag points at one of them, so the
    /// other shipped untagged and untagged is unfindable. A version is a name for a specific tree;
    /// naming two trees with it means the name identifies neither.
    /// </summary>
    /// <summary>
    /// Duplicates already in the history, named with their reason.
    ///
    /// History is not editable and rewriting it to make a guard green would be the wrong direction —
    /// the same reasoning <see cref="CorrectedAfterShipping"/> carries. What the guard must catch is
    /// the NEXT one.
    /// </summary>
    private static readonly Dictionary<string, string> DuplicateReleaseCommits =
        new(StringComparer.Ordinal)
        {
            ["0.3.8.40"] = "#224 and #225 both shipped under this version; the tag points at one of "
                         + "them. Found during the v0.3.8.41 audit, recorded rather than rewritten.",

            // NOT the same defect as .40, despite arriving at the same guard. Nothing shipped twice
            // under this name: `3ec0366` (#16) is the real v0.3.8.60, and `9198dd5` (#23) is
            // v0.3.8.67 wearing v0.3.8.60's subject because `RELEASE_MSG.txt` was never regenerated
            // and the release procedure pipes it into `git commit -F`. The tag `v0.3.8.67` points at
            // `9198dd5` and the tree in it is v0.3.8.67's; only the sentence is wrong.
            //
            // Recorded here because history is not editable, but note what this means: THIS GUARD
            // CAUGHT IT BY COINCIDENCE. It fires on a repeated version, and a stale notes file only
            // repeats a version if it happens to hold a previous release's text — which it did.
            // A notes file naming nothing, or naming a version never released, would have shipped
            // silently. See TheSubjectOfATaggedRelease_NamesThatRelease for the check that is
            // actually about this, and ReleaseNotesTests for the one that prevents it.
            ["0.3.8.60"] = "#16 is this release. #23 is v0.3.8.67 committed under this subject from "
                         + "a stale RELEASE_MSG.txt; its tag and tree are correct.",
            // v0.3.8.104. `25b0ec1` is the real v0.3.8.103 and carries the tag. `25f26ad` is a
            // second commit under the same subject, merged during a botched .104 release sequence:
            // a `git am` failed partway, `git am --abort` rolled the branch back and discarded the
            // .104 commits while leaving a ZERO-BYTE MissionPreflight.cs on disk, and that empty
            // file was committed and merged under the notes file's stale .103 title. The tree it
            // added is one empty file, superseded by .104's real one.
            //
            // Recorded rather than rewritten, on this file's own standing policy and the same one
            // DocsConsistencyTests applies to the duplicate v1.x/v2.x changelog headings: shared
            // history is not force-pushed to make a guard green. The guard was right, it caught a
            // real defect within minutes of it landing, and what it caught is now written down
            // where the next reader will find it.
            ["0.3.8.103"] = "25b0ec1 is the release and holds the tag. 25f26ad is an empty-file "
                          + "commit merged under a stale RELEASE_MSG.txt subject during the .104 "
                          + "sequence; superseded by .104.",
        };

    /// <summary>
    /// Tagged releases whose commit subject names the wrong version, with the reason. Same policy as
    /// the dictionary above: the history stands, the mistake is written down.
    /// </summary>
    private static readonly Dictionary<string, string> MisnamedReleaseCommits =
        new(StringComparer.Ordinal)
        {
            ["0.3.8.67"] = "shipped under v0.3.8.60's subject line — RELEASE_MSG.txt was not "
                         + "regenerated and the release script commits with `-F` that file.",
        };

    [Fact]
    public void NoTwoReleaseCommits_ClaimTheSameVersion()
    {
        var log = Git("log --format=%s -n 400");
        if (log is null) return;

        var duplicates = Regex.Matches(log, @"^v(0\.\d+(?:\.\d+)+)\s*[:\-]", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .GroupBy(v => v, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Where(g => !DuplicateReleaseCommits.ContainsKey(g.Key))
            .Select(g => $"{g.Key} ({g.Count()} commits)")
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        Assert.True(duplicates.Count == 0,
            "These versions are claimed by more than one release commit, so the tag can only point "
            + "at one of them and the rest shipped unfindable: " + string.Join(", ", duplicates));
    }

    /// <summary>
    /// A tagged release's commit subject names THAT release. v0.3.8.68.
    ///
    /// WHY THIS EXISTS AND THE DUPLICATE GUARD ABOVE WAS NOT ENOUGH. v0.3.8.67 shipped with the
    /// commit subject "v0.3.8.60 - the second copy of every rule v0.3.8.59 fixed", because
    /// `RELEASE_MSG.txt` had not been regenerated and the release procedure pipes it into
    /// `git commit -F`. The tag, the tree and all four version markers were right; only every
    /// human-readable account of the release was wrong, which is the kind of error a green build
    /// cannot see.
    ///
    /// <see cref="NoTwoReleaseCommits_ClaimTheSameVersion"/> did fire — but by accident. It notices a
    /// version claimed TWICE, and a stale notes file only produces a duplicate when it happens to
    /// hold a previous release's text. Had the file held a draft, a placeholder, or a version number
    /// that never shipped, nothing would have failed. The property that was actually violated is the
    /// one asserted here, and it holds regardless of what the stale text says.
    ///
    /// THE SUBJECT SHAPE IS OPTIONAL, DELIBERATELY. Several releases on this line are tagged on a
    /// commit whose subject describes the work rather than the version — v0.3.8.61, .65 and .66 all
    /// do. That is a legitimate style and this guard does not argue with it: it only checks subjects
    /// that DO use the `v<version>:` form, and requires the version in them to be the tag's own.
    /// A check that also demanded the form would fail three honest releases, and a guard that is
    /// wrong on releases that were fine is one people learn to override.
    /// </summary>
    [Fact]
    public void TheSubjectOfATaggedRelease_NamesThatRelease()
    {
        var tags = Git("tag --list v0.*");
        if (tags is null) return;   // git unavailable, or an export with no tags

        var wrong = new List<string>();

        foreach (var tag in tags.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                .Select(t => t.Trim()).Where(t => t.Length > 1))
        {
            var version = tag[1..];   // strip the leading 'v'
            if (MisnamedReleaseCommits.ContainsKey(version)) continue;

            var subject = Git($"log -1 --format=%s {tag}")?.Trim();
            if (string.IsNullOrEmpty(subject)) continue;   // tag unreachable in a shallow clone

            var named = Regex.Match(subject, @"^v(0\.\d+(?:\.\d+)+)\s*[:\-—]");
            if (!named.Success) continue;   // describes the work, not the version — allowed

            if (!string.Equals(named.Groups[1].Value, version, StringComparison.Ordinal))
                wrong.Add($"{tag} is committed as \"{subject}\"");
        }

        Assert.True(wrong.Count == 0,
            "These tags point at a commit whose subject names a DIFFERENT release, so the tree is "
            + "right and every account of it is wrong — the state v0.3.8.67 shipped in: "
            + string.Join("; ", wrong)
            + ". The cause is a stale RELEASE_MSG.txt reaching `git commit -F`; derive it from the "
            + "changelog's top entry and let ReleaseNotesTests check it before the commit exists.");
    }

    /// <summary>
    /// The misnaming list may not name a version with no entry — same rule the correction list below
    /// carries. An excuse for a release that does not exist is an excuse nobody can check.
    /// </summary>
    [Fact]
    public void TheMisnamingList_NamesOnlyReleasesThatExist()
    {
        var entries = Entries(File.ReadAllText(Path.Combine(Root(), "CHANGELOG.md")));

        var unknown = MisnamedReleaseCommits.Keys
            .Concat(DuplicateReleaseCommits.Keys)
            .Where(v => !entries.ContainsKey(v))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        Assert.True(unknown.Count == 0,
            "These versions are excused as misnamed or duplicated, but the changelog has no entry "
            + "for them: " + string.Join(", ", unknown));
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
