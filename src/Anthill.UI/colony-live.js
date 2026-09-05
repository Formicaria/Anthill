/* ─────────────────────────────────────────────────────────────────────────────
   COLONY LIVE — the underground neural formicarium renderer.
   Vanilla JS, canvas-2D with a real 3D projection (no framework, no CDN, no
   bundler; CSP-safe: served as its own asset like app.js).

   One-world/one-renderer: create ONE instance and re-parent its root element
   between Colony page, Dashboard widget, and Chat's colony layer — exactly the
   discipline the existing canvas has.

   Boundary rule: this file renders. It never decides, and it never fetches. All
   state arrives through ColonyLive.setTopology(scene) — the scene colony-topology.js
   projects from the Colony Live read model (/colony/live/snapshot, /colony/live/records,
   the event stream, /graph, the approvals poll, the fleet listing). Nothing on screen
   exists that the scene did not put there: a chamber's grains are its RECORDS, its orbs
   are its registry RESIDENTS, and an empty chamber is a fact, drawn empty.

   Public API:
     const live = ColonyLive.create();
     live.mount(containerEl);          // creates canvas + overlays inside
     live.unmount();
     live.setTopology(scene);          // projection from colony-topology.js
     live.survey(); live.focus(id); live.followMission(); live.resetView();
     live.destroy();
   ───────────────────────────────────────────────────────────────────────────── */
(function () {
  'use strict';
  var TAU = Math.PI * 2;
  function h2(h) { return [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)]; }
  function V(a, b, t) { return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t]; }
  function lcg(seed) { var s = seed; return function () { s = (s * 16807) % 2147483647; return (s - 1) / 2147483646; }; }

  // Stable spatial grammar (design doc §3). Positions are constants, never simulated.
  var SECTOR_DEFS = [
    { id: 'queen', label: "QUEEN'S CORE", color: '#e21f7b', core: '#e8b25a', pos: [-90, -10, 0], R: 95, n: 460, rot: .000037 },
    { id: 'intel', label: 'INTELLIGENCE', color: '#5ec4cf', core: '#8fd8df', pos: [-300, -170, 40], R: 62, n: 260, rot: .00005 },
    { id: 'forge', label: 'FORGE', color: '#c97a3d', core: '#e0a06a', pos: [120, -190, -30], R: 64, n: 260, rot: -.00004 },
    { id: 'valid', label: 'VALIDATION', color: '#c25f6e', core: '#d98a96', pos: [310, -20, 50], R: 58, n: 230, rot: .000045 },
    { id: 'memory', label: 'MEMORY', color: '#d9b054', core: '#ecd39a', pos: [210, 180, -40], R: 60, n: 250, rot: -.00003 },
    { id: 'output', label: 'OUTPUT', color: '#8f78c9', core: '#b3a0e0', pos: [-180, 200, -70], R: 56, n: 220, rot: .00004 },
    { id: 'homelab', label: 'INFRASTRUCTURE', mound: true, color: '#5aa07a', core: '#9ad4b0', pos: [-330, 90, -40], R: 48, n: 0, rot: .00004 },
    { id: 'mound', label: 'MICROMOUND', mound: true, color: '#a55a7e', core: '#c9cfdc', pos: [-95, 265, 70], R: 34, n: 110, rot: .00006 }
  ];
  // Ids are the server's (ColonySectors); labels are its DEFAULTS, overridable per operator in the
  // persisted layout. Positions are constants — a stable spatial grammar — until the operator drags.
  var SECTOR_ORDER = ['queen', 'intel', 'forge', 'valid', 'memory', 'output', 'homelab', 'mound'];
  // One strand per root. The Queen's spokes are always drawn (faint); the inter-sector roots exist
  // for the mission circuit and evidence return to travel along and are drawn ONLY when they carry
  // flow — an idle colony shows no lines that mean nothing.
  var ROOT_PAIRS = [['queen', 'intel', 1, 26], ['queen', 'forge', 1, 30], ['queen', 'valid', 1, 40], ['queen', 'memory', 1, 34], ['queen', 'output', 1, 26], ['intel', 'forge', 1, -20], ['forge', 'valid', 1, 22], ['valid', 'memory', 1, 26], ['memory', 'output', 1, 38]];
  // Four cluster seats inside a chamber. WHICH clusters sit in them comes from the scene — the
  // chamber's real record groupings (event types), largest first; the fourth seat is the verified core.
  var LAYOUT_SCHEMA = 3;

  function create() {
    var root = null, cv = null, ctx = null, tip = null, crumb = null;
    var W = 0, H = 0, scx = 0, scy = 0, raf = 0, ro = null, destroyed = false;
    var reduced = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    // env: space (galaxy, default) | light | strata | nebula | void.
    // labels: none | min | all (zoom-driven; see labelTier).  links.opacity: 0..1.
    // conduits: density less | normal | more; bright 0.4–2; color null (each conduit's own) or '#rrggbb'.
    var opts = { motion: 'normal', labels: 'all', trails: true, env: 'space',
      conduits: { density: 'normal', bright: 1, color: null },
      links: { opacity: .125 } };
    function isLight() { return opts.env === 'light'; }
    // text ink and the sector palette follow the environment: dark ink on the light page, and the
    // chamber colours darkened so grains read on paper instead of washing out
    function ink(a) { return isLight() ? 'rgba(29,40,54,' + a + ')' : 'rgba(201,210,221,' + a + ')'; }
    function dim(a) { return isLight() ? 'rgba(79,100,125,' + a + ')' : 'rgba(107,116,136,' + a + ')'; }
    function shade3(c, k) { return [Math.round(c[0] * k), Math.round(c[1] * k), Math.round(c[2] * k)]; }
    function lighten3(c, k) { return [Math.round(c[0] + (255 - c[0]) * k), Math.round(c[1] + (255 - c[1]) * k), Math.round(c[2] + (255 - c[2]) * k)]; }
    function sectorColors(s) {
      var base = (s.style && s.style.color) ? h2(s.style.color) : h2(s.color);
      var core = (s.style && s.style.color) ? lighten3(base, .38) : h2(s.core);
      if (isLight()) { base = shade3(base, .62); core = shade3(core, .55); }
      return { c0: base, c1: core };
    }
    var live = function () { return !reduced && opts.motion !== 'off'; };

    var rnd = lcg(42);
    var SEC = SECTOR_DEFS.map(function (d) { return Object.assign({ morph: 0, frozen: null, defPos: d.pos.slice(), defLabel: d.label, present: false, pts: [], links: [], records: [], residents: [], clusters: [], counts: null, style: { color: null, glow: 1, bright: 1 } }, d, { pos: d.pos.slice() }); });
    // operator overrides for ants: { roleIdLower: { name, color } } — presentation, persisted with the layout
    var antStyles = {};
    /* OPERATOR-ADDED MOUND CHAMBERS — v0.3.8.122.
       `+ Mound` puts a chamber in the colony straight away, drawn with the roster every mound runs
       (fetched from /micromound/roster/defaults, never invented here — `moundDefaults` stays empty
       until the server answers, and a chamber added before then simply has no residents yet).

       EVERY NAME AND COLOUR AN OPERATOR SETS ON ONE OF THESE IS PRESENTATION AND NOTHING ELSE. It
       lives in this layout, beside the chamber positions, and never reaches a device: a mound is
       enrolled by one-time token and keeps taking commands under its own identity whatever the
       colony calls it. That is the whole point — an operator labels their fleet for their own use
       case, and the fleet does not care. */
    var moundDefaults = [];
    var addedMounds = [];   // [{ id, label, pos }] — persisted; the sector defs are derived
    var bySec = {}; SEC.forEach(function (s) { bySec[s.id] = s; });
    /** Materialise one operator-added mound as a sector, in the same shape SECTOR_DEFS produce. */
    function mountAddedMound(rec) {
      if (bySec[rec.id]) return bySec[rec.id];
      var d = { id: rec.id, label: rec.label, mound: true, added: true, color: '#a55a7e', core: '#c9cfdc',
        pos: rec.pos.slice(), R: 34, n: 110, rot: .00006 };
      var s = Object.assign({ morph: 0, frozen: null, defPos: d.pos.slice(), defLabel: d.label,
        present: true, pts: [], links: [], records: [], residents: [], clusters: [], counts: null,
        style: { color: null, glow: 1, bright: 1 } }, d, { pos: d.pos.slice() });
      SEC.push(s); bySec[s.id] = s;
      return s;
    }
    /** Where the next added mound sits: a ring below the colony, so they never land on each other. */
    function nextMoundSeat(n) {
      var th = n * 1.05 + .4;
      return [Math.cos(th) * 260, 300 + (n % 2 ? 40 : 0), Math.sin(th) * 200];
    }
    /** The residents an added mound shows: the default roster, as presentation-only ants. */
    function moundResidents(sectorId) {
      return moundDefaults.map(function (a) {
        return { roleId: sectorId + '/' + a.name, name: a.name, status: 'idle', workers: [], trail: null, note: a.role };
      });
    }
    /** A chamber is drawn only when the scene names it (the mound only when the fleet has one). */
    function shown() { return SEC.filter(function (s) { return s.present; }); }

    // roots: shape offsets stored so a moved sphere re-grows its roots (§4)
    function rebuildRoot(r) {
      var A = bySec[r.a].pos, B = bySec[r.b].pos;
      r.mids.forEach(function (mo, si) {
        var mid = V(A, B, .5); mid[0] += mo[0]; mid[1] += mo[1]; mid[2] += mo[2];
        var pts = r.strands[si]; pts.length = 0;
        for (var i = 0; i <= 18; i++) { var t = i / 18; pts.push([(1 - t) * (1 - t) * A[0] + 2 * (1 - t) * t * mid[0] + t * t * B[0], (1 - t) * (1 - t) * A[1] + 2 * (1 - t) * t * mid[1] + t * t * B[1], (1 - t) * (1 - t) * A[2] + 2 * (1 - t) * t * mid[2] + t * t * B[2]]); }
      });
    }
    function mkRoot(a, b, strands, sag) {
      var r = { a: a, b: b, mids: [], strands: [] };
      for (var s = 0; s < strands; s++) { r.mids.push([(rnd() - .5) * 14, sag + (rnd() - .5) * 10, (rnd() - .5) * 14]); r.strands.push([]); }
      rebuildRoot(r); return r;
    }
    var roots = ROOT_PAIRS.map(function (p) { return mkRoot(p[0], p[1], p[2], p[3]); });
    /* EVERY MOUND HANGS OFF THE QUEEN, AND WHICH MOUNDS EXIST CHANGES WHILE THE PAGE IS OPEN.
       v0.3.8.123.

       `.122` had exactly one authority strand, hard-coded `queen → mound`. Infrastructure had none
       and an operator-added chamber had none — so the two kinds of mound an operator actually ends
       up with floated unattached while the one built-in placeholder was wired. That is not a
       cosmetic gap. A conduit in this view is the statement that a chamber answers to the Queen,
       and a mound that takes charters from her and shows no strand is the console contradicting
       what the colony does.

       So the strands are DERIVED from the sector table rather than listed: one per chamber flagged
       `mound`, rebuilt whenever that set changes. Adding a mound wires it; removing one drops its
       strand with it; nothing has to remember to keep a second list in step. */
    var authorities = {};
    function rebuildAuthorities() {
      var want = {}, changed = false;
      SEC.forEach(function (s) {
        if (!s.mound) return;
        want[s.id] = true;
        if (!authorities[s.id]) { authorities[s.id] = mkRoot('queen', s.id, 1, 20); changed = true; }
      });
      Object.keys(authorities).forEach(function (id) { if (!want[id]) { delete authorities[id]; changed = true; } });
      return changed;
    }
    function eachAuthority(fn) { Object.keys(authorities).forEach(function (id) { fn(authorities[id], id); }); }
    // A new strand with no particles on it reads as a dead line, so the streams are rebuilt in the
    // same breath the strand set changes rather than waiting for whatever else happens to call it.
    function rebuildAll() { var ch = rebuildAuthorities(); roots.forEach(rebuildRoot); eachAuthority(rebuildRoot); if (ch) buildStreams(); }
    rebuildAuthorities();

    // active route + ants — REPLACED wholesale by setTopology; demo defaults below
    var rootIndex = {}; roots.forEach(function (r, i) { rootIndex[r.a + '>' + r.b] = i; rootIndex[r.b + '>' + r.a] = i; });
    var circuit = [], retSeg = null, ants = [], attention = [], partial = false, lastScene = null;
    function routeFromSectorPath(path, paused) {
      circuit = [];
      for (var i = 0; i < path.length - 1; i++) {
        var ri = rootIndex[path[i] + '>' + path[i + 1]];
        if (ri == null) continue;
        var r = roots[ri], rev = r.a !== path[i];
        circuit.push({ pts: r.strands[0], rev: rev, col: bySec[path[i + 1]].color, pausedAt: (i === path.length - 2 && paused) ? .88 : null });
      }
      buildStreams();
    }
    /* ── A CHAMBER'S CONTENTS ARE ITS RECORDS AND ITS RESIDENTS, AND NOTHING ELSE ─────────────────
       Each persisted record is one grain, placed by the hash the reducer computed from its id
       (`place`), so a record lands in the same spot on every reload and on every screen. A record
       whose task PASSED deterministic evidence sits in the core (`verification === 'verified'`);
       everything else sits in the shell, seated by its cluster (event type). Each registry
       resident is one orb in the mid band, its workers small orbs around it. No filler grains,
       no floor: an empty chamber is empty. */
    function seatOf(place, r) { var u = place.a * 2 - 1, th = place.b * TAU, sq = Math.sqrt(Math.max(0, 1 - u * u)); return [sq * Math.cos(th) * r, u * r, sq * Math.sin(th) * r]; }
    function hashPlace(str) { var h = 2166136261 >>> 0; for (var i = 0; i < str.length; i++) { h ^= str.charCodeAt(i); h = Math.imul(h, 16777619) >>> 0; } var a = ((h >>> 0) % 10007) / 10007, b = ((Math.imul(h, 2654435761) >>> 0) % 10007) / 10007, c = ((Math.imul(h ^ 0x9e3779b9, 40503) >>> 0) % 10007) / 10007; return { a: a, b: b, c: c }; }
    var GOLDEN = Math.PI * (3 - Math.sqrt(5)), SPIRAL_STEP = 2.399963;   // the golden angle, for the strata spirals
    function unit(str, salt) { return hashPlace(salt + ':' + str).a; }
    /* CLUSTERS ARE REAL AND SO ARE THEIR SEATS (ported from the retired renderer, geometry intact).
       A chamber's clusters are the kinds of record it actually holds, in a stable order, on a
       golden-angle lattice inside the sphere. Each record sits inside its own cluster at a depth its
       durability decides — verified records and those authored by a unit with a strong pheromone
       trail sit deeper — with its direction from the hash of its id, so a record keeps its seat as
       the chamber fills up around it. */
    function clusterSeats(sec, R) {
      var order = (sec.clusters || []).slice().sort(function (a, b) { return a.id < b.id ? -1 : a.id > b.id ? 1 : 0; });
      var n = order.length;
      return order.map(function (cl, i) {
        var y = 1 - (i / Math.max(1, n - 1)) * 1.55, rad = Math.sqrt(Math.max(.05, 1 - y * y)), th = GOLDEN * i;
        var shellFrac = .42 + unit(cl.id, 'shell') * .36;
        return { id: cl.id, label: cl.label || cl.id, records: cl.records || [], count: cl.count || (cl.records || []).length,
          center: [Math.cos(th) * rad * R * shellFrac, y * R * shellFrac * .85, Math.sin(th) * rad * R * shellFrac] };
      });
    }
    /* SYMMETRIC CLOUD SEATS. The reference seated records inside their cluster with a hashed
       direction, which reads as lopsided clumps once one cluster dominates. The cloud is now a
       fixed Fibonacci lattice of 96 slots on the sphere — evenly spread by construction — and a
       record takes the slot its id hashes to (linear probe on collision), at a radius its
       durability decides. Stable per record, symmetric per chamber, and the ordered strata on
       focus are untouched. */
    var SLOTS = 96, LATTICE = [];
    for (var li = 0; li < SLOTS; li++) { var zz = 1 - 2 * (li + .5) / SLOTS, rr = Math.sqrt(Math.max(0, 1 - zz * zz)), ph = li * GOLDEN; LATTICE.push([Math.cos(ph) * rr, zz, Math.sin(ph) * rr]); }
    function rebuildSector(s, sec) {
      var old = {}; s.pts.forEach(function (p) { if (p.rec) old[p.rec.id] = p; });
      var pts = [], links = [], taken = {};
      // the trail the colony recorded for whichever unit authored a record; a role with no trail is
      // null, which is not zero — nothing has run
      var trails = {};
      (sec.residents || []).forEach(function (r) { var st = r.trail && isFinite(r.trail.strength) ? Number(r.trail.strength) : 0; if (r.roleId) trails[String(r.roleId).toLowerCase()] = st; (r.workers || []).forEach(function (w) { var id = (w && (w.id || w)) || ''; if (id) trails[String(id).toLowerCase()] = st; }); });
      function trailOf(ant) { var k = String(ant || '').toLowerCase(); return Object.prototype.hasOwnProperty.call(trails, k) ? Math.max(0, Math.min(1, trails[k])) : 0; }
      var seats = clusterSeats(sec, s.R), C = Math.max(1, seats.length);
      s.strata = [];
      seats.forEach(function (cl, ci) {
        // THE ORDERED FORMATION: one level (stratum) per cluster, records on an even golden-angle
        // spiral within it; the cloud cross-fades into this when the chamber is focused.
        var mcount = Math.max(1, cl.records.length);
        var y = ((ci + .5) / C - .5) * s.R * 1.55;
        var band = Math.sqrt(Math.max(.12, 1 - Math.pow(y / (s.R * 1.05), 2)));
        s.strata.push({ id: cl.id, label: cl.label, count: cl.count, y: y, band: band });
        cl.records.forEach(function (r, k) {
          var id = r.recordId || r.id || (r.title + r.createdAt);
          var place = r.place || hashPlace(String(id));
          var verified = r.verification === 'verified', pher = trailOf(r.ant);
          var durable = (verified ? .55 : .1) + pher * .45;
          var slot = Math.floor(unit(id, 'slot') * SLOTS) % SLOTS, probe = 0; while (taken[slot] && probe < SLOTS) { slot = (slot + 1) % SLOTS; probe++; }
          var ring = Math.floor(Object.keys(taken).length / SLOTS); taken[slot] = true;   // a 97th record starts a second, inner ring
          /* THE RADIUS IS QUANTISED INTO SHELLS. v0.3.8.123 — this was a continuous function of
             durability, so no two records sat at quite the same distance and the cloud read as
             fuzz: the lattice underneath it is perfectly even, and a per-record radius was the one
             thing scattering it. Three shells (core, mid, outer) keep exactly the meaning the
             continuous version carried — verified and well-trailed records sit deeper — and let
             the eye see the arrangement, which is what "less madness" asks for. Which shell a
             record lands in still comes only from its own durability, so its seat is as stable as
             it ever was. */
          var shell = verified ? 0 : durable > .34 ? 1 : 2;
          var seatR = s.R * [.34, .62, .84][shell] * (ring ? .8 : 1);
          var o = [LATTICE[slot][0] * seatR, LATTICE[slot][1] * seatR, LATTICE[slot][2] * seatR];
          var ang = k * SPIRAL_STEP, rad = s.R * .86 * band * Math.sqrt((k + .55) / mcount);
          var org = [Math.cos(ang) * rad, y, Math.sin(ang) * rad];
          var rec = { id: id, title: r.title || r.recordType || 'record', type: r.recordType || r.type || 'record', ant: r.ant || '—', mission: r.missionId || '', taskId: r.taskId || '', time: r.createdAt || '', verif: r.verification || 'not_scanned', cluster: cl.id, pher: pher };
          var prev = old[id], pt = prev || { born: performance.now(), ph: place.b * TAU, rec: null };
          var radN = Math.min(1, Math.hypot(o[0], o[1], o[2]) / s.R), edge = 1 - .72 * Math.pow(radN, 2.6);
          if (prev && (Math.abs(prev.o[0] - o[0]) + Math.abs(prev.o[1] - o[1]) + Math.abs(prev.o[2] - o[2])) > .5) pt.settle = { from: prev.o.slice(), to: o.slice(), t: 0 };
          pt.o = o.slice(); pt.org = org; pt.layer = verified ? 2 : 0; pt.cl = ci; pt.stratum = ci;   // o is its own array: the settle interpolates INTO it from a frozen `to`
          pt.sz = (1.15 + pher * 1.7) * (.72 + .28 * edge) * .9; pt.a = Math.min(1, .82 + pher * .2) * (.86 + .14 * edge); pt.coreMix = Math.min(1, Math.pow(1 - radN, 1.5) * 1.15);
          pt.rec = rec; pt.resident = null;
          pts.push(pt);
        });
      });
      /* RESIDENTS SIT ON A RING, NOT IN A DRIFT. v0.3.8.123.

         The seats used to carry two pieces of deliberate noise — a `+.7` phase offset and a
         `sin(ri * 2.4)` vertical wobble on the role orbs, and an alternating ±.08R zigzag on the
         workers. Both were there to keep orbs from overlapping, and both worked, at the cost of a
         chamber that reads as a handful of ants dropped in rather than a colony arranged in one.
         The operator's word for it was "method and symmetry and less madness."

         What replaces them: roles on a level ring, first seat at the top and the rest evenly round
         it; each role's workers on a concentric outer arc CENTRED ON THEIR PARENT, at one fixed
         drop below it, so a role and its sub-ants read as one group. Nothing overlaps because the
         spacing is computed rather than jittered, and the arrangement now says something true —
         the ring is the roster, the arc under a seat is that role's workers.

         THE QUEEN IS THE EXCEPTION, and she is the exception everywhere else in this colony too:
         her chamber is the authority chamber, she is the one resident who is not a peer of the
         others, and drawing her as one seat among seven on the same ring said otherwise. She sits
         at the centre of her own chamber at nearly double size, and the ring closes around her. */
      var top = s.strata.length ? s.strata[0].y - s.R * .42 : -s.R * .4;   // −y is up: the row sits over the highest level
      var resList = sec.residents || [];
      function isQueenSeat(r) { return s.id === 'queen' && String(r.roleId || '').toLowerCase() === 'queen'; }
      var ringN = Math.max(1, resList.filter(function (r) { return !isQueenSeat(r); }).length), ringI = 0;
      resList.forEach(function (r, ri) {
        var queenSeat = isQueenSeat(r);
        var th = -Math.PI / 2 + (queenSeat ? 0 : (ringI / ringN) * TAU);
        if (!queenSeat) ringI++;
        var base = queenSeat ? [0, 0, 0] : [Math.cos(th) * s.R * .58, 0, Math.sin(th) * s.R * .58];
        var rowX = queenSeat ? 0 : ((ringI - .5) / ringN - .5) * s.R * 2.2;
        var ov = antStyles[String(r.roleId || '').toLowerCase()] || {};
        pts.push({ o: base, org: [rowX, queenSeat ? top - s.R * .22 : top, 0], layer: 1, cl: 0, sz: queenSeat ? 4.4 : 2.4, a: .95, ph: ri, born: 0, rec: null, below: false, queen: queenSeat, antColor: ov.color || null, resident: { roleId: r.roleId, name: ov.name || r.name || r.roleId, registryName: r.name || r.roleId, status: r.status, trail: r.trail || null, workers: (r.workers || []).length, color: ov.color || null } });
        var roleIdx = pts.length - 1;
        var wn = (r.workers || []).length;
        (r.workers || []).forEach(function (w, wi) {
          // Spread so the arc a role's workers occupy never reaches its neighbour's, however many
          // it has: the ring's own angular pitch, minus a gap, divided among them.
          var pitch = TAU / ringN, spread = Math.min(pitch * .62, .34 * Math.max(1, wn - 1));
          var wt = th + (wn > 1 ? (wi / (wn - 1) - .5) * spread : 0);
          var ovw = antStyles[String(w.id || '').toLowerCase()] || {};
          pts.push({ o: [Math.cos(wt) * s.R * .82, s.R * .22, Math.sin(wt) * s.R * .82], org: [rowX + (wi - (wn - 1) / 2) * s.R * .12, top - s.R * .16, 0], layer: 1, cl: 0, sz: 1.4, a: .8, ph: wi, born: 0, rec: null, below: true, antColor: ovw.color || ov.color || null, resident: { roleId: w.id, name: ovw.name || w.name || w.id, registryName: w.name || w.id, parent: w.parent || r.roleId, status: w.enabled === false ? 'disabled' : r.status, worker: true, color: ovw.color || null } });
          links.push([pts.length - 1, roleIdx]);   // the roster chain: worker → its role
        });
      });
      // a mission's thread through this chamber: records sharing a mission_id, in recorded order
      var byMission = {}; pts.forEach(function (p, i) { if (p.rec && p.rec.mission) (byMission[p.rec.mission] = byMission[p.rec.mission] || []).push(i); });
      Object.keys(byMission).forEach(function (mkey) { var list = byMission[mkey].sort(function (a, b) { return pts[a].rec.time < pts[b].rec.time ? -1 : 1; }); if (list.length < 2) return; for (var i = 1; i < list.length; i++) links.push([list[i - 1], list[i]]); });
      // Which points a link actually joins, so tier 3 can label the endpoints and nothing else.
      // Computed here, once per rebuild, rather than scanned per frame inside the draw loop.
      pts.forEach(function (p) { p.linked = false; });
      links.forEach(function (lk) { if (pts[lk[0]]) pts[lk[0]].linked = true; if (pts[lk[1]]) pts[lk[1]].linked = true; });
      s.pts = pts; s.links = links;
      s.records = sec.records || []; s.residents = sec.residents || []; s.clusters = sec.clusters || [];
      s.counts = { records: sec.recordCount != null ? sec.recordCount : s.records.length, running: (sec.runningTasks || []).length, residents: s.residents.length, verified: s.records.filter(function (r) { return r.verification === 'verified'; }).length };
    }
    // one-shot flights: a recorded transition plays once, ant from → to, then is done
    var flights = [], playedTransitions = {};

    // pheromone streams (the 3h connection language: particles, not lines)
    var rootStreams = [], circStreams = [], retStream = [], authStreams = {};
    function mkStream(pts, n, s0, s1) { var out = []; for (var i = 0; i < n; i++) out.push({ pts: pts, t: rnd(), sp: s0 + rnd() * (s1 - s0), n: (rnd() - .5) * 10, ph: rnd() * TAU }); return out; }
    function densityK() { return opts.conduits.density === 'less' ? .5 : opts.conduits.density === 'more' ? 1.9 : 1; }
    function buildStreams() {
      // pace (mockup 2a): unhurried. A particle takes ~25–60 s to cross a root; the circuit is the
      // fastest thing on screen and still takes ~15 s a segment. Counts scale with the operator's
      // density choice (less / normal / more).
      var k = densityK();
      rootStreams = roots.filter(function (r) { return r.a === 'queen'; }).map(function (r) { return mkStream(r.strands[0], Math.round(6 * k), .000016, .00003); });
      circStreams = circuit.map(function (sg) { return { col: sg.col, ps: mkStream(sg.pts, Math.round(22 * k), .00004, .00007) }; });
      retStream = retSeg ? mkStream(retSeg.pts, Math.round(8 * k), .00003, .00005) : [];
      authStreams = {};
      eachAuthority(function (a, id) { authStreams[id] = mkStream(a.strands[0], Math.round(7 * k), .00002, .000035); });
    }
    /** A conduit's particle colour: the operator's override, else its own (darkened on the light page). */
    function conduitRGB(own) { var c = opts.conduits.color ? h2(opts.conduits.color) : own.split(',').map(Number); if (isLight()) c = shade3(c, .7); return c.join(','); }

    // 3c galaxy environment: world-space stars + dust so everything parallaxes
    var DUST = [], STARS = [];
    for (var i = 0; i < 150; i++) DUST.push({ p: [(rnd() - .5) * 1080, (rnd() - .5) * 760, (rnd() - .5) * 560], sp: .008 + rnd() * .02, ph: rnd() * TAU });
    for (var j = 0; j < 110; j++) { var u2 = rnd() * 2 - 1, th2 = rnd() * TAU, sq2 = Math.sqrt(1 - u2 * u2), RR = 820 + rnd() * 380; STARS.push({ p: [sq2 * Math.cos(th2) * RR, u2 * RR * .7, sq2 * Math.sin(th2) * RR], sz: rnd() < .82 ? .7 : 1.5, ph: rnd() * TAU }); }
    // ENVIRONMENTS (design doc §17, operator review): nothing is painted flat on the glass any more.
    // Every light in the sky is a point in WORLD space that the camera projects, so a drag moves
    // it like everything else, and the plane's light comes from sources that are never drawn.
    //   strata — the formicarium's cross-section: soil-strata bands and contour lines behind the
    //            colony, and the dust motes in TRUE 3D so they parallax with every orbit and pan.
    //   space  — a star sphere (varied, a few tinted) and a galactic haze along an INCLINED GREAT
    //            CIRCLE in world space — a 3D band that swings with the camera, not a stripe.
    //   nebula — layered gas at several depths, each patch tinted by the nearest sector, so the
    //            sectors appear to light the gas around them; heavy parallax.
    //   void   — black. The sectors are the only light there is.
    //
    // THERE IS NO GROUND PLANE, AND THAT IS THE POINT (v0.3.8.122). `plane` drew a lit floor at
    // y=340 — a wide faint disc, three unseen lights glinting off it, and one coloured pool per
    // chamber — and `void` drew the disc too. Every one of those is a horizontal surface, so each
    // one silently declared a down: the colony had a floor, the floor bounced light back up onto
    // the chambers, and the camera could not be taken under it without drawing the colony through
    // its own shadow. The operator's word for it was that the light bouncing off it was the
    // problem, and the fix is not to dim the floor — a dimmer floor is still a floor, and still
    // fixes the horizon. The whole plane is gone: `PLANE_Y`, `planePool`, `LIGHTS`, `envGround`,
    // `camPos`, and the `plane` environment that existed only to show them.
    //
    // A colony suspended in a void has no privileged direction, which is what lets the camera
    // orbit through the full sphere (see the pitch drag). Everything that remains — stars, band,
    // clouds, strata, nebula gas — is a WORLD point the camera projects, so it reads correctly
    // from underneath. Nothing is painted flat on the glass.
    // THE GALAXY (default sky). Everything below is a WORLD point the camera projects — stars,
    // band grains, dust lanes, cloud sub-blobs, spiral-arm points — so orbit, zoom and pan move the
    // sky as a sky. Built once with the seeded generator; drawn with fillRect where it can be.
    var SKY = [], BANDG = [], KNOTS = [], LANES = [], CLOUDS = [], SPIRAL = null;
    (function buildSky() {
      // stars: a power-law brightness distribution (many faint, few bright), warm/cool tints
      for (var i = 0; i < 950; i++) {
        var u = rnd() * 2 - 1, th = rnd() * TAU, sq = Math.sqrt(1 - u * u), R = 1700 + rnd() * 700, b = Math.pow(rnd(), 3.2), t = rnd();
        SKY.push({ p: [sq * Math.cos(th) * R, u * R, sq * Math.sin(th) * R], sz: .45 + b * 1.6, a: .16 + b * .8, ph: rnd() * TAU, spike: b > .82,
          c: t < .07 ? '255,205,160' : t < .15 ? '255,228,200' : t < .27 ? '176,200,255' : '226,232,246' });
      }
      // the band: a great circle inclined and rolled; grains cluster tight with a wide halo, warm
      // toward the "core" direction and cool along the arms; knots (bright clusters) and dark dust
      // lanes sit just off the midline so the band has structure instead of a smooth glow
      var tilt = .62, roll = .35, ct = Math.cos(tilt), stt = Math.sin(tilt), cr = Math.cos(roll), srl = Math.sin(roll);
      function onBand(a, off, R) { var x = Math.cos(a) * R, y = off, z = Math.sin(a) * R; var y1 = y * ct - z * stt, z1 = y * stt + z * ct; return [x * cr - y1 * srl, x * srl + y1 * cr, z1]; }
      var coreA = 1.1;
      for (var g = 0; g < 640; g++) {
        var a = rnd() * TAU, tight = rnd() < .68, off = (rnd() - .5) * (tight ? 110 : 420) + (rnd() - .5) * 40, R2 = 1500 + (rnd() - .5) * 260;
        var warm = Math.max(0, Math.cos(a - coreA)), b2 = Math.pow(rnd(), 2.4);
        BANDG.push({ p: onBand(a, off, R2), sz: .6 + b2 * 1.2, a: (.2 + b2 * .5) * (tight ? 1 : .6), ph: rnd() * TAU,
          c: warm > .6 ? '255,226,190' : warm > .25 ? '236,232,230' : '196,210,250' });
      }
      for (var k = 0; k < 18; k++) { var ak = rnd() * TAU; KNOTS.push({ p: onBand(ak, (rnd() - .5) * 120, 1500), r: 60 + rnd() * 110, a: .06 + rnd() * .05, c: Math.cos(ak - coreA) > .3 ? '255,222,180' : '190,205,245' }); }
      for (var l = 0; l < 11; l++) { var al = rnd() * TAU; LANES.push({ p: onBand(al, (rnd() - .5) * 90, 1490), r: 60 + rnd() * 120, a: .22 + rnd() * .2, sx: .5 + rnd() * .6 }); }
      // volumetric clouds: each is a family of sub-blobs (two tints + a dark wisp) that drift out
      // of phase, so the cloud has an inside instead of being one gradient
      var seeds = [ // spread around the full sphere, so every orbit angle has a sky
        { p: [-900, -420, -1500], r: 560, c: '107,74,158', h: '150,110,210', a: .14, ph: 0 },
        { p: [1050, 260, -1650], r: 520, c: '47,127,138', h: '90,180,190', a: .13, ph: 2 },
        { p: [150, 720, -1750], r: 600, c: '125,42,85', h: '190,80,130', a: .11, ph: 4 },
        { p: [-300, -1200, 900], r: 480, c: '70,80,150', h: '110,120,200', a: .08, ph: 1 },
        { p: [1500, -600, 900], r: 540, c: '96,80,170', h: '140,120,220', a: .12, ph: 3 },
        { p: [-1600, 300, 600], r: 500, c: '50,120,150', h: '90,170,190', a: .11, ph: 5 },
        { p: [400, -1300, -700], r: 460, c: '150,60,110', h: '200,100,150', a: .09, ph: 6 }];
      seeds.forEach(function (sd) {
        var parts = [];
        for (var j = 0; j < 10; j++) { var dx = (rnd() - .5) * sd.r * 1.1, dy = (rnd() - .5) * sd.r * .8, dz = (rnd() - .5) * sd.r * .5; parts.push({ o: [dx, dy, dz], r: sd.r * (.22 + rnd() * .38), c: rnd() < .35 ? sd.h : sd.c, a: sd.a * (.35 + rnd() * .5), ph: rnd() * TAU, dark: false }); }
        for (var d2 = 0; d2 < 3; d2++) { parts.push({ o: [(rnd() - .5) * sd.r * .9, (rnd() - .5) * sd.r * .6, 0], r: sd.r * (.18 + rnd() * .22), c: '4,5,9', a: .18 + rnd() * .12, ph: rnd() * TAU, dark: true }); }
        CLOUDS.push({ p: sd.p, parts: parts, ph: sd.ph });
      });
      // one distant spiral galaxy: two logarithmic arms of grains, a warm bulge, a faint disc, on
      // its own inclined basis so it foreshortens as the camera moves around it
      var C = [1500, -950, -1900], u1 = [.86, .18, .48], v1 = [-.26, .93, .12];
      var arms = [];
      for (var arm = 0; arm < 2; arm++) for (var n = 0; n < 150; n++) {
        var t2 = n / 150, ang = t2 * 3.6 * Math.PI + arm * Math.PI, rad = 22 + 230 * Math.pow(t2, .9), jit = (rnd() - .5) * (18 + t2 * 44);
        var px = Math.cos(ang) * (rad + jit), py = Math.sin(ang) * (rad + jit) * .92;
        arms.push({ p: [C[0] + u1[0] * px + v1[0] * py, C[1] + u1[1] * px + v1[1] * py, C[2] + u1[2] * px + v1[2] * py], sz: .5 + rnd() * .9, a: .12 + (1 - t2) * .35 + rnd() * .1, c: t2 < .3 ? '255,226,190' : (rnd() < .6 ? '200,212,250' : '236,236,246') });
      }
      SPIRAL = { c: C, arms: arms, r: 250, u: u1, v: v1 };
    })();
    var STRATA = [];  // contour lines for 'strata': y position (fraction), wave phases, warmth
    for (var st2 = 0; st2 < 9; st2++) STRATA.push({ y: .08 + st2 * .105 + (rnd() - .5) * .03, ph: rnd() * TAU, ph2: rnd() * TAU, amp: 6 + rnd() * 10, warm: rnd() });
    var FOG = [];   // nebula layers: patches at several depths, tinted later by the nearest sector
    for (var fz = 0; fz < 4; fz++) for (var fp = 0; fp < 9; fp++) FOG.push({ p: [(rnd() - .5) * 1500, (rnd() - .5) * 900, -520 - fz * 260 + (rnd() - .5) * 120], r: 160 + rnd() * 220, a: .035 + rnd() * .035, ph: rnd() * TAU });

    // camera (§7): full 360° yaw, clamped tilt/dolly, eased goals
    var cam = { yaw: -.3, pitch: .4, dist: 900, tgt: [0, 20, 0] };
    var goal = { yaw: -.3, pitch: .4, dist: 900, tgt: [0, 20, 0] };
    var focused = null, follow = false, selRec = null, hovPt = null;
    // The survey distance FITS the colony to the stage. A constant (900) left the whole colony in
    // the middle third of a full-screen stage; the distance now follows the sectors' extent
    // (including dragged ones) and the canvas size, so the colony fills the frame it is given.
    function fitDist() {
      var xs = [], ys = [];
      (shown().length ? shown() : SEC).forEach(function (s) { xs.push(s.pos[0] - s.R, s.pos[0] + s.R); ys.push(s.pos[1] - s.R, s.pos[1] + s.R); });
      var ew = Math.max.apply(null, xs) - Math.min.apply(null, xs), eh = Math.max.apply(null, ys) - Math.min.apply(null, ys);
      var byW = 780 * ew / Math.max(200, W * .74), byH = 780 * eh / Math.max(200, H * .62);
      return Math.max(420, Math.min(1400, Math.max(byW, byH)));
    }
    function proj(p) {
      var rx = p[0] - cam.tgt[0], ry = p[1] - cam.tgt[1], rz = p[2] - cam.tgt[2];
      var cyw = Math.cos(cam.yaw), syw = Math.sin(cam.yaw);
      var x1 = rx * cyw - rz * syw, z1 = rx * syw + rz * cyw;
      var cp = Math.cos(cam.pitch), sp = Math.sin(cam.pitch);
      var y1 = ry * cp - z1 * sp, z2 = ry * sp + z1 * cp;
      var zc = z2 + cam.dist;
      if (zc < 60) return null;
      var s = 780 / zc;
      return { x: scx + x1 * s, y: scy + y1 * s, s: s, zc: zc };
    }
    function fog(zc) { return Math.max(.06, Math.min(1, 1.5 - zc / (cam.dist * 1.55))); }
    /**
     * HOW MUCH TEXT THIS CHAMBER HAS EARNED, 0..3. v0.3.8.123.
     *
     *   0  nothing        1  the chamber's name
     *   2  + every ant in it, WORKERS INCLUDED       3  + EVERY point you can click
     *
     * TWO MODES, NOT FOUR. `.122` shipped `fixed` (the old always-on behaviour) beside a new
     * zoom-driven `zoom`, and offered both. The operator's answer was that the choice was the
     * problem: "i dont want a 'all on zoom' option, i just want it that when all is selected, when
     * you zoom into a chamber, allll the little dots that can be clicked on, show some sort of
     * label on them." So `All` IS the zoom behaviour now — there is no separate setting for it —
     * and it goes all the way: at the closest tier every point `pickPoint` would accept carries a
     * label, records included, not just the ones a link happens to join.
     *
     * `min` is unchanged and still means what it always did: the Queen's name, and detail only in
     * the chamber you have focused. `none` draws no text at all. A browser holding `fixed`, `zoom`
     * or the older `normal` heals to `all` — see `setOptions`.
     *
     * The thresholds are multiples of the chamber's own RADIUS rather than absolute distances, so a
     * small chamber and a large one hand over their names at the same apparent size. Tier 3 sits
     * comfortably inside `pickPoint`'s own 7.5R reach, which is the property that makes "labelled"
     * and "clickable" the same set rather than two sets that nearly agree.
     */
    function labelTier(s, isFocused) {
      if (opts.labels === 'none') return 0;
      if (opts.labels === 'min') return isFocused ? 2 : (s.id === 'queen' ? 1 : 0);
      if (cam.dist < s.R * 3 && isFocused) return 3;
      if (cam.dist < s.R * 5.5 && isFocused) return 2;
      if (cam.dist < s.R * 9.5) return 1;
      return 0;
    }
    /**
     * Drop whole turns out of yaw and pitch WITHOUT MOVING THE CAMERA.
     *
     * Both angles accumulate freely, so an operator who has spun the colony a few times sits at,
     * say, pitch 7.4 rad. Reset then eases from 7.4 to 0.4 — the same orientation, reached by
     * unwinding a full revolution the operator never asked for. Subtracting the same multiple of
     * 2π from `cam` AND `goal` leaves the rendered orientation bit-identical (cos and sin are
     * 2π-periodic) and leaves the ease with the short way round. Called before reset sets its goal;
     * never mid-drag, where shifting only one of the pair WOULD be a visible jump.
     */
    function unwind() {
      var t = TAU, k = Math.round(cam.pitch / t); cam.pitch -= k * t; goal.pitch -= k * t;
      k = Math.round(cam.yaw / t); cam.yaw -= k * t; goal.yaw -= k * t;
    }
    // LIGHTING (design doc §17: "the lighting is dynamic as you move"). Three terms, all cheap:
    //   key   — a fixed WORLD-space light up-left-front, so each sphere has a lit hemisphere that
    //           you see from different angles as you orbit (the lit side does not follow you);
    //   rim   — a VIEW-dependent term from the normal's camera-space depth, brightest at the limb
    //           facing you, so the silhouette changes with every drag;
    //   expo  — exposure from zoom: approaching a chamber brightens its glow and the band; the
    //           far survey is quieter. All three read cam each frame; nothing is precomputed.
    var LKEY = (function () { var l = [-.55, -.7, .45], n = Math.hypot(l[0], l[1], l[2]); return [l[0] / n, l[1] / n, l[2] / n]; })();
    var LT = { cyw: 1, syw: 0, cp: 1, sp: 0, expo: 1 };
    function lightPrep() {
      LT.cyw = Math.cos(cam.yaw); LT.syw = Math.sin(cam.yaw); LT.cp = Math.cos(cam.pitch); LT.sp = Math.sin(cam.pitch);
      LT.expo = Math.max(.8, Math.min(1.6, Math.pow(900 / Math.max(120, cam.dist), .35)));
    }
    /** 0..1 shade for a point at world w on a sphere centred at c (normal = radial direction). */
    function shadeAt(w, c) {
      var nx = w[0] - c[0], ny = w[1] - c[1], nz = w[2] - c[2], len = Math.hypot(nx, ny, nz) || 1;
      nx /= len; ny /= len; nz /= len;
      var key = Math.max(0, nx * LKEY[0] + ny * LKEY[1] + nz * LKEY[2]);
      var z1 = nx * LT.syw + nz * LT.cyw, z2 = ny * LT.sp + z1 * LT.cp;      // camera-space depth of the normal
      var facing = Math.max(0, -z2), rim = Math.pow(1 - Math.min(1, Math.abs(z2)), 2);
      return Math.min(1, .42 + .4 * key + .18 * facing + .2 * rim);
    }
    /** Screen-space offset of the key light for a sphere: where its highlight sits. */
    function lightOffset(s, pr) { var q = proj([s.pos[0] + LKEY[0] * s.R * .55, s.pos[1] + LKEY[1] * s.R * .55, s.pos[2] + LKEY[2] * s.R * .55]); return q ? { dx: q.x - pr.x, dy: q.y - pr.y, front: q.zc < pr.zc } : { dx: 0, dy: 0, front: true }; }
    function pathAt(pts, t, rev) { if (rev) t = 1 - t; var fi = Math.min(.999, Math.max(0, t)) * (pts.length - 1), jj = Math.floor(fi); return V(pts[jj], pts[Math.min(pts.length - 1, jj + 1)], fi - jj); }
    function setCrumb(t) { if (crumb) crumb.textContent = t + (partial ? ' · partial history' : ''); }
    /** The tooltip and breadcrumb are DOM, so they take their ink from the environment here. */
    function restyleChrome() {
      if (tip) { tip.style.background = isLight() ? 'rgba(255,255,255,.96)' : 'rgba(6,8,10,.94)'; tip.style.borderColor = isLight() ? 'rgba(29,50,80,.18)' : 'rgba(255,255,255,.12)'; tip.style.color = isLight() ? '#4f647d' : '#8b93a8'; }
      if (crumb) crumb.style.color = isLight() ? 'rgba(29,40,54,.55)' : 'rgba(185,194,207,.45)';
    }

    // operator layout: positions and name overrides. Emitted to the host, which persists them in
    // /ui/state beside the console's other layout; applied back through setLayout. One store.
    function layoutSnapshot() {
      var positions = {}, names = {}, styles = {};
      SEC.forEach(function (s) {
        positions[s.id] = s.pos.slice(); if (s.label !== s.defLabel) names[s.id] = s.label;
        if (s.style.color || s.style.glow !== 1 || s.style.bright !== 1) styles[s.id] = { color: s.style.color || null, glow: s.style.glow, bright: s.style.bright };
      });
      var ants = {}; Object.keys(antStyles).forEach(function (k) { if (antStyles[k].name || antStyles[k].color) ants[k] = antStyles[k]; });
      // The added mounds themselves, not just their seats: without this the chambers vanish on
      // reload and the operator's fleet labelling goes with them.
      var mounds = addedMounds.map(function (m) { return { id: m.id, label: m.label, pos: (bySec[m.id] || m).pos.slice() }; });
      return { schema: LAYOUT_SCHEMA, positions: positions, names: names, styles: styles, ants: ants, mounds: mounds };
    }
    function validColor(c) { return typeof c === 'string' && /^#[0-9a-fA-F]{6}$/.test(c) ? c.toLowerCase() : null; }
    function clampNum(v, lo, hi, dflt) { v = Number(v); return isFinite(v) ? Math.max(lo, Math.min(hi, v)) : dflt; }
    function saveLayout() { emit('layout', layoutSnapshot()); }
    /* THE WEBGL BUILD'S LAYOUT (schema 2) IS MIGRATED, NOT DROPPED. `.116`–`.117` persisted
       `{ schema: 2, sectors: { id: [x, y, z] } }` in three.js coordinates: y up, home seats on a
       ±16.5 ring with the outliers at ±33 (see the retired renderer's LOOK table, kept verbatim
       below as the migration's source of truth). This world is ~10× that scale with −y up. An
       operator's arrangement is their OFFSET from a home seat, so that is what carries: offset ×10,
       y flipped, applied to this world's home seat, then written back as schema 3 so it happens
       once. Schema 1 (the `.115` world, factor never recorded) still resets — a guessed factor
       would be a fiction dressed as a migration. */
    var SCHEMA2_HOME = { queen: [0, 0, 0], intel: [-16.5, 0, 16.5], forge: [16.5, 0, 16.5], valid: [16.5, 0, -16.5], memory: [-16.5, 0, -16.5], output: [0, 17, 0], mound: [0, -17, 0], homelab: [33, 0, 0] };
    var SCHEMA2_SCALE = 10;
    function migrateSchema2(l) {
      var positions = {}, any = false;
      SEC.forEach(function (s) {
        var p = l.sectors && l.sectors[s.id], h = SCHEMA2_HOME[s.id];
        if (!h || !Array.isArray(p) || p.length !== 3 || !p.every(function (n) { return typeof n === 'number' && isFinite(n) && Math.abs(n) <= 120; })) return;
        positions[s.id] = [s.defPos[0] + (p[0] - h[0]) * SCHEMA2_SCALE, s.defPos[1] - (p[1] - h[1]) * SCHEMA2_SCALE, s.defPos[2] + (p[2] - h[2]) * SCHEMA2_SCALE];
        any = true;
      });
      return any ? { schema: LAYOUT_SCHEMA, positions: positions, names: {}, migratedFrom: 2 } : null;
    }
    function applyLayout(l) {
      if (l && l.schema === 2 && l.sectors) { var m2 = migrateSchema2(l); if (!m2) return false; var ok2 = applyLayout(m2); if (ok2) saveLayout(); return ok2; }
      if (!l || l.schema !== LAYOUT_SCHEMA) return false;   // schema 1 or unknown: reset, not guessed
      SEC.forEach(function (s) {
        var p = l.positions && l.positions[s.id];
        if (Array.isArray(p) && p.length === 3 && p.every(function (n) { return typeof n === 'number' && isFinite(n) && Math.abs(n) <= 1200; })) s.pos = p.slice();
        var nm = l.names && l.names[s.id];
        if (typeof nm === 'string' && nm.trim()) { s.label = nm.trim().toUpperCase().slice(0, 28); s.renamed = true; }
        var st = l.styles && l.styles[s.id];
        if (st && typeof st === 'object') s.style = { color: validColor(st.color), glow: clampNum(st.glow, .5, 2.5, 1), bright: clampNum(st.bright, .3, 2.5, 1) };
      });
      // Added mounds are restored BEFORE the styles and names above would want them — so they are
      // re-read here and the whole apply runs again over the enlarged sector list.
      if (Array.isArray(l.mounds)) {
        addedMounds = [];
        l.mounds.slice(0, 24).forEach(function (m) {
          if (!m || typeof m.id !== 'string' || m.id.indexOf('mound:') !== 0) return;
          var pos = Array.isArray(m.pos) && m.pos.length === 3 && m.pos.every(function (n) { return typeof n === 'number' && isFinite(n) && Math.abs(n) <= 1200; })
            ? m.pos.slice() : nextMoundSeat(addedMounds.length);
          var rec = { id: m.id, label: String(m.label || 'MICROMOUND').toUpperCase().slice(0, 28), pos: pos };
          addedMounds.push(rec); mountAddedMound(rec);
        });
        // second pass so a restored mound picks up its own name, seat and style
        SEC.forEach(function (s2) {
          if (!s2.added) return;
          var p2 = l.positions && l.positions[s2.id]; if (Array.isArray(p2) && p2.length === 3) s2.pos = p2.slice();
          var nm3 = l.names && l.names[s2.id]; if (typeof nm3 === 'string' && nm3.trim()) { s2.label = nm3.trim().toUpperCase().slice(0, 28); s2.renamed = true; }
          var st3 = l.styles && l.styles[s2.id];
          if (st3 && typeof st3 === 'object') s2.style = { color: validColor(st3.color), glow: clampNum(st3.glow, .5, 2.5, 1), bright: clampNum(st3.bright, .3, 2.5, 1) };
        });
      }
      antStyles = {};
      if (l.ants && typeof l.ants === 'object') Object.keys(l.ants).slice(0, 200).forEach(function (k) { var a = l.ants[k] || {}; var nm2 = typeof a.name === 'string' ? a.name.trim().slice(0, 28) : ''; var col = validColor(a.color); if (nm2 || col) antStyles[String(k).toLowerCase()] = { name: nm2 || null, color: col }; });
      rebuildAll(); if (lastScene) api.setTopology(lastScene); return true;
    }

    buildStreams();   // nothing lit until the topology says so — an idle colony is idle

    // ---- rendering ----
    function drawStrand(pts, col, alpha, lw, ts) {
      ctx.beginPath(); var started = false, sSum = 0, sN = 0;
      for (var i = 0; i < pts.length; i++) { var pr = proj(pts[i]); if (!pr) { started = false; continue; } sSum += pr.s; sN++; if (!started) { ctx.moveTo(pr.x, pr.y); started = true; } else ctx.lineTo(pr.x, pr.y); }
      if (!sN) return;
      ctx.strokeStyle = col.replace('$A', String(alpha * fog(cam.dist)));
      ctx.lineWidth = lw * (sSum / sN); ctx.lineCap = 'round'; ctx.stroke();
    }
    function drawStream(ps, col, aScale, sz, rev) {
      for (var i = 0; i < ps.length; i++) {
        var p = ps[i];
        if (live()) { p.t += p.sp * 16; if (p.t >= 1) p.t -= 1; }
        var w = pathAt(p.pts, p.t, rev), q = proj(w); if (!q) continue;
        var wob = Math.sin(p.t * 10 + p.ph) * p.n * q.s;
        var fade = 1 - Math.abs(p.t - .5) * .9;
        ctx.beginPath(); ctx.arc(q.x + wob * .35, q.y + wob, Math.max(.5, (sz || 1.2) * q.s), 0, TAU);
        ctx.fillStyle = 'rgba(' + col + ',' + (aScale * fade * fog(q.zc)) + ')'; ctx.fill();
      }
    }
    // ---- environments ---------------------------------------------------------------------
    function softPoint(q, r, c, a){ if (!q || a <= 0) return; var rr = Math.max(2, r * q.s); var g = ctx.createRadialGradient(q.x, q.y, 0, q.x, q.y, rr); g.addColorStop(0, 'rgba(' + c + ',' + a + ')'); g.addColorStop(1, 'rgba(' + c + ',0)'); ctx.beginPath(); ctx.arc(q.x, q.y, rr, 0, TAU); ctx.fillStyle = g; ctx.fill(); }
    function drawStars(list, base, ts) { list.forEach(function (st) { var q = proj(st.p); if (!q) return; var tw = .55 + Math.sin(ts * .0011 + st.ph) * .35; ctx.beginPath(); ctx.arc(q.x, q.y, st.sz, 0, TAU); ctx.fillStyle = 'rgba(' + (st.c || '220,228,245') + ',' + (base * tw) + ')'; ctx.fill(); }); }
    function drawDust(ts) { DUST.forEach(function (d) { if (live()) { d.p[1] -= d.sp * 1.4; if (d.p[1] < -400) d.p[1] = 400; } var q = proj(d.p); if (!q) return; var tw = .55 + Math.sin(ts * .0009 + d.ph) * .35; ctx.beginPath(); ctx.arc(q.x, q.y, Math.max(.4, q.s), 0, TAU); ctx.fillStyle = 'rgba(172,182,208,' + (.10 * tw * fog(q.zc)) + ')'; ctx.fill(); }); }
    function envStrata(ts) {
      // soil, darker and warmer with depth; the whole section slides a little with the camera so
      // the backdrop answers a drag without pretending to be geometry.
      //
      // THE OFFSETS ARE TRIGONOMETRIC BECAUSE THE ANGLES ARE UNBOUNDED. These were `cam.pitch * 40`
      // and `cam.yaw * 24` — linear in an angle that never wraps. Yaw has always been free, so a few
      // full turns already slid this backdrop off the canvas and left a flat gradient behind; with
      // pitch now free as well (v0.3.8.122) it would do the same going over the top. sin/cos are
      // bounded and periodic, so the parallax returns to where it started after a full turn, which
      // is what a backdrop tied to a viewing angle should do.
      var oy = Math.sin(cam.pitch) * 40, ox = Math.sin(cam.yaw) * 24;
      var g = ctx.createLinearGradient(0, -oy, 0, H - oy);
      g.addColorStop(0, '#07080c'); g.addColorStop(.35, '#0b0a0d'); g.addColorStop(.7, '#100c0d'); g.addColorStop(1, '#130e0e');
      ctx.fillStyle = g; ctx.fillRect(0, 0, W, H);
      ctx.lineWidth = 1; ctx.lineCap = 'round';
      STRATA.forEach(function (L, li) {
        var depth = li / STRATA.length, para = 1 + depth * .8;           // deeper lines move more
        var y0 = L.y * H - oy * para, drift = live() ? ts * .00002 : 0;
        ctx.beginPath();
        for (var x = -40; x <= W + 40; x += 18) {
          var y = y0 + Math.sin(x * .004 + L.ph + drift) * L.amp + Math.sin(x * .011 + L.ph2 - drift * 1.7) * L.amp * .35;
          if (x === -40) ctx.moveTo(x - ox * para, y); else ctx.lineTo(x - ox * para, y);
        }
        var warm = L.warm > .5 ? '150,120,90' : '120,110,120';
        ctx.strokeStyle = 'rgba(' + warm + ',' + ((.035 + depth * .03) * LT.expo) + ')'; ctx.stroke();
      });
      drawDust(ts);
    }
    function grain(q, sz, c, a) { var r = Math.max(.5, sz * Math.min(1.4, q.s * 1.6)); ctx.fillStyle = 'rgba(' + c + ',' + a + ')'; ctx.fillRect(q.x - r * .5, q.y - r * .5, r, r); }
    function drawGalaxy(ts) {
      var ex = Math.max(1, LT.expo) * 1.25, tw0 = ts * .0011;
      // 1. clouds (far, volumetric): dark wisps are drawn AFTER their colour so they carve it
      CLOUDS.forEach(function (cl) {
        var drift = live() ? Math.sin(ts * .00004 + cl.ph) * 40 : 0, base = [cl.p[0] + drift, cl.p[1] + drift * .4, cl.p[2]];
        cl.parts.forEach(function (pt) {
          if (pt.dark) return;
          var w = live() ? Math.sin(ts * .00007 + pt.ph) * 18 : 0;
          softPoint(proj([base[0] + pt.o[0] + w, base[1] + pt.o[1] - w * .5, base[2] + pt.o[2]]), pt.r, pt.c, pt.a * ex);
        });
        cl.parts.forEach(function (pt) { if (pt.dark) softPoint(proj([base[0] + pt.o[0], base[1] + pt.o[1], base[2] + pt.o[2]]), pt.r, pt.c, pt.a); });
      });
      // 2. the band: knots, then grains, then the dust lanes over them
      KNOTS.forEach(function (k) { softPoint(proj(k.p), k.r, k.c, k.a * ex); });
      for (var i = 0; i < BANDG.length; i++) { var g = BANDG[i], q = proj(g.p); if (!q) continue; var tw = .75 + Math.sin(tw0 * .6 + g.ph) * .25; grain(q, g.sz, g.c, g.a * tw * ex); }
      LANES.forEach(function (l) { var q = proj(l.p); if (!q) return; ctx.save(); ctx.translate(q.x, q.y); ctx.scale(1, l.sx); softPoint({ x: 0, y: 0, s: q.s }, l.r, '4,5,9', l.a); ctx.restore(); });
      // 3. the spiral galaxy: disc glow, bulge, then the arms' grains
      if (SPIRAL) {
        var qc = proj(SPIRAL.c);
        if (qc) {
          softPoint(qc, SPIRAL.r * 1.15, '150,160,210', .05 * ex);
          softPoint(qc, SPIRAL.r * .32, '255,228,196', .22 * ex); softPoint(qc, SPIRAL.r * .12, '255,240,220', .35 * ex);
          for (var a2 = 0; a2 < SPIRAL.arms.length; a2++) { var ar = SPIRAL.arms[a2], qa = proj(ar.p); if (qa) grain(qa, ar.sz, ar.c, ar.a * ex); }
        }
      }
      // 4. stars: fillRect grains; the brightest few carry a faint four-point spike
      for (var s2 = 0; s2 < SKY.length; s2++) {
        var st = SKY[s2], q2 = proj(st.p); if (!q2) continue;
        var tw2 = .7 + Math.sin(tw0 + st.ph) * .3, a3 = st.a * tw2 * ex;
        grain(q2, st.sz, st.c, a3);
        if (st.spike) { var L = st.sz * 4 * tw2; ctx.strokeStyle = 'rgba(' + st.c + ',' + (a3 * .35) + ')'; ctx.lineWidth = .6; ctx.beginPath(); ctx.moveTo(q2.x - L, q2.y); ctx.lineTo(q2.x + L, q2.y); ctx.moveTo(q2.x, q2.y - L); ctx.lineTo(q2.x, q2.y + L); ctx.stroke(); }
      }
    }
    /* LIGHT. The console's light theme is paper, not a dimmed night: a cool off-white page with a
       faint warm vignette, no stars, no galaxy; the chambers' palette is darkened by sectorColors()
       and every label uses dark ink. Chosen explicitly (Sky: Light) or automatically when the
       console theme is light — the page decides that, not this file. */
    function envLight(ts) {
      var g = ctx.createLinearGradient(0, 0, 0, H);
      g.addColorStop(0, '#f6f7fa'); g.addColorStop(1, '#e9edf3');
      ctx.fillStyle = g; ctx.fillRect(0, 0, W, H);
      var v = ctx.createRadialGradient(W * .5, H * .45, Math.min(W, H) * .2, W * .5, H * .45, Math.max(W, H) * .8);
      v.addColorStop(0, 'rgba(255,255,255,.55)'); v.addColorStop(1, 'rgba(190,200,215,.35)');
      ctx.fillStyle = v; ctx.fillRect(0, 0, W, H);
      DUST.forEach(function (d) { if (live()) { d.p[1] -= d.sp * 1.4; if (d.p[1] < -400) d.p[1] = 400; } var q = proj(d.p); if (!q) return; ctx.beginPath(); ctx.arc(q.x, q.y, Math.max(.4, q.s), 0, TAU); ctx.fillStyle = 'rgba(90,105,130,' + (.07 * fog(q.zc)) + ')'; ctx.fill(); });
    }
    function drawEnv(ts) {
      if (opts.env === 'strata') { envStrata(ts); return; }
      if (opts.env === 'light') { envLight(ts); return; }
      ctx.fillStyle = opts.env === 'void' ? '#000000' : '#050607'; ctx.fillRect(0, 0, W, H);
      if (opts.env === 'space') { drawGalaxy(ts); drawDust(ts); }
      else if (opts.env === 'nebula') {
        FOG.forEach(function (f) {
          var q = proj(f.p); if (!q) return;
          var near = null, nd = 1e9; shown().forEach(function (s) { var d = Math.hypot(s.pos[0] - f.p[0], s.pos[1] - f.p[1], (s.pos[2] - f.p[2]) * .35); if (d < nd) { nd = d; near = s; } });
          var c = near ? h2(near.color).join(',') : '120,120,160', drift = live() ? Math.sin(ts * .0002 + f.ph) * 12 : 0;
          softPoint({ x: q.x + drift, y: q.y, s: q.s }, f.r, c, f.a * Math.min(1, 520 / nd) * LT.expo);
        });
        drawStars(STARS, .3, ts); drawDust(ts);
      } else { // void — black, and the chambers are the only light in it
        drawStars(STARS, .16, ts);
      }
    }

    function frame(ts) {
      if (destroyed) return;
      raf = requestAnimationFrame(frame);
      var e = .06;
      cam.yaw += (goal.yaw - cam.yaw) * e; cam.pitch += (goal.pitch - cam.pitch) * e; cam.dist += (goal.dist - cam.dist) * e;
      for (var i = 0; i < 3; i++) cam.tgt[i] += (goal.tgt[i] - cam.tgt[i]) * e;
      lightPrep();
      if (live() && !dragging() && !focused && !follow) goal.yaw += Math.sin(ts * .00006) * .00012;
      if (follow && ants[0]) { var ap = antPos(ants[0]); if (ap) goal.tgt = ap; }
      drawEnv(ts);
      // roots + streams: the Queen's spokes always, the inter-sector roots only while they carry flow
      var flowing = circuit.map(function (sg) { return sg.pts; }); if (retSeg) flowing.push(retSeg.pts);
      // strokes are a whisper ghost; the particles are the connection (a touch brighter than before,
      // and scaled by the operator's conduit brightness)
      // On paper a whisper is silence: the strand strokes carry roughly twice the alpha they do
      // under the galaxy, which is the same weight to the eye against a white ground.
      var cb = opts.conduits.bright * (isLight() ? 1.9 : 1), strandInk = isLight() ? 'rgba(52,64,84,$A)' : 'rgba(146,158,176,$A)';
      roots.forEach(function (r) { if (!bySec[r.a].present || !bySec[r.b].present) return; var carries = flowing.indexOf(r.strands[0]) >= 0; if (r.a === 'queen' || carries) drawStrand(r.strands[0], strandInk, (carries ? .055 : .032) * cb, 2.2, ts); });
      // One authority conduit per mound chamber, drawn when that chamber is on screen. The colour
      // is the Queen's, not the mound's: the strand is her authority reaching it, and every mound
      // — infrastructure, the fleet chamber, each one an operator added — is reached the same way.
      eachAuthority(function (a, id) {
        if (!bySec[id] || !bySec[id].present) return;
        a.strands.forEach(function (st) { drawStrand(st, 'rgba(226,31,123,$A)', .045 * cb, 2.2, ts); });
        if (authStreams[id]) drawStream(authStreams[id], conduitRGB('226,31,123'), .5 * cb);
      });
      rootStreams.forEach(function (ps, i) { var r = roots.filter(function (x) { return x.a === 'queen'; })[i]; if (r && r.b && bySec[r.b].present) drawStream(ps, conduitRGB('146,158,176'), .42 * cb); });
      var dens = opts.motion === 'low' ? .55 : 1;
      circStreams.forEach(function (cs, ci) { var c = h2(cs.col); drawStream(cs.ps.slice(0, Math.ceil(cs.ps.length * dens)), conduitRGB(c[0] + ',' + c[1] + ',' + c[2]), Math.min(1, .95 * cb), 1.6, circuit[ci] && circuit[ci].rev); });
      if (opts.trails) drawStream(retStream, conduitRGB('217,176,84'), .75 * cb, 1.4);
      drawSpheres(ts);
      drawAttention(ts);
      drawAnts(ts);
    }
    // LABELS DO NOT STACK. Every label drawn in a frame is registered; one that would overlap an
    // earlier one steps down a row (up to four rows) before it is drawn, so two chambers that project
    // close together, or a role name over a stratum label, stay legible instead of overprinting.
    var labelRects = [];
    function label(text, x, y, font, fill, align) {
      ctx.font = font; ctx.textAlign = align || 'center';
      var wdt = ctx.measureText(text).width, h = 11;
      var x0 = align === 'left' ? x : align === 'right' ? x - wdt : x - wdt / 2;
      for (var tries = 0; tries < 4; tries++) {
        var clash = labelRects.some(function (r) { return x0 < r.x + r.w && x0 + wdt > r.x && y - h < r.y && y > r.y - r.h; });
        if (!clash) break;
        y += h + 2;
      }
      labelRects.push({ x: x0, y: y, w: wdt, h: h });
      ctx.fillStyle = fill; ctx.fillText(text, x, y);
    }
    function drawSpheres(ts) {
      labelRects = [];
      var order = shown().map(function (s) { return { s: s, pr: proj(s.pos) }; }).filter(function (o) { return o.pr; }).sort(function (a, b) { return b.pr.zc - a.pr.zc; });
      order.forEach(function (o) {
        var s = o.s, pr = o.pr;
        var isFocused = focused === s.id;
        var tier = labelTier(s, isFocused);
        var labelBudget = 140;   // per chamber, per frame — see the tier-3 block below
        var rot = s.frozen != null ? s.frozen : (live() ? ts * s.rot : 0);
        var cr = Math.cos(rot), sr = Math.sin(rot);
        var pal = sectorColors(s), c0 = pal.c0, c1 = pal.c1, sty = s.style;
        var selHere = selRec && selRec.sec === s.id ? selRec.idx : null;
        var relSet = selHere != null && s.pts[selHere].rec ? (s.pts[selHere].rec.rel || []) : null;
        var wantMorph = isFocused && cam.dist < s.R * 5.5 ? 1 : 0;
        s.morph += (wantMorph - s.morph) * .05;
        var m = s.morph;
        var nr = s.R * .34 * pr.s;
        // On paper the nucleus keeps the chamber's own core colour: the amber the dark sky gives the
        // Queen turns to mud on white.
        var nuc = (s.id === 'queen' && !sty.color && !isLight()) ? '232,178,90' : c1.join(',');
        /* THE CHAMBER GLOW ENCOMPASSES ITS CONTENTS: every seat lies within .92R, the envelope reaches
           1.2R × the operator's glow size, brightness scales with theirs; a nucleus sits inside it.
           LIGHT MODE IS NOT THE DARK ONE WITH A WHITE SKY. A pale halo that reads as depth against
           black reads as a smudge on paper, so the light envelope is a stronger tint that falls off
           late and closes on a faint rim — the chamber keeps an edge instead of fogging out. */
        var env = Math.max(6, s.R * 1.2 * sty.glow * pr.s), eb = (isLight() ? .30 : .13) * sty.bright * (1 - m * .5) * LT.expo * fog(pr.zc);
        var eg = ctx.createRadialGradient(pr.x, pr.y, 0, pr.x, pr.y, env);
        if (isLight()) {
          eg.addColorStop(0, 'rgba(' + nuc + ',' + (eb * .55) + ')');
          eg.addColorStop(.55, 'rgba(' + c0.join(',') + ',' + (eb * .5) + ')');
          eg.addColorStop(.84, 'rgba(' + c0.join(',') + ',' + (eb * .26) + ')');
          eg.addColorStop(1, 'rgba(' + c0.join(',') + ',0)');
        } else {
          eg.addColorStop(0, 'rgba(' + nuc + ',' + eb + ')'); eg.addColorStop(.62, 'rgba(' + c0.join(',') + ',' + (eb * .55) + ')'); eg.addColorStop(1, 'rgba(' + c0.join(',') + ',0)');
        }
        ctx.beginPath(); ctx.arc(pr.x, pr.y, env, 0, TAU); ctx.fillStyle = eg; ctx.fill();
        if (isLight()) {   // the rim: the chamber's boundary, so the glow has a shape on paper
          ctx.beginPath(); ctx.arc(pr.x, pr.y, s.R * .98 * sty.glow * pr.s, 0, TAU);
          ctx.strokeStyle = 'rgba(' + c0.join(',') + ',' + (.16 * sty.bright * (1 - m * .6) * fog(pr.zc)) + ')'; ctx.lineWidth = 1; ctx.stroke();
        }
        var g = ctx.createRadialGradient(pr.x, pr.y, 0, pr.x, pr.y, Math.max(4, nr * 1.6 * sty.glow));
        g.addColorStop(0, 'rgba(' + nuc + ',' + (.3 * sty.bright * (1 - m * .65) * LT.expo * fog(pr.zc)) + ')'); g.addColorStop(1, 'rgba(' + nuc + ',0)');
        ctx.beginPath(); ctx.arc(pr.x, pr.y, Math.max(4, nr * 1.6 * sty.glow), 0, TAU); ctx.fillStyle = g; ctx.fill();
        var lo = lightOffset(s, pr);
        if (lo.front) {
          // Same inversion as the ant cores: a pale specular is invisible on paper at best and a
          // milky smear at worst, so on the light page the key light leaves a soft DARK bloom where
          // it would otherwise leave a bright one. Both read as a lit sphere; only one of them reads
          // as a lit sphere on white.
          var hr = Math.max(3, nr * 1.4), hc = isLight() ? '44,58,78' : '235,240,250';
          var hg = ctx.createRadialGradient(pr.x + lo.dx, pr.y + lo.dy, 0, pr.x + lo.dx, pr.y + lo.dy, hr);
          hg.addColorStop(0, 'rgba(' + hc + ',' + ((isLight() ? .08 : .10) * LT.expo * fog(pr.zc)) + ')'); hg.addColorStop(1, 'rgba(' + hc + ',0)');
          ctx.beginPath(); ctx.arc(pr.x + lo.dx, pr.y + lo.dy, hr, 0, TAU); ctx.fillStyle = hg; ctx.fill();
        }
        /* LINKAGE OPACITY IS THE OPERATOR'S. v0.3.8.122 — this was hard-coded at .045 focused and
           .022 otherwise, which is "almost transparent" and was the only answer available. The
           range now runs from 0 (the dots alone, no lines at all) to 1 (solid), and .125 is the
           value that reproduces exactly what it looked like before, which is why it is the default:
           an operator who never touches the slider sees no change. */
        var linkA = clampNum(opts.links.opacity, 0, 1, .125) * (isFocused ? .36 : .18) * fog(pr.zc);
        ctx.strokeStyle = 'rgba(' + c0.join(',') + ',' + linkA + ')'; ctx.lineWidth = .6;
        if (linkA > .002) 
        s.links.forEach(function (lk) {
          var pa = s.pts[lk[0]], pb = s.pts[lk[1]];
          if (pa.hidden || pb.hidden) return;
          var wa = ptWorld(s, pa, cr, sr, m), wb = ptWorld(s, pb, cr, sr, m);
          var a = proj(wa), b = proj(wb);
          if (a && b && Math.hypot(a.x - b.x, a.y - b.y) < 90 * pr.s) { ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke(); }
        });
        s.pts.forEach(function (p, pi) {
          if (p.settle) { p.settle.t = Math.min(1, p.settle.t + .02); var k = 1 - Math.pow(1 - p.settle.t, 3); for (var d = 0; d < 3; d++) p.o[d] = p.settle.from[d] + (p.settle.to[d] - p.settle.from[d]) * k; if (p.settle.t >= 1) delete p.settle; }
          if (p.hidden) { p._q = null; return; }
          var w = ptWorld(s, p, cr, sr, m);
          var q = proj(w); if (!q) return;
          p._q = q; p._w = w;
          var a = p.a * fog(q.zc);
          if (selHere != null) a *= (pi === selHere || (relSet && relSet.indexOf(pi) >= 0)) ? 1 : .16;
          if (p.born && ts - p.born < 2000) a *= (ts - p.born) / 2000;
          var res = p.resident;
          // a resident's colour is its STATUS: working = the chamber's core colour with a pulse;
          // idle = the chamber colour; disabled = grey. A record is shell (chamber) or core (verified).
          var col;
          if (res) { var ac = p.antColor ? h2(p.antColor) : null; if (ac && isLight()) ac = shade3(ac, .7); col = res.status === 'disabled' ? '110,118,134' : ac ? ac.join(',') : (res.status === 'working' ? c1.join(',') : c0.join(',')); }
          else { var mix = p.layer === 2 ? 1 : (p.coreMix || 0); col = Math.round(c0[0] + (c1[0] - c0[0]) * mix) + ',' + Math.round(c0[1] + (c1[1] - c0[1]) * mix) + ',' + Math.round(c0[2] + (c1[2] - c0[2]) * mix); }
          var tw = res ? (res.status === 'working' && live() ? .8 + Math.sin(ts * .004 + p.ph) * .2 : 1) : (live() && p.layer === 0 ? .85 + Math.sin(ts * .0012 + p.ph) * .15 : 1);
          var hp = isFocused && hovPt === pi;
          var sh = shadeAt(w, s.pos);                 // lit hemisphere + rim, per point, per frame
          // grains grow as the strata form, so a level's records read as a row and not as dust
          var rad = Math.max(.6, p.sz * q.s * (.95 + .5 * sh) * (res ? 1 : 1 + m * .4)) * (hp ? 1.5 : 1);
          var alpha = Math.min(1, a * tw * (.7 + .8 * sh) * LT.expo * (hp ? 1.4 : 1));
          if (res) {
            // AN ANT IS NOT A GRAIN: a soft halo, a bright core and a ring — the record grains are
            // flat discs. Working ants pulse; a worker is the same shape, smaller.
            var hrad = rad * 2.6, hg2 = ctx.createRadialGradient(q.x, q.y, 0, q.x, q.y, hrad);
            hg2.addColorStop(0, 'rgba(' + col + ',' + (alpha * .55) + ')'); hg2.addColorStop(1, 'rgba(' + col + ',0)');
            ctx.beginPath(); ctx.arc(q.x, q.y, hrad, 0, TAU); ctx.fillStyle = hg2; ctx.fill();
            ctx.beginPath(); ctx.arc(q.x, q.y, rad, 0, TAU); ctx.fillStyle = 'rgba(' + col + ',' + alpha + ')'; ctx.fill();
            /* AN ANT'S CORE IS ITS BRIGHTEST POINT UNDER THE GALAXY AND ITS DARKEST ON PAPER.
               v0.3.8.123 — it was near-white in both, which is right against black and an eye sore
               against an off-white page: the operator's words were that the inside of the ants
               being "hued with white or lighter shade" is "kind of an eye sore with how bright it
               is." The core exists to make an ant read as lit from within rather than as a flat
               disc, and on a light ground the way to say "lit from within" is CONTRAST, not more
               white. So the highlight inverts with the environment — deep ink on paper, warm white
               under the sky — and the shape reads the same in both. */
            ctx.beginPath(); ctx.arc(q.x, q.y, rad * .45, 0, TAU); ctx.fillStyle = isLight() ? 'rgba(20,28,42,' + (alpha * .82) + ')' : 'rgba(255,250,240,' + (alpha * .85) + ')'; ctx.fill();
            ctx.beginPath(); ctx.arc(q.x, q.y, rad + 2.2, 0, TAU); ctx.strokeStyle = 'rgba(' + col + ',' + (alpha * (res.status === 'working' ? .9 * tw : .45)) + ')'; ctx.lineWidth = res.status === 'working' ? 1.4 : .9; ctx.stroke();
          } else { ctx.beginPath(); ctx.arc(q.x, q.y, rad, 0, TAU); ctx.fillStyle = 'rgba(' + col + ',' + alpha + ')'; ctx.fill(); }
          // Every ant at tier 2 — INCLUDING THE WORKERS HANGING OFF EACH ROLE, which `fixed` still
          // omits. A worker is drawn smaller and labelled smaller, so the role reads as the parent
          // and the sub-ants read as its children rather than as nine peers of equal weight.
          if (res && tier >= 2 && m > .25)
            label(res.name, q.x, p.below ? q.y + rad + 11 : q.y - rad - 6,
              (res.worker ? "7px" : p.queen ? "600 9.5px" : "8px") + " 'IBM Plex Mono',monospace",
              ink((res.worker ? .62 : p.queen ? .95 : .8) * m), 'center');
          /* TIER 3 LABELS EVERY CLICKABLE POINT. v0.3.8.123 — it used to label only the record
             points a link joined, on the argument that a label on a point nothing connects to is
             noise. The operator disagreed with the premise: a dot you can click is a dot you should
             be able to read, and a dot you can click but not read is the worse noise. So the `linked`
             filter is gone and the only condition left is that the point HAS something to say.

             The count is capped per chamber because the anti-stacking pass is quadratic in labels
             drawn and a chamber can hold two hundred records. Beyond the cap the grains are still
             there, still clickable, and still name themselves on hover — an unreadable wall of
             overprinted text would not have told the operator anything the tooltip does not. */
          if (!res && tier >= 3 && p.rec && p.rec.title && labelBudget > 0) {
            labelBudget--;
            label(String(p.rec.title).slice(0, 28), q.x, q.y - rad - 5,
              "6.5px 'IBM Plex Mono',monospace", ink(.5 * m), 'center');
          }
          if (hp) { ctx.beginPath(); ctx.arc(q.x, q.y, Math.max(4, p.sz * q.s + 5), 0, TAU); ctx.strokeStyle = 'rgba(' + c0.join(',') + ',.7)'; ctx.lineWidth = 1; ctx.stroke(); }
        });
        if (selHere != null && s.pts[selHere]._q) {
          var sq = s.pts[selHere]._q;
          var pl = live() ? 1 + Math.sin(ts * .004) * .12 : 1;
          ctx.beginPath(); ctx.arc(sq.x, sq.y, Math.max(7, s.pts[selHere].sz * sq.s + 8) * pl, 0, TAU);
          ctx.strokeStyle = 'rgba(226,31,123,.85)'; ctx.lineWidth = 1.6; ctx.stroke();
          (relSet || []).forEach(function (ri) {
            var rq = s.pts[ri] && s.pts[ri]._q; if (!rq) return;
            ctx.beginPath(); ctx.moveTo(sq.x, sq.y); ctx.lineTo(rq.x, rq.y); ctx.strokeStyle = 'rgba(' + c0.join(',') + ',.45)'; ctx.lineWidth = .9; ctx.stroke();
            ctx.beginPath(); ctx.arc(rq.x, rq.y, 2.4, 0, TAU); ctx.fillStyle = 'rgba(' + c0.join(',') + ',.9)'; ctx.fill();
          });
        }
        // THE DEVICE RING MARKS EVERY MOUND, not only the built-in fleet chamber. v0.3.8.123 —
        // this was keyed on `s.id === 'mound'`, so an operator-added chamber and infrastructure
        // were drawn as plain spheres and read as ordinary chambers. The ring is what says "this
        // one is hardware", and it is the same claim for all three kinds.
        if (s.mound) {
          for (var k2 = 0; k2 < 6; k2++) { var th3 = k2 * 1.047 + .5; var w2 = [s.pos[0] + Math.cos(th3) * s.R * .62, s.pos[1] + Math.sin(th3) * s.R * .5, s.pos[2] + Math.sin(th3 * 2) * 8]; var q2 = proj(w2); if (q2) { ctx.beginPath(); ctx.arc(q2.x, q2.y, Math.max(.8, 1.6 * q2.s), 0, TAU); ctx.fillStyle = isLight() ? 'rgba(64,78,98,' + (.5 * fog(q2.zc)) + ')' : 'rgba(201,207,220,' + (.5 * fog(q2.zc)) + ')'; ctx.fill(); } }
          if (s.stopped) { ctx.beginPath(); ctx.arc(pr.x, pr.y, s.R * 1.2 * pr.s, 0, TAU); ctx.strokeStyle = 'rgba(226,31,123,.8)'; ctx.lineWidth = 2.2; ctx.stroke(); }
        }
        if (tier >= 1) {
          label(s.label + (s.mound && s.stopped ? ' · STOPPED' : ''), pr.x, pr.y + (s.R + 18) * pr.s,
            '600 ' + Math.max(8, Math.min(11, 9 * pr.s * 8)) + "px 'IBM Plex Mono',monospace",
            'rgba(' + (isLight() ? shade3(c0, .62).join(',') : c0.join(',')) + ',' + ((isFocused ? .95 : (isLight() ? .8 : .5)) * fog(pr.zc)) + ')', 'center');
        }
        if (isFocused && m > .25 && s.strata && s.strata.length && tier >= 2) {
          // one label per stratum, at the level's right edge (rotates with the chamber), each on its
          // own level so labels cannot stack; the level's ring is a faint guide under its records
          s.strata.forEach(function (st) {
            var lx = s.R * .92 * st.band, ex = lx * cr, ez = lx * sr;
            var q = proj([s.pos[0] + ex, s.pos[1] + st.y, s.pos[2] + ez]); if (!q) return;
            ctx.beginPath();
            for (var ai = 0; ai <= 36; ai++) { var aa = ai / 36 * TAU, rx = Math.cos(aa) * s.R * .86 * st.band, rz = Math.sin(aa) * s.R * .86 * st.band, rq = proj([s.pos[0] + rx * cr - rz * sr, s.pos[1] + st.y, s.pos[2] + rx * sr + rz * cr]); if (!rq) { ai = 99; break; } if (ai) ctx.lineTo(rq.x, rq.y); else ctx.moveTo(rq.x, rq.y); }
            ctx.strokeStyle = 'rgba(' + c0.join(',') + ',' + (.08 * m) + ')'; ctx.lineWidth = .8; ctx.stroke();
            ctx.font = "600 9px 'IBM Plex Mono',monospace"; ctx.textAlign = 'left';
            ctx.fillStyle = ink(m * .85);
            ctx.fillText(String(st.label).toUpperCase().slice(0, 28), q.x + 8, q.y + 3);
            ctx.font = "8px 'IBM Plex Mono',monospace"; ctx.fillStyle = dim(m);
            ctx.fillText(st.count + (st.count === 1 ? ' record' : ' records'), q.x + 8, q.y + 14);
          });
          ctx.textAlign = 'center';
        }
      });
    }
    /** A point's live seat: the cloud seat, the ordered seat, or the blend on screen (m). One source
        of truth for grains, links and labels, so a link never trails the grain it points at. */
    function ptWorld(s, p, cr, sr, m) {
      var o = p.o, g = p.org || p.o;
      var lx = o[0] + (g[0] - o[0]) * m, ly = o[1] + (g[1] - o[1]) * m, lz = o[2] + (g[2] - o[2]) * m;
      return [s.pos[0] + lx * cr - lz * sr, s.pos[1] + ly, s.pos[2] + lx * sr + lz * cr];
    }
    function antPos(an) {
      var sg = an.seg === -1 ? retSeg : circuit[an.seg];
      if (!sg) return null;
      return pathAt(sg.pts, an.t, sg.rev);
    }
    function drawAnts(ts) {
      // recorded transitions: an ant travels from → to once, along a bezier between the chambers
      for (var fi = flights.length - 1; fi >= 0; fi--) {
        var f = flights[fi], A = bySec[f.from].pos, B = bySec[f.to].pos;
        if (live()) f.t += f.sp * 16; else f.t = 1;
        if (f.t >= 1) { flights.splice(fi, 1); continue; }
        var mid = V(A, B, .5); mid[1] -= 40;
        var t = f.t, w = [(1 - t) * (1 - t) * A[0] + 2 * (1 - t) * t * mid[0] + t * t * B[0], (1 - t) * (1 - t) * A[1] + 2 * (1 - t) * t * mid[1] + t * t * B[1], (1 - t) * (1 - t) * A[2] + 2 * (1 - t) * t * mid[2] + t * t * B[2]];
        var fq = proj(w); if (!fq) continue;
        var fc = h2(f.col).join(',');
        ctx.beginPath(); ctx.arc(fq.x, fq.y, Math.max(1.4, 2.2 * fq.s), 0, TAU); ctx.fillStyle = 'rgba(' + fc + ',.95)'; ctx.fill();
        ctx.beginPath(); ctx.arc(fq.x, fq.y, Math.max(2.6, 4.5 * fq.s), 0, TAU); ctx.strokeStyle = 'rgba(' + fc + ',.25)'; ctx.lineWidth = 1; ctx.stroke();
      }
      ants.forEach(function (an) {
        if (live() && !an.paused) { an.t += an.sp * 16; if (an.t >= 1) an.t = 0; }
        var p = antPos(an); if (!p) return;
        var q = proj(p); if (!q) return;
        var c = an.gold ? '217,176,84' : '232,120,171';
        ctx.beginPath(); ctx.arc(q.x, q.y, Math.max(1.4, 2.2 * q.s), 0, TAU); ctx.fillStyle = 'rgba(' + c + ',.95)'; ctx.fill();
        ctx.beginPath(); ctx.arc(q.x, q.y, Math.max(2.6, 4.5 * q.s), 0, TAU); ctx.strokeStyle = 'rgba(' + c + ',.25)'; ctx.lineWidth = 1; ctx.stroke();
        if (an.paused && live()) { ctx.beginPath(); ctx.arc(q.x, q.y, Math.max(4, 7 * q.s) * (1 + Math.sin(ts * .004) * .15), 0, TAU); ctx.strokeStyle = 'rgba(217,176,84,.45)'; ctx.stroke(); }
      });
    }
    function drawAttention(ts) {
      circuit.forEach(function (sg) {
        if (sg.pausedAt == null) return;
        var p = pathAt(sg.pts, sg.pausedAt, sg.rev), q = proj(p); if (!q) return;
        var pl = live() ? .45 + Math.sin(ts * .005) * .3 : .6;
        ctx.beginPath(); ctx.arc(q.x, q.y, Math.max(4, 7 * q.s), 0, TAU); ctx.strokeStyle = 'rgba(217,176,84,' + pl + ')'; ctx.lineWidth = 1.6; ctx.stroke();
        if (cam.dist < 760) { ctx.font = "8.5px 'IBM Plex Mono',monospace"; ctx.textAlign = 'center'; ctx.fillStyle = 'rgba(217,176,84,' + pl + ')'; ctx.fillText('approval boundary', q.x, q.y - Math.max(8, 12 * q.s)); }
      });
    }

    // ---- interaction ----
    var drag = null, sphDrag = null, moved = false;
    function dragging() { return !!(drag || sphDrag); }
    function pickPoint(mx, my) {
      if (!focused) return null;
      var s = bySec[focused];
      if (cam.dist > s.R * 7.5) return null;
      var best = null, bd = 10;
      s.pts.forEach(function (p, i) { if (p._q && (p.rec || p.resident)) { var d = Math.hypot(p._q.x - mx, p._q.y - my); if (d < bd) { bd = d; best = i; } } });
      return best;
    }
    var api = {
      survey: function () { SEC.forEach(function (s) { s.frozen = null; }); focused = null; follow = false; selRec = null; unwind(); goal.yaw = -.3; goal.pitch = .4; goal.dist = fitDist(); goal.tgt = [0, 20, 0]; setCrumb('colony survey'); emit('deselect'); },
      focus: function (id) { var s = bySec[id]; if (!s) return; if (s.frozen == null) s.frozen = live() ? performance.now() * s.rot : 0; focused = id; follow = false; goal.tgt = s.pos.slice(); goal.dist = s.R * 4.6; setCrumb('colony survey → ' + s.label.toLowerCase()); emit('sector', s); },
      followMission: function () { follow = true; focused = null; goal.dist = 460; setCrumb('following active mission'); },
      resetView: function () { api.survey(); },
      resetLayout: function () { SEC.forEach(function (s) { s.pos = s.defPos.slice(); s.label = s.serverLabel || s.defLabel; s.renamed = false; s.style = { color: null, glow: 1, bright: 1 }; }); antStyles = {}; rebuildAll(); if (lastScene) api.setTopology(lastScene); saveLayout(); if (!focused) api.survey(); },
      resetAll: function () { api.resetLayout(); api.survey(); },
      renameSector: function (id, name) { var s = bySec[id]; if (s && name && name.trim()) { s.label = name.trim().toUpperCase().slice(0, 28); s.renamed = s.label !== s.serverLabel; saveLayout(); } },
      setLayout: applyLayout,
      getLayout: layoutSnapshot,
      zoom: function (f) { goal.dist = Math.max(90, Math.min(1500, goal.dist / (f || 1))); },
      setOptions: function (o) {
        o = o || {};
        var before = opts.conduits.density;
        /* A browser remembering a label mode this build no longer has heals to `all` rather than
           falling through to a default it never chose. `normal` was `.121`'s name for always-on;
           `.122` renamed it `fixed` and added `zoom` beside it; `.123` folded both into `all`,
           which IS the zoom behaviour — the operator asked for one setting, not a choice between
           two ways of showing everything. `min` and `none` are untouched and still mean what they
           always meant. */
        if (o.labels === 'normal' || o.labels === 'fixed' || o.labels === 'zoom') o.labels = 'all';
        if (o.links) { opts.links = Object.assign({}, opts.links, o.links); opts.links.opacity = clampNum(opts.links.opacity, 0, 1, .125); delete o.links; }
        if (o.conduits) { opts.conduits = Object.assign({}, opts.conduits, o.conduits); if (opts.conduits.color && !validColor(opts.conduits.color)) opts.conduits.color = null; opts.conduits.bright = clampNum(opts.conduits.bright, .4, 2, 1); delete o.conduits; }
        Object.assign(opts, o);
        // A browser that remembers `plane` is remembering an environment that no longer exists
        // (v0.3.8.122, the ground plane). It heals to `void` — the same black field, minus the floor
        // it was named for — rather than being left to fall through to a default it never chose.
        if (opts.env === 'plane') opts.env = 'void';
        if (opts.conduits.density !== before) buildStreams();
        if (root) root.classList.toggle('cl-light', isLight());
        restyleChrome();
      },
      getOptions: function () { return { motion: opts.motion, labels: opts.labels, trails: opts.trails, env: opts.env, conduits: Object.assign({}, opts.conduits), links: Object.assign({}, opts.links) }; },
      setSectorStyle: function (id, patch) { var s = bySec[id]; if (!s) return; patch = patch || {}; if ('color' in patch) s.style.color = validColor(patch.color); if ('glow' in patch) s.style.glow = clampNum(patch.glow, .5, 2.5, 1); if ('bright' in patch) s.style.bright = clampNum(patch.bright, .3, 2.5, 1); saveLayout(); },
      isMound: function (id) { var s = bySec[id]; return !!(s && s.mound); },
      isAddedMound: function (id) { var s = bySec[id]; return !!(s && s.added); },
      /** The roster every mound runs, from the server. Presentation only — see `addedMounds`. */
      setMoundDefaults: function (list) {
        if (!Array.isArray(list)) return;
        moundDefaults = list.filter(function (a) { return a && typeof a.name === 'string' && a.name; })
          .slice(0, 24).map(function (a) { return { name: String(a.name).slice(0, 40), role: String(a.role || '').slice(0, 80) }; });
        // A chamber added before the roster arrived fills in now rather than staying empty.
        SEC.forEach(function (s2) { if (s2.added) rebuildSector(s2, { residents: moundResidents(s2.id), records: [], clusters: [] }); });
      },
      /** Add a mound chamber. Returns its id. The label is the operator's from the first frame. */
      addMound: function (label) {
        var n = addedMounds.length + 1, id = 'mound:' + n;
        while (bySec[id]) { n++; id = 'mound:' + n; }
        var rec = { id: id, label: String(label || ('MICROMOUND ' + n)).toUpperCase().slice(0, 28), pos: nextMoundSeat(addedMounds.length) };
        addedMounds.push(rec);
        var s2 = mountAddedMound(rec);
        rebuildSector(s2, { residents: moundResidents(id), records: [], clusters: [] });
        rebuildAll(); saveLayout(); api.focus(id);
        return id;
      },
      /** Remove one. Only ever an ADDED chamber: the registry's own sectors are not the operator's
          to delete, and silently ignoring the difference is how a colony loses a real chamber. */
      removeMound: function (id) {
        var s2 = bySec[id]; if (!s2 || !s2.added) return false;
        SEC = SEC.filter(function (x) { return x.id !== id; });
        delete bySec[id];
        addedMounds = addedMounds.filter(function (m) { return m.id !== id; });
        if (focused === id) { focused = null; }
        rebuildAll(); saveLayout(); api.survey();
        return true;
      },
      /** EVERY MOUND CHAMBER, for the registry listing — not only the operator-added ones.
          v0.3.8.123.

          `.122` listed `added` chambers alone, so the registry showed nothing until you had used
          `+ Mound`, and INFRASTRUCTURE — a mound in every respect the renderer cares about, and the
          one an operator meets first — was absent from the page that exists to list mounds. The
          operator asked for it directly: "lets treat the infrastructure as a micromound, so have it
          be in the colony>mounds tab, even though its already been fully built in with roles."

          `removable` is the difference the registry has to render, because it is the difference
          `removeMound` enforces: a chamber the operator created is theirs to delete, and one the
          server declared is not. A Delete button that exists to be refused is worse than no button,
          so the flag travels with the row rather than the page guessing from the id. */
      listMounds: function () {
        return SEC.filter(function (x) { return x.mound; }).map(function (x) {
          return { id: x.id, label: x.label, color: x.style.color || x.color,
            residents: x.residents.length, removable: !!x.added, present: !!x.present };
        });
      },
      getSectorStyle: function (id) { var s = bySec[id]; return s ? { color: s.style.color, glow: s.style.glow, bright: s.style.bright, defaultColor: s.color } : null; },
      setAntStyle: function (roleId, patch) { var k = String(roleId || '').toLowerCase(); if (!k) return; var cur = antStyles[k] || { name: null, color: null }; patch = patch || {}; if ('name' in patch) cur.name = (typeof patch.name === 'string' && patch.name.trim()) ? patch.name.trim().slice(0, 28) : null; if ('color' in patch) cur.color = validColor(patch.color); if (cur.name || cur.color) antStyles[k] = cur; else delete antStyles[k]; if (lastScene) api.setTopology(lastScene); saveLayout(); },
      getAntStyle: function (roleId) { return Object.assign({ name: null, color: null }, antStyles[String(roleId || '').toLowerCase()] || {}); },
      stopMound: function (v) { bySec.mound.stopped = v !== false; },
      setTopology: function (scene) {
        lastScene = scene || lastScene;
        /* The reducer's scene (colony-topology.js `project()`):
             sectors[]     { id, label, residents[{roleId,name,status,workers[],trail}], runningTasks[],
                             records[{recordId,title,recordType,ant,missionId,taskId,createdAt,cluster,verification,place}],
                             recordCount, clusters[{id,label,count}] }
             edges[]       sector→sector links derived from real task edges
             transitions[] one-shot, event-backed arrivals { id, from, to, role }
             approvals[]   { approvalId, sector, resolved, title }
             mound         null | { present, mounds[{id, stopped, quiesced, ...}], globalStop }
             meta          { hydrated, partialHistory, runtime } */
        if (!scene || !Array.isArray(scene.sectors)) return;
        var named = {};
        scene.sectors.forEach(function (sec) {
          var s = bySec[sec.id]; if (!s) return;
          named[sec.id] = true;
          s.serverLabel = String(sec.label || s.defLabel).toUpperCase();
          if (!s.renamed) s.label = s.serverLabel;
          rebuildSector(s, sec);
        });
        SEC.forEach(function (s) {
          // An ADDED mound is the operator's, not the server's: the snapshot has never heard of it
          // and must not switch it off. Everything else is present exactly when the projection
          // says so, which is the rule that stops a chamber outliving the roles behind it.
          if (s.added) s.present = true;
          else if (s.id === 'mound') { s.present = !!(scene.mound && scene.mound.present); if (s.present) s.stopped = (scene.mound.mounds || []).some(function (m) { return m.stopped; }); }
          else s.present = !!named[s.id];
        });
        // The mission circuit: the chambers with RUNNING tasks, in the canonical order, from the Queen.
        var running = {}; scene.sectors.forEach(function (sec) { if ((sec.runningTasks || []).length) running[sec.id] = sec.runningTasks; });
        var path = SECTOR_ORDER.filter(function (id) { return id === 'queen' || (running[id] && bySec[id].present); });
        var unresolved = (scene.approvals || []).some(function (a) { return !a.resolved; });
        var atBoundary = (scene.approvals || []).some(function (a) { return a.resolved; });
        if (path.length > 1) routeFromSectorPath(path, atBoundary); else { circuit = []; buildStreams(); }
        // Ants: one per running task, riding the segment into its chamber. `label` is the role that
        // actually holds the task; nothing rides a segment without a task behind it.
        var segIdx = {}; path.forEach(function (id, i) { if (i > 0) segIdx[id] = i - 1; });
        ants = [];
        Object.keys(running).forEach(function (id) { if (segIdx[id] == null) return; running[id].forEach(function (t, k) { ants.push({ seg: segIdx[id], t: .15 + ((k * .23) % .6), sp: .00004, paused: false, label: t.role || '' }); }); });
        if (atBoundary && path.length > 1) ants.push({ seg: path.length - 2, t: .86, sp: 0, paused: true, label: 'awaiting approval' });
        // Evidence return valid → memory: only while Validation holds a record that PASSED evidence.
        var validSec = bySec.valid;
        if (validSec && validSec.present && validSec.counts && validSec.counts.verified > 0 && rootIndex['valid>memory'] != null) { retSeg = { pts: roots[rootIndex['valid>memory']].strands[0] }; if (!retStream.length) retStream = mkStream(retSeg.pts, 8, .00003, .00005); }
        else { retSeg = null; retStream = []; }
        // Recorded transitions play once each, from → to, when both ends are chambers we draw.
        (scene.transitions || []).forEach(function (tr) {
          if (!tr.id || playedTransitions[tr.id]) return; playedTransitions[tr.id] = true;
          if (scene.meta && scene.meta.history) return;
          var a = tr.from && bySec[tr.from], b = tr.to && bySec[tr.to];
          if (!a || !b || a === b || !a.present || !b.present) return;
          flights.push({ from: a.id, to: b.id, t: 0, sp: .00012, col: b.color, label: tr.role || '' });
        });
        attention = (scene.approvals || []).filter(function (a) { return !a.resolved; }).map(function (a) { return { sector: 'queen', kind: 'approval', label: a.title || 'approval needed' }; });
        partial = !!(scene.meta && scene.meta.partialHistory);
      },
      recordAt: function (secId, idx) {
        // Only a real record answers; a resident orb answers with its resident. Nothing else is there.
        var s = bySec[secId], p = s && s.pts[idx];
        return (p && (p.rec || p.resident)) || null;
      },
      sectorInfo: function (id) { var s = bySec[id]; return s ? { id: s.id, label: s.label, color: s.color, counts: s.counts, clusters: s.clusters, present: s.present } : null; },
      mount: mount, unmount: unmount, destroy: destroy, on: on
    };
    var handlers = {};
    function on(ev, fn) { (handlers[ev] = handlers[ev] || []).push(fn); }
    function emit(ev, data) { (handlers[ev] || []).forEach(function (fn) { fn(data); }); }

    function mount(el) {
      root = el;
      cv = document.createElement('canvas');
      cv.style.cssText = 'position:absolute;top:0;left:0;display:block;cursor:grab'; el.classList.toggle('cl-light', isLight());
      tip = document.createElement('div');
      tip.style.cssText = 'position:fixed;display:none;z-index:60;background:rgba(6,8,10,.94);border:1px solid rgba(255,255,255,.12);border-radius:8px;padding:8px 11px;pointer-events:none;min-width:150px;font:9px "IBM Plex Mono",monospace;color:#8b93a8';
      crumb = document.createElement('div');
      crumb.style.cssText = 'position:absolute;top:10px;left:14px;font:9.5px "IBM Plex Mono",monospace;color:rgba(185,194,207,.45);pointer-events:none';
      crumb.textContent = 'colony survey';
      // Sit directly behind the HUD: insert right after the classic canvas (#c) when the mount has
      // one, so the legend, viewbar and anchor overlays keep painting above the formicarium.
      var anchor = el.querySelector('canvas');
      if (anchor && anchor !== cv) { el.insertBefore(crumb, anchor.nextSibling); el.insertBefore(cv, anchor.nextSibling); }
      else { el.appendChild(cv); el.appendChild(crumb); }
      document.body.appendChild(tip); restyleChrome();
      // Render at the device's pixel ratio. A 1:1 backing store on a 125% or 150% display is
      // upscaled by the compositor, which softens every 1px grain and dims the whole colony —
      // the same build looked crisp in one browser and muddy in another for exactly this reason.
      function fit() {
        var rc = el.getBoundingClientRect(), dpr = Math.min(2, window.devicePixelRatio || 1);
        W = Math.max(50, rc.width); H = Math.max(50, rc.height); scx = W / 2; scy = H / 2;
        cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr); cv.style.width = W + 'px'; cv.style.height = H + 'px';
        if (ctx) ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        if (!focused && !follow) goal.dist = fitDist();
      }
      fit(); cam.dist = goal.dist; ctx = cv.getContext('2d'); fit();
      ro = new ResizeObserver(fit); ro.observe(el);
      cv.addEventListener('mousedown', onDown);
      window.addEventListener('mousemove', onMove);
      window.addEventListener('mouseup', onUp);
      cv.addEventListener('wheel', onWheel, { passive: false });
      cv.addEventListener('click', onClick);
      cv.addEventListener('mousemove', onHover);
      cv.addEventListener('mouseleave', function () { hovPt = null; tip.style.display = 'none'; });
      window.addEventListener('keydown', onKey);
      raf = requestAnimationFrame(frame);
    }
    function unmount() {
      cancelAnimationFrame(raf);
      if (ro) ro.disconnect();
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
      window.removeEventListener('keydown', onKey);
      if (tip && tip.parentNode) tip.parentNode.removeChild(tip);
      if (cv && cv.parentNode) cv.parentNode.removeChild(cv);
      if (crumb && crumb.parentNode) crumb.parentNode.removeChild(crumb);
    }
    function destroy() { destroyed = true; unmount(); }
    function local(e) { var rc = cv.getBoundingClientRect(); return { x: e.clientX - rc.left, y: e.clientY - rc.top }; }
    function onDown(e) {
      moved = false;
      var m = local(e);
      if (pickPoint(m.x, m.y) == null) {
        // Moving a chamber is deliberate: grab its nucleus, or hold Shift anywhere on it. A drag that
        // starts on the shell orbits — otherwise every orbit that began over a chamber rearranged the
        // colony and persisted the accident.
        var vis = shown();
        for (var i = 0; i < vis.length; i++) {
          var s = vis[i], pr = proj(s.pos); if (!pr) continue;
          var d = Math.hypot(pr.x - m.x, pr.y - m.y), grabR = e.shiftKey ? Math.max(20, s.R * pr.s) : Math.max(10, s.R * pr.s * .34);
          if (d < grabR) { sphDrag = { s: s, x: e.clientX, y: e.clientY, orig: s.pos.slice(), zc: pr.zc }; cv.style.cursor = 'grabbing'; return; }
        }
      }
      drag = { x: e.clientX, y: e.clientY, yaw: cam.yaw, pitch: cam.pitch };
      cv.style.cursor = 'grabbing';
    }
    function onMove(e) {
      if (sphDrag) {
        var dx = e.clientX - sphDrag.x, dy = e.clientY - sphDrag.y;
        if (Math.abs(dx) + Math.abs(dy) > 3) moved = true;
        var k = sphDrag.zc / 780, ddx = dx * k, ddy = dy * k;
        var cp = Math.cos(cam.pitch), sp = Math.sin(cam.pitch), cyw = Math.cos(cam.yaw), syw = Math.sin(cam.yaw);
        var py = ddy * cp, pz = -ddy * sp;
        var s = sphDrag.s;
        s.pos[0] = sphDrag.orig[0] + ddx * cyw + pz * syw;
        s.pos[1] = sphDrag.orig[1] + py;
        s.pos[2] = sphDrag.orig[2] - ddx * syw + pz * cyw;
        rebuildAll();
        if (focused === s.id) goal.tgt = s.pos.slice();
        tip.style.display = 'none';
        return;
      }
      if (!drag) return;
      var dx2 = e.clientX - drag.x, dy2 = e.clientY - drag.y;
      if (Math.abs(dx2) + Math.abs(dy2) > 3) moved = true;
      goal.yaw = drag.yaw + dx2 * .0035;
      // PITCH IS FREE, LIKE YAW (v0.3.8.122). It was clamped to [0.05, 1.15] rad — about 3° to 66°,
      // a band that kept the camera above the ground plane and looking slightly down at it. With the
      // plane gone there is nothing under the colony to be on the wrong side of, and the operator
      // asked to be able to go under it. The projection is a plain two-axis rotation and the
      // chambers are painted back-to-front by `zc` every frame, so every angle draws correctly:
      // overhead, edge-on, and from below with the colony inverted, which is what a full orbit means.
      goal.pitch = drag.pitch + dy2 * .003;
      follow = false;
    }
    function onUp() { if (sphDrag && moved) saveLayout(); if (drag || sphDrag) cv.style.cursor = 'grab'; drag = null; sphDrag = null; }
    function onWheel(e) { e.preventDefault(); goal.dist = Math.max(90, Math.min(1500, goal.dist * (e.deltaY > 0 ? 1.09 : .92))); }
    function onClick(e) {
      if (moved) return;
      var m = local(e);
      var pi = pickPoint(m.x, m.y);
      if (pi != null) { selRec = { sec: focused, idx: pi }; var s = bySec[focused]; var w = s.pts[pi]._w; if (w) { goal.tgt = w.slice(); goal.dist = Math.max(120, s.R * 2.2); } var rec = api.recordAt(focused, pi); if (!rec) return; if (rec.roleId) { setCrumb('colony survey → ' + s.label.toLowerCase() + ' → ' + rec.name); emit('resident', { sector: focused, index: pi, resident: rec }); return; } setCrumb('colony survey → ' + s.label.toLowerCase() + ' → ' + rec.title); emit('record', { sector: focused, index: pi, record: rec }); return; }
      var vis = shown();
      for (var i = 0; i < vis.length; i++) {
        var s2 = vis[i], pr = proj(s2.pos);
        if (pr && Math.hypot(pr.x - m.x, pr.y - m.y) < Math.max(20, s2.R * pr.s)) {
          selRec = null;
          /* A MOUND CHAMBER IS A CHAMBER. v0.3.8.124.

             `.122` made a mound's second click a door to its settings page. The intent was
             reachability; the effect was that the one chamber an operator most wanted to recolour
             and rename was the one chamber where a second click threw them out of the colony view
             — and the panel they were using went with it. Every other chamber rewards a second
             click by staying put.

             Settings now live in ONE place, the registry, which is reachable from the Mounds button
             beside `+ Mound` and from this chamber's own panel. A chamber here behaves like every
             other chamber: focus, look, rename, recolour. */
          api.focus(s2.id); return;
        }
      }
      if (selRec) { selRec = null; var sf = bySec[focused]; if (sf) { goal.tgt = sf.pos.slice(); goal.dist = sf.R * 4.6; setCrumb('colony survey → ' + sf.label.toLowerCase()); emit('sector', sf); } else emit('deselect'); return; }
      api.survey();
    }
    function onKey(e) { if (e.key === 'Escape') { if (selRec) onClick({ clientX: -9999, clientY: -9999 }); else api.survey(); } }
    function onHover(e) {
      var m = local(e);
      var pi = dragging() ? null : pickPoint(m.x, m.y);
      hovPt = pi;
      if (pi != null) {
        var s = bySec[focused], r = api.recordAt(focused, pi);
        if (!r) { hovPt = null; tip.style.display = 'none'; return; }
        cv.style.cursor = 'pointer';
        tip.innerHTML = '<div style="font-size:10.5px;font-weight:600;color:' + s.color + ';margin-bottom:2px"></div>';
        var sub = document.createElement('div');
        if (r.roleId) { tip.firstChild.textContent = r.name; sub.textContent = (r.worker ? 'worker of ' + r.parent : 'role') + ' · ' + r.status + (r.trail && isFinite(r.trail.strength) ? ' · trail ' + Number(r.trail.strength).toFixed(2) : ''); }
        else { tip.firstChild.textContent = r.title; sub.textContent = r.type + ' · ' + r.ant + ' · ' + r.time + ' · ' + r.verif; }
        tip.appendChild(sub);
        tip.style.display = 'block'; tip.style.left = (e.clientX + 14) + 'px'; tip.style.top = (e.clientY - 10) + 'px';
        return;
      }
      var hit = null;
      var vis2 = shown(); for (var i = 0; i < vis2.length; i++) { var s2 = vis2[i], pr = proj(s2.pos); if (pr && Math.hypot(pr.x - m.x, pr.y - m.y) < Math.max(18, s2.R * pr.s)) { hit = s2; break; } }
      if (hit && !dragging()) {
        var hpr = proj(hit.pos), overNucleus = hpr && Math.hypot(hpr.x - m.x, hpr.y - m.y) < Math.max(10, hit.R * hpr.s * .34);
        cv.style.cursor = overNucleus ? 'move' : 'pointer';
        var hc = hit.counts || {};
        tip.textContent = hit.label + ' · ' + (hc.records ? hc.records + ' record' + (hc.records === 1 ? '' : 's') : 'no records') + (hc.verified ? ' (' + hc.verified + ' verified)' : '') + ' · ' + (hc.residents || 0) + ' resident' + (hc.residents === 1 ? '' : 's') + (hc.running ? ' · ' + hc.running + ' running' : '');
        tip.style.display = 'block'; tip.style.left = (e.clientX + 14) + 'px'; tip.style.top = (e.clientY - 10) + 'px';
      } else { if (!dragging()) cv.style.cursor = 'grab'; tip.style.display = 'none'; }
    }
    return api;
  }
  window.ColonyLive = { create: create };
})();
