/* ─────────────────────────────────────────────────────────────────────────────
   COLONY LIVE — the host. v0.3.8.115.

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
    live.mount(area);

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
        onResident: openAgentInspector
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

    live.on('resident', function (h) {
      openAgentInspector((h && h.data && h.data.roleId) || '');
    });

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
