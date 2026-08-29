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
    /// THE RELEASE SEQUENCE IS ORDERED, UNIQUE, AND STARTS AT THE SHIPPING VERSION.
    ///
    /// Parsed from the program table's leading cells rather than from the surrounding prose, so
    /// rewriting the descriptions cannot break it and reordering the releases cannot hide in them.
    /// </summary>
    [Fact]
    public void TheUniversalWorkflowProgram_IsOrderedAndUnique()
    {
        var ids = Regex.Matches(Plan(), @"^\|\s*\*\*\.(?<n>\d{2,3})\*\*\s*\|", RegexOptions.Multiline)
            .Select(m => int.Parse(m.Groups["n"].Value))
            .ToList();

        Assert.True(ids.Count == 10,
            $"the universal-workflow program declares {ids.Count} releases; it is a ten-release "
          + "sequence. Add or remove a row deliberately, and update this expectation with it.");

        Assert.Equal(ids.OrderBy(n => n).ToList(), ids);
        Assert.Equal(ids.Distinct().Count(), ids.Count);

        // It begins at the release being built: a program whose first entry has already shipped is
        // a plan describing the past.
        var shipped = int.Parse(AnthillRuntime.Version.Split('.').Last());
        Assert.Equal(shipped, ids[0]);
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
