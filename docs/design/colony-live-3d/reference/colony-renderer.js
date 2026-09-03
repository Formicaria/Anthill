/* Colony Live 3D — renderer. Owns three.js only; it never invents activity.
   Everything it draws comes from colony-topology.js plus a mission state object handed in
   by the host. One world, one renderer: the stage can be re-parented without rebuilding. */

import { SECTORS, SECTOR_BY_ID, ROOTS, CONTEXT, TOKENS } from './colony-topology.js';

const T = () => window.THREE;

/* ---- textures (generated once, no external assets) ---- */
function radialTex(stops, size = 128) {
  const c = document.createElement('canvas'); c.width = c.height = size;
  const g = c.getContext('2d');
  const grd = g.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2);
  stops.forEach(s => grd.addColorStop(s[0], s[1]));
  g.fillStyle = grd; g.fillRect(0, 0, size, size);
  return new (T().CanvasTexture)(c);
}
function antTex() {
  const c = document.createElement('canvas'); c.width = c.height = 64;
  const g = c.getContext('2d');
  g.fillStyle = 'rgba(245,238,226,.95)';
  g.beginPath(); g.ellipse(32, 26, 6, 9, 0, 0, 7); g.fill();
  g.beginPath(); g.ellipse(32, 40, 8, 11, 0, 0, 7); g.fill();
  g.beginPath(); g.arc(32, 15, 5, 0, 7); g.fill();
  g.strokeStyle = 'rgba(245,238,226,.75)'; g.lineWidth = 2.4; g.lineCap = 'round';
  [[-1, -6], [-1, 4], [1, -6], [1, 4]].forEach(([sx, dy]) => {
    g.beginPath(); g.moveTo(32 + sx * 5, 30 + dy); g.lineTo(32 + sx * 17, 30 + dy - 4); g.stroke();
  });
  return new (T().CanvasTexture)(c);
}
function lockTex() {
  const c = document.createElement('canvas'); c.width = c.height = 96;
  const g = c.getContext('2d');
  g.strokeStyle = TOKENS.queen; g.lineWidth = 5;
  g.beginPath(); g.arc(48, 48, 30, 0, 7); g.stroke();
  g.fillStyle = TOKENS.queen;
  g.fillRect(36, 46, 24, 20);
  g.lineWidth = 5; g.beginPath(); g.arc(48, 46, 10, Math.PI, 0); g.stroke();
  return new (T().CanvasTexture)(c);
}
function rngFrom(seed) {
  let s = (seed | 0) || 1;
  return () => { s = (s * 1664525 + 1013904223) & 0x7fffffff; return s / 0x7fffffff; };
}
function seedOf(str) { let h = 2166136261; for (let i = 0; i < str.length; i++) h = (h ^ str.charCodeAt(i)) * 16777619 & 0x7fffffff; return h; }

/* Excavated path: meanders in all three axes, sags under its own span, never a clean arc. */
function fin(v) { return Number.isFinite(v); }
function finV(v) { return v && fin(v.x) && fin(v.y) && fin(v.z); }

/* A near-direct run: two control points, one gentle consistent bow. Degenerate or
   non-finite geometry falls back to a straight two-point curve rather than feeding
   three.js a curve whose arc-length search cannot converge. */
function curveFor(root) {
  const th = T();
  const a = SECTOR_BY_ID[root.from], b = SECTOR_BY_ID[root.to];
  const A = new th.Vector3(...a.pos), B = new th.Vector3(...b.pos);
  const straight = () => {
    const c0 = new th.CatmullRomCurve3([A.clone(), A.clone().lerp(B, 0.5), B.clone()]);
    c0.curveType = 'catmullrom'; c0.tension = 0.5; return c0;
  };
  if (!finV(A) || !finV(B)) return straight();
  const dir = B.clone().sub(A);
  const len = dir.length();
  if (!fin(len) || len < 1e-3) return straight();
  const p0 = A.clone().add(dir.clone().multiplyScalar(a.r / len * 0.35));
  const p3 = B.clone().sub(dir.clone().multiplyScalar(b.r / len * 0.35));
  const axis = p3.clone().sub(p0);
  const span = axis.length();
  if (!finV(p0) || !finV(p3) || !fin(span) || span < 0.25) return straight();
  const u = axis.clone().normalize();
  let n1 = new th.Vector3(0, 1, 0);
  if (Math.abs(u.dot(n1)) > 0.9) n1.set(1, 0, 0);
  n1.crossVectors(u, n1).normalize();
  const n2 = new th.Vector3().crossVectors(u, n1).normalize();
  const rnd = rngFrom(seedOf(root.id) + 7);
  const lean = (rnd() * 2 - 1) * span * 0.035;
  const lift = (rnd() * 2 - 1) * span * 0.025;
  const raw = root.bow || [0, 0, 0];
  const bow = new th.Vector3(fin(raw[0]) ? raw[0] : 0, fin(raw[1]) ? raw[1] : 0, fin(raw[2]) ? raw[2] : 0).multiplyScalar(0.18);
  const pts = [p0.clone()];
  for (let i = 1; i <= 2; i++) {
    const t = i / 3, env = Math.sin(Math.PI * t);
    const p = p0.clone().lerp(p3, t)
      .addScaledVector(n1, env * lean)
      .addScaledVector(n2, env * lift)
      .addScaledVector(bow, env);
    p.y -= env * span * 0.012;
    if (!finV(p)) return straight();
    pts.push(p);
  }
  pts.push(p3.clone());
  const c = new th.CatmullRomCurve3(pts);
  c.curveType = 'catmullrom';
  c.tension = 0.5;
  /* one sanity check: a curve that cannot report a finite length or midpoint is
     replaced outright — never handed to the sampler */
  const mid = c.getPoint(0.5);
  if (!finV(mid)) return straight();
  return c;
}


/* Record particles: one point per persisted record. uRec lifts them when the camera
   is inside the sphere; there is no filler channel any more. */
const POINT_VS = `
attribute vec3 acolor; attribute float size; attribute float alpha; attribute vec3 aOrg;
uniform float uScale; uniform float uAlpha; uniform float uRec; uniform float uOrg;
varying vec3 vC; varying float vA;
void main(){
  vC = acolor; vA = alpha * uAlpha;
  vec4 mv = modelViewMatrix * vec4(mix(position, aOrg, uOrg), 1.0);
  gl_PointSize = clamp(size * uRec * uScale * (300.0 / max(1.0, -mv.z)), 2.0, 12.0);
  gl_Position = projectionMatrix * mv;
}`;
const POINT_FS = `
uniform sampler2D uMap; varying vec3 vC; varying float vA;
void main(){ if(vA < 0.02) discard; if(texture2D(uMap, gl_PointCoord).a < 0.5) discard;
  gl_FragColor = vec4(vC, clamp(vA, 0.0, 1.0)); }`;

const CONDUIT_VS = [
'attribute float aT; attribute float aS; attribute float aB;',
'uniform float uHead; uniform float uActive; uniform float uRest; uniform float uScale; uniform float uSharp;',
'uniform vec3 uFrom; uniform vec3 uTo; uniform vec3 uMag; uniform vec3 uDest;',
'varying float vA; varying vec3 vC;',
'void main(){',
'  float d = aT - uHead;',
'  float wave = uActive * exp(-d * d * uSharp);',
'  vec3 rest = mix(uFrom, uTo, smoothstep(0.05, 0.95, aT));',
'  vC = mix(rest, min(rest * 1.9, vec3(1.0)), clamp(wave * 1.3, 0.0, 1.0));',
'  vA = aB * (uRest + 2.1 * wave);',
'  vec4 mv = modelViewMatrix * vec4(position, 1.0);',
'  gl_PointSize = clamp(aS * (1.0 + 1.5 * wave) * uScale * (300.0 / max(1.0, -mv.z)), 2.4, 8.5);',
'  gl_Position = projectionMatrix * mv;',
'}'
].join('\n');
const CONDUIT_FS = [
'uniform sampler2D uMap; varying float vA; varying vec3 vC;',
'void main(){ if(vA < 0.02) discard; if(texture2D(uMap, gl_PointCoord).a < 0.5) discard;',
'  gl_FragColor = vec4(vC, clamp(vA, 0.0, 1.0)); }'
].join('\n');


const mark = (label, t0) => {
  try { console.log('[colony-live] ' + label + ' ' + Math.round(performance.now() - t0) + 'ms'); } catch (e) { /* no console */ }
};

export function createStage(mount, hooks = {}) {
  const bootT0 = performance.now();
  mark('createStage:enter', bootT0);
  const th = T();
  if (!th) return null;
  let renderer;
  try {
    renderer = new th.WebGLRenderer({ antialias: true, alpha: false, powerPreference: 'high-performance' });
  } catch (e) { return null; }
  const scene = new th.Scene();
  const camera = new th.PerspectiveCamera(42, 1, 0.5, 400);
  renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
  renderer.setClearColor(0x04060b, 1);
  const canvas = renderer.domElement;
  canvas.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;display:block;';
  mount.appendChild(canvas);

  const overlay = document.createElement('div');
  overlay.style.cssText = 'position:absolute;inset:0;pointer-events:none;overflow:hidden;';
  mount.appendChild(overlay);

  const dotTex = radialTex([[0, 'rgba(255,255,255,1)'], [0.9, 'rgba(255,255,255,1)'], [0.98, 'rgba(255,255,255,1)'], [1, 'rgba(255,255,255,0)']], 128);
  const glowTex = radialTex([[0, 'rgba(255,255,255,1)'], [0.3, 'rgba(255,255,255,.92)'], [0.46, 'rgba(255,255,255,.34)'], [0.72, 'rgba(255,255,255,.06)'], [1, 'rgba(255,255,255,0)']], 256);
  /* conduit grain: essentially a solid disc with a hairline soft edge, so the
     streams read as distinct dots rather than overlapping bloom */
  const grainTex = radialTex([[0, 'rgba(255,255,255,1)'], [0.92, 'rgba(255,255,255,1)'], [0.99, 'rgba(255,255,255,1)'], [1, 'rgba(255,255,255,0)']], 128);
  const ant = antTex(), lock = lockTex();
  /* chamber halo: smooth power falloff with no defined rim — bright at the core,
     dissolving into the volume rather than ending on a circle */
  const haloTex = (() => {
    const S = 256, c = document.createElement('canvas'); c.width = c.height = S;
    const x = c.getContext('2d'), g = x.createRadialGradient(S / 2, S / 2, 0, S / 2, S / 2, S / 2);
    for (let i = 0; i <= 24; i++) {
      const t = i / 24;
      g.addColorStop(t, 'rgba(255,255,255,' + (Math.pow(1 - t, 2.2) * 0.5).toFixed(4) + ')');
    }
    x.fillStyle = g; x.fillRect(0, 0, S, S);
    const tex = new th.CanvasTexture(c); tex.needsUpdate = true; return tex;
  })();

  /* no decorative backdrop: the volume is empty black so nothing competes with
     the chambers and their conduits. */

  /* ---- sectors ---- */
  const sectorObjs = {};
  SECTORS.forEach(s => {
    const grp = new th.Group(); grp.position.set(...s.pos); scene.add(grp);
    const ctx = CONTEXT[s.id];
    const recs = [];
    ctx.clusters.forEach(cl => cl.records.forEach(r => recs.push(r)));
    const total = recs.length;   /* one particle per persisted record, nothing else */
    const pos = new Float32Array(total * 3), col = new Float32Array(total * 3);
    const siz = new Float32Array(total), alp = new Float32Array(total);
    const shell = new th.Color(s.shell), core = new th.Color(s.core);
    let seed = 1337 + s.id.length * 91;
    const rnd = () => { seed = (seed * 1664525 + 1013904223) & 0x7fffffff; return seed / 0x7fffffff; };
    const c = new th.Color();
    for (let i = 0; i < total; i++) {
      let x, y, z, depth, tint = 0.6, a = 0.5, size = 1.5;
      if (i < recs.length) {
        const r = recs[i];
        x = r.pos[0]; y = r.pos[1]; z = r.pos[2];
        const rad = Math.min(1, Math.hypot(x, y, z) / s.r);
        depth = 1 - rad;
        /* soft edge: the outermost records thin out rather than stacking on a rim */
        const edge = 1 - 0.72 * Math.pow(rad, 2.6);
        tint = r.tint; a = Math.min(1, 0.82 + r.pheromone * 0.2) * (0.86 + 0.14 * edge); size = (1.15 + r.pheromone * 1.7) * (0.72 + 0.28 * edge);
        r._i = i;
      }
      pos[i * 3] = x; pos[i * 3 + 1] = y; pos[i * 3 + 2] = z;
      c.copy(shell).lerp(core, Math.min(1, Math.pow(depth, 1.5) * 1.15)).multiplyScalar(0.5 + tint * 0.45);
      col[i * 3] = c.r; col[i * 3 + 1] = c.g; col[i * 3 + 2] = c.b;
      siz[i] = size; alp[i] = a;
    }
    /* ordered formation: one level stratum per cluster, records laid on an even
       spiral within it. Focusing a chamber cross-fades the cloud into this. */
    const org = new Float32Array(total * 3);
    {
      const C = Math.max(1, ctx.clusters.length);
      let w = 0;
      ctx.clusters.forEach((cl, ci) => {
        const m = Math.max(1, cl.records.length);
        const y = ((ci + 0.5) / C - 0.5) * s.r * 1.55;
        const band = Math.sqrt(Math.max(0.12, 1 - Math.pow(y / (s.r * 1.05), 2)));
        cl.org = [0, y, 0];
        cl.records.forEach((rec, k) => {
          const ang = k * 2.399963;
          const rad = s.r * 0.86 * band * Math.sqrt((k + 0.55) / m);
          const ox = Math.cos(ang) * rad, oz = Math.sin(ang) * rad;
          rec.org = [ox, y, oz];
          org[w * 3] = ox; org[w * 3 + 1] = y; org[w * 3 + 2] = oz; w++;
        });
      });
    }
    const geo = new th.BufferGeometry();
    geo.setAttribute('position', new th.BufferAttribute(pos, 3));
    geo.setAttribute('aOrg', new th.BufferAttribute(org, 3));
    geo.setAttribute('acolor', new th.BufferAttribute(col, 3));
    geo.setAttribute('size', new th.BufferAttribute(siz, 1));
    geo.setAttribute('alpha', new th.BufferAttribute(alp, 1));
    const mat = new th.ShaderMaterial({
      uniforms: { uMap: { value: dotTex }, uScale: { value: 1 }, uAlpha: { value: 1 }, uRec: { value: 1 }, uOrg: { value: 0 } },
      vertexShader: POINT_VS, fragmentShader: POINT_FS,
      transparent: true, depthWrite: false, blending: th.AdditiveBlending
    });
    const points = new th.Points(geo, mat); grp.add(points);

    /* The chamber's shape is carried by its own particles — the structural filler
       points above — not by decorative links. Only data-bearing links remain:
       cluster→record spokes (innerFor) and the conduit nets. */
    /* no membrane mesh: a sphere shell blooms at grazing angles and reads as a
       hard outline. The chamber boundary is implied by particle density alone. */
    const glow = new th.Sprite(new th.SpriteMaterial({ map: haloTex, color: new th.Color(s.shell), transparent: true, opacity: 0.17, depthWrite: false, blending: th.AdditiveBlending, fog: false }));
    glow.scale.setScalar(s.r * 2.8);
    const nucleus = new th.Sprite(new th.SpriteMaterial({ map: haloTex, color: new th.Color(s.nucleus), transparent: true, opacity: 0.8, depthWrite: false, blending: th.AdditiveBlending, fog: false }));
    /* core light: a soft gradient from the chamber's centre outward. The wide
       outer halo stays unmounted — this is the only diffuse element left. */
    /* sprite scale is a diameter, so 2r puts the gradient's zero exactly on the
       outermost record/link points of the chamber */
    nucleus.scale.setScalar(s.r * 4.2); grp.add(nucleus);

    /* resident crew: the actual ants that authored this sector's records */
    const crnd = rngFrom(seedOf(s.id) + 4441);
    const crew = [];
    const byAnt = {};
    recs.forEach(r => { (byAnt[r.ant] = byAnt[r.ant] || []).push(r); });
    /* the chamber's own roster is the truth; records attach where the author matches */
    const leadNames = s.leads || s.roles || ['Worker'];
    const workerNames = s.workers || [];
    const roster = leadNames.map(name => ({ name, lead: true, rs: byAnt[name] || [] }))
      .concat(workerNames.map(name => ({ name, lead: false, rs: byAnt[name] || [] })));
    if (roster[0] && roster[0].name === 'Queen') roster[0].authority = true;
    const nLead = leadNames.length, nWork = workerNames.length;
    const nAnt = roster.length;
    for (let i = 0; i < nAnt; i++) {
      const entry = roster[i];
      const role = entry.name;
      const isQueen = !!entry.authority;
      const isLead = !!entry.lead;
      /* home cluster = wherever this ant filed the most records */
      const tally = {};
      entry.rs.forEach(r => { tally[r.cluster] = (tally[r.cluster] || 0) + 1; });
      const homeLabel = Object.keys(tally).sort((a, b) => tally[b] - tally[a])[0] || ctx.clusters[0].label;
      const home = Math.max(0, ctx.clusters.findIndex(cl => cl.label === homeLabel));
      const c0 = ctx.clusters[home].center;
      const verified = entry.rs.filter(r => r.verification === 'verified').length;
      const refused = entry.rs.filter(r => r.verification === 'refused').length;
      const pherAvg = entry.rs.length ? entry.rs.reduce((n, r) => n + r.pheromone, 0) / entry.rs.length : 0;
      const latest = entry.rs.slice().sort((a, b) => (a.ts < b.ts ? 1 : -1))[0];
      const clustersServed = Object.keys(tally).length;
      /* two concentric rings: registry roles inside, their workers outside. The
         Queen holds the centre of her own chamber. */
      const ringN = Math.max(1, isLead ? (isQueen ? nLead - 1 : nLead) : nWork);
      const ringI = isLead ? (roster[0].authority ? i - 1 : i) : i - nLead;
      const ang = (ringI / ringN) * Math.PI * 2 + (isLead ? 0 : Math.PI / ringN);
      const rr = s.r * (isLead ? (s.child ? 0.4 : 0.44) : (s.child ? 0.82 : 0.86));
      const slot = isQueen
        ? new th.Vector3(0, 0, 0)
        : new th.Vector3(Math.cos(ang) * rr, (ringI % 2 ? 1 : -1) * s.r * (isLead ? 0.08 : 0.14), Math.sin(ang) * rr);
      const orbColor = new th.Color(isQueen ? TOKENS.gold : isLead ? s.core : s.shell);
      const sp = new th.Sprite(new th.SpriteMaterial({ map: glowTex, color: orbColor, transparent: true, opacity: 0, depthWrite: false, blending: th.AdditiveBlending, fog: false }));
      sp.position.set(slot.x, slot.y, slot.z); grp.add(sp);
      /* bright core reads as a body inside the halo */
      const core = new th.Sprite(new th.SpriteMaterial({ map: dotTex, color: new th.Color(orbColor).lerp(new th.Color(0xffffff), 0.55), transparent: true, opacity: 0, depthWrite: false, blending: th.AdditiveBlending, fog: false }));
      sp.add(core); core.position.set(0, 0, 0.001); core.scale.setScalar(0.34);
      let ring = null;
      if (isQueen) {
        ring = new th.Sprite(new th.SpriteMaterial({ map: glowTex, color: new th.Color(TOKENS.queen), transparent: true, opacity: 0, depthWrite: false, blending: th.AdditiveBlending, fog: false }));
        sp.add(ring); ring.position.set(0, 0, -0.001); ring.scale.setScalar(2.1);
      }
      crew.push({
        sp, core, ring, role, isQueen, isLead, home, rnd: crnd, color: orbColor, slot,
        recs: entry.rs,
        target: ctx.clusters[home].label, targetC: new th.Vector3(c0[0], c0[1], c0[2]),
        bob: crnd() * 9, beam: 0, beamDur: 1, work: 1 + crnd() * 6, lastDrop: 0,
        tasks: entry.rs.length, drops: 0,
        info: {
          ant_id: isQueen ? 'queen_01' : 'ant_' + s.id.slice(0, 3) + '_' + role.toLowerCase().replace(/[^a-z]/g, '').slice(0, 6),
          role, rank: isQueen ? 'colony authority' : isLead ? 'registry role' : 'worker', sector: s.id,
          sectorLabel: (s.label || '').replace('\n', ' '),
          home_cluster: ctx.clusters[home].label,
          records: entry.rs.length,
          clusters_served: clustersServed,
          verified, refused,
          pheromone_avg: pherAvg.toFixed(2),
          last_record: latest ? latest.title : (isQueen ? 'Mission intake admitted' : '—'),
          last_ts: latest ? latest.ts : '—',
          protocol: s.child ? 'edge_queen' : 'colony_native',
          color: '#' + orbColor.getHexString()
        }
      });
    }

    sectorObjs[s.id] = { s, grp, points, glow, nucleus, mat, records: recs, ctx, crew };
  });

  /* ---- volumetric neural particle conduits ---- */
  const rootObjs = {};

  function conduitSpec(r) {
    const lateral = r.kind === 'lateral', auth = r.kind === 'authority';
    return {
      n: auth ? 40 : lateral ? 24 : 60,
      streams: auth ? 2 : lateral ? 1 : 2,
      rad: auth ? 0.44 : lateral ? 0.5 : 0.8,
      rest: auth ? 0.3 : lateral ? 0.14 : 0.32,
      sharp: auth ? 150 : 120
    };
  }

  /* particle placement in PATH SPACE: each particle keeps its own t, radius, angle
     and drift speed, so the stream can travel chamber→chamber without re-solving
     the curve every frame. */
  function fillConduit(ro) {
    const curve = ro.curve, spec = ro.spec, N = 64;
    /* uniform-parameter sampling with a rotation-minimising frame: no arc-length
       cache, no binary search, nothing that can fail to converge */
    const samp = new Float32Array((N + 1) * 3), nrm = new Float32Array((N + 1) * 3), bnm = new Float32Array((N + 1) * 3);
    const P = new th.Vector3(), Tn = new th.Vector3(), Nv = new th.Vector3(), Bv = new th.Vector3(), tmp = new th.Vector3();
    Nv.set(0, 1, 0);
    for (let i = 0; i <= N; i++) {
      const t = i / N;
      curve.getPoint(t, P);
      curve.getPoint(Math.min(1, t + 1 / N), tmp);
      Tn.copy(tmp).sub(P);
      if (Tn.lengthSq() < 1e-9) {
        curve.getPoint(Math.max(0, t - 1 / N), tmp);
        Tn.copy(P).sub(tmp);
      }
      if (!finV(Tn) || Tn.lengthSq() < 1e-9) Tn.set(0, 0, 1);
      Tn.normalize();
      if (!finV(Nv) || Math.abs(Tn.dot(Nv)) > 0.98) Nv.set(Math.abs(Tn.x) > 0.9 ? 0 : 1, Math.abs(Tn.x) > 0.9 ? 1 : 0, 0);
      Bv.crossVectors(Tn, Nv);
      if (Bv.lengthSq() < 1e-9) Bv.set(0, 0, 1);
      Bv.normalize();
      Nv.crossVectors(Bv, Tn);
      if (Nv.lengthSq() < 1e-9) Nv.set(0, 1, 0);
      Nv.normalize();
      if (!finV(P) || !finV(Nv) || !finV(Bv)) { P.set(0, 0, 0); Nv.set(0, 1, 0); Bv.set(0, 0, 1); }
      samp[i * 3] = P.x; samp[i * 3 + 1] = P.y; samp[i * 3 + 2] = P.z;
      nrm[i * 3] = Nv.x; nrm[i * 3 + 1] = Nv.y; nrm[i * 3 + 2] = Nv.z;
      bnm[i * 3] = Bv.x; bnm[i * 3 + 1] = Bv.y; bnm[i * 3 + 2] = Bv.z;
    }
    ro.path = { N, samp, nrm, bnm };
    /* a fixed, generous bounding sphere: the stream never leaves the corridor, so
       the per-frame drift does not need to recompute bounds */
    const cA = new th.Vector3(samp[0], samp[1], samp[2]), cB = new th.Vector3(samp[N * 3], samp[N * 3 + 1], samp[N * 3 + 2]);
    ro.geo.boundingSphere = new th.Sphere(cA.clone().lerp(cB, 0.5), cA.distanceTo(cB) * 0.6 + spec.rad * 6 + 12);
    ro.points.frustumCulled = true;
    if (!ro.p) {
      const rnd = rngFrom(ro.seed + 77);
      const per = Math.ceil(spec.n / spec.streams);
      const p = {
        t: new Float32Array(spec.n), base: new Float32Array(spec.n), jit: new Float32Array(spec.n),
        a0: new Float32Array(spec.n), tw: new Float32Array(spec.n), sp: new Float32Array(spec.n),
        b0: new Float32Array(spec.n)
      };
      const aS = ro.geo.attributes.aS.array;
      for (let i = 0; i < spec.n; i++) {
        const stream = i % spec.streams, step = Math.floor(i / spec.streams);
        const primary = stream === 0, syn = primary && step % 9 === 4;
        p.t[i] = Math.min(0.998, Math.max(0.002, (step + 0.15 + rnd() * 0.7) / per));
        p.base[i] = primary ? spec.rad * (0.04 + rnd() * 0.26) : spec.rad * (0.5 + stream * 0.26 + rnd() * 0.26);
        p.jit[i] = 0.35 + rnd() * 0.9;
        p.a0[i] = stream * 2.2 + rnd() * 0.6;
        p.tw[i] = primary ? 1.1 : 3.2;
        /* brisk, varied: a particle crosses a conduit in roughly 9–14 s */
        p.sp[i] = (0.072 + rnd() * 0.038) * (primary ? 1 : 0.9);
        p.b0[i] = (primary ? 0.75 + rnd() * 0.5 : 0.4 + rnd() * 0.3) * (syn ? 1.9 : 1);
        aS[i] = (primary ? 1.1 + rnd() * 0.8 : 0.65 + rnd() * 0.6) * (syn ? 1.7 : 1);
      }
      ro.geo.attributes.aS.needsUpdate = true;
      ro.p = p;
    }
    driftConduit(ro, 0);
  }

  /* advance the stream along the path; dt = 0 just re-places it */
  function driftConduit(ro, dt) {
    const spec = ro.spec, p = ro.p, path = ro.path, N = path.N;
    const pos = ro.geo.attributes.position.array;
    const aT = ro.geo.attributes.aT.array, aB = ro.geo.attributes.aB.array;
    for (let i = 0; i < spec.n; i++) {
      let t = p.t[i] + p.sp[i] * dt;
      if (t > 1) t -= 1;
      p.t[i] = t;
      const ft = t * N, i0 = Math.min(N - 1, Math.floor(ft)), fr2 = ft - i0, i1 = i0 + 1;
      const x = path.samp[i0 * 3] + (path.samp[i1 * 3] - path.samp[i0 * 3]) * fr2;
      const y = path.samp[i0 * 3 + 1] + (path.samp[i1 * 3 + 1] - path.samp[i0 * 3 + 1]) * fr2;
      const z = path.samp[i0 * 3 + 2] + (path.samp[i1 * 3 + 2] - path.samp[i0 * 3 + 2]) * fr2;
      const nx = path.nrm[i0 * 3], ny = path.nrm[i0 * 3 + 1], nz = path.nrm[i0 * 3 + 2];
      const bx = path.bnm[i0 * 3], by = path.bnm[i0 * 3 + 1], bz = path.bnm[i0 * 3 + 2];
      const env = Math.sin(Math.PI * t);
      /* converge to the axis at both ends: a straight run into the chamber centre,
         no splay across the shell */
      const rr = p.base[i] * (0.06 + 0.94 * Math.pow(env, 0.5));
      const ang = p.a0[i] + t * p.tw[i];
      const ca = Math.cos(ang) * rr, sa = Math.sin(ang) * rr;
      pos[i * 3] = x + nx * ca + bx * sa;
      pos[i * 3 + 1] = y + ny * ca + by * sa;
      pos[i * 3 + 2] = z + nz * ca + bz * sa;
      aT[i] = t;
      aB[i] = p.b0[i] * (0.32 + 0.68 * Math.pow(env, 0.3));
    }
    ro.geo.attributes.position.needsUpdate = true;
    ro.geo.attributes.aT.needsUpdate = true;
    ro.geo.attributes.aB.needsUpdate = true;
  }

  function conduitGeo(n, sizeFill, brightFill) {
    const g = new th.BufferGeometry();
    g.setAttribute('position', new th.BufferAttribute(new Float32Array(n * 3), 3));
    g.setAttribute('aT', new th.BufferAttribute(new Float32Array(n), 1));
    const sz = new Float32Array(n); if (sizeFill) sz.fill(sizeFill);
    const br = new Float32Array(n); if (brightFill) br.fill(brightFill);
    g.setAttribute('aS', new th.BufferAttribute(sz, 1));
    g.setAttribute('aB', new th.BufferAttribute(br, 1));
    return g;
  }

  ROOTS.forEach(r => {
    const curve = curveFor(r);
    const from = SECTOR_BY_ID[r.from], to = SECTOR_BY_ID[r.to];
    const spec = conduitSpec(r);
    /* each end of a conduit carries its own chamber's colour; a grain crossing the
       run reads as leaving one chamber and arriving at the other */
    const cFrom = new th.Color(from.shell).lerp(new th.Color(0xdfe8f5), 0.3);
    const cTo = new th.Color(to.shell).lerp(new th.Color(0xdfe8f5), 0.3);
    const uni = () => ({
      uMap: { value: grainTex }, uHead: { value: -1 }, uActive: { value: 0 },
      uRest: { value: spec.rest }, uScale: { value: 1 }, uSharp: { value: spec.sharp },
      uFrom: { value: cFrom.clone() }, uTo: { value: cTo.clone() }, uMag: { value: new th.Color(TOKENS.queen) },
      uDest: { value: new th.Color(to.shell) }
    });
    const geo = conduitGeo(spec.n);
    const mat = new th.ShaderMaterial({
      uniforms: uni(), vertexShader: CONDUIT_VS, fragmentShader: CONDUIT_FS,
      transparent: true, depthWrite: false, blending: th.AdditiveBlending
    });
    const points = new th.Points(geo, mat); scene.add(points);
    const litColor = new th.Color(to.shell).lerp(new th.Color(0xffffff), 0.25);
    const ants = [];
    for (let i = 0; i < 2; i++) {
      const sp = new th.Sprite(new th.SpriteMaterial({ map: glowTex, color: new th.Color(litColor).lerp(new th.Color(0xffffff), 0.4), transparent: true, opacity: 0, depthWrite: false, blending: th.AdditiveBlending, fog: false }));
      const cr = new th.Sprite(new th.SpriteMaterial({ map: dotTex, color: 0xfff4e0, transparent: true, opacity: 0.9, depthWrite: false, blending: th.AdditiveBlending, fog: false }));
      sp.add(cr); cr.scale.setScalar(0.32); cr.position.set(0, 0, 0.001);
      sp.scale.setScalar(0.62); sp.visible = false; scene.add(sp); ants.push(sp);
    }
    const ro = { r, curve, spec, seed: seedOf(r.id), thick: spec.rad, lateral: r.kind === 'lateral', geo, mat, points, ants, trail: 0 };
    fillConduit(ro);
    rootObjs[r.id] = ro;
  });

  /* authority seal on the Queen→Micromound root */
  const seal = new th.Sprite(new th.SpriteMaterial({ map: lock, transparent: true, opacity: 0.85, depthWrite: false, fog: false }));
  seal.scale.setScalar(2.1);
  seal.position.copy(rootObjs['q-mm'].curve.getPoint(0.42));
  scene.add(seal);

  /* approval boundary ring */
  const boundary = new th.Sprite(new th.SpriteMaterial({ map: glowTex, color: new th.Color(TOKENS.queen), transparent: true, opacity: 0, depthWrite: false, blending: th.AdditiveBlending, fog: false }));
  boundary.scale.setScalar(4); scene.add(boundary);

  /* ---- intra-sphere record links: cluster → its records, for every chamber ---- */
  const innerCache = {};
  function innerFor(id) {
    if (innerCache[id]) return innerCache[id];
    const so = sectorObjs[id], s = so.s;
    const segs = [];
    so.ctx.clusters.forEach(cl => {
      const c0 = cl.center;
      cl.records.forEach(r => { segs.push(c0[0], c0[1], c0[2], r.pos[0], r.pos[1], r.pos[2]); });
    });
    const g = new th.BufferGeometry();
    g.setAttribute('position', new th.BufferAttribute(new Float32Array(segs), 3));
    const m = new th.LineBasicMaterial({ color: new th.Color(s.core), transparent: true, opacity: 0, depthWrite: false, blending: th.AdditiveBlending });
    /* cluster ↔ cluster: the chamber's own context relationships */
    const cc = [];
    so.ctx.clusters.forEach((cl, i) => {
      const nx = so.ctx.clusters[(i + 1) % so.ctx.clusters.length];
      cc.push(cl.center[0], cl.center[1], cl.center[2], nx.center[0], nx.center[1], nx.center[2]);
    });
    const g2 = new th.BufferGeometry();
    g2.setAttribute('position', new th.BufferAttribute(new Float32Array(cc), 3));
    const m2 = new th.LineBasicMaterial({ color: new th.Color(s.shell), transparent: true, opacity: 0, depthWrite: false, blending: th.AdditiveBlending });
    so.grp.add(new th.LineSegments(g2, m2));
    const ls = new th.LineSegments(g, m); so.grp.add(ls);
    innerCache[id] = { ls, m, m2, g, g2 };
    return innerCache[id];
  }

  /* rewrite the spoke endpoints for the current cloud→ordered blend */
  function reflowLinks(id) {
    const ic = innerCache[id]; if (!ic) return;
    const so = sectorObjs[id], t2 = so.mat.uniforms.uOrg.value;
    const at = ic.g.attributes.position.array, at2 = ic.g2.attributes.position.array;
    const mixv = (p, o, k) => p[k] + ((o ? o[k] : p[k]) - p[k]) * t2;
    let w = 0;
    so.ctx.clusters.forEach(cl => {
      cl.records.forEach(rec => {
        at[w++] = mixv(cl.center, cl.org, 0); at[w++] = mixv(cl.center, cl.org, 1); at[w++] = mixv(cl.center, cl.org, 2);
        at[w++] = mixv(rec.pos, rec.org, 0); at[w++] = mixv(rec.pos, rec.org, 1); at[w++] = mixv(rec.pos, rec.org, 2);
      });
    });
    let w2 = 0;
    so.ctx.clusters.forEach((cl, i) => {
      const nx = so.ctx.clusters[(i + 1) % so.ctx.clusters.length];
      at2[w2++] = mixv(cl.center, cl.org, 0); at2[w2++] = mixv(cl.center, cl.org, 1); at2[w2++] = mixv(cl.center, cl.org, 2);
      at2[w2++] = mixv(nx.center, nx.org, 0); at2[w2++] = mixv(nx.center, nx.org, 1); at2[w2++] = mixv(nx.center, nx.org, 2);
    });
    ic.g.attributes.position.needsUpdate = true;
    ic.g2.attributes.position.needsUpdate = true;
  }

  /* ---- resident crew + pheromone memory ---- */
  const tmpV = new th.Vector3(), offN = new th.Vector3();
  /* live position of a record/cluster: the cloud seat, the ordered seat, or the
     blend currently on screen. One source of truth for dots, links and labels. */
  const orgT = id => { const so = sectorObjs[id]; return so ? so.mat.uniforms.uOrg.value : 0; };
  function livePos(so, node, out) {
    const t2 = so.mat.uniforms.uOrg.value, p = node.pos || node.center, o = node.org || p;
    return out.set(
      p[0] + (o[0] - p[0]) * t2 + so.s.pos[0],
      p[1] + (o[1] - p[1]) * t2 + so.s.pos[1],
      p[2] + (o[2] - p[2]) * t2 + so.s.pos[2]
    );
  }
  const TANF = Math.tan(42 * Math.PI / 360);
  const pxScale = (d, px) => 2 * TANF * d / (renderer.domElement.clientHeight || 700) * px;
  let lifeLast = 0, antEmit = 0;
  function colonyLife(ms, k) {
    const now = ms / 1000;
    const dt = Math.min(0.05, lifeLast ? (ms - lifeLast) / 1000 : 0.016);
    lifeLast = ms;
    const near = clamp((78 - cam.dist) / 46, 0, 1);
    SECTORS.forEach(s => {
      const so = sectorObjs[s.id];
      const focused = state.focus === s.id;
      const cls = so.ctx.clusters;
      const antA = Math.min(0.9, 0.14 + near * 0.8) * (state.focus && !focused ? 0.35 : 1)
        * (mission.missionState === 'disconnected' && s.child ? 0.25 : 1);
      so.crew.forEach(a => {
        if (k) {
          /* holds station; lays a pheromone run out to the cluster it serves */
          a.bob += dt;
          a.sp.position.set(
            a.slot.x + Math.sin(a.bob * 0.55) * s.r * 0.008,
            a.slot.y + Math.sin(a.bob * 0.7 + 1.3) * s.r * 0.012,
            a.slot.z + Math.cos(a.bob * 0.6) * s.r * 0.008
          );
          if (a.beam > 0) {
            a.beam = Math.max(0, a.beam - dt);
          } else if ((a.work -= dt) <= 0) {
            const pick = a.rnd() < 0.72 ? a.home : Math.floor(a.rnd() * cls.length);
            const c0 = cls[pick].center;
            a.targetC.set(c0[0], c0[1], c0[2]);
            a.target = cls[pick].label;
            a.beamDur = 1.3 + a.rnd() * 1.4;
            a.beam = a.beamDur;
            a.work = 4 + a.rnd() * 7;
            a.tasks++;
          }
        }
        /* orbs: constant legible screen size, brighter when selected or inside */
        const dcam = camera.position.distanceTo(so.grp.position);
        const detail = focused && state.level >= 1;
        const sel = state.ant === a;
        const px = (detail ? 54 : 30) * (a.isQueen ? 1.55 : a.isLead ? 1 : 0.62) * (sel ? 1.35 : 1);
        a.sp.scale.setScalar(pxScale(dcam, px));
        const base = antA * (sel ? 1.3 : detail ? 1 : 0.85) * (a.isQueen ? 1.2 : 1);
        a.sp.material.opacity += (Math.min(1, base) - a.sp.material.opacity) * 0.08;
        a.core.material.opacity += (Math.min(1, base * 1.15) - a.core.material.opacity) * 0.08;
        if (a.ring) {
          const puls = k ? 0.5 + 0.5 * Math.sin(ms * 0.0016) : 0.7;
          a.ring.material.opacity += (Math.min(0.85, base * (0.35 + 0.35 * puls)) - a.ring.material.opacity) * 0.08;
        }
      });
    });
    /* keep an open ant inspector live */
    if (state.ant && hooks.onAnt && now - antEmit > 0.45) {
      antEmit = now;
      hooks.onAnt(antPayload(state.ant));
    }
  }

  SECTORS.forEach(s => innerFor(s.id));   /* built once, at bring-up */
  mark('createStage:built', bootT0);

  /* ---- camera rig: limited orbit / tilt / pan / zoom ---- */
  const colonyCenter = () => {
    const c = new th.Vector3();
    SECTORS.forEach(s => c.add(new th.Vector3(...s.pos)));
    return c.multiplyScalar(1 / Math.max(1, SECTORS.length));
  };
  /* frame the whole spread with margin under the header: the fan is wide and
     tall, so home sits back and looks very slightly down at the centroid */
  /* the crystal is centred on the Queen, so home simply looks at the centroid */
  const HOME = { target: colonyCenter().add(new th.Vector3(0, 2.6, 0)), dist: 96, theta: 0.42, phi: 1.36 };
  const cam = { target: HOME.target.clone(), dist: HOME.dist, theta: HOME.theta, phi: HOME.phi };
  const want = { target: HOME.target.clone(), dist: HOME.dist, theta: HOME.theta, phi: HOME.phi };
  const LIMITS = { phi: [0.06, Math.PI - 0.06], dist: [2.2, 130] };

  const state = { level: 0, focus: null, cluster: null, record: null, ant: null, follow: false, motion: 'normal', labels: 'all', pheromones: 'all', reduced: false };
  let mission = { stepIndex: 0, stepT: 0, lit: {}, approval: false, trails: {}, activeAnt: null, antPos: null, missionState: 'active' };

  const TAU2 = Math.PI * 2;
  function sane() {
    /* one gate: nothing non-finite is ever allowed into the camera state, so no
       loop or interpolation downstream can run away */
    if (!Number.isFinite(want.theta)) want.theta = HOME.theta;
    if (!Number.isFinite(want.phi)) want.phi = HOME.phi;
    if (!Number.isFinite(want.dist)) want.dist = HOME.dist;
    if (!finV(want.target)) want.target.copy(HOME.target);
    want.phi = clamp(want.phi, LIMITS.phi[0], LIMITS.phi[1]);
    want.dist = clamp(want.dist, LIMITS.dist[0], LIMITS.dist[1]);
    want.target.x = clamp(want.target.x, -80, 80);
    want.target.y = clamp(want.target.y, -60, 60);
    want.target.z = clamp(want.target.z, -80, 80);
    if (!Number.isFinite(cam.theta)) cam.theta = want.theta;
    if (!Number.isFinite(cam.phi)) cam.phi = want.phi;
    if (!Number.isFinite(cam.dist)) cam.dist = want.dist;
    if (!finV(cam.target)) cam.target.copy(want.target);
  }
  function applyCam() {
    sane();
    let dth = (want.theta - cam.theta) % TAU2;
    if (dth > Math.PI) dth -= TAU2; else if (dth < -Math.PI) dth += TAU2;
    cam.theta += dth * 0.08;
    cam.phi += (want.phi - cam.phi) * 0.08;
    cam.dist += (want.dist - cam.dist) * 0.075;
    cam.target.lerp(want.target, 0.075);
    const sp = Math.sin(cam.phi), cp = Math.cos(cam.phi);
    camera.position.set(
      cam.target.x + cam.dist * sp * Math.sin(cam.theta),
      cam.target.y + cam.dist * cp,
      cam.target.z + cam.dist * sp * Math.cos(cam.theta)
    );
    camera.lookAt(cam.target);
  }

  function clamp(v, a, b) { return Math.max(a, Math.min(b, v)); }

  /* ---- flexible layout: chambers can be dragged, tunnels re-solve to follow ---- */
  const HOME_POS = {};
  SECTORS.forEach(s => { HOME_POS[s.id] = s.pos.slice(); });

  function reflow(id, heavy) {
    ROOTS.forEach(r => {
      if (r.from !== id && r.to !== id) return;
      const ro = rootObjs[r.id];
      ro.curve = curveFor(r);
      fillConduit(ro);
    });
    seal.position.copy(rootObjs['q-mm'].curve.getPoint(0.42));
  }


  function moveSector(id, dx, dy, depth) {
    const s = SECTOR_BY_ID[id], so = sectorObjs[id];
    const d = camera.position.distanceTo(so.grp.position);
    const wpp = 2 * Math.tan(42 * Math.PI / 360) * d / (mount.clientHeight || 700);
    const right = new th.Vector3().setFromMatrixColumn(camera.matrixWorld, 0);
    const up = new th.Vector3().setFromMatrixColumn(camera.matrixWorld, 1);
    const fwd = new th.Vector3().setFromMatrixColumn(camera.matrixWorld, 2).negate();
    const mv = right.multiplyScalar(dx * wpp).add(up.multiplyScalar(-dy * wpp));
    if (depth) mv.copy(fwd.multiplyScalar(-dy * wpp * 1.4));
    s.pos[0] = clamp(s.pos[0] + mv.x, -60, 60);
    s.pos[1] = clamp(s.pos[1] + mv.y, -46, 46);
    s.pos[2] = clamp(s.pos[2] + mv.z, -60, 60);
    so.grp.position.set(s.pos[0], s.pos[1], s.pos[2]);
    reflow(id);
  }

  /* ---- interaction ---- */
  let drag = null;
  canvas.style.cursor = 'grab';
  canvas.addEventListener('pointerdown', e => {
    const preHit = hitTest(e);
    const grab = (e.altKey || e.metaKey) && preHit && preHit.kind === 'sector' ? preHit.id : null;
    drag = { x: e.clientX, y: e.clientY, pan: !grab && (e.shiftKey || e.button === 1), move: grab, moved: 0 };
    try { canvas.setPointerCapture(e.pointerId); } catch (err) { /* no active pointer */ } canvas.style.cursor = 'grabbing';
  });
  canvas.addEventListener('pointermove', e => {
    const hit = hitTest(e);
    canvas.style.cursor = drag ? (drag.move ? 'move' : 'grabbing')
      : ((e.altKey || e.metaKey) && hit && hit.kind === 'sector') ? 'move' : (hit ? 'pointer' : 'grab');
    if (!drag) { hooks.onHover && hooks.onHover(hit); return; }
    const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
    drag.moved += Math.abs(dx) + Math.abs(dy);
    if (drag.move) {
      moveSector(drag.move, dx, dy, e.shiftKey);
    } else if (drag.pan) {
      const k = cam.dist * 0.0016;
      const right = new th.Vector3().subVectors(camera.position, cam.target).cross(new th.Vector3(0, 1, 0)).normalize();
      want.target.add(right.multiplyScalar(-dx * k)).add(new th.Vector3(0, dy * k, 0));
      want.target.x = clamp(want.target.x, -40, 40); want.target.y = clamp(want.target.y, -32, 28); want.target.z = clamp(want.target.z, -34, 34);
    } else {
      want.theta -= dx * 0.005;
      want.phi = clamp(want.phi - dy * 0.004, LIMITS.phi[0], LIMITS.phi[1]);
    }
    drag.x = e.clientX; drag.y = e.clientY;
  });
  window.addEventListener('keydown', e => {
    if ((e.key === 'l' || e.key === 'L') && !e.metaKey && !e.ctrlKey) {
      SECTORS.forEach(s => {
        s.pos[0] = HOME_POS[s.id][0]; s.pos[1] = HOME_POS[s.id][1]; s.pos[2] = HOME_POS[s.id][2];
        sectorObjs[s.id].grp.position.set(s.pos[0], s.pos[1], s.pos[2]);
      });
      SECTORS.forEach(s => reflow(s.id, true));
      HOME.target.copy(colonyCenter());
      want.target.copy(HOME.target);
    }
  });

  canvas.addEventListener('pointerup', e => {
    const finish = drag && drag.move;
    if (finish) reflow(finish, true);
    const wasDrag = drag && drag.moved > 6; drag = null; canvas.style.cursor = 'grab';
    if (wasDrag) return;
    const hit = hitTest(e);
    if (!hit) {
      /* empty space: drop the whole selection and return to the survey */
      state.ant = null; state.record = null; state.cluster = null;
      if (state.focus || state.level) {
        state.focus = null; state.level = 0; state.follow = false;
        Object.assign(want, { dist: HOME.dist, theta: HOME.theta, phi: HOME.phi });
        want.target.copy(HOME.target);
        hooks.onLevel && hooks.onLevel(0, null);
      } else if (hooks.onAnt) hooks.onAnt(null);
      return;
    }
    if (hit.kind === 'sector') select(hit.id);
    else if (hit.kind === 'ant') { state.ant = hit.ant; hooks.onAnt && hooks.onAnt(antPayload(hit.ant)); }
    else if (hit.kind === 'record') { state.ant = null; state.record = hit.rec; state.level = 4; hooks.onRecord && hooks.onRecord(hit.rec); }
    else if (hit.kind === 'cluster') { state.ant = null; state.cluster = hit.cl; state.level = 3; hooks.onCluster && hooks.onCluster(hit.cl); }
  });
  canvas.addEventListener('wheel', e => {
    e.preventDefault();
    const f = Math.exp(e.deltaY * 0.0014);
    const minD = state.focus ? SECTOR_BY_ID[state.focus].r * 1.45 : 18;
    want.dist = clamp(want.dist * f, minD, LIMITS.dist[1]);
    if (state.focus) {
      const r = SECTOR_BY_ID[state.focus].r;
      const lvl = want.dist < r * 2.35 ? 2 : want.dist < r * 4.8 ? 1 : 0;
      if (lvl === 0) { state.focus = null; state.cluster = null; state.record = null; want.target.copy(HOME.target); }
      if (lvl < state.level && state.level > 2) state.level = 2;
      if (state.level < 3 || lvl === 0) state.level = lvl;
      hooks.onLevel && hooks.onLevel(state.level, state.focus);
    }
  }, { passive: false });

  /* ---- chrome-aware label placement ---- */
  let avoidRects = [], avoidClock = 0;
  function refreshAvoid(nowMs) {
    if (nowMs - avoidClock < 400) return;
    avoidClock = nowMs;
    const cr = canvas.getBoundingClientRect();
    avoidRects = Array.prototype.map.call(document.querySelectorAll('[data-chrome-avoid]'), el => {
      const b = el.getBoundingClientRect();
      return { x0: b.left - cr.left, x1: b.right - cr.left, y0: b.top - cr.top, y1: b.bottom - cr.top };
    });
  }
  function chromeBlocked(x, y, side) {
    const x0 = side === 1 ? x : x - 132, x1 = side === 1 ? x + 132 : x;
    const y0 = y - 11, y1 = y + 11;
    for (let i = 0; i < avoidRects.length; i++) {
      const r = avoidRects[i];
      if (x1 > r.x0 - 4 && x0 < r.x1 + 4 && y1 > r.y0 - 3 && y0 < r.y1 + 3) return true;
    }
    return false;
  }

  const v = new th.Vector3();
  function project(p) {
    v.copy(p).project(camera);
    const w = mount.clientWidth, h = mount.clientHeight;
    return { x: (v.x * 0.5 + 0.5) * w, y: (-v.y * 0.5 + 0.5) * h, z: v.z, w, h };
  }
  function antPayload(a) {
    return Object.assign({}, a.info, {
      status: a.beam > 0 ? 'laying pheromone' : 'holding station',
      current_target: a.target || a.info.home_cluster,
      tasks_completed: a.tasks,
      deposits: a.drops,
      dwell: a.beam > 0
        ? Math.round((1 - a.beam / a.beamDur) * 100) + '% of run'
        : Math.max(0, a.work).toFixed(1) + 's to next run'
    });
  }

  function hitTest(e) {
    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left, my = e.clientY - rect.top;
    if (state.focus) {
      const so = sectorObjs[state.focus];
      let ba = null, bad = 32;
      so.crew.forEach(a => {
        const p = project(tmpV.copy(a.sp.position).add(so.grp.position));
        if (p.z > 1) return;
        const d = Math.hypot(p.x - mx, p.y - my);
        if (d < bad) { bad = d; ba = { kind: 'ant', ant: a }; }
      });
      if (ba) return ba;
    }
    if (state.level >= 2 && state.focus) {
      const so = sectorObjs[state.focus];
      let best = null, bd = 14;
      so.records.forEach(r => {
        if (state.level >= 3 && state.cluster && r.cluster !== state.cluster.label) return;
        const p = project(livePos(so, r, new th.Vector3()));
        const d = Math.hypot(p.x - mx, p.y - my);
        if (d < bd) { bd = d; best = { kind: 'record', rec: r }; }
      });
      if (best) return best;
      if (state.level === 2) {
        let bc = null, bcd = 26;
        so.ctx.clusters.forEach(cl => {
          const p = project(livePos(so, cl, new th.Vector3()));
          const d = Math.hypot(p.x - mx, p.y - my);
          if (d < bcd) { bcd = d; bc = { kind: 'cluster', cl }; }
        });
        if (bc) return bc;
      }
    }
    let hit = null, hz = Infinity;
    SECTORS.forEach(s => {
      const p = project(new th.Vector3(...s.pos));
      const edge = project(new th.Vector3(s.pos[0] + s.r, s.pos[1], s.pos[2]));
      const rad = Math.max(12, Math.abs(edge.x - p.x));
      if (Math.hypot(p.x - mx, p.y - my) < rad && p.z < hz) { hz = p.z; hit = { kind: 'sector', id: s.id }; }
    });
    return hit;
  }

  /* Operator restyle: one colour drives the chamber's whole palette (records,
     core light, links, crew orbs, both ends of every conduit it touches). */
  function setChamberStyle(id, cfg) {
    const so = sectorObjs[id]; if (!so || !cfg) return;
    const s = so.s;
    if (cfg.label && cfg.label !== s.label) {
      s.label = cfg.label;
      sectorLabels[id].textContent = cfg.label;
    }
    if (!cfg.color || cfg.color === s.shell) return;
    s.shell = cfg.color;
    const shell = new th.Color(cfg.color);
    const core = shell.clone().lerp(new th.Color(0xffffff), 0.42);
    const nuc = shell.clone().multiplyScalar(0.6);
    s.core = '#' + core.getHexString();
    s.nucleus = '#' + nuc.getHexString();
    so.nucleus.material.color.copy(nuc);
    so.glow.material.color.copy(shell);
    sectorLabels[id].style.color = cfg.color;
    /* records: same depth/tint rule as at build time, new endpoints */
    const col = so.points.geometry.attributes.acolor;
    const c = new th.Color();
    so.records.forEach((rec, i) => {
      const rad = Math.min(1, Math.hypot(rec.pos[0], rec.pos[1], rec.pos[2]) / s.r);
      c.copy(shell).lerp(core, Math.min(1, Math.pow(1 - rad, 1.5) * 1.15)).multiplyScalar(0.5 + rec.tint * 0.45);
      col.array[i * 3] = c.r; col.array[i * 3 + 1] = c.g; col.array[i * 3 + 2] = c.b;
    });
    col.needsUpdate = true;
    const ic = innerCache[id];
    if (ic) { ic.m.color.copy(core); ic.m2.color.copy(shell); }
    so.crew.forEach(a => {
      if (a.isQueen) return;
      a.color.copy(a.isLead ? core : shell);
      a.sp.material.color.copy(a.color);
      a.core.material.color.copy(a.color.clone().lerp(new th.Color(0xffffff), 0.55));
      a.info.color = '#' + a.color.getHexString();
    });
    ROOTS.forEach(rt => {
      const ro = rootObjs[rt.id]; if (!ro || (rt.from !== id && rt.to !== id)) return;
      const u = ro.mat.uniforms;
      const tint = h => new th.Color(h).lerp(new th.Color(0xdfe8f5), 0.3);
      u.uFrom.value.copy(tint(SECTOR_BY_ID[rt.from].shell));
      u.uTo.value.copy(tint(SECTOR_BY_ID[rt.to].shell));
      u.uDest.value.set(SECTOR_BY_ID[rt.to].shell);
    });
  }

  /* Operator restyle of a single ant: name and orb colour. Returns the refreshed
     inspector payload so the panel stays in step. */
  function setAntStyle(antId, cfg) {
    let found = null;
    SECTORS.forEach(s => sectorObjs[s.id].crew.forEach(a => { if (a.info.ant_id === antId) found = a; }));
    if (!found || !cfg) return null;
    if (typeof cfg.name === 'string') { found.role = cfg.name; found.info.role = cfg.name; }
    if (cfg.color) {
      found.color.set(cfg.color);
      found.sp.material.color.set(cfg.color);
      found.core.material.color.copy(found.color.clone().lerp(new th.Color(0xffffff), 0.55));
      found.info.color = cfg.color;
    }
    return antPayload(found);
  }

  function select(id) {
    const s = SECTOR_BY_ID[id];
    state.focus = id; state.cluster = null; state.record = null; state.ant = null; state.level = 1;
    want.target.set(...s.pos); want.dist = s.r * 3.4;
    hooks.onLevel && hooks.onLevel(1, id);
    hooks.onSector && hooks.onSector(id);
  }

  /* ---- DOM labels ---- */
  function mkLabel(cls, txt) {
    const d = document.createElement('div');
    d.className = cls; d.textContent = txt;
    overlay.appendChild(d); return d;
  }
  const sectorLabels = {};
  SECTORS.forEach(s => {
    const d = document.createElement('div');
    d.style.cssText = 'position:absolute;transform:translate(-50%,-50%);font:600 11px/1.35 "IBM Plex Mono",monospace;letter-spacing:.16em;white-space:pre;text-align:center;text-shadow:0 0 18px rgba(0,0,0,.9);transition:opacity .35s;';
    d.style.color = s.shell === TOKENS.queenDeep ? TOKENS.queen : s.shell;
    d.textContent = s.label;
    overlay.appendChild(d);
    sectorLabels[s.id] = d;
  });
  const clusterLabels = [];
  for (let i = 0; i < 12; i++) {
    const d = document.createElement('div');
    d.style.cssText = 'position:absolute;transform:translate(-50%,-50%);font:500 9.5px/1.3 "IBM Plex Mono",monospace;letter-spacing:.1em;color:rgba(201,207,220,.82);white-space:nowrap;text-shadow:0 0 12px #000;opacity:0;transition:opacity .3s;';
    overlay.appendChild(d); clusterLabels.push(d);
  }
  const recordLabels = [];
  const moveHint = document.createElement('div');
  moveHint.textContent = 'ALT-DRAG A CHAMBER TO REPOSITION · SHIFT TO PUSH IN DEPTH · L RESETS LAYOUT';
  moveHint.style.cssText = 'position:absolute;left:14px;bottom:12px;font:500 8.5px/1 "IBM Plex Mono",monospace;letter-spacing:.14em;color:rgba(139,147,168,.55);pointer-events:none;';
  overlay.appendChild(moveHint);
  const crewLabels = [];
  for (let i = 0; i < 24; i++) {
    const d = document.createElement('div');
    d.style.cssText = 'position:absolute;transform:translate(12px,-50%);font:500 9px/1.3 "IBM Plex Mono",monospace;letter-spacing:.08em;white-space:nowrap;text-shadow:0 0 10px #000,0 0 3px #000;opacity:0;transition:opacity .3s;';
    overlay.appendChild(d); crewLabels.push(d);
  }
  for (let i = 0; i < 16; i++) {
    const d = document.createElement('div');
    d.style.cssText = 'position:absolute;transform:translate(10px,-50%);font:400 9.5px/1.3 "IBM Plex Mono",monospace;color:rgba(244,233,214,.9);white-space:nowrap;text-shadow:0 0 10px #000;opacity:0;transition:opacity .25s;padding-left:6px;border-left:1px solid rgba(255,63,164,.45);';
    overlay.appendChild(d); recordLabels.push(d);
  }

  /* ---- mission projection ---- */
  function setMission(m) { mission = Object.assign(mission, m); }

  /* ---- adaptive quality: a slow rasteriser must never be asked to keep drawing ---- */
  const perf = { avg: 0, n: 0, level: 0, lastDraw: 0, slack: false, stalled: false };
  function setQuality(level, rms) {
    level = Math.min(2, level);
    if (level <= perf.level) return;
    perf.level = level;
    mark('frame ' + Math.round(rms) + 'ms → quality ' + level, performance.now());
    renderer.setPixelRatio(1);
    const keep = level === 1 ? 0.5 : 0.2;
    ROOTS.forEach(r => { const ro = rootObjs[r.id]; ro.geo.setDrawRange(0, Math.ceil(ro.spec.n * keep)); });
    SECTORS.forEach(s => {
      const so = sectorObjs[s.id];
      if (level === 2) {
        so.points.geometry.setDrawRange(0, Math.ceil(so.records.length * 0.4));
      }
    });
  }
  /* circuit breaker: a render that blocks for a second is not a quality problem */
  function breaker(rms) {
    perf.stalled = true;
    dead = true;
    cancelAnimationFrame(raf);
    mark('frame ' + Math.round(rms) + 'ms → render stopped', performance.now());
    hooks.onStall && hooks.onStall(Math.round(rms));
  }

  /* ---- frame ---- */
  let t = 0, raf = 0, dead = false, frameLast = 0, firstFrameLogged = false;
  const clockScale = () => state.motion === 'off' ? 0 : state.motion === 'calm' ? 0.5 : 1;

  function frame(ms) {
    if (dead) return;
    raf = requestAnimationFrame(frame);
    /* pace to ~30fps and give the thread slack after an over-budget render */
    if (ms - perf.lastDraw < (perf.slack ? 90 : 31)) return;
    perf.slack = false;
    perf.lastDraw = ms;
    const dt = Math.min(0.05, (ms - t) / 1000 || 0.016); t = ms;
    const k = clockScale();
    const dtSec = Math.min(0.05, frameLast ? (ms - frameLast) / 1000 : 0.016);
    frameLast = ms;

    applyCam();

    /* sector emphasis */
    SECTORS.forEach(s => {
      const so = sectorObjs[s.id];
      const focused = state.focus === s.id;
      const involved = mission.lit && mission.lit[s.id];
      const targetGlow = focused ? 0.34 : involved ? 0.6 : 0.44;
      so.glow.material.opacity += (targetGlow - so.glow.material.opacity) * 0.05;
      const nucBase = s.id === 'queen' ? 1.1 : 1;
      const puls = k ? (0.06 * Math.sin(ms * 0.0012 + s.r)) : 0;
      so.nucleus.material.opacity += ((involved ? nucBase + 0.22 : nucBase) + puls - so.nucleus.material.opacity) * 0.06;
      const dim = state.focus && !focused && state.level >= 2 ? 0.42 : 1;
      so.mat.uniforms.uAlpha.value += (dim - so.mat.uniforms.uAlpha.value) * 0.05;
      const inside = focused && state.level >= 2;
      so.mat.uniforms.uScale.value += ((inside ? 0.8 : 1) - so.mat.uniforms.uScale.value) * 0.06;
      so.mat.uniforms.uRec.value += ((inside ? 1.7 : 1) - so.mat.uniforms.uRec.value) * 0.06;
      /* clicking a chamber orders its records; leaving it lets them relax back */
      const wantOrg = focused && state.level >= 1 ? 1 : 0;
      const uo = so.mat.uniforms.uOrg;
      if (Math.abs(wantOrg - uo.value) > 0.0008) {
        uo.value += (wantOrg - uo.value) * 0.07;
        reflowLinks(s.id);
      }
      if (mission.missionState === 'disconnected' && s.child) {
        so.glow.material.opacity *= 0.3;
      }
    });

    /* record links are always present; they simply grow legible as you close in */
    Object.keys(innerCache).forEach(id => {
      const focusedHere = state.focus === id;
      const near = clamp((58 - cam.dist) / 30, 0, 1);
      const want2 = (focusedHere && state.level >= 2) ? 0.36 : (state.focus && !focusedHere) ? 0.03 : 0.06 + near * 0.16;
      const ic = innerCache[id];
      ic.m.opacity += (want2 - ic.m.opacity) * 0.06;
      ic.m2.opacity += (want2 * 0.7 - ic.m2.opacity) * 0.06;
    });

    colonyLife(ms, k);
    refreshAvoid(ms);

    /* conduits: only the route backed by real state carries a travelling wave */
    ROOTS.forEach(r => {
      const ro = rootObjs[r.id];
      const litInfo = mission.lit && mission.lit[r.id];
      const trail = (mission.trails && mission.trails[r.id]) || 0;
      const pherOn = state.pheromones !== 'off';
      const u = ro.mat.uniforms;
      let head = -1, act = 0;
      if (litInfo) {
        let tt = litInfo.progress != null ? litInfo.progress : ((ms * 0.00016) % 1);
        if (litInfo.halt != null) tt = Math.min(tt, litInfo.halt);
        if (!k) tt = litInfo.halt != null ? litInfo.halt : 0.5;
        head = litInfo.reverse ? 1 - tt : tt;
        act = 1;
      }
      u.uHead.value = head;
      u.uActive.value += (act - u.uActive.value) * 0.12;
      const restT = ro.spec.rest * (state.level >= 2 ? 0.6 : 1)
        + (pherOn ? trail * 0.22 : 0) + (litInfo ? 0.2 : 0);
      u.uRest.value += (restT - u.uRest.value) * 0.06;
      u.uScale.value += (1 - u.uScale.value) * 0.1;
      /* the stream itself travels chamber → chamber, slowly, whether or not a
         mission is running; the mission adds the bright wave on top */
      if (perf.n % 3 === 0) driftConduit(ro, k ? dtSec * 3 : 0);

      ro.ants.forEach((sp, i) => {
        const show = litInfo && litInfo.ants > i;
        if (!show) { sp.material.opacity = 0; sp.visible = false; return; }
        sp.visible = true;
        let tt = litInfo.progress != null ? litInfo.progress : ((ms * 0.00009 + i * 0.4) % 1);
        if (litInfo.halt != null) tt = Math.min(tt, litInfo.halt);
        if (!k) tt = litInfo.halt != null ? litInfo.halt : 0.5;
        const gt = Math.min(0.999, Math.max(0.001, litInfo.reverse ? 1 - tt : tt));
        const p = ro.curve.getPoint(gt);
        sp.position.copy(p);
        const tg = ro.curve.getTangentAt(gt);
        offN.set(0, 1, 0); if (Math.abs(tg.y) > 0.85) offN.set(1, 0, 0);
        offN.crossVectors(tg, offN).normalize();
        sp.position.addScaledVector(offN, Math.cos(ms * 0.0007 + i * 2.1) * ro.spec.rad * 0.3);
        sp.scale.setScalar(Math.max(ro.spec.rad * 0.9, pxScale(camera.position.distanceTo(sp.position), 11)));
        sp.material.opacity += ((cam.dist < 24 ? 0.7 : 0.95) - sp.material.opacity) * 0.15;
      });
    });

    /* approval boundary */
    if (mission.approval) {
      const ro = rootObjs[mission.approval.root] || rootObjs['v-m'];
      boundary.position.copy(ro.curve.getPoint(mission.approval.at || 0.6));
      const pl = k ? 0.55 + 0.25 * Math.sin(ms * 0.004) : 0.6;
      boundary.material.opacity += (pl - boundary.material.opacity) * 0.12;
      boundary.scale.setScalar(3.4 + (k ? Math.sin(ms * 0.004) * 0.5 : 0));
    } else boundary.material.opacity += (0 - boundary.material.opacity) * 0.1;

    seal.material.opacity = mission.missionState === 'stopped' ? 1 : 0.7;
    seal.material.color.set(mission.missionState === 'stopped' ? TOKENS.rose : TOKENS.queen);

    /* labels */
    SECTORS.forEach(s => {
      const d = sectorLabels[s.id];
      const p = project(new th.Vector3(...s.pos));
      const edge = project(new th.Vector3(s.pos[0] + s.r, s.pos[1], s.pos[2]));
      const rad = Math.abs(edge.x - p.x);
      const side = s.labelSide === 'right' ? 1 : -1;
      /* keep clear of the chrome panels; try each side, then above, then below */
      const cands = [
        { x: p.x + side * (rad + 34), y: p.y + (s.child ? rad * 0.4 : 0), s: side },
        { x: p.x - side * (rad + 34), y: p.y + (s.child ? rad * 0.4 : 0), s: -side },
        { x: p.x + side * (rad + 34), y: p.y - rad - 26, s: side },
        { x: p.x - side * (rad + 34), y: p.y - rad - 26, s: -side }
      ];
      let pick = cands.find(c => !chromeBlocked(c.x, c.y, c.s));
      if (!pick) {
        let top = null;
        for (let ai = 0; ai < avoidRects.length; ai++) {
          const rr = avoidRects[ai];
          if (p.x > rr.x0 - 150 && p.x < rr.x1 + 150 && (top === null || rr.y0 < top)) top = rr.y0;
        }
        pick = { x: p.x, y: (top === null ? p.y : top - 16), s: side };
      }
      const clear = true;
      d.style.left = pick.x + 'px';
      d.style.top = pick.y + 'px';
      d.style.transform = 'translate(' + (pick.s === 1 ? '0' : '-100%') + ',-50%)';
      d.style.textAlign = pick.s === 1 ? 'left' : 'right';
      const show = clear && state.labels !== 'off' && (state.level === 0 || state.focus === s.id || state.level < 2)
        && p.x > -40 && p.x < p.w + 40 && p.y > 30 && p.y < p.h - 20;
      d.style.opacity = show && p.z < 1 ? (state.focus && state.focus !== s.id ? 0.35 : 1) : 0;
    });
    /* crew role labels inside the sector you're viewing */
    const crewHost = (state.labels !== 'off' && state.focus && state.level >= 1) ? sectorObjs[state.focus] : null;
    crewLabels.forEach((d, i) => {
      const a = crewHost && crewHost.crew[i];
      if (!a) { d.style.opacity = 0; return; }
      const p = project(tmpV.copy(a.sp.position).add(crewHost.grp.position));
      const txt = a.role;
      if (d.textContent !== txt) d.textContent = txt;
      d.style.color = '#' + a.color.getHexString();
      d.style.left = p.x + 'px'; d.style.top = p.y + 'px';
      const vis = p.z < 1 && p.x > 4 && p.x < p.w - 92 && p.y > 40 && p.y < p.h - 40;
      d.style.opacity = vis ? (state.ant === a ? 1 : a.isQueen ? 0.95 : a.isLead ? 0.78 : 0.5) : 0;
    });

    if (state.focus && state.level >= 2) {
      const so = sectorObjs[state.focus];
      so.ctx.clusters.forEach((cl, i) => {
        const d = clusterLabels[i]; if (!d) return;
        const p = project(livePos(so, cl, tmpV));
        d.textContent = cl.label.toUpperCase();
        d.style.left = p.x + 'px'; d.style.top = p.y + 'px';
        const vis = p.x > 4 && p.x < p.w - 4 && p.y > 40 && p.y < p.h - 60;
        const on = state.labels !== 'off' && p.z < 1 && vis && (!state.cluster || state.cluster.label === cl.label);
        d.style.opacity = on ? (state.cluster ? 1 : 0.8) : 0;
      });
      const rl = state.cluster ? state.cluster.records.slice(0, 16) : [];
      recordLabels.forEach((d, i) => {
        const r = rl[i];
        if (!r) { d.style.opacity = 0; return; }
        const p = project(livePos(so, r, tmpV));
        d.textContent = r.title;
        d.style.left = p.x + 'px'; d.style.top = p.y + 'px';
        d.style.opacity = (p.z < 1 && p.x > 4 && p.x < p.w - 180 && p.y > 40 && p.y < p.h - 40) ? 0.9 : 0;
      });
    } else {
      clusterLabels.forEach(d => d.style.opacity = 0);
      recordLabels.forEach(d => d.style.opacity = 0);
    }

    /* follow the active ant */
    if (state.follow && mission.activeSeg && rootObjs[mission.activeSeg]) {
      const ro = rootObjs[mission.activeSeg];
      const li = mission.lit[mission.activeSeg];
      const tt = li && li.progress != null ? li.progress : 0.5;
      const p = ro.curve.getPoint(Math.min(0.999, tt));
      want.target.lerp(p, 0.08);
      want.dist += (16 - want.dist) * 0.02;
    }

    if (!firstFrameLogged) { firstFrameLogged = true; mark('firstFrame', bootT0); }
    const rT0 = performance.now();
    renderer.render(scene, camera);
    const rms = performance.now() - rT0;
    perf.avg = perf.avg ? perf.avg * 0.85 + rms * 0.15 : rms;
    perf.n++;
    /* react to the FIRST slow frame, not to a 30-frame average */
    if (rms > 1000) { breaker(rms); return; }
    if (rms > 500) setQuality(2, rms);
    else if (rms > 120) setQuality(1, rms);
    else if (perf.avg > 45 && perf.n % 20 === 0) setQuality(perf.level + 1, perf.avg);
    if (rms > 60) perf.slack = true;
  }

  function resize() {
    const w = mount.clientWidth || 1, h = mount.clientHeight || 1;
    renderer.setSize(w, h, false);
    camera.aspect = w / h; camera.updateProjectionMatrix();
    const fit = 62 * Math.max(1, 1.75 / camera.aspect);
    HOME.dist = fit;
    if (!state.focus) { want.dist = fit; want.target.copy(HOME.target); cam.dist = cam.dist || fit; }
  }
  resize();
  const ro = new ResizeObserver(resize); ro.observe(mount);
  raf = requestAnimationFrame(frame);

  return {
    state, setMission, select, resize, clearAnt() { state.ant = null; },
    resetLayout() {
      SECTORS.forEach(s => {
        s.pos[0] = HOME_POS[s.id][0]; s.pos[1] = HOME_POS[s.id][1]; s.pos[2] = HOME_POS[s.id][2];
        sectorObjs[s.id].grp.position.set(s.pos[0], s.pos[1], s.pos[2]);
      });
      SECTORS.forEach(s => reflow(s.id, true));
      HOME.target.copy(colonyCenter());
      want.target.copy(HOME.target);
    },
    reset() { state.focus = null; state.cluster = null; state.record = null; state.ant = null; state.level = 0; state.follow = false; Object.assign(want, { dist: HOME.dist, theta: HOME.theta, phi: HOME.phi }); want.target.copy(HOME.target); hooks.onLevel && hooks.onLevel(0, null); },
    up() {
      if (state.level >= 4) { state.record = null; state.level = 3; }
      else if (state.level === 3) { state.cluster = null; state.level = 2; }
      else if (state.level === 2) { state.level = 1; want.dist = SECTOR_BY_ID[state.focus].r * 3.4; }
      else this.reset();
      hooks.onLevel && hooks.onLevel(state.level, state.focus);
    },
    enter() {
      if (!state.focus) return;
      state.level = 2; want.dist = SECTOR_BY_ID[state.focus].r * 1.8;
      hooks.onLevel && hooks.onLevel(2, state.focus);
    },
    setPref(k2, v2) { state[k2] = v2; },
    setFollow(b) { state.follow = b; },
    setChamberStyle,
    setAntStyle,
    contextFor(id) { return sectorObjs[id].ctx; },
    dispose() { dead = true; cancelAnimationFrame(raf); ro.disconnect(); renderer.dispose(); mount.innerHTML = ''; }
  };
}

/* ---- WebGL-absent fallback: the same topology, flat, no motion ---- */
export function createFallback(mount) {
  const c = document.createElement('canvas');
  c.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;';
  mount.appendChild(c);
  function draw() {
    const w = mount.clientWidth, h = mount.clientHeight, dpr = Math.min(2, devicePixelRatio || 1);
    c.width = w * dpr; c.height = h * dpr;
    const g = c.getContext('2d'); g.scale(dpr, dpr);
    g.fillStyle = '#05070c'; g.fillRect(0, 0, w, h);
    const sc = Math.min(w / 62, h / 42), cx = w / 2 - 2 * sc, cy = h / 2 + 2 * sc;
    const P = s => [cx + s.pos[0] * sc, cy - s.pos[1] * sc];
    ROOTS.forEach(r => {
      const a = P(SECTOR_BY_ID[r.from]), b = P(SECTOR_BY_ID[r.to]);
      g.strokeStyle = r.kind === 'authority' ? 'rgba(255,63,164,.45)' : 'rgba(58,68,92,.9)';
      g.lineWidth = r.kind === 'authority' ? 2 : 5; g.beginPath(); g.moveTo(a[0], a[1]); g.lineTo(b[0], b[1]); g.stroke();
    });
    SECTORS.forEach(s => {
      const p = P(s);
      g.strokeStyle = s.shell; g.globalAlpha = 0.9; g.lineWidth = 1.5;
      g.beginPath(); g.arc(p[0], p[1], s.r * sc, 0, 7); g.stroke();
      g.globalAlpha = 0.12; g.fillStyle = s.shell; g.fill(); g.globalAlpha = 1;
      g.fillStyle = s.shell; g.font = '600 10px "IBM Plex Mono",monospace';
      g.textAlign = 'center'; g.fillText(s.label.replace('\n', ' '), p[0], p[1] + s.r * sc + 16);
    });
  }
  draw();
  new ResizeObserver(draw).observe(mount);
  return { fallback: true, setMission() { }, reset() { }, up() { }, enter() { }, setPref() { }, setFollow() { }, select() { }, resize: draw, state: { level: 0 } };
}
