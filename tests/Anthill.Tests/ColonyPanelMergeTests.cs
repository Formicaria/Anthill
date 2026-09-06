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
/// THE MERGE IS A MOVE, NOT A COPY, and that distinction is what these guards are for. `#agent-detail`
/// is one element with two hosts — the colony page and the dashboard's Ant Inspector widget, which
/// re-parents it exactly as the colony canvas area is re-parented. Rebuilding its markup inside
/// colony-home.js would have produced a second inspector that drifts from the first and a dashboard
/// widget pointing at an element that no longer exists. So the element moved, and `showInspector`
/// is untouched.
/// </summary>
public class ColonyPanelMergeTests
{
    private static string Ui(string file) =>
        File.ReadAllText(Path.Combine(SourceText.RepoRoot(), "src", "Anthill.UI", file));

    /// <summary>
    /// THE INSPECTOR ELEMENT LIVES IN THE LIVE PANEL, AND THERE IS EXACTLY ONE OF IT.
    ///
    /// The count is the load-bearing half. A second `#agent-detail` is not a visible bug — both
    /// would render, `getElementById` would silently pick the first, and the operator would watch
    /// one of the two panels never update.
    /// </summary>
    [Fact]
    public void TheAntInspector_IsOneElement_InsideTheLivePanel()
    {
        var html = Ui("index.html");

        var count = System.Text.RegularExpressions.Regex.Matches(html, "id=\"agent-detail\"").Count;
        Assert.True(count == 1,
            $"expected exactly one #agent-detail element, found {count}. The dashboard's Ant "
          + "Inspector widget re-parents this node rather than building its own, so a second one "
          + "means getElementById silently picks a winner and the other panel goes stale.");

        // It sits inside the live panel — after the counters it now follows, and before that
        // panel closes. Asserted by ORDER rather than by nesting, because the markup is flat text
        // here and an assertion about nesting would be a lie about what was checked.
        var stats = html.IndexOf("id=\"clb-ant-stats\"", StringComparison.Ordinal);
        var detail = html.IndexOf("id=\"agent-detail\"", StringComparison.Ordinal);
        var record = html.IndexOf("id=\"clb-record\"", StringComparison.Ordinal);
        Assert.True(record >= 0 && stats > record, "the counters are not inside the live record panel");
        Assert.True(detail > stats, "the inspector must follow the counters inside the live panel");

        // And the card that used to hold it in the right sidebar is gone, rather than left empty.
        Assert.DoesNotContain("id=\"card-inspector\"", html);
    }

    /// <summary>
    /// THE WIDGET STILL POINTS AT IT. The dashboard's Ant Inspector names `agent-detail` as its
    /// body; moving the element without checking this is how a widget comes up permanently blank.
    /// </summary>
    [Fact]
    public void TheDashboardWidget_StillNamesTheElementItReParents()
    {
        Assert.Contains("body:'agent-detail'", Ui("app.js").Replace(" ", ""), StringComparison.Ordinal);
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

        foreach (var section in new[] { "Purpose", "Permissions", "Tools", "Live Task Load", "Configure" })
            Assert.Contains(section, body, StringComparison.Ordinal);

        // The customization is still editable, not merely displayed.
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
