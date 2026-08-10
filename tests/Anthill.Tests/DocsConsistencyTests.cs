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

    /// <summary>
    /// EVERY `docs/...md` link anywhere in the repository's markdown must point at a real file.
    /// v3.8.24.
    ///
    /// This test replaces one that checked a single code block in a single document, and the
    /// difference is the whole lesson. The old version parsed NORTH_STAR's "Canonical documents"
    /// block and verified those paths existed — written in v2.15.0 because five of the nine
    /// documents that block named did not exist at all.
    ///
    /// It worked, for that block. Meanwhile FIVE MORE dead links accumulated outside its scope:
    /// README, CHANGELOG and DASHBOARD_WORKSPACE all pointed at `docs/ADAPTIVE_RUNTIME_STATUS.md`,
    /// `docs/CONSOLE_REDESIGN.md`, `docs/CONSOLE_REFIT.md`, `docs/PRE_V3_RUNTIME_HARDENING.md` and
    /// `docs/UI_ROADMAP.md`, every one of which had been MOVED to `docs/archive/v2/` with the
    /// references left behind. Not lost documents — moved documents with stale pointers, which is
    /// worse, because the reader is sent somewhere that looks deliberate.
    ///
    /// A guard scoped to one list checks one list. This one checks every link, which is the only
    /// version of it that cannot be outgrown by the thing it guards.
    /// </summary>
    [Fact]
    public void EveryDocumentationLink_PointsAtAFileThatExists()
    {
        var root = Root();
        var markdown = new List<string> { "README.md", "CHANGELOG.md", "CONTRIBUTING.md" };
        markdown.AddRange(Directory.GetFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            // LIVE documents only. An archived file is a SNAPSHOT: its links describe the world as it
            // was on the day it was frozen, and `docs/archive/v2/ROADMAP.md` pointing at
            // `docs/NORTH_STAR.md` is an accurate record of a document that existed then. Rewriting
            // those to keep a guard green would edit the historical record to satisfy a test, which
            // is the same trade this suite refuses over duplicate changelog headings. Twenty-eight
            // such links exist and every one of them is correct about its own moment.
            .Where(f => !f.StartsWith("docs/archive/", StringComparison.OrdinalIgnoreCase)));

        var broken = new List<string>();
        foreach (var file in markdown.Where(f => File.Exists(Path.Combine(root, f.Replace('/', Path.DirectorySeparatorChar)))))
        {
            var text = File.ReadAllText(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)));
            foreach (Match m in Regex.Matches(text, @"docs/[A-Za-z0-9_./-]+\.md"))
            {
                var target = m.Value;
                // `docs/x.md`, `docs/a.md` and friends appear inside worked EXAMPLES of the patch and
                // indexing formats. Single-letter and single-character stems are never real documents
                // in this repository, and excluding them here beats renaming every example.
                var stem = Path.GetFileNameWithoutExtension(target);
                if (stem.Length <= 1) continue;
                if (!File.Exists(Path.Combine(root, target.Replace('/', Path.DirectorySeparatorChar))))
                    broken.Add($"{file} -> {target}");
            }
        }

        Assert.True(broken.Count == 0,
            "These documentation links point at files that do not exist. If the document MOVED, "
          + "update the link; a stale pointer reads as deliberate and sends the reader nowhere:\n  "
          + string.Join("\n  ", broken.Distinct().OrderBy(x => x, StringComparer.Ordinal)));
    }

    /// <summary>
    /// The plan must name the version that is actually shipping.
    ///
    /// v3.8.24: ONE document, not three. This used to check NORTH_STAR, ROADMAP and
    /// DASHBOARD_WORKSPACE — the last of which had said since v3.2.0 that it "describes a workspace
    /// that no longer exists", so every release was obliged to edit a document about deleted code in
    /// order to stay green. Three documents to keep current is how they come to disagree.
    ///
    /// Still deliberately an "is it mentioned at all" check rather than "is the newest mention
    /// current": PLAN.md legitimately names FUTURE work, so a newest-version rule would fail on
    /// every forward-looking line. Mentioning the current version is the weakest condition that
    /// still catches real drift — NORTH_STAR and ROADMAP once sat at v2.14.13 while v2.14.15
    /// shipped, which is what prompted this test.
    /// </summary>
    [Fact]
    public void ThePlan_MentionsTheShippingVersion()
    {
        var current = "v" + AnthillRuntime.Version;

        Assert.True(Read("docs/PLAN.md").Contains(current, StringComparison.Ordinal),
            $"docs/PLAN.md never mentions the shipping version {current}, so it has fallen behind "
          + "the release it is supposed to describe.");
    }

    /// <summary>
    /// Release headings are unique and ascend.
    ///
    /// v3.8.24: reads CHANGELOG.md rather than the archived ROADMAP. The invariant is unchanged and
    /// the subject is better — the changelog is now the only document with per-release headings, and
    /// it is the one an operator actually reads to answer "what is in this release".
    ///
    /// SCOPED TO THE LIVE MAJOR LINE, and that is a finding rather than a convenience. Repointing
    /// this guard at the changelog immediately surfaced FIFTEEN duplicate version headings and
    /// several out-of-order entries across v1.x and v2.x — drift the roadmap-scoped version could
    /// never have seen. Those lines are frozen history: v2 closed at v2.26.0 and rewriting 173
    /// headings to satisfy a guard would edit the record of what shipped in order to make a test
    /// green, which is the wrong direction. The invariant that matters is that the line being
    /// ACTIVELY WRITTEN cannot name a release twice, so that is what is asserted.
    ///
    /// Written because the roadmap had once drifted into naming the same release twice: two `## v3.5.0`
    /// sections, a `## v3.4.0` appearing after a v3.5.0 one, and no section for two phases that had
    /// actually shipped. Ordering is asserted as well as uniqueness, because renumbering an entry and
    /// leaving it in place produces a document that is unique, wrong, and reads plausibly.
    /// </summary>
    [Fact]
    public void ReleaseHeadings_AreUniqueAndDescend()
    {
        // v0.3.8.34 — versions are parsed as a VARIABLE-length list of components rather than as
        // exactly three, because the line was renumbered from `3.8.x` to `0.3.8.x`. The old form
        // derived a "current major" and kept only entries sharing it; under the new scheme that
        // major is 0, which excluded every historical heading and left the guard asserting over a
        // single entry.
        //
        // The ACTIVE LINE is what this guard has always really been about — see the note above about
        // v2's frozen headings. It is now expressed directly: entries sharing the current version's
        // leading components, differing only in the last. That is `0.3.8.*` today and was `3.8.*`
        // before, with no reinterpretation of either.
        static int[] Parse(string v) => v.Split('.').Select(int.Parse).ToArray();
        static int Compare(int[] a, int[] b)
        {
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var (x, y) = (i < a.Length ? a[i] : 0, i < b.Length ? b[i] : 0);
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        var current = Parse(AnthillRuntime.Version);
        var line = current[..^1];   // everything but the patch component

        var all = Regex.Matches(Read("CHANGELOG.md"), @"^##\s+v(\d+(?:\.\d+)+)",
                RegexOptions.Multiline)
            .Select(m => (text: m.Value.Trim(), key: Parse(m.Groups[1].Value)))
            .ToList();

        // Non-vacuity is asserted against the WHOLE file. Scoping it to the active line would make
        // the guard weakest exactly when a renumbering has just happened and only one entry exists —
        // the moment it is most needed.
        Assert.True(all.Count >= 5,
            $"Expected the changelog to have several release headings, found {all.Count}.");

        var entries = all
            .Where(e => e.key.Length == current.Length && e.key[..^1].SequenceEqual(line))
            .ToList();

        Assert.True(entries.Count >= 1,
            "No changelog heading belongs to the release line being written ("
            + string.Join(".", line) + ".x).");

        var duplicates = entries.GroupBy(p => p.key).Where(g => g.Count() > 1)
            .Select(g => string.Join(" AND ", g.Select(x => x.text))).ToList();
        Assert.True(duplicates.Count == 0,
            "The changelog names the same version more than once, so it cannot say what is in that "
          + "release:\n  " + string.Join("\n  ", duplicates));

        // Newest first, so each entry must be STRICTLY LOWER than the one above it.
        var outOfOrder = entries.Zip(entries.Skip(1))
            .Where(pair => Compare(pair.Second.key, pair.First.key) >= 0)
            .Select(pair => $"{pair.Second.text} is listed below {pair.First.text}").ToList();
        Assert.True(outOfOrder.Count == 0,
            "Changelog entries must read newest first:\n  " + string.Join("\n  ", outOfOrder));

        // v0.3.8.34 — ordering across the WHOLE file, with exactly one permitted discontinuity.
        //
        // The per-line check above cannot see across a renumbering: `0.3.8.34` sorts below `3.8.33`
        // by every numeric rule, so a global "strictly descending" assertion would fail on a
        // deliberate scheme change, and dropping the global check entirely would stop noticing an
        // entry inserted in the wrong place anywhere in 184 headings.
        //
        // So inversions are counted rather than forbidden. One is the renumbering. Two means
        // something was filed in the wrong place, and the message names it.
        // Scoped to the era still being maintained. v1 and v2 are FROZEN — the note above records
        // that this guard's first run found fifteen duplicate headings and several out-of-order
        // entries across them, and that rewriting 173 lines to satisfy a test would edit the record
        // of what shipped. Three of those inversions survive today and are deliberately left.
        //
        // The v3 lineage includes the v0.3.x renumbering: same line, one `0.` in front.
        var maintained = all.Where(e => e.key[0] >= 3 || e.key[0] == 0).ToList();

        var inversions = maintained.Zip(maintained.Skip(1))
            .Where(pair => Compare(pair.Second.key, pair.First.key) >= 0)
            .Select(pair => $"{pair.Second.text} is listed below {pair.First.text}")
            .ToList();

        Assert.True(inversions.Count <= 1,
            "The maintained changelog era reads newest-first apart from at most ONE deliberate "
            + $"renumbering boundary; found {inversions.Count} places where an entry is listed below "
            + "a lower version:\n  " + string.Join("\n  ", inversions));
    }

    /// <summary>
    /// Two ADRs must not share a number. `docs/ADR-003-AGENT-HARNESS.md` was written at the repo
    /// root while `docs/adr/ADR-003-worker-protocol.md` already existed, so twenty source files
    /// cited "ADR-003" meaning one of two different documents.
    /// </summary>
    [Fact]
    public void AdrNumbers_AreUnique_AndAllAdrsLiveTogether()
    {
        // NUMBERED ADRs only. docs/ADR-ADAPTIVE-MISSION-RUNTIME.md is unnumbered and so cannot
        // collide with anything; the invariant being protected is the numbering, not the filing.
        var strays = Directory.GetFiles(Path.Combine(Root(), "docs"), "ADR-[0-9]*.md")
            .Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(strays.Count == 0,
            "Numbered ADRs belong in docs/adr/, where a collision is visible: " + string.Join(", ", strays));

        var duplicates = Directory.GetFiles(Path.Combine(Root(), "docs", "adr"), "ADR-[0-9]*.md")
            .Select(f => Regex.Match(Path.GetFileName(f), @"^ADR-(\d+)"))
            .Where(m => m.Success).GroupBy(m => m.Groups[1].Value)
            .Where(g => g.Count() > 1).Select(g => "ADR-" + g.Key).ToList();
        Assert.True(duplicates.Count == 0,
            "These ADR numbers are used more than once, so a citation is ambiguous: "
          + string.Join(", ", duplicates));
    }
}
