using System.Text.RegularExpressions;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE COLONY LIVE REGRESSION GUARDS — v0.3.8.115 (§17).
///
/// Every rule here is one the PREVIOUS Colony Live broke, or one the reference package invited.
/// Each is written as the narrowest check that would have failed on the defect, and each carries a
/// VACUITY FLOOR — an assertion that the scan can see what it claims to be looking for — because
/// `docs/GUARDS.md`'s standing lesson is that a guard which cannot express success is not a guard,
/// it is a deadline. Four times now (`.74`, `.79`, `.113`, `.114`) a guard here passed by reading
/// nothing.
///
/// These are SOURCE SCANS, which the guard hierarchy puts last for good reason: they cannot observe
/// behaviour, only text. They are used here because the subject is a browser console with no test
/// host in this suite, and the alternative is no guard at all. Where a rule could be checked at a
/// stronger level it is — the record-creation rule below reads the C# that decides it, not the
/// JavaScript that displays it.
/// </summary>
public class ColonyLiveGuardTests
{
    private static string UiDir() => Path.Combine(SourceText.RepoRoot(), "src", "Anthill.UI");

    /// <summary>
    /// The feature's assets, excluding the vendored bundle (third-party, and not ours to constrain).
    /// Named explicitly rather than enumerated: these guards make claims about THIS feature, and a
    /// directory sweep would silently start policing whatever lands beside it.
    /// </summary>
    private static readonly string[] ColonyAssets =
    [
        "colony-topology.js", "colony-renderer.js", "colony-live.js", "colony-host.js", "colony-hud.js"
    ];

    private static string Raw(string asset) => File.ReadAllText(Path.Combine(UiDir(), asset));

    /// <summary>
    /// Comments and string bodies blanked, so a guard reads CODE.
    ///
    /// Not `SourceText.CodeOnly`: that treats `'` as a C# char delimiter, and JavaScript's most
    /// common string literal is single-quoted. Reusing it here would mis-tokenise almost every line
    /// of these files. The rule this exists to enforce is a real one — `colony-topology.js`'s header
    /// contains the sentence "Math.random() appears nowhere in this file", and a naive Contains()
    /// scan would fail on the comment that documents the guarantee.
    /// </summary>
    private static string Code(string asset)
    {
        var src = Raw(asset).Replace("\r\n", "\n");
        var sb = new System.Text.StringBuilder(src.Length);
        bool line = false, block = false;
        char quote = '\0';

        for (var i = 0; i < src.Length; i++)
        {
            var c = src[i];
            var next = i + 1 < src.Length ? src[i + 1] : '\0';

            if (line) { if (c == '\n') { line = false; sb.Append(c); } else sb.Append(' '); continue; }
            if (block) { if (c == '*' && next == '/') { block = false; sb.Append("  "); i++; } else sb.Append(c == '\n' ? '\n' : ' '); continue; }
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == '\\' && next != '\0') { sb.Append(next); i++; }
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c == '/' && next == '/') { line = true; sb.Append("  "); i++; continue; }
            if (c == '/' && next == '*') { block = true; sb.Append("  "); i++; continue; }
            if (c is '"' or '\'' or '`') { quote = c; sb.Append(c); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Every guard below depends on these files being readable and substantial.</summary>
    [Fact]
    public void TheGuardsInThisFile_AreReadingRealAssets()
    {
        foreach (var asset in ColonyAssets)
        {
            var code = Code(asset);
            Assert.True(code.Length > 1_000,
                $"{asset} produced {code.Length} characters of code after comment stripping. Every "
              + "guard in this file would pass vacuously on an input this small.");
        }

        // And the stripper genuinely removes comments rather than returning the input: the topology
        // header states the Math.random guarantee in prose, and that sentence must not survive.
        Assert.Contains("Math.random", Raw("colony-topology.js"));
        Assert.DoesNotContain("Math.random", Code("colony-topology.js"));
    }

    // ---------------------------------------------------------------------------------------------
    // §17.1 — placement is deterministic
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// `.111` positioned ants with `t: 0.2 + ants.length * .25` and drifted them at a fixed speed:
    /// a travel animation presented as progress. Nothing in this feature may place, move or size a
    /// thing by chance — the same colony state must draw the same picture twice.
    /// </summary>
    [Fact]
    public void NoColonyAsset_PlacesAnythingAtRandom()
    {
        foreach (var asset in ColonyAssets)
            Assert.False(Code(asset).Contains("Math.random", StringComparison.Ordinal),
                $"{asset} calls Math.random. Record and ant placement is derived from a hash of the "
              + "record id so a re-render is stable; a random position reshuffles the colony under "
              + "the operator's cursor and makes \"the third particle from the left\" meaningless.");

        // Vacuity floor: the deterministic replacement is actually present.
        var topo = Code("colony-topology.js");
        Assert.Contains("function hash32", topo);
        Assert.Contains("function placement", topo);
    }

    // ---------------------------------------------------------------------------------------------
    // §17.2 — no client-side clock, no looping playback
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A repeating timer is how a mission clock, a looping traffic animation and a second poll all
    /// get built. None of the three is allowed: mission progress comes from task status, transitions
    /// play once per recorded event id, and the live picture rides the stream app.js already holds.
    /// </summary>
    [Fact]
    public void NoColonyAsset_RunsARepeatingTimer()
    {
        foreach (var asset in ColonyAssets)
            Assert.False(Code(asset).Contains("setInterval", StringComparison.Ordinal),
                $"{asset} starts a repeating timer. In this feature that is always one of three "
              + "forbidden things: a client mission clock, a looping traffic animation, or a second "
              + "poll of something the console already has.");

        // Vacuity floor: the scan detects setInterval where it legitimately exists.
        Assert.Contains("setInterval", File.ReadAllText(Path.Combine(UiDir(), "app.js")));
    }

    /// <summary>
    /// A recorded transition plays ONCE, keyed by the event id, and the flight is disposed when it
    /// lands. The `.111` version restarted every ant every frame, which reads as continuous traffic
    /// in a colony that is doing nothing.
    /// </summary>
    [Fact]
    public void RecordedTransitions_PlayOncePerEventId()
    {
        var r = Code("colony-renderer.js");
        Assert.Contains("playedTransitions[tr.id]", r);
        Assert.Contains("playedTransitions[tr.id] = true", r);
        // And a historical frame never burns those ids — see §14.
        Assert.Contains("if (sc.meta && sc.meta.history) return;", r);
    }

    // ---------------------------------------------------------------------------------------------
    // §17.3 — one place fetches, and it is not the model or the view
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The model must not be able to go and get more data, and the view must not be able to act on
    /// the colony behind the host's back. Both would make the "no second fetch" boundary unenforceable
    /// by inspection — you would have to read four files to know what the feature talks to.
    /// </summary>
    [Fact]
    public void OnlyTheHost_TalksToTheApi()
    {
        foreach (var asset in ColonyAssets.Where(a => a != "colony-host.js"))
        {
            var code = Code(asset);
            foreach (var io in new[] { "fetch(", "XMLHttpRequest", "EventSource", "navigator.sendBeacon" })
                Assert.False(code.Contains(io, StringComparison.Ordinal),
                    $"{asset} performs I/O ({io}). colony-host.js is the only file in this feature "
                  + "that may reach the network.");
        }

        // Vacuity floor: the host really is the one doing it.
        Assert.Contains("api('/colony/live/snapshot')", Code("colony-host.js"));
    }

    // ---------------------------------------------------------------------------------------------
    // §17.4 — sector membership has exactly one source
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// THE `.111` DEFECT THIS RELEASE EXISTS FOR. The console kept a hand-written role→sector map
    /// and resolved a miss with `sectorOfAnt(ant) || 'queen'`, so every role added after the map was
    /// last edited — and every plugin-contributed role — was silently filed under the Queen.
    ///
    /// The registry owns membership. An unknown role is UNASSIGNED and visibly so.
    /// </summary>
    [Fact]
    public void RoleToSectorMembership_ComesFromTheServerAndFallsToUnassigned()
    {
        var topo = Code("colony-topology.js");

        Assert.DoesNotContain("SECTOR_OF", topo);
        Assert.DoesNotContain("|| 'queen'", topo);
        Assert.DoesNotContain("|| \"queen\"", topo);

        // The membership table is built from the snapshot, and the miss resolves to unassigned.
        Assert.Contains("st.roleSector", topo);
        Assert.Contains("st.unassignedId", topo);

        // The server side of the same rule: one map, beside the registry it maps.
        var projection = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "ColonyLive", "ColonyLiveProjection.cs")));
        Assert.Contains("ByColony", projection);
        Assert.Contains("Unassigned", projection);
    }

    /// <summary>
    /// The records endpoint applies the same rule. An event whose ant the colony does not recognise
    /// is unassigned; it is never attributed to the Queen because she is the convenient default.
    /// </summary>
    [Fact]
    public void TheRecordsEndpoint_FilesAnUnknownAntAsUnassigned()
    {
        var api = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Api", "ColonyLive", "ApiHost.ColonyLive.cs")));

        Assert.Contains("sectorOfRole.GetValueOrDefault(ant, ColonySectors.Unassigned)", api);
        Assert.DoesNotContain("ColonySectors.Queen)", api);
    }

    // ---------------------------------------------------------------------------------------------
    // §17.5 — what counts as a stored record is decided once, on the server
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// `.111` declared a `RECORD_EVENTS` regex for exactly this and never called it, so every event
    /// became a "record": a task starting and a memory being written grew the same chamber by the
    /// same amount. The rule now lives in one C# method and travels on the wire, and BOTH stream
    /// serialisers must carry it or the replay path and the live path would disagree.
    /// </summary>
    [Fact]
    public void WhetherAnEventCreatedARecord_IsDecidedInExactlyOnePlace()
    {
        var topo = Code("colony-topology.js");
        Assert.Contains("ev.event_type_creates_record", topo);
        Assert.DoesNotContain("RECORD_EVENTS", topo);

        var stream = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Api", "ApiHost.EventStream.cs")));

        // Both overloads — the live path and the replay path.
        var carried = Regex.Matches(stream, @"""event_type_creates_record""").Count;
        Assert.True(carried >= 2,
            $"the stream carries `event_type_creates_record` in {carried} place(s). Both Serialize "
          + "overloads must set it, or a replayed event and a live one disagree about whether the "
          + "colony stored anything.");

        Assert.Contains("CreatesDurableRecord", stream);
    }

    // ---------------------------------------------------------------------------------------------
    // §17.6 — approvals are exact or explicitly unresolved, and decisions really leave the browser
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// `.111` set `pausedForApproval` for ANY approval and parked the ant on whatever route segment
    /// was last drawn. An approval belongs to one task; attaching it to the nearest thing is a guess
    /// rendered as a fact.
    /// </summary>
    [Fact]
    public void AnApproval_IsResolvedToItsOwnTaskOrDeclaredUnresolved()
    {
        var topo = Code("colony-topology.js");

        Assert.DoesNotContain("pausedForApproval", topo);
        Assert.Contains("resolved:", topo);
        // Resolution requires BOTH a role the colony can place and the task it belongs to.
        Assert.Contains("known && taskId", topo);
    }

    /// <summary>
    /// §11. The decision must reach the colony through the console's existing authenticated path.
    /// A Colony Live that marked an approval "approved" in its own state and never called the server
    /// is the specific failure this guards — the operator would believe they had answered.
    /// </summary>
    [Fact]
    public void ApprovalDecisions_GoThroughTheConsolesRealAuthenticatedPath()
    {
        var hud = Code("colony-hud.js");

        Assert.Contains("window.doApproval", hud);

        // And NOT through a second implementation of its own.
        foreach (var forbidden in new[] { "'/approve/", "\"/approve/", "'/reject/", "\"/reject/" })
            Assert.False(hud.Contains(forbidden, StringComparison.Ordinal),
                "colony-hud.js builds its own approval route. The decision endpoints belong to "
              + "app.js's doApproval, which already carries the bearer token and already refreshes "
              + "the queue; a second caller is a second place for the rule to drift.");

        // Vacuity floor: the function it delegates to exists, and still hits the real routes.
        var app = File.ReadAllText(Path.Combine(UiDir(), "app.js"));
        Assert.Contains("async function doApproval(", app);
        Assert.Contains("/approve/", app);
    }

    // ---------------------------------------------------------------------------------------------
    // §17.7 — nothing about a Micromound is invented
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The mound node exists only when the fleet listing returned one, and every field shown is one
    /// the record literally carries. In particular the client does not recompute online/offline:
    /// `MicromoundWidgets.StatusOf` decides that from options the browser cannot see, and a second
    /// opinion here would disagree the moment the grace configuration changed.
    /// </summary>
    [Fact]
    public void TheMoundNode_IsOnlyWhatTheFleetListingReturned()
    {
        var topo = Code("colony-topology.js");

        // No fleet, no mound. Not an empty placeholder, not a demo device.
        Assert.Contains("if (!fleet) return null;", topo);

        // The wire tier value is carried, never invented, and never turned into a status verdict.
        Assert.DoesNotContain("'edge_queen'", topo);
        Assert.DoesNotContain("\"edge_queen\"", topo);
        foreach (var verdict in new[] { "'online'", "'offline'", "'quiesced'" })
            Assert.False(topo.Contains(verdict, StringComparison.Ordinal),
                $"colony-topology.js decides a mound status ({verdict}). That verdict is the "
              + "server's — MicromoundWidgets.StatusOf reads the beat interval and the configured "
              + "missed-beat grace, neither of which the browser has.");

        // The operator-facing name for the wire value lives in the view, with the id kept visible.
        var hud = Code("colony-hud.js");
        Assert.Contains("edge_queen: 'Mound Major'", hud);
        Assert.Contains("row('tier', m.tier)", hud);
    }

    // ---------------------------------------------------------------------------------------------
    // §17.8 — playback reconstructs; it does not re-enact
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// §14. A historical frame is derived and read-only. If `historyAt` wrote to the model, scrubbing
    /// backwards would destroy the live state it was derived from — and the operator would return to
    /// LIVE to find a colony that had quietly shrunk to whatever they last looked at.
    /// </summary>
    [Fact]
    public void AHistoricalFrame_IsDerivedAndMutatesNothing()
    {
        var topo = Code("colony-topology.js");

        var at = topo.IndexOf("function historyAt(", StringComparison.Ordinal);
        Assert.True(at > 0, "colony-topology.js no longer defines historyAt; §14 has no model.");

        var end = topo.IndexOf("\n    return {", at, StringComparison.Ordinal);
        Assert.True(end > at, "could not bound historyAt's body; this guard would read the whole file.");
        var body = topo[at..end];

        // The floor: the body really is the function (it filters both timestamped collections).
        Assert.Contains("st.records.filter", body);
        Assert.Contains("st.transitions.filter", body);

        // No assignment to model state anywhere inside it.
        var writes = Regex.Matches(body, @"\bst\.[A-Za-z_$][\w$]*\s*(?:=[^=]|\.push\(|\.shift\(|\.pop\()");
        Assert.True(writes.Count == 0,
            "historyAt writes to the model: " + string.Join(", ", writes.Select(m => m.Value.Trim())));
    }

    /// <summary>
    /// And it must not present live-only facts as historical ones. Task status, the approvals queue
    /// and the fleet listing are all current-value-only in this model, so a past frame shows none of
    /// them rather than showing today's.
    /// </summary>
    [Fact]
    public void AHistoricalFrame_ShowsNoLiveOnlyFact()
    {
        var topo = Code("colony-topology.js");
        var at = topo.IndexOf("function historyAt(", StringComparison.Ordinal);
        var end = topo.IndexOf("\n    return {", at, StringComparison.Ordinal);
        var body = topo[at..end];

        Assert.Contains("runningTasks: []", body);
        Assert.Contains("s.approvals = []", body);
        Assert.Contains("s.mound = null", body);
        // And it says out loud that the chambers themselves are today's.
        Assert.Contains("sectorsAreCurrent: true", body);
    }

    // ---------------------------------------------------------------------------------------------
    // §17.9 — the reference package's scaffolding did not come along
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The visual reference shipped as a self-contained prototype with its own support script and
    /// `.dc.html` wrappers, plus demo data to make an empty page look alive. The look was ported;
    /// none of that was, and a later "just to see it working" reintroduction is what this catches.
    /// </summary>
    [Fact]
    public void NoConsoleAsset_CarriesTheReferencePackagesScaffolding()
    {
        foreach (var asset in ColonyAssets)
        {
            var raw = Raw(asset);
            foreach (var token in new[] { "support.js", ".dc.html", "DEMO_", "SAMPLE_MISSION", "FAKE_" })
                Assert.False(raw.Contains(token, StringComparison.OrdinalIgnoreCase),
                    $"{asset} references '{token}' — reference-package scaffolding. The prototype's "
                  + "demo data existed to make a page with no colony behind it look busy; this view "
                  + "shows an empty colony as empty.");
        }
    }
}
