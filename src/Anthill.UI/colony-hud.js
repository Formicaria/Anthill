/* ─────────────────────────────────────────────────────────────────────────────
   COLONY LIVE — the operator surface. v0.3.8.115.

   The chrome around the scene: breadcrumbs, the semantic-zoom controls, the
   inspectors, the growth-playback timeline, and the Micromound descent entry.

   Each file in the feature has exactly one job, and none of them overlap:

     colony-topology.js   what is TRUE      (no fetch, no drawing)
     colony-renderer.js   how it LOOKS      (WebGL; no state decisions)
     colony-live.js       which renderer    (WebGL, or the classic 2D fallback)
     colony-host.js       wiring and I/O    (the only file that fetches)
     colony-hud.js        what the operator sees and presses   (this file)

   ── THE RULES THIS FILE IS BOUND BY ─────────────────────────────────────────

   1. IT SHOWS ONLY WHAT THE SCENE CONTAINS. Every value printed here comes from
      the projection. Where a fact is absent the panel says so — "—", "not
      recorded", "unresolved" — and never fills the gap with a plausible one.

   2. IT OWNS NO STATE ABOUT THE COLONY. Playback position and which panel is
      open are view state and live here. Anything about the colony itself is
      asked of the topology, every time.

   3. NO CLOCK DRIVES ANYTHING. Elapsed time is printed by subtracting a
      recorded `created_at` from now, which is a measurement. Nothing here
      advances a progress bar, estimates a completion, or animates on a timer.

   4. ACTIONS GO THROUGH THE CONSOLE'S EXISTING AUTHENTICATED PATH. The approve
      and reject buttons call `doApproval` — app.js's function, the same one the
      approvals card uses, hitting POST /approve/{id} and /reject/{id} with the
      operator's bearer token. A second implementation here would be a second
      place for the decision rule to drift (defect class 5), and a local-only
      "approved" that never reached the colony is the specific failure §11
      forbids.

   5. NO INLINE HANDLERS. Every control is bound with addEventListener, because
      the console runs under `script-src 'self'` with no `unsafe-inline`.
   ───────────────────────────────────────────────────────────────────────────── */
(function () {
  'use strict';

  var TIER_LABEL = {
    // The operator-facing name for the wire value. The identifier itself is
    // never hidden — it is printed in the technical block, because an operator
    // reading PROTOCOL.md needs to see the same string the protocol uses.
    edge_queen: 'Mound Major',
    deterministic_controller: 'Deterministic Controller'
  };

  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text !== undefined && text !== null) n.textContent = String(text);
    return n;
  }

  /** Real elapsed time from a recorded timestamp. Never an estimate, never a projection. */
  function ago(iso) {
    if (!iso) return '';
    var t = Date.parse(iso);
    if (!isFinite(t)) return '';
    var s = Math.floor((Date.now() - t) / 1000);
    if (s < 0) return 'just now';
    if (s < 60) return s + 's ago';
    if (s < 3600) return Math.floor(s / 60) + 'm ago';
    if (s < 86400) return Math.floor(s / 3600) + 'h ago';
    return Math.floor(s / 86400) + 'd ago';
  }

  function shortId(id) {
    var s = String(id || '');
    return s.length > 14 ? s.slice(0, 14) + '…' : s;
  }

  function create(o) {
    var mount = o.mount, live = o.live, topo = o.topo;
    var scene = null;                 // the last scene handed in
    var mode = 'live';                // 'live' | 'history'
    var historyAt = null;             // ISO string while scrubbing
    var selection = null;             // {kind:'sector'|'record'|'approval', ...}
    var busy = Object.create(null);   // approvalId -> true while a decision is in flight

    var root = el('div', 'clh');
    var crumbs = el('div', 'clh-crumbs');
    var controls = el('div', 'clh-controls');
    var panel = el('div', 'clh-panel clh-hidden');
    var timeline = el('div', 'clh-timeline clh-hidden');
    var notice = el('div', 'clh-notice clh-hidden');

    root.appendChild(crumbs);
    root.appendChild(controls);
    root.appendChild(notice);
    root.appendChild(panel);
    root.appendChild(timeline);
    mount.appendChild(root);

    /* ── Controls ──────────────────────────────────────────────────────────── */
    function can(name) { return live && typeof live[name] === 'function'; }

    function button(label, title, fn) {
      var b = el('button', 'clh-btn', label);
      b.type = 'button';
      b.title = title;
      b.setAttribute('aria-label', title);
      b.addEventListener('click', fn);
      controls.appendChild(b);
      return b;
    }

    function select(label, value, options, fn) {
      var wrap = el('label', 'clh-sel-wrap');
      wrap.appendChild(el('span', 'clh-sel-lbl', label));
      var s = el('select', 'clh-sel');
      options.forEach(function (opt) {
        var op = el('option', null, opt[1]);
        op.value = opt[0];
        if (opt[0] === value) op.selected = true;
        s.appendChild(op);
      });
      s.addEventListener('change', function () { fn(s.value); });
      wrap.appendChild(s);
      controls.appendChild(wrap);
      return s;
    }

    var backBtn = null, descendBtn = null, historyBtn = null, apprBtn = null;

    function buildControls() {
      if (can('survey')) button('Survey', 'Pull back to the whole colony', function () {
        live.survey(); selection = null; render();
      });

      // Back means "one level out from where the camera is". It exists only when
      // the renderer can tell us where that is.
      if (can('survey') && can('focused')) {
        backBtn = button('Back', 'Step out one level', function () {
          var d = can('depth') ? live.depth() : 'survey';
          var f = live.focused();
          // Inside a chamber, back means the chamber from outside. From there,
          // back means the colony. There is no level below `cluster`.
          if (f && (d === 'cluster' || d === 'inside')) live.focus(f);
          else live.survey();
          selection = null; render();
        });
      }

      // §15. Offered only by a renderer that HAS a camera and only once the
      // fleet listing has returned a mound. A control that cannot act is not
      // shown disabled — it is not shown.
      if (can('descend')) {
        descendBtn = button('Descend to mound', 'Travel the authority conduit to the enrolled device', function () {
          if (!live.descend()) return;
          selection = { kind: 'mound' };
          render();
        });
        descendBtn.classList.add('clh-hidden');
      }

      // §11. The count is the real queue length; the panel is where the decision
      // is made. Hidden when nothing is pending rather than showing a zero.
      apprBtn = button('Approvals', 'Approvals awaiting the Queen', function () {
        selection = { kind: 'approval' }; render();
      });
      apprBtn.classList.add('clh-hidden');

      // §14. Shown only when there is a real span to scrub.
      historyBtn = button('History', 'Reconstruct the colony from its persisted records', function () {
        if (mode === 'history') { toLive(); return; }
        var b = topo.historyBounds();
        if (!b.available) return;
        mode = 'history';
        historyAt = b.to;
        timeline.classList.remove('clh-hidden');
        applyHistory();
      });
      historyBtn.classList.add('clh-hidden');

      if (can('setOptions')) {
        select('Motion', o.motion || 'normal',
          [['normal', 'Normal'], ['calm', 'Calm'], ['off', 'Off']],
          function (v) { live.setOptions({ motion: v }); });
        select('Labels', o.labels || 'normal',
          [['normal', 'On'], ['off', 'Off']],
          function (v) { live.setOptions({ labels: v }); });
        select('Trails', o.trails === false ? 'off' : 'on',
          [['on', 'On'], ['off', 'Off']],
          function (v) { live.setOptions({ trails: v === 'on' }); });
      }
    }

    /* ── §14 the timeline ──────────────────────────────────────────────────── */
    var scrub = null, stamp = null, coverage = null;

    function buildTimeline() {
      var liveBtn = el('button', 'clh-btn clh-live', 'Return to LIVE');
      liveBtn.type = 'button';
      liveBtn.addEventListener('click', toLive);

      scrub = el('input', 'clh-scrub');
      scrub.type = 'range';
      scrub.min = '0'; scrub.max = '1000'; scrub.value = '1000'; scrub.step = '1';
      scrub.setAttribute('aria-label', 'Reconstruct the colony at a point in its recorded history');
      scrub.addEventListener('input', function () {
        var b = topo.historyBounds();
        if (!b.available) return;
        var lo = Date.parse(b.from), hi = Date.parse(b.to);
        if (!isFinite(lo) || !isFinite(hi)) return;
        var k = Number(scrub.value) / 1000;
        historyAt = new Date(lo + (hi - lo) * k).toISOString();
        applyHistory();
      });

      stamp = el('div', 'clh-stamp');
      coverage = el('div', 'clh-coverage');

      timeline.appendChild(liveBtn);
      timeline.appendChild(scrub);
      timeline.appendChild(stamp);
      timeline.appendChild(coverage);
    }

    function applyHistory() {
      if (mode !== 'history' || !historyAt) return;
      var frame = topo.historyAt(historyAt);
      live.setTopology(frame);
      renderFrame(frame);
    }

    function toLive() {
      mode = 'live'; historyAt = null;
      timeline.classList.add('clh-hidden');
      if (scene) live.setTopology(scene);
      render();
    }

    /* ── Inspectors ────────────────────────────────────────────────────────── */
    function closeButton() {
      var b = el('button', 'clh-x', '✕');
      b.type = 'button';
      b.title = 'Close';
      b.setAttribute('aria-label', 'Close inspector');
      b.addEventListener('click', function () { selection = null; render(); });
      return b;
    }

    function row(k, v, cls) {
      var r = el('div', 'clh-row' + (cls ? ' ' + cls : ''));
      r.appendChild(el('span', 'clh-k', k));
      r.appendChild(el('span', 'clh-v', v === undefined || v === null || v === '' ? '—' : String(v)));
      return r;
    }

    function sectorPanel(frame, sectorId) {
      var sec = (frame.sectors || []).filter(function (s) { return s.id === sectorId; })[0];
      if (!sec) return null;

      var box = el('div');
      box.appendChild(closeButton());
      box.appendChild(el('h4', 'clh-h', sec.label));
      box.appendChild(row('Sector', sec.id));
      box.appendChild(row('Colonies', (sec.colonies || []).join(', ')));
      box.appendChild(row('Residents', sec.residents.length));
      box.appendChild(row('Running tasks', frame.meta.history ? 'not reconstructable' : sec.runningTasks.length));
      box.appendChild(row('Records held', sec.recordCount));

      if (sec.residents.length) {
        box.appendChild(el('h5', 'clh-h5', 'Residents'));
        var ul = el('div', 'clh-list');
        sec.residents.forEach(function (r) {
          var item = el('button', 'clh-item clh-res-' + r.status);
          item.type = 'button';
          item.appendChild(el('span', 'clh-item-n', r.name));
          item.appendChild(el('span', 'clh-item-s', r.status));
          item.addEventListener('click', function () {
            if (typeof o.onResident === 'function') o.onResident(r.roleId);
          });
          ul.appendChild(item);
        });
        box.appendChild(ul);
      }

      var recs = (sec.records || []).slice(-8).reverse();
      if (recs.length) {
        box.appendChild(el('h5', 'clh-h5', 'Most recent records'));
        var rl = el('div', 'clh-list');
        recs.forEach(function (rec) {
          var item = el('button', 'clh-item');
          item.type = 'button';
          item.appendChild(el('span', 'clh-item-n', rec.title || rec.recordType));
          item.appendChild(el('span', 'clh-item-s', ago(rec.createdAt)));
          item.addEventListener('click', function () {
            selection = { kind: 'record', record: rec }; render();
          });
          rl.appendChild(item);
        });
        box.appendChild(rl);
      }
      return box;
    }

    function recordPanel(rec) {
      var box = el('div');
      box.appendChild(closeButton());
      box.appendChild(el('h4', 'clh-h', rec.title || rec.recordType || 'Record'));
      box.appendChild(row('Type', rec.recordType));
      box.appendChild(row('Sector', rec.sector));
      box.appendChild(row('Ant', rec.ant));
      box.appendChild(row('Mission', shortId(rec.missionId)));
      box.appendChild(row('Task', shortId(rec.taskId)));
      box.appendChild(row('Written', rec.createdAt ? (rec.createdAt + '  (' + ago(rec.createdAt) + ')') : ''));
      box.appendChild(row('Record id', shortId(rec.recordId)));
      return box;
    }

    /* §11. The decision leaves the browser, or the operator is told it did not. */
    function approvalPanel(frame) {
      var pend = frame.approvals || [];
      var box = el('div');
      box.appendChild(closeButton());
      box.appendChild(el('h4', 'clh-h', 'Approvals awaiting the Queen'));

      if (frame.meta.history) {
        box.appendChild(el('p', 'clh-note',
          'The approvals queue is a record of NOW. A reconstructed frame cannot say '
        + 'which approvals were pending at that moment, so none are shown.'));
        return box;
      }
      if (!pend.length) {
        box.appendChild(el('p', 'clh-note', 'Nothing is waiting.'));
        return box;
      }

      pend.forEach(function (a) {
        var card = el('div', 'clh-appr');
        card.appendChild(el('div', 'clh-appr-t', a.title || a.actionType || 'Approval needed'));
        card.appendChild(row('Action', a.actionType));
        card.appendChild(row('Role', a.role));
        card.appendChild(row('Task', shortId(a.taskId)));

        // The one place this view is allowed to be uncertain, said out loud.
        card.appendChild(row('Placement', a.resolved
          ? ('resolved to ' + a.sector)
          : 'unresolved — shown at the Queen, not attached to a route',
          a.resolved ? '' : 'clh-unresolved'));

        var btns = el('div', 'clh-appr-btns');
        [['approve', 'Approve'], ['reject', 'Reject']].forEach(function (pair) {
          var b = el('button', 'clh-btn clh-' + pair[0], pair[1]);
          b.type = 'button';
          b.disabled = !!busy[a.approvalId] || typeof window.doApproval !== 'function';
          if (typeof window.doApproval !== 'function') {
            b.title = 'The console action path is unavailable; this view will not pretend otherwise.';
          }
          b.addEventListener('click', function () {
            if (busy[a.approvalId]) return;
            busy[a.approvalId] = true;
            render();                                  // the button goes pending, visibly
            Promise.resolve()
              .then(function () { return window.doApproval(a.approvalId, pair[0]); })
              .catch(function (e) {
                try { console.warn('[colony-live] approval failed: ' + (e && e.message)); } catch (e2) { }
              })
              .then(function () {
                // The queue is authoritative: the card disappears when the next
                // approvals poll says it is gone, NOT because we clicked. Until
                // then the operator sees a pending control, which is the truth.
                delete busy[a.approvalId];
                render();
              });
          });
          btns.appendChild(b);
        });
        card.appendChild(btns);
        box.appendChild(card);
      });
      return box;
    }

    /* §15. What the fleet listing literally recorded — no derived status. */
    function moundPanel(frame) {
      var fleet = frame.mound;
      var box = el('div');
      box.appendChild(closeButton());
      box.appendChild(el('h4', 'clh-h', 'Micromound'));

      if (!fleet) {
        // Never asked, or the route is not mapped in this build. Distinct from
        // "asked and there are none", and said as such.
        box.appendChild(el('p', 'clh-note',
          'This colony reports no Micromound fleet. Either the module is not built '
        + 'in or the fleet listing was not readable.'));
        return box;
      }
      if (!fleet.present) {
        box.appendChild(el('p', 'clh-note', 'The fleet listing answered: no devices are enrolled.'));
        box.appendChild(row('Global stop', fleet.globalStop ? 'ENGAGED' : 'not engaged'));
        return box;
      }

      box.appendChild(row('Global stop', fleet.globalStop ? 'ENGAGED' : 'not engaged'));
      box.appendChild(row('Command path', fleet.commandPath ? 'available' : 'not available'));

      fleet.mounds.forEach(function (m) {
        box.appendChild(el('h5', 'clh-h5', m.name || m.moundId));
        box.appendChild(row('Class', TIER_LABEL[m.tier] || m.tier || '—'));
        box.appendChild(row('Enrolled', m.enrolled ? 'yes' : 'no — no public key bound'));
        box.appendChild(row('Last beat', m.lastSeen ? (m.lastSeen + '  (' + ago(m.lastSeen) + ')') : 'never'));
        box.appendChild(row('Stopped', m.stopped ? 'yes' : 'no'));
        box.appendChild(row('Quiesced', m.quiesced ? 'yes' : 'no'));
        box.appendChild(row('Charter', m.charterId ? shortId(m.charterId) : 'none — observe only'));
        box.appendChild(row('Charter expires', m.charterExpiresAt));
        box.appendChild(row('Lease expires', m.leaseExpiresAt));
        box.appendChild(row('Capabilities', (m.capabilities || []).join(', ')));

        // The wire identifiers, verbatim. The friendly class name above is for
        // reading; these are what the protocol, the store and the logs say.
        box.appendChild(row('tier', m.tier));
        box.appendChild(row('mound_id', m.moundId));
        box.appendChild(row('last_seq', m.lastSeq));
      });

      box.appendChild(el('p', 'clh-note',
        'Online/offline is decided by the colony from the beat interval and the '
      + 'configured grace. The fleet listing does not carry that verdict, so this '
      + 'panel shows the recorded fields rather than computing a second opinion.'));
      return box;
    }

    /* ── Render ────────────────────────────────────────────────────────────── */
    function renderCrumbs(frame) {
      crumbs.textContent = '';
      var parts = ['Colony'];
      var focused = can('focused') ? live.focused() : null;
      if (focused) {
        var sec = (frame.sectors || []).filter(function (s) { return s.id === focused; })[0];
        parts.push(sec ? sec.label : focused);
      }
      if (selection && selection.kind === 'record') parts.push('record');
      if (selection && selection.kind === 'mound') parts.push('mound');

      parts.forEach(function (p, i) {
        if (i) crumbs.appendChild(el('span', 'clh-sep', '›'));
        crumbs.appendChild(el('span', 'clh-crumb', p));
      });
      if (can('depth')) crumbs.appendChild(el('span', 'clh-depth', live.depth()));
    }

    function renderNotice(frame) {
      var lines = [];
      if (frame.meta.history) {
        lines.push('HISTORY — ' + frame.meta.history.at);
        lines.push('Chambers are the colony’s CURRENT sectors; only their contents are reconstructed.');
        lines.push(frame.meta.history.coverage + '.');
      } else if (!frame.meta.hydrated) {
        lines.push('Waiting for the snapshot. Events are buffered, not guessed.');
      }
      if (frame.meta.partialHistory) {
        lines.push('Partial history: older records than those held were not read.');
      }
      if (lines.length) {
        notice.textContent = '';
        lines.forEach(function (t) { notice.appendChild(el('div', null, t)); });
        notice.classList.remove('clh-hidden');
        notice.classList.toggle('clh-history', !!frame.meta.history);
      } else {
        notice.classList.add('clh-hidden');
      }
    }

    function renderPanel(frame) {
      panel.textContent = '';
      var body = null;
      if (selection && selection.kind === 'sector') body = sectorPanel(frame, selection.sectorId);
      else if (selection && selection.kind === 'record') body = recordPanel(selection.record);
      else if (selection && selection.kind === 'approval') body = approvalPanel(frame);
      else if (selection && selection.kind === 'mound') body = moundPanel(frame);

      if (!body) { panel.classList.add('clh-hidden'); return; }
      panel.appendChild(body);
      panel.classList.remove('clh-hidden');
    }

    function renderFrame(frame) {
      if (!frame) return;
      renderCrumbs(frame);
      renderNotice(frame);
      renderPanel(frame);

      var b = topo.historyBounds();
      historyBtn.classList.toggle('clh-hidden', !b.available);
      historyBtn.textContent = mode === 'history' ? 'Return to LIVE' : 'History';

      if (descendBtn) {
        var hasMound = !!(frame.mound && frame.mound.present);
        descendBtn.classList.toggle('clh-hidden', !hasMound);
      }

      if (apprBtn) {
        // History has no approvals to answer, so the control goes away with the
        // queue rather than offering a decision about a moment that has passed.
        var pending = frame.meta.history ? 0 : (frame.approvals || []).length;
        apprBtn.classList.toggle('clh-hidden', pending === 0);
        apprBtn.textContent = 'Approvals ' + pending;
      }

      if (mode === 'history' && stamp) {
        stamp.textContent = frame.meta.history
          ? (frame.meta.history.recordsShown + ' of ' + frame.meta.history.recordsHeld + ' held records')
          : '';
        coverage.textContent = b.from ? ('recorded span: ' + b.from + '  →  ' + b.to) : '';
      }
    }

    function render() { renderFrame(mode === 'history' && historyAt ? topo.historyAt(historyAt) : scene); }

    /* ── The scene feed ────────────────────────────────────────────────────── */
    function setScene(s) {
      scene = s;
      // A new live scene must not yank the operator out of a reconstruction.
      // It updates what LIVE would show; the frame on screen stays historical
      // until they return to it.
      if (mode === 'history') { renderFrame(topo.historyAt(historyAt)); return; }
      renderFrame(s);
    }

    buildControls();
    buildTimeline();

    return {
      setScene: setScene,
      /** The renderer's selection events land here — sector clicks, resident clicks. */
      selectSector: function (id) { selection = { kind: 'sector', sectorId: id }; render(); },
      showApprovals: function () { selection = { kind: 'approval' }; render(); },
      mode: function () { return mode; },
      destroy: function () { if (root.parentNode) root.parentNode.removeChild(root); }
    };
  }

  window.ColonyHud = { create: create };
})();
