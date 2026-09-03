/* ─────────────────────────────────────────────────────────────────────────────
   COLONY LIVE — the host. v0.3.8.116.

   The integration layer, and the only file in the feature that talks to the API.
   It exists as its own console asset for the reason the v0.3.8.52 split exists:
   `app.js` is the shared foundation, a guard holds it under 10,000 lines, and a
   growing feature belongs beside its renderer rather than inside the foundation.

   Each file in the feature has exactly one job, and none of them overlap:

     colony-topology.js   what is TRUE      (no fetch, no drawing)
     colony-renderer.js   how it LOOKS      (WebGL; no state decisions)
     colony-live.js       which renderer    (WebGL, or the classic 2D fallback)
     colony-host.js       wiring and I/O    (this file)

   It loads after `app.js` and uses the globals that file defines — `api`,
   `nodes`, `showInspector`, `onColonyEvent`, the colony display preferences.
   ───────────────────────────────────────────────────────────────────────────── */
(function () {
  'use strict';

  var live = null, topo = null, hud = null;
  var VIEW_KEY = 'anthill.colony.view3d';
  var layoutTimer = null;

  function pref(name, fallback) {
    try { return typeof window[name] !== 'undefined' ? window[name] : fallback; }
    catch (e) { return fallback; }
  }

  function enable(area, classic) {
    topo = ColonyTopology.create();
    live = ColonyLive.create();

    /* MOUNTING IS WHERE A RENDERER ACTUALLY FAILS, and until this was fixed it was
       the one step outside the fallback.

       `ColonyLive.create()` already guards CONSTRUCTION: if `ColonyRenderer.create()`
       throws, it returns the classic projection instead. But `available()` only
       proves that `window.THREE` exists and that *a* WebGL context can be created —
       neither of which is the same as THIS renderer mounting. `mount()` resolves
       three.js, constructs a `WebGLRenderer`, allocates textures and attaches a
       canvas, and any of that can throw on a driver, a blocked context, or a
       three.js whose API moved.

       When it did, the exception escaped `enable()` and then `toggle()`, so
       `classic.style.display` was never restored — leaving the WebGL root div
       (`position:absolute; inset:0; background:#04060b`) as a black rectangle over a
       classic canvas that had been hidden and never brought back. The view looked
       dead and the fallback that existed for exactly this never ran.

       So the mount is guarded here, in the file whose job is wiring, and the failure
       is REPORTED rather than swallowed: the operator gets the 2D projection and the
       console gets the real reason. */
    try {
      live.mount(area);
    } catch (e) {
      try {
        console.warn('[colony-live] the WebGL renderer failed to mount, falling back to the '
                   + 'classic projection: ' + ((e && e.message) || e));
      } catch (e2) { }
      // Remove whatever the failed mount attached before replacing it. A partial
      // mount leaves a full-bleed opaque div, which is the black rectangle above.
      try { live.destroy(); } catch (e3) { }
      live = ColonyLive.createClassic();
      live.renderer = 'canvas2d';
      live.mount(area);
    }

    var options = {
      motion: pref('colonyMotion', 'normal'),
      labels: pref('colonyLabels', 'normal'),
      trails: pref('colonyPheromones', 'on') !== 'off'
    };
    live.setOptions(options);

    // The HUD is optional: a missing asset must cost the operator the chrome,
    // never the view. It is created BEFORE the first scene so no scene is lost.
    if (window.ColonyHud) {
      hud = ColonyHud.create({
        mount: area, live: live, topo: topo,
        motion: options.motion, labels: options.labels, trails: options.trails,
        onResident: openAgentInspector,
        onMoundStop: moundStop
      });
    }

    // The topology publishes to both, and to nothing else. While the HUD is
    // showing a reconstructed frame it keeps feeding the renderer that frame —
    // which is why the renderer's scene is set by the HUD in history mode and
    // by this subscription in live mode, never by both at once.
    topo.onScene(function (s) {
      if (hud) { hud.setScene(s); if (hud.mode() === 'history') return; }
      if (live) live.setTopology(s);
    });

    /* An ant opens the HUD's ant inspector — the trail, the workers, the status —
       and ALSO the console's existing Agent Inspector, which is where routing and
       telemetry for that role already live. Two panels, neither duplicating the
       other: this one is about the ant in the colony, that one about its wiring. */
    live.on('resident', function (h) {
      var r = h && h.data;
      if (hud && r) hud.selectAnt(r);
      openAgentInspector((r && r.roleId) || '');
    });

    // A record grain resolves to the record it stands for.
    live.on('record', function (h) {
      if (hud && h && h.data) hud.selectRecord(h.data);
    });

    // The level changed, so the breadcrumb and BACK have to follow the camera.
    live.on('depth', function () { if (hud) hud.onDepth(); });

    // A chamber click opens the sector inspector; the HUD owns every panel so
    // there is one place that decides what an inspector shows.
    live.on('sector', function (h) {
      if (hud && h && h.sectorId) hud.selectSector(h.sectorId);
    });

    live.on('layout', saveLayout);

    // Hydrate BEFORE the stream matters. Until the snapshot lands the reducer
    // buffers events rather than guessing which sector an ant belongs to — the
    // guess is what used to file unknown roles under the Queen.
    hydrate();
  }

  /* A resident is a real registry role, so opening one opens the EXISTING Agent
     Inspector for it rather than a second inspector of our own. */
  function openAgentInspector(roleId) {
    var who = String(roleId || '').toLowerCase();
    if (!who || typeof nodes === 'undefined') return;
    var n = nodes.find(function (x) { return x.ant === who || x.worker === who || x.id === who; });
    if (n && typeof showInspector === 'function') showInspector(n);
  }

  function disable() {
    if (hud) hud.destroy();
    if (live) live.destroy();
    live = null; topo = null; hud = null;
  }

  function toggle(want) {
    var area = document.getElementById('colony-canvas-area');
    var classic = document.getElementById('c');
    if (!area || !classic) return;

    var on = want === undefined ? !live : !!want;
    if (on && !(window.ColonyLive && window.ColonyTopology)) {
      try { console.warn('[colony-live] assets not loaded; classic canvas kept'); } catch (e) { }
      on = false;
    }

    if (on && !live) enable(area, classic);
    else if (!on && live) disable();

    classic.style.display = on ? 'none' : '';

    /* THE 2D CHROME BELONGS TO THE 2D CANVAS. v0.3.8.115 fix: `toggle` hid `#c` and
       nothing else, so the caste legend, the learning-signals panel, the partial-
       history notice and the "+/- to zoom · drag canvas to pan · drag ant to move"
       hint all stayed painted over the WebGL scene — describing gestures that view
       does not have, on top of a picture they do not describe. The viewbar itself
       STAYS: it owns the Live 3D toggle, and hiding it would strand the operator in
       a view with no way back. */
    document.body.classList.toggle('colony-live-3d', !!on);

    document.querySelectorAll('#colony-viewbar [data-colonyact="live3d"]')
      .forEach(function (b) { b.classList.toggle('on', !!on); });
    try { localStorage.setItem(VIEW_KEY, on ? '1' : '0'); } catch (e) { }
  }

  /* ── The read model. Two bounded reads on enable, then nothing. ───────────
     The live picture comes from the /events/stream subscription this page
     already holds; adding a poll here would be the second fetch the feature is
     not allowed to have. Failure is reported and non-fatal — a colony that
     cannot serve the snapshot still draws its structure from /graph. */
  function hydrate() {
    if (typeof api !== 'function') return;

    api('/colony/live/snapshot').then(function (snap) {
      if (topo) topo.applySnapshot((snap && snap.data) || snap);
      return api('/ui/state');
    }).then(function (st) {
      var saved = ((st && st.data) || st || {}).colony_live_layout;
      if (saved && live && live.setLayout) live.setLayout(saved);
      return api('/colony/live/records?limit=200');
    }).then(function (recs) {
      if (topo) topo.ingestRecords((recs && recs.data) || recs);
    }).catch(function (e) {
      try { console.warn('[colony-live] read model unavailable: ' + (e && e.message)); } catch (e2) { }
    });

    /* §15. The fleet listing, once, on enable — not polled.
       A colony built without the Micromound module does not map this route, so a
       404 here is the ORDINARY case and must be silent about capability the
       colony does not have: no mound is ingested, the mound chamber is never
       built, and the descent control is never offered. The one thing this must
       never do is invent a device so the view has something to descend into. */
    api('/micromound/mounds').then(function (fleet) {
      if (topo) topo.ingestMound((fleet && fleet.data) || fleet);
    }).catch(function () { /* no module, or no permission: there is no mound. */ });
  }

  /* ── The one mutation this feature performs ───────────────────────────────
     PER-MOUND STOP AND RESUME, and it lives here because this is the only file
     in the feature allowed to reach the network — the same boundary that keeps
     the HUD from acting on the colony behind the host's back.

     Three things it deliberately does NOT do:

       · It does not decide the new state. It posts, then re-reads the fleet, so
         the panel shows the COLONY's answer. A view that flipped its own flag on
         a 200 would disagree with the device the first time a stop was accepted
         and then superseded.
       · It does not touch the global stop. That is a file on disk precisely so
         no API flow can clear it (SAFETY.md), and neither this nor micromound.js
         offers a control that appears to.
       · It does not claim delivery. The colony never dials a mound; a stop order
         sits in the downlink queue until the device's next beat collects it, and
         both this and the panel say so in those words.

     A failure is reported and rethrown, so the HUD's pending state clears and
     the operator learns the order did not land rather than watching a button
     settle back as though it had. */
  function moundStop(moundId, stopped) {
    if (typeof api !== 'function' || !moundId) return Promise.resolve(false);
    var path = stopped ? '/micromound/stop' : '/micromound/stop/resume';
    return api(path, 'POST', { mound_id: moundId })
      .then(function () { return api('/micromound/mounds'); })
      .then(function (fleet) {
        if (topo) topo.ingestMound((fleet && fleet.data) || fleet);
        return true;
      })
      .catch(function (e) {
        try {
          console.warn('[colony-live] mound ' + (stopped ? 'stop' : 'resume')
                     + ' failed for ' + moundId + ': ' + ((e && e.message) || e));
        } catch (e2) { }
        // Re-read anyway: the post may have landed and the follow-up read failed,
        // and a stale panel is worse than a slow one.
        if (typeof api === 'function') {
          api('/micromound/mounds')
            .then(function (fleet) { if (topo) topo.ingestMound((fleet && fleet.data) || fleet); })
            .catch(function () { });
        }
        throw e;
      });
  }

  /* Layout persists through /ui/state, not localStorage alone: a layout an
     operator arranged on one machine should follow their account. Debounced,
     because a drag emits continuously and every save is a write. */
  function saveLayout(layout) {
    if (typeof api !== 'function' || !layout) return;
    clearTimeout(layoutTimer);
    layoutTimer = setTimeout(function () {
      api('/ui/state').then(function (cur) {
        var body = Object.assign({}, (cur && cur.data) || cur || {}, { colony_live_layout: layout });
        // api(path, method, body) — positional, and it serializes the body itself.
        return api('/ui/state', 'PUT', body);
      }).catch(function (e) {
        try { console.warn('[colony-live] layout not saved: ' + (e && e.message)); } catch (e2) { }
      });
    }, 900);
  }

  // One subscription for the page's life; toggling must not stack listeners.
  // The reducer decides what an event MEANS — including whether it created a
  // durable record, which the server answers on the wire so there is exactly
  // one implementation of that rule.
  if (typeof onColonyEvent === 'function') {
    onColonyEvent(function (ev) { if (topo) topo.ingestEvent(ev); });
  }

  document.addEventListener('DOMContentLoaded', function () {
    var want = false;
    try { want = localStorage.getItem(VIEW_KEY) === '1'; } catch (e) { }
    if (want) toggle(true);
  });

  window.ColonyHost = {
    toggle: toggle,
    /** For app.js's graph poll — the host owns the feed, app.js owns the fetch. */
    ingestGraph: function (g) { if (topo) topo.ingestGraph(g); },
    ingestApprovals: function (a) { if (topo) topo.ingestApprovals(a); },
    ingestMound: function (m) { if (topo) topo.ingestMound(m); },
    setOptions: function (o) { if (live) live.setOptions(o); },
    active: function () { return !!live; },
    renderer: function () { return live ? live.renderer : null; }
  };
})();
