using System.Text.RegularExpressions;
using Anthill.Core.Agents;
using Anthill.Core.ColonyLive;
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

    /// <summary>
    /// EVERY BUILT-IN ROLE AND WORKER HAS A CHAMBER. v0.3.8.115.1.
    ///
    /// `unassigned` exists for a plugin-contributed role this colony has never heard of, and for
    /// that it is right — better a visible unknown than a silent default. What it must NEVER hold
    /// is a role from the shipped roster, because that means `ByColony` has fallen behind the
    /// registry, which is the same drift `.111`'s hand-written `SECTOR_OF` died of.
    ///
    /// It had already happened. The registry declares seventeen distinct `Colony` values and the
    /// map covered fifteen: `constraint` (Command / Safety) and `scribe` (Communication / Docs)
    /// resolved to `unassigned`, in a release whose entire premise was that membership comes from
    /// the registry. Nothing caught it because every guard checked the SHAPE of the mapping and
    /// none checked its COVERAGE.
    ///
    /// Workers are asserted too, and that is the half that matters most in practice: an event's
    /// `ant_name` is whichever unit actually ran, and most executable units are workers rather
    /// than roles, so an unindexed worker sends every record it authored to `unassigned`.
    ///
    /// Typed registry, not a source scan — this reads the real roster, so adding a role with a new
    /// colony fails here rather than quietly landing in the unknown bucket.
    /// </summary>
    [Fact]
    public void EveryBuiltInRoleAndWorker_BelongsToARealChamber()
    {
        var sectors = ColonyLiveProjection.Sectors();

        var placed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sectors)
            foreach (var resident in s.Residents)
            {
                placed[resident.RoleId] = s.SectorId;
                foreach (var w in resident.Workers) placed[w.WorkerId] = s.SectorId;
            }

        // Vacuity floor: a real roster, not an empty projection.
        Assert.True(AntRegistry.Roles.Count >= 20,
            $"the registry reports {AntRegistry.Roles.Count} roles; this guard would prove nothing.");
        Assert.True(placed.Count >= 40,
            $"only {placed.Count} roles and workers were placed at all.");

        var stranded = AntRegistry.Roles
            .Where(r => !placed.TryGetValue(r.RoleId, out var sec) || sec == ColonySectors.Unassigned)
            .Select(r => $"{r.RoleId} (colony \"{r.Colony}\")")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(stranded.Count == 0,
            "These shipped roles have no chamber, so Colony Live files them — and every record they "
          + "author — under `unassigned`. `ColonySectors.ByColony` has fallen behind the registry: "
          + string.Join(", ", stranded));

        var strandedWorkers = AntRegistry.Roles
            .SelectMany(r => r.Workers.Select(w => (Role: r, Worker: w)))
            .Where(x => !placed.TryGetValue(x.Worker.WorkerId, out var sec) || sec == ColonySectors.Unassigned)
            .Select(x => $"{x.Worker.WorkerId} (under {x.Role.RoleId})")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(strandedWorkers.Count == 0,
            "These shipped workers resolve to no chamber. An event names whichever unit ran, so "
          + "every record they author lands in `unassigned`: " + string.Join(", ", strandedWorkers));
    }

    /// <summary>
    /// A WORKER TRAVELS WITH BOTH OF ITS NAMES, AND WITH ITS PARENT. Until `.116` the projection
    /// carried `WorkerId` alone, so Colony Live labelled an ant "constraint.scope_guard" while every
    /// other page in this console — and the roster editor that names it — called the same ant
    /// "ScopeGuard". One ant, two names, in one product.
    ///
    /// `ParentRoleId` travels for the same reason: the registry owns the fact that scope_guard
    /// belongs to constraint. A view that wanted to draw that relationship and split the id on a dot
    /// instead would be re-deriving a fact it was handed, and would be wrong the first time a worker
    /// id contained one.
    /// </summary>
    [Fact]
    public void EveryProjectedWorker_CarriesItsDisplayNameAndItsParent()
    {
        var sectors = ColonyLiveProjection.Sectors();
        var workers = sectors.SelectMany(s => s.Residents).SelectMany(r => r.Workers).ToList();

        Assert.True(workers.Count >= 30,
            $"the projection emitted {workers.Count} workers; this guard would prove nothing.");

        var byId = AntRegistry.Roles.SelectMany(r => r.Workers).ToDictionary(w => w.WorkerId, StringComparer.Ordinal);

        var wrong = workers
            .Where(w => !byId.TryGetValue(w.WorkerId, out var def)
                     || def.DisplayName != w.DisplayName
                     || def.ParentRoleId != w.ParentRoleId)
            .Select(w => w.WorkerId)
            .ToList();
        Assert.True(wrong.Count == 0,
            "These projected workers disagree with the registry about their own name or parent: "
          + string.Join(", ", wrong));

        // And the display name is a NAME, not the id again — the defect this replaced.
        var unnamed = workers.Where(w => w.DisplayName == w.WorkerId || w.DisplayName.Contains('.'))
                             .Select(w => w.WorkerId).ToList();
        Assert.True(unnamed.Count == 0,
            "These workers project a dotted id where a display name belongs, which is exactly what "
          + "Colony Live was printing under every worker orb: " + string.Join(", ", unnamed));

        // Vacuity floor: the two names really are different, so the check above can fail.
        Assert.Contains(workers, w => w.WorkerId == "constraint.scope_guard" && w.DisplayName == "ScopeGuard");
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
    // §16 — a renderer that fails takes itself down, not the view
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// THE FALLBACK MUST COVER MOUNTING, NOT ONLY CONSTRUCTION.
    ///
    /// `.115` shipped with `ColonyLive.create()` guarding construction and nothing guarding
    /// `mount()` — which is where three.js is actually resolved, the `WebGLRenderer` constructed
    /// and the canvas attached. `available()` proves `window.THREE` exists and that *a* WebGL
    /// context can be made; it does not prove this renderer mounts.
    ///
    /// The failure mode was total rather than graceful: the exception escaped `enable()` and
    /// `toggle()`, so `classic.style.display` was never restored, and the already-attached WebGL
    /// root — full-bleed, opaque, `#04060b` — sat as a black rectangle over a classic canvas that
    /// had been hidden and never brought back. The view looked dead and the fallback built for
    /// exactly this case never ran.
    ///
    /// Asserted on the wiring file because that is where the decision lives, and asserted with its
    /// three parts named: the attempt is guarded, the partial mount is torn down, and the classic
    /// projection is what replaces it.
    /// </summary>
    [Fact]
    public void ARendererThatFailsToMount_FallsBackInsteadOfBlankingTheView()
    {
        var host = Code("colony-host.js");

        Assert.Contains("try {", host);
        Assert.Contains("live.mount(area);", host);

        // The teardown, without which the failed mount's opaque root stays on screen.
        Assert.Contains("live.destroy();", host);

        // And what replaces it is the projection that cannot fail this way.
        Assert.Contains("ColonyLive.createClassic()", host);

        // The operator is told WHY, rather than being handed a silent downgrade.
        Assert.Contains("failed to mount", Raw("colony-host.js"));

        // Vacuity floor: `createClassic` is a real export, so this is checking a wiring that
        // exists rather than matching a string that happens to be present.
        Assert.Contains("createClassic: create", Code("colony-live.js"));
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
    // ---------------------------------------------------------------------------------------------
    // §18 — the Claude Design port. v0.3.8.116.
    //
    // The reference renderer was ported literally: its world scale, camera, shaders, texture stop
    // tables, conduit sampling, pixel-sized crew orbs, screen-space picking and quality ladder. Four
    // things in it were NOT ported, and every one of them is a picture that is true of the
    // reference's invented sample data and false of this colony. The guards below hold both halves:
    // the ported numbers must stay the reference's, and the four exclusions must stay excluded.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// THE WORLD IS THE REFERENCE'S WORLD. `.115` scaled the reference's seats by fourteen and set
    /// the camera to a 52° field at 900 units — and then had to invent every other constant to match,
    /// which is why chambers sat four to six radii apart on a black field. The reference's proportions
    /// are load-bearing: at ±16.5 with radii 3.1–7.7 the chambers sit two to three radii apart, which
    /// is the ratio that reads as a crystal with galleries rather than a constellation.
    /// </summary>
    [Fact]
    public void TheWorldScale_AndCamera_AreTheReferences()
    {
        var r = Code("colony-renderer.js");

        Assert.Contains("new TH.PerspectiveCamera(42, 1, 0.5, 400)", r);
        Assert.Contains("dist: 96", r);
        Assert.Contains("dist: [2.2, 130]", r);
        Assert.Contains("pos: [-16.5, 0, 16.5]", r);
        Assert.Contains("pos: [16.5, 0, 16.5]", r);
        Assert.Contains("r: 7.7", r);
        Assert.Contains("r: 3.1", r);

        // And the `.115` blow-up is gone rather than merely shadowed by the new numbers.
        foreach (var stale in new[] { "PerspectiveCamera(52", "dist: 900", "231", "dist: [90, 2400]" })
            Assert.False(r.Contains(stale, StringComparison.Ordinal),
                $"colony-renderer.js still carries the `.115` world scale ({stale}). Two scales in one "
              + "file is two spatial grammars, and the constants derived from each disagree.");
    }

    /// <summary>
    /// A CHAMBER'S MASS IS ITS RECORD COUNT AND NOTHING ELSE. `.115` drew 260×mass "structural"
    /// grains per chamber so a young colony would not look empty — a cloud that said the same thing
    /// whether the chamber held sixty records or none, which is the one claim this view exists to
    /// make and the one it was quietly refusing to make.
    ///
    /// What carries a chamber's PRESENCE instead is the core light, which is light and not data: an
    /// empty chamber is small and lit, never absent.
    /// </summary>
    [Fact]
    public void AChambersParticles_AreItsRecordsAndNothingElse()
    {
        var r = Code("colony-renderer.js");

        Assert.Contains("var total = recs.length;", r);
        foreach (var filler in new[] { "bodyCount", "260 * lk.mass", "sizeFill", "brightFill" })
            Assert.False(r.Contains(filler, StringComparison.Ordinal),
                $"colony-renderer.js builds chamber geometry from '{filler}'. A chamber's particle "
              + "count is its persisted record count; a filler term makes an empty chamber and a full "
              + "one draw the same picture.");

        // Vacuity floor: the light that carries an empty chamber is really there.
        Assert.Contains("nucleus.scale.setScalar(lk.r * 4.2)", r);
    }

    /// <summary>
    /// EXACTLY ONE HALO PER CHAMBER. The reference constructs a second, wider `glow` sprite at 2.8r
    /// and never adds it to the group — it survives only as a colour handle for its restyle path.
    /// Read quickly that looks like an oversight, and adding it produces the failure the design
    /// handoff names by symptom: "nebula-like coloured wash filling a quadrant". Two overlapping
    /// halos, nine chambers, additive over black, and the centre of the frame turns to fog with the
    /// record grains lost inside it. That port defect shipped in this file for one build and was
    /// caught by rendering it, not by reading it.
    /// </summary>
    [Fact]
    public void AChamberHasOneHaloSprite_NotTwo()
    {
        var r = Code("colony-renderer.js");

        var sprites = Regex.Matches(r, @"new TH\.Sprite\(new TH\.SpriteMaterial\(\{\s*\n?\s*map: TEX\.halo")
                           .Count;
        Assert.True(sprites == 1,
            $"colony-renderer.js builds {sprites} halo sprites per chamber. The core light is one "
          + "sprite at 4.2r in the chamber's deep hue; a second wide halo turns nine chambers into "
          + "one coloured wash and swallows the record grains.");

        Assert.False(r.Contains("glow.scale.setScalar", StringComparison.Ordinal),
            "colony-renderer.js still scales a `glow` sprite. The reference never adds that object "
          + "to the scene; this view does not build it at all.");

        // Vacuity floor: the scan can see the halo it is counting.
        Assert.Contains("map: TEX.halo", r);
    }

    /// <summary>
    /// THE POINT SHADERS ARE THE DESIGN'S, INCLUDING THE HARD SPRITE MASK. `.115` used a
    /// `PointsMaterial` with a soft alpha map, which is why twelve records read as one smudge: a
    /// blended sprite edge merges neighbouring grains into bloom. The alpha-0.5 discard is what makes
    /// a record look like a distinct thing.
    /// </summary>
    [Fact]
    public void TheParticleShaders_AreTheDesignsAndDiscardOnTheSpriteMask()
    {
        var r = Code("colony-renderer.js");

        Assert.Contains("attribute vec3 acolor; attribute float size; attribute float alpha; attribute vec3 aOrg;", r);
        Assert.Contains("uniform float uScale; uniform float uAlpha; uniform float uRec; uniform float uOrg;", r);
        Assert.Contains("gl_PointSize = clamp(size * uRec * uScale * (300.0 / max(1.0, -mv.z)), 2.0, 12.0);", r);
        Assert.Contains("if(texture2D(uMap, gl_PointCoord).a < 0.5) discard;", r);
        Assert.Contains("new TH.ShaderMaterial({", r);

        // The conduit pair, and the wave term the recorded transitions ride.
        Assert.Contains("uniform float uHead; uniform float uActive; uniform float uRest; uniform float uScale; uniform float uSharp;", r);
        Assert.Contains("float wave = uActive * exp(-d * d * uSharp);", r);

        // The `.115` material is gone, not merely unused.
        Assert.False(r.Contains("new TH.PointsMaterial(", StringComparison.Ordinal),
            "colony-renderer.js still constructs a PointsMaterial. The design's grains are a "
          + "ShaderMaterial with a hard sprite mask; a PointsMaterial cannot express the discard.");
    }

    /// <summary>
    /// THE TEXTURE STOP TABLES ARE THE REFERENCE'S. These four gradients are the whole reason the
    /// view is legible: a solid disc with a hairline edge for grains, a wide soft glow for orbs, and
    /// a power-2.2 halo with NO defined rim for the core light — a halo that ends on a circle turns a
    /// cloud of facts into a bubble.
    /// </summary>
    [Fact]
    public void TheGeneratedTextures_UseTheReferencesStops()
    {
        var r = Code("colony-renderer.js");

        Assert.Contains("Math.pow(1 - t, 2.2) * 0.5", r);          // halo falloff
        Assert.Contains("[0.9, 'rgba(255,255,255,1)']", r);        // dot: hairline edge
        Assert.Contains("[0.92, 'rgba(255,255,255,1)']", r);       // conduit grain
        Assert.Contains("[0.46, 'rgba(255,255,255,.34)']", r);     // glow shoulder
        Assert.Contains("function antTex", r);
        // `lockTex` is deliberately absent — see TheAuthorityConduit_CarriesNoLockBadge.

        // No external asset: the console's img-src stays 'self'.
        foreach (var fetched in new[] { "TextureLoader", "http://", "https://", ".png", ".jpg" })
            Assert.False(r.Contains(fetched, StringComparison.Ordinal),
                $"colony-renderer.js loads a texture from '{fetched}'. Every texture in this view is "
              + "generated on a canvas so the console's CSP needs no img-src exception.");
    }

    /// <summary>
    /// A CONDUIT MAY DRIFT; IT MAY ONLY BRIGHTEN FOR SOMETHING RECORDED.
    ///
    /// An earlier `.116` pass froze the grains outright, on the reasoning that flow along a permanent
    /// structural link claims work is passing through it. That was the rule applied one step too far
    /// and it bought a console that looked dead. The line is AMBIENT versus ASSERTED: drifting grains
    /// say the passage exists and the view is live; a BRIGHT WAVE with no event behind it is a lie.
    ///
    /// So this guard no longer forbids motion. It forbids the two ways a conduit could brighten
    /// without a row behind it: a head position swept from a clock, and a fourth term in the resting
    /// brightness that nothing recorded.
    /// </summary>
    [Fact]
    public void AConduitBrightens_OnlyForSomethingTheColonyRecorded()
    {
        var r = Code("colony-renderer.js");

        // The head is set in exactly one place: the flight loop, one entry per unique event id.
        Assert.Contains("u.uHead.value = co.head;", r);
        var heads = Regex.Matches(r, @"\.head = ").Count;
        Assert.True(heads == 2,
            $"`co.head` is assigned in {heads} places. The legal two are both inside the flight loop — "
          + "the advance and the reset on completion (the conduit's initial -1 is an object literal, "
          + "not an assignment). A third is a head position coming from somewhere other than a "
          + "recorded transition.");

        // …and never from a clock. `ms` and `performance.now()` may pace the frame; they may not
        // decide where along a conduit anything is.
        foreach (var clocked in new[] { "uHead.value = ms", "% 1)", "Date.now() %" })
            Assert.False(r.Contains(clocked, StringComparison.Ordinal),
                $"colony-renderer.js derives conduit progress from '{clocked}'. The reference sweeps "
              + "`(ms * 0.00016) % 1` when the host gives it no progress; there is no per-task "
              + "progress in this model, so a swept head animates a number that does not exist.");

        // Resting brightness has exactly three terms, and each names its row.
        Assert.Contains("co.spec.rest * (state.level >= 2 ? 0.6 : 1)", r);
        Assert.Contains("(opts.trails ? co.trail * 0.22 : 0)", r);
        Assert.Contains("(co.busy ? 0.2 : 0)", r);

        // Drift is real motion and freezes with the operator's motion preference.
        Assert.Contains("driftConduit(co, k ? dtSec * 3 : 0)", r);

        // And the flights themselves are still one-shot and still refused on a historical frame.
        Assert.Contains("playedTransitions[tr.id] = true", r);
        Assert.Contains("if (sc.meta && sc.meta.history) return;", r);
    }

    /// <summary>
    /// THE PHEROMONE LAYER IS DRAWN FROM ITS TWO REAL ROWS, AT THE LEVEL EACH ONE EXISTS AT.
    ///
    /// `pheromone_trails` keys strength to `worker:{id}`, so an EDGE has no row of its own and a
    /// conduit that claimed a "trail strength" would be quoting a number for a thing the table does
    /// not describe. What an edge does have is how many recorded transitions have crossed it, and
    /// reinforcement-by-use is what a trail is. The per-worker strength is not discarded — it lights
    /// the ant orbs, where it belongs.
    ///
    /// Both are gated by the operator's `trails` preference, because a control that turns off a
    /// display and leaves the thing it names still driving the picture is a lie about the control.
    /// </summary>
    [Fact]
    public void ThePheromoneLayer_IsDrawnFromItsOwnRows()
    {
        var r = Code("colony-renderer.js");

        // Per-edge: counted crossings, normalised, never invented.
        Assert.Contains("function conduitState", r);
        Assert.Contains("crossings[k] = (crossings[k] || 0) + 1;", r);
        Assert.Contains("co.trail = most > 0 ? n / most : 0;", r);

        // Per-ant: the projection's own trail strength, and null is not zero.
        Assert.Contains("entry.res.trail && fin(entry.res.trail.strength)", r);
        Assert.Contains("var trail = opts.trails ? a.pher : 0;", r);

        // The preference reaches both.
        Assert.Contains("opts.trails ? co.trail * 0.22 : 0", r);

        // And `busy` is a real running task on a real persisted edge, not a guess about either.
        Assert.Contains("(x.runningTasks || []).length", r);
        Assert.Contains("(sc.edges || []).forEach", r);
    }

    /// <summary>
    /// EVERY INTRA-CHAMBER LINK IS A ROW THAT EXISTS. Five families, each one a relationship the
    /// colony recorded: a record belongs to its cluster (that is the event type it has), the clusters
    /// form the chamber's context ring, a worker reports to the role the registry says it reports to,
    /// `record.ant` names whichever unit actually ran, and records sharing a `mission_id` are one
    /// mission's thread through this chamber.
    ///
    /// The last three are not in the reference — its sample data has no equivalent — so they are the
    /// place a generated link would most easily be added to make a sparse chamber look busy. A
    /// chamber whose records name no ant and no mission draws the cluster families and the roster
    /// chain and nothing else; a mission with one record here contributes no segment, because a
    /// thread of one is not a thread; and the parent of a worker is READ from `parentRoleId` rather
    /// than recovered by splitting an id on a dot, which would be re-deriving a fact the projection
    /// already handed over.
    /// </summary>
    [Fact]
    public void EveryIntraChamberLink_IsARelationshipTheColonyRecorded()
    {
        var r = Code("colony-renderer.js");

        Assert.Contains("slotOf[String(a.name).toLowerCase()] = a.slot;", r);
        Assert.Contains("if (!slot) return;", r);                       // an ant this chamber does not host
        Assert.Contains("if (!r.missionId) return;", r);                // no mission, no thread
        Assert.Contains("if (list.length < 2) return;", r);             // a thread of one is not a thread
        Assert.Contains("a.createdAt < b.createdAt", r);                // recorded order, not invented order

        // The roster chain reads its parent rather than parsing the id for one.
        Assert.Contains("a.parentRoleId", r);
        Assert.Contains("seatOf[a.parentRoleId]", r);
        Assert.False(r.Contains("split('.')", StringComparison.Ordinal),
            "colony-renderer.js splits a worker id to find its parent. `parentRoleId` travels on the "
          + "worker precisely so nothing has to, and the split is wrong the first time a worker id "
          + "contains a dot of its own.");

        // All five families exist and all five start dark.
        Assert.Contains("var ring = lines(cc, ch.shellHex);", r);
        Assert.Contains("var mission = lines(ms, TOKENS.gold);", r);
        Assert.Contains("var chainL = lines(chain, ch.shellHex);", r);
        Assert.Contains("var author = lines(au, ch.coreHex);", r);
        Assert.Contains("var spoke = lines(segs, ch.coreHex);", r);
        Assert.Contains("opacity: 0,", r);

        // …and the blend helper rewrites every one of them, so a link never trails the particle it
        // points at during the ordered-strata cross-fade.
        foreach (var arr in new[] { "ic.g.attributes.position", "ic.g2.attributes.position",
                                    "ic.gA.attributes.position", "ic.gM.attributes.position" })
            Assert.Contains(arr + ".needsUpdate = true;", r);
    }

    /// <summary>
    /// A WORKER IS LABELLED WITH ITS NAME AND MATCHED ON ITS ID, and those are two different strings.
    /// `ant_name` on an event is `constraint.scope_guard`, so that is what a record can be joined on;
    /// "ScopeGuard" is what the registry calls the ant and what the 2D colony view has always shown.
    /// Printing the id under every worker orb gave one ant two names in one product, and matching on
    /// the display name instead would file every worker-authored record under `unassigned` again —
    /// the same defect, in the other direction.
    /// </summary>
    [Fact]
    public void AWorkerOrb_ShowsItsNameAndMatchesOnItsId()
    {
        var r = Code("colony-renderer.js");
        var topo = Code("colony-topology.js");

        // The renderer keeps the two apart, deliberately.
        Assert.Contains("name: w.name || w.id, matchId: w.id", r);
        Assert.Contains("byAnt[String(entry.matchId).toLowerCase()]", r);
        Assert.False(r.Contains("byAnt[String(entry.name)", StringComparison.Ordinal),
            "colony-renderer.js matches records on a worker's DISPLAY name. Events carry the worker "
          + "id; matching on the readable name files every worker-authored record as unauthored.");

        // The sector index is keyed on the id, for the same reason.
        Assert.Contains("st.roleSector[wid.toLowerCase()] = sid;", topo);
        Assert.Contains("function workerId", topo);
        Assert.Contains("function workerName", topo);
        Assert.Contains("function workerParent", topo);

        // And the inspector prints both, because the two answer different questions.
        var hud = Code("colony-hud.js");
        Assert.Contains("el('span', 'clh-item-n', w.name || w.id)", hud);
        Assert.Contains("el('span', 'clh-item-s', w.id)", hud);
    }

    /// <summary>
    /// AN IDLE ANT HOLDS STATION. The reference gives every ant a countdown — `a.work -= dt`, then a
    /// cluster picked at random and a "pheromone run" laid out to it. It is the most misleading thing
    /// this view could draw: an operator looking at a quiet colony would see every ant working.
    ///
    /// `working` here requires a real running task assigned to that unit, which the projection
    /// derives from /graph, and the pheromone number an ant shows is the trail the colony recorded.
    /// </summary>
    [Fact]
    public void NoAnt_RunsOnAFabricatedWorkTimer()
    {
        var r = Code("colony-renderer.js");

        foreach (var invented in new[] { "a.work", "beamDur", "lastDrop", "a.drops", "tasks_completed" })
            Assert.False(r.Contains(invented, StringComparison.Ordinal),
                $"colony-renderer.js carries '{invented}' — the reference's ant work timer. An ant "
              + "that appears busy without a running task is an animation of work that is not "
              + "happening.");

        // Vacuity floor: the real status and the real trail are both read.
        Assert.Contains("a.status === 'working'", r);
        Assert.Contains("r.trail && fin(r.trail.strength)", r);
    }

    /// <summary>
    /// A CHAMBER'S NAME IS THE REGISTRY'S. The reference lets an operator retitle a chamber and
    /// recolour it in one call. Colour is presentation, like the layout that already persists to
    /// /ui/state; a name is identity, and a console that let one page disagree with the registry
    /// about what a colony is called would be wrong on every other page at the same time.
    /// </summary>
    [Fact]
    public void AnOperatorMayRecolourAChamber_ButNotRenameOne()
    {
        var r = Code("colony-renderer.js");

        Assert.Contains("function setChamberStyle", r);
        Assert.Contains("if (!cfg || !cfg.color", r);
        Assert.False(r.Contains("cfg.label", StringComparison.Ordinal),
            "colony-renderer.js reads cfg.label in setChamberStyle. A chamber's label is the "
          + "registry's Colony value, projected by ColonyLiveProjection; the console renders it.");

        // The label really does come from the projection rather than from this file.
        Assert.Contains("ch.sec.label || id", r);
    }

    /// <summary>
    /// THE SAVED LAYOUT FROM `.115` IS REFUSED, NOT MIGRATED. Those seats were recorded in a world
    /// fourteen times this one; replayed here every chamber lands far outside the 130-unit dolly
    /// limit and the operator opens the view to empty space. The ×14 factor was never written down,
    /// so back-solving it would be a fiction dressed as a migration — an old layout resets instead.
    /// </summary>
    [Fact]
    public void ASavedLayout_FromThePreviousWorldScale_IsRefused()
    {
        var r = Code("colony-renderer.js");

        Assert.Contains("schema: 2", r);
        Assert.Contains("l.schema !== 2", r);
        Assert.Contains("Math.abs(n) <= 120", r);
        Assert.False(r.Contains("Math.abs(n) <= 4000", StringComparison.Ordinal),
            "colony-renderer.js still accepts coordinates up to 4000, which is the `.115` bound. At "
          + "this world scale that admits a saved layout no camera limit can reach.");
    }

    /// <summary>
    /// PICKING IS MEASURED IN PIXELS. A Points cloud raycast tests a WORLD-space threshold, which is
    /// the wrong unit for a target aimed at with a cursor: one constant is a huge target up close and
    /// a sub-pixel one at survey distance. `.115` set that threshold to 7 and chambers still felt
    /// unresponsive, because no single value can be right at both ends of a 60× dolly range.
    /// </summary>
    [Fact]
    public void PickingIsScreenSpace_NotARaycast()
    {
        var r = Code("colony-renderer.js");

        Assert.Contains("function hitTest", r);
        Assert.Contains("Math.hypot(p.x - mx, p.y - my)", r);
        foreach (var world in new[] { "Raycaster", "ray.params.Points.threshold", "intersectObjects" })
            Assert.False(r.Contains(world, StringComparison.Ordinal),
                $"colony-renderer.js picks with '{world}'. A world-space threshold cannot be correct "
              + "across the dolly range; the screen-space test makes every target the size it looks.");
    }

    /// <summary>
    /// A SLOW RASTERISER IS NOT ASKED TO KEEP DRAWING, AND THE LADDER CAN EXPRESS BOTH OUTCOMES. It
    /// reacts to the FIRST slow frame rather than to a thirty-frame average — by the time an average
    /// moves, the operator has already watched the view stutter — and a frame that blocks for a full
    /// second stops the loop and says so, rather than degrading quality forever.
    /// </summary>
    [Fact]
    public void TheQualityLadder_DegradesAndThenStops()
    {
        var r = Code("colony-renderer.js");

        Assert.Contains("function setQuality", r);
        Assert.Contains("function breaker", r);
        Assert.Contains("if (rms > 1000) { breaker(rms); return; }", r);
        Assert.Contains("else if (rms > 120) setQuality(1, rms);", r);
        Assert.Contains("perf.avg > 45", r);
        Assert.Contains("setDrawRange", r);

        // The stall is reported rather than silently swallowed — the host and the HUD can both hear it.
        Assert.Contains("emit('stall'", r);
    }

    /// <summary>
    /// THE CHROME-AVOID CONTRACT HAS BOTH ENDS. The renderer places chamber labels clear of the
    /// panels the HUD puts over the canvas, and it finds them by attribute. A renderer that reads the
    /// attribute while nothing sets it is the "declared and reaching nobody" defect: the code looks
    /// correct, the query returns an empty list, and every label lands under the inspector.
    /// </summary>
    [Fact]
    public void TheChromeAvoidContract_IsWrittenAtBothEnds()
    {
        Assert.Contains("[data-chrome-avoid]", Code("colony-renderer.js"));
        Assert.Contains("setAttribute('data-chrome-avoid'", Code("colony-hud.js"));
        Assert.Contains("data-chrome-avoid", Raw("index.html"));

        // A hidden panel measures 0x0 at the origin; keeping that rectangle would push every label
        // away from the top-left corner for a panel that is not on screen.
        Assert.Contains("b.width < 1 || b.height < 1", Code("colony-renderer.js"));
    }
    /// <summary>
    /// THE MICROMOUND CHAMBER OPENS THE MICROMOUND. Every other chamber is a group of registry roles
    /// and the chamber inspector answers the questions worth asking about one. The mound is a
    /// PHYSICAL DEVICE with a stop, a charter, a lease and an enrollment — a card reading "registry
    /// roles: 0, workers: 0" answers none of them, and that is what clicking it used to produce.
    ///
    /// The panel it opens carries the one control that has to be reachable from wherever the operator
    /// is looking, because the reason to reach for it is that something is going wrong right now.
    /// Everything else hands over to the Micromound console rather than growing a second copy of
    /// forms whose vocabulary is a closed PROTOCOL set.
    /// </summary>
    [Fact]
    public void ClickingTheMicromoundChamber_OpensTheDeviceAndNotAChamberCard()
    {
        var hud = Code("colony-hud.js");

        Assert.Contains("id === 'mound' ? { kind: 'mound' }", hud);
        Assert.Contains("function moundPanel", hud);

        // The control an operator needs from anywhere, and its honest wording.
        Assert.Contains("'RESUME MOUND' : 'STOP THIS MOUND'", hud);
        Assert.Contains("o.onMoundStop", hud);
        Assert.Contains("OPEN MICROMOUND CONSOLE", hud);
        Assert.Contains("window.go('/tools/micromound')", hud);

        // …and it is disabled rather than decorative when the path is not there.
        Assert.Contains("stopBtn.disabled = !o.onMoundStop", hud);
        Assert.Contains("typeof window.go !== 'function'", hud);

        // The global stop is still not offered as a control, here as in micromound.js.
        Assert.DoesNotContain("'/micromound/stop/global'", hud);
        Assert.Contains("Stop always wins", Raw("colony-hud.js"));
    }

    /// <summary>
    /// AND THE MUTATION GOES THROUGH THE HOST, WHICH RE-READS RATHER THAN ASSUMING. The HUD may not
    /// reach the network — that boundary is what makes "one place fetches" checkable by inspection —
    /// so the stop is posted by `colony-host.js`, which then re-reads the fleet listing. A view that
    /// flipped its own `stopped` flag on a 200 would disagree with the colony the first time an order
    /// was accepted and then superseded, and would look right while doing it.
    /// </summary>
    [Fact]
    public void AMoundStop_IsPostedByTheHostAndThenReRead()
    {
        var host = Code("colony-host.js");

        Assert.Contains("function moundStop", host);
        Assert.Contains("stopped ? '/micromound/stop' : '/micromound/stop/resume'", host);
        Assert.Contains("api(path, 'POST', { mound_id: moundId })", host);
        // The re-read, on both the success and the failure path.
        Assert.Equal(3, Regex.Matches(host, @"api\('/micromound/mounds'\)").Count);
        Assert.Contains("onMoundStop: moundStop", host);

        // The state is never decided here.
        Assert.False(host.Contains("m.stopped = ", StringComparison.Ordinal),
            "colony-host.js writes a mound's stopped flag. The colony decides that; this re-reads it.");
    }

    /// <summary>
    /// NO AUTHORITY SEAL. The reference parks a lock sprite at 0.42 along the Queen→Micromound
    /// conduit. It is a badge on a line, not a control, and it sat in the middle of the frame
    /// implying the mound was locked to interaction — the opposite of true, now that clicking that
    /// chamber is how an operator stops the device. The authority relationship is already said by the
    /// conduit, which is the only edge in the colony with its own kind.
    /// </summary>
    [Fact]
    public void TheAuthorityConduit_CarriesNoLockBadge()
    {
        var r = Code("colony-renderer.js");

        foreach (var badge in new[] { "lockTex", "TEX.lock", "seal" })
            Assert.False(r.Contains(badge, StringComparison.Ordinal),
                $"colony-renderer.js still builds the authority seal ('{badge}'). A lock drawn over "
              + "the one chamber an operator can act on reads as 'you may not touch this'.");

        // Vacuity floor: the authority conduit it used to sit on is still there, and still its own kind.
        Assert.Contains("kind: 'authority'", r);
        Assert.Contains("auth ? 40 : lateral ? 24 : 60", r);
    }

    /// <summary>
    /// THE PORT STILL AGREES WITH THE SOURCE IT CAME FROM — the strongest guard in this file, and the
    /// only one that is not a literal transcribed by hand.
    ///
    /// `docs/design/colony-live-3d/reference/colony-renderer.js` is the design handoff's working
    /// implementation, vendored unchanged. `src/Anthill.UI/colony-renderer.js` is its port. Every
    /// other check in this file asserts a constant this test file wrote down, so it can only catch a
    /// constant being DELETED; this one reads both files and compares what it extracts, so it also
    /// catches a constant being CHANGED to something plausible — which is how a ported renderer
    /// actually drifts. `.115` did not change these numbers by mistake, it re-derived them, and every
    /// re-derived one was wrong.
    ///
    /// Each pattern is written to match BOTH files, which is why they avoid `th.`/`TH.`,
    /// `const`/`var` and parameter names. A pattern that stops matching the reference is reported as
    /// loudly as a value that disagrees: this guard is not allowed to pass by reading nothing.
    ///
    /// If the design is ever revised: update `reference/`, run this, and it names every place the
    /// port has fallen behind.
    /// </summary>
    [Fact]
    public void ThePortedConstants_StillAgreeWithTheVendoredReference()
    {
        var refPath = Path.Combine(SourceText.RepoRoot(), "docs", "design", "colony-live-3d",
                                   "reference", "colony-renderer.js");
        Assert.True(File.Exists(refPath),
            "docs/design/colony-live-3d/reference/colony-renderer.js is missing. It is the source this "
          + "renderer was ported from, and docs/HANDOFF.md tells the next session not to re-derive the "
          + "math — an instruction that is worthless without the code it points at.");

        var reference = File.ReadAllText(refPath).Replace("\r\n", "\n");
        var port = Raw("colony-renderer.js").Replace("\r\n", "\n");

        (string Name, string Pattern)[] constants =
        [
        ("camera", @"PerspectiveCamera\((\d+),\s*1,\s*([\d.]+),\s*(\d+)\)"),
        ("home camera", @"dist:\s*(\d+),\s*theta:\s*([\d.]+),\s*phi:\s*([\d.]+)"),
        ("orbit limits", @"phi:\s*\[([\d.]+), Math\.PI - ([\d.]+)\],\s*dist:\s*\[([\d.]+),\s*(\d+)\]"),
        ("resize fit", @"(\d+) \* Math\.max\(1, ([\d.]+) / camera\.aspect\)"),
        ("point size clamp", @"clamp\(size \* uRec \* uScale \* \(([\d.]+) / max\(([\d.]+), -mv\.z\)\), ([\d.]+), ([\d.]+)\)"),
        ("sprite mask cut", @"gl_PointCoord\)\.a < ([\d.]+)\) discard"),
        ("conduit alpha", @"vA = aB \* \(uRest \+ ([\d.]+) \* wave\);"),
        ("conduit size clamp", @"clamp\(aS \* \(1\.0 \+ ([\d.]+) \* wave\) \* uScale \* \(([\d.]+) / max\(([\d.]+), -mv\.z\)\), ([\d.]+), ([\d.]+)\)"),
        ("conduit colour mix", @"mix\(uFrom, uTo, smoothstep\(([\d.]+), ([\d.]+), aT\)\)"),
        ("conduit grains", @"n: auth \? (\d+) : lateral \? (\d+) : (\d+)"),
        ("conduit streams", @"streams: auth \? (\d+) : lateral \? (\d+) : (\d+)"),
        ("conduit radius", @"rad: auth \? ([\d.]+) : lateral \? ([\d.]+) : ([\d.]+)"),
        ("conduit rest floor", @"rest: auth \? ([\d.]+) : lateral \? ([\d.]+) : ([\d.]+)"),
        ("conduit sharpness", @"sharp: auth \? (\d+) : (\d+)"),
        ("halo falloff", @"Math\.pow\(1 - t, ([\d.]+)\) \* ([\d.]+)"),
        ("nucleus scale", @"nucleus\.scale\.setScalar\(\w+\.r \* ([\d.]+)\)"),
        ("record edge", @"1 - ([\d.]+) \* Math\.pow\(rad, ([\d.]+)\)"),
        ("record alpha", @"82 \+ \w+\.?\w* \* ([\d.]+)\) \* \(([\d.]+) \+ ([\d.]+) \* edge\)"),
        ("record size", @"\(([\d.]+) \+ \w+\.?\w* \* ([\d.]+)\) \* \(([\d.]+) \+ ([\d.]+) \* edge\)"),
        ("strata height", @"- 0\.5\) \* \w+\.r \* ([\d.]+)"),
        ("strata band", @"Math\.pow\(y / \(\w+\.r \* ([\d.]+)\), 2\)"),
        ("strata radius", @"\w+\.r \* ([\d.]+) \* band \* Math\.sqrt\(\(k \+ ([\d.]+)\) / m\)"),
        ("golden angle", @"ang = k \* ([\d.]+)"),
        ("curve end trim", @"/ len \* ([\d.]+)\)"),
        ("curve lean", @"lean = \(rnd\(\) \* 2 - 1\) \* span \* ([\d.]+)"),
        ("curve lift", @"lift = \(rnd\(\) \* 2 - 1\) \* span \* ([\d.]+)"),
        ("curve sag", @"p\.y -= env \* span \* ([\d.]+)"),
        ("path samples", @"spec = \w+\.spec, N = (\d+)"),
        ("grain speed", @"\(([\d.]+) \+ rnd\(\) \* ([\d.]+)\) \* \(primary \? 1 : ([\d.]+)\)"),
        ("grain converge", @"([\d.]+) \+ ([\d.]+) \* Math\.pow\(env, ([\d.]+)\)\)"),
        ("grain brightness", @"\* \(([\d.]+) \+ ([\d.]+) \* Math\.pow\(env, ([\d.]+)\)\)"),
        ("pixel-scale fov", @"Math\.tan\((\d+) \* Math\.PI / 360\)"),
        ("orb pixel size", @"\(detail \? (\d+) : (\d+)\) \* \(\w+\.isQueen \? ([\d.]+) : \w+\.isLead \? 1 : ([\d.]+)\)"),
        ("crew ramp", @"\((78) - cam\.dist\) / (46)"),
        ("link ramp", @"\((58) - cam\.dist\) / (30)"),
        ("hit ant radius", @"bad = (\d+);"),
        ("hit record radius", @"bd = (\d+);"),
        ("hit cluster radius", @"bcd = (\d+);"),
        ("hit sector radius", @"Math\.max\((\d+), Math\.abs\(edge\.x - p\.x\)\)"),
        ("orbit gain", @"theta -= dx \* ([\d.]+)"),
        ("tilt gain", @"phi - dy \* ([\d.]+)"),
        ("pan gain", @"cam\.dist \* ([\d.]+)"),
        ("wheel gain", @"Math\.exp\(e\.deltaY \* ([\d.]+)\)"),
        ("zoom floor", @"\.r \* ([\d.]+) : (\d+)"),
        ("level thresholds", @"r \* (2\.35) \? 2 : [\s\S]{0,40}?r \* (4\.8) \? 1 : 0"),
        ("focus distance", @"\.r \* (3\.4)"),
        ("enter distance", @"\.r \* (1\.8)"),
        ("ordering step", @"wantOrg - uo\.value\) \* (?:\(reduced \? 1 : )?(0\.07)"),
        ("ordering epsilon", @"> (0\.0008)"),
        ("frame pacing", @"perf\.slack \? (\d+) : (\d+)"),
        ("quality draw range", @"level === 1 \? ([\d.]+) : ([\d.]+)"),
        ("circuit breaker", @"rms > (\d+)\) \{ breaker"),
        ("quality step 2", @"rms > (\d+)\) setQuality\(2"),
        ("quality step 1", @"rms > (\d+)\) setQuality\(1"),
        ("quality average", @"perf\.avg > (\d+) && perf\.n % (\d+)"),
        ("slack threshold", @"rms > (\d+)\) perf\.slack"),
        ("dim other chambers", @"\.level >= 2 \? ([\d.]+) : 1"),
        ("inside point scale", @"inside \? ([\d.]+) : 1\) - \w+\.\w+\.uniforms\.uScale"),
        ("inside record lift", @"inside \? ([\d.]+) : 1\) - \w+\.\w+\.uniforms\.uRec"),
        ("drift cadence", @"% 3 === 0\) driftConduit"),
        ("link opacity", @"\? (0\.36) : \(state\.focus && !focused\w*\) \? (0\.03) : (0\.06) \+ \w+ \* (0\.16)")
        ];

        var blind = new List<string>();
        var drifted = new List<string>();

        foreach (var (name, pattern) in constants)
        {
            var a = Regex.Match(reference, pattern);
            var b = Regex.Match(port, pattern);

            if (!a.Success) { blind.Add($"{name}: the pattern no longer matches the REFERENCE"); continue; }
            if (!b.Success) { drifted.Add($"{name}: gone from the port (reference has {Show(a)})"); continue; }
            if (Show(a) != Show(b)) drifted.Add($"{name}: reference {Show(a)} vs port {Show(b)}");
        }

        Assert.True(blind.Count == 0,
            "This guard has gone blind — the patterns below no longer match the vendored reference, so "
          + "they prove nothing about the port either. Fix the patterns before trusting a pass:\n  "
          + string.Join("\n  ", blind));

        Assert.True(drifted.Count == 0,
            $"colony-renderer.js has drifted from the design it was ported from in {drifted.Count} "
          + "place(s). These are not stylistic choices — the handoff's own failure table lists what "
          + "changing each one breaks:\n  " + string.Join("\n  ", drifted));

        // Vacuity floor: a real comparison happened over a substantial table.
        Assert.True(constants.Length >= 50,
            $"only {constants.Length} constants are compared; the table has been gutted.");
        Assert.True(reference.Length > 40_000, "the vendored reference is too small to be the real file.");
    }

    /// <summary>Groups of one match, rendered for a diff message.</summary>
    private static string Show(Match m) =>
        "[" + string.Join(", ", m.Groups.Cast<Group>().Skip(1).Select(g => g.Value)) + "]";
}
