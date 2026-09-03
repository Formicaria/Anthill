/* ─────────────────────────────────────────────────────────────────────────────
   COLONY LIVE — the WebGL renderer. v0.3.8.116.

   It owns three.js and nothing else. Everything it draws arrives through
   `setScene(scene)` from `colony-topology.js`; it never decides mission state,
   never invents activity, never manufactures a record, and holds no clock that
   application state can be read out of.

   ── WHY three.js AND HOW IT LOADS ──────────────────────────────────────────
   `window.THREE`, from `/ui/vendor/three.min.js` — a pinned 0.128.0 UMD build
   served as an embedded resource from this origin. There is no import, no
   bundler, no CDN and no Blob evaluation, so the console's CSP stays
   `script-src 'self'`. If the global is absent this file reports unavailable
   and `colony-live.js` keeps the classic canvas: a missing dependency degrades,
   it never blanks the view.

   ── THE REFERENCE, AND WHERE THIS DEPARTS FROM IT ──────────────────────────
   `.116` ports the Claude Design renderer literally: its world scale (chambers
   at ±16.5 with radii 3.1–7.7, not the ×14 blow-up `.115` invented), its camera
   (FOV 42, near 0.5, far 400, home distance fitted from the aspect), its point
   shaders, its four texture stop tables, its Catmull-Rom conduits sampled on a
   rotation-minimising frame, its pixel-sized crew orbs, its screen-space hit
   test, its DOM label pools and its quality ladder. Where a number appears
   below it is the reference's number.

   THREE THINGS ARE DELIBERATELY NOT PORTED, and each is named where it would
   have gone. All three are the same defect wearing different clothes — a picture
   that is true of the reference's SAMPLE DATA and false of this colony:

     1. GENERATED RECORDS. The reference builds nine named clusters per chamber
        and 6–17 invented records in each, which is why its chambers look dense.
        Anthill's records are the ones the colony actually wrote, its clusters
        are the event types those records actually have, and a chamber holding
        nothing draws no grains. Chamber PRESENCE is carried by the core light,
        which is light and not data, so an empty chamber is small and lit rather
        than absent.

     2. THE MISSION CLOCK. The reference advances `litInfo.progress` from a host
        timer and sweeps a bright head along the conduit. There is no per-task
        progress in this model, so a swept head would be an animation of a number
        that does not exist. What lights a conduit here is real, and there are
        exactly two kinds of it — see `conduitState` below.

     3. THE ANT WORK TIMER. `a.work -= dt` picks a cluster at random every few
        seconds and lays a "pheromone run". An idle ant that appears to be
        working is the single most misleading thing this view could draw. An ant
        is `working` only when a real task is running against it, and the
        pheromone strength it shows is the trail the colony actually recorded.

   And one restyle is narrowed rather than dropped: an operator may recolour a
   chamber (presentation, like the layout that already persists to /ui/state)
   but may not rename one. A chamber's name is the registry's.

   ── MOTION, AND WHERE THE LINE ACTUALLY IS ─────────────────────────────────
   An earlier `.116` pass also froze the conduit grains, reasoning that flow
   along a permanent link claims work is passing through it. That was the rule
   applied one step too far, and what it bought was a console that looked dead.

   The line is not motion versus stillness, it is AMBIENT versus ASSERTED. Grains
   drifting along a passage say the passage exists and the view is live, the way
   a cursor blinks; they carry no claim about any task. What would be a lie is a
   BRIGHT WAVE with no event behind it, or an ant that looks busy while idle.

   So the grains drift, always, at the reference's speed — and `conduitState`
   admits exactly two things that may brighten a conduit beyond that, both facts
   with rows behind them:

     · A RECORDED TRANSITION travels it, one wave per unique event id, ever. The
       event names the ant, so the route is known rather than inferred. This is
       the colony lighting up as work actually moves through it.
     · A RUNNING TASK sits at one end of a persisted mission edge. That raises
       the conduit's RESTING brightness and never sweeps a head, because a task
       status is not a position along a line.

   The pheromone layer is drawn from both of its real records: a conduit's rest
   rises with how many transitions have been recorded across it this session, and
   an ant's orb brightens with its own `TrailView.Strength` out of the
   `pheromone_trails` table. Motion `off` and `prefers-reduced-motion` freeze
   every one of these.
   ───────────────────────────────────────────────────────────────────────────── */
(function () {
  'use strict';

  var T = function () { return window.THREE; };

  /** WebGL plus the vendored library. Both, because either alone is a blank view. */
  function available() {
    if (!T()) return false;
    try {
      var c = document.createElement('canvas');
      return !!(c.getContext('webgl2') || c.getContext('webgl') || c.getContext('experimental-webgl'));
    } catch (e) { return false; }
  }

  /* ── Palette ───────────────────────────────────────────────────────────────
     The reference's console tokens, verbatim. */
  var TOKENS = {
    queen: '#ff3fa4', queenDeep: '#e21f7b', gold: '#f5b23c',
    cyan: '#35aadf', cyanHot: '#57c7f0', orange: '#fb923c', rose: '#ef4444',
    amber: '#f59e0b', purple: '#8b5cf6', cream: '#f4e9d6'
  };

  /* ── Sector presentation ───────────────────────────────────────────────────
     Position, radius and three colours per chamber — shell (mid), core (light,
     for record grains) and nucleus (DEEPER than the shell; additive blending
     over black makes a deep hue read as saturated light and a pale one as fog).

     Membership is the SERVER's — the registry's `Colony`, projected by
     ColonyLiveProjection — and this table never decides which ants live where.
     A sector id it has not been taught about still renders, in neutral grey at
     a seat on the equatorial ring, because an unknown sector is a real sector.

     The seven reference chambers keep the reference's exact seats and radii.
     Anthill's two extra chambers take the free equatorial axes — and they sit
     at 33, not at the diagonals' own radius of 23.33. Rendered and measured:
     at 23.33 each of them was 17.9 units from its nearest neighbour, barely two
     combined radii, and Unassigned crowded Intelligence into one smear. The
     reference's equatorial chambers are 33 apart and roughly 2–3 radii clear of
     each other, and that ratio is what makes the colony read as a crystal with
     galleries rather than a pile. Pushed out, these two clear their neighbours
     by 23.3 — the same 2.7 radii. */
  var LOOK = {
    queen: { pos: [0, 0, 0], r: 7.7, shell: TOKENS.queenDeep, core: TOKENS.gold, nucleus: '#c2247e', side: 'left' },
    intel: { pos: [-16.5, 0, 16.5], r: 5.0, shell: TOKENS.cyan, core: TOKENS.cyanHot, nucleus: '#0d7f9c', side: 'right' },
    forge: { pos: [16.5, 0, 16.5], r: 5.3, shell: TOKENS.orange, core: '#ffb26b', nucleus: '#c05f18', side: 'right' },
    valid: { pos: [16.5, 0, -16.5], r: 5.0, shell: TOKENS.rose, core: '#ff7a7a', nucleus: '#b02f3e', side: 'right' },
    memory: { pos: [-16.5, 0, -16.5], r: 5.5, shell: TOKENS.amber, core: '#ffcf6b', nucleus: '#b8811c', side: 'right' },
    output: { pos: [0, 17.0, 0], r: 4.7, shell: TOKENS.purple, core: '#b79bff', nucleus: '#6247c0', side: 'left' },
    mound: { pos: [0, -17.0, 0], r: 3.1, shell: TOKENS.queen, core: TOKENS.queen, nucleus: '#bc2872', side: 'left', child: true },
    homelab: { pos: [33, 0, 0], r: 4.5, shell: '#6f8ea8', core: '#9fb6c9', nucleus: '#3f5a70', side: 'right' },
    unassigned: { pos: [-33, 0, 0], r: 3.6, shell: '#6b7280', core: '#9aa1ab', nucleus: '#404650', side: 'left' }
  };
  var NEUTRAL = { r: 4.0, shell: '#6b7280', core: '#9aa1ab', nucleus: '#404650', side: 'right' };
  function look(id) { return LOOK[id] || NEUTRAL; }
  function home(id, i) {
    if (LOOK[id]) return LOOK[id].pos.slice();
    // An unknown sector still needs a deterministic seat outside the known ring,
    // rather than being dropped at the origin on top of the Queen.
    var a = i * 2.39996;
    return [Math.cos(a) * 31, ((i % 3) - 1) * 6.5, Math.sin(a) * 31];
  }

  /* ── THE PERMANENT WEB ─────────────────────────────────────────────────────
     The reference's sixteen roots at the reference's bows, plus two structural
     links for this colony's two extra chambers. Three kinds:

       structural — the backbone. "These chambers are adjacent in the workflow."
       authority  — Queen → Micromound. A GRANT, not a dependency; the only edge
                    in the scene that delegates, and the only one drawn as its own kind.
       lateral    — a gallery. "Work can pass directly between these two."

     A permanent link is a claim about what the colony CAN do, which is true. It
     is drawn still, and only motion would make it read as traffic. */
  var ROOTS = [
    { id: 'q-i', from: 'queen', to: 'intel', kind: 'structural', bow: [1.6, 0.6, -3.4] },
    { id: 'q-f', from: 'queen', to: 'forge', kind: 'structural', bow: [0.4, 2.2, 3.2] },
    { id: 'q-m', from: 'queen', to: 'memory', kind: 'structural', bow: [1.2, -1.4, 4.0] },
    { id: 'q-o', from: 'queen', to: 'output', kind: 'structural', bow: [-1.0, -2.4, -2.6] },
    { id: 'i-f', from: 'intel', to: 'forge', kind: 'structural', bow: [0.2, 2.8, -1.8] },
    { id: 'f-v', from: 'forge', to: 'valid', kind: 'structural', bow: [2.6, 0.4, 2.2] },
    { id: 'v-m', from: 'valid', to: 'memory', kind: 'structural', bow: [2.4, -1.0, -2.0] },
    { id: 'q-h', from: 'queen', to: 'homelab', kind: 'structural', bow: [-1.8, 1.2, 2.8] },
    { id: 'q-u', from: 'queen', to: 'unassigned', kind: 'structural', bow: [2.0, -1.6, -3.0] },

    { id: 'q-mm', from: 'queen', to: 'mound', kind: 'authority', bow: [0.6, -1.2, 3.0] },

    { id: 'q-v', from: 'queen', to: 'valid', kind: 'lateral', bow: [0.5, 4.2, -1.6] },
    { id: 'i-v', from: 'intel', to: 'valid', kind: 'lateral', bow: [2.0, 2.6, 2.4] },
    { id: 'i-m', from: 'intel', to: 'memory', kind: 'lateral', bow: [3.2, 1.0, -2.8] },
    { id: 'i-o', from: 'intel', to: 'output', kind: 'lateral', bow: [-2.8, 1.4, 2.0] },
    { id: 'f-m', from: 'forge', to: 'memory', kind: 'lateral', bow: [2.8, -0.6, 2.6] },
    { id: 'f-o', from: 'forge', to: 'output', kind: 'lateral', bow: [-1.2, 3.4, -2.2] },
    { id: 'v-o', from: 'valid', to: 'output', kind: 'lateral', bow: [0.8, -3.6, -2.4] },
    { id: 'm-o', from: 'memory', to: 'output', kind: 'lateral', bow: [-0.6, -3.8, 2.2] }
  ];

  /* ── Shaders ───────────────────────────────────────────────────────────────
     Both pairs are the reference's, unchanged. The sprite mask is a hard
     alpha-0.5 discard rather than a soft blend: it is what makes a record read
     as a distinct dot instead of dissolving into the bloom of its neighbours,
     and it is the reason a chamber holding twelve records looks like twelve
     things and not like a smudge. */
  var POINT_VS = [
    'attribute vec3 acolor; attribute float size; attribute float alpha; attribute vec3 aOrg;',
    'uniform float uScale; uniform float uAlpha; uniform float uRec; uniform float uOrg;',
    'varying vec3 vC; varying float vA;',
    'void main(){',
    '  vC = acolor; vA = alpha * uAlpha;',
    '  vec4 mv = modelViewMatrix * vec4(mix(position, aOrg, uOrg), 1.0);',
    '  gl_PointSize = clamp(size * uRec * uScale * (300.0 / max(1.0, -mv.z)), 2.0, 12.0);',
    '  gl_Position = projectionMatrix * mv;',
    '}'
  ].join('\n');
  var POINT_FS = [
    'uniform sampler2D uMap; varying vec3 vC; varying float vA;',
    'void main(){ if(vA < 0.02) discard; if(texture2D(uMap, gl_PointCoord).a < 0.5) discard;',
    '  gl_FragColor = vec4(vC, clamp(vA, 0.0, 1.0)); }'
  ].join('\n');

  var CONDUIT_VS = [
    'attribute float aT; attribute float aS; attribute float aB;',
    'uniform float uHead; uniform float uActive; uniform float uRest; uniform float uScale; uniform float uSharp;',
    'uniform vec3 uFrom; uniform vec3 uTo;',
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
  var CONDUIT_FS = [
    'uniform sampler2D uMap; varying float vA; varying vec3 vC;',
    'void main(){ if(vA < 0.02) discard; if(texture2D(uMap, gl_PointCoord).a < 0.5) discard;',
    '  gl_FragColor = vec4(vC, clamp(vA, 0.0, 1.0)); }'
  ].join('\n');

  /* ── Determinism ───────────────────────────────────────────────────────────
     `Math.random` appears nowhere in this feature and a guard enforces that.
     Two deterministic sources, and the difference between them matters:

       rngFrom(seed) — an ITERATED stream, for structure keyed to a sector or a
         root id. Correct here because the whole sequence is consumed in one
         pass and never re-indexed.

       hash32(id)    — a mix, for anything keyed to a RECORD id. Records arrive
         over time and their index changes; seating them by a stream would
         reshuffle the chamber every time a new record landed. `.115` used an
         LCG STEP indexed by point number, which is not a hash at all — the
         azimuth advanced linearly and the "sphere" came out as visible spiral
         arcs. A mix mixes; a step observed one point at a time does not. */
  function rngFrom(seed) {
    var s = (seed | 0) || 1;
    return function () { s = (s * 1664525 + 1013904223) & 0x7fffffff; return s / 0x7fffffff; };
  }
  function seedOf(str) {
    var h = 2166136261, s = String(str == null ? '' : str);
    for (var i = 0; i < s.length; i++) h = (h ^ s.charCodeAt(i)) * 16777619 & 0x7fffffff;
    return h;
  }
  function unit(id, salt) {
    var h = 2166136261, s = salt + ':' + String(id == null ? '' : id);
    for (var i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = (h * 16777619) >>> 0; }
    h = Math.imul(h ^ (h >>> 16), 2246822507) >>> 0;
    h = Math.imul(h ^ (h >>> 13), 3266489909) >>> 0;
    return ((h ^ (h >>> 16)) >>> 0) / 4294967296;
  }

  /* Verification is a fact the evidence table answered, and it is what decides
     how bright and how deep a record sits: a signed outcome is durable and sits
     near the core, a refusal drifts to the shell. Nothing here is a taxonomy
     this file invented — every value is one `/colony/live/records` returns. */
  var VERIF_TINT = {
    verified: 0.95, refused: 0.35, not_recorded: 0.6, not_scanned: 0.6, not_applicable: 0.6
  };

  var BG = 0x04060b;

  function create() {
    var TH = T();
    var root = null, renderer = null, scene = null, camera = null, overlay = null;
    var raf = 0, ro = null, destroyed = false, dead = false, contextLost = false;
    var listeners = {};
    var opts = { motion: 'normal', labels: 'normal', trails: true, quality: 'auto' };
    var reduced = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

    var chambers = {};        // sectorId -> chamber object
    var conduits = {};        // rootId  -> conduit object
    var conduitList = [];
    var current = null;       // last scene from the topology
    var layout = {};          // sectorId -> [x,y,z] operator overrides
    var restyle = {};         // sectorId -> shell colour override
    var playedTransitions = Object.create(null);
    var flights = [];         // finite, event-backed waves in progress
    var TEX = null;

    /* NO AUTHORITY SEAL. The reference parks a lock sprite at 0.42 along the
       Queen→Micromound conduit. It is a badge on a line, not a control, and it
       sat in the middle of the frame implying the mound was locked to
       interaction when the opposite is true: that chamber is the most
       interactive thing in the scene. The authority relationship is already said
       by the conduit itself — it is the only edge in the colony with its own
       kind — so the badge was carrying no fact the picture did not already
       carry, and was actively misread. */

    function emit(name, payload) { (listeners[name] || []).forEach(function (fn) { fn(payload); }); }
    function on(name, fn) { (listeners[name] = listeners[name] || []).push(fn); }
    function animating() { return !reduced && opts.motion !== 'off'; }
    function clamp(v, a, b) { return Math.max(a, Math.min(b, v)); }
    function fin(v) { return typeof v === 'number' && isFinite(v); }
    function finV(v) { return !!v && fin(v.x) && fin(v.y) && fin(v.z); }

    /* ── THE CAMERA RIG ──────────────────────────────────────────────────────
       Spherical: theta yaws, phi pitches, dist dollies, each easing toward a
       `want`. `phi` is clamped just short of the poles because the lookAt basis
       degenerates there and the view flips. Nothing here animates on its own —
       the rig moves when an operator moves it or a focus call retargets it. */
    var HOME = { target: new TH.Vector3(0, 2.6, 0), dist: 96, theta: 0.42, phi: 1.36 };
    var LIMITS = { phi: [0.06, Math.PI - 0.06], dist: [2.2, 130] };
    var cam = { target: HOME.target.clone(), dist: HOME.dist, theta: HOME.theta, phi: HOME.phi };
    var want = { target: HOME.target.clone(), dist: HOME.dist, theta: HOME.theta, phi: HOME.phi };
    var TAU = Math.PI * 2;

    /* Level, in the reference's numbering, translated at the edge into the four
       depth names the HUD's breadcrumb has always used. */
    var state = { level: 0, focus: null, cluster: null, record: null, ant: null };
    var DEPTH = ['survey', 'approach', 'inside', 'cluster', 'cluster'];
    var lastDepth = 'survey', lastFocus = null;

    function sane() {
      if (!fin(want.theta)) want.theta = HOME.theta;
      if (!fin(want.phi)) want.phi = HOME.phi;
      if (!fin(want.dist)) want.dist = HOME.dist;
      if (!finV(want.target)) want.target.copy(HOME.target);
      want.phi = clamp(want.phi, LIMITS.phi[0], LIMITS.phi[1]);
      want.dist = clamp(want.dist, LIMITS.dist[0], LIMITS.dist[1]);
      want.target.x = clamp(want.target.x, -80, 80);
      want.target.y = clamp(want.target.y, -60, 60);
      want.target.z = clamp(want.target.z, -80, 80);
      if (!fin(cam.theta)) cam.theta = want.theta;
      if (!fin(cam.phi)) cam.phi = want.phi;
      if (!fin(cam.dist)) cam.dist = want.dist;
      if (!finV(cam.target)) cam.target.copy(want.target);
    }
    function applyCam() {
      sane();
      var e = reduced ? 1 : 0.08, e2 = reduced ? 1 : 0.075;
      var dth = (want.theta - cam.theta) % TAU;
      if (dth > Math.PI) dth -= TAU; else if (dth < -Math.PI) dth += TAU;
      cam.theta += dth * e;
      cam.phi += (want.phi - cam.phi) * e;
      cam.dist += (want.dist - cam.dist) * e2;
      cam.target.lerp(want.target, e2);
      var sp = Math.sin(cam.phi), cp = Math.cos(cam.phi);
      camera.position.set(
        cam.target.x + cam.dist * sp * Math.sin(cam.theta),
        cam.target.y + cam.dist * cp,
        cam.target.z + cam.dist * sp * Math.cos(cam.theta));
      camera.lookAt(cam.target);
    }
    function pushDepth() {
      var d = DEPTH[Math.min(4, Math.max(0, state.level))] || 'survey';
      // The focus can change without the depth name changing — clicking from one
      // chamber straight to another stays at `approach` — and the breadcrumb has
      // to follow the focus, so both are compared.
      if (d === lastDepth && state.focus === lastFocus) return;
      lastDepth = d; lastFocus = state.focus;
      emit('depth', { depth: d, focus: state.focus });
    }

    /* ── Textures. Generated once, no external assets (img-src stays 'self'). ── */
    function radialTex(stops, size) {
      var c = document.createElement('canvas'); c.width = c.height = size || 128;
      var g = c.getContext('2d');
      var grd = g.createRadialGradient(c.width / 2, c.width / 2, 0, c.width / 2, c.width / 2, c.width / 2);
      stops.forEach(function (s) { grd.addColorStop(s[0], s[1]); });
      g.fillStyle = grd; g.fillRect(0, 0, c.width, c.width);
      var t = new TH.CanvasTexture(c); t.needsUpdate = true; return t;
    }
    function antTex() {
      var c = document.createElement('canvas'); c.width = c.height = 64;
      var g = c.getContext('2d');
      g.fillStyle = 'rgba(245,238,226,.95)';
      g.beginPath(); g.ellipse(32, 26, 6, 9, 0, 0, 7); g.fill();
      g.beginPath(); g.ellipse(32, 40, 8, 11, 0, 0, 7); g.fill();
      g.beginPath(); g.arc(32, 15, 5, 0, 7); g.fill();
      g.strokeStyle = 'rgba(245,238,226,.75)'; g.lineWidth = 2.4; g.lineCap = 'round';
      [[-1, -6], [-1, 4], [1, -6], [1, 4]].forEach(function (p) {
        g.beginPath(); g.moveTo(32 + p[0] * 5, 30 + p[1]); g.lineTo(32 + p[0] * 17, 30 + p[1] - 4); g.stroke();
      });
      var t = new TH.CanvasTexture(c); t.needsUpdate = true; return t;
    }
    function buildTextures() {
      return {
        /* record grain: a solid disc with a hairline soft edge, so records read
           as distinct dots rather than as overlapping bloom */
        dot: radialTex([[0, 'rgba(255,255,255,1)'], [0.9, 'rgba(255,255,255,1)'],
                        [0.98, 'rgba(255,255,255,1)'], [1, 'rgba(255,255,255,0)']], 128),
        glow: radialTex([[0, 'rgba(255,255,255,1)'], [0.3, 'rgba(255,255,255,.92)'],
                         [0.46, 'rgba(255,255,255,.34)'], [0.72, 'rgba(255,255,255,.06)'],
                         [1, 'rgba(255,255,255,0)']], 256),
        grain: radialTex([[0, 'rgba(255,255,255,1)'], [0.92, 'rgba(255,255,255,1)'],
                          [0.99, 'rgba(255,255,255,1)'], [1, 'rgba(255,255,255,0)']], 128),
        /* chamber halo: a smooth power falloff with NO defined rim — bright at
           the core, dissolving into the volume rather than ending on a circle.
           A sprite with a hard edge reads as a bubble, which is the one
           silhouette this view has always refused to draw. */
        halo: (function () {
          var S = 256, c = document.createElement('canvas'); c.width = c.height = S;
          var x = c.getContext('2d');
          var g = x.createRadialGradient(S / 2, S / 2, 0, S / 2, S / 2, S / 2);
          for (var i = 0; i <= 24; i++) {
            var t = i / 24;
            g.addColorStop(t, 'rgba(255,255,255,' + (Math.pow(1 - t, 2.2) * 0.5).toFixed(4) + ')');
          }
          x.fillStyle = g; x.fillRect(0, 0, S, S);
          var tex = new TH.CanvasTexture(c); tex.needsUpdate = true; return tex;
        })(),
        ant: antTex()
      };
    }

    /* ── Build ─────────────────────────────────────────────────────────────── */
    function mount(container) {
      TH = T();
      if (!TH) throw new Error('three.js is not loaded');

      root = document.createElement('div');
      root.className = 'colony-webgl';
      root.style.cssText = 'position:absolute;inset:0;overflow:hidden;background:#04060b';
      container.appendChild(root);

      renderer = new TH.WebGLRenderer({ antialias: true, alpha: false, powerPreference: 'high-performance' });
      renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
      renderer.setClearColor(BG, 1);
      renderer.domElement.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;display:block;';
      root.appendChild(renderer.domElement);

      overlay = document.createElement('div');
      overlay.style.cssText = 'position:absolute;inset:0;pointer-events:none;overflow:hidden;';
      root.appendChild(overlay);

      scene = new TH.Scene();
      /* No fog. The reference has none, and the volume is empty black precisely
         so nothing competes with the chambers and their conduits — a fog term
         at this world scale ate the far side of the crystal. */
      camera = new TH.PerspectiveCamera(42, 1, 0.5, 400);

      TEX = buildTextures();
      buildLabelPools();

      renderer.domElement.addEventListener('webglcontextlost', onContextLost, false);
      renderer.domElement.addEventListener('webglcontextrestored', onContextRestored, false);
      bindInput();

      ro = new ResizeObserver(resize); ro.observe(root);
      resize();
      raf = requestAnimationFrame(frame);
      return true;
    }

    function resize() {
      if (!root || !renderer) return;
      var w = root.clientWidth || 1, h = root.clientHeight || 1;
      renderer.setSize(w, h, false);
      camera.aspect = w / h; camera.updateProjectionMatrix();
      /* Frame the whole spread with margin: the crystal is wide, so a narrow
         viewport pulls the home distance back rather than cropping it. */
      var fit = 62 * Math.max(1, 1.75 / camera.aspect);
      HOME.dist = fit;
      if (!state.focus) { want.dist = fit; want.target.copy(HOME.target); cam.dist = cam.dist || fit; }
    }

    function onContextLost(e) {
      // Preventing default is what makes restoration possible at all; without it
      // the context is gone permanently and the view is black until reload.
      e.preventDefault();
      contextLost = true;
      emit('context', { lost: true });
    }
    function onContextRestored() {
      contextLost = false;
      // Rebuild from the model, not from whatever was on screen — the GPU
      // objects are gone and the authoritative state is the scene we were given.
      if (current) setScene(current);
      emit('context', { lost: false });
    }

    /* ── Chamber geometry ────────────────────────────────────────────────────
       CLUSTERS ARE REAL AND SO ARE THEIR SEATS. The reference invents nine named
       context clusters per chamber and lays them on a golden-angle lattice. The
       lattice is geometry and is ported exactly; the clusters are whatever kinds
       of record this chamber actually holds, in a stable order, so a chamber
       with three kinds of record has three strata and a chamber with none has
       no geometry at all rather than an empty scaffold. */
    function clusterSeats(sec, lk) {
      var order = (sec.clusters || []).slice().sort(function (a, b) {
        return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
      });
      var n = order.length, golden = Math.PI * (3 - Math.sqrt(5));
      return order.map(function (cl, i) {
        var y = 1 - (i / Math.max(1, n - 1)) * 1.55;
        var rad = Math.sqrt(Math.max(0.05, 1 - y * y));
        var th = golden * i;
        var shellFrac = 0.42 + unit(cl.id, 'shell') * 0.36;
        return {
          id: cl.id, label: cl.label || cl.id, records: cl.records || [],
          center: [Math.cos(th) * rad * lk.r * shellFrac,
                   y * lk.r * shellFrac * 0.85,
                   Math.sin(th) * rad * lk.r * shellFrac],
          org: [0, 0, 0]
        };
      });
    }

    /** How durable a record is, from the two facts the colony actually recorded. */
    function durabilityOf(rec, trailOf) {
      var verified = rec.verification === 'verified';
      var pher = trailOf(rec.ant);
      var durable = (verified ? 0.55 : 0.1) + pher * 0.45;
      return { depth: 1 - Math.min(0.94, durable), pher: pher, tint: VERIF_TINT[rec.verification] || 0.6 };
    }

    function buildChamber(sec, index) {
      var lk = look(sec.id);
      var shellHex = restyle[sec.id] || lk.shell;
      var coreHex = restyle[sec.id] ? '#' + new TH.Color(shellHex).lerp(new TH.Color(0xffffff), 0.42).getHexString() : lk.core;
      var nucHex = restyle[sec.id] ? '#' + new TH.Color(shellHex).multiplyScalar(0.6).getHexString() : lk.nucleus;
      var pos = layout[sec.id] || home(sec.id, index);
      var grp = new TH.Group();
      grp.position.set(pos[0], pos[1], pos[2]);
      grp.userData.sectorId = sec.id;

      // The trail the colony recorded for whichever unit authored a record. A
      // role with no trail is NULL, not zero — nothing has run, which is not the
      // same as having run and failed.
      var trails = Object.create(null);
      (sec.residents || []).forEach(function (r) {
        var s = r.trail && fin(r.trail.strength) ? r.trail.strength : 0;
        if (r.roleId) trails[String(r.roleId).toLowerCase()] = s;
        (r.workers || []).forEach(function (w) { if (w) trails[String(w).toLowerCase()] = s; });
      });
      function trailOf(ant) {
        var k = String(ant || '').toLowerCase();
        return Object.prototype.hasOwnProperty.call(trails, k) ? clamp(trails[k], 0, 1) : 0;
      }

      var seats = clusterSeats(sec, lk);
      var recs = [];
      seats.forEach(function (cl) { cl.records.forEach(function (r) { recs.push(r); }); });
      var total = recs.length;   /* ONE PARTICLE PER PERSISTED RECORD, NOTHING ELSE. */

      var posA = new Float32Array(total * 3), colA = new Float32Array(total * 3);
      var sizA = new Float32Array(total), alpA = new Float32Array(total);
      var orgA = new Float32Array(total * 3);
      var shell = new TH.Color(shellHex), core = new TH.Color(coreHex), c = new TH.Color();

      // Record seats: inside their own cluster, at a depth their durability
      // decides. Direction comes from a hash of the record id, so a record keeps
      // its seat as the chamber fills up around it.
      var w = 0;
      seats.forEach(function (cl) {
        cl.records.forEach(function (rec) {
          var d = durabilityOf(rec, trailOf);
          var dir = [unit(rec.recordId, 'dx') * 2 - 1, unit(rec.recordId, 'dy') * 2 - 1, unit(rec.recordId, 'dz') * 2 - 1];
          var len = Math.hypot(dir[0], dir[1], dir[2]) || 1;
          var spread = lk.r * 0.16, k = 0.5 + d.depth * 0.75;
          var px = cl.center[0] * k + (dir[0] / len) * spread;
          var py = cl.center[1] * k + (dir[1] / len) * spread;
          var pz = cl.center[2] * k + (dir[2] / len) * spread;
          rec.pos = [px, py, pz];
          rec._i = w;

          var rad = Math.min(1, Math.hypot(px, py, pz) / lk.r);
          // Soft edge: the outermost records thin out rather than stacking on a rim.
          var edge = 1 - 0.72 * Math.pow(rad, 2.6);
          posA[w * 3] = px; posA[w * 3 + 1] = py; posA[w * 3 + 2] = pz;
          c.copy(shell).lerp(core, Math.min(1, Math.pow(1 - rad, 1.5) * 1.15)).multiplyScalar(0.5 + d.tint * 0.45);
          colA[w * 3] = c.r; colA[w * 3 + 1] = c.g; colA[w * 3 + 2] = c.b;
          alpA[w] = Math.min(1, 0.82 + d.pher * 0.2) * (0.86 + 0.14 * edge);
          sizA[w] = (1.15 + d.pher * 1.7) * (0.72 + 0.28 * edge);
          w++;
        });
      });

      /* THE ORDERED FORMATION. Focusing a chamber cross-fades its records out of
         the cloud into STRATA — one level per cluster, records laid on an even
         golden-angle spiral within it. The strata are real groupings: a cluster
         is a kind of record the colony actually wrote. */
      var C = Math.max(1, seats.length);
      w = 0;
      seats.forEach(function (cl, ci) {
        var m = Math.max(1, cl.records.length);
        var y = ((ci + 0.5) / C - 0.5) * lk.r * 1.55;
        var band = Math.sqrt(Math.max(0.12, 1 - Math.pow(y / (lk.r * 1.05), 2)));
        cl.org = [0, y, 0];
        cl.records.forEach(function (rec, k) {
          var ang = k * 2.399963;
          var rad = lk.r * 0.86 * band * Math.sqrt((k + 0.55) / m);
          var ox = Math.cos(ang) * rad, oz = Math.sin(ang) * rad;
          rec.org = [ox, y, oz];
          orgA[w * 3] = ox; orgA[w * 3 + 1] = y; orgA[w * 3 + 2] = oz; w++;
        });
      });

      var geo = new TH.BufferGeometry();
      geo.setAttribute('position', new TH.BufferAttribute(posA, 3));
      geo.setAttribute('aOrg', new TH.BufferAttribute(orgA, 3));
      geo.setAttribute('acolor', new TH.BufferAttribute(colA, 3));
      geo.setAttribute('size', new TH.BufferAttribute(sizA, 1));
      geo.setAttribute('alpha', new TH.BufferAttribute(alpA, 1));
      var mat = new TH.ShaderMaterial({
        uniforms: {
          uMap: { value: TEX.dot }, uScale: { value: 1 }, uAlpha: { value: 1 },
          uRec: { value: 1 }, uOrg: { value: 0 }
        },
        vertexShader: POINT_VS, fragmentShader: POINT_FS,
        transparent: true, depthWrite: false, blending: TH.AdditiveBlending
      });
      var points = new TH.Points(geo, mat);
      points.userData.sectorId = sec.id;
      grp.add(points);

      /* No membrane mesh: a sphere shell blooms at grazing angles and reads as a
         hard outline. The chamber boundary is implied by particle density and by
         the core light alone.

         AND EXACTLY ONE HALO PER CHAMBER. The reference constructs a second,
         wider `glow` sprite at 2.8r and then never adds it to the group — it
         survives only as a colour handle for the restyle path. Read quickly that
         looks like an oversight, so this port added it, and the result was the
         failure the design handoff names by symptom: "nebula-like coloured wash
         filling a quadrant". Two overlapping halos per chamber, nine chambers,
         additive over black, and the whole centre of the frame turns to fog with
         the record grains lost inside it. There is one halo, and this is it. */

      /* THE NUCLEUS TAKES THE DEEP HUE, NOT THE HIGHLIGHT, and it is the reason
         a chamber is visible at all when it holds nothing. This is LIGHT, not
         data: it says a chamber is here and how big it is, exactly as its
         position and colour do, and it says nothing about how much it holds. */
      var nucleus = new TH.Sprite(new TH.SpriteMaterial({
        map: TEX.halo, color: new TH.Color(nucHex), transparent: true, opacity: 0.8,
        depthWrite: false, blending: TH.AdditiveBlending, fog: false
      }));
      nucleus.scale.setScalar(lk.r * 4.2); grp.add(nucleus);

      scene.add(grp);

      var ch = {
        id: sec.id, sec: sec, look: lk, r: lk.r, grp: grp, points: points, mat: mat,
        nucleus: nucleus, seats: seats, records: recs, pos: pos,
        shellHex: shellHex, coreHex: coreHex, crew: [], inner: null
      };
      buildCrew(ch, sec, trailOf);
      buildInner(ch);
      return ch;
    }

    /* ── Residents: real roles and their real workers ────────────────────────
       Two concentric rings — registry roles inside, their workers outside and
       offset half a step — and the Queen holds the centre of her own chamber.
       An ant's home cluster is wherever it filed the most records, which is a
       count over real rows and not a preference this file assigned. */
    function buildCrew(ch, sec, trailOf) {
      var lk = ch.look, residents = sec.residents || [];
      var roster = [];
      residents.forEach(function (r) {
        roster.push({ name: r.name || r.roleId, matchId: r.roleId, roleId: r.roleId, lead: true, res: r });
      });
      /* A WORKER IS LABELLED WITH ITS NAME AND MATCHED ON ITS ID, and those are two
         different strings. `ant_name` on an event is `constraint.scope_guard`, so
         that is what a record can be joined on; "ScopeGuard" is what the registry
         calls the ant and what the 2D colony view has always shown. Printing the id
         gave one ant two names in one product, which is what an operator saw under
         every worker orb. */
      residents.forEach(function (r) {
        (r.workers || []).forEach(function (w) {
          roster.push({
            name: w.name || w.id, matchId: w.id, roleId: w.parent || r.roleId,
            lead: false, res: r, enabled: w.enabled !== false
          });
        });
      });
      if (!roster.length) return;

      var byAnt = Object.create(null);
      ch.records.forEach(function (r) { (byAnt[String(r.ant || '').toLowerCase()] = byAnt[String(r.ant || '').toLowerCase()] || []).push(r); });

      var nLead = residents.length;
      var nWork = roster.length - nLead;
      var queenAt = -1;
      roster.forEach(function (e, i) {
        if (queenAt < 0 && sec.id === 'queen' && e.lead && String(e.roleId).toLowerCase() === 'queen') queenAt = i;
      });

      // Where each role sits on the inner ring, and how many workers hang off it —
      // both needed before any seat is computed, so a worker can be placed under
      // its own parent rather than at its index in a flat roster.
      var leadSeat = Object.create(null), workerCount = Object.create(null), workerSeen = Object.create(null);
      roster.forEach(function (e, i) {
        if (e.lead) leadSeat[e.roleId] = queenAt >= 0 && i > queenAt ? i - 1 : i;
        else workerCount[e.roleId] = (workerCount[e.roleId] || 0) + 1;
      });

      roster.forEach(function (entry, i) {
        var rs = byAnt[String(entry.matchId).toLowerCase()] || [];
        var isQueen = i === queenAt;
        var isLead = entry.lead;

        var tally = Object.create(null);
        rs.forEach(function (r) { tally[r.cluster] = (tally[r.cluster] || 0) + 1; });
        var homeLabel = Object.keys(tally).sort(function (a, b) { return tally[b] - tally[a]; })[0];
        var homeIdx = Math.max(0, ch.seats.findIndex(function (s) { return s.id === homeLabel; }));
        var seat = ch.seats[homeIdx] || null;

        /* TWO RINGS, AND A WORKER SITS UNDER ITS OWN ROLE.
           The reference spreads workers evenly around the outer ring by their index
           in the roster, which is fine when the roster is a flat list of names and
           wrong here: `scope_guard` belongs to `constraint`, the registry says so,
           and an outer ring in roster order puts it on the far side of the chamber
           from the ant it reports to. Each worker is seated in the arc directly
           outside its parent, so the shape of the chamber IS the shape of the
           roster before a single link is drawn. */
        var leadRingN = Math.max(1, queenAt >= 0 ? nLead - 1 : nLead);
        var ringI, ang;
        if (isLead) {
          ringI = queenAt >= 0 && i > queenAt ? i - 1 : i;
          ang = (ringI / leadRingN) * Math.PI * 2;
        } else {
          var pIdx = leadSeat[entry.roleId];
          var band = Math.PI * 2 / leadRingN;
          var mine = (workerCount[entry.roleId] || 1);
          var kth = workerSeen[entry.roleId] || 0;
          workerSeen[entry.roleId] = kth + 1;
          ringI = i - nLead;
          // Centred on the parent's spoke, spanning 80% of its share of the ring so
          // two adjacent roles' workers never interleave.
          ang = (pIdx === undefined ? (ringI / Math.max(1, nWork)) * Math.PI * 2
                                    : (pIdx / leadRingN) * Math.PI * 2)
              + ((kth + 0.5) / mine - 0.5) * band * 0.8;
        }
        var rr = lk.r * (isLead ? (lk.child ? 0.4 : 0.44) : (lk.child ? 0.82 : 0.86));
        var slot = isQueen
          ? new TH.Vector3(0, 0, 0)
          : new TH.Vector3(Math.cos(ang) * rr, (ringI % 2 ? 1 : -1) * lk.r * (isLead ? 0.08 : 0.14), Math.sin(ang) * rr);

        var status = entry.lead ? (entry.res.status || 'idle')
                   : (entry.enabled === false ? 'disabled' : (entry.res.status || 'idle'));
        var orbColor = new TH.Color(isQueen ? TOKENS.gold : status === 'disabled' ? '#4a5160' : isLead ? ch.coreHex : ch.shellHex);
        var sp = new TH.Sprite(new TH.SpriteMaterial({
          map: TEX.glow, color: orbColor, transparent: true, opacity: 0,
          depthWrite: false, blending: TH.AdditiveBlending, fog: false
        }));
        sp.position.copy(slot); ch.grp.add(sp);
        var coreSp = new TH.Sprite(new TH.SpriteMaterial({
          map: TEX.dot, color: new TH.Color(orbColor).lerp(new TH.Color(0xffffff), 0.55),
          transparent: true, opacity: 0, depthWrite: false, blending: TH.AdditiveBlending, fog: false
        }));
        sp.add(coreSp); coreSp.position.set(0, 0, 0.001); coreSp.scale.setScalar(0.34);
        var ring = null;
        if (isQueen) {
          ring = new TH.Sprite(new TH.SpriteMaterial({
            map: TEX.glow, color: new TH.Color(TOKENS.queen), transparent: true, opacity: 0,
            depthWrite: false, blending: TH.AdditiveBlending, fog: false
          }));
          sp.add(ring); ring.position.set(0, 0, -0.001); ring.scale.setScalar(2.1);
        }

        var verified = rs.filter(function (r) { return r.verification === 'verified'; }).length;
        var refused = rs.filter(function (r) { return r.verification === 'refused'; }).length;
        var latest = rs.slice().sort(function (a, b) { return a.createdAt < b.createdAt ? 1 : -1; })[0];

        /* THE PHEROMONE TRAIL, ON THE ANT IT BELONGS TO. `pheromone_trails` keys
           strength to `worker:{id}`; the projection sums it per role. A role
           whose workers have never run has NO trail, which is not a strength of
           zero — so `null` reads as 0 for brightness but the inspector prints the
           two differently, and this never invents a floor. */
        var pher = entry.res.trail && fin(entry.res.trail.strength)
                 ? clamp(entry.res.trail.strength, 0, 1) : 0;

        ch.crew.push({
          sp: sp, core: coreSp, ring: ring, slot: slot, color: orbColor,
          isQueen: isQueen, isLead: isLead, name: entry.name, matchId: entry.matchId,
          parentRoleId: entry.lead ? null : entry.roleId, resident: entry.res,
          status: status, records: rs, homeSeat: seat, pher: pher,
          /* The ant inspector's payload. Every field is a count over real rows or
             a value the registry and the pheromone layer own — there is no
             `tasks_completed` here, because nothing in this model counts that. */
          info: {
            name: entry.name, id: entry.matchId, roleId: entry.roleId, sector: sec.id,
            parent: entry.lead ? '' : entry.roleId,
            sectorLabel: sec.label || sec.id,
            rank: isQueen ? 'colony authority' : isLead ? 'registry role' : 'worker',
            status: status,
            home_cluster: seat ? seat.label : '—',
            records: rs.length,
            clusters_served: Object.keys(tally).length,
            verified: verified, refused: refused,
            trail: entry.res.trail || null,
            last_record: latest ? latest.title : '—',
            last_ts: latest ? latest.createdAt : '—',
            color: '#' + orbColor.getHexString()
          }
        });
      });
    }

    /* ── Intra-chamber linkage ───────────────────────────────────────────────
       FOUR RELATIONSHIPS, AND EVERY SEGMENT IS A ROW THAT EXISTS. The reference
       draws two; the other two are relationships this colony records and the
       reference's sample data has no equivalent of, so they were never in it:

         cluster → its records   a record belongs to its cluster because that is
                                 the event type it has.
         cluster → next cluster  the chamber's own context ring.
         ant → its records       `record.ant` is whichever unit actually ran. This
                                 is the answer to "who wrote what in here", drawn
                                 rather than read out of a table, and it is why a
                                 busy worker's orb visibly owns a region of the
                                 cloud.
         worker → its role       `AntWorkerDefinition.ParentRoleId`, carried by the
                                 projection. `scope_guard` reports to `constraint`
                                 and the registry is what says so — this file does
                                 not split an id on a dot to find out.
         record → record         consecutive records sharing a `mission_id`, in
                                 recorded order. A mission's own thread through
                                 this chamber. Records with no mission, and
                                 missions with a single record here, contribute
                                 no segment — a thread of one is not a thread.

       Nothing here is generated to fill space: a chamber whose records name no
       ant and no mission draws the first two families and nothing else. All four
       start at opacity 0 and grow legible as the camera closes in. */
    function buildInner(ch) {
      var segs = [], cc = [], au = [], ms = [];

      ch.seats.forEach(function (cl, i) {
        cl.records.forEach(function (r) {
          segs.push(cl.center[0], cl.center[1], cl.center[2], r.pos[0], r.pos[1], r.pos[2]);
        });
        var nx = ch.seats[(i + 1) % ch.seats.length];
        if (nx && ch.seats.length > 1) {
          cc.push(cl.center[0], cl.center[1], cl.center[2], nx.center[0], nx.center[1], nx.center[2]);
        }
      });

      // ant → the records it authored. The orb's seat is fixed, so only the
      // record end moves with the ordered-strata blend.
      var slotOf = Object.create(null);
      ch.crew.forEach(function (a) { slotOf[String(a.name).toLowerCase()] = a.slot; });
      var authored = [];
      ch.records.forEach(function (r) {
        var slot = slotOf[String(r.ant || '').toLowerCase()];
        if (!slot) return;                       // an ant this chamber does not host
        authored.push({ slot: slot, rec: r });
        au.push(slot.x, slot.y, slot.z, r.pos[0], r.pos[1], r.pos[2]);
      });

      // record → record along a mission, in recorded order.
      var byMission = Object.create(null);
      ch.records.forEach(function (r) {
        if (!r.missionId) return;
        (byMission[r.missionId] = byMission[r.missionId] || []).push(r);
      });
      var threads = [];
      Object.keys(byMission).forEach(function (mid) {
        var list = byMission[mid];
        if (list.length < 2) return;
        list.sort(function (a, b) { return a.createdAt < b.createdAt ? -1 : a.createdAt > b.createdAt ? 1 : 0; });
        for (var i = 1; i < list.length; i++) {
          threads.push([list[i - 1], list[i]]);
          ms.push(list[i - 1].pos[0], list[i - 1].pos[1], list[i - 1].pos[2],
                  list[i].pos[0], list[i].pos[1], list[i].pos[2]);
        }
      });

      // worker → the role it reports to. Both seats are fixed, so this geometry is
      // written once and never reflowed.
      var chain = [];
      var seatOf = Object.create(null);
      ch.crew.forEach(function (a) { if (a.isLead) seatOf[a.matchId] = a.slot; });
      ch.crew.forEach(function (a) {
        if (a.isLead || !a.parentRoleId) return;
        var pslot = seatOf[a.parentRoleId];
        if (!pslot) return;                       // a parent this chamber does not host
        chain.push(a.slot.x, a.slot.y, a.slot.z, pslot.x, pslot.y, pslot.z);
      });

      function lines(arr, hex) {
        var g = new TH.BufferGeometry();
        g.setAttribute('position', new TH.BufferAttribute(new Float32Array(arr), 3));
        var m = new TH.LineBasicMaterial({
          color: new TH.Color(hex), transparent: true, opacity: 0,
          depthWrite: false, blending: TH.AdditiveBlending
        });
        ch.grp.add(new TH.LineSegments(g, m));
        return { g: g, m: m };
      }
      var ring = lines(cc, ch.shellHex);
      var mission = lines(ms, TOKENS.gold);
      var chainL = lines(chain, ch.shellHex);
      var author = lines(au, ch.coreHex);
      var spoke = lines(segs, ch.coreHex);

      ch.inner = {
        g: spoke.g, m: spoke.m, g2: ring.g, m2: ring.m,
        gA: author.g, mA: author.m, authored: authored,
        gM: mission.g, mM: mission.m, threads: threads,
        mC: chainL.m
      };
    }

    /** Rewrite every link's endpoints for the current cloud→ordered blend. */
    function reflowLinks(ch) {
      var ic = ch.inner; if (!ic) return;
      var t2 = ch.mat.uniforms.uOrg.value;
      function mixv(p, o, k) { return p[k] + ((o ? o[k] : p[k]) - p[k]) * t2; }

      var at = ic.g.attributes.position.array, w = 0;
      ch.seats.forEach(function (cl) {
        cl.records.forEach(function (rec) {
          at[w++] = mixv(cl.center, cl.org, 0); at[w++] = mixv(cl.center, cl.org, 1); at[w++] = mixv(cl.center, cl.org, 2);
          at[w++] = mixv(rec.pos, rec.org, 0); at[w++] = mixv(rec.pos, rec.org, 1); at[w++] = mixv(rec.pos, rec.org, 2);
        });
      });
      ic.g.attributes.position.needsUpdate = true;

      if (ch.seats.length > 1) {
        var at2 = ic.g2.attributes.position.array, w2 = 0;
        ch.seats.forEach(function (cl, i) {
          var nx = ch.seats[(i + 1) % ch.seats.length];
          at2[w2++] = mixv(cl.center, cl.org, 0); at2[w2++] = mixv(cl.center, cl.org, 1); at2[w2++] = mixv(cl.center, cl.org, 2);
          at2[w2++] = mixv(nx.center, nx.org, 0); at2[w2++] = mixv(nx.center, nx.org, 1); at2[w2++] = mixv(nx.center, nx.org, 2);
        });
        ic.g2.attributes.position.needsUpdate = true;
      }

      // The ant's seat does not move; only its records do.
      var atA = ic.gA.attributes.position.array, wA = 0;
      ic.authored.forEach(function (e) {
        atA[wA++] = e.slot.x; atA[wA++] = e.slot.y; atA[wA++] = e.slot.z;
        atA[wA++] = mixv(e.rec.pos, e.rec.org, 0); atA[wA++] = mixv(e.rec.pos, e.rec.org, 1); atA[wA++] = mixv(e.rec.pos, e.rec.org, 2);
      });
      ic.gA.attributes.position.needsUpdate = true;

      var atM = ic.gM.attributes.position.array, wM = 0;
      ic.threads.forEach(function (pairR) {
        atM[wM++] = mixv(pairR[0].pos, pairR[0].org, 0); atM[wM++] = mixv(pairR[0].pos, pairR[0].org, 1); atM[wM++] = mixv(pairR[0].pos, pairR[0].org, 2);
        atM[wM++] = mixv(pairR[1].pos, pairR[1].org, 0); atM[wM++] = mixv(pairR[1].pos, pairR[1].org, 1); atM[wM++] = mixv(pairR[1].pos, pairR[1].org, 2);
      });
      ic.gM.attributes.position.needsUpdate = true;
    }

    /* ── Conduits ────────────────────────────────────────────────────────────
       A near-direct run: two control points, one gentle consistent bow.
       Degenerate or non-finite geometry falls back to a straight two-point curve
       rather than feeding three.js a curve whose arc-length search cannot
       converge — a hang inside the sampler is a black view with no error. */
    function curveFor(rt) {
      var a = chambers[rt.from], b = chambers[rt.to];
      if (!a || !b) return null;
      var A = a.grp.position.clone(), B = b.grp.position.clone();
      function straight() {
        var c0 = new TH.CatmullRomCurve3([A.clone(), A.clone().lerp(B, 0.5), B.clone()]);
        c0.curveType = 'catmullrom'; c0.tension = 0.5; return c0;
      }
      if (!finV(A) || !finV(B)) return straight();
      var dir = B.clone().sub(A), len = dir.length();
      if (!fin(len) || len < 1e-3) return straight();
      var p0 = A.clone().add(dir.clone().multiplyScalar(a.r / len * 0.35));
      var p3 = B.clone().sub(dir.clone().multiplyScalar(b.r / len * 0.35));
      var axis = p3.clone().sub(p0), span = axis.length();
      if (!finV(p0) || !finV(p3) || !fin(span) || span < 0.25) return straight();
      var u = axis.clone().normalize();
      var n1 = new TH.Vector3(0, 1, 0);
      if (Math.abs(u.dot(n1)) > 0.9) n1.set(1, 0, 0);
      n1.crossVectors(u, n1).normalize();
      var n2 = new TH.Vector3().crossVectors(u, n1).normalize();
      var rnd = rngFrom(seedOf(rt.id) + 7);
      var lean = (rnd() * 2 - 1) * span * 0.035;
      var lift = (rnd() * 2 - 1) * span * 0.025;
      var raw = rt.bow || [0, 0, 0];
      var bow = new TH.Vector3(fin(raw[0]) ? raw[0] : 0, fin(raw[1]) ? raw[1] : 0, fin(raw[2]) ? raw[2] : 0).multiplyScalar(0.18);
      var pts = [p0.clone()];
      for (var i = 1; i <= 2; i++) {
        var t = i / 3, env = Math.sin(Math.PI * t);
        var p = p0.clone().lerp(p3, t)
          .addScaledVector(n1, env * lean)
          .addScaledVector(n2, env * lift)
          .addScaledVector(bow, env);
        p.y -= env * span * 0.012;
        if (!finV(p)) return straight();
        pts.push(p);
      }
      pts.push(p3.clone());
      var c = new TH.CatmullRomCurve3(pts);
      c.curveType = 'catmullrom'; c.tension = 0.5;
      // One sanity check: a curve that cannot report a finite midpoint is
      // replaced outright, never handed to the sampler.
      if (!finV(c.getPoint(0.5))) return straight();
      return c;
    }

    /* GRAIN COUNT IS THE ONE PORTED CONSTANT THIS CONSOLE DELIBERATELY LOWERS.
       The reference runs 60 grains on a structural conduit, 24 on a lateral and 40
       on the authority root — over its sixteen roots, about 650 points in flight.
       This colony has eighteen roots, not sixteen, and its chambers hold far fewer
       record grains than the reference's generated ones, so the streams stopped
       being the supporting texture and became the loudest thing in the frame.

       Cut to roughly 60% of the reference. The stream still reads as a chain of
       distinct dots — which is what the hard sprite-mask discard is for — and the
       chambers get the eye back. Every other conduit constant (radius, rest,
       sharpness, drift speed, taper) is the reference's, so the LOOK of a stream
       is unchanged; there is simply less of it.

       `ThePortedConstants_StillAgreeWithTheVendoredReference` knows about this:
       it is listed as a named divergence with both values pinned, so lowering it
       further, or the reference changing underneath, both fail loudly. */
    function conduitSpec(rt) {
      var lateral = rt.kind === 'lateral', auth = rt.kind === 'authority';
      return {
        n: auth ? 24 : lateral ? 15 : 36,
        streams: auth ? 2 : lateral ? 1 : 2,
        rad: auth ? 0.44 : lateral ? 0.5 : 0.8,
        rest: auth ? 0.3 : lateral ? 0.14 : 0.32,
        sharp: auth ? 150 : 120
      };
    }
    function conduitGeo(n) {
      var g = new TH.BufferGeometry();
      g.setAttribute('position', new TH.BufferAttribute(new Float32Array(n * 3), 3));
      g.setAttribute('aT', new TH.BufferAttribute(new Float32Array(n), 1));
      g.setAttribute('aS', new TH.BufferAttribute(new Float32Array(n), 1));
      g.setAttribute('aB', new TH.BufferAttribute(new Float32Array(n), 1));
      return g;
    }

    /* Particle placement in PATH SPACE. Uniform-parameter sampling with a
       rotation-minimising frame: no arc-length cache, no binary search, nothing
       that can fail to converge. Each particle keeps its own t, radius, angle
       and drift speed, so a recorded transition can travel the conduit without
       re-solving the curve. */
    function fillConduit(co) {
      var curve = co.curve, spec = co.spec, N = 64;
      var samp = new Float32Array((N + 1) * 3), nrm = new Float32Array((N + 1) * 3), bnm = new Float32Array((N + 1) * 3);
      var P = new TH.Vector3(), Tn = new TH.Vector3(), Nv = new TH.Vector3(), Bv = new TH.Vector3(), tmp = new TH.Vector3();
      Nv.set(0, 1, 0);
      for (var i = 0; i <= N; i++) {
        var t = i / N;
        curve.getPoint(t, P);
        curve.getPoint(Math.min(1, t + 1 / N), tmp);
        Tn.copy(tmp).sub(P);
        if (Tn.lengthSq() < 1e-9) { curve.getPoint(Math.max(0, t - 1 / N), tmp); Tn.copy(P).sub(tmp); }
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
      co.path = { N: N, samp: samp, nrm: nrm, bnm: bnm };
      // A fixed, generous bounding sphere: the stream never leaves the corridor,
      // so per-frame drift never needs to recompute bounds.
      var cA = new TH.Vector3(samp[0], samp[1], samp[2]);
      var cB = new TH.Vector3(samp[N * 3], samp[N * 3 + 1], samp[N * 3 + 2]);
      co.geo.boundingSphere = new TH.Sphere(cA.clone().lerp(cB, 0.5), cA.distanceTo(cB) * 0.6 + spec.rad * 6 + 12);
      co.points.frustumCulled = true;

      if (!co.p) {
        var rnd = rngFrom(co.seed + 77);
        var per = Math.ceil(spec.n / spec.streams);
        var p = {
          t: new Float32Array(spec.n), base: new Float32Array(spec.n), jit: new Float32Array(spec.n),
          a0: new Float32Array(spec.n), tw: new Float32Array(spec.n), sp: new Float32Array(spec.n),
          b0: new Float32Array(spec.n)
        };
        var aS = co.geo.attributes.aS.array;
        for (var j = 0; j < spec.n; j++) {
          var stream = j % spec.streams, step = Math.floor(j / spec.streams);
          var primary = stream === 0, syn = primary && step % 9 === 4;
          p.t[j] = Math.min(0.998, Math.max(0.002, (step + 0.15 + rnd() * 0.7) / per));
          p.base[j] = primary ? spec.rad * (0.04 + rnd() * 0.26) : spec.rad * (0.5 + stream * 0.26 + rnd() * 0.26);
          p.jit[j] = 0.35 + rnd() * 0.9;
          p.a0[j] = stream * 2.2 + rnd() * 0.6;
          p.tw[j] = primary ? 1.1 : 3.2;
          p.sp[j] = (0.072 + rnd() * 0.038) * (primary ? 1 : 0.9);
          p.b0[j] = (primary ? 0.75 + rnd() * 0.5 : 0.4 + rnd() * 0.3) * (syn ? 1.9 : 1);
          aS[j] = (primary ? 1.1 + rnd() * 0.8 : 0.65 + rnd() * 0.6) * (syn ? 1.7 : 1);
        }
        co.geo.attributes.aS.needsUpdate = true;
        co.p = p;
      }
      driftConduit(co, 0);
    }

    /* Advance the stream along the path; dt = 0 just re-places it. Called every
       third frame with `dt * 3`, as the reference does, so a grain crosses a
       conduit in roughly 9–14 seconds. This is AMBIENT motion: it says the
       passage is there and the view is live. Nothing about it claims a task —
       what claims a task is `conduitState`, below, and every input to that has a
       row behind it. Frozen entirely when motion is off. */
    function driftConduit(co, dt) {
      var spec = co.spec, p = co.p, path = co.path, N = path.N;
      var pos = co.geo.attributes.position.array;
      var aT = co.geo.attributes.aT.array, aB = co.geo.attributes.aB.array;
      for (var i = 0; i < spec.n; i++) {
        var t = p.t[i] + p.sp[i] * dt;
        if (t > 1) t -= 1;
        p.t[i] = t;
        var ft = t * N, i0 = Math.min(N - 1, Math.floor(ft)), fr = ft - i0, i1 = i0 + 1;
        var x = path.samp[i0 * 3] + (path.samp[i1 * 3] - path.samp[i0 * 3]) * fr;
        var y = path.samp[i0 * 3 + 1] + (path.samp[i1 * 3 + 1] - path.samp[i0 * 3 + 1]) * fr;
        var z = path.samp[i0 * 3 + 2] + (path.samp[i1 * 3 + 2] - path.samp[i0 * 3 + 2]) * fr;
        var nx = path.nrm[i0 * 3], ny = path.nrm[i0 * 3 + 1], nz = path.nrm[i0 * 3 + 2];
        var bx = path.bnm[i0 * 3], by = path.bnm[i0 * 3 + 1], bz = path.bnm[i0 * 3 + 2];
        var env = Math.sin(Math.PI * t);
        // Converge to the axis at both ends: a straight run into the chamber
        // centre, no splay across the shell.
        var rr = p.base[i] * (0.06 + 0.94 * Math.pow(env, 0.5));
        var ang = p.a0[i] + t * p.tw[i];
        var ca = Math.cos(ang) * rr, sa = Math.sin(ang) * rr;
        pos[i * 3] = x + nx * ca + bx * sa;
        pos[i * 3 + 1] = y + ny * ca + by * sa;
        pos[i * 3 + 2] = z + nz * ca + bz * sa;
        aT[i] = t;
        aB[i] = p.b0[i] * (0.32 + 0.68 * Math.pow(env, 0.3));
      }
      co.geo.attributes.position.needsUpdate = true;
      co.geo.attributes.aT.needsUpdate = true;
      co.geo.attributes.aB.needsUpdate = true;
    }

    function buildConduit(rt) {
      var curve = curveFor(rt);
      if (!curve) return null;
      var from = chambers[rt.from], to = chambers[rt.to];
      var spec = conduitSpec(rt);
      // Each end carries its own chamber's colour, so a grain crossing the run
      // reads as leaving one chamber and arriving at the other.
      var cFrom = new TH.Color(from.shellHex).lerp(new TH.Color(0xdfe8f5), 0.3);
      var cTo = new TH.Color(to.shellHex).lerp(new TH.Color(0xdfe8f5), 0.3);
      var geo = conduitGeo(spec.n);
      var mat = new TH.ShaderMaterial({
        uniforms: {
          uMap: { value: TEX.grain }, uHead: { value: -1 }, uActive: { value: 0 },
          uRest: { value: spec.rest }, uScale: { value: 1 }, uSharp: { value: spec.sharp },
          uFrom: { value: cFrom }, uTo: { value: cTo }
        },
        vertexShader: CONDUIT_VS, fragmentShader: CONDUIT_FS,
        transparent: true, depthWrite: false, blending: TH.AdditiveBlending
      });
      var points = new TH.Points(geo, mat); scene.add(points);
      var co = {
        rt: rt, id: rt.id, from: rt.from, to: rt.to, kind: rt.kind,
        curve: curve, spec: spec, seed: seedOf(rt.id), geo: geo, mat: mat, points: points,
        head: -1, active: 0, trail: 0, crossings: 0, busy: false
      };
      fillConduit(co);
      return co;
    }

    function rebuildConduits() {
      conduitList.forEach(function (c) { scene.remove(c.points); c.geo.dispose(); c.mat.dispose(); });
      conduits = {}; conduitList = [];
      ROOTS.forEach(function (rt) {
        var c = buildConduit(rt);
        if (c) { conduits[rt.id] = c; conduitList.push(c); }
      });
      // Mission routes come from persisted task edges, and only those. They are
      // drawn with the structural spec but keyed separately so a route the
      // colony really recorded is never confused with the permanent web.
      ((current && current.edges) || []).forEach(function (e, i) {
        var id = 'msn-' + e.from + '-' + e.to;
        if (conduits[id] || !chambers[e.from] || !chambers[e.to]) return;
        var c = buildConduit({ id: id, from: e.from, to: e.to, kind: 'structural', bow: [0, 1.4 + i * 0.3, 0] });
        if (c) { c.mission = true; conduits[id] = c; conduitList.push(c); }
      });
    }

    /* ── What may brighten a conduit, and nothing else ───────────────────────
       Two facts, computed from the scene the topology handed in. Both are
       recomputed on every scene, so a conduit's brightness is never carried over
       from a state that has since ended.

       TRAIL — the pheromone layer, read at the level a conduit can honestly show
       it. `pheromone_trails` keys strength to `worker:{id}`, so an EDGE has no
       row of its own; what an edge does have is how many recorded transitions
       have crossed it, and reinforcement-by-use is exactly what a trail is.
       Normalised against the busiest route so the scale means something in a
       colony of any size, and gated by the operator's `trails` preference. The
       per-worker strength is not thrown away — it lights the ant orbs instead,
       where it belongs.

       BUSY — a task is running at one end of a route the colony actually
       persisted (`/graph`'s `depends_on` edges, projected to sector pairs). It
       raises the whole line. It does NOT sweep a head: a status says work is
       happening, not where along a line it has got to, and drawing a position
       from a status is the invented-progress defect this release removed. */
    function conduitState(sc) {
      var pair = function (a, b) { return a < b ? a + '|' + b : b + '|' + a; };

      var crossings = Object.create(null), most = 0;
      (sc.transitions || []).forEach(function (tr) {
        if (!tr.from || !tr.to || tr.from === tr.to) return;
        var k = pair(tr.from, tr.to);
        crossings[k] = (crossings[k] || 0) + 1;
        if (crossings[k] > most) most = crossings[k];
      });

      var running = Object.create(null);
      (sc.sectors || []).forEach(function (x) {
        if ((x.runningTasks || []).length) running[x.id] = true;
      });
      var routed = Object.create(null);
      (sc.edges || []).forEach(function (e) { routed[pair(e.from, e.to)] = true; });

      conduitList.forEach(function (co) {
        var k = pair(co.from, co.to);
        var n = crossings[k] || 0;
        co.crossings = n;
        co.trail = most > 0 ? n / most : 0;
        co.busy = !!(routed[k] && (running[co.from] || running[co.to]));
      });
    }

    /* ── Finite, event-backed transitions ──────────────────────────────────── */
    function startFlights(sc) {
      // A historical frame is a STILL. Playing a past transition would both
      // animate something that is not happening and burn its one-shot id, so the
      // same transition would never play when the view returns to LIVE.
      if (sc.meta && sc.meta.history) return;

      (sc.transitions || []).forEach(function (tr) {
        if (playedTransitions[tr.id]) return;   // once per unique event id, ever
        playedTransitions[tr.id] = true;
        if (!animating()) return;
        if (!tr.from || !chambers[tr.to] || !chambers[tr.from]) return;

        // The conduit this transition actually crossed, in either direction. No
        // recorded source, or no conduit between those chambers, means there is
        // nothing to travel — an arrival is not a journey.
        var co = null, reverse = false;
        conduitList.forEach(function (c) {
          if (co) return;
          if (c.from === tr.from && c.to === tr.to) co = c;
          else if (c.from === tr.to && c.to === tr.from) { co = c; reverse = true; }
        });
        if (!co) return;
        flights.push({ co: co, reverse: reverse, t: 0, life: 1400 });
      });
    }

    /* ── Scene intake ─────────────────────────────────────────────────────── */
    function setScene(sc) {
      current = sc;
      if (!scene) return;

      var wanted = {};
      (sc.sectors || []).forEach(function (s, i) {
        wanted[s.id] = true;
        if (chambers[s.id]) { scene.remove(chambers[s.id].grp); disposeGroup(chambers[s.id].grp); }
        chambers[s.id] = buildChamber(s, i);
      });
      Object.keys(chambers).forEach(function (id) {
        if (wanted[id]) return;
        scene.remove(chambers[id].grp); disposeGroup(chambers[id].grp); delete chambers[id];
      });

      rebuildConduits();
      conduitState(sc);
      syncSectorLabels();
      startFlights(sc);
      emit('scene', sc);
    }

    function disposeGroup(g) {
      g.traverse(function (o) {
        if (o.geometry) o.geometry.dispose();
        if (o.material) o.material.dispose();
      });
    }

    /* ── Screen-space picking ────────────────────────────────────────────────
       No raycaster. A Points cloud is raycast against a world-space threshold,
       which is the wrong unit for a target an operator aims at with a cursor:
       the same threshold is a huge target up close and a sub-pixel one at survey
       distance, which is why chambers used to feel unresponsive. Projecting the
       candidates and measuring in PIXELS makes every target the size it looks. */
    var projV = new TH.Vector3(), tmpV = new TH.Vector3();
    function project(p) {
      projV.copy(p).project(camera);
      var w = root.clientWidth || 1, h = root.clientHeight || 1;
      return { x: (projV.x * 0.5 + 0.5) * w, y: (-projV.y * 0.5 + 0.5) * h, z: projV.z, w: w, h: h };
    }
    /** A record or cluster's live seat: cloud, ordered, or the blend on screen. */
    function livePos(ch, node, out) {
      var t2 = ch.mat.uniforms.uOrg.value, p = node.pos || node.center, o = node.org || p;
      return out.set(
        p[0] + (o[0] - p[0]) * t2 + ch.grp.position.x,
        p[1] + (o[1] - p[1]) * t2 + ch.grp.position.y,
        p[2] + (o[2] - p[2]) * t2 + ch.grp.position.z);
    }

    function hitTest(e) {
      var rect = renderer.domElement.getBoundingClientRect();
      var mx = e.clientX - rect.left, my = e.clientY - rect.top;

      if (state.focus && chambers[state.focus]) {
        var ch = chambers[state.focus];
        var ba = null, bad = 32;
        ch.crew.forEach(function (a) {
          var p = project(tmpV.copy(a.sp.position).add(ch.grp.position));
          if (p.z > 1) return;
          var d = Math.hypot(p.x - mx, p.y - my);
          if (d < bad) { bad = d; ba = { kind: 'resident', data: a.resident, ant: a, sectorId: ch.id }; }
        });
        if (ba) return ba;

        if (state.level >= 2) {
          var best = null, bd = 14;
          ch.records.forEach(function (r) {
            if (state.level >= 3 && state.cluster && r.cluster !== state.cluster.id) return;
            var p = project(livePos(ch, r, new TH.Vector3()));
            var d = Math.hypot(p.x - mx, p.y - my);
            if (d < bd) { bd = d; best = { kind: 'record', data: r, sectorId: ch.id }; }
          });
          if (best) return best;
          if (state.level === 2) {
            var bc = null, bcd = 26;
            ch.seats.forEach(function (cl) {
              var p = project(livePos(ch, cl, new TH.Vector3()));
              var d = Math.hypot(p.x - mx, p.y - my);
              if (d < bcd) { bcd = d; bc = { kind: 'cluster', data: cl, sectorId: ch.id }; }
            });
            if (bc) return bc;
          }
        }
      }

      var hit = null, hz = Infinity;
      Object.keys(chambers).forEach(function (id) {
        var c = chambers[id];
        var p = project(c.grp.position);
        var edge = project(tmpV.copy(c.grp.position).add(new TH.Vector3(c.r, 0, 0)));
        var rad = Math.max(12, Math.abs(edge.x - p.x));
        if (Math.hypot(p.x - mx, p.y - my) < rad && p.z < hz) { hz = p.z; hit = { kind: 'sector', sectorId: id }; }
      });
      return hit;
    }

    /* ── Input ─────────────────────────────────────────────────────────────── */
    var drag = null;
    function bindInput() {
      var el = renderer.domElement;
      el.style.touchAction = 'none';
      el.style.cursor = 'grab';
      el.addEventListener('pointerdown', onDown);
      el.addEventListener('pointermove', onMove);
      window.addEventListener('pointerup', onUp);
      el.addEventListener('wheel', onWheel, { passive: false });
      el.addEventListener('dblclick', onDouble);
    }
    /* THE GESTURE TABLE, and why moving a chamber needs a modifier. Plain drag
       ORBITS — the gesture an operator reaches for first in any 3D view.
       Rearranging is rarer and more consequential (it persists to /ui/state), so
       it costs a modifier. */
    function onDown(e) {
      var pre = hitTest(e);
      var grab = (e.altKey || e.metaKey) && pre && pre.kind === 'sector' ? pre.sectorId : null;
      drag = { x: e.clientX, y: e.clientY, moved: 0, move: grab, pan: !grab && (e.shiftKey || e.button === 1), hit: pre };
      try { renderer.domElement.setPointerCapture(e.pointerId); } catch (err) { }
      renderer.domElement.style.cursor = grab ? 'move' : 'grabbing';
    }
    function onMove(e) {
      if (!drag) {
        var hit = hitTest(e);
        renderer.domElement.style.cursor =
          ((e.altKey || e.metaKey) && hit && hit.kind === 'sector') ? 'move' : (hit ? 'pointer' : 'grab');
        emit('hover', hit);
        return;
      }
      var dx = e.clientX - drag.x, dy = e.clientY - drag.y;
      drag.moved += Math.abs(dx) + Math.abs(dy);
      if (drag.move) moveSector(drag.move, dx, dy, e.shiftKey);
      else if (drag.pan) {
        var k = cam.dist * 0.0016;
        var right = new TH.Vector3().subVectors(camera.position, cam.target).cross(new TH.Vector3(0, 1, 0)).normalize();
        want.target.add(right.multiplyScalar(-dx * k)).add(new TH.Vector3(0, dy * k, 0));
      } else {
        want.theta -= dx * 0.005;
        want.phi = clamp(want.phi - dy * 0.004, LIMITS.phi[0], LIMITS.phi[1]);
      }
      drag.x = e.clientX; drag.y = e.clientY;
    }
    function onUp() {
      if (!drag) return;
      var moved = drag.moved > 6, moving = drag.move, hit = drag.hit;
      drag = null;
      renderer.domElement.style.cursor = 'grab';

      if (moving) { reflow(moving); if (moved) emit('layout', getLayout()); return; }
      if (moved) return;                       // an orbit or a pan is not a click

      if (!hit) {
        // Empty space clears the selection and pulls back out, which is the
        // gesture people try when they feel lost.
        state.ant = null; state.record = null; state.cluster = null;
        if (state.focus || state.level) {
          state.focus = null; state.level = 0;
          want.dist = HOME.dist; want.theta = HOME.theta; want.phi = HOME.phi;
          want.target.copy(HOME.target);
          pushDepth();
        }
        emit('sector', { sectorId: null });
        return;
      }
      if (hit.kind === 'sector') {
        /* CLICKING A CHAMBER GOES TO IT, and a second click on the chamber you
           are already focused on enters it. */
        if (state.focus === hit.sectorId && state.level <= 1) enter(hit.sectorId);
        else focus(hit.sectorId);
        emit('sector', { sectorId: hit.sectorId });
      } else if (hit.kind === 'resident') {
        state.ant = hit.ant; emit('resident', hit);
      } else if (hit.kind === 'record') {
        state.ant = null; state.record = hit.data; state.level = 4; pushDepth(); emit('record', hit);
      } else if (hit.kind === 'cluster') {
        state.ant = null; state.cluster = hit.data; state.level = 3; pushDepth(); emit('cluster', hit);
      }
    }
    function onWheel(e) {
      e.preventDefault();
      // Exponential, so one notch feels the same at every distance.
      var f = Math.exp(e.deltaY * 0.0014);
      var ch = state.focus ? chambers[state.focus] : null;
      var minD = ch ? ch.r * 1.45 : 18;
      want.dist = clamp(want.dist * f, minD, LIMITS.dist[1]);
      if (ch) {
        var lvl = want.dist < ch.r * 2.35 ? 2 : want.dist < ch.r * 4.8 ? 1 : 0;
        if (lvl === 0) { state.focus = null; state.cluster = null; state.record = null; want.target.copy(HOME.target); }
        if (lvl < state.level && state.level > 2) state.level = 2;
        if (state.level < 3 || lvl === 0) state.level = lvl;
        pushDepth();
      }
    }
    function onDouble(e) {
      var hit = hitTest(e);
      if (hit && hit.kind === 'sector') focus(hit.sectorId); else survey();
    }

    function moveSector(id, dx, dy, depthDrag) {
      var ch = chambers[id]; if (!ch) return;
      var d = camera.position.distanceTo(ch.grp.position);
      var wpp = 2 * Math.tan(42 * Math.PI / 360) * d / (root.clientHeight || 700);
      var right = new TH.Vector3().setFromMatrixColumn(camera.matrixWorld, 0);
      var up = new TH.Vector3().setFromMatrixColumn(camera.matrixWorld, 1);
      var fwd = new TH.Vector3().setFromMatrixColumn(camera.matrixWorld, 2).negate();
      var mv = right.multiplyScalar(dx * wpp).add(up.multiplyScalar(-dy * wpp));
      if (depthDrag) mv.copy(fwd.multiplyScalar(-dy * wpp * 1.4));
      ch.grp.position.set(
        clamp(ch.grp.position.x + mv.x, -60, 60),
        clamp(ch.grp.position.y + mv.y, -46, 46),
        clamp(ch.grp.position.z + mv.z, -60, 60));
      layout[id] = [ch.grp.position.x, ch.grp.position.y, ch.grp.position.z];
    }
    function reflow(id) {
      conduitList.forEach(function (co) {
        if (co.from !== id && co.to !== id) return;
        var c = curveFor(co.rt || { id: co.id, from: co.from, to: co.to, kind: co.kind, bow: [0, 0, 0] });
        if (c) { co.curve = c; fillConduit(co); }
      });
    }

    /* ── Semantic zoom ─────────────────────────────────────────────────────── */
    function survey() {
      state.focus = null; state.cluster = null; state.record = null; state.ant = null; state.level = 0;
      want.dist = HOME.dist; want.target.copy(HOME.target);
      // Yaw and pitch are the OPERATOR's. Survey re-frames the colony; it does
      // not spin the view back to a canonical angle they did not ask for.
      pushDepth();
    }
    function focus(id) {
      var ch = chambers[id]; if (!ch) return;
      state.focus = id; state.cluster = null; state.record = null; state.ant = null; state.level = 1;
      want.target.copy(ch.grp.position);
      want.dist = clamp(ch.r * 3.4, LIMITS.dist[0], LIMITS.dist[1]);
      pushDepth();
    }
    function enter(id) {
      var ch = chambers[id || state.focus]; if (!ch) return;
      state.focus = ch.id; state.level = 2;
      want.target.copy(ch.grp.position);
      want.dist = clamp(ch.r * 1.8, LIMITS.dist[0], LIMITS.dist[1]);
      pushDepth();
    }

    /* ── §15 MICROMOUND DESCENT ──────────────────────────────────────────────
       The camera leaves the colony along the AUTHORITY conduit — the same line
       the mound hangs from, and the only edge here that represents a grant rather
       than a dependency. Travelling it is the point: a physical device acts
       because the Queen chartered it.

       Refuses when there is no mound chamber. A descent into a device the colony
       has not enrolled would be a camera move into empty space presented as
       arrival at a machine. */
    var descentTimer = 0;
    function descend() {
      var mound = chambers.mound, queen = chambers.queen;
      if (!mound) return false;
      if (queen) {
        want.target.copy(queen.grp.position);
        want.dist = clamp(queen.r * 3.0, LIMITS.dist[0], LIMITS.dist[1]);
      }
      var arrive = function () {
        if (destroyed || !chambers.mound) return;
        state.focus = 'mound'; state.level = 2;
        want.target.copy(chambers.mound.grp.position);
        want.dist = clamp(chambers.mound.r * 1.8, LIMITS.dist[0], LIMITS.dist[1]);
        pushDepth();
        emit('descend', { moundChamber: true });
      };
      if (reduced || !queen) arrive();
      else { clearTimeout(descentTimer); descentTimer = setTimeout(arrive, 620); }
      return true;
    }

    /* ── Restyle: colour, never identity ─────────────────────────────────────
       One colour drives the chamber's whole palette. A chamber's NAME is not
       offered: the label is the registry's `Colony` value projected by the
       server, and a console that let an operator rename it would produce a view
       that disagrees with every other page in the console. */
    function setChamberStyle(id, cfg) {
      if (!cfg || !cfg.color || !chambers[id]) return false;
      restyle[id] = cfg.color;
      if (current) setScene(current);
      return true;
    }

    /* ── Labels ──────────────────────────────────────────────────────────────
       Bounded DOM pools, never one node per record: a chamber can hold hundreds
       and the cost of labelling them all is not worth a wall of text. Placement
       is chrome-aware — an element marked `data-chrome-avoid` is a panel the HUD
       has put over the canvas, and a label under one is a label nobody reads. */
    var sectorLabels = {}, clusterLabels = [], crewLabels = [], recordLabels = [], moveHint = null;
    function mkLabel(css) {
      var d = document.createElement('div');
      d.style.cssText = css;
      overlay.appendChild(d);
      return d;
    }
    function buildLabelPools() {
      var i;
      for (i = 0; i < 12; i++) clusterLabels.push(mkLabel(
        'position:absolute;transform:translate(-50%,-50%);font:500 9.5px/1.3 ui-monospace,monospace;'
        + 'letter-spacing:.1em;color:rgba(201,207,220,.82);white-space:nowrap;text-shadow:0 0 12px #000;'
        + 'opacity:0;transition:opacity .3s;'));
      for (i = 0; i < 24; i++) crewLabels.push(mkLabel(
        'position:absolute;transform:translate(12px,-50%);font:500 9px/1.3 ui-monospace,monospace;'
        + 'letter-spacing:.08em;white-space:nowrap;text-shadow:0 0 10px #000,0 0 3px #000;'
        + 'opacity:0;transition:opacity .3s;'));
      for (i = 0; i < 16; i++) recordLabels.push(mkLabel(
        'position:absolute;transform:translate(10px,-50%);font:400 9.5px/1.3 ui-monospace,monospace;'
        + 'color:rgba(244,233,214,.9);white-space:nowrap;text-shadow:0 0 10px #000;opacity:0;'
        + 'transition:opacity .25s;padding-left:6px;border-left:1px solid rgba(255,63,164,.45);'));
      moveHint = mkLabel(
        'position:absolute;left:14px;bottom:12px;font:500 8.5px/1 ui-monospace,monospace;'
        + 'letter-spacing:.14em;color:rgba(139,147,168,.55);');
      moveHint.textContent = 'ALT-DRAG A CHAMBER TO REPOSITION · SHIFT TO PUSH IN DEPTH';
    }
    function syncSectorLabels() {
      Object.keys(sectorLabels).forEach(function (id) {
        if (chambers[id]) return;
        overlay.removeChild(sectorLabels[id]); delete sectorLabels[id];
      });
      Object.keys(chambers).forEach(function (id) {
        var ch = chambers[id];
        if (!sectorLabels[id]) sectorLabels[id] = mkLabel(
          'position:absolute;transform:translate(-50%,-50%);font:600 11px/1.35 ui-monospace,monospace;'
          + 'letter-spacing:.16em;white-space:pre;text-align:center;text-shadow:0 0 18px rgba(0,0,0,.9);'
          + 'transition:opacity .35s;');
        sectorLabels[id].textContent = (ch.sec.label || id)
          + (ch.records.length ? '\n' + ch.records.length : '');
        sectorLabels[id].style.color = ch.shellHex === TOKENS.queenDeep ? TOKENS.queen : ch.shellHex;
      });
    }

    var avoidRects = [], avoidClock = 0;
    function refreshAvoid(nowMs) {
      if (nowMs - avoidClock < 400) return;
      avoidClock = nowMs;
      var cr = renderer.domElement.getBoundingClientRect();
      avoidRects = [];
      Array.prototype.forEach.call(document.querySelectorAll('[data-chrome-avoid]'), function (el) {
        var b = el.getBoundingClientRect();
        // A hidden panel measures 0×0 at the origin, and a zero rect kept in this
        // list would push every label away from the top-left corner for a panel
        // that is not on screen.
        if (b.width < 1 || b.height < 1) return;
        avoidRects.push({ x0: b.left - cr.left, x1: b.right - cr.left, y0: b.top - cr.top, y1: b.bottom - cr.top });
      });
    }
    /* Labels placed so far THIS FRAME are blockers too. The reference avoids the
       chrome and nothing else, so two chambers that project near each other print
       their names on top of one another — and when several are pushed off a panel
       they all land on the same fallback line. A label nobody can read is the same
       defect as a label under a panel. */
    var placedRects = [];
    function labelRect(x, y, side) {
      return { x0: side === 1 ? x : x - 132, x1: side === 1 ? x + 132 : x, y0: y - 11, y1: y + 11 };
    }
    function overlaps(a, list, padX, padY) {
      for (var i = 0; i < list.length; i++) {
        var r = list[i];
        if (a.x1 > r.x0 - padX && a.x0 < r.x1 + padX && a.y1 > r.y0 - padY && a.y0 < r.y1 + padY) return true;
      }
      return false;
    }
    function chromeBlocked(x, y, side) {
      var a = labelRect(x, y, side);
      return overlaps(a, avoidRects, 4, 3) || overlaps(a, placedRects, 6, 4);
    }

    function drawLabels(ms) {
      refreshAvoid(ms);
      var off = opts.labels === 'off';
      placedRects.length = 0;

      Object.keys(chambers).forEach(function (id) {
        var ch = chambers[id], d = sectorLabels[id];
        if (!d) return;
        var p = project(ch.grp.position);
        var edge = project(tmpV.copy(ch.grp.position).add(new TH.Vector3(ch.r, 0, 0)));
        var rad = Math.abs(edge.x - p.x);
        var side = ch.look.side === 'right' ? 1 : -1;
        var cands = [
          { x: p.x + side * (rad + 34), y: p.y + (ch.look.child ? rad * 0.4 : 0), s: side },
          { x: p.x - side * (rad + 34), y: p.y + (ch.look.child ? rad * 0.4 : 0), s: -side },
          { x: p.x + side * (rad + 34), y: p.y - rad - 26, s: side },
          { x: p.x - side * (rad + 34), y: p.y - rad - 26, s: -side }
        ];
        /* A candidate has to be ON SCREEN as well as clear of the chrome. The
           reference tests the CHAMBER's projected point and then places the label
           an arm's length to one side of it, so a chamber near the right edge
           gets a label past the frame and the overlay's overflow clips it away —
           the chamber is drawn, lit and unnamed. Homelab sits furthest right in
           this colony and was exactly that case. */
        var onScreen = function (c) {
          var b = labelRect(c.x, c.y, c.s);
          return b.x0 > 6 && b.x1 < p.w - 6 && b.y0 > 30 && b.y1 < p.h - 20;
        };
        var pick = null;
        for (var i = 0; i < cands.length && !pick; i++) {
          if (onScreen(cands[i]) && !chromeBlocked(cands[i].x, cands[i].y, cands[i].s)) pick = cands[i];
        }
        // Nothing clear AND on screen: take the first that is merely on screen
        // before falling through to the stacked fallback below.
        for (var i2 = 0; i2 < cands.length && !pick; i2++) if (onScreen(cands[i2])) pick = cands[i2];
        if (!pick) {
          // Last resort: sit above whatever panel is in the way, and step up in
          // 18px rows so several chambers forced onto the same line do not stack.
          var top = null;
          for (var a = 0; a < avoidRects.length; a++) {
            var rr = avoidRects[a];
            if (p.x > rr.x0 - 150 && p.x < rr.x1 + 150 && (top === null || rr.y0 < top)) top = rr.y0;
          }
          var base = top === null ? p.y : top - 16;
          for (var row = 0; row < 6; row++) {
            var cand = { x: p.x, y: base - row * 18, s: side };
            if (!chromeBlocked(cand.x, cand.y, cand.s)) { pick = cand; break; }
          }
          if (!pick) pick = { x: p.x, y: base, s: side };
        }
        placedRects.push(labelRect(pick.x, pick.y, pick.s));
        d.style.left = pick.x + 'px';
        d.style.top = pick.y + 'px';
        d.style.transform = 'translate(' + (pick.s === 1 ? '0' : '-100%') + ',-50%)';
        d.style.textAlign = pick.s === 1 ? 'left' : 'right';
        var show = !off && (state.level === 0 || state.focus === id || state.level < 2)
          && p.x > -40 && p.x < p.w + 40 && p.y > 30 && p.y < p.h - 20 && p.z < 1;
        d.style.opacity = show ? (state.focus && state.focus !== id ? 0.35 : 1) : 0;
      });

      var host = (!off && state.focus && state.level >= 1) ? chambers[state.focus] : null;
      crewLabels.forEach(function (d, i) {
        var a = host && host.crew[i];
        if (!a) { d.style.opacity = 0; return; }
        var p = project(tmpV.copy(a.sp.position).add(host.grp.position));
        if (d.textContent !== a.name) d.textContent = a.name;
        d.style.color = '#' + a.color.getHexString();
        d.style.left = p.x + 'px'; d.style.top = p.y + 'px';
        // Crew names are drawn to the RIGHT of their orb, so a name whose box lands
        // under an inspector is hidden rather than half-printed beneath it.
        var vis = p.z < 1 && p.x > 4 && p.x < p.w - 92 && p.y > 40 && p.y < p.h - 40
               && !chromeBlocked(p.x + 12, p.y, 1);
        d.style.opacity = vis
          ? (state.ant === a ? 1 : a.status === 'disabled' ? 0.3 : a.isQueen ? 0.95 : a.isLead ? 0.78 : 0.5)
          : 0;
      });

      if (host && state.level >= 2) {
        host.seats.forEach(function (cl, i) {
          var d = clusterLabels[i]; if (!d) return;
          var p = project(livePos(host, cl, tmpV));
          d.textContent = String(cl.label).toUpperCase();
          d.style.left = p.x + 'px'; d.style.top = p.y + 'px';
          var vis = p.x > 4 && p.x < p.w - 4 && p.y > 40 && p.y < p.h - 60;
          var showIt = !off && p.z < 1 && vis && (!state.cluster || state.cluster.id === cl.id);
          d.style.opacity = showIt ? (state.cluster ? 1 : 0.8) : 0;
        });
        for (var j = host.seats.length; j < clusterLabels.length; j++) clusterLabels[j].style.opacity = 0;
        var rl = state.cluster ? state.cluster.records.slice(0, 16) : [];
        recordLabels.forEach(function (d, i) {
          var r = rl[i];
          if (!r) { d.style.opacity = 0; return; }
          var p = project(livePos(host, r, tmpV));
          d.textContent = r.title || r.recordType || r.recordId;
          d.style.left = p.x + 'px'; d.style.top = p.y + 'px';
          d.style.opacity = (p.z < 1 && p.x > 4 && p.x < p.w - 180 && p.y > 40 && p.y < p.h - 40) ? 0.9 : 0;
        });
      } else {
        clusterLabels.forEach(function (d) { d.style.opacity = 0; });
        recordLabels.forEach(function (d) { d.style.opacity = 0; });
      }
      moveHint.style.opacity = off ? 0 : 1;
    }

    /* ── Adaptive quality ────────────────────────────────────────────────────
       A slow rasteriser must never be asked to keep drawing. React to the FIRST
       slow frame, not to a thirty-frame average — by the time an average moves,
       the operator has already watched the view stutter. */
    var perf = { avg: 0, n: 0, level: 0, lastDraw: 0, slack: false, stalled: false };
    function setQuality(level, rms) {
      level = Math.min(2, level);
      if (level <= perf.level) return;
      perf.level = level;
      renderer.setPixelRatio(1);
      var keep = level === 1 ? 0.5 : 0.2;
      conduitList.forEach(function (co) { co.geo.setDrawRange(0, Math.ceil(co.spec.n * keep)); });
      if (level === 2) {
        Object.keys(chambers).forEach(function (id) {
          var ch = chambers[id];
          ch.points.geometry.setDrawRange(0, Math.ceil(ch.records.length * 0.4));
        });
      }
      emit('quality', { level: level, frameMs: Math.round(rms) });
    }
    /** A render that blocks for a second is not a quality problem. */
    function breaker(rms) {
      perf.stalled = true;
      dead = true;
      cancelAnimationFrame(raf);
      emit('stall', { frameMs: Math.round(rms) });
      try { console.warn('[colony-live] frame ' + Math.round(rms) + 'ms → render stopped'); } catch (e) { }
    }

    /* ── Frame ─────────────────────────────────────────────────────────────── */
    var lastMs = 0;
    function frame(ms) {
      if (destroyed || dead) return;
      raf = requestAnimationFrame(frame);
      if (contextLost) return;
      // Pace to ~30fps and give the thread slack after an over-budget render.
      if (ms - perf.lastDraw < (perf.slack ? 90 : 31)) return;
      perf.slack = false; perf.lastDraw = ms;
      var dtSec = Math.min(0.05, lastMs ? (ms - lastMs) / 1000 : 0.016);
      lastMs = ms;
      var k = animating() ? (opts.motion === 'calm' ? 0.5 : 1) : 0;

      applyCam();

      Object.keys(chambers).forEach(function (id) {
        var ch = chambers[id];
        var focused = state.focus === id;
        var busy = (ch.sec.runningTasks || []).length > 0;
        // Emphasis, and the only thing that raises it is a real running task.
        // No pulse term: the reference breathes the core light at 6% on a sine of
        // wall-clock time, and a light that beats is a light that says something
        // is happening. Focus and a running task are the only two things that
        // move this number.
        var nucBase = (id === 'queen' ? 1.1 : 1) + (busy ? 0.22 : 0) - (focused ? 0.1 : 0);
        ch.nucleus.material.opacity += (nucBase - ch.nucleus.material.opacity) * 0.06;

        var dim = state.focus && !focused && state.level >= 2 ? 0.42 : 1;
        ch.mat.uniforms.uAlpha.value += (dim - ch.mat.uniforms.uAlpha.value) * 0.05;
        var inside = focused && state.level >= 2;
        ch.mat.uniforms.uScale.value += ((inside ? 0.8 : 1) - ch.mat.uniforms.uScale.value) * 0.06;
        ch.mat.uniforms.uRec.value += ((inside ? 1.7 : 1) - ch.mat.uniforms.uRec.value) * 0.06;

        // Focusing orders the records; leaving lets them relax back.
        var wantOrg = focused && state.level >= 1 ? 1 : 0;
        var uo = ch.mat.uniforms.uOrg;
        if (Math.abs(wantOrg - uo.value) > 0.0008) {
          uo.value += (wantOrg - uo.value) * (reduced ? 1 : 0.07);
          reflowLinks(ch);
        }

        // Record links are always present; they simply grow legible as you close in.
        var nearLinks = clamp((58 - cam.dist) / 30, 0, 1);
        var w2 = (focused && state.level >= 2) ? 0.36 : (state.focus && !focused) ? 0.03 : 0.06 + nearLinks * 0.16;
        ch.inner.m.opacity += (w2 - ch.inner.m.opacity) * 0.06;
        ch.inner.m2.opacity += (w2 * 0.7 - ch.inner.m2.opacity) * 0.06;
        /* Authorship reads at APPROACH, where the question is "who works here";
           mission threads only INSIDE, where there are enough records on screen
           for a thread through them to mean anything. Both dim with the rest of
           the colony when another chamber has focus. */
        var wA = focused ? (state.level >= 1 ? 0.30 : 0.10) : (state.focus ? 0.02 : 0.04 + nearLinks * 0.08);
        var wM = (focused && state.level >= 2) ? 0.42 : (focused ? 0.06 : 0.0);
        /* The roster chain reads brightest of the four, because it is the one that
           answers "what is this chamber MADE of" — and unlike the other three it is
           true of a chamber that has never recorded anything. */
        var wC = focused ? 0.50 : (state.focus ? 0.05 : 0.10 + nearLinks * 0.18);
        ch.inner.mA.opacity += (wA - ch.inner.mA.opacity) * 0.06;
        ch.inner.mM.opacity += (wM - ch.inner.mM.opacity) * 0.06;
        ch.inner.mC.opacity += (wC - ch.inner.mC.opacity) * 0.06;

        // The crew ramp is its OWN curve and a wider one — (78 - dist)/46, not the
        // links' (58 - dist)/30. Passing the link ramp to both made the ant orbs
        // stay dark until the camera was almost inside the chamber.
        crewFrame(ch, focused, clamp((78 - cam.dist) / 46, 0, 1));
      });

      /* RECORDED TRANSITIONS: one travelling wave per unique event id, and it
         ends. `k` scales it with the motion preference and stops it at zero, so
         a halted view parks the wave rather than losing it. */
      for (var i = flights.length - 1; i >= 0; i--) {
        var f = flights[i];
        f.t += dtSec * 1000 * k;
        var t = Math.min(1, f.t / f.life);
        f.co.head = f.reverse ? 1 - t : t;
        f.co.active = Math.sin(Math.PI * t);
        if (t >= 1) { f.co.head = -1; f.co.active = 0; flights.splice(i, 1); }
      }

      conduitList.forEach(function (co) {
        var u = co.mat.uniforms;
        u.uHead.value = co.head;
        u.uActive.value += (co.active - u.uActive.value) * 0.12;

        /* RESTING BRIGHTNESS — three real terms and no fourth.
             · the kind's own floor (structural / lateral / authority)
             · the pheromone trail: how many transitions this session recorded
               ACROSS THIS ROUTE, normalised against the busiest one. A route the
               colony keeps using glows without anything having to be running on
               it now, which is what a pheromone trail is for.
             · a running task at one end of a persisted mission edge. A status,
               so it raises the whole line and never sweeps a head. */
        var restT = co.spec.rest * (state.level >= 2 ? 0.6 : 1)
                  + (opts.trails ? co.trail * 0.22 : 0)
                  + (co.busy ? 0.2 : 0);
        var mine = !state.focus || co.from === state.focus || co.to === state.focus;
        u.uRest.value += (restT * (mine ? 1 : 0.12) - u.uRest.value) * 0.06;
        u.uScale.value += (1 - u.uScale.value) * 0.1;

        // The stream itself. Every third frame, as the reference paces it.
        if (perf.n % 3 === 0) driftConduit(co, k ? dtSec * 3 : 0);
      });

      drawLabels(ms);

      var rT0 = (window.performance && performance.now) ? performance.now() : Date.now();
      renderer.render(scene, camera);
      var rms = ((window.performance && performance.now) ? performance.now() : Date.now()) - rT0;
      perf.avg = perf.avg ? perf.avg * 0.85 + rms * 0.15 : rms;
      perf.n++;
      if (rms > 1000) { breaker(rms); return; }
      if (rms > 500) setQuality(2, rms);
      else if (rms > 120) setQuality(1, rms);
      else if (perf.avg > 45 && perf.n % 20 === 0) setQuality(perf.level + 1, perf.avg);
      if (rms > 60) perf.slack = true;
    }

    /* Crew orbs hold a constant legible SCREEN size, which is the whole reason
       this is not a world-space scale: an ant three chambers away and one under
       the cursor should be the same target. Position is fixed — an idle ant
       holds station, and the reference's idle bob is the beginning of a story
       about work that is not happening. */
    var TANF = Math.tan(42 * Math.PI / 360);
    function pxScale(d, px) { return 2 * TANF * d / (root.clientHeight || 700) * px; }
    function crewFrame(ch, focused, near) {
      if (!ch.crew.length) return;
      var antA = Math.min(0.9, 0.14 + near * 0.8) * (state.focus && !focused ? 0.35 : 1);
      var dcam = camera.position.distanceTo(ch.grp.position);
      var detail = focused && state.level >= 1;
      ch.crew.forEach(function (a) {
        var sel = state.ant === a;
        var working = a.status === 'working', offline = a.status === 'disabled';
        /* The trail shows up as SIZE and BRIGHTNESS on the ant that earned it, so
           a chamber's most-reinforced workers read first. Bounded to a quarter
           either way: a strong trail is worth noticing, not worth making a weak
           ant invisible, and an ant with no trail at all is still fully drawn. */
        var trail = opts.trails ? a.pher : 0;
        var px = (detail ? 54 : 30) * (a.isQueen ? 1.55 : a.isLead ? 1 : 0.62)
               * (sel ? 1.35 : 1) * (working ? 1.25 : 1) * (0.88 + trail * 0.24);
        a.sp.scale.setScalar(pxScale(dcam, px));
        var base = antA * (sel ? 1.3 : detail ? 1 : 0.85) * (a.isQueen ? 1.2 : 1)
                 * (working ? 1.3 : offline ? 0.35 : 1) * (0.85 + trail * 0.3);
        a.sp.material.opacity += (Math.min(1, base) - a.sp.material.opacity) * 0.08;
        a.core.material.opacity += (Math.min(1, base * 1.15) - a.core.material.opacity) * 0.08;
        if (a.ring) a.ring.material.opacity += (Math.min(0.85, base * 0.7) - a.ring.material.opacity) * 0.08;
      });
    }

    /* ── Layout, for /ui/state ───────────────────────────────────────────────
       SCHEMA 2. `.115` persisted seats in a world fourteen times this size; a
       schema-1 layout replayed here would fling every chamber far outside the
       130-unit dolly limit and leave the operator staring at empty space. An old
       layout is ignored rather than migrated, because the ×14 factor was never
       written down and guessing it back is how a "migration" becomes a fiction. */
    function getLayout() {
      var out = { schema: 2, sectors: {} };
      Object.keys(chambers).forEach(function (id) {
        var p = chambers[id].grp.position;
        out.sectors[id] = [round(p.x), round(p.y), round(p.z)];
      });
      return out;
    }
    function round(n) { return Math.round(n * 100) / 100; }
    function setLayout(l) {
      if (!l || l.schema !== 2 || !l.sectors) return false;
      var ok = false;
      Object.keys(l.sectors).forEach(function (id) {
        var p = l.sectors[id];
        if (!Array.isArray(p) || p.length !== 3) return;
        if (!p.every(function (n) { return fin(n) && Math.abs(n) <= 120; })) return;
        layout[id] = p.slice(); ok = true;
        if (chambers[id]) chambers[id].grp.position.set(p[0], p[1], p[2]);
      });
      if (ok) { Object.keys(chambers).forEach(reflow); }
      return ok;
    }
    function resetLayout() {
      layout = {};
      Object.keys(chambers).forEach(function (id, i) {
        var h = home(id, i);
        chambers[id].grp.position.set(h[0], h[1], h[2]);
      });
      Object.keys(chambers).forEach(reflow);
      emit('layout', getLayout());
    }

    function destroy() {
      destroyed = true;
      cancelAnimationFrame(raf);
      clearTimeout(descentTimer);
      if (ro) ro.disconnect();
      if (renderer) {
        renderer.domElement.removeEventListener('webglcontextlost', onContextLost);
        renderer.domElement.removeEventListener('webglcontextrestored', onContextRestored);
        renderer.domElement.removeEventListener('pointerdown', onDown);
        renderer.domElement.removeEventListener('pointermove', onMove);
        renderer.domElement.removeEventListener('wheel', onWheel);
        renderer.domElement.removeEventListener('dblclick', onDouble);
        renderer.dispose();
      }
      window.removeEventListener('pointerup', onUp);
      Object.keys(chambers).forEach(function (id) { disposeGroup(chambers[id].grp); });
      conduitList.forEach(function (c) { c.geo.dispose(); c.mat.dispose(); });
      chambers = {}; conduits = {}; conduitList = []; flights = [];
      sectorLabels = {}; clusterLabels = []; crewLabels = []; recordLabels = [];
      if (root && root.parentNode) root.parentNode.removeChild(root);
      root = null; scene = null; renderer = null; overlay = null;
    }

    return {
      mount: mount, destroy: destroy, setScene: setScene, on: on,
      setOptions: function (o) {
        opts = Object.assign(opts, o || {});
        reduced = (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches)
               || opts.motion === 'off';
      },
      survey: survey, focus: focus, enter: enter, resetView: survey,
      /* The viewbar's +/- buttons. Same exponential step the wheel uses and the same floor, so a
         button press and a wheel notch are the same gesture — and the same `pushDepth`, so the
         breadcrumb follows a zoom however it was made. */
      zoom: function (f) {
        if (!fin(f) || f <= 0) return;
        var ch = state.focus ? chambers[state.focus] : null;
        var minD = ch ? ch.r * 1.45 : 18;
        want.dist = clamp(want.dist / f, minD, LIMITS.dist[1]);
        if (ch) {
          var lvl = want.dist < ch.r * 2.35 ? 2 : want.dist < ch.r * 4.8 ? 1 : 0;
          if (lvl === 0) { state.focus = null; state.cluster = null; state.record = null; want.target.copy(HOME.target); }
          if (lvl < state.level && state.level > 2) state.level = 2;
          if (state.level < 3 || lvl === 0) state.level = lvl;
        }
        pushDepth();
      },
      descend: descend,
      setChamberStyle: setChamberStyle,
      getLayout: getLayout, setLayout: setLayout, resetLayout: resetLayout,
      depth: function () { return DEPTH[Math.min(4, Math.max(0, state.level))] || 'survey'; },
      focused: function () { return state.focus; },
      /** True when the last scene handed in was a reconstructed frame, not live. */
      historical: function () { return !!(current && current.meta && current.meta.history); },
      /** The sectors this renderer actually built — the HUD offers no control for a chamber that is not there. */
      sectorIds: function () { return Object.keys(chambers); },
      /** The crew inspector payload for the ant currently selected, or null. */
      selectedAnt: function () { return state.ant ? state.ant.info : null; }
    };
  }

  window.ColonyRenderer = { create: create, available: available };
})();
