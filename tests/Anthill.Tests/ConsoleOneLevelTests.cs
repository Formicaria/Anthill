using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// ONE ROW OF TABS, AND ONE EDITOR PER FACT. v0.3.8.127.
///
/// Three operator reports, one shape between them: the console kept offering the same choice in two
/// places, and the two places meant different things without saying so.
///
///   · Settings had a domain row and a second identical-looking row inside one of its sections. The
///     outer row changes PAGE; the inner one changed PANE. Nothing on screen distinguished them.
///   · Projects put the project list and the Director's automation backlog in columns side by side,
///     so one page carried two headers, two button groups and two unrelated subjects — and below a
///     wide desktop the columns wrapped, which is a tab with no way to choose it.
///   · The ant panel carried two "Display name / Accent colour" pairs. Not duplicates, which is
///     worse: one styles THAT ANT in the live view, the other renamed the whole CASTE.
///
/// The last is the one this repository has a name for. v0.3.8.124 removed the model-route editor
/// from that same panel with the argument "the operator cannot see the scope, so a mis-scoped change
/// is silent", and then left two name/colour editors sitting under it.
/// </summary>
public class ConsoleOneLevelTests
{
    private static string Ui(string file) =>
        File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.UI", file));

    // ---- Settings: one level -------------------------------------------------------------------

    /// <summary>
    /// THE FOUR SETTINGS PANES ARE SECTIONS, and the strip that used to choose them is not shown.
    ///
    /// The strip's markup deliberately SURVIVES: clicking a `.settings-tab` is still what switches a
    /// pane, and `showPage` still drives it from the route, so removing it would mean writing a
    /// second implementation of the switch. What must not survive is it being visible, which is what
    /// made two rows out of one choice.
    /// </summary>
    [Fact]
    public void TheSettingsPanes_AreSections_AndTheSecondRowIsGone()
    {
        var app = Ui("app.js");

        foreach (var route in new[] { "/settings/connection", "/settings/colony", "/settings/models", "/settings/diagnostics" })
            Assert.Contains($"route:'{route}'", app.Replace(" ", ""), StringComparison.Ordinal);

        // The section that used to hold them is gone as a destination…
        Assert.DoesNotContain("route:'/settings/general'", app.Replace(" ", ""), StringComparison.Ordinal);
        // …but not as a promise: an old bookmark still lands somewhere real.
        Assert.Contains("'/settings/general':'/settings/connection'", app.Replace(" ", ""), StringComparison.Ordinal);

        // Each section names the pane it opens, through the field that has carried that since v2.6.
        foreach (var stab in new[] { "stab:'connection'", "stab:'colony'", "stab:'models'", "stab:'info'" })
            Assert.Contains(stab, app.Replace(" ", ""), StringComparison.Ordinal);

        // Hidden in BOTH places. Script alone leaves it painting once before the script runs, and
        // CSS alone would leave a route able to show it again.
        Assert.Contains(".settings-tabs{display:none !important;}", Ui("index.html"), StringComparison.Ordinal);
        Assert.Contains("if(strip) strip.style.display='none';", app, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND NO SECTION ROW ANYWHERE CONTAINS TWO SECTIONS WITH THE SAME NAME.
    ///
    /// The specific confusion that made "System Info" worth renaming: the outer row already had a
    /// `System` section pointing at a different page, so promoting the pane under its own name would
    /// have put `System` and `System Info` side by side, one showing events and one showing
    /// diagnostics. It is `Diagnostics` now, and this keeps the general case closed.
    /// </summary>
    [Fact]
    public void NoDomain_OffersTwoSectionsWhoseNamesReadAsTheSameThing()
    {
        var app = Ui("app.js");

        foreach (Match domain in Regex.Matches(app, @"type:'domain',\s*id:'(?<id>[a-z]+)'.*?sections:\[(?<body>.*?)\n  \]\}", RegexOptions.Singleline))
        {
            var labels = Regex.Matches(domain.Groups["body"].Value, @"label:'([^']+)'")
                .Select(m => m.Groups[1].Value).ToList();

            Assert.True(labels.Count > 0, $"domain '{domain.Groups["id"].Value}' parsed with no sections");

            var duplicates = labels.GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(duplicates.Count == 0,
                $"domain '{domain.Groups["id"].Value}' offers two sections named "
              + string.Join(", ", duplicates) + " — an operator cannot tell which one holds what.");

            // The near-miss, which is the one that actually shipped: one label being another plus a
            // qualifier reads as the same destination twice.
            foreach (var a in labels)
                foreach (var b in labels)
                {
                    if (ReferenceEquals(a, b) || a == b) continue;
                    Assert.False(b.StartsWith(a + " ", StringComparison.OrdinalIgnoreCase),
                        $"'{a}' and '{b}' sit in the same row and read as the same destination.");
                }
        }
    }

    // ---- Projects: tabs, not columns ------------------------------------------------------------

    /// <summary>
    /// PROJECTS AND AUTOMATION ARE SECTIONS OF ONE DOMAIN, shown one at a time.
    ///
    /// Sections rather than a new in-page strip, so this uses the row Tools already uses — the
    /// alternative was a second tab idiom for the third layout in the console.
    /// </summary>
    [Fact]
    public void ProjectsAndAutomation_AreTabs_ShownOneAtATime()
    {
        var app = Ui("app.js");
        var flat = app.Replace(" ", "");

        Assert.Contains("route:'/projects',page:'projects',ptab:'projects'", flat, StringComparison.Ordinal);
        Assert.Contains("route:'/projects/automation',page:'projects',ptab:'automation'", flat, StringComparison.Ordinal);

        // Exactly one is in the page at a time — the property that makes them tabs rather than a
        // stack. Both hidden flags are written from the same call, so they cannot disagree.
        var show = SourceText.MemberBody(app, app.IndexOf("function projectsShowTab(which)", StringComparison.Ordinal));
        Assert.Contains("main.hidden=(projectsTab!=='projects')", show.Replace(" ", ""), StringComparison.Ordinal);
        Assert.Contains("auto.hidden=(projectsTab!=='automation')", show.Replace(" ", ""), StringComparison.Ordinal);

        // Automation keeps the visibility the standalone page had, in the section AND in the switch:
        // a non-admin never sees the tab, and asking for its route directly still lands on the list.
        Assert.Contains("route:'/projects/automation',page:'projects',ptab:'automation',vis:'admin'",
            flat, StringComparison.Ordinal);
        Assert.Contains("ROLE==='admin'", show, StringComparison.Ordinal);

        // The columns are gone rather than merely stacked: a flex row that wraps is what made
        // Automation appear below the list with no way to choose it.
        Assert.DoesNotContain("id=\"projects-cols\" style=\"display:flex", Ui("index.html"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A DECLARED ROUTE IS NOT A PROJECT ID.
    ///
    /// `/projects/{id}` is the console's one parameterised route, and `/projects/automation` matches
    /// its pattern exactly as well as `/projects/a1b2c3` does. Without the table being consulted
    /// first, the Automation tab opens a project workspace for a project called "automation" — which
    /// fetches nothing, finds nothing, and reads as the project list having failed.
    ///
    /// Asserted at BOTH sites, because the pattern is tested twice: once in `go` and once on the
    /// boot/hash path, and fixing one would leave a reload landing somewhere the click did not.
    /// </summary>
    [Fact]
    public void TheProjectDeepLink_NeverSwallowsADeclaredRoute()
    {
        var app = Ui("app.js").Replace(" ", "");

        Assert.Contains(@"constpm=!ROUTE_TABLE[route]&&/^\/projects\/([A-Za-z0-9]+)$/.exec(route)",
            app, StringComparison.Ordinal);
        Assert.Contains(@"if(!ROUTE_TABLE[h]&&/^\/projects\/[A-Za-z0-9]+$/.test(h))",
            app, StringComparison.Ordinal);
    }

    // ---- The ant panel: one editor per fact ------------------------------------------------------

    /// <summary>
    /// THE PANEL HAS ONE NAME/COLOUR EDITOR, and it is the live view's.
    ///
    /// The inspector's block is now information — which provider and model this ant runs on, and
    /// where that is set — with no inputs and no Save. Asserted as an absence of the CONTROLS rather
    /// than of the words, because the panel legitimately still says "Runs on ollama".
    /// </summary>
    [Fact]
    public void TheAntPanel_OffersOneNameAndColourEditor()
    {
        var app = Ui("app.js");
        var html = Ui("index.html");

        // The inspector's editor is gone, with everything that served it.
        foreach (var token in new[] { "ins-name", "ins-color", "ins-msg", "data-insact", "inspectorSave" })
            Assert.DoesNotContain(token, app, StringComparison.Ordinal);

        // What remains is read-only, and still says where the route IS set rather than implying it.
        var block = SourceText.MemberBody(app, app.IndexOf("function inspectorEditorHtml(n)", StringComparison.Ordinal));
        Assert.Contains("Set per project in Projects → Settings.", block, StringComparison.Ordinal);
        Assert.DoesNotContain("<input", block, StringComparison.Ordinal);

        // And the editor that survives is the live panel's, writing the renderer's own store.
        Assert.Contains("id=\"clb-ant-name\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"clb-ant-color\"", html, StringComparison.Ordinal);
        Assert.Contains("live.setAntStyle(antId,", Ui("colony-home.js"), StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CASTE NAMES ALREADY ON DISK ARE STILL READ.
    ///
    /// Removing the only writer of `uiState.castes` is a decision about what an operator may CHANGE.
    /// Dropping the read as well would have been a decision to erase what they already set, which is
    /// a different and much worse one — a colony that has renamed its castes would have found them
    /// silently back to defaults after an upgrade.
    /// </summary>
    [Fact]
    public void ACasteNameSetInAnEarlierRelease_IsStillHonoured()
    {
        var app = Ui("app.js");

        Assert.Contains("uiState.castes[caste]?.name", app, StringComparison.Ordinal);
        Assert.Contains("uiState.castes[caste]?.color", app, StringComparison.Ordinal);
        // The document still carries the key, so nothing is dropped on the next write either.
        Assert.Contains("doc.castes=uiState.castes", app.Replace(" ", ""), StringComparison.Ordinal);
    }
}
