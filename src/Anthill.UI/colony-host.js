/* ─────────────────────────────────────────────────────────────────────────────
   COLONY HOST — the wiring between the console and Colony Live.

   The ONLY file in the feature allowed to reach the network, and it reaches it
   exactly as `.115` laid down: two bounded reads on enable (the snapshot, the
   first records page), the saved layout from /ui/state, the fleet listing once,
   and per-mound stop/resume. Everything live arrives through the /events/stream
   subscription this page already holds and the polls app.js already runs
   (`ColonyHost.ingestGraph` / `ingestApprovals` are called FROM those handlers);
   nothing here polls anything the console can already see.

   The reducer (colony-topology.js) decides what an event means. The renderer
   (colony-live.js) draws the scene it publishes. This file toggles, hydrates,
   persists the operator's layout, and relays the renderer's events to the page
   (colony-home.js) and to the console's existing Agent Inspector. It does not
   decide, and it does not draw.
   ───────────────────────────────────────────────────────────────────────────── */
(function () {
  'use strict';

  var live = null, topo = null;
  var VIEW_KEY = 'anthill.colony.view3d';
  var layoutTimer = null;
  var sceneListeners = [], liveListeners = [];

  function pref(name, fallback) {
    try { return typeof window[name] !== 'undefined' ? window[name] : fallback; }
    catch (e) { return fallback; }
  }

  function enable(area, classic) {
    topo = ColonyTopology.create();
    live = ColonyLive.create();

    /* MOUNTING IS WHERE A RENDERER ACTUALLY FAILS. The renderer is canvas-2D and has no
       WebGL to lose, but a mount can still throw on a detached area or an exotic
       canvas policy — and when it does the classic canvas must come back rather than
       leave a hidden `#c` under nothing. Reported, not swallowed. */
    try {
      live.mount(area);
    } catch (e) {
      try { console.warn('[colony-live] the renderer failed to mount, keeping the classic canvas: ' + ((e && e.message) || e)); } catch (e2) { }
      try { live.destroy(); } catch (e3) { }
      live = null; topo = null;
      return false;
    }

    live.setOptions({
      motion: pref('colonyMotion', 'normal'),
      labels: pref('colonyLabels', 'normal'),
      trails: pref('colonyPheromones', 'on') !== 'off'
    });

    // The topology publishes to the renderer and to whoever asked (the live bar reads counts
    // from the same scene rather than polling anything).
    topo.onScene(function (s) {
      if (live) live.setTopology(s);
      sceneListeners.forEach(function (fn) { try { fn(s); } catch (e) { } });
    });

    /* A resident is a real registry role or worker, so opening one opens the EXISTING Agent
       Inspector for it rather than a second inspector of our own. */
    live.on('resident', function (h) { openAgentInspector((h && h.resident && (h.resident.parent || h.resident.roleId)) || ''); });
    live.on('layout', saveLayout);

    liveListeners.forEach(function (fn) { try { fn(live); } catch (e) { } });
    hydrate();
    return true;
  }

  function openAgentInspector(roleId) {
    var who = String(roleId || '').toLowerCase();
    if (!who || typeof nodes === 'undefined' || typeof showInspector !== 'function') return;
    var n = nodes.find(function (x) { return x.ant === who || x.worker === who || x.id === who; });
    if (n) showInspector(n);
  }

  function disable() {
    if (live) live.destroy();
    live = null; topo = null;
    liveListeners.forEach(function (fn) { try { fn(null); } catch (e) { } });
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

    // The 2D chrome belongs to the 2D canvas; the stylesheet folds it away under this class. It is
    // set BEFORE the renderer is created or torn down, so chrome notified by onLive reads the new
    // state rather than the old one.
    document.body.classList.toggle('colony-live-on', !!on);
    if (on && !live) { if (!enable(area, classic)) on = false; }
    else if (!on && live) disable();

    classic.style.display = on ? 'none' : '';
    document.body.classList.toggle('colony-live-on', !!on);
    document.querySelectorAll('#colony-viewbar [data-colonyact="live3d"]')
      .forEach(function (b) { b.classList.toggle('on', !!on); });
    try { localStorage.setItem(VIEW_KEY, on ? '1' : '0'); } catch (e) { }
  }

  /* ── The read model. Two bounded reads on enable, then nothing — unless enable happened before
     the operator signed in, in which case both reads were refused and the colony would draw
     nothing forever. Found live: Live is enabled at DOMContentLoaded, which on a fresh session is
     the sign-in screen. So hydration is RE-ATTEMPTED — once per trigger, never on a clock — when
     the page is entered and when the first event arrives on the stream (which only connects after
     auth). A snapshot that has already landed makes every later attempt a no-op. */
  var hydrating = false;
  function hydrated() { try { return !!(topo && topo.project().meta.hydrated); } catch (e) { return false; } }
  function hydrate() {
    if (typeof api !== 'function' || !topo || hydrating || hydrated()) return;
    hydrating = true;
    api('/colony/live/snapshot').then(function (snap) {
      var body = (snap && snap.data) || snap;
      if (!body || !body.sectors) throw new Error((snap && snap.message) || 'snapshot refused');
      if (topo) topo.applySnapshot(body);
      return api('/ui/state');
    }).then(function (st) {
      var saved = ((st && st.data) || st || {}).colony_live_layout;
      if (saved && live && live.setLayout) live.setLayout(saved);
      return api('/colony/live/records?limit=200');
    }).then(function (recs) {
      if (topo) topo.ingestRecords((recs && recs.data) || recs);
    }).catch(function (e) {
      try { console.warn('[colony-live] read model unavailable (will retry on page entry or first event): ' + (e && e.message)); } catch (e2) { }
    }).then(function () { hydrating = false; });

    /* §15. The fleet listing, once, on enable — not polled. A colony without the Micromound
       module does not map this route, so a 404 is the ORDINARY case: no mound is ingested and
       the mound chamber is never built. Nothing here invents a device to have something to draw. */
    api('/micromound/mounds').then(function (fleet) {
      if (topo) topo.ingestMound((fleet && fleet.data) || fleet);
    }).catch(function () { /* no module, or no permission: there is no mound. */ });
  }

  /* ── The one mutation: per-mound stop / resume. Posts, then RE-READS the fleet so the view
     shows the colony's answer; never flips its own flag on a 200, never touches the global stop
     (a file on disk, by design), never claims delivery — the order waits for the device's beat. */
  function moundStop(moundId, stopped) {
    if (typeof api !== 'function' || !moundId) return Promise.resolve(false);
    var path = stopped ? '/micromound/stop' : '/micromound/stop/resume';
    return api(path, 'POST', { mound_id: moundId })
      .then(function () { return api('/micromound/mounds'); })
      .then(function (fleet) { if (topo) topo.ingestMound((fleet && fleet.data) || fleet); return true; })
      .catch(function (e) {
        try { console.warn('[colony-live] mound ' + (stopped ? 'stop' : 'resume') + ' failed for ' + moundId + ': ' + ((e && e.message) || e)); } catch (e2) { }
        if (typeof api === 'function') api('/micromound/mounds').then(function (fleet) { if (topo) topo.ingestMound((fleet && fleet.data) || fleet); }).catch(function () { });
        throw e;
      });
  }

  /* Layout persists through /ui/state: an arrangement (and any chamber renames) made on one
     machine follows the operator's account. Debounced — a drag emits continuously. */
  function saveLayout(layout) {
    if (typeof api !== 'function' || !layout) return;
    clearTimeout(layoutTimer);
    layoutTimer = setTimeout(function () {
      api('/ui/state').then(function (cur) {
        var body = Object.assign({}, (cur && cur.data) || cur || {}, { colony_live_layout: layout });
        return api('/ui/state', 'PUT', body);
      }).catch(function (e) {
        try { console.warn('[colony-live] layout not saved: ' + (e && e.message)); } catch (e2) { }
      });
    }, 900);
  }

  // One subscription for the page's life; toggling must not stack listeners.
  if (typeof onColonyEvent === 'function') {
    onColonyEvent(function (ev) { if (!topo) return; if (!hydrated()) hydrate(); topo.ingestEvent(ev); });
  }

  /* COLONY LIVE IS THE DEFAULT VIEW (`.117`). Only an explicit '0' — an operator who turned it
     off — keeps the classic canvas; a mount that fails falls back on its own. */
  document.addEventListener('DOMContentLoaded', function () {
    var want = true;
    try { want = localStorage.getItem(VIEW_KEY) !== '0'; } catch (e) { }
    if (want) toggle(true);
  });

  window.ColonyHost = {
    toggle: toggle,
    /** For app.js's polls — the host owns the feed, app.js owns the fetch. */
    ingestGraph: function (g) { if (topo) topo.ingestGraph(g); },
    ingestApprovals: function (a) { if (topo) topo.ingestApprovals(a); },
    ingestMound: function (m) { if (topo) topo.ingestMound(m); },
    setOptions: function (o) { if (live) live.setOptions(o); },
    resetAll: function () { if (live) live.resetAll(); },
    zoom: function (f) { if (live) live.zoom(f); },
    active: function () { return !!live; },
    live: function () { return live; },
    topology: function () { return topo; },
    /** The page chrome subscribes here — the same scene the renderer gets, no second feed. */
    onScene: function (fn) { if (typeof fn === 'function') sceneListeners.push(fn); },
    /** Fires with the renderer on enable and with null on disable, so chrome can (re)hook it. */
    onLive: function (fn) { if (typeof fn === 'function') { liveListeners.push(fn); if (live) fn(live); } },
    moundStop: moundStop,
    /** Re-attempt hydration (a no-op once the snapshot has landed) — the page calls this on entry. */
    hydrate: hydrate,
    renderer: function () { return live ? 'canvas2d' : null; }
  };
})();
