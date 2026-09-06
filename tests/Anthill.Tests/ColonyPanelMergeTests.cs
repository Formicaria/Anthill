using Xunit;

namespace Anthill.Tests;

/// <summary>
/// ONE ANT, ONE PANEL. v0.3.8.125.
///
/// The colony page described the ant you clicked in two places at once. The live panel on the left
/// gave its name, colour, role, "trail 0.62" and four counters; the Ant Inspector card on the right
/// gave its status, chamber, "Pheromone 0.74", purpose, permissions, tools, live task load and
/// workers. Same ant, two vocabularies for the same facts, and an operator answering one question
/// had to read both — which is defect class #5 wearing a layout: two implementations of one rule,
/// where the rule is "tell me about this ant".
///
/// ONE RENDERER, TWO SINKS — and v0.3.8.126 is the correction that made that true.
///
/// `.125` merged the two panels by MOVING `#agent-detail` into the live panel, reasoning that the
/// colony canvas area is re-parented between hosts the same way. That reasoning was wrong, and
/// wrong in a way that only appears after visiting the dashboard: the Ant Inspector WIDGET
/// re-parents `#agent-detail` into itself and does not give it back, so the live panel was left
/// with no element to render into and every ant came up blank. The canvas survives re-parenting
/// because exactly one host wants it at a time; two panels that both want to show the same ant do
/// not.
///
/// So each host owns its own element and `showInspector` writes the SAME markup to every host that
/// exists. Still one implementation — one string, however many sinks — which is the property the
/// original design was reaching for and the reason a second copy in colony-home.js was never the
/// answer either.
/// </summary>
public class ColonyPanelMergeTests
{
    private static string Ui(string file) =>
        File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.UI", file));

    /// <summary>
    /// EACH HOST OWNS ITS OWN ELEMENT, and there is exactly one of each.
    ///
    /// The live panel renders into `#clb-ant-detail`; the dashboard widget adopts `#agent-detail`.
    /// Two ids, one apiece — because the widget takes its element and keeps it, and a panel whose
    /// host has been adopted away renders nowhere with nothing to read.
    ///
    /// The counts are the load-bearing half. A duplicate id is not a visible bug: both render,
    /// `getElementById` silently picks the first, and the operator watches one panel never update.
    /// </summary>
    [Fact]
    public void EachInspectorHost_ExistsExactlyOnce_AndIsNotShared()
    {
        var html = Ui("index.html");

        foreach (var id in new[] { "agent-detail", "clb-ant-detail" })
        {
            var count = System.Text.RegularExpressions.Regex.Matches(html, $"id=\"{id}\"").Count;
            Assert.True(count == 1, $"expected exactly one #{id} element, found {count}.");
        }

        // The live panel's host sits inside the live record panel, after the counters it follows.
        // Asserted by ORDER rather than by nesting: the markup is flat text here, and an assertion
        // about nesting would be a lie about what was actually checked.
        var record = html.IndexOf("id=\"clb-record\"", StringComparison.Ordinal);
        var stats = html.IndexOf("id=\"clb-ant-stats\"", StringComparison.Ordinal);
        var detail = html.IndexOf("id=\"clb-ant-detail\"", StringComparison.Ordinal);
        Assert.True(record >= 0 && stats > record, "the counters are not inside the live record panel");
        Assert.True(detail > stats, "the inspector must follow the counters inside the live panel");

        // And the card that used to hold the inspector in the colony's right sidebar is still gone.
        Assert.DoesNotContain("id=\"card-inspector\"", html);
    }

    /// <summary>
    /// THE MARKUP IS BUILT ONCE AND WRITTEN TO EVERY HOST THAT EXISTS.
    ///
    /// The property that keeps "two sinks" from becoming two implementations. `showInspector` must
    /// assemble one string and fan it out — not branch per host, and not write to a single
    /// hard-coded id, which is what left the live panel blank in v0.3.8.125.
    /// </summary>
    [Fact]
    public void TheInspector_RendersIntoEveryHost_RatherThanOneHardCodedId()
    {
        var js = Ui("app.js");
        var body = SourceText.MemberBody(js, js.IndexOf("function showInspector(n)", StringComparison.Ordinal));

        Assert.Contains("inspectorHosts().forEach(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("getElementById('agent-detail').innerHTML", body, StringComparison.Ordinal);

        var hosts = SourceText.MemberBody(js, js.IndexOf("function inspectorHosts()", StringComparison.Ordinal));
        Assert.Contains("clb-ant-detail", hosts, StringComparison.Ordinal);
        Assert.Contains("agent-detail", hosts, StringComparison.Ordinal);

        // An absent host is ordinary — a collapsed widget, a page that is not the colony — so the
        // dispatch that reaches the editor inside the panel may not bind to a specific element.
        // v0.3.8.125 bound it to `#agent-detail` directly, which threw at load once that id moved.
        Assert.DoesNotContain("getElementById('agent-detail').addEventListener", js, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE PANEL RENDERS THE ANT ITSELF rather than depending on another listener having run.
    ///
    /// Two files subscribe to the renderer's `resident` event. In `.125` only colony-host.js
    /// rendered the inspector, and colony-home.js — the file that owns the panel — assumed it had.
    /// When the lookup there did not resolve, the panel showed nothing at all: no inspector, no
    /// message, no error.
    /// </summary>
    [Fact]
    public void ThePanelThatNamesTheAnt_RendersIt()
    {
        var home = Ui("colony-home.js");
        var detail = home[home.IndexOf("function showAntDetail(res)", StringComparison.Ordinal)..];
        detail = detail[..detail.IndexOf("\n  }", StringComparison.Ordinal)];

        Assert.Contains("showInspector(n)", detail, StringComparison.Ordinal);
        Assert.Contains("nodes.find(", detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE WIDGET STILL POINTS AT IT. The dashboard's Ant Inspector names `agent-detail` as its
    /// body; moving the element without checking this is how a widget comes up permanently blank.
    /// </summary>
    [Fact]
    public void TheDashboardWidget_StillHasAnElementToAdopt()
    {
        Assert.Contains("body:'agent-detail'", Ui("app.js").Replace(" ", ""), StringComparison.Ordinal);
        // ...and that element is in the markup for it to find. Naming a body id that no element
        // carries gives a permanently empty widget, which is what the widget had after v0.3.8.125
        // moved its element into the colony panel.
        Assert.Contains("id=\"agent-detail\"", Ui("index.html"), StringComparison.Ordinal);
    }

    /// <summary>
    /// THE INSPECTOR'S CONTENT SURVIVED THE MOVE — every section the operator asked to keep.
    ///
    /// Checked against `showInspector`'s own body rather than the file, so a section deleted from
    /// the panel and left defined somewhere else does not pass.
    /// </summary>
    [Fact]
    public void TheMergedPanel_StillCarriesPurposePermissionsToolsAndLoad()
    {
        var js = Ui("app.js");
        var body = SourceText.MemberBody(js, js.IndexOf("function showInspector(n)", StringComparison.Ordinal));

        // v0.3.8.127: "Configure" became "Model". The header outlived what it labelled — the block
        // under it is a read-only statement of which provider and model this ant runs on, and a
        // heading promising a control that was removed is the same defect one layer up.
        foreach (var section in new[] { "Purpose", "Permissions", "Tools", "Live Task Load", "Model" })
            Assert.Contains(section, body, StringComparison.Ordinal);

        // The model block is still reached from here — read-only since v0.3.8.127, but present:
        // an ant whose panel stopped saying which model it runs on would be a quieter regression
        // than one that stopped saying its permissions.
        Assert.Contains("inspectorEditorHtml(n)", body, StringComparison.Ordinal);
        Assert.Contains("Running", body, StringComparison.Ordinal);
        Assert.Contains("Completed", body, StringComparison.Ordinal);
        Assert.Contains("Failed", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE SECOND EVENT LOG IS GONE. The live panel carried a "recent activity" disclosure that
    /// fetched twelve event rows per ant — a second request, for a feed the Events page already is,
    /// under a panel that now shows the ant's live task load. Removed at the operator's word, with
    /// its loader, rather than hidden.
    /// </summary>
    [Fact]
    public void TheRecentActivityFeed_IsGone_WithItsLoader()
    {
        Assert.DoesNotContain("clb-ant-recent", Ui("colony-home.js"));
        Assert.DoesNotContain("recent activity", Ui("colony-home.js"));
        Assert.DoesNotContain("onAntRecentToggle", Ui("app.js"));
        Assert.DoesNotContain("ac-recent", Ui("index.html"));
    }

    /// <summary>
    /// A MOUND'S ANTS ARE CUSTOMIZABLE, AND THE PANEL SAYS WHY THEY HAVE NOTHING ELSE.
    ///
    /// A micromound's residents come from the mound roster, not from `/colony/registry`, so
    /// `colony-host.js` cannot resolve them against `nodes` and `showInspector` is never called for
    /// them. Before this release that left the panel showing a name, a colour and an empty
    /// inspector below — which reads as a broken panel rather than as an ant that has no registry
    /// role. It now says which, and the name and colour above it are the renderer's and work for
    /// every resident alike.
    /// </summary>
    [Fact]
    public void AMoundsAnts_AreStillStyleable_AndTheEmptyInspectorExplainsItself()
    {
        var home = Ui("colony-home.js");

        Assert.Contains("function showAntDetail(res)", home, StringComparison.Ordinal);
        Assert.Contains("belongs to a mound", home, StringComparison.Ordinal);

        // The editor is shown for EVERY resident — it is the renderer's style store, not the
        // registry's, which is exactly why it works for an ant the registry has never heard of.
        Assert.Contains("if (edit) edit.style.display = '';", home, StringComparison.Ordinal);
        Assert.Contains("live.setAntStyle(antId,", home, StringComparison.Ordinal);
    }
}
