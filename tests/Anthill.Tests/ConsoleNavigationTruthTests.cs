using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The navigation may not promise a subsystem the backend does not have. v0.3.8.41.
///
/// FOUND BY CLICKING IT. The console shipped a top-level nav item labelled <b>Scheduled</b>, with a
/// clock icon, at its own route <c>/scheduled</c>. Anthill has no scheduling subsystem: there is no
/// cron, interval, cadence or next-run anywhere in the objective model, the Director or the API.
/// The item rendered the Director's standing objectives — which the console itself titles
/// <c>Objectives</c>, so an operator was told two different things one click apart.
///
/// It is a small lie and it is the expensive kind. A label is a promise about what the software
/// does, and the operator who clicks "Scheduled" is looking for something they will not find, then
/// has to work out whether they are confused or the product is. The comment beside the entry
/// admitted it in passing — "Scheduled is the Director's standing objectives" — which is how these
/// survive: everyone who reads the code knows, and nobody who reads the SCREEN does.
///
/// The rule below is deliberately a LEDGER rather than a general theory of naming. Only a human can
/// say whether a label is honest; what a test can do is pin the specific promises that were caught
/// being false, so they cannot come back quietly.
/// </summary>
public class ConsoleNavigationTruthTests
{
    private static string AppJs() =>
        File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.UI", "app.js"));

    private static string ApiSource()
    {
        var dir = Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Api");
        return string.Concat(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText));
    }

    /// <summary>Every nav label the information architecture declares.</summary>
    private static List<string> NavLabels() =>
        Regex.Matches(AppJs(), @"label\s*:\s*'([^']+)'")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Labels that name a SUBSYSTEM, and the backend evidence each one requires.
    ///
    /// A label goes in here once it has been caught promising something absent. The value is a
    /// substring that must appear in a mapped API route for the promise to be true.
    /// </summary>
    private static readonly Dictionary<string, string> LabelsRequiringBackend =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // v0.3.8.41. Restore this label only alongside a real scheduling route.
            ["Scheduled"] = "schedul",
        };

    [Fact]
    public void NoNavLabel_PromisesASubsystemTheApiDoesNotHave()
    {
        var labels = NavLabels();
        var api = ApiSource();

        var broken = new List<string>();
        foreach (var (label, evidence) in LabelsRequiringBackend)
        {
            if (!labels.Contains(label, StringComparer.OrdinalIgnoreCase)) continue;

            // Does the API map any route whose path contains the evidence substring?
            var mapped = Regex.Matches(api, "\"(/[^\"]*)\"")
                .Any(m => m.Groups[1].Value.Contains(evidence, StringComparison.OrdinalIgnoreCase));

            if (!mapped) broken.Add($"'{label}' (no API route containing \"{evidence}\")");
        }

        Assert.True(broken.Count == 0,
            "The console navigation offers these, and the backend has nothing behind them. A label "
          + "is a promise about what the software does: " + string.Join(", ", broken));
    }

    /// <summary>
    /// And the specific one, pinned by name — the nav must not say "Scheduled" while Anthill has no
    /// scheduling of any kind.
    ///
    /// Asserted separately from the ledger above so the failure names the actual history rather than
    /// a generic rule, and so deleting the ledger entry does not silently delete the lesson.
    /// </summary>
    [Fact]
    public void TheNavDoesNotOfferScheduling_BecauseThereIsNone()
    {
        var source = string.Concat(
            File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Core", "Domain", "AutonomyModels.cs")),
            string.Concat(Directory
                .GetFiles(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Core", "Autonomy"), "*.cs")
                .Select(File.ReadAllText)));

        var hasScheduling = Regex.IsMatch(SourceText.CodeOnly(source),
            @"\b(cron|NextRun|next_run|IntervalMinutes|Cadence)\b", RegexOptions.IgnoreCase);

        if (hasScheduling) return;   // someone built it; the label is allowed back

        Assert.DoesNotContain("label:'Scheduled'", AppJs().Replace(" ", ""), StringComparison.Ordinal);
    }

    /// <summary>
    /// Objectives is no longer a standalone destination — it folded into Projects (UI/UX pass §5).
    ///
    /// The invariant that matters is unchanged in spirit: there is no duplicate/dead route for the
    /// objboard page. There is no top-level Objectives nav item and no `/scheduled` alias, and the
    /// page's canonical home is the Projects route it now lives under, so the nav and the console's
    /// notion of "where objboard lives" cannot disagree.
    /// </summary>
    [Fact]
    public void ObjectivesFoldedIntoProjects_LeavesNoDuplicateRoute()
    {
        var app = AppJs().Replace(" ", "");

        // No standalone Objectives destination and no legacy /scheduled route.
        Assert.DoesNotContain("route:'/objectives',page:'objboard'", app, StringComparison.Ordinal);
        Assert.DoesNotContain("route:'/scheduled'", app, StringComparison.Ordinal);

        // objboard's canonical home is Projects, where Objectives now lives.
        Assert.Contains("objboard:'/projects'", app, StringComparison.Ordinal);

        // /objectives still resolves for old bookmarks — as an alias to Projects, not a live route.
        Assert.Contains("'/objectives':'/projects'", app, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every nav item has an icon. Without one `buildNav` interpolates `IAICON[d.id]` unguarded and
    /// writes the literal string "undefined" into the rail — the defect v3.8.39 recorded, which a
    /// renamed id reintroduces for free.
    /// </summary>
    [Fact]
    public void EveryTopLevelNavItem_HasAnIcon()
    {
        var app = AppJs();

        var iconKeys = Regex.Matches(app, @"^\s{2}(\w+)\s*:\s*'<svg", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var navIds = Regex.Matches(app, @"type\s*:\s*'(?:item|domain)'\s*,\s*id\s*:\s*'(\w+)'")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(navIds);
        var missing = navIds.Where(id => !iconKeys.Contains(id)).ToList();

        Assert.True(missing.Count == 0,
            "These nav ids have no IAICON entry, so buildNav writes \"undefined\" into the nav icon: "
            + string.Join(", ", missing));
    }
}
