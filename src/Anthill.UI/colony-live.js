/* ─────────────────────────────────────────────────────────────────────────────
   COLONY LIVE — the underground neural formicarium renderer.
   Vanilla JS, canvas-2D with a real 3D projection (no framework, no CDN, no
   bundler; CSP-safe: served as its own asset like app.js).

   One-world/one-renderer: create ONE instance and re-parent its root element
   between Colony page, Dashboard widget, and Chat's colony layer — exactly the
   discipline the existing canvas has.

   Boundary rule: this file renders. It never decides. All state arrives through
   ColonyLive.setTopology(scene) — normally fed by colony-topology.js. Without a
   topology it renders a clearly-labelled DEMO colony so the view is testable.

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
    { id: 'mound', label: 'MICROMOUND', color: '#a55a7e', core: '#c9cfdc', pos: [-95, 265, 70], R: 34, n: 110, rot: .00006 }
  ];
  var ROOT_PAIRS = [['queen', 'intel', 3, 26], ['queen', 'forge', 3, 30], ['queen', 'valid', 2, 40], ['queen', 'memory', 2, 34], ['queen', 'output', 3, 26], ['intel', 'forge', 2, -20], ['forge', 'valid', 2, 22], ['valid', 'memory', 2, 26], ['memory', 'output', 2, 38]];
  var CLN = { queen: ['plans', 'decisions', 'directives', 'durable authority'], intel: ['conversations', 'context windows', 'web lookups', 'durable memories'], forge: ['patches', 'artifacts', 'build logs', 'durable memories'], valid: ['test runs', 'evidence', 'checks', 'durable memories'], memory: ['outcomes', 'patterns', 'pheromones', 'durable core'], output: ['results', 'reports', 'deliveries', 'durable memories'], mound: ['beats', 'syncs', 'telemetry', 'chain'] };
  var CLSLOT = [[-.62, -.38, .05], [.62, -.28, -.05], [.4, .55, .08], [0, .1, 0]];
  var LKEY = 'anthill.colonyLive.layout';

  function create() {
    var root = null, cv = null, ctx = null, tip = null, crumb = null;
    var W = 0, H = 0, scx = 0, scy = 0, raf = 0, ro = null, destroyed = false;
    var reduced = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    var opts = { motion: 'normal', labels: 'normal', trails: true };
    var live = function () { return !reduced && opts.motion !== 'off'; };

    var rnd = lcg(42);
    var SEC = SECTOR_DEFS.map(function (d) { return Object.assign({ morph: 0, frozen: null, defPos: d.pos.slice(), defLabel: d.label, demo: true }, d, { pos: d.pos.slice() }); });
    var bySec = {}; SEC.forEach(function (s) { bySec[s.id] = s; });

    // point clouds: shell = recent, mid = working set, core = durable (§5)
    SEC.forEach(function (s) {
      s.pts = []; s.links = [];
      for (var i = 0; i < s.n; i++) {
        var u = rnd() * 2 - 1, th = rnd() * TAU, sq = Math.sqrt(1 - u * u);
        var band = rnd(), r, layer;
        if (band < .55) { r = s.R * (.86 + rnd() * .14); layer = 0; }
        else if (band < .85) { r = s.R * (.45 + rnd() * .38); layer = 1; }
        else { r = s.R * rnd() * .32; layer = 2; }
        var ca = rnd() * TAU, crr = Math.pow(rnd(), .5) * .34, cu = (rnd() - .5) * .4;
        s.pts.push({ o: [sq * Math.cos(th) * r, u * r, sq * Math.sin(th) * r], layer: layer, cl: layer === 2 ? 3 : i % 3, clOff: [Math.cos(ca) * crr, cu, Math.sin(ca) * crr], sz: layer === 2 ? .9 + rnd() * .9 : .5 + rnd() * .9, a: layer === 2 ? .85 : layer === 1 ? .4 : .55, ph: rnd() * TAU, born: 0, rec: null });
      }
      for (var k = 0; k < s.n * .16; k++) s.links.push([Math.floor(rnd() * s.n), Math.floor(rnd() * s.n)]);
    });

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
      for (var s = 0; s < strands; s++) { r.mids.push([(rnd() - .5) * 46, sag + (rnd() - .5) * 30, (rnd() - .5) * 46]); r.strands.push([]); }
      rebuildRoot(r); return r;
    }
    var roots = ROOT_PAIRS.map(function (p) { return mkRoot(p[0], p[1], p[2], p[3]); });
    var authority = mkRoot('queen', 'mound', 1, 20);
    function rebuildAll() { roots.forEach(rebuildRoot); rebuildRoot(authority); }
    var fils = [];
    roots.forEach(function (r) { for (var k = 0; k < 4; k++) fils.push({ r: r, i: 3 + Math.floor(rnd() * 12), off: [(rnd() - .5) * 70, 30 + rnd() * 60, (rnd() - .5) * 70] }); });

    // active route + ants — REPLACED wholesale by setTopology; demo defaults below
    var rootIndex = {}; roots.forEach(function (r, i) { rootIndex[r.a + '>' + r.b] = i; rootIndex[r.b + '>' + r.a] = i; });
    var circuit = [], retSeg = null, ants = [], attention = [];
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
    function demoTopology() {
      routeFromSectorPath(['queen', 'intel', 'forge', 'valid'], true);
      retSeg = { pts: roots[rootIndex['valid>memory']].strands[0] };
      ants = [{ seg: 0, t: 0, sp: .00023, paused: false, label: 'researcher' }, { seg: 2, t: .86, sp: 0, paused: true, label: 'builder' }, { seg: -1, t: .3, sp: .00016, paused: false, gold: true, label: 'evidence' }];
      attention = [{ sector: 'valid', kind: 'approval', label: 'approval boundary' }];
    }

    // pheromone streams (the 3h connection language: particles, not lines)
    var rootStreams = [], circStreams = [], retStream = [], authStream = [];
    function mkStream(pts, n, s0, s1) { var out = []; for (var i = 0; i < n; i++) out.push({ pts: pts, t: rnd(), sp: s0 + rnd() * (s1 - s0), n: (rnd() - .5) * 10, ph: rnd() * TAU }); return out; }
    function buildStreams() {
      rootStreams = roots.map(function (r) { return mkStream(r.strands[0], 10, .00004, .00008); });
      circStreams = circuit.map(function (sg) { return { col: sg.col, ps: mkStream(sg.pts, 16, .0001, .00017) }; });
      retStream = retSeg ? mkStream(retSeg.pts, 8, .00008, .00013) : [];
      authStream = mkStream(authority.strands[0], 7, .00005, .00009);
    }

    // 3c galaxy environment: world-space stars + dust so everything parallaxes
    var DUST = [], STARS = [];
    for (var i = 0; i < 150; i++) DUST.push({ p: [(rnd() - .5) * 1080, (rnd() - .5) * 760, (rnd() - .5) * 560], sp: .008 + rnd() * .02, ph: rnd() * TAU });
    for (var j = 0; j < 110; j++) { var u2 = rnd() * 2 - 1, th2 = rnd() * TAU, sq2 = Math.sqrt(1 - u2 * u2), RR = 820 + rnd() * 380; STARS.push({ p: [sq2 * Math.cos(th2) * RR, u2 * RR * .7, sq2 * Math.sin(th2) * RR], sz: rnd() < .82 ? .7 : 1.5, ph: rnd() * TAU }); }

    // camera (§7): full 360° yaw, clamped tilt/dolly, eased goals
    var cam = { yaw: -.3, pitch: .4, dist: 900, tgt: [0, 20, 0] };
    var goal = { yaw: -.3, pitch: .4, dist: 900, tgt: [0, 20, 0] };
    var focused = null, follow = false, selRec = null, hovPt = null;
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
    function pathAt(pts, t, rev) { if (rev) t = 1 - t; var fi = Math.min(.999, Math.max(0, t)) * (pts.length - 1), jj = Math.floor(fi); return V(pts[jj], pts[Math.min(pts.length - 1, jj + 1)], fi - jj); }
    function setCrumb(t) { if (crumb) crumb.textContent = t; }

    // operator layout persistence
    try {
      var saved = JSON.parse(localStorage.getItem(LKEY) || '{}');
      SEC.forEach(function (s) {
        if (saved.positions && saved.positions[s.id]) s.pos = saved.positions[s.id].slice();
        if (saved.names && saved.names[s.id]) s.label = saved.names[s.id];
      });
      rebuildAll();
    } catch (e) { }
    function saveLayout() {
      var positions = {}, names = {};
      SEC.forEach(function (s) { positions[s.id] = s.pos; if (s.label !== s.defLabel) names[s.id] = s.label; });
      try { localStorage.setItem(LKEY, JSON.stringify({ positions: positions, names: names })); } catch (e) { }
    }

    demoTopology(); buildStreams();

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
    function frame(ts) {
      if (destroyed) return;
      raf = requestAnimationFrame(frame);
      var e = .06;
      cam.yaw += (goal.yaw - cam.yaw) * e; cam.pitch += (goal.pitch - cam.pitch) * e; cam.dist += (goal.dist - cam.dist) * e;
      for (var i = 0; i < 3; i++) cam.tgt[i] += (goal.tgt[i] - cam.tgt[i]) * e;
      if (live() && !dragging() && !focused && !follow) goal.yaw += Math.sin(ts * .00006) * .00012;
      if (follow && ants[0]) { var ap = antPos(ants[0]); if (ap) goal.tgt = ap; }
      // environment (3c galaxy + parallax dust)
      ctx.fillStyle = '#050607'; ctx.fillRect(0, 0, W, H);
      var nOx = cam.yaw * 46, nOy = cam.pitch * 60;
      [[.32, .34, '107,74,158', .09], [.7, .52, '47,127,138', .075], [.5, .74, '125,42,85', .06]].forEach(function (nb, ni) {
        var dx = (live() ? Math.sin(ts * .00005 + ni * 2) * 24 : 0) - nOx * (1 + ni * .3), dy = -nOy * (1 + ni * .25);
        var g = ctx.createRadialGradient(W * nb[0] + dx, H * nb[1] + dy, 0, W * nb[0] + dx, H * nb[1] + dy, 300);
        g.addColorStop(0, 'rgba(' + nb[2] + ',' + nb[3] + ')'); g.addColorStop(1, 'rgba(' + nb[2] + ',0)');
        ctx.fillStyle = g; ctx.fillRect(0, 0, W, H);
      });
      ctx.save(); ctx.translate(scx - nOx * 1.4, scy - nOy); ctx.rotate(-.48);
      var band = ctx.createLinearGradient(0, -70, 0, 70);
      band.addColorStop(0, 'rgba(200,210,235,0)'); band.addColorStop(.5, 'rgba(200,210,235,.035)'); band.addColorStop(1, 'rgba(200,210,235,0)');
      ctx.fillStyle = band; ctx.fillRect(-W * 1.2, -70, W * 2.4, 140); ctx.restore();
      STARS.forEach(function (st) { var q = proj(st.p); if (!q) return; var tw = .5 + Math.sin(ts * .0013 + st.ph) * .4; ctx.beginPath(); ctx.arc(q.x, q.y, st.sz, 0, TAU); ctx.fillStyle = 'rgba(220,228,245,' + (.42 * tw) + ')'; ctx.fill(); });
      DUST.forEach(function (d) { if (live()) { d.p[1] -= d.sp * 1.4; if (d.p[1] < -400) d.p[1] = 400; } var q = proj(d.p); if (!q) return; var tw = .55 + Math.sin(ts * .0009 + d.ph) * .35; ctx.beginPath(); ctx.arc(q.x, q.y, Math.max(.4, q.s), 0, TAU); ctx.fillStyle = 'rgba(172,182,208,' + (.10 * tw * fog(q.zc)) + ')'; ctx.fill(); });
      // filaments + whisper roots + streams
      fils.forEach(function (f) { var p = f.r.strands[0][f.i]; if (!p) return; var ew = [p[0] + f.off[0], p[1] + f.off[1], p[2] + f.off[2]], mw = V(p, ew, .55); var a = proj(p), m2 = proj(mw), b = proj(ew); if (a && m2 && b) { ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.quadraticCurveTo(m2.x, m2.y, b.x, b.y); ctx.strokeStyle = 'rgba(140,152,170,.05)'; ctx.lineWidth = .7; ctx.stroke(); } });
      roots.forEach(function (r) { drawStrand(r.strands[0], 'rgba(146,158,176,$A)', .045, 2.4, ts); });
      authority.strands.forEach(function (st) { drawStrand(st, 'rgba(226,31,123,$A)', .07, 2.6, ts); });
      rootStreams.forEach(function (ps) { drawStream(ps, '146,158,176', .3); });
      drawStream(authStream, '226,31,123', .4);
      var dens = opts.motion === 'low' ? .55 : 1;
      circStreams.forEach(function (cs, ci) { var c = h2(cs.col); drawStream(cs.ps.slice(0, Math.ceil(cs.ps.length * dens)), c[0] + ',' + c[1] + ',' + c[2], .8, 1.5, circuit[ci] && circuit[ci].rev); });
      if (opts.trails) drawStream(retStream, '217,176,84', .6, 1.3);
      drawSpheres(ts);
      drawAttention(ts);
      drawAnts(ts);
    }
    function drawSpheres(ts) {
      var order = SEC.map(function (s) { return { s: s, pr: proj(s.pos) }; }).filter(function (o) { return o.pr; }).sort(function (a, b) { return b.pr.zc - a.pr.zc; });
      order.forEach(function (o) {
        var s = o.s, pr = o.pr;
        var isFocused = focused === s.id;
        var rot = s.frozen != null ? s.frozen : (live() ? ts * s.rot : 0);
        var cr = Math.cos(rot), sr = Math.sin(rot);
        var c0 = h2(s.color), c1 = h2(s.core);
        var selHere = selRec && selRec.sec === s.id ? selRec.idx : null;
        var relSet = selHere != null && s.pts[selHere].rec ? s.pts[selHere].rec.rel : null;
        var wantMorph = isFocused && cam.dist < s.R * 5.5 ? 1 : 0;
        s.morph += (wantMorph - s.morph) * .05;
        var m = s.morph;
        var clC = CLSLOT.map(function (sl) { return [s.pos[0] + sl[0] * s.R * .8, s.pos[1] + sl[1] * s.R * .8, s.pos[2] + sl[2] * s.R * .8]; });
        var nr = s.R * .34 * pr.s;
        var nuc = s.id === 'queen' ? '232,178,90' : c1.join(',');
        var g = ctx.createRadialGradient(pr.x, pr.y, 0, pr.x, pr.y, Math.max(4, nr * 2.2));
        g.addColorStop(0, 'rgba(' + nuc + ',' + (.22 * fog(pr.zc)) + ')'); g.addColorStop(1, 'rgba(' + nuc + ',0)');
        ctx.beginPath(); ctx.arc(pr.x, pr.y, Math.max(4, nr * 2.2), 0, TAU); ctx.fillStyle = g; ctx.fill();
        ctx.strokeStyle = 'rgba(' + c0.join(',') + ',' + ((isFocused ? .1 : .06) * fog(pr.zc)) + ')'; ctx.lineWidth = .6;
        s.links.forEach(function (lk) {
          var pa = s.pts[lk[0]], pb = s.pts[lk[1]];
          var wa = ptWorld(s, pa, cr, sr, m, clC), wb = ptWorld(s, pb, cr, sr, m, clC);
          var a = proj(wa), b = proj(wb);
          if (a && b && Math.hypot(a.x - b.x, a.y - b.y) < 90 * pr.s) { ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke(); }
        });
        s.pts.forEach(function (p, pi) {
          if (p.settle) { p.settle.t = Math.min(1, p.settle.t + .02); var k = 1 - Math.pow(1 - p.settle.t, 3); for (var d = 0; d < 3; d++) p.o[d] = p.settle.from[d] + (p.settle.to[d] - p.settle.from[d]) * k; if (p.settle.t >= 1) delete p.settle; }
          var w = ptWorld(s, p, cr, sr, m, clC);
          var q = proj(w); if (!q) return;
          p._q = q; p._w = w;
          var a = p.a * fog(q.zc);
          if (isFocused && p.layer === 0 && selHere == null) a *= .28;
          if (selHere != null) a *= (pi === selHere || (relSet && relSet.indexOf(pi) >= 0)) ? 1 : .16;
          if (p.born && ts - p.born < 2000) a *= (ts - p.born) / 2000;
          var col = p.layer === 2 ? c1.join(',') : c0.join(',');
          var tw = live() && p.layer === 0 ? .85 + Math.sin(ts * .0012 + p.ph) * .15 : 1;
          var hp = isFocused && hovPt === pi;
          ctx.beginPath(); ctx.arc(q.x, q.y, Math.max(.4, p.sz * q.s) * (hp ? 1.6 : 1), 0, TAU);
          ctx.fillStyle = 'rgba(' + col + ',' + Math.min(1, a * tw * (hp ? 1.4 : 1)) + ')'; ctx.fill();
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
          ctx.font = '600 ' + Math.max(8, Math.min(11, 9 * pr.s * 8)) + "px 'IBM Plex Mono',monospace"; ctx.textAlign = 'center';
          ctx.fillStyle = 'rgba(' + c0.join(',') + ',' + ((isFocused ? .85 : .5) * fog(pr.zc)) + ')';
          ctx.fillText(s.label + (s.id === 'mound' && s.stopped ? ' · STOPPED' : ''), pr.x, pr.y + (s.R + 18) * pr.s);
        }
        if (isFocused && m > .25) {
          var counts = [0, 0, 0, 0]; s.pts.forEach(function (p) { counts[p.cl]++; });
          CLN[s.id].forEach(function (txt, ci) {
            var q = proj([clC[ci][0], clC[ci][1] - s.R * .52, clC[ci][2]]); if (!q) return;
            ctx.font = "600 9px 'IBM Plex Mono',monospace"; ctx.textAlign = 'center';
            ctx.fillStyle = ci === 3 ? 'rgba(' + c1.join(',') + ',' + (m * .9) + ')' : 'rgba(201,210,221,' + (m * .72) + ')';
            ctx.fillText(txt.toUpperCase(), q.x, q.y);
            ctx.font = "8px 'IBM Plex Mono',monospace"; ctx.fillStyle = 'rgba(107,116,136,' + m + ')';
            ctx.fillText(counts[ci] + ' records', q.x, q.y + 11);
          });
        }
      });
    }
    function ptWorld(s, p, cr, sr, m, clC) {
      var w = [s.pos[0] + p.o[0] * cr - p.o[2] * sr, s.pos[1] + p.o[1], s.pos[2] + p.o[0] * sr + p.o[2] * cr];
      if (m > .01) {
        var cc = clC[p.cl];
        var cw = [cc[0] + p.clOff[0] * s.R, cc[1] + p.clOff[1] * s.R, cc[2] + p.clOff[2] * s.R];
        w = [w[0] + (cw[0] - w[0]) * m, w[1] + (cw[1] - w[1]) * m, w[2] + (cw[2] - w[2]) * m];
      }
      return w;
    }
    function antPos(an) {
      var sg = an.seg === -1 ? retSeg : circuit[an.seg];
      if (!sg) return null;
      return pathAt(sg.pts, an.t, sg.rev);
    }
    function drawAnts(ts) {
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
      s.pts.forEach(function (p, i) { if (p._q) { var d = Math.hypot(p._q.x - mx, p._q.y - my); if (d < bd) { bd = d; best = i; } } });
      return best;
    }
    var api = {
      survey: function () { SEC.forEach(function (s) { s.frozen = null; }); focused = null; follow = false; selRec = null; goal.yaw = -.3; goal.pitch = .4; goal.dist = 900; goal.tgt = [0, 20, 0]; setCrumb('colony survey'); emit('deselect'); },
      focus: function (id) { var s = bySec[id]; if (!s) return; if (s.frozen == null) s.frozen = live() ? performance.now() * s.rot : 0; focused = id; follow = false; goal.tgt = s.pos.slice(); goal.dist = s.R * 4.6; setCrumb('colony survey → ' + s.label.toLowerCase()); emit('sector', s); },
      followMission: function () { follow = true; focused = null; goal.dist = 460; setCrumb('following active mission'); },
      resetView: function () { api.survey(); },
      resetLayout: function () { SEC.forEach(function (s) { s.pos = s.defPos.slice(); s.label = s.defLabel; }); rebuildAll(); try { localStorage.removeItem(LKEY); } catch (e) { } },
      renameSector: function (id, name) { var s = bySec[id]; if (s && name && name.trim()) { s.label = name.trim().toUpperCase(); saveLayout(); } },
      setOptions: function (o) { Object.assign(opts, o || {}); },
      stopMound: function (v) { bySec.mound.stopped = v !== false; },
      setTopology: function (scene) {
        // scene: { route: [sectorIds], pausedForApproval, evidenceReturn, ants:[{seg,t,gold}],
        //          counts: {sectorId: {shell, mid, core}}, records: {sectorId: [recordFacts]},
        //          mound: {online, stopped, ...} } — see colony-topology.js
        if (!scene) return;
        if (scene.route) routeFromSectorPath(scene.route, !!scene.pausedForApproval);
        if (scene.evidenceReturn) { var ri = rootIndex[scene.evidenceReturn.join('>')]; if (ri != null) { retSeg = { pts: roots[ri].strands[0] }; retStream = mkStream(retSeg.pts, 8, .00008, .00013); } }
        if (scene.ants) ants = scene.ants;
        if (scene.records) SEC.forEach(function (s) { s.records = scene.records[s.id] || null; s.demo = !s.records; });
        if (scene.mound) bySec.mound.stopped = !!scene.mound.stopped;
        attention = scene.attention || attention;
      },
      recordAt: function (secId, idx) {
        // Truthful record surface: real records from topology when present; demo facts otherwise.
        var s = bySec[secId], p = s.pts[idx];
        if (s.records && s.records[idx % s.records.length]) return s.records[idx % s.records.length];
        if (!p.rec) {
          var h = (idx * 2654435761) >>> 0;
          p.rec = { title: s.label.toLowerCase() + ' record ' + idx + ' (demo)', type: ['conversation', 'task', 'artifact', 'evidence', 'memory'][h % 5], ant: '—', mission: 'demo', time: '—', verif: p.layer === 2 ? 'verified' : 'unverified', phero: 20 + (h % 60), rel: [(h + 13) % s.n, (h + 97) % s.n, (h + 211) % s.n] };
        }
        return p.rec;
      },
      verifyRecord: function (secId, idx) { // visual settle — call ONLY after the backend confirms
        var s = bySec[secId], p = s.pts[idx];
        if (p.layer === 2) return;
        var mlen = Math.hypot(p.o[0], p.o[1], p.o[2]) || 1, target = s.R * .26;
        p.settle = { from: p.o.slice(), to: [p.o[0] / mlen * target, p.o[1] / mlen * target, p.o[2] / mlen * target], t: 0 };
        p.layer = 2; p.a = .85;
      },
      addRecordPoint: function (secId) { // call when the backend writes a record (SSE event)
        var s = bySec[secId]; if (!s || s.pts.length > s.n + 40) return;
        var u = Math.random() * 2 - 1, th = Math.random() * TAU, sq = Math.sqrt(1 - u * u);
        s.pts.push({ o: [sq * Math.cos(th) * s.R * .95, u * s.R * .95, sq * Math.sin(th) * s.R * .95], layer: 0, cl: s.pts.length % 3, clOff: [0, 0, 0], sz: 1.1, a: .8, ph: Math.random() * TAU, born: performance.now(), rec: null });
      },
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
      function fit() { var rc = el.getBoundingClientRect(); W = cv.width = Math.max(50, rc.width); H = cv.height = Math.max(50, rc.height); scx = W / 2; scy = H / 2; }
      fit(); ctx = cv.getContext('2d');
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
        for (var i = 0; i < SEC.length; i++) {
          var s = SEC[i], pr = proj(s.pos);
          if (pr && Math.hypot(pr.x - m.x, pr.y - m.y) < Math.max(20, s.R * pr.s)) { sphDrag = { s: s, x: e.clientX, y: e.clientY, orig: s.pos.slice(), zc: pr.zc }; cv.style.cursor = 'grabbing'; return; }
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
      if (pi != null) { selRec = { sec: focused, idx: pi }; var s = bySec[focused]; var w = s.pts[pi]._w; if (w) { goal.tgt = w.slice(); goal.dist = Math.max(120, s.R * 2.2); } var rec = api.recordAt(focused, pi); setCrumb('colony survey → ' + s.label.toLowerCase() + ' → ' + rec.title); emit('record', { sector: focused, index: pi, record: rec }); return; }
      for (var i = 0; i < SEC.length; i++) { var s2 = SEC[i], pr = proj(s2.pos); if (pr && Math.hypot(pr.x - m.x, pr.y - m.y) < Math.max(20, s2.R * pr.s)) { selRec = null; api.focus(s2.id); return; } }
      if (selRec) { selRec = null; var sf = bySec[focused]; if (sf) { goal.tgt = sf.pos.slice(); goal.dist = sf.R * 4.6; setCrumb('colony survey → ' + sf.label.toLowerCase()); } emit('deselect'); return; }
      api.survey();
    }
    function onKey(e) { if (e.key === 'Escape') { if (selRec) onClick({ clientX: -9999, clientY: -9999 }); else api.survey(); } }
    function onHover(e) {
      var m = local(e);
      var pi = dragging() ? null : pickPoint(m.x, m.y);
      hovPt = pi;
      if (pi != null) {
        var s = bySec[focused], r = api.recordAt(focused, pi);
        cv.style.cursor = 'pointer';
        tip.innerHTML = '<div style="font-size:10.5px;font-weight:600;color:' + s.color + ';margin-bottom:2px"></div>';
        tip.firstChild.textContent = r.title;
        var sub = document.createElement('div'); sub.textContent = r.type + ' · ' + r.ant + ' · ' + r.time; tip.appendChild(sub);
        tip.style.display = 'block'; tip.style.left = (e.clientX + 14) + 'px'; tip.style.top = (e.clientY - 10) + 'px';
        return;
      }
      var hit = null;
      for (var i = 0; i < SEC.length; i++) { var s2 = SEC[i], pr = proj(s2.pos); if (pr && Math.hypot(pr.x - m.x, pr.y - m.y) < Math.max(18, s2.R * pr.s)) { hit = s2; break; } }
      if (hit && !dragging()) {
        cv.style.cursor = 'pointer';
        tip.textContent = hit.label + (hit.demo ? ' · demo data' : '');
        tip.style.display = 'block'; tip.style.left = (e.clientX + 14) + 'px'; tip.style.top = (e.clientY - 10) + 'px';
      } else { if (!dragging()) cv.style.cursor = 'grab'; tip.style.display = 'none'; }
    }
    return api;
  }
  window.ColonyLive = { create: create };
})();
