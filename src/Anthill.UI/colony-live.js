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
    { id: 'homelab', label: 'HOMELAB', color: '#5aa07a', core: '#9ad4b0', pos: [-330, 90, -40], R: 48, n: 0, rot: .00004 },
    { id: 'unassigned', label: 'UNASSIGNED', color: '#8a98ad', core: '#c3cad6', pos: [340, 200, 60], R: 40, n: 0, rot: .00005 },
    { id: 'mound', label: 'MICROMOUND', color: '#a55a7e', core: '#c9cfdc', pos: [-95, 265, 70], R: 34, n: 110, rot: .00006 }
  ];
  // Ids are the server's (ColonySectors); labels are its DEFAULTS, overridable per operator in the
  // persisted layout. Positions are constants — a stable spatial grammar — until the operator drags.
  var SECTOR_ORDER = ['queen', 'intel', 'forge', 'valid', 'memory', 'output', 'homelab', 'unassigned', 'mound'];
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
    var opts = { motion: 'normal', labels: 'normal', trails: true, env: 'space' };   // env: space (galaxy, default) | strata | plane | nebula | void
    var live = function () { return !reduced && opts.motion !== 'off'; };

    var rnd = lcg(42);
    var SEC = SECTOR_DEFS.map(function (d) { return Object.assign({ morph: 0, frozen: null, defPos: d.pos.slice(), defLabel: d.label, present: false, pts: [], links: [], records: [], residents: [], clusters: [], counts: null }, d, { pos: d.pos.slice() }); });
    var bySec = {}; SEC.forEach(function (s) { bySec[s.id] = s; });
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
    var authority = mkRoot('queen', 'mound', 1, 20);
    function rebuildAll() { roots.forEach(rebuildRoot); rebuildRoot(authority); }

    // active route + ants — REPLACED wholesale by setTopology; demo defaults below
    var rootIndex = {}; roots.forEach(function (r, i) { rootIndex[r.a + '>' + r.b] = i; rootIndex[r.b + '>' + r.a] = i; });
    var circuit = [], retSeg = null, ants = [], attention = [], partial = false;
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
    var GOLDEN = Math.PI * (3 - Math.sqrt(5)), SPIRAL = 2.399963;
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
    function rebuildSector(s, sec) {
      var old = {}; s.pts.forEach(function (p) { if (p.rec) old[p.rec.id] = p; });
      var pts = [], links = [];
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
          var durable = (verified ? .55 : .1) + pher * .45, depth = 1 - Math.min(.94, durable);
          var dir = [unit(id, 'dx') * 2 - 1, unit(id, 'dy') * 2 - 1, unit(id, 'dz') * 2 - 1], len = Math.hypot(dir[0], dir[1], dir[2]) || 1;
          var spread = s.R * .16, kk = .5 + depth * .75;
          var o = [cl.center[0] * kk + dir[0] / len * spread, cl.center[1] * kk + dir[1] / len * spread, cl.center[2] * kk + dir[2] / len * spread];
          var ang = k * SPIRAL, rad = s.R * .86 * band * Math.sqrt((k + .55) / mcount);
          var org = [Math.cos(ang) * rad, y, Math.sin(ang) * rad];
          var rec = { id: id, title: r.title || r.recordType || 'record', type: r.recordType || r.type || 'record', ant: r.ant || '—', mission: r.missionId || '', taskId: r.taskId || '', time: r.createdAt || '', verif: r.verification || 'not_scanned', cluster: cl.id, pher: pher };
          var prev = old[id], pt = prev || { born: performance.now(), ph: place.b * TAU, rec: null };
          var radN = Math.min(1, Math.hypot(o[0], o[1], o[2]) / s.R), edge = 1 - .72 * Math.pow(radN, 2.6);
          if (prev && (Math.abs(prev.o[0] - o[0]) + Math.abs(prev.o[1] - o[1]) + Math.abs(prev.o[2] - o[2])) > .5) pt.settle = { from: prev.o.slice(), to: o.slice(), t: 0 };
          pt.o = o; pt.org = org; pt.layer = verified ? 2 : 0; pt.cl = ci; pt.stratum = ci;
          pt.sz = (1.15 + pher * 1.7) * (.72 + .28 * edge) * .9; pt.a = Math.min(1, .82 + pher * .2) * (.86 + .14 * edge); pt.coreMix = Math.min(1, Math.pow(1 - radN, 1.5) * 1.15);
          pt.rec = rec; pt.resident = null;
          pts.push(pt);
        });
      });
      // residents: one orb per role on the mid ring, its workers as smaller orbs beside it; in the
      // ordered formation they line up on a row above the top stratum
      var top = s.strata.length ? s.strata[0].y - s.R * .42 : -s.R * .4;   // −y is up: the row sits over the highest level
      (sec.residents || []).forEach(function (r, ri) {
        var n = Math.max(1, (sec.residents || []).length), th = ri / n * TAU + .7, yy = Math.sin(ri * 2.4) * .25;
        var base = [Math.cos(th) * s.R * .55, yy * s.R, Math.sin(th) * s.R * .55];
        var rowX = ((ri + .5) / n - .5) * s.R * 2.2;
        pts.push({ o: base, org: [rowX, top, 0], layer: 1, cl: 0, sz: 2.4, a: .95, ph: ri, born: 0, rec: null, below: !!(ri % 2), resident: { roleId: r.roleId, name: r.name || r.roleId, status: r.status, trail: r.trail || null, workers: (r.workers || []).length } });
        var roleIdx = pts.length - 1;
        (r.workers || []).forEach(function (w, wi) {
          var wn = (r.workers || []).length, wt = th + (wi - (wn - 1) / 2) * .28;
          pts.push({ o: [Math.cos(wt) * s.R * .68, base[1] + (wi % 2 ? .08 : -.08) * s.R, Math.sin(wt) * s.R * .68], org: [rowX + (wi - (wn - 1) / 2) * s.R * .12, top - s.R * .16, 0], layer: 1, cl: 0, sz: 1.4, a: .8, ph: wi, born: 0, rec: null, resident: { roleId: w.id, name: w.name || w.id, parent: w.parent || r.roleId, status: w.enabled === false ? 'disabled' : r.status, worker: true } });
          links.push([pts.length - 1, roleIdx]);   // the roster chain: worker → its role
        });
      });
      // a mission's thread through this chamber: records sharing a mission_id, in recorded order
      var byMission = {}; pts.forEach(function (p, i) { if (p.rec && p.rec.mission) (byMission[p.rec.mission] = byMission[p.rec.mission] || []).push(i); });
      Object.keys(byMission).forEach(function (mkey) { var list = byMission[mkey].sort(function (a, b) { return pts[a].rec.time < pts[b].rec.time ? -1 : 1; }); if (list.length < 2) return; for (var i = 1; i < list.length; i++) links.push([list[i - 1], list[i]]); });
      s.pts = pts; s.links = links;
      s.records = sec.records || []; s.residents = sec.residents || []; s.clusters = sec.clusters || [];
      s.counts = { records: sec.recordCount != null ? sec.recordCount : s.records.length, running: (sec.runningTasks || []).length, residents: s.residents.length, verified: s.records.filter(function (r) { return r.verification === 'verified'; }).length };
    }
    // one-shot flights: a recorded transition plays once, ant from → to, then is done
    var flights = [], playedTransitions = {};

    // pheromone streams (the 3h connection language: particles, not lines)
    var rootStreams = [], circStreams = [], retStream = [], authStream = [];
    function mkStream(pts, n, s0, s1) { var out = []; for (var i = 0; i < n; i++) out.push({ pts: pts, t: rnd(), sp: s0 + rnd() * (s1 - s0), n: (rnd() - .5) * 10, ph: rnd() * TAU }); return out; }
    function buildStreams() {
      // pace (mockup 2a): unhurried. A particle takes ~25–60 s to cross a root; the circuit is the
      // fastest thing on screen and still takes ~15 s a segment.
      rootStreams = roots.filter(function (r) { return r.a === 'queen'; }).map(function (r) { return mkStream(r.strands[0], 6, .000016, .00003); });
      circStreams = circuit.map(function (sg) { return { col: sg.col, ps: mkStream(sg.pts, 22, .00004, .00007) }; });
      retStream = retSeg ? mkStream(retSeg.pts, 8, .00003, .00005) : [];
      authStream = mkStream(authority.strands[0], 7, .00002, .000035);
    }

    // 3c galaxy environment: world-space stars + dust so everything parallaxes
    var DUST = [], STARS = [];
    for (var i = 0; i < 150; i++) DUST.push({ p: [(rnd() - .5) * 1080, (rnd() - .5) * 760, (rnd() - .5) * 560], sp: .008 + rnd() * .02, ph: rnd() * TAU });
    for (var j = 0; j < 110; j++) { var u2 = rnd() * 2 - 1, th2 = rnd() * TAU, sq2 = Math.sqrt(1 - u2 * u2), RR = 820 + rnd() * 380; STARS.push({ p: [sq2 * Math.cos(th2) * RR, u2 * RR * .7, sq2 * Math.sin(th2) * RR], sz: rnd() < .82 ? .7 : 1.5, ph: rnd() * TAU }); }
    // ENVIRONMENTS (design doc §17, operator review): nothing is painted flat on the glass any more.
    // Every light in the sky is a point in WORLD space that the camera projects, so a drag moves
    // it like everything else, and the plane's light comes from sources that are never drawn.
    //   strata — (default; mockup 2a = 3a + 3h) the formicarium's cross-section: soil-strata bands
    //            and contour lines behind the colony, and the dust motes in TRUE 3D so they parallax
    //            with every orbit, zoom and pan. No galaxy, no band, no blobs.
    //   plane  — a ground beneath the colony lit by unseen sources: soft pools that glint as you
    //            orbit (real reflection against the camera position) and each sector's own tint
    //            cast on the ground under it, brighter when it is on the active circuit.
    //   space  — a star sphere (varied, a few tinted) and a galactic haze along an INCLINED GREAT
    //            CIRCLE in world space — a 3D band that swings with the camera, not a stripe.
    //   nebula — layered gas at several depths, each patch tinted by the nearest sector, so the
    //            sectors appear to light the gas around them; heavy parallax.
    //   void   — black; the sectors are the only light, with their glow on a faint ground disc.
    var PLANE_Y = 340;
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
    var LIGHTS = [ // unseen sources above the plane, for 'plane': never drawn, only their pools
      { p: [-320, -560, 140], c: '118,150,225', r: 300, ph: 0 },
      { p: [340, -500, -110], c: '226,110,170', r: 260, ph: 2.1 },
      { p: [30, -640, 380], c: '96,196,206', r: 340, ph: 4.2 }];
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

    // operator layout: positions and name overrides. Emitted to the host, which persists them in
    // /ui/state beside the console's other layout; applied back through setLayout. One store.
    function layoutSnapshot() {
      var positions = {}, names = {};
      SEC.forEach(function (s) { positions[s.id] = s.pos.slice(); if (s.label !== s.defLabel) names[s.id] = s.label; });
      return { schema: LAYOUT_SCHEMA, positions: positions, names: names };
    }
    function saveLayout() { emit('layout', layoutSnapshot()); }
    function applyLayout(l) {
      if (!l || l.schema !== LAYOUT_SCHEMA) return false;   // an older schema resets rather than migrates
      SEC.forEach(function (s) {
        var p = l.positions && l.positions[s.id];
        if (Array.isArray(p) && p.length === 3 && p.every(function (n) { return typeof n === 'number' && isFinite(n) && Math.abs(n) <= 1200; })) s.pos = p.slice();
        var nm = l.names && l.names[s.id];
        if (typeof nm === 'string' && nm.trim()) { s.label = nm.trim().toUpperCase().slice(0, 28); s.renamed = true; }
      });
      rebuildAll(); return true;
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
    function camPos() { return [cam.tgt[0] - cam.dist * LT.cp * LT.syw, cam.tgt[1] - cam.dist * LT.sp, cam.tgt[2] - cam.dist * LT.cp * LT.cyw]; }
    function softPoint(q, r, c, a) { if (!q || a <= 0) return; var rr = Math.max(2, r * q.s); var g = ctx.createRadialGradient(q.x, q.y, 0, q.x, q.y, rr); g.addColorStop(0, 'rgba(' + c + ',' + a + ')'); g.addColorStop(1, 'rgba(' + c + ',0)'); ctx.beginPath(); ctx.arc(q.x, q.y, rr, 0, TAU); ctx.fillStyle = g; ctx.fill(); }
    /** A lit pool on the ground plane: the projected ellipse, filled with a falloff at its centre. */
    function planePool(cx, cz, r, c, a) {
      if (a <= 0.002) return;
      var q = proj([cx, PLANE_Y, cz]); if (!q) return;
      ctx.beginPath();
      for (var i = 0; i < 30; i++) { var th = i / 30 * TAU, pq = proj([cx + Math.cos(th) * r, PLANE_Y, cz + Math.sin(th) * r]); if (!pq) return; if (i) ctx.lineTo(pq.x, pq.y); else ctx.moveTo(pq.x, pq.y); }
      ctx.closePath();
      var g = ctx.createRadialGradient(q.x, q.y, 0, q.x, q.y, Math.max(3, r * q.s));
      g.addColorStop(0, 'rgba(' + c + ',' + a + ')'); g.addColorStop(.55, 'rgba(' + c + ',' + (a * .35) + ')'); g.addColorStop(1, 'rgba(' + c + ',0)');
      ctx.fillStyle = g; ctx.fill();
    }
    function activeSectors() { var set = {}; circuit.forEach(function (sg) { SEC.forEach(function (s) { if (s.color === sg.col) set[s.id] = true; }); }); if (circuit.length) set.queen = true; return set; }
    function drawStars(list, base, ts) { list.forEach(function (st) { var q = proj(st.p); if (!q) return; var tw = .55 + Math.sin(ts * .0011 + st.ph) * .35; ctx.beginPath(); ctx.arc(q.x, q.y, st.sz, 0, TAU); ctx.fillStyle = 'rgba(' + (st.c || '220,228,245') + ',' + (base * tw) + ')'; ctx.fill(); }); }
    function drawDust(ts) { DUST.forEach(function (d) { if (live()) { d.p[1] -= d.sp * 1.4; if (d.p[1] < -400) d.p[1] = 400; } var q = proj(d.p); if (!q) return; var tw = .55 + Math.sin(ts * .0009 + d.ph) * .35; ctx.beginPath(); ctx.arc(q.x, q.y, Math.max(.4, q.s), 0, TAU); ctx.fillStyle = 'rgba(172,182,208,' + (.10 * tw * fog(q.zc)) + ')'; ctx.fill(); }); }
    function envGround(ts, withPools) {
      var act = activeSectors(), pulse = live() ? .5 + .5 * Math.sin(ts * .0016) : .5;
      // the ground itself: a wide, very faint disc so the pools have something to land on
      planePool(0, 40, 780, '120,130,160', .028 * LT.expo);
      if (withPools) {
        var V = camPos();
        LIGHTS.forEach(function (L) {
          var px = L.p[0] + Math.sin(cam.yaw + L.ph) * 30, pz = L.p[2] + Math.cos(cam.yaw + L.ph) * 30;   // the pool leans with the view
          // glint: reflect the light off the plane (normal points up, i.e. -y) toward the camera
          var ix = px - L.p[0], iy = PLANE_Y - L.p[1], iz = pz - L.p[2], il = Math.hypot(ix, iy, iz) || 1; ix /= il; iy /= il; iz /= il;
          var rx = ix, ry = -iy, rz = iz;   // reflection about the plane normal
          var vx = V[0] - px, vy = V[1] - PLANE_Y, vz = V[2] - pz, vl = Math.hypot(vx, vy, vz) || 1;
          var spec = Math.pow(Math.max(0, (rx * vx + ry * vy + rz * vz) / vl), 6);
          var breathe = live() ? .85 + .15 * Math.sin(ts * .0004 + L.ph) : 1;
          planePool(px, pz, L.r, L.c, (.075 + .16 * spec) * breathe * LT.expo);
        });
      }
      // each sector lights the ground beneath it with its own colour; the active circuit burns brighter
      shown().forEach(function (s) {
        var c = h2(s.color).join(','), h = Math.max(60, PLANE_Y - s.pos[1]);
        var a = (act[s.id] ? .09 + .05 * pulse : .04) * Math.min(1, 260 / h) * LT.expo;
        planePool(s.pos[0], s.pos[2], s.R * 2.1 + h * .25, c, a);
      });
    }
    function envStrata(ts) {
      // soil, darker and warmer with depth; the whole section slides a little with the camera so
      // the backdrop answers a drag without pretending to be geometry
      var oy = cam.pitch * 40, ox = cam.yaw * 24;
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
    function drawEnv(ts) {
      if (opts.env === 'strata') { envStrata(ts); return; }
      ctx.fillStyle = opts.env === 'void' ? '#030304' : '#050607'; ctx.fillRect(0, 0, W, H);
      if (opts.env === 'space') { drawGalaxy(ts); drawDust(ts); }
      else if (opts.env === 'nebula') {
        FOG.forEach(function (f) {
          var q = proj(f.p); if (!q) return;
          var near = null, nd = 1e9; shown().forEach(function (s) { var d = Math.hypot(s.pos[0] - f.p[0], s.pos[1] - f.p[1], (s.pos[2] - f.p[2]) * .35); if (d < nd) { nd = d; near = s; } });
          var c = near ? h2(near.color).join(',') : '120,120,160', drift = live() ? Math.sin(ts * .0002 + f.ph) * 12 : 0;
          softPoint({ x: q.x + drift, y: q.y, s: q.s }, f.r, c, f.a * Math.min(1, 520 / nd) * LT.expo);
        });
        drawStars(STARS, .3, ts); drawDust(ts);
      } else if (opts.env === 'void') {
        envGround(ts, false); drawStars(STARS, .16, ts);
      } else { // plane
        envGround(ts, true); drawStars(STARS, .3, ts); drawDust(ts);
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
      // strokes are a whisper ghost; the particles are the connection
      roots.forEach(function (r) { if (!bySec[r.a].present || !bySec[r.b].present) return; var carries = flowing.indexOf(r.strands[0]) >= 0; if (r.a === 'queen' || carries) drawStrand(r.strands[0], 'rgba(146,158,176,$A)', carries ? .035 : .02, 2.2, ts); });
      if (bySec.mound.present) authority.strands.forEach(function (st) { drawStrand(st, 'rgba(226,31,123,$A)', .03, 2.2, ts); });
      rootStreams.forEach(function (ps, i) { var r = roots.filter(function (x) { return x.a === 'queen'; })[i]; if (r && r.b && bySec[r.b].present) drawStream(ps, '146,158,176', .3); });
      if (bySec.mound.present) drawStream(authStream, '226,31,123', .4);
      var dens = opts.motion === 'low' ? .55 : 1;
      circStreams.forEach(function (cs, ci) { var c = h2(cs.col); drawStream(cs.ps.slice(0, Math.ceil(cs.ps.length * dens)), c[0] + ',' + c[1] + ',' + c[2], .8, 1.5, circuit[ci] && circuit[ci].rev); });
      if (opts.trails) drawStream(retStream, '217,176,84', .6, 1.3);
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
        var rot = s.frozen != null ? s.frozen : (live() ? ts * s.rot : 0);
        var cr = Math.cos(rot), sr = Math.sin(rot);
        var c0 = h2(s.color), c1 = h2(s.core);
        var selHere = selRec && selRec.sec === s.id ? selRec.idx : null;
        var relSet = selHere != null && s.pts[selHere].rec ? (s.pts[selHere].rec.rel || []) : null;
        var wantMorph = isFocused && cam.dist < s.R * 5.5 ? 1 : 0;
        s.morph += (wantMorph - s.morph) * .05;
        var m = s.morph;
        var nr = s.R * .34 * pr.s;
        var nuc = s.id === 'queen' ? '232,178,90' : c1.join(',');
        var g = ctx.createRadialGradient(pr.x, pr.y, 0, pr.x, pr.y, Math.max(4, nr * 2.2));
        g.addColorStop(0, 'rgba(' + nuc + ',' + (.3 * (1 - m * .65) * LT.expo * fog(pr.zc)) + ')'); g.addColorStop(1, 'rgba(' + nuc + ',0)');
        ctx.beginPath(); ctx.arc(pr.x, pr.y, Math.max(4, nr * 2.2), 0, TAU); ctx.fillStyle = g; ctx.fill();
        var lo = lightOffset(s, pr);
        if (lo.front) {
          var hr = Math.max(3, nr * 1.4), hg = ctx.createRadialGradient(pr.x + lo.dx, pr.y + lo.dy, 0, pr.x + lo.dx, pr.y + lo.dy, hr);
          hg.addColorStop(0, 'rgba(235,240,250,' + (.10 * LT.expo * fog(pr.zc)) + ')'); hg.addColorStop(1, 'rgba(235,240,250,0)');
          ctx.beginPath(); ctx.arc(pr.x + lo.dx, pr.y + lo.dy, hr, 0, TAU); ctx.fillStyle = hg; ctx.fill();
        }
        ctx.strokeStyle = 'rgba(' + c0.join(',') + ',' + ((isFocused ? .1 : .06) * fog(pr.zc)) + ')'; ctx.lineWidth = .6;
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
          if (res) col = res.status === 'working' ? c1.join(',') : res.status === 'disabled' ? '110,118,134' : c0.join(',');
          else { var mix = p.layer === 2 ? 1 : (p.coreMix || 0); col = Math.round(c0[0] + (c1[0] - c0[0]) * mix) + ',' + Math.round(c0[1] + (c1[1] - c0[1]) * mix) + ',' + Math.round(c0[2] + (c1[2] - c0[2]) * mix); }
          var tw = res ? (res.status === 'working' && live() ? .8 + Math.sin(ts * .004 + p.ph) * .2 : 1) : (live() && p.layer === 0 ? .85 + Math.sin(ts * .0012 + p.ph) * .15 : 1);
          var hp = isFocused && hovPt === pi;
          var sh = shadeAt(w, s.pos);                 // lit hemisphere + rim, per point, per frame
          // grains grow as the strata form, so a level's records read as a row and not as dust
          var rad = Math.max(.6, p.sz * q.s * (.95 + .5 * sh) * (res ? 1 : 1 + m * .9)) * (hp ? 1.5 : 1);
          ctx.beginPath(); ctx.arc(q.x, q.y, rad, 0, TAU);
          ctx.fillStyle = 'rgba(' + col + ',' + Math.min(1, a * tw * (.7 + .8 * sh) * LT.expo * (hp ? 1.4 : 1)) + ')'; ctx.fill();
          if (res && res.status === 'working') { ctx.beginPath(); ctx.arc(q.x, q.y, rad + 3, 0, TAU); ctx.strokeStyle = 'rgba(' + c1.join(',') + ',' + (.35 * tw) + ')'; ctx.lineWidth = 1; ctx.stroke(); }
          if (res && !res.worker && isFocused && m > .25) label(res.name, q.x, p.below ? q.y + rad + 11 : q.y - rad - 6, "8px 'IBM Plex Mono',monospace", 'rgba(201,210,221,' + (.75 * m) + ')', 'center');
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
        if (s.id === 'mound') {
          for (var k2 = 0; k2 < 6; k2++) { var th3 = k2 * 1.047 + .5; var w2 = [s.pos[0] + Math.cos(th3) * s.R * .62, s.pos[1] + Math.sin(th3) * s.R * .5, s.pos[2] + Math.sin(th3 * 2) * 8]; var q2 = proj(w2); if (q2) { ctx.beginPath(); ctx.arc(q2.x, q2.y, Math.max(.8, 1.6 * q2.s), 0, TAU); ctx.fillStyle = 'rgba(201,207,220,' + (.5 * fog(q2.zc)) + ')'; ctx.fill(); } }
          if (s.stopped) { ctx.beginPath(); ctx.arc(pr.x, pr.y, s.R * 1.2 * pr.s, 0, TAU); ctx.strokeStyle = 'rgba(226,31,123,.8)'; ctx.lineWidth = 2.2; ctx.stroke(); }
        }
        if (opts.labels !== 'min' || isFocused || s.id === 'queen') {
          label(s.label + (s.id === 'mound' && s.stopped ? ' · STOPPED' : ''), pr.x, pr.y + (s.R + 18) * pr.s,
            '600 ' + Math.max(8, Math.min(11, 9 * pr.s * 8)) + "px 'IBM Plex Mono',monospace", 'rgba(' + c0.join(',') + ',' + ((isFocused ? .85 : .5) * fog(pr.zc)) + ')', 'center');
        }
        if (isFocused && m > .25 && s.strata && s.strata.length) {
          // one label per stratum, at the level's right edge (rotates with the chamber), each on its
          // own level so labels cannot stack; the level's ring is a faint guide under its records
          s.strata.forEach(function (st) {
            var lx = s.R * .92 * st.band, ex = lx * cr, ez = lx * sr;
            var q = proj([s.pos[0] + ex, s.pos[1] + st.y, s.pos[2] + ez]); if (!q) return;
            ctx.beginPath();
            for (var ai = 0; ai <= 36; ai++) { var aa = ai / 36 * TAU, rx = Math.cos(aa) * s.R * .86 * st.band, rz = Math.sin(aa) * s.R * .86 * st.band, rq = proj([s.pos[0] + rx * cr - rz * sr, s.pos[1] + st.y, s.pos[2] + rx * sr + rz * cr]); if (!rq) { ai = 99; break; } if (ai) ctx.lineTo(rq.x, rq.y); else ctx.moveTo(rq.x, rq.y); }
            ctx.strokeStyle = 'rgba(' + c0.join(',') + ',' + (.08 * m) + ')'; ctx.lineWidth = .8; ctx.stroke();
            ctx.font = "600 9px 'IBM Plex Mono',monospace"; ctx.textAlign = 'left';
            ctx.fillStyle = 'rgba(201,210,221,' + (m * .8) + ')';
            ctx.fillText(String(st.label).toUpperCase().slice(0, 28), q.x + 8, q.y + 3);
            ctx.font = "8px 'IBM Plex Mono',monospace"; ctx.fillStyle = 'rgba(107,116,136,' + m + ')';
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
      survey: function () { SEC.forEach(function (s) { s.frozen = null; }); focused = null; follow = false; selRec = null; goal.yaw = -.3; goal.pitch = .4; goal.dist = fitDist(); goal.tgt = [0, 20, 0]; setCrumb('colony survey'); emit('deselect'); },
      focus: function (id) { var s = bySec[id]; if (!s) return; if (s.frozen == null) s.frozen = live() ? performance.now() * s.rot : 0; focused = id; follow = false; goal.tgt = s.pos.slice(); goal.dist = s.R * 4.6; setCrumb('colony survey → ' + s.label.toLowerCase()); emit('sector', s); },
      followMission: function () { follow = true; focused = null; goal.dist = 460; setCrumb('following active mission'); },
      resetView: function () { api.survey(); },
      resetLayout: function () { SEC.forEach(function (s) { s.pos = s.defPos.slice(); s.label = s.serverLabel || s.defLabel; s.renamed = false; }); rebuildAll(); saveLayout(); if (!focused) api.survey(); },
      resetAll: function () { api.resetLayout(); api.survey(); },
      renameSector: function (id, name) { var s = bySec[id]; if (s && name && name.trim()) { s.label = name.trim().toUpperCase().slice(0, 28); s.renamed = s.label !== s.serverLabel; saveLayout(); } },
      setLayout: applyLayout,
      getLayout: layoutSnapshot,
      zoom: function (f) { goal.dist = Math.max(90, Math.min(1500, goal.dist / (f || 1))); },
      setOptions: function (o) { Object.assign(opts, o || {}); },
      stopMound: function (v) { bySec.mound.stopped = v !== false; },
      setTopology: function (scene) {
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
          if (s.id === 'mound') { s.present = !!(scene.mound && scene.mound.present); if (s.present) s.stopped = (scene.mound.mounds || []).some(function (m) { return m.stopped; }); }
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
      cv.style.cssText = 'position:absolute;top:0;left:0;display:block;cursor:grab';
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
      document.body.appendChild(tip);
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
      goal.pitch = Math.max(.05, Math.min(1.15, drag.pitch + dy2 * .003));
      follow = false;
    }
    function onUp() { if (sphDrag && moved) saveLayout(); if (drag || sphDrag) cv.style.cursor = 'grab'; drag = null; sphDrag = null; }
    function onWheel(e) { e.preventDefault(); goal.dist = Math.max(90, Math.min(1500, goal.dist * (e.deltaY > 0 ? 1.09 : .92))); }
    function onClick(e) {
      if (moved) return;
      var m = local(e);
      var pi = pickPoint(m.x, m.y);
      if (pi != null) { selRec = { sec: focused, idx: pi }; var s = bySec[focused]; var w = s.pts[pi]._w; if (w) { goal.tgt = w.slice(); goal.dist = Math.max(120, s.R * 2.2); } var rec = api.recordAt(focused, pi); if (!rec) return; if (rec.roleId) { setCrumb('colony survey → ' + s.label.toLowerCase() + ' → ' + rec.name); emit('resident', { sector: focused, index: pi, resident: rec }); return; } setCrumb('colony survey → ' + s.label.toLowerCase() + ' → ' + rec.title); emit('record', { sector: focused, index: pi, record: rec }); return; }
      var vis = shown(); for (var i = 0; i < vis.length; i++) { var s2 = vis[i], pr = proj(s2.pos); if (pr && Math.hypot(pr.x - m.x, pr.y - m.y) < Math.max(20, s2.R * pr.s)) { selRec = null; api.focus(s2.id); return; } }
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
