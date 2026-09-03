using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Every document says whether it is CURRENT or HISTORICAL, and a current one may not describe a
/// superseded state. v0.3.8.75 — PLAN.md §2 item 1, "reconcile the documentation", given the only
/// form that lasts.
///
/// WHY THIS EXISTS RATHER THAN ANOTHER REVIEW PASS. Item 1 has been absorbed into other releases
/// repeatedly and keeps reappearing, because a reconciliation is true on the day it is done and
/// decays silently afterwards. This release alone corrected three documents that had sent work in
/// the wrong direction:
///
///   * qualification scenario 3's chain named a `docs_patch_set` pipeline that does not exist and
///     should not — following it would have meant building an applier for an artifact designed
///     never to be applied;
///   * the same entry then called the remainder "a missing script book" when it was a missing seam;
///   * the ledger's own header claimed no scenario was open, one release before that was true.
///
/// And `HANDOFF.md` — the file whose entire purpose is to be pasted into a fresh session — opened
/// with "The 3.8 line is CLOSED at v0.3.8.34" while the shipping release was v0.3.8.74. That one was
/// not merely stale: a handoff is read by someone who knows nothing else yet.
///
/// THE RULE. A document is either CURRENT (it describes how things are, and must not name a version
/// older than the shipped one without saying it is history) or HISTORICAL (it describes a moment,
/// and says so in its opening lines). Every file in `docs/` must be classified, so adding one forces
/// the decision rather than defaulting to "current and quietly rotting".
///
/// WHAT THIS CANNOT DO, said plainly: it cannot tell whether a current document is CORRECT. A
/// sentence can be wrong without naming a version at all — the `docs_patch_set` chain named none.
/// This closes the subclass that is mechanically detectable: a document presenting itself as current
/// while pointing at a superseded release. The rest is reading, and the changelog is where the
/// reading gets recorded.
/// </summary>
public class DocumentCurrencyTests
{
    /// <summary>
    /// Documents that describe how things ARE. A version mentioned here must be the shipped one, or
    /// the sentence must mark it as history ("since v…", "at v…", "fixed in v…" and similar are
    /// historical references INSIDE a current document, which is legitimate and common).
    /// </summary>
    private static readonly string[] Current =
    {
        "PLAN.md", "ANT_EXECUTION.md", "QUALIFICATION.md", "HANDOFF.md",
        "CONTRACTS.md", "APPROVALS.md", "DEPLOYMENT.md", "HOMELAB.md",
        "AUTONOMY.md", "TRAINING_MISSIONS.md", "QA-CHECKLIST.md",
        "ADR-ADAPTIVE-MISSION-RUNTIME.md",
        // v0.3.8.112 — the guard hierarchy. CURRENT: it describes how guards are written, which is
        // a standing rule rather than a report on one moment, and it names no version.
        "GUARDS.md",
        // v0.3.8.114 — the configuration reference. CURRENT by construction rather than by
        // discipline: it is GENERATED from `ConfigCatalog` and `ConfigCatalogTests` regenerates and
        // compares it, so it cannot describe a setting the runtime does not have. It is the one
        // document in this list that could not go stale without failing the build.
        "CONFIGURATION.md",
    };

    /// <summary>
    /// Documents that describe a MOMENT — a release report, an audit measured against a commit, a
    /// brief written for one piece of work. They may name any version, and must say what they are in
    /// their opening lines so a reader knows before the content starts.
    /// </summary>
    private static readonly string[] Historical =
    {
        "RELEASE-REPORT-v0.3.8.42.md", "UI-CONTRACT-AUDIT.md", "UI-ALIGNMENT-BRIEF.md",
        "DASHBOARD_GRID_MIGRATION.md",
    };

    /// <summary>Documents whose whole content is "this moved" — they point and stop.</summary>
    private static readonly string[] Pointers = { "AUTONOMY-10.md" };

    private static IEnumerable<string> DocFiles() =>
        Directory.EnumerateFiles(Path.Combine(SourceText.RepoRoot(), "docs"), "*.md")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!);

    /// <summary>
    /// EVERY document is classified. A new file must be put in a bucket deliberately, because the
    /// default — nobody deciding — is exactly how a document comes to present itself as current
    /// while describing something that stopped being true.
    /// </summary>
    [Fact]
    public void EveryDocument_IsClassified()
    {
        var known = Current.Concat(Historical).Concat(Pointers).ToHashSet(StringComparer.Ordinal);
        var unclassified = DocFiles().Where(n => !known.Contains(n)).OrderBy(n => n).ToList();

        Assert.True(unclassified.Count == 0,
            "these documents are in neither the CURRENT nor the HISTORICAL list: "
          + string.Join(", ", unclassified)
          + ". Decide which it is. A document nobody classified is one that will read as current "
          + "forever, whatever happens to the thing it describes.");
    }

    /// <summary>And the lists name only documents that exist — a stale entry excuses a file that is
    /// gone, which is the same rot pointed the other way.</summary>
    [Fact]
    public void TheClassificationLists_NameOnlyRealDocuments()
    {
        var present = DocFiles().ToHashSet(StringComparer.Ordinal);
        var missing = Current.Concat(Historical).Concat(Pointers)
            .Where(n => !present.Contains(n)).OrderBy(n => n).ToList();

        Assert.True(missing.Count == 0,
            "these classified documents do not exist: " + string.Join(", ", missing));
    }

    /// <summary>
    /// A HISTORICAL document says so before its content starts — in its title or its opening lines.
    /// A reader who does not know reads a snapshot as a description.
    /// </summary>
    [Fact]
    public void EveryHistoricalDocument_SaysSoAtTheTop()
    {
        var silent = new List<string>();

        foreach (var name in Historical)
        {
            var head = string.Join("\n", File.ReadLines(
                Path.Combine(SourceText.RepoRoot(), "docs", name)).Take(8));

            var declares =
                System.Text.RegularExpressions.Regex.IsMatch(head, @"v\d+\.\d+\.\d+(\.\d+)?")
                || head.Contains("audit", StringComparison.OrdinalIgnoreCase)
                || head.Contains("report", StringComparison.OrdinalIgnoreCase)
                || head.Contains("brief", StringComparison.OrdinalIgnoreCase)
                || head.Contains("migration", StringComparison.OrdinalIgnoreCase);

            if (!declares) silent.Add(name);
        }

        Assert.True(silent.Count == 0,
            "these documents record a moment and do not say so in their opening lines: "
          + string.Join(", ", silent)
          + ". Name the release or the commit they describe, in the title or the first paragraph.");
    }

    /// <summary>
    /// THE ONE THAT MATTERS. A CURRENT document may not claim to BE a superseded release.
    ///
    /// The distinction is between describing and referencing. "Fixed in v0.3.8.59" inside a current
    /// document is a historical reference and is fine — this repository's documents are full of them
    /// and they are most of their value. What is refused is a line that presents an old release as
    /// the state of things: "Shipping release: v0.3.8.34", "Current version: v0.3.8.42", "The line is
    /// CLOSED at v0.3.8.34". `HANDOFF.md` opened with the last of those, forty releases stale, and
    /// nothing failed.
    /// </summary>
    [Fact]
    public void NoCurrentDocument_PresentsASupersededReleaseAsTheStateOfThings()
    {
        var shipped = AnthillRuntime.Version;
        var offenders = new List<string>();

        // Phrases that assert a version IS the present state, rather than referring to one.
        //
        // `as of` was in this list on the first run and had to come out, which is the same trap this
        // repository keeps finding in its own guards: it matched "Provenance already carries most of
        // this per artifact AS OF v0.3.8.57" in QUALIFICATION.md — a historical reference inside a
        // current document, and exactly the construction that must stay legal. The guard was wrong,
        // not the document, so the pattern narrowed rather than the sentence changing.
        var assertsCurrency = new System.Text.RegularExpressions.Regex(
            @"(?:shipping release|current version|current release|latest release|the line is closed at)\s*[:\-—]?\s*\**v?(\d+\.\d+\.\d+(?:\.\d+)?)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var name in Current)
        {
            var text = File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "docs", name));
            foreach (System.Text.RegularExpressions.Match m in assertsCurrency.Matches(text))
                if (!string.Equals(m.Groups[1].Value, shipped, StringComparison.Ordinal))
                    offenders.Add($"{name}: \"{m.Value.Trim()}\" (shipped is {shipped})");
        }

        Assert.True(offenders.Count == 0,
            "these current documents present a superseded release as the state of things: "
          + string.Join("; ", offenders)
          + ". Either update the line, or move the document to the HISTORICAL list and say at the "
          + "top which moment it records. A document read by someone who knows nothing else does "
          + "not merely age when it is stale — it misdirects.");
    }

    /// <summary>
    /// A POINTER points. Short, and it names where the content went, so "folded into" cannot become
    /// "deleted and forgotten" without anything noticing.
    /// </summary>
    [Fact]
    public void EveryPointer_NamesWhereTheContentWent()
    {
        foreach (var name in Pointers)
        {
            var path = Path.Combine(SourceText.RepoRoot(), "docs", name);
            var text = File.ReadAllText(path);

            Assert.True(File.ReadLines(path).Count() <= 60,
                $"{name} is a pointer and has grown into a document; either it is current content "
              + "or it points elsewhere, and it cannot be both.");
            Assert.Contains("PLAN.md", text);
        }
    }
}
