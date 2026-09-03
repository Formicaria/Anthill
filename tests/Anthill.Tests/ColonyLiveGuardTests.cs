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
    /// <summary>
    /// The four assets of the feature after the `.119` port: the reducer over the read model, the
    /// canvas renderer, the wiring, and the page chrome. `colony-renderer.js`, `colony-hud.js` and the
    /// vendored three.js are gone with the WebGL UI they belonged to; the backend they were built
    /// against — the snapshot, the records read, the projection, the stream watermark — is what the
    /// canvas renderer now speaks, and every guard below that protected that contract survives.
    /// </summary>
    private static readonly string[] ColonyAssets =
    [
        "colony-topology.js", "colony-live.js", "colony-host.js", "colony-home.js"
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
    /// Nothing in this feature may place, move or size a thing by chance — the same colony state
    /// must draw the same picture twice. A record grain sits where the hash of its id puts it, on
    /// every reload and on every screen; the reducer computes that hash and the renderer reads it.
    /// </summary>
    [Fact]
    public void NoColonyAsset_PlacesAnythingAtRandom()
    {
        foreach (var asset in ColonyAssets)
            Assert.False(Code(asset).Contains("Math.random", StringComparison.Ordinal),
                $"{asset} calls Math.random. Record placement is derived from a hash of the record id "
              + "so a re-render is stable; a random position reshuffles the colony under the "
              + "operator's cursor and makes \"the third grain from the left\" meaningless.");

        // Vacuity floor: the deterministic placement exists at both ends.
        var topo = Code("colony-topology.js");
        Assert.Contains("function hash32", topo);
        Assert.Contains("function placement", topo);
        Assert.Contains("r.place || hashPlace(", Code("colony-live.js"));
    }

    // ---------------------------------------------------------------------------------------------
    // §17.2 — no client-side clock, no looping playback
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A repeating timer is how a mission clock, a looping traffic animation and a second poll all
    /// get built. None of the three is allowed: mission progress comes from task status, transitions
    /// play once per recorded event id, and the live picture rides the stream app.js already holds.
    /// The page chrome included — its bar refreshes when the reducer publishes, not on a clock.
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
        // And the bar's refresh really is scene-driven.
        Assert.Contains("ColonyHost.onScene(", Code("colony-home.js"));
    }

    /// <summary>
    /// A recorded transition plays ONCE, keyed by the event id, and the flight is disposed when it
    /// lands. Restarting every ant every frame reads as continuous traffic in a colony doing nothing.
    /// </summary>
    [Fact]
    public void RecordedTransitions_PlayOncePerEventId()
    {
        var r = Code("colony-live.js");
        Assert.Contains("playedTransitions[tr.id]", r);
        Assert.Contains("playedTransitions[tr.id] = true", r);
        // A historical frame never burns those ids — see §14.
        Assert.Contains("if (scene.meta && scene.meta.history) return;", r);
        // And a landed flight is removed, not restarted.
        Assert.Contains("flights.splice(fi, 1)", r);
    }

    // ---------------------------------------------------------------------------------------------
    // §17.3 — one place fetches, and it is not the model or the view
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The model must not be able to go and get more data, and the view must not be able to act on
    /// the colony behind the host's back. The page chrome (`colony-home.js`) is allowed exactly one
    /// kind of call of its own — the project list and project creation its composer needs, which
    /// are the Chat picker's own calls — and nothing of the read model.
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
            Assert.False(code.Contains("/colony/live", StringComparison.Ordinal),
                $"{asset} names a Colony Live endpoint. The host hydrates; the model and the view consume.");
        }

        foreach (var asset in new[] { "colony-topology.js", "colony-live.js" })
            Assert.False(Code(asset).Contains("api(", StringComparison.Ordinal),
                $"{asset} calls api(). The reducer and the renderer never fetch.");

        var homeCalls = Regex.Matches(Code("colony-home.js"), @"api\('([^']+)'").Select(m => m.Groups[1].Value).ToList();
        Assert.True(homeCalls.Count > 0, "colony-home.js makes no api() call; the composer's project scope has gone somewhere else.");
        Assert.True(homeCalls.All(p => p == "/projects"),
            "colony-home.js calls endpoints beyond /projects: " + string.Join(", ", homeCalls.Distinct())
          + ". The page chrome may resolve a project for its composer and nothing more.");

        // Vacuity floor: the host really is the one doing it.
        Assert.Contains("api('/colony/live/snapshot')", Code("colony-host.js"));
        Assert.Contains("api('/colony/live/records?limit=200')", Code("colony-host.js"));
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
    // §17.6 — approvals are exact or explicitly unresolved, and nothing here decides one
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
    /// The renderer draws an approval BOUNDARY only for a resolved approval — one the reducer could
    /// place on a role and a task — and shows an unresolved one as attention at the Queen, who is
    /// the authority that must answer it. Neither the renderer nor the page chrome decides an
    /// approval: the chip that counts them opens Chat, where app.js's authenticated `doApproval`
    /// already lives, and no asset here builds an approve or reject route of its own.
    /// </summary>
    [Fact]
    public void NothingHere_DecidesAnApproval()
    {
        var live = Code("colony-live.js");
        Assert.Contains("return a.resolved;", live);
        Assert.Contains("return !a.resolved;", live);
        Assert.Contains("sector: 'queen', kind: 'approval'", live);

        foreach (var asset in ColonyAssets)
            foreach (var forbidden in new[] { "'/approve/", "\"/approve/", "'/reject/", "\"/reject/", "doApproval(" })
                Assert.False(Code(asset).Contains(forbidden, StringComparison.Ordinal),
                    $"{asset} decides an approval ({forbidden}). Decisions belong to app.js's doApproval, "
                  + "which carries the bearer token and refreshes the queue; Colony Live only shows them.");

        // The needs-you chip is a door to Chat, not a control.
        Assert.Contains("act === 'needs') go('/chat')", Code("colony-home.js"));
        // Vacuity floor: the real path still exists where it is supposed to.
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

    }

    /// <summary>
    /// The renderer has a mound chamber only when the fleet listing returned one, and it says
    /// STOPPED only from the fleet's own `stopped`. No placeholder device, no derived verdict.
    /// </summary>
    [Fact]
    public void TheMoundChamber_ExistsOnlyWhenTheFleetSaysSo()
    {
        var live = Code("colony-live.js");
        Assert.Contains("s.present = !!(scene.mound && scene.mound.present)", live);
        Assert.Contains("return m.stopped;", live);
        foreach (var asset in new[] { "colony-live.js", "colony-home.js" })
            foreach (var invented in new[] { "'edge_queen'", "'online'", "'offline'", "'quiesced'" })
                Assert.False(Code(asset).Contains(invented, StringComparison.Ordinal),
                    $"{asset} carries {invented} — a mound tier or verdict the browser does not decide.");
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
    /// Mounting is where a renderer actually fails. When it throws, the classic canvas must come
    /// back — not stay hidden under a renderer that never drew — and the operator is told why.
    /// </summary>
    [Fact]
    public void ARendererThatFailsToMount_FallsBackInsteadOfBlankingTheView()
    {
        var host = Code("colony-host.js");

        Assert.Contains("try {", host);
        Assert.Contains("live.mount(area);", host);
        Assert.Contains("live.destroy();", host);
        Assert.Contains("return false;", host);
        // enable()'s verdict decides the classic canvas's visibility; a failed mount leaves it shown.
        Assert.Contains("if (!enable(area, classic)) on = false;", host);
        Assert.Contains("classic.style.display = on ? 'none' : '';", host);
        Assert.Contains("failed to mount", Raw("colony-host.js"));
    }

    /// <summary>
    /// Colony Live is the default view (`.117`): only an explicit '0' — an operator who turned it
    /// off — keeps the classic canvas, and the switch is offered in BOTH states so the canvas is
    /// never a one-way door.
    /// </summary>
    [Fact]
    public void TheLiveView_IsTheDefault_AndTheClassicCanvasIsAnOptOutWithAWayBack()
    {
        Assert.Contains("localStorage.getItem(VIEW_KEY) !== '0'", Code("colony-host.js"));
        var home = Code("colony-home.js");
        Assert.Contains("act === 'toggle3d'", home);
        Assert.Contains("b.textContent = on ? 'Classic 2D' : 'Live 3D'", home);
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
    // §19 — the canvas renderer draws the read model and nothing else. v0.3.8.119.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A CHAMBER'S GRAINS ARE ITS RECORDS AND ITS ORBS ARE ITS RESIDENTS. The `.111` renderer seeded
    /// several hundred points per sphere so an empty page looked alive, then bound records to some
    /// of them and dimmed the rest. Nothing is seeded now: every grain is a persisted record from
    /// the read model, placed by its hash and seated by its cluster; every orb is a registry role
    /// or worker from the snapshot. An empty chamber is drawn empty, and `verified` — the evidence
    /// table's verdict, read from the record — is what moves a grain into the core.
    /// </summary>
    [Fact]
    public void AChambersGrains_AreItsRecords_AndItsOrbs_AreItsResidents()
    {
        var live = Code("colony-live.js");

        Assert.Contains("function rebuildSector(s, sec)", live);
        // Records are walked per cluster — the chamber's REAL groupings — never from a seeded pool.
        Assert.Contains("function clusterSeats(sec, R)", live);
        Assert.Contains("cl.records.forEach(function (r, k)", live);
        Assert.Contains("(sec.residents || []).forEach(", live);
        Assert.Contains("var verified = r.verification === 'verified', pher = trailOf(r.ant);", live);
        Assert.Contains("verif: r.verification", live);

        foreach (var filler in new[] { "for (var i = 0; i < s.n;", "applyCounts", "bindRecords", "p.hidden = seen", "(demo)", "demo data" })
            Assert.False(live.Contains(filler, StringComparison.Ordinal),
                $"colony-live.js carries '{filler}' — seeded filler or demo scaffolding. A chamber's "
              + "particles are its records; a chamber with none is empty.");

        // The strata spiral steps by the golden angle under its OWN name. `.119`'s first cut named the
        // constant SPIRAL, which the galaxy's spiral object (`var SPIRAL = null`, same function scope,
        // hoisted) silently clobbered — every record grain projected to NaN and drew nothing, with no
        // error anywhere. A chamber with ten records looked like a chamber with none.
        Assert.Contains("SPIRAL_STEP = 2.399963", live);
        Assert.Contains("var ang = k * SPIRAL_STEP", live);
        Assert.Single(Regex.Matches(live, @"\bSPIRAL = null\b"));   // the galaxy declares it once; nothing else may

        // The only points the operator can pick are the ones that ARE something.
        Assert.Contains("if (p._q && (p.rec || p.resident))", live);
        Assert.Contains("return (p && (p.rec || p.resident)) || null;", live);
    }

    /// <summary>
    /// AN IDLE ANT HOLDS STATION. An ant rides a segment only because a running task in the graph
    /// puts it there; the list is rebuilt from `runningTasks` on every scene and never carries a
    /// countdown. A resident's `working` colour comes from the reducer's status, which requires a
    /// real running task assigned to that unit.
    /// </summary>
    [Fact]
    public void NoAnt_RunsOnAFabricatedWorkTimer()
    {
        var live = Code("colony-live.js");
        Assert.Contains("ants = [];", live);
        Assert.Contains("running[id].forEach(function (t, k)", live);
        Assert.Contains("res.status === 'working'", live);
        foreach (var invented in new[] { "a.work", "beamDur", "lastDrop", "a.drops", "tasks_completed", "demoTopology" })
            Assert.False(live.Contains(invented, StringComparison.Ordinal),
                $"colony-live.js carries '{invented}' — an ant work timer or a seeded route. An ant that "
              + "appears busy without a running task is an animation of work that is not happening.");
    }

    /// <summary>
    /// A CHAMBER'S NAME IS THE PROJECTION'S UNLESS THE OPERATOR OVERRODE IT. The default label comes
    /// with the snapshot (`ColonySector.Label`, "an operator may override it; the id does not move");
    /// an override is presentation, persisted in the layout, and a later scene never overwrites it.
    /// Resetting the layout returns to the server's label, not to a constant in this file.
    /// </summary>
    [Fact]
    public void AChamberLabel_IsTheServers_UnlessTheOperatorOverrodeIt()
    {
        var live = Code("colony-live.js");
        Assert.Contains("s.serverLabel = String(sec.label || s.defLabel).toUpperCase();", live);
        Assert.Contains("if (!s.renamed) s.label = s.serverLabel;", live);
        Assert.Contains("s.label = s.serverLabel || s.defLabel; s.renamed = false;", live);
    }

    /// <summary>
    /// THE LAYOUT LIVES IN /ui/state AND AN OLDER SCHEMA RESETS RATHER THAN MIGRATES. Seats recorded
    /// by an earlier world scale replayed here would land chambers far outside any camera limit; a
    /// layout that does not say it is schema 3, or names a coordinate no camera can reach, is refused.
    /// There is no second store: the renderer emits, the host persists, nothing reads localStorage.
    /// </summary>
    [Fact]
    public void ASavedLayout_IsServerSide_AndAnOlderSchemaIsRefused()
    {
        var live = Code("colony-live.js");
        Assert.Contains("var LAYOUT_SCHEMA = 3;", live);
        Assert.Contains("l.schema !== LAYOUT_SCHEMA", live);
        Assert.Contains("Math.abs(n) <= 1200", live);
        Assert.Contains("emit('layout', layoutSnapshot())", live);
        Assert.False(live.Contains("localStorage", StringComparison.Ordinal),
            "colony-live.js reads or writes localStorage. The layout has one store, /ui/state, through the host.");

        var host = Code("colony-host.js");
        Assert.Contains("colony_live_layout: layout", host);
        Assert.Contains("live.setLayout(saved)", host);
        Assert.Contains("live.on('layout', saveLayout)", host);
    }

    /// <summary>
    /// PICKING IS MEASURED IN PIXELS. A world-space threshold is a huge target up close and a
    /// sub-pixel one at survey distance; the renderer tests the projected screen position it drew.
    /// </summary>
    [Fact]
    public void PickingIsScreenSpace()
    {
        Assert.Contains("Math.hypot(p._q.x - mx, p._q.y - my)", Code("colony-live.js"));
    }

    /// <summary>
    /// A MOUND STOP IS POSTED BY THE HOST AND THEN RE-READ. It posts, re-reads the fleet so the
    /// panel shows the colony's answer, never flips its own flag, and never touches the global stop.
    /// </summary>
    [Fact]
    public void AMoundStop_IsPostedByTheHostAndThenReRead()
    {
        var host = Code("colony-host.js");
        Assert.Contains("function moundStop", host);
        Assert.Contains("stopped ? '/micromound/stop' : '/micromound/stop/resume'", host);
        Assert.Contains("api(path, 'POST', { mound_id: moundId })", host);
        Assert.Equal(3, Regex.Matches(host, @"api\('/micromound/mounds'\)").Count);
        Assert.False(host.Contains("m.stopped = ", StringComparison.Ordinal), "colony-host.js decides a mound's stopped state locally.");
        foreach (var asset in ColonyAssets)
            Assert.False(Code(asset).Contains("/micromound/stop/global", StringComparison.Ordinal),
                $"{asset} reaches for the global stop, which is a file on disk precisely so no API flow can clear it.");
    }

    /// <summary>
    /// THE COMPOSER IS A DOORWAY, NOT A SECOND PIPELINE. §3 still holds — Chat is the one mission
    /// entry. The home page's composer resolves WHERE the conversation lives, sets the hand-off state
    /// Chat already honours, opens Chat and calls Chat's own send. It never creates a conversation
    /// or posts a turn itself, so streaming, refusals, attachments and policy have one implementation.
    /// </summary>
    [Fact]
    public void TheComposer_IsADoorwayToChat_NotASecondPipeline()
    {
        var home = Code("colony-home.js");
        Assert.Contains("chatPendingProjectId = pid; chatActiveId = null; chatComposingNew = true;", home);
        Assert.Contains("go('/chat');", home);
        Assert.Contains("await chatSend(mode);", home);
        foreach (var forbidden in new[] { "/conversations", "/turns", "/missions" })
            Assert.False(home.Contains(forbidden, StringComparison.Ordinal),
                $"colony-home.js names {forbidden}. The composer hands its text to Chat; it does not run its own pipeline.");
        // A plain question runs as a chat turn and never invents a project to run work in.
        Assert.Contains("if (mode === 'mission' && $('ccp-scope') && $('ccp-scope').value === 'q') mode = 'chat';", home);
    }
}
