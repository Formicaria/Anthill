/* ─────────────────────────────────────────────────────────────────────────────
   COLONY LIVE — the WebGL renderer. v0.3.8.115.

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

   ── THE VISUAL RULES, AND WHAT THEY EXCLUDE ────────────────────────────────
   A chamber is a VOLUME OF PARTICLES with a soft nucleus — not a sphere mesh.
   There is deliberately no membrane geometry, no outline pass and no rim shell:
   a hard silhouette turns a cloud of facts into a bubble, and the whole point is
   that a chamber's mass IS its record count.

   A conduit is a soft particle path — not a tube, cylinder, extrusion or mesh.
   Density tapers toward the chambers and dissolves into the background, so
   nothing in the scene has a continuous border.

   Motion is event-backed only. An idle colony is still: idle ants hold position
   and idle conduits hold sparse, stationary particles. Animation time drives
   camera easing, hover, and a finite transition that has already been recorded —
   never a loop that implies work is happening.
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

  /* ── Sector presentation ───────────────────────────────────────────────────
     Colour and relative mass only. Membership is the SERVER's (the registry's
     `Colony`, projected by ColonyLiveProjection); this table never decides which
     ants live where, and a sector id it does not know still renders — in neutral
     grey, at normal mass — because an unknown sector is a real sector this
     table has not been taught about yet. */
  var LOOK = {
    queen:      { color: 0xe21f7b, core: 0xe8b25a, mass: 1.5 },
    intel:      { color: 0x5ec4cf, core: 0x8fd8df, mass: 1.0 },
    forge:      { color: 0xc97a3d, core: 0xe0a06a, mass: 1.0 },
    valid:      { color: 0xc25f6e, core: 0xd98a96, mass: 1.0 },
    memory:     { color: 0xd9b054, core: 0xecd39a, mass: 1.0 },
    output:     { color: 0x8f78c9, core: 0xb3a0e0, mass: 1.0 },
    homelab:    { color: 0x6f8ea8, core: 0x9fb6c9, mass: 0.9 },
    unassigned: { color: 0x6b7280, core: 0x9aa1ab, mass: 0.7 },
    mound:      { color: 0xa55a7e, core: 0xc9cfdc, mass: 0.6 }
  };
  var NEUTRAL = { color: 0x6b7280, core: 0x9aa1ab, mass: 1.0 };
  function look(id) { return LOOK[id] || NEUTRAL; }

  /* Default chamber positions. Deterministic, and only a DEFAULT — an operator's
     layout from /ui/state replaces them, and reset restores exactly these. */
  var HOME = {
    queen: [0, 0, 0], intel: [-260, 120, 40], forge: [230, 110, -30],
    valid: [250, -120, 50], memory: [-40, -230, -40], output: [-250, -110, -70],
    homelab: [40, 240, 60], unassigned: [-420, -40, 120], mound: [0, -60, 320]
  };
  function home(id, i) {
    if (HOME[id]) return HOME[id].slice();
    // An unknown sector still needs a deterministic seat: ring it outside the
    // known ones by index rather than dropping it at the origin on top of Queen.
    var a = (i * 2.39996);
    return [Math.cos(a) * 470, Math.sin(a) * 470, ((i % 3) - 1) * 90];
  }

  var BG = 0x04060b;

  function create() {
    var TH = T();
    var root = null, renderer = null, scene = null, camera = null;
    var raf = 0, ro = null, destroyed = false, contextLost = false;
    var listeners = {};
    var opts = { motion: 'normal', labels: 'normal', trails: true, quality: 'auto' };
    var reduced = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

    var chambers = {};        // sectorId -> { group, points, nucleus, ants, pos, radius, look }
    var conduits = [];        // { from, to, kind, points, geom }
    var labelPool = [], labelHost = null;
    var current = null;       // last scene from the topology
    var layout = {};          // sectorId -> [x,y,z] operator overrides
    var playedTransitions = Object.create(null);
    var flights = [];         // finite, event-backed particle waves in progress

    var camTarget = new TH.Vector3(0, 0, 0), camWant = new TH.Vector3(0, 0, 900);
    var focusId = null, depth = 'survey';
    var descentTimer = 0;          // §15: the one staged camera move; cleared on destroy
    var drag = null, hover = null;
    var ray = new TH.Raycaster(), ndc = new TH.Vector2();

    function emit(name, payload) { (listeners[name] || []).forEach(function (fn) { fn(payload); }); }
    function on(name, fn) { (listeners[name] = listeners[name] || []).push(fn); }
    function animating() { return !reduced && opts.motion !== 'off'; }

    /* ── Textures. Generated once, no external assets (img-src stays 'self'). ── */
    function softDot(inner, outer) {
      var c = document.createElement('canvas'); c.width = c.height = 64;
      var g = c.getContext('2d');
      var grd = g.createRadialGradient(32, 32, 0, 32, 32, 32);
      grd.addColorStop(0, inner); grd.addColorStop(0.45, outer); grd.addColorStop(1, 'rgba(0,0,0,0)');
      g.fillStyle = grd; g.fillRect(0, 0, 64, 64);
      var t = new TH.CanvasTexture(c); t.needsUpdate = true; return t;
    }
    var TEX = null;

    /* ── Build ─────────────────────────────────────────────────────────────── */
    function mount(container) {
      TH = T();
      if (!TH) throw new Error('three.js is not loaded');

      root = document.createElement('div');
      root.className = 'colony-webgl';
      root.style.cssText = 'position:absolute;inset:0;overflow:hidden;background:#04060b';
      container.appendChild(root);

      labelHost = document.createElement('div');
      labelHost.style.cssText = 'position:absolute;inset:0;pointer-events:none;font:11px/1.3 ui-monospace,monospace';
      root.appendChild(labelHost);

      renderer = new TH.WebGLRenderer({ antialias: false, alpha: false, powerPreference: 'high-performance' });
      // Capped rather than trusted: a 3x DPR phone renders nine times the pixels
      // of a 1x panel for no visible gain on a particle field.
      renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 1.75));
      renderer.setClearColor(BG, 1);
      root.appendChild(renderer.domElement);

      scene = new TH.Scene();
      scene.fog = new TH.FogExp2(BG, 0.00055);
      camera = new TH.PerspectiveCamera(52, 1, 1, 6000);
      camera.position.set(0, 0, 900);

      TEX = {
        record: softDot('rgba(255,255,255,.95)', 'rgba(255,255,255,.28)'),
        nucleus: softDot('rgba(255,255,255,.85)', 'rgba(255,255,255,.12)')
      };

      renderer.domElement.addEventListener('webglcontextlost', onContextLost, false);
      renderer.domElement.addEventListener('webglcontextrestored', onContextRestored, false);
      bindInput();

      ro = new ResizeObserver(resize); ro.observe(root);
      resize();
      loop();
      return true;
    }

    function resize() {
      if (!root || !renderer) return;
      var w = root.clientWidth || 1, h = root.clientHeight || 1;
      renderer.setSize(w, h, false);
      camera.aspect = w / h; camera.updateProjectionMatrix();
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

    /* ── Chambers: a volume of particles, no membrane ──────────────────────── */
    function buildChamber(sec, index) {
      var lk = look(sec.id);
      var pos = layout[sec.id] || home(sec.id, index);
      var group = new TH.Group();
      group.position.set(pos[0], pos[1], pos[2]);
      group.userData.sectorId = sec.id;

      // Radius follows real mass: the sector's relative weight and how many
      // records it actually holds. An empty chamber is SMALL, and that is the
      // truthful picture of a young colony rather than a defect to pad out.
      var n = sec.recordCount || 0;
      var radius = 46 * lk.mass + Math.min(58, Math.sqrt(n) * 7);

      var count = Math.min(1400, 90 + n * 3);
      var geo = new TH.BufferGeometry();
      var xyz = new Float32Array(count * 3), col = new Float32Array(count * 3), sz = new Float32Array(count);
      var base = new TH.Color(lk.color);

      for (var i = 0; i < count; i++) {
        // Placement is deterministic per index — the same snapshot rebuilds the
        // same cloud. Depth carries meaning: verified/durable settles inward,
        // recent context rides the shell.
        var r0 = ((i * 2654435761) >>> 0) / 4294967296;
        var r1 = ((i * 40503 + 12345) >>> 0 % 65536) / 65536;
        var r2 = ((i * 2246822519) >>> 0) / 4294967296;
        var u = r0 * 2 - 1, th = r1 * Math.PI * 2, s = Math.sqrt(Math.max(0, 1 - u * u));
        var band = 0.55 + r2 * 0.45;
        var rr = radius * band;
        xyz[i * 3] = s * Math.cos(th) * rr;
        xyz[i * 3 + 1] = s * Math.sin(th) * rr;
        xyz[i * 3 + 2] = u * rr;
        var shade = 0.45 + r2 * 0.55;
        col[i * 3] = base.r * shade; col[i * 3 + 1] = base.g * shade; col[i * 3 + 2] = base.b * shade;
        sz[i] = 2.2 + r2 * 3.4;
      }
      geo.setAttribute('position', new TH.BufferAttribute(xyz, 3));
      geo.setAttribute('color', new TH.BufferAttribute(col, 3));
      geo.setAttribute('size', new TH.BufferAttribute(sz, 1));

      var mat = new TH.PointsMaterial({
        size: 4.2, map: TEX.record, vertexColors: true, transparent: true,
        opacity: 0.72, depthWrite: false, blending: TH.AdditiveBlending, sizeAttenuation: true
      });
      var points = new TH.Points(geo, mat);
      group.add(points);

      // The soft internal nucleus — a glow, not a core sphere. No geometry means
      // no silhouette and nothing for a rim light to catch.
      var nucleus = new TH.Sprite(new TH.SpriteMaterial({
        map: TEX.nucleus, color: lk.core, transparent: true, opacity: 0.5,
        depthWrite: false, blending: TH.AdditiveBlending
      }));
      nucleus.scale.setScalar(radius * 1.15);
      group.add(nucleus);

      var ants = new TH.Group();
      group.add(ants);

      scene.add(group);
      return { id: sec.id, group: group, points: points, nucleus: nucleus, ants: ants, pos: pos, radius: radius, look: lk };
    }

    /* ── Residents: real roles, docked, dim, never wandering ───────────────── */
    function placeAnts(ch, sec) {
      while (ch.ants.children.length) ch.ants.remove(ch.ants.children[0]);
      var list = sec.residents || [];
      list.forEach(function (r, i) {
        // A fixed seat per role id: an ant does not drift between frames, and it
        // does not move between renders of the same roster.
        var a = (i / Math.max(1, list.length)) * Math.PI * 2;
        var ring = ch.radius * 0.52;
        var s = new TH.Sprite(new TH.SpriteMaterial({
          map: TEX.record,
          color: r.status === 'working' ? 0xf5eee2 : (r.status === 'disabled' ? 0x3b4048 : 0x8b93a1),
          transparent: true,
          // Idle is DIM and stays dim. Only a real running task assigned to this
          // role brightens it, and even then it does not move.
          opacity: r.status === 'working' ? 0.95 : (r.status === 'disabled' ? 0.22 : 0.45),
          depthWrite: false
        }));
        s.position.set(Math.cos(a) * ring, Math.sin(a) * ring, Math.sin(i * 1.7) * ch.radius * 0.2);
        s.scale.setScalar(r.status === 'working' ? 13 : 10);
        s.userData.resident = r;
        s.userData.sectorId = sec.id;
        ch.ants.add(s);
      });
    }

    /* ── Conduits: soft particle paths, never tubes ────────────────────────── */
    function buildConduit(a, b, kind) {
      var from = chambers[a], to = chambers[b];
      if (!from || !to) return null;

      // Structural and authority links are SPARSE and still. They say "these
      // chambers are related", never "traffic is flowing" — portraying a
      // navigation backbone as activity is the misreading this avoids.
      var authority = kind === 'authority';
      var n = authority ? 150 : 120;
      var strands = authority ? 1 : 3;

      var geo = new TH.BufferGeometry();
      var xyz = new Float32Array(n * 3), col = new Float32Array(n * 3), total = 0;
      var A = from.group.position, B = to.group.position;
      var mid = new TH.Vector3().addVectors(A, B).multiplyScalar(0.5);
      // The Queen→Micromound path is straighter and more orderly; everything
      // else bows gently so parallel links do not read as a bundle of pipes.
      if (!authority) mid.z += 60;

      var tint = new TH.Color(authority ? 0xc9cfdc : 0x5d6b7d);
      for (var s = 0; s < strands && total < n; s++) {
        var jitter = (s - (strands - 1) / 2) * (authority ? 0 : 9);
        var per = Math.floor(n / strands);
        for (var i = 0; i < per && total < n; i++, total++) {
          var t = i / Math.max(1, per - 1);
          // Quadratic bow, then density tapering: particles thin out toward the
          // chambers so nothing forms a hard mouth at either end.
          var it = 1 - t;
          var x = it * it * A.x + 2 * it * t * mid.x + t * t * B.x;
          var y = it * it * A.y + 2 * it * t * mid.y + t * t * B.y;
          var z = it * it * A.z + 2 * it * t * mid.z + t * t * B.z;
          var taper = Math.sin(Math.PI * t);
          xyz[total * 3] = x + jitter * taper;
          xyz[total * 3 + 1] = y + jitter * taper * 0.6;
          xyz[total * 3 + 2] = z + jitter * taper * 0.3;
          var f = 0.25 + taper * 0.5;
          col[total * 3] = tint.r * f; col[total * 3 + 1] = tint.g * f; col[total * 3 + 2] = tint.b * f;
        }
      }
      geo.setAttribute('position', new TH.BufferAttribute(xyz.subarray(0, total * 3), 3));
      geo.setAttribute('color', new TH.BufferAttribute(col.subarray(0, total * 3), 3));

      var pts = new TH.Points(geo, new TH.PointsMaterial({
        size: authority ? 2.6 : 2.2, map: TEX.record, vertexColors: true, transparent: true,
        opacity: authority ? 0.5 : 0.32, depthWrite: false, blending: TH.AdditiveBlending
      }));
      scene.add(pts);
      return { from: a, to: b, kind: kind, obj: pts };
    }

    function rebuildConduits(sc) {
      conduits.forEach(function (c) { scene.remove(c.obj); c.obj.geometry.dispose(); });
      conduits = [];

      // The idle scene shows a structural backbone and the authority path — and
      // nothing else. No all-to-all lateral galleries: a permanent mesh between
      // every chamber is decoration that reads as traffic.
      var ids = Object.keys(chambers);
      ids.forEach(function (id) {
        if (id === 'queen' || id === 'mound') return;
        var c = buildConduit('queen', id, 'structural'); if (c) conduits.push(c);
      });
      if (chambers.mound) {
        var auth = buildConduit('queen', 'mound', 'authority'); if (auth) conduits.push(auth);
      }
      // Mission routes come from persisted task edges, and only those.
      (sc.edges || []).forEach(function (e) {
        var c = buildConduit(e.from, e.to, 'mission'); if (c) conduits.push(c);
      });
    }

    /* ── Finite, event-backed transitions ──────────────────────────────────── */
    function startFlights(sc) {
      // §14: a historical frame is a STILL. Playing a past transition would both
      // animate something that is not happening and burn its one-shot id, so the
      // same transition would never play when the view returns to LIVE.
      if (sc.meta && sc.meta.history) return;

      (sc.transitions || []).forEach(function (tr) {
        if (playedTransitions[tr.id]) return;   // once per unique event id, ever
        playedTransitions[tr.id] = true;
        if (!animating()) return;

        var to = chambers[tr.to];
        if (!to) return;
        var from = tr.from ? chambers[tr.from] : null;

        // No recorded source means an ARRIVAL, not a journey. Inventing an origin
        // so the particle has somewhere to start is exactly the inference this
        // release removed.
        flights.push({
          a: from ? from.group.position.clone() : to.group.position.clone().add(new TH.Vector3(0, 0, 90)),
          b: to.group.position.clone(),
          t: 0, life: from ? 1100 : 520, sprite: spawnFlight(to.look.core)
        });
      });
    }
    function spawnFlight(color) {
      var s = new TH.Sprite(new TH.SpriteMaterial({
        map: TEX.nucleus, color: color, transparent: true, opacity: 0.9,
        depthWrite: false, blending: TH.AdditiveBlending
      }));
      s.scale.setScalar(16); scene.add(s); return s;
    }

    /* ── Scene intake ─────────────────────────────────────────────────────── */
    function setScene(sc) {
      current = sc;
      if (!scene) return;

      var wanted = {};
      (sc.sectors || []).forEach(function (s, i) {
        wanted[s.id] = true;
        var ch = chambers[s.id];
        if (ch) { scene.remove(ch.group); disposeGroup(ch.group); }
        chambers[s.id] = buildChamber(s, i);
        placeAnts(chambers[s.id], s);
      });
      Object.keys(chambers).forEach(function (id) {
        if (wanted[id]) return;
        scene.remove(chambers[id].group); disposeGroup(chambers[id].group); delete chambers[id];
      });

      rebuildConduits(sc);
      startFlights(sc);
      emit('scene', sc);
    }

    function disposeGroup(g) {
      g.traverse(function (o) {
        if (o.geometry) o.geometry.dispose();
        if (o.material) { if (o.material.map && o.material.map !== TEX.record && o.material.map !== TEX.nucleus) o.material.map.dispose(); o.material.dispose(); }
      });
    }

    /* ── Input: hover, select, drag ────────────────────────────────────────── */
    function bindInput() {
      var el = renderer.domElement;
      el.style.touchAction = 'none';
      el.addEventListener('pointermove', onMove);
      el.addEventListener('pointerdown', onDown);
      window.addEventListener('pointerup', onUp);
      el.addEventListener('wheel', onWheel, { passive: false });
      el.addEventListener('dblclick', onDouble);
    }
    function pick(e) {
      var r = renderer.domElement.getBoundingClientRect();
      ndc.x = ((e.clientX - r.left) / r.width) * 2 - 1;
      ndc.y = -((e.clientY - r.top) / r.height) * 2 + 1;
      ray.setFromCamera(ndc, camera);
      var groups = Object.keys(chambers).map(function (k) { return chambers[k].group; });
      var hits = ray.intersectObjects(groups, true);
      for (var i = 0; i < hits.length; i++) {
        var o = hits[i].object;
        if (o.userData && o.userData.resident) return { kind: 'resident', data: o.userData.resident, sectorId: o.userData.sectorId };
        var p = o; while (p && !p.userData.sectorId) p = p.parent;
        if (p) return { kind: 'sector', sectorId: p.userData.sectorId };
      }
      return null;
    }
    function onMove(e) {
      if (drag) {
        var r = renderer.domElement.getBoundingClientRect();
        var scale = camera.position.distanceTo(camTarget) / r.height;
        var dx = (e.clientX - drag.x) * scale, dy = -(e.clientY - drag.y) * scale;
        var ch = chambers[drag.id];
        if (ch) {
          // Shift moves in DEPTH. Without an explicit modifier a 2D pointer
          // cannot address three axes, and guessing from gesture direction makes
          // the third one arrive by accident.
          if (e.shiftKey) ch.group.position.z = drag.z0 + dy;
          else { ch.group.position.x = drag.x0 + dx; ch.group.position.y = drag.y0 + dy; }
          layout[drag.id] = [ch.group.position.x, ch.group.position.y, ch.group.position.z];
          if (current) rebuildConduits(current);   // conduits reflow with the chamber
        }
        return;
      }
      hover = pick(e);
      renderer.domElement.style.cursor = hover ? 'pointer' : 'default';
      emit('hover', hover);
    }
    function onDown(e) {
      var hit = pick(e);
      if (!hit) return;
      if (hit.kind === 'resident') { emit('resident', hit); return; }
      var ch = chambers[hit.sectorId];
      if (!ch) return;
      drag = { id: hit.sectorId, x: e.clientX, y: e.clientY, x0: ch.group.position.x, y0: ch.group.position.y, z0: ch.group.position.z, moved: false };
    }
    function onUp(e) {
      if (drag) {
        var moved = Math.abs(e.clientX - drag.x) > 3 || Math.abs(e.clientY - drag.y) > 3;
        if (moved) emit('layout', getLayout());
        else emit('sector', { sectorId: drag.id });
        drag = null;
      }
    }
    function onWheel(e) {
      e.preventDefault();
      var d = camWant.length();
      camWant.setLength(Math.max(120, Math.min(2200, d * (e.deltaY > 0 ? 1.12 : 0.89))));
      updateDepth();
    }
    function onDouble(e) {
      var hit = pick(e);
      if (hit && hit.sectorId) focus(hit.sectorId);
      else survey();
    }

    /* ── Semantic zoom ─────────────────────────────────────────────────────── */
    function updateDepth() {
      var d = camWant.length();
      var next = d > 800 ? 'survey' : d > 420 ? 'approach' : d > 200 ? 'inside' : 'cluster';
      if (next !== depth) { depth = next; emit('depth', { depth: depth, focus: focusId }); }
    }
    function survey() { focusId = null; camTarget.set(0, 0, 0); camWant.set(0, 0, 900); updateDepth(); }
    function focus(id) {
      var ch = chambers[id]; if (!ch) return;
      focusId = id;
      camTarget.copy(ch.group.position);
      camWant.set(0, 0, Math.max(150, ch.radius * 3.4));
      updateDepth();
    }
    function enter(id) {
      var ch = chambers[id]; if (!ch) return;
      focusId = id; camTarget.copy(ch.group.position);
      camWant.set(0, 0, Math.max(90, ch.radius * 0.85));   // camera inside the volume
      updateDepth();
    }

    /* ── §15 MICROMOUND DESCENT ────────────────────────────────────────────────
       The camera leaves the colony along the AUTHORITY conduit — the same line
       `rebuildConduits` draws from the Queen to the mound, and the only edge in
       this scene that represents a grant rather than a dependency. Travelling it
       is the point: a physical device acts because the Queen chartered it, and
       the descent is that sentence drawn.

       Two staged waypoints, then a stop. It is not a flythrough and it does not
       loop: the camera moves once, on the operator's request, and settles inside
       the mound volume where the technical panel takes over.

       Refuses when there is no mound chamber. A descent into a device the colony
       has not enrolled would be a camera move into empty space presented as
       arrival at a machine. */
    function descend() {
      var mound = chambers.mound, queen = chambers.queen;
      if (!mound) return false;

      // Stage one: the authority seal at the Queen, looking down the conduit.
      if (queen) {
        camTarget.copy(queen.group.position);
        camWant.set(0, 0, Math.max(150, queen.radius * 3.0));
        updateDepth();
      }

      // Stage two: through the conduit and into the mound. Scheduled rather than
      // set immediately so the eased camera actually travels the line instead of
      // cutting to the far end; skipped under reduced motion, which asks for the
      // destination without the journey.
      var arrive = function () {
        if (destroyed || !chambers.mound) return;
        focusId = 'mound';
        camTarget.copy(chambers.mound.group.position);
        camWant.set(0, 0, Math.max(70, chambers.mound.radius * 0.9));
        updateDepth();
        emit('descend', { moundChamber: true });
      };
      if (reduced || !queen) arrive();
      else { clearTimeout(descentTimer); descentTimer = setTimeout(arrive, 620); }
      return true;
    }

    /* ── Labels: a bounded pool, never one node per record ─────────────────── */
    function syncLabels() {
      if (opts.labels === 'off') { labelPool.forEach(function (l) { l.style.display = 'none'; }); return; }
      var ids = Object.keys(chambers);
      var want = Math.min(ids.length, 12);
      while (labelPool.length < want) {
        var el = document.createElement('div');
        el.style.cssText = 'position:absolute;transform:translate(-50%,-50%);color:#8b93a1;letter-spacing:.14em;white-space:nowrap;text-shadow:0 0 8px #04060b';
        labelHost.appendChild(el); labelPool.push(el);
      }
      var v = new TH.Vector3();
      for (var i = 0; i < labelPool.length; i++) {
        var el2 = labelPool[i], id = ids[i];
        if (!id) { el2.style.display = 'none'; continue; }
        var ch = chambers[id];
        // Outside the silhouette at survey scale, so a label never sits on top
        // of the cloud it names.
        v.copy(ch.group.position); v.y += ch.radius * 1.35;
        v.project(camera);
        if (v.z > 1) { el2.style.display = 'none'; continue; }
        var r = renderer.domElement;
        el2.style.display = '';
        el2.style.left = ((v.x * 0.5 + 0.5) * r.clientWidth) + 'px';
        el2.style.top = ((-v.y * 0.5 + 0.5) * r.clientHeight) + 'px';
        var sec = (current && (current.sectors || []).filter(function (s) { return s.id === id; })[0]) || null;
        el2.textContent = (sec ? sec.label : id) + (sec && sec.recordCount ? '  ' + sec.recordCount : '');
        el2.style.opacity = depth === 'survey' ? '.85' : '.35';
      }
    }

    /* ── Frame ─────────────────────────────────────────────────────────────── */
    var last = 0;
    function loop(now) {
      if (destroyed) return;
      raf = requestAnimationFrame(loop);
      if (contextLost) return;
      var dt = Math.min(64, (now || 0) - last); last = now || 0;

      // Camera easing is the ONLY continuous motion in an idle colony, and it
      // settles. It moves the viewpoint; it never moves the colony.
      var ease = reduced ? 1 : 0.085;
      camera.position.lerp(new TH.Vector3(camTarget.x + camWant.x, camTarget.y + camWant.y, camTarget.z + camWant.z), ease);
      camera.lookAt(camTarget);

      // Recorded transitions play once and stop. Nothing loops.
      for (var i = flights.length - 1; i >= 0; i--) {
        var f = flights[i];
        f.t += dt;
        var k = Math.min(1, f.t / f.life);
        f.sprite.position.lerpVectors(f.a, f.b, k);
        f.sprite.material.opacity = 0.9 * (1 - k);
        if (k >= 1) { scene.remove(f.sprite); f.sprite.material.dispose(); flights.splice(i, 1); }
      }

      syncLabels();
      renderer.render(scene, camera);
    }

    /* ── Layout, for /ui/state ─────────────────────────────────────────────── */
    function getLayout() {
      var out = { schema: 1, sectors: {} };
      Object.keys(chambers).forEach(function (id) {
        var p = chambers[id].group.position;
        out.sectors[id] = [round(p.x), round(p.y), round(p.z)];
      });
      return out;
    }
    function round(n) { return Math.round(n * 10) / 10; }
    function setLayout(l) {
      if (!l || l.schema !== 1 || !l.sectors) return false;
      var ok = false;
      Object.keys(l.sectors).forEach(function (id) {
        var p = l.sectors[id];
        // Finite and bounded, or ignored. A NaN from a corrupted save puts a
        // chamber at an unreachable coordinate and the view looks empty.
        if (!Array.isArray(p) || p.length !== 3) return;
        if (!p.every(function (n) { return typeof n === 'number' && isFinite(n) && Math.abs(n) <= 4000; })) return;
        layout[id] = p.slice(); ok = true;
        if (chambers[id]) chambers[id].group.position.set(p[0], p[1], p[2]);
      });
      if (ok && current) rebuildConduits(current);
      return ok;
    }
    function resetLayout() {
      layout = {};
      Object.keys(chambers).forEach(function (id, i) {
        var h = home(id, i);
        chambers[id].group.position.set(h[0], h[1], h[2]);
      });
      if (current) rebuildConduits(current);
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
        renderer.dispose();
      }
      Object.keys(chambers).forEach(function (id) { disposeGroup(chambers[id].group); });
      chambers = {}; conduits = []; flights = [];
      if (root && root.parentNode) root.parentNode.removeChild(root);
      root = null; scene = null; renderer = null;
    }

    return {
      mount: mount, destroy: destroy, setScene: setScene, on: on,
      setOptions: function (o) {
        opts = Object.assign(opts, o || {});
        reduced = (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches)
               || opts.motion === 'off';
      },
      survey: survey, focus: focus, enter: enter, resetView: survey,
      descend: descend,
      getLayout: getLayout, setLayout: setLayout, resetLayout: resetLayout,
      depth: function () { return depth; },
      focused: function () { return focusId; },
      /** True when the last scene handed in was a reconstructed frame, not live. */
      historical: function () { return !!(current && current.meta && current.meta.history); },
      /** The sectors this renderer actually built — the HUD offers no control for a chamber that is not there. */
      sectorIds: function () { return Object.keys(chambers); }
    };
  }

  window.ColonyRenderer = { create: create, available: available };
})();
