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
    /// THE REGISTRY OWNS MEMBERSHIP, and that is still the rule after v0.3.8.122 removed the
    /// `unassigned` chamber. The fallback is queen-shaped again, and it is NOT the old bug
    /// returning: the difference is where the decision lives. The server declares the fallback and
    /// says so in the snapshot; the browser reads `fallback_sector` and picks nothing. A client that
    /// hard-codes `|| 'queen'` is guessing about an open set it cannot see, and that is still
    /// forbidden here.
    /// </summary>
    [Fact]
    public void RoleToSectorMembership_ComesFromTheServer_AndTheClientNeverPicksTheFallback()
    {
        var topo = Code("colony-topology.js");

        Assert.DoesNotContain("SECTOR_OF", topo);
        Assert.DoesNotContain("|| 'queen'", topo);
        Assert.DoesNotContain("|| \"queen\"", topo);

        // The membership table is built from the snapshot, and the miss resolves to what the SERVER
        // named — never to a literal this file chose.
        Assert.Contains("st.roleSector", topo);
        Assert.Contains("st.fallbackId", topo);
        Assert.Contains("snap.fallback_sector", topo);

        // The server side of the same rule: one map, beside the registry it maps.
        var projection = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Core", "ColonyLive", "ColonyLiveProjection.cs")));
        Assert.Contains("ByColony", projection);
        Assert.Contains("Fallback", projection);
    }

    /// <summary>
    /// EVERY COLONY THE REGISTRY DECLARES HAS A CHAMBER OF ITS OWN — v0.3.8.122, and this is the
    /// guard that made removing `unassigned` safe rather than convenient.
    ///
    /// The old design routed an unmapped colony to a visible neutral chamber, on the reasoning that
    /// a bucket gets noticed and a plausible placement does not. That reasoning is sound and the
    /// chamber is gone anyway, because THIS fact is a stronger version of the same protection: a new
    /// colony value fails a test at the moment somebody adds it, rather than producing an odd sphere
    /// an operator has to notice and interpret. The fallback is only allowed to point at a real
    /// sector because this test proves nothing reaches it.
    ///
    /// Ranged over the LIVE registry, not over a list repeated here — a guard that keeps its own
    /// copy of the thing it checks is checking its copy.
    /// </summary>
    [Fact]
    public void EveryRegistryColony_MapsToARealChamber_SoTheFallbackIsUnreachable()
    {
        var mapped = ColonySectors.MappedColonies.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declared = AntRegistry.Roles.Select(r => r.Colony)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var unmapped = declared.Where(c => !mapped.Contains(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();
        Assert.True(unmapped.Count == 0,
            "these registry colonies have no chamber and would land on the fallback: "
          + string.Join(", ", unmapped)
          + ". Add each to ColonySectors.ByColony. The `unassigned` chamber that used to catch them "
          + "was removed at v0.3.8.122 precisely because this test replaces it — so this failing is "
          + "the protection working, not a test to relax.");

        // VACUITY FLOOR: the registry was actually read, and every sector named is one that exists.
        Assert.True(declared.Count >= 12, $"only {declared.Count} colonies seen — the sweep found nothing to check");
        var real = ColonySectors.Order.ToHashSet(StringComparer.Ordinal);
        Assert.All(declared, c => Assert.Contains(ColonySectors.ForColony(c), real));
        Assert.DoesNotContain("unassigned", ColonySectors.Order);
    }

    /// <summary>
    /// The records endpoint applies the same rule, and the point is WHERE the decision is made.
    /// An event whose ant the colony does not recognise goes to the server's declared fallback —
    /// stated in the snapshot, so the browser never has to choose. The forbidden thing was never
    /// "the queen"; it was a client picking a default for an open set.
    ///
    /// v0.3.8.123 — AND THE FALLBACK IS MEMORY NOW, WHICH IS NOT AN ARBITRARY SWAP. `.122` sent
    /// these rows to mission control on the argument that mission-level events already live there.
    /// The trouble is what that CLAIMS: the Queen's Core is an authority chamber, so filing a row
    /// there says the colony's command layer produced it — an attribution we have no basis for
    /// about a row whose author is precisely what could not be resolved, and one that makes the
    /// authority chamber look busier than the colony's authority actually was. Memory claims
    /// nothing about who did the work; it says only that the colony stored a row, which is the one
    /// thing about an unattributable record that is definitely true.
    /// </summary>
    [Fact]
    public void TheRecordsEndpoint_FilesAnUnknownAnt_AtTheServersDeclaredFallback()
    {
        var api = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Api", "ColonyLive", "ApiHost.ColonyLive.cs")));

        Assert.Contains("sectorOfRole.GetValueOrDefault(ant, ColonySectors.Fallback)", api);
        // The snapshot tells the client where that is, so it is never inferred.
        Assert.Contains("[\"fallback_sector\"] = ColonySectors.Fallback", api);

        Assert.Equal(ColonySectors.Memory, ColonySectors.Fallback);
        Assert.NotEqual(ColonySectors.Queen, ColonySectors.Fallback);
    }

    /// <summary>
    /// A ROW THE COLONY STORED BELONGS TO MEMORY, WHOEVER WROTE IT. v0.3.8.123.
    ///
    /// Every record used to be filed by its author, and that is right for most of them: a
    /// verification is validation's, a mission outcome is mission control's, and reading them
    /// anywhere else would hide which part of the colony did the work. It is wrong for the rows
    /// where the record IS the colony committing something to memory. Those were scattered across
    /// six chambers by author while MEMORY — the chamber whose whole subject is what the colony
    /// keeps — sat almost empty, because exactly one registry colony maps to it. The operator
    /// noticed and was right: "memory should eventually be one of the most populated chambers."
    ///
    /// Nothing is invented to achieve that. The event type already carries the fact; this reads it
    /// instead of ignoring it. `_recorded` is deliberately excluded — that is the colony noting
    /// that something happened, which stays with whoever it happened to — and the exclusion is
    /// asserted, because a rule that swallowed everything would fill Memory by emptying the rest.
    /// </summary>
    [Fact]
    public void ARecordTheColonyStored_IsFiledInMemory_WhoeverWroteIt()
    {
        Assert.Equal(ColonySectors.Memory, ColonySectors.ForRecordType("artifact_stored"));
        Assert.Equal(ColonySectors.Memory, ColonySectors.ForRecordType("summary_written"));
        Assert.Equal(ColonySectors.Memory, ColonySectors.ForRecordType("memory_candidate"));
        Assert.Equal(ColonySectors.Memory, ColonySectors.ForRecordType("pheromone_scored"));

        // Noting that something happened is not storing it. These keep their author.
        Assert.Null(ColonySectors.ForRecordType("mission_evaluated"));
        Assert.Null(ColonySectors.ForRecordType("verification_bound_to_evidence"));
        Assert.Null(ColonySectors.ForRecordType("patch_recorded"));
        Assert.Null(ColonySectors.ForRecordType(""));
        Assert.Null(ColonySectors.ForRecordType(null));

        // VACUITY FLOOR: every type this rule claims is one the read model actually files as a
        // record. A rule over event types nobody records would be a rule over nothing.
        foreach (var type in new[] { "artifact_stored", "summary_written", "memory_candidate", "pheromone_scored" })
            Assert.True(ColonyLiveProjection.CreatesDurableRecord(type),
                $"'{type}' is routed to Memory but is not a durable record, so the routing is unreachable.");

        // And the endpoint consults the rule BEFORE the author, which is the ordering the whole
        // change rests on: what a record is beats who wrote it, where the type settles the question.
        var api = SourceText.CodeOnly(File.ReadAllText(Path.Combine(
            SourceText.RepoRoot(), "src", "Anthill.Api", "ColonyLive", "ApiHost.ColonyLive.cs")));
        Assert.Contains("ColonySectors.ForRecordType(eventType)", api);
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
            .Where(r => !placed.TryGetValue(r.RoleId, out _))
            .Select(r => $"{r.RoleId} (colony \"{r.Colony}\")")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(stranded.Count == 0,
            "These shipped roles appear in no chamber at all, so Colony Live cannot draw them and "
          + "every record they author is misfiled. `ColonySectors.ByColony` has fallen behind the "
          + "registry: " + string.Join(", ", stranded));

        var strandedWorkers = AntRegistry.Roles
            .SelectMany(r => r.Workers.Select(w => (Role: r, Worker: w)))
            .Where(x => !placed.TryGetValue(x.Worker.WorkerId, out _))
            .Select(x => $"{x.Worker.WorkerId} (under {x.Role.RoleId})")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(strandedWorkers.Count == 0,
            "These shipped workers resolve to no chamber. An event names whichever unit ran, so "
          + "every record they author is misfiled: " + string.Join(", ", strandedWorkers));
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
    /// THE LAYOUT LIVES IN /ui/state; THE WEBGL BUILD'S LAYOUT IS MIGRATED, THE `.115` ONE IS NOT.
    /// Schema 2 (`.116`–`.117`) recorded seats in three.js coordinates on a ±16.5 ring, y up; its
    /// home table is kept verbatim here and an operator's OFFSET from home carries across at ×10
    /// with y flipped, then persists as schema 3 — so an arrangement made in the retired build is
    /// not thrown away. Schema 1's factor was never recorded, so it resets: a guessed factor would be
    /// a fiction dressed as a migration. There is no second store: the renderer emits, the host
    /// persists, nothing reads localStorage.
    /// </summary>
    [Fact]
    public void ASavedLayout_IsServerSide_Schema2Migrates_AndSchema1IsRefused()
    {
        var live = Code("colony-live.js");
        Assert.Contains("var LAYOUT_SCHEMA = 3;", live);
        Assert.Contains("l.schema !== LAYOUT_SCHEMA", live);
        Assert.Contains("Math.abs(n) <= 1200", live);
        // The migration: the retired renderer's home seats, verbatim, and the offset rule.
        Assert.Contains("intel: [-16.5, 0, 16.5]", live);
        Assert.Contains("homelab: [33, 0, 0]", live);
        Assert.Contains("var SCHEMA2_SCALE = 10;", live);
        Assert.Contains("s.defPos[1] - (p[1] - h[1]) * SCHEMA2_SCALE", live);   // y flips: three.js up is this world's −y
        Assert.Contains("if (l && l.schema === 2 && l.sectors)", live);
        Assert.Contains("if (ok2) saveLayout();", live);                        // written back once, as schema 3
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

    /// <summary>
    /// HYDRATION SURVIVES THE SIGN-IN SCREEN. Colony Live enables at DOMContentLoaded, which on a
    /// fresh session is the sign-in page: both bounded reads are refused, and before this guard the
    /// colony then drew an empty sky FOREVER — the operator signed in and got stars and no chambers.
    ///
    /// The fix is a re-attempt, not a poll: hydrate() is idempotent (a snapshot already applied makes
    /// it a no-op), guarded against overlap, and re-attempted on the two things that mean the session
    /// changed — the page being entered, and the first event arriving on the stream. Anything on a
    /// timer here would be the polling this feature exists to avoid.
    /// </summary>
    [Fact]
    public void Hydration_IsReAttemptedAfterSignIn_AndIsNeverPolled()
    {
        var host = Code("colony-host.js");

        // Idempotent and non-overlapping: the guard clause names both conditions.
        Assert.Contains("function hydrated()", host);
        Assert.Contains("if (typeof api !== 'function' || !topo || hydrating || hydrated()) return;", host);
        Assert.Contains("hydrating = true;", host);

        // A refused snapshot is a FAILED hydration, so the retry still has work to do: the chain
        // throws rather than applying an empty body and calling the colony hydrated.
        Assert.Contains("if (!body || !body.sectors) throw new Error", host);

        // The three triggers, and the door each uses.
        Assert.Contains("if (!hydrated()) hydrate();", host);
        Assert.Contains("hydrate: hydrate,", host);
        Assert.Contains("ColonyHost.hydrate()", Code("colony-home.js"));

        // v0.3.8.122 — AND THE SECOND SIGN-IN. `.120` covered the first: `startPolling()` runs
        // `restoreLayout()`, which navigates to the colony and fires `PAGE_ENTER`. But
        // `pollingStarted` is set once per page load and never reset, so a session that lapsed and
        // was signed back into took the guarded branch, never re-entered the page, and left a colony
        // whose refused reads were retried only by the first colony event — which on an idle colony
        // may never arrive. `enterApp` is the one function every sign-in path reaches.
        var app = Code("app.js");
        Assert.Matches(new Regex(@"function enterApp\(\)\{(?:(?!\n\}).)*ColonyHost\.hydrate\(\)", RegexOptions.Singleline), app);

        // ... and nothing on a clock anywhere in the feature.
        foreach (var asset in ColonyAssets)
            foreach (var timer in new[] { "setInterval", "setTimeout(hydrate" })
                Assert.False(Code(asset).Contains(timer, StringComparison.Ordinal),
                    $"{asset} uses {timer}. Colony Live reads on a trigger, never on a clock.");
    }

    /// <summary>
    /// A MOUND CHAMBER IS A LABEL, AND A LABEL REACHES NO DEVICE — v0.3.8.122, revised .123.
    ///
    /// `+ Mound` adds a chamber to the operator's colony immediately, drawn with the roster every
    /// mound runs. The whole feature rests on one separation: the chamber's name, its colour, and
    /// its ants' names are PRESENTATION, stored in the operator's saved layout, and never sent
    /// anywhere. A mound is enrolled by one-time token and keeps answering under its own identity
    /// whatever the colony calls it — which is what lets an operator label a fleet for their own use
    /// case without touching what the devices are.
    ///
    /// WHERE THE ROSTER COMES FROM CHANGED AT .123, AND THAT IS THE POINT OF THIS REVISION. `.122`
    /// served it from `/micromound/roster/defaults`, inside `#if MICROMOUND`, behind
    /// `read_micromound`, fetched from inside the fleet listing's own `.then`. Four conditions had
    /// to hold before seven presentation labels appeared, and when any of them did not the operator
    /// got a mound chamber with nothing in it — which is exactly what they reported. None of those
    /// conditions is about authority, so the roster moved to `Anthill.SDK.Modules.MoundRoster`,
    /// served at `/colony/mound-roster`, always mapped and guarded like the rest of the picture.
    ///
    /// What did NOT change is that there is one store. `MicromoundRoster` forwards to the SDK list
    /// rather than declaring its own, so `RosterProjectionTests` still checks the whole chain
    /// against the device runtime by compiled inspection.
    /// </summary>
    [Fact]
    public void AnOperatorAddedMound_IsDrawnFromTheServersRoster_AndItsLabelsGoNowhere()
    {
        var live = Code("colony-live.js");
        var host = Code("colony-host.js");
        var home = Code("colony-home.js");

        // The roster is FETCHED, never spelled out here. If any of the seven names appears in the
        // CODE of a console asset, someone has started a second copy of the device's roster.
        //
        // Comments are stripped first, and deliberately: `colony-topology.js` explains that the wire
        // value `edge_queen` is displayed as "Mound Major", which is a note about a mapping and not
        // a store of the roster. A guard that cannot tell those apart forces the next author to
        // delete a useful sentence to make a test pass, which is how comments stop being written.
        foreach (var ant in new[] { "Mound Major", "Scout Ant", "Forager Ant", "Guard Ant", "Witness Ant", "Cache Ant", "Runner Ant" })
            foreach (var asset in ColonyAssets)
                Assert.False(Code(asset).Contains(ant, StringComparison.Ordinal),
                    $"{asset} names the mound ant \"{ant}\" in code. That roster has one source — "
                  + "Anthill.SDK.Modules.MoundRoster, served at /colony/mound-roster — and a copy "
                  + "here is the second store of one fact that its own header warns about.");

        // ITS OWN FETCH, NOT THE FLEET'S PASSENGER. Nested inside `/micromound/mounds` the roster
        // arrived only when a device listing already had; that is the bug this line pins closed.
        Assert.Contains("api('/colony/mound-roster')", host);
        Assert.DoesNotContain("/micromound/roster/defaults", host);
        Assert.Contains("setMoundDefaults", live);
        Assert.Contains("moundDefaults", live);

        // Added chambers survive a reload, and the snapshot cannot switch them off — the server has
        // never heard of them.
        Assert.Contains("mounds: mounds", live);
        Assert.Contains("if (s.added) s.present = true;", live);

        // Only the operator's own chambers can be deleted. A registry sector is refused in the
        // renderer, not merely hidden in the page: a button that exists to be refused is worse than
        // no button, and hiding is not enforcing. The registry now LISTS every mound (.123 —
        // infrastructure belongs on the page that lists mounds), so `removable` is what the row
        // renders and `added` is still what the renderer enforces.
        Assert.Contains("if (!s2 || !s2.added) return false;", live);
        Assert.Contains("removable: !!x.added", live);
        Assert.Contains("m.removable", home);
        Assert.Contains("return SEC.filter(function (x) { return x.mound; })", live);

        // DELETING BELONGS TO THE FLEET VIEW, NOT TO ONE CHAMBER'S PANEL. `+ Mound` makes as many
        // chambers as an operator wants, so there is no single mound for a settings page to mean —
        // the registry is the list, and removing one is a fleet-level act you should not have to be
        // standing inside the thing to perform. A chamber's own panel offers the door to it.
        Assert.Contains("act === 'moundremove'", home);
        Assert.Contains("go('/colony/mounds')", home);
        Assert.Contains("PAGE_ENTER['mounds'] = renderMounds", home);
        Assert.Contains("id=\"page-mounds\"", Raw("index.html"));

        // AND THE REGISTRY'S BUTTONS HAVE TO REACH A HANDLER. v0.3.8.123: `onAct` was bound to
        // `#page-colony` alone while the registry lives in `#page-mounds`, so Delete emitted a click
        // that reached nothing at all — not a broken delete, an unlistened one. This is the line
        // whose absence made a whole page inert, which is why it is guarded rather than assumed.
        Assert.Contains("mpage.addEventListener('click', onAct)", home);

        // And one mound's settings carry WHICH mound. A destination that cannot say which is the
        // reason this was rebuilt: `window.micromoundPendingId` is the console's established shape
        // for that, the same one the project pickers use. v0.3.8.124 — the id now travels from the
        // REGISTRY ROW rather than from a chamber click, the chamber having stopped being a door.
        Assert.Contains("window.micromoundPendingId = id", home);
        Assert.Contains("openMoundSettings(b.dataset.moundId", home);

        // BOTH DESTINATIONS DELETE, AND A DELETE ANYWHERE TAKES THE CHAMBER OUT OF THE COLONY AT
        // ONCE. An operator who removes a mound and then finds it still drawn has been told a lie
        // by one of the two surfaces, so the settings page calls the same `removeMound` the registry
        // does rather than keeping a second notion of what exists.
        var mm = Code("micromound.js");
        Assert.Contains("live.removeMound(chamber.id)", mm);
        Assert.Contains("window.micromoundPendingId", mm);
        Assert.Contains("mm-chamber", Raw("index.html"));

        // The button adds a chamber rather than navigating away, which is what it used to do.
        Assert.Contains("lm.addMound()", home);
        Assert.DoesNotContain("act === 'addmound') { go(", home);

        // VACUITY FLOOR: the API this guard reasons about is actually exposed.
        Assert.Contains("addMound: function (label)", live);
        Assert.Contains("removeMound: function (id)", live);
    }

    /// <summary>
    /// THE COLONY READS ITSELF AT THE ZOOM YOU ARE AT — v0.3.8.122, and ONE SETTING AT .123.
    ///
    /// Every chamber name was drawn at every distance, so the survey was a wall of text nobody was
    /// reading and the detail an operator actually wanted — which ants, which records — was never
    /// shown at all. `.122` answered that with a zoom-driven mode and kept the old always-on
    /// behaviour beside it as `fixed`, offering both.
    ///
    /// THE CHOICE WAS THE PROBLEM. The operator's reply was direct: "i dont want a 'all on zoom'
    /// option, i just want it that when all is selected, when you zoom into a chamber, allll the
    /// little dots that can be clicked on, show some sort of label on them." So `All` IS the zoom
    /// behaviour now, there is no separate setting for it, and tier 3 labels EVERY point
    /// `pickPoint` would accept rather than only the ones a link happens to join — a dot you can
    /// click is a dot you should be able to read.
    ///
    /// Two properties are worth guarding beyond the mapping. The thresholds are multiples of the
    /// chamber's OWN radius rather than absolute distances: a fixed distance makes a small chamber
    /// surrender its name while a large one is still silent, and the two look like a bug rather
    /// than a rule. And tier 3 sits inside `pickPoint`'s own 7.5R reach, which is what makes
    /// "labelled" and "clickable" the same set instead of two sets that nearly agree.
    /// </summary>
    [Fact]
    public void EveryClickableDot_NamesItself_OnceYouHaveZoomedIn()
    {
        var live = Code("colony-live.js");

        Assert.Contains("function labelTier(s, isFocused)", live);
        Assert.Contains("cam.dist < s.R * 3 && isFocused) return 3", live);
        Assert.Contains("cam.dist < s.R * 5.5 && isFocused) return 2", live);
        Assert.Contains("cam.dist < s.R * 9.5) return 1", live);

        // Tier 3 no longer filters by whether a link touched the point. That filter WAS the
        // complaint, so its absence is the assertion.
        Assert.Contains("tier >= 3 && p.rec && p.rec.title", live);
        Assert.DoesNotContain("tier >= 3 && p.linked", live);

        // Every labelled dot is inside the distance at which a dot can be clicked. If either
        // constant moves without the other, the view starts labelling things nothing will select.
        Assert.Contains("cam.dist > s.R * 7.5) return null;", live);

        // Workers are labelled at tier 2 with no mode left to condition it on.
        Assert.Contains("if (res && tier >= 2 && m > .25)", live);
        Assert.DoesNotContain("opts.labels === 'zoom' || !res.worker", live);

        // Every retired spelling heals, at BOTH ends, so a remembered value cannot leave the select
        // and the renderer disagreeing about what the operator chose — or leave the select blank,
        // which is what assigning a value it no longer offers would do.
        Assert.Contains("if (o.labels === 'normal' || o.labels === 'fixed' || o.labels === 'zoom') o.labels = 'all';", live);
        Assert.Contains("/^(normal|fixed|zoom)$/.test(v.labels) ? 'all'", Code("colony-home.js"));
        Assert.Contains("labels: 'all'", live);

        var html = Raw("index.html").Replace("\r\n", "\n");
        Assert.Contains("value=\"all\" selected", html);
        Assert.DoesNotContain("value=\"zoom\"", html);
        Assert.DoesNotContain("value=\"fixed\"", html);

        // VACUITY FLOOR: the tier is consulted where labels are actually drawn.
        Assert.Contains("var tier = labelTier(s, isFocused);", live);
        Assert.Contains("if (tier >= 1) {", live);
    }

    /// <summary>
    /// THE LINKAGE IS THE OPERATOR'S TO DIM — v0.3.8.122. It was hard-coded "almost transparent"
    /// (.045 focused, .022 otherwise) with no way to see the lines or to hide them. The range now
    /// runs 0 (dots alone) to 1 (solid), and the DEFAULT reproduces the old look exactly, which is
    /// the property that makes this a new control rather than a restyle: an operator who never
    /// touches the slider sees no change at all.
    /// </summary>
    [Fact]
    public void LinkageOpacity_IsAnOperatorControl_AndItsDefaultChangesNothing()
    {
        var live = Code("colony-live.js");

        Assert.Contains("links: { opacity: .125 }", live);
        Assert.Contains("clampNum(opts.links.opacity, 0, 1, .125) * (isFocused ? .36 : .18)", live);
        // 0 draws no line at all rather than a line nobody can see.
        Assert.Contains("if (linkA > .002)", live);
        // The old constants are gone, so there is one answer to how bright a link is.
        Assert.DoesNotContain("(isFocused ? .045 : .022)", live);

        Assert.Contains("clb-linkalpha", Raw("index.html"));
        Assert.Contains("clb-linkalpha", Code("colony-home.js"));

        // .125 * .36 == .045 and .125 * .18 == .0225: the pre-.122 look, to three decimals.
        Assert.Equal(.045, .125 * .36, 3);
        Assert.Equal(.022, .125 * .18, 2);
    }

    /// <summary>
    /// A MOUND CHAMBER IS A CHAMBER — v0.3.8.124, reversing .122.
    ///
    /// `.122` made a mound's second click a door to its settings page. The intent was reachability;
    /// the effect was that the one chamber an operator most wanted to recolour and rename was the
    /// one where a second click threw them out of the colony view, taking the panel they were using
    /// with it. Every other chamber rewards a second click by staying put.
    ///
    /// Settings live in ONE place now — the mound registry — reachable from the Mounds button beside
    /// `+ Mound` and from the chamber's own panel. This guards the reversal at both ends: the
    /// renderer emits no settings event, and the page listens for none.
    ///
    /// The parity assertions below are the part that was always right and stays: a mound reaches the
    /// sector panel through the same `sector` event as every other chamber, so customization is not a
    /// special case that could quietly stop applying to it.
    /// </summary>
    [Fact]
    public void AMoundChamber_IsAnOrdinaryChamber_AndItsSettingsLiveInTheRegistry()
    {
        var live = Code("colony-live.js");
        var home = Code("colony-home.js");

        // No door in the renderer, and no listener on the page. Both, because either alone would
        // leave half a feature that reads as working.
        Assert.DoesNotContain("emit('moundsettings'", live);
        Assert.DoesNotContain("live.on('moundsettings'", home);

        // The one way in is the registry, and the button beside `+ Mound` opens it.
        Assert.Contains("if (act === 'mounds') { go('/colony/mounds'); return; }", home);
        Assert.Contains("act === 'moundopen'", home);

        // Both chambers that present as mounds are flagged as such, in the sector table.
        Assert.Contains("id: 'mound', label: 'MICROMOUND', mound: true", live);
        Assert.Contains("id: 'homelab', label: 'INFRASTRUCTURE', mound: true", live);
        // HOMELAB is renamed at BOTH ends, and the server's label is the one that wins.
        Assert.Contains("[Homelab] = \"INFRASTRUCTURE\"", SourceText.CodeOnly(File.ReadAllText(
            Path.Combine(SourceText.RepoRoot(), "src", "Anthill.Core", "ColonyLive", "ColonyLiveProjection.cs"))));

        // Customization parity is not a special case: the sector panel is generic and a mound reaches
        // it through the same `sector` event as everything else. If that ever stops being true this
        // asserts the panel still reads its style from the renderer rather than from a branch.
        Assert.Contains("live.getSectorStyle(s.id)", home);
    }

    /// <summary>
    /// INFRASTRUCTURE'S SETTINGS ARE INFRASTRUCTURE'S — v0.3.8.124.
    ///
    /// It is a mound in every way the registry cares about: drawn as one, conduited to the Queen
    /// like one, listed beside the others. What it is NOT is a micromound — no device, no enrolment
    /// token, no charter — so the micromound console had nothing to show for it and would have
    /// offered an operator a form for hardware that does not exist.
    ///
    /// So the destination is decided PER ROW rather than per page, and the row that is not a device
    /// goes to the page where its eight real roles have always been configured. That page also loses
    /// its card on Integrations, because two doors would make the registry's own promise — "every
    /// mound, and where its settings are" — a half-truth.
    /// </summary>
    [Fact]
    public void TheInfrastructureRow_OpensInfrastructure_NotAMicromoundForm()
    {
        var home = Code("colony-home.js");
        var app = Code("app.js");

        // The registry sends this row somewhere else, and says where.
        Assert.Contains("infraopen", home);
        Assert.Contains("go('/tools/infrastructure')", home);

        // The router can resolve that, without a nav entry — the registry is the way in.
        Assert.Contains("ROUTE_TABLE['/tools/infrastructure']", app);

        // And the Integrations card is gone at both ends: the markup and its handler.
        Assert.DoesNotContain("data-int-hl", app);
        Assert.DoesNotContain("showPage('homelab'", app);

        // VACUITY FLOOR: the registry really does list infrastructure, which is the premise the
        // whole guard rests on. `listMounds` returns every chamber flagged `mound`, not only added.
        Assert.Contains("return SEC.filter(function (x) { return x.mound; })", Code("colony-live.js"));
    }

    /// <summary>
    /// NOTHING HORIZONTAL IS DRAWN UNDER THE COLONY — v0.3.8.122.
    ///
    /// The renderer drew a lit floor at y=340: a wide faint disc, three unseen lights glinting off
    /// it, and one coloured pool per chamber cast down onto it. Two environments used it, and one
    /// (`plane`) existed only to show it. Every one of those marks is a horizontal surface, so each
    /// silently declared a DOWN — the colony acquired a floor, the floor bounced light back onto the
    /// chambers, and the camera could not be taken below it without drawing the colony through its
    /// own ground.
    ///
    /// This guard is paired with the one below and they are not separable: the free orbit is only
    /// coherent because there is no privileged direction left to be on the wrong side of. A future
    /// session adding "just a faint floor for depth" re-creates both defects at once, which is why
    /// the names of all five removed symbols are listed rather than only the drawing call.
    /// </summary>
    [Fact]
    public void TheColonyIsSuspended_WithNothingDrawnBeneathIt()
    {
        var live = Code("colony-live.js");

        foreach (var gone in new[] { "PLANE_Y", "planePool", "envGround", "camPos", "LIGHTS" })
            Assert.False(live.Contains(gone, StringComparison.Ordinal),
                $"colony-live.js still references `{gone}`. The ground plane was removed at .122 — "
                + "a floor is what gave the colony a down, bounced light back onto the chambers, and "
                + "stopped the camera going underneath. A dimmer floor is still a floor.");

        // The environment it existed for is gone from the renderer, the page and the menu, and a
        // browser that remembers it heals rather than falling through to a default it never chose.
        Assert.DoesNotContain("<option value=\"plane\">", Raw("index.html").Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("if (opts.env === 'plane') opts.env = 'void';", live);
        Assert.Contains("if (v === 'plane') { v = 'void';", Code("colony-home.js"));

        // VACUITY FLOOR: the environment switch this guard is making a claim about still exists,
        // and still has the branch a removed `plane` now heals into.
        Assert.Contains("function drawEnv(ts)", live);
        Assert.Contains("opts.env === 'void'", live);
    }

    /// <summary>
    /// THE CAMERA ORBITS THROUGH THE WHOLE SPHERE — v0.3.8.122.
    ///
    /// Pitch was clamped to [0.05, 1.15] rad — roughly 3° to 66°, a band chosen to keep the camera
    /// above the ground plane and tilted down at it. With the plane gone the clamp was the only
    /// thing left asserting an up, and the operator asked to be able to go under the colony.
    ///
    /// What makes the unclamp SAFE is not the drag line, it is the painter's sort: chambers are
    /// projected and drawn back-to-front by camera-space depth every frame, so a view from below is
    /// composited in the right order rather than inside-out. That sort is asserted here, in the same
    /// guard, because removing it would not break any test that talks about the camera and would
    /// turn every angle past the horizon into a silent mess.
    /// </summary>
    [Fact]
    public void TheCameraPitch_IsFree_AndDepthStillSortsBackToFront()
    {
        var live = Code("colony-live.js");

        // The drag assigns pitch the way it assigns yaw: no clamp on either.
        Assert.Contains("goal.pitch = drag.pitch + dy2 * .003;", live);
        Assert.Contains("goal.yaw = drag.yaw + dx2 * .0035;", live);
        Assert.DoesNotContain("goal.pitch = Math.max(", live);
        Assert.DoesNotContain("goal.pitch = Math.min(", live);

        // Whole turns are dropped from BOTH angles together before a reset eases home, so the reset
        // never unwinds a revolution the operator did not ask for. Shifting cam and goal by the same
        // multiple of 2π leaves the rendered orientation identical, which is why it is safe at all.
        Assert.Contains("function unwind()", live);
        Assert.Contains("cam.pitch -= k * t; goal.pitch -= k * t;", live);
        Assert.Contains("cam.yaw -= k * t; goal.yaw -= k * t;", live);
        Assert.Matches(new Regex(@"survey:\s*function\s*\(\)\s*\{[^\n]*unwind\(\);"), live);

        // The backdrop's parallax is trigonometric, so an unbounded angle cannot slide it off the
        // canvas and leave a bare gradient behind. Linear offsets were correct only while the angles
        // were clamped, and yaw never was.
        Assert.Contains("Math.sin(cam.pitch) * 40", live);
        Assert.Contains("Math.sin(cam.yaw) * 24", live);

        // The invariant the free orbit rests on: back-to-front by camera-space depth.
        Assert.Contains("sort(function (a, b) { return b.pr.zc - a.pr.zc; })", live);

        // VACUITY FLOOR: the drag handler and the projection this guard reasons about are both here.
        Assert.Contains("function onMove(e)", live);
        Assert.Contains("function proj(p)", live);
    }

    /// <summary>
    /// EVERY MOUND HANGS OFF THE QUEEN, AND WHICH MOUNDS EXIST CHANGES AT RUNTIME. v0.3.8.123.
    ///
    /// `.122` had exactly one authority strand and it was hard-coded `queen → mound`.
    /// INFRASTRUCTURE had none and an operator-added chamber had none — so the two kinds of mound
    /// an operator actually ends up with floated unattached while the one built-in placeholder was
    /// wired. That is not cosmetic. A conduit in this view is the statement that a chamber answers
    /// to the Queen, and a mound that takes charters from her and shows no strand is the console
    /// contradicting what the colony does.
    ///
    /// The fix is that the strands are DERIVED from the sector table rather than listed, which is
    /// what this guards: adding a mound wires it, removing one drops its strand, and nothing has to
    /// remember to keep a second list in step. A hard-coded pair reappearing here is the bug coming
    /// back, so its absence is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void EveryMoundHangsOffTheQueen_IncludingTheOnesAnOperatorAdds()
    {
        var live = Code("colony-live.js");

        // Derived from the table, not a list. `s.mound` is the flag every kind of mound carries:
        // infrastructure, the fleet chamber, and each one an operator added.
        Assert.Contains("function rebuildAuthorities()", live);
        Assert.Contains("if (!s.mound) return;", live);
        Assert.Contains("authorities[s.id] = mkRoot('queen', s.id, 1, 20)", live);

        // The single hard-coded strand is gone, at both ends — the root and its particle stream.
        Assert.DoesNotContain("mkRoot('queen', 'mound', 1, 20)", live);
        Assert.DoesNotContain("if (bySec.mound.present) authority.strands", live);

        // Removing a mound drops its conduit rather than leaving a strand to a chamber that is not
        // there, and a NEW strand gets particles in the same breath: a line with nothing moving on
        // it reads as dead, which is the opposite of what a conduit is for.
        Assert.Contains("if (!want[id]) { delete authorities[id]; changed = true; }", live);
        Assert.Contains("if (ch) buildStreams();", live);

        // Drawn only for a chamber that is on screen.
        Assert.Contains("if (!bySec[id] || !bySec[id].present) return;", live);

        // The device ring marks every mound too, not just the built-in one — it is the mark that
        // says "this one is hardware", and that claim is the same for all three kinds.
        Assert.Contains("if (s.mound) {", live);
        // The STOPPED suffix moved with it. `else if (s.id === 'mound')` still exists one function
        // away — that one reads the SERVER'S fleet chamber out of the snapshot, which is a different
        // question from "is this hardware" — so the assertion is on what the label now says rather
        // than on the absence of a substring that legitimately survives elsewhere.
        Assert.Contains("s.label + (s.mound && s.stopped ? ' \u00b7 STOPPED' : '')", live);

        // VACUITY FLOOR: the draw loop this guard reasons about is here and does consult them.
        Assert.Contains("function eachAuthority(fn)", live);
        Assert.Contains("eachAuthority(function (a, id) {", live);
    }

    /// <summary>
    /// THE COLONY IS ARRANGED, NOT SCATTERED — v0.3.8.123.
    ///
    /// Two pieces of deliberate noise had been sitting in the seat layout since the renderer was
    /// written: a phase offset and a `sin(ri * 2.4)` vertical wobble on the role orbs, and an
    /// alternating zigzag on the workers. Both existed to stop orbs overlapping and both worked, at
    /// the cost of a chamber that reads as a handful of ants dropped in rather than a colony
    /// arranged in one. Records had the same problem from the other direction: the LATTICE they sit
    /// on is perfectly even, and a per-record continuous radius was the one thing scattering it.
    /// The operator's word for the result was "method and symmetry and less madness".
    ///
    /// THE QUEEN IS THE EXCEPTION, AND SHE IS THE EXCEPTION EVERYWHERE ELSE TOO. Her chamber is the
    /// authority chamber and she is not a peer of the six around her; drawing her as one seat among
    /// them on the same ring said otherwise. She sits at the centre at nearly double size and the
    /// ring closes around her.
    ///
    /// What must NOT change is that a record keeps its seat as the chamber fills up around it —
    /// that is why the shells are chosen by the record's own durability and the slot by its own id,
    /// rather than by its position in whatever order the page happened to receive.
    /// </summary>
    [Fact]
    public void ChambersAreArrangedWithMethod_AndTheQueenSitsAtTheCentreOfHers()
    {
        var live = Code("colony-live.js");

        // The Queen: centred, larger, and identified by the registry id rather than by her position
        // in the resident list — an ordering the server owns and this file must not depend on.
        Assert.Contains("String(r.roleId || '').toLowerCase() === 'queen'", live);
        Assert.Contains("base = queenSeat ? [0, 0, 0]", live);
        Assert.Contains("sz: queenSeat ? 4.4 : 2.4", live);
        // The ring is counted WITHOUT her, so six around a centre is an even six and not a gap.
        Assert.Contains("resList.filter(function (r) { return !isQueenSeat(r); }).length", live);

        // The jitter is gone: an even ring from a fixed start, and no vertical wobble.
        Assert.Contains("-Math.PI / 2 + (queenSeat ? 0 : (ringI / ringN) * TAU)", live);
        Assert.DoesNotContain("Math.sin(ri * 2.4)", live);
        Assert.DoesNotContain("(wi % 2 ? .08 : -.08)", live);

        // Workers fan around their own parent, spread so one role's arc cannot reach its
        // neighbour's however many workers it has.
        Assert.Contains("var pitch = TAU / ringN", live);

        // Records sit in shells rather than at a continuous radius, and which shell is still
        // decided by the record's own durability — so its seat is as stable as it ever was.
        Assert.Contains("var shell = verified ? 0 : durable > .34 ? 1 : 2;", live);
        Assert.Contains("s.R * [.34, .62, .84][shell]", live);
        Assert.Contains("unit(id, 'slot')", live);

        // VACUITY FLOOR: this is the function that actually seats a chamber's contents.
        Assert.Contains("function rebuildSector(s, sec)", live);
        Assert.Contains("LATTICE.push(", live);
    }

    /// <summary>
    /// LIGHT MODE IS NOT THE DARK ONE WITH A WHITE SKY — v0.3.8.123.
    ///
    /// The console's light theme is paper, and `.122` already knew that for the chamber envelopes:
    /// a pale halo that reads as depth against black reads as a smudge on white, so the light
    /// envelope was rebuilt as a tint with a rim. Two highlights were missed. An ant's core and a
    /// sphere's key-light bloom were both near-white in BOTH environments, which is right against
    /// the galaxy and an eye sore against the page — the operator's words were that the inside of
    /// the ants being "hued with white or lighter shade" is "kind of an eye sore with how bright it
    /// is."
    ///
    /// A core exists to make an ant read as lit from within rather than as a flat disc, and on a
    /// light ground the way to say "lit from within" is CONTRAST, not more white. So both invert
    /// with the environment. The shape reads the same in either; only one of them reads at all on
    /// paper.
    /// </summary>
    [Fact]
    public void LightModeInvertsItsHighlights_InsteadOfPilingWhiteOnWhite()
    {
        var live = Code("colony-live.js");

        // The ant core, and the specular bloom on a chamber.
        Assert.Contains("isLight() ? 'rgba(20,28,42,'", live);
        Assert.Contains("hc = isLight() ? '44,58,78' : '235,240,250'", live);

        // Neither is unconditionally pale any more. These were the two literals that were.
        Assert.DoesNotContain("isLight() ? 'rgba(255,255,255,' + (alpha * .9)", live);
        Assert.DoesNotContain("hg.addColorStop(0, 'rgba(235,240,250,'", live);

        // The mound's device ring darkens on paper for the same reason.
        Assert.Contains("isLight() ? 'rgba(64,78,98,'", live);

        // VACUITY FLOOR: the environment predicate exists and the light page is still a real one.
        Assert.Contains("function isLight() { return opts.env === 'light'; }", live);
        Assert.Contains("value=\"light\"", Raw("index.html"));
    }

    /// <summary>
    /// A SIMPLE PAGE OVER A COMPLETE-REPLACEMENT DOCUMENT MUST NOT DELETE WHAT IT CANNOT SHOW.
    /// v0.3.8.123 — the console half of `MicromoundAuthoring`.
    ///
    /// The Micromound console asked an operator to type a charter: capability ids, an action-class
    /// enum, a lease TTL in seconds, a `device_limits` map keyed by capability, evidence globs.
    /// Every one is real and none is a question a person can answer, which is what the operator
    /// meant by "less of a json file communicated as settings."
    ///
    /// Three properties make the friendly page safe rather than merely nicer, and all three are
    /// guarded here. It TRANSLATES rather than deciding — no ceiling, limit or policy is derived in
    /// the browser, because a second browser-side idea of what a charter means is how the sector map
    /// drifted and had to be moved server-side at `.115`. It offers only what the DEVICE reported,
    /// so a form that saves is a form the mound can accept. And it CARRIES what it cannot author,
    /// because a manifest and a charter are complete replacements and a save from the simple page
    /// writes the whole document.
    /// </summary>
    [Fact]
    public void TheFriendlySetUpPage_TranslatesRatherThanDeciding_AndNeverDeletesWhatItCannotShow()
    {
        var mm = Code("micromound.js");
        var html = Raw("index.html");

        // It reads and posts; it does not compute. If the browser ever starts naming an action
        // class or building a limits map, the translation has grown a second home.
        Assert.Contains("api('/micromound/authoring/' + encodeURIComponent(mmSelected))", mm);
        Assert.Contains("api('/micromound/authoring/preview', 'POST', mmSetup)", mm);
        Assert.Contains("mmPost('/micromound/authoring', mmSetup, 'Set-up')", mm);

        // THE FORM IS POSTED VERBATIM. `mmSetup` is whatever the server projected, edited in place
        // and sent back whole — there is no assembly step in between, which is the property that
        // stops the browser growing a second opinion about what a charter means. Reshaping it here
        // would be the drift that put the sector map on the server at `.115`.
        Assert.Contains("mmSetup[key] = el.type === 'number' ? Number(el.value) : el.value;", mm);
        Assert.Contains("row[field] = el.type === 'number'", mm);
        // A blank number is ABSENT, not zero: "never above 0" is a real bound, so the two have to
        // stay distinguishable all the way to the compile.
        Assert.Contains("el.value.trim() === '' ? null : Number(el.value)", mm);

        // Only what the device reported is offered.
        Assert.Contains("const reported = meta.reported || [];", mm);
        Assert.Contains("reported.filter(c => used.indexOf(c) < 0)", mm);

        // Carried untouched, and named on screen. Carrying something silently and losing it
        // silently are one bug apart, so the page does both.
        Assert.Contains("(meta.unrepresented || []).forEach", mm);
        Assert.Contains("mmSetup = d.form || null;", mm);

        // The compiled documents are SHOWN rather than hidden. Nothing about this feature is about
        // keeping the contract from the operator — what changed is who writes it.
        Assert.Contains("What this will actually issue", mm);

        // The raw forms are still reachable, folded rather than deleted, for everything the simple
        // vocabulary cannot say.
        Assert.Contains("mm-adv", html);
        Assert.Contains("Advanced &mdash; the raw charter and manifest", html);
        Assert.Contains("id=\"mm-setup\"", html);

        // Which of the seven holds a capability comes from the server, not from a copy here.
        Assert.Contains("mmCap(id).default_ant", mm);

        // VACUITY FLOOR: the card actually renders and binds.
        Assert.Contains("function mmSetupRender()", mm);
        Assert.Contains("function mmSetupBind()", mm);
        Assert.Contains("mmSetupLoad();", mm);
    }
}
