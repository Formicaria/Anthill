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
   (colony-live.js) draws the scene it publishes. This file mounts, hydrates,
   persists the operator's layout, and relays the renderer's events to the page
   (colony-home.js) and to the console's Ant Inspector. It does not decide, and
   it does not draw.
   ───────────────────────────────────────────────────────────────────────────── */
(function () {
  'use strict';

  var live = null, topo = null;
  var layoutTimer = null;
  var sceneListeners = [], liveListeners = [];

  function pref(name, fallback) {
    try { return typeof window[name] !== 'undefined' ? window[name] : fallback; }
    catch (e) { return fallback; }
  }

  /* ── When the renderer does not come up ──────────────────────────────────────
     v0.3.8.125. Until this release the answer to a failed mount was "show the
     classic canvas instead", and that was a real answer because there were two
     renderers. There is one now, so the fallback had to become something rather
     than nothing: a blank panel and a console warning is a colony view that
     silently is not there, which is the failure this file's own guard was
     written to prevent.

     So a failure SAYS SO, in the DOM, where the colony would have been — what
     failed, and a way to try again. Retry is worth offering because the causes
     are overwhelmingly transient: a container measured at 0×0 mid-layout, an
     asset that lost a race. Nothing here retries on its own; an operator asks. */
  function renderMountFailure(area, message) {
    if (!area) return;
    area.innerHTML =
      '<div class="colony-down" role="alert">'
      + '<div class="colony-down-hd">The colony view could not start</div>'
      + '<div class="colony-down-why"></div>'
      + '<button class="btn btn-ghost" id="colony-down-retry">Retry</button>'
      + '</div>';
    // textContent, not interpolation: the message is an exception's, and an exception's text can
    // carry anything the runtime put in it.
    var why = area.querySelector('.colony-down-why');
    if (why) why.textContent = String(message || 'the renderer did not start');
    var btn = area.querySelector('#colony-down-retry');
    if (btn) btn.addEventListener('click', function () { area.innerHTML = ''; mount(); });
  }

  function enable(area) {
    topo = ColonyTopology.create();
    live = ColonyLive.create();

    /* MOUNTING IS WHERE A RENDERER ACTUALLY FAILS. The renderer is canvas-2D and has no
       WebGL to lose, but a mount can still throw on a detached area or an exotic
       canvas policy. Reported and SHOWN, never swallowed. */
    try {
      live.mount(area);
    } catch (e) {
      var msg = (e && e.message) || String(e);
      try { console.warn('[colony-live] the renderer failed to mount: ' + msg); } catch (e2) { }
      try { live.destroy(); } catch (e3) { }
      live = null; topo = null;
      renderMountFailure(area, msg);
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

  /* THE LIVE VIEW IS THE ONLY VIEW. v0.3.8.125.

     This was `toggle(want)`, and the thing it toggled to was the classic force-graph canvas that
     `.125` deleted. What is left is not a toggle with one position — it is a mount, so it is
     spelled as one. Idempotent: called on DOMContentLoaded, again from the Retry button, and
     harmlessly again by anything that used to flip the view. */
  function mount() {
    var area = document.getElementById('colony-canvas-area');
    if (!area || live) return !!live;

    if (!(window.ColonyLive && window.ColonyTopology)) {
      // The assets did not load. Said out loud rather than left as an empty panel — this is the
      // one failure mode an operator can actually act on (a blocked or stale asset).
      renderMountFailure(area, 'the colony renderer did not load');
      return false;
    }

    // Kept as a body class because the page chrome styles against it: the live bar, the sector and
    // record panels are only meaningful while the renderer is up.
    document.body.classList.add('colony-live-on');
    if (!enable(area)) { document.body.classList.remove('colony-live-on'); return false; }
    return true;
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

    /* The roster a mound chamber is drawn with — ITS OWN FETCH, not the fleet's passenger.
       v0.3.8.123.

       `.122` nested this inside the call above, so the seven ants a `+ Mound` chamber shows arrived
       only when the micromound module was compiled in AND the operator held `read_micromound` AND
       the fleet listing had already resolved. Any of those missing and the chamber came up empty,
       which is what the operator saw: "i cant see the ants within them at all."

       None of those conditions has anything to do with drawing seven labels in the operator's own
       colony view, so the route moved to `/colony/mound-roster` — always mapped, guarded by the
       same `read_graph` the snapshot uses — and the fetch moved out here beside the others. */
    api('/colony/mound-roster')
      .then(function (r) { var d = (r && r.data) || r; if (live && live.setMoundDefaults && d && d.ants) live.setMoundDefaults(d.ants); })
      .catch(function (e) { try { console.warn('[colony-live] mound roster unavailable: ' + ((e && e.message) || e)); } catch (e2) { } });
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

  /* COLONY LIVE IS THE VIEW (`.117` made it the default; `.125` made it the only one). There is no
     stored preference to read any more — `anthill.colony.view3d` is not written and not consulted,
     because an operator who had turned the live view OFF would otherwise boot into a colony page
     that renders nothing at all. Any stale '0' in localStorage is simply ignored. */
  document.addEventListener('DOMContentLoaded', function () { mount(); });

  window.ColonyHost = {
    mount: mount,
    /** For app.js's polls — the host owns the feed, app.js owns the fetch. */
    ingestGraph: function (g) { if (topo) topo.ingestGraph(g); },
    ingestApprovals: function (a) { if (topo) topo.ingestApprovals(a); },
    ingestMound: function (m) { if (topo) topo.ingestMound(m); },
    setOptions: function (o) { if (live) live.setOptions(o); },
    resetAll: function () { if (live) live.resetAll(); },
    zoom: function (f) { if (live) live.zoom(f); },
    /** The seam `topologyRemeasure` calls after a re-parent. The renderer has its own
        ResizeObserver, so this is normally redundant — it exists so a renderer that DOES need
        telling has somewhere to be told, rather than that knowledge living back in app.js. */
    remeasure: function () { if (live && typeof live.remeasure === 'function') live.remeasure(); },
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
