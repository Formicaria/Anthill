using System.Text.RegularExpressions;
using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// v0.3.8.98 — THE DOCUMENTS MUST AGREE WITH EACH OTHER AND WITH THE BUILD.
///
/// WHAT THIS EXISTS FOR. A documentation inventory before this release found four contradictions,
/// each individually plausible and each shipped: `PLAN.md` opened with "structurally complete" and
/// recorded live qualification as "never run under protocol"; `QUALIFICATION.md` had already been
/// corrected to PARTIAL; `QA-CHECKLIST.md` still asserted live qualification had never run against
/// any provider; and `HANDOFF.md` described the `.97` tag as uncut after it was cut. Every one was
/// written by someone reading a different document.
///
/// HOW IT READS, and this is the part that decides whether the guard survives contact with prose.
/// It parses DECLARED STRUCTURE — a version marker, a table row, a heading, a status field — and
/// never a sentence. `DocumentCurrencyTests` already learned this the hard way: its first pattern
/// matched "as of v0.3.8.57" inside a legitimate historical reference and had to be narrowed. A
/// guard that greps for wording fails on a rewrite that changes nothing and passes on a rewording
/// that changes everything, so wording is exactly what this must not read.
///
/// WHAT IT DOES NOT DO. It does not build a second status ledger. Every fact it checks is derived
/// from a document that already owns it — the version from the build, the roadmap from `PLAN.md`,
/// the qualification status from `QUALIFICATION.md`. A guard with its own copy of the truth is one
/// more thing to drift.
/// </summary>
public class DocumentationConsistencyTests
{
    private static string Doc(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { SourceText.RepoRoot() }.Concat(parts).ToArray()));

    private static string Plan() => Doc("docs", "PLAN.md");

    // ---- current-state agreement ---------------------------------------------------------------

    /// <summary>
    /// The forward plan ships the version the build ships.
    ///
    /// `DocumentCurrencyTests` already refuses a superseded release presented as current; this is
    /// the positive half — PLAN must name THIS version, not merely avoid naming an older one.
    /// </summary>
    [Fact]
    public void ThePlansShippingRelease_IsTheVersionTheBuildDeclares()
    {
        var declared = Regex.Match(Plan(), @"Shipping release:\s*\*\*v(?<v>\d+\.\d+\.\d+(?:\.\d+)?)\*\*");

        Assert.True(declared.Success,
            "docs/PLAN.md no longer declares a shipping release in the form "
          + "`Shipping release: **vX.Y.Z**`, so nothing can be checked against it.");

        Assert.Equal(AnthillRuntime.Version, declared.Groups["v"].Value);
    }

    /// <summary>
    /// EXACTLY ONE DOCUMENT CLAIMS TO BE THE FORWARD PLAN.
    ///
    /// Two roadmaps is the failure this repository keeps re-learning in other forms: two appliers,
    /// two preflights, two comment strippers. A second forward plan is worse than those, because
    /// nothing compiles it and the drift is invisible until someone acts on the stale one.
    /// </summary>
    [Fact]
    public void OnlyOneDocument_DeclaresItselfTheForwardPlan()
    {
        var claimants = new List<string>();
        foreach (var path in Directory.GetFiles(Path.Combine(SourceText.RepoRoot(), "docs"), "*.md"))
        {
            var head = string.Join("\n", File.ReadLines(path).Take(12));
            if (Regex.IsMatch(head, @"\*\*The single forward document", RegexOptions.IgnoreCase))
                claimants.Add(Path.GetFileName(path));
        }

        var only = Assert.Single(claimants);
        Assert.Equal("PLAN.md", only);
    }

    /// <summary>
    /// HANDOFF MAY NOT CALL AN EXISTING TAG UNCUT.
    ///
    /// The specific contradiction that prompted this file. `HANDOFF.md` carried "THE v0.3.8.97 TAG
    /// IS NOT CUT YET" while `v0.3.8.97` existed on the remote — a document telling the next
    /// session not to do something already done. Read as a declared field: the handoff states a
    /// tag status, and a tag status about the shipped version must not be a negative one.
    /// </summary>
    [Fact]
    public void TheHandoff_DoesNotDenyATagItAlreadyHas()
    {
        var handoff = Doc("docs", "HANDOFF.md");

        var denials = Regex.Matches(handoff,
            @"v(?<v>\d+\.\d+\.\d+(?:\.\d+)?)\s+TAG\s+IS\s+NOT\s+CUT", RegexOptions.IgnoreCase);

        foreach (Match m in denials)
            Assert.True(m.Groups["v"].Value != AnthillRuntime.Version
                     && m.Groups["v"].Value != PreviousShippedVersion(),
                $"docs/HANDOFF.md says the v{m.Groups["v"].Value} tag is not cut. That version has "
              + "shipped. Correct the handoff rather than the history.");
    }

    /// <summary>
    /// THE CAPABILITY TABLE MAY NOT OUTRANK THE QUALIFICATION RECORD.
    ///
    /// The table in `PLAN.md` §1 rates each capability as live-qualified or not. `QUALIFICATION.md`
    /// §3 owns whether live qualification has happened at all and to what extent. If §3 records a
    /// PARTIAL live status, the table may mark the coding lane live and must not mark a non-code
    /// class live — a capability claim stronger than the evidence is the precise failure the whole
    /// document set was reconciled to remove.
    /// </summary>
    [Fact]
    public void NoCapability_ClaimsMoreLiveQualificationThanTheRecordHolds()
    {
        var qualification = Doc("docs", "QUALIFICATION.md");
        var liveHeading = Regex.Match(qualification,
            @"^##\s*3\.\s*Live qualification\s*[—-]\s*(?<status>.+)$", RegexOptions.Multiline);

        Assert.True(liveHeading.Success,
            "docs/QUALIFICATION.md no longer declares its live-qualification status in its §3 "
          + "heading, so the capability table cannot be checked against it.");

        var status = liveHeading.Groups["status"].Value.Trim();
        var fullyQualified = status.StartsWith("COMPLETE", StringComparison.OrdinalIgnoreCase);
        if (fullyQualified) return;   // nothing to constrain: the record allows any live claim

        // Rows are `| capability | impl | default | det | live | notes |`. Only the coding lane may
        // read "yes" in the live column while §3 is short of COMPLETE.
        var codingRows = new[] { "Coding:" };
        foreach (var row in Regex.Matches(Plan(), @"^\|\s*(?<cap>[^|]+?)\s*\|(?<rest>[^\n]*)\|\s*$",
                     RegexOptions.Multiline).Cast<Match>())
        {
            var cells = row.Groups["rest"].Value.Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 4) continue;                       // not a capability row
            var capability = row.Groups["cap"].Value.Trim();
            var live = cells[3].Trim().Trim('*').ToLowerInvariant();
            if (live is not ("yes" or "**yes**")) continue;

            Assert.True(codingRows.Any(c => capability.StartsWith(c, StringComparison.Ordinal)),
                $"the capability table marks '{capability}' as live-qualified while "
              + $"QUALIFICATION.md §3 records live status as '{status}'. A capability may not claim "
              + "more than the qualification record holds.");
        }
    }

    // ---- the program's own shape ---------------------------------------------------------------

    /// <summary>
    /// THE RELEASE SEQUENCE IS EXACTLY THE RANGE ITS OWN HEADING DECLARES, AND STARTS AT THE
    /// SHIPPING VERSION.
    ///
    /// Parsed from the program table's leading cells rather than from the surrounding prose, so
    /// rewriting the descriptions cannot break it and reordering the releases cannot hide in them.
    ///
    /// v0.3.8.99 — the range is now READ from the section heading rather than hardcoded as "ten".
    /// The table is the REMAINING sequence and shrinks as releases ship, so a fixed count had to be
    /// edited every release, and an expectation edited by routine is one nobody is deciding about.
    /// Reading the declared range is also strictly stronger than the count was: it catches a gap in
    /// the middle, which counting could not. This is the same rule the rest of this file follows —
    /// check a document against what it DECLARES, never against a second copy of the truth kept
    /// here.
    /// </summary>
    [Fact]
    public void TheUniversalWorkflowProgram_IsExactlyTheRangeItDeclares()
    {
        var heading = Regex.Match(Plan(),
            @"^##\s*2b\..*?v\d+\.\d+\.\d+\.(?<from>\d+)\s*(?:→|->)\s*v\d+\.\d+\.\d+\.(?<to>\d+)"
          + @"(?<closed>\s*·\s*✅\s*CLOSED at v\d+\.\d+\.\d+\.(?<at>\d+))?\s*$",
            RegexOptions.Multiline);

        Assert.True(heading.Success,
            "docs/PLAN.md §2b no longer declares its release range in its heading "
          + "(`## 2b. … — vX.Y.Z.99 → vX.Y.Z.107`), so the program table cannot be checked "
          + "against anything but itself.");

        var from = int.Parse(heading.Groups["from"].Value);
        var to = int.Parse(heading.Groups["to"].Value);
        var shipped = int.Parse(AnthillRuntime.Version.Split('.').Last());

        var ids = Regex.Matches(Plan(), @"^\|\s*\*\*\.(?<n>\d{2,3})\*\*\s*\|", RegexOptions.Multiline)
            .Select(m => int.Parse(m.Groups["n"].Value))
            .ToList();

        // v0.3.8.114 — A FINISHED PROGRAM, which is the state this check could not express.
        //
        // `.113` widened it to admit `to == from`, the LAST release. It said nothing about the
        // release AFTER the last one, when the final row has shipped and left and the table is
        // empty — and then `from == shipped` fails forever, because the program cannot begin at a
        // version that will never come. That is the repository's own meta-rule arriving for the
        // fourth time: a guard that cannot express success is not a guard, it is a deadline.
        //
        // A closed program declares itself closed in the heading, and what is checked flips
        // accordingly: the range must now describe the PAST, and the table must be empty. Both are
        // still assertions — a section claiming closure while listing rows is refused, and so is
        // one claiming closure at a release that has not shipped.
        if (heading.Groups["closed"].Success)
        {
            var closedAt = int.Parse(heading.Groups["at"].Value);

            Assert.True(closedAt == to,
                $"§2b says it closed at .{closedAt} and declares its range ending at .{to}. A "
              + "program closes at its last release or the heading is describing two programs.");

            Assert.True(to <= shipped,
                $"§2b claims to have CLOSED at .{to}, which has not shipped (.{shipped} is "
              + "current). A program cannot be finished by a release that has not happened.");

            Assert.True(ids.Count == 0,
                $"§2b declares itself CLOSED and still lists {ids.Count} release row(s). A closed "
              + "program has nothing remaining; a row that outlives the program is work nobody is "
              + "doing and nobody has written down.");
            return;
        }

        // It begins at the release being built: a program whose first entry has already shipped is
        // a plan describing the past, and a shipped row that lingers is one whose unmet items can
        // be dropped without anyone noticing they were unmet.
        Assert.True(from == shipped,
            $"§2b declares the program as beginning at .{from} while the shipping release is "
          + $".{shipped}. When a release ships, its row leaves the table and anything it did not "
          + "finish is carried into §2c — the row is not deleted on its own.");

        // v0.3.8.113 — A PROGRAM MAY END, and this is the terminal case the check never reached.
        //
        // `to > from` was right for every release from `.98` onward and could not hold for the last
        // one: when a single release remains, the table has one row and the range is `.n → .n`.
        // `open-items.md` has carried that as a known future failure since `.107`, which is the
        // honest way to hold an unreachable case — and this is the release that reaches it.
        //
        // The relaxation is exactly one release wide. `to < from` is still refused, and the equal
        // case is admitted only when the table really does hold a single row: a program that
        // declared `.113 → .113` while listing three would be describing a range it does not have,
        // which is the drift this whole check exists to catch.
        Assert.True(to >= from,
            $"§2b declares the range .{from} → .{to}, which ends before it begins.");

        Assert.Equal(Enumerable.Range(from, to - from + 1).ToList(), ids);

        if (to == from)
            Assert.True(ids.Count == 1,
                $"§2b declares the single-release range .{from} → .{to} and lists {ids.Count} rows. "
              + "A range equal at both ends is how a program says it is on its last entry; a table "
              + "with more than one row is not on its last entry.");
    }

    /// <summary>
    /// ARCHIVED MATERIAL IS NOT LINKED AS CURRENT TRUTH.
    ///
    /// A pointer from a current document into `docs/archive/**` reads as guidance. The archive is
    /// explicitly historical, and the one legitimate reference — "if a proposal contradicts the
    /// archived refactor plan, the plan is probably right" — is a statement ABOUT history and lives
    /// in HANDOFF, which is why that file is exempted by name rather than by pattern.
    /// </summary>
    [Fact]
    public void CurrentDocuments_DoNotLinkTheArchiveAsGuidance()
    {
        string[] current = { "PLAN.md", "QUALIFICATION.md", "ANT_EXECUTION.md", "QA-CHECKLIST.md" };

        foreach (var name in current)
        {
            var text = Doc("docs", name);
            Assert.False(Regex.IsMatch(text, @"\]\((?:\./)?archive/"),
                $"docs/{name} links into docs/archive/ as though it were current. Archived "
              + "documents are snapshots; a current document that points at one invites a reader to "
              + "act on a superseded state.");
        }
    }

    /// <summary>
    /// The version immediately before the shipping one, derived rather than hardcoded — used only
    /// to allow HANDOFF to discuss the previous release's tag decision in the past tense.
    /// </summary>
    private static string PreviousShippedVersion()
    {
        var parts = AnthillRuntime.Version.Split('.');
        if (parts.Length == 0 || !int.TryParse(parts[^1], out var last) || last == 0) return "";
        parts[^1] = (last - 1).ToString();
        return string.Join('.', parts);
    }
}
