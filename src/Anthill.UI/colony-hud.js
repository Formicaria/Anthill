/* ─────────────────────────────────────────────────────────────────────────────
   COLONY LIVE — the operator surface. v0.3.8.116.

   Breadcrumbs, the semantic-zoom controls, the inspectors, the active-work strip,
   growth playback and the Micromound descent.

   Each file in the feature has exactly one job, and none of them overlap:

     colony-topology.js   what is TRUE      (no fetch, no drawing)
     colony-renderer.js   how it LOOKS      (WebGL; no state decisions)
     colony-live.js       which renderer    (WebGL, or the classic 2D fallback)
     colony-host.js       wiring and I/O    (the only file that fetches)
     colony-hud.js        what the operator sees and presses   (this file)

   ── PORTED FROM THE DESIGN, MINUS WHAT THE COLONY CANNOT SUPPORT ────────────
   The chrome, the five zoom levels, the inspector field lists and the control
   bar are the design's. Three of its fields are NOT, and each is named where it
   would have appeared rather than filled in with something plausible:

   · A RECORD HAS NO PHEROMONE SCORE. The design meters one per record. Anthill
     keys trails to `worker:{id}` — reputation belongs to an ANT, not to a stored
     fact — so the meter lives in the ant inspector, where a real number backs it,
     and the record inspector says so instead of drawing a bar.

   · VERIFICATION COMES FROM THE EVIDENCE TABLE, or admits it does not. A record
     nothing ever judged is `not recorded`, which is the ordinary case and is not
     the same claim as `refused`. Both render, differently.

   · THERE IS NO MISSION CLOCK. The design advances `elapsed` on a 120 ms interval
     and derives the current step from it. Nothing here runs on a timer: the strip
     reads task STATUS from the graph the console already polls, so work is shown
     as current because a task is running — not because a clock said so. A guard
     forbids `setInterval` in this file.

   ── THE STANDING RULES ──────────────────────────────────────────────────────
   1. Only what the scene contains. Absent facts read "—" or name what is missing.
   2. No colony state lives here. View state does; colony state is asked of the
      topology, every time.
   3. Actions go through the console's existing authenticated path — `doApproval`
      is app.js's, hitting the same routes the approvals card does.
   4. No inline handlers; the console runs under `script-src 'self'`.
   ───────────────────────────────────────────────────────────────────────────── */
(function () {
  'use strict';

  var TIER_LABEL = {
    // Operator-facing name for the wire value. The identifier is never hidden — it
    // is printed below, because an operator reading PROTOCOL.md needs the string
    // the protocol actually uses.
    edge_queen: 'Mound Major',
    deterministic_controller: 'Deterministic Controller'
  };

  /** The design's five levels, against this renderer's depths. */
  var LEVEL = {
    survey:   'L0 COLONY SURVEY',
    approach: 'L1 SECTOR APPROACH',
    inside:   'L2 INSIDE SPHERE',
    cluster:  'L3 CONTEXT CLUSTER',
    record:   'L4 RECORD'
  };

  /** Verification in the operator's words, with a colour that does not overclaim. */
  var VERDICT = {
    verified:       { text: 'verified — deterministic evidence passed', cls: 'clh-ok' },
    refused:        { text: 'refused — evidence did not pass',          cls: 'clh-bad' },
    not_recorded:   { text: 'not recorded — nothing judged this',       cls: '' },
    not_scanned:    { text: 'not read — arrived on the stream',         cls: '' },
    not_applicable: { text: 'no task to judge',                         cls: '' }
  };

  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text !== undefined && text !== null) n.textContent = String(text);
    return n;
  }

  /** Real elapsed time from a recorded timestamp. A measurement, never an estimate. */
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
    return s.length > 16 ? s.slice(0, 16) + '…' : s;
  }

  function create(o) {
    var mount = o.mount, live = o.live, topo = o.topo;
    var scene = null;                 // the last scene handed in
    var mode = 'live';                // 'live' | 'history'
    var historyAt = null;             // ISO string while scrubbing
    var selection = null;             // {kind:'sector'|'record'|'ant'|'approval'|'mound', …}
    var busy = Object.create(null);   // approvalId -> decision in flight

    var root = el('div', 'clh');
    var crumbs = el('div', 'clh-crumbs');
    var notice = el('div', 'clh-notice clh-hidden');
    var panel = el('div', 'clh-panel clh-hidden');
    var strip = el('div', 'clh-strip clh-hidden');
    var bar = el('div', 'clh-bar');
    var pop = el('div', 'clh-pop clh-hidden');
    var timeline = el('div', 'clh-timeline clh-hidden');

    /* `data-chrome-avoid` is the renderer's contract for "a panel sits here".
       Its DOM label pass reads these rectangles and places a chamber's name on
       whichever side is clear, which is the difference between a label an
       operator can read and one printed underneath the inspector. Marked at
       creation rather than in the stylesheet so a panel added later cannot
       forget: every direct child of the HUD root is chrome by definition. */
    [crumbs, notice, panel, strip, bar, pop, timeline].forEach(function (n) {
      n.setAttribute('data-chrome-avoid', '');
      root.appendChild(n);
    });
    mount.appendChild(root);

    /* v0.3.8.117: THE CONTROL BAR BELONGS IN THE CONSOLE'S OWN BAR. It floated bottom-right, so the
       colony page carried two rows of controls in two corners — the viewbar's (motion, labels,
       pheromones, zoom, reset) and this one's (survey, mission, memory, mounds, history, view) —
       with nothing to say from looking which row owned what.

       Given a `barMount` by the host, it re-parents into it and drops its own chrome so the buttons
       join that row rather than starting a second one. It stays the HUD's in every other respect:
       `destroy()` removes it explicitly, because it is no longer inside `root` and would otherwise
       outlive the view that owns it. */
    if (o.barMount) {
      bar.classList.add('clh-bar-inline');
      bar.removeAttribute('data-chrome-avoid');   // chrome the viewbar already declares for itself
      o.barMount.appendChild(bar);
    }

    function can(name) { return live && typeof live[name] === 'function'; }
    function depth() { return can('depth') ? live.depth() : 'survey'; }
    function focused() { return can('focused') ? live.focused() : null; }

    /* ── Breadcrumbs and level ─────────────────────────────────────────────── */
    function renderCrumbs(frame) {
      crumbs.textContent = '';
      var d = depth(), f = focused();
      var parts = ['COLONY'];
      if (f) {
        var sec = (frame.sectors || []).filter(function (s) { return s.id === f; })[0];
        parts.push((sec ? sec.label : f).toUpperCase());
      }
      if (selection && selection.kind === 'record' && selection.record.cluster)
        parts.push(String(selection.record.cluster).replace(/_/g, ' ').toUpperCase());

      parts.forEach(function (p, i) {
        if (i) crumbs.appendChild(el('span', 'clh-sep', '/'));
        crumbs.appendChild(el('span', i === parts.length - 1 ? 'clh-crumb clh-crumb-on' : 'clh-crumb', p));
      });

      var lvl = (selection && selection.kind === 'record') ? LEVEL.record : (LEVEL[d] || LEVEL.survey);
      crumbs.appendChild(el('span', 'clh-level', lvl));

      // BACK exists only when there is somewhere to go back to.
      if (d !== 'survey' || f || selection) {
        var b = el('button', 'clh-btn clh-back', 'BACK');
        b.type = 'button';
        b.addEventListener('click', back);
        crumbs.appendChild(b);
      }
    }

    function back() {
      var d = depth(), f = focused();
      if (selection && (selection.kind === 'record' || selection.kind === 'ant')) { selection = null; render(); return; }
      if (f && (d === 'cluster' || d === 'inside')) { live.focus(f); selection = null; render(); return; }
      if (can('survey')) live.survey();
      selection = null; render();
    }

    /* ── The control bar ───────────────────────────────────────────────────── */
    var moundBtn = null, historyBtn = null, apprBtn = null;

    function barButton(label, title, fn) {
      var b = el('button', 'clh-btn', label);
      b.type = 'button'; b.title = title;
      b.setAttribute('aria-label', title);
      b.addEventListener('click', fn);
      bar.appendChild(b);
      return b;
    }

    function buildBar() {
      barButton('SURVEY', 'Pull back to the whole colony', function () {
        if (can('survey')) live.survey();
        selection = null; render();
      });

      // MISSION — the chambers the persisted task edges actually touch, or nothing.
      barButton('MISSION', 'Frame the recorded mission route', function () {
        if (can('followMission')) live.followMission();
        selection = null; render();
      });

      barButton('MEMORY', 'Go to the memory chamber', function () {
        if (can('focus')) live.focus('memory');
        selection = { kind: 'sector', sectorId: 'memory' }; render();
      });

      // MOUNDS — offered only once the fleet listing has returned a device.
      moundBtn = barButton('MOUNDS', 'Travel the authority conduit to the enrolled device', function () {
        if (can('descend') && !live.descend()) return;
        selection = { kind: 'mound' }; render();
      });
      moundBtn.classList.add('clh-hidden');

      apprBtn = barButton('APPROVALS', 'Approvals awaiting the Queen', function () {
        selection = { kind: 'approval' }; render();
      });
      apprBtn.classList.add('clh-hidden');

      historyBtn = barButton('HISTORY', 'Reconstruct the colony from its persisted records', function () {
        if (mode === 'history') { toLive(); return; }
        var b = topo.historyBounds();
        if (!b.available) return;
        mode = 'history'; historyAt = b.to;
        timeline.classList.remove('clh-hidden');
        applyHistory();
      });
      historyBtn.classList.add('clh-hidden');

      bar.appendChild(el('span', 'clh-bar-sep'));

      var v = el('button', 'clh-btn', 'VIEW ⌄');
      v.type = 'button';
      v.addEventListener('click', function () { pop.classList.toggle('clh-hidden'); });
      bar.appendChild(v);
      buildPopover();
    }

    /* VIEW holds only what the 3D view alone can do. Motion, Labels and Pheromones
       belong to `#colony-viewbar`, which already owns them and already pushes them
       through `ColonyHost.setOptions` — a second copy here is how the console once
       showed each control twice, in two bars that could disagree. */
    function buildPopover() {
      pop.appendChild(el('div', 'clh-pop-h', 'VIEW'));

      function act(label, fn) {
        var b = el('button', 'clh-pop-row', label);
        b.type = 'button';
        b.addEventListener('click', function () { fn(); pop.classList.add('clh-hidden'); render(); });
        pop.appendChild(b);
      }
      if (can('resetView')) act('Reset camera', function () { live.resetView(); });
      if (can('resetLayout')) act('Reset chamber layout', function () { live.resetLayout(); });

      pop.appendChild(el('p', 'clh-note',
        'Drag to orbit · shift-drag to pan · wheel to zoom · alt-drag a chamber to move it. '
      + 'A moved chamber persists to your account. Motion, labels and pheromone trails are on '
      + 'the colony view bar.'));
    }

    /* ── §14 growth playback ───────────────────────────────────────────────── */
    var scrub = null, stamp = null, coverage = null;

    function buildTimeline() {
      var liveBtn = el('button', 'clh-btn clh-live', 'RETURN TO LIVE');
      liveBtn.type = 'button';
      liveBtn.addEventListener('click', toLive);

      scrub = el('input', 'clh-scrub');
      scrub.type = 'range'; scrub.min = '0'; scrub.max = '1000'; scrub.value = '1000'; scrub.step = '1';
      scrub.setAttribute('aria-label', 'Reconstruct the colony at a point in its recorded history');
      scrub.addEventListener('input', function () {
        var b = topo.historyBounds();
        if (!b.available) return;
        var lo = Date.parse(b.from), hi = Date.parse(b.to);
        if (!isFinite(lo) || !isFinite(hi)) return;
        historyAt = new Date(lo + (hi - lo) * (Number(scrub.value) / 1000)).toISOString();
        applyHistory();
      });

      stamp = el('div', 'clh-stamp');
      coverage = el('div', 'clh-coverage');
      [liveBtn, scrub, stamp, coverage].forEach(function (n) { timeline.appendChild(n); });
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

    /* ── Inspector plumbing ────────────────────────────────────────────────── */
    function closeButton() {
      var b = el('button', 'clh-x', '✕');
      b.type = 'button'; b.title = 'Close';
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

    /** A 0–1 meter. Drawn only where a real number backs it. */
    function meter(value) {
      var wrap = el('div', 'clh-meter');
      var fill = el('div', 'clh-meter-f');
      fill.style.width = Math.max(0, Math.min(1, value)) * 100 + '%';
      wrap.appendChild(fill);
      return wrap;
    }

    /* ── Chamber inspector ─────────────────────────────────────────────────── */
    function sectorPanel(frame, sectorId) {
      var sec = (frame.sectors || []).filter(function (s) { return s.id === sectorId; })[0];
      if (!sec) return null;

      var box = el('div');
      box.appendChild(closeButton());
      box.appendChild(el('div', 'clh-kick', sec.id === 'mound' ? 'CHILD COLONY' : 'COLONY CHAMBER'));
      box.appendChild(el('h4', 'clh-h', sec.label));

      box.appendChild(row('Chamber id', sec.id));
      box.appendChild(row('Records', sec.recordCount));
      box.appendChild(row('Clusters', (sec.clusters || []).length));
      box.appendChild(row('Registry roles', sec.residents.length));
      box.appendChild(row('Workers', sec.residents.reduce(function (n, r) { return n + (r.workers || []).length; }, 0)));
      box.appendChild(row('Running tasks', frame.meta.history ? 'not reconstructable' : sec.runningTasks.length));
      box.appendChild(row('Colonies', (sec.colonies || []).join(', ')));

      /* NO NAME OR COLOUR EDITOR. The design makes chamber identity editable here.
         A chamber's label and its membership come from the registry — renaming one
         in a browser would create a second name for something the server names, and
         the colour is a fixed spatial grammar operators navigate by. What IS editable
         is position, by alt-drag, and that persists to /ui/state. */

      var cl = sec.clusters || [];
      if (cl.length) {
        box.appendChild(el('h5', 'clh-h5', 'Clusters — the record kinds held here'));
        var cw = el('div', 'clh-list');
        cl.slice(0, 12).forEach(function (c) {
          var item = el('div', 'clh-item');
          item.appendChild(el('span', 'clh-item-n', c.label));
          item.appendChild(el('span', 'clh-item-s', c.count));
          cw.appendChild(item);
        });
        box.appendChild(cw);
      }

      if (sec.residents.length) {
        box.appendChild(el('h5', 'clh-h5', 'Residents'));
        var ul = el('div', 'clh-list');
        sec.residents.forEach(function (r) {
          var item = el('button', 'clh-item clh-res-' + r.status);
          item.type = 'button';
          item.appendChild(el('span', 'clh-item-n', r.name));
          item.appendChild(el('span', 'clh-item-s', r.status));
          item.addEventListener('click', function () { selection = { kind: 'ant', ant: r }; render(); });
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
          item.addEventListener('click', function () { selection = { kind: 'record', record: rec }; render(); });
          rl.appendChild(item);
        });
        box.appendChild(rl);
      }
      return box;
    }

    /* ── Record inspector ──────────────────────────────────────────────────── */
    function recordPanel(rec) {
      var box = el('div');
      box.appendChild(closeButton());
      box.appendChild(el('div', 'clh-kick', String(rec.recordType || 'record').replace(/_/g, ' ')));
      box.appendChild(el('h4', 'clh-h', rec.title || rec.recordType || 'Record'));

      box.appendChild(row('Record type', rec.recordType));
      box.appendChild(row('Sector', rec.sector));
      box.appendChild(row('Cluster', String(rec.cluster || '').replace(/_/g, ' ')));
      box.appendChild(row('Mission', shortId(rec.missionId)));
      box.appendChild(row('Source ant', rec.ant));
      box.appendChild(row('Recorded', rec.createdAt ? (rec.createdAt + '  (' + ago(rec.createdAt) + ')') : ''));

      var v = VERDICT[rec.verification] || VERDICT.not_recorded;
      box.appendChild(row('Verification', v.text, v.cls));

      /* NO PHEROMONE METER HERE. The design shows one per record; Anthill measures
         reputation per WORKER (`worker:{id}`), never per stored fact. */
      box.appendChild(el('p', 'clh-note',
        'Records carry no pheromone score in this colony — reputation is measured per ant, '
      + 'and is shown in the ant inspector.'));

      box.appendChild(el('h5', 'clh-h5', 'Technical'));
      box.appendChild(row('record_id', shortId(rec.recordId)));
      box.appendChild(row('task_id', shortId(rec.taskId)));
      box.appendChild(row('event_type', rec.recordType));
      return box;
    }

    /* ── Ant inspector ─────────────────────────────────────────────────────── */
    function antPanel(ant) {
      var box = el('div');
      box.appendChild(closeButton());
      box.appendChild(el('div', 'clh-kick', ant.roleId === 'queen' ? 'COLONY AUTHORITY' : 'REGISTRY ROLE'));
      box.appendChild(el('h4', 'clh-h', ant.name || ant.roleId));

      box.appendChild(row('Role id', ant.roleId));
      box.appendChild(row('Colony', ant.colony));
      box.appendChild(row('Status', ant.status));
      box.appendChild(row('Enabled', ant.enabled ? 'yes' : 'no'));
      box.appendChild(row('Executable', ant.executable ? 'yes' : 'no — never runs'));
      box.appendChild(row('Workers', (ant.workers || []).length));

      /* THE ONE REAL REPUTATION NUMBER IN THE VIEW, summed over this role's workers
         from `pheromone_trails`. Null is not zero: a role whose workers have never
         run has no trail, and printing "0.000" would be a verdict nobody reached. */
      box.appendChild(el('h5', 'clh-h5', 'Pheromone trail'));
      if (ant.trail) {
        box.appendChild(row('Strength', ant.trail.strength.toFixed(3)));
        box.appendChild(meter(ant.trail.strength));
        box.appendChild(row('Successes', ant.trail.successes));
        box.appendChild(row('Failures', ant.trail.failures));
        box.appendChild(row('Workers with a trail', ant.trail.workers + ' of ' + (ant.workers || []).length));
      } else {
        box.appendChild(el('p', 'clh-note',
          'No trail recorded. None of this role’s workers has run, so the colony has formed '
        + 'no reputation — which is not the same as a strength of zero.'));
      }

      if ((ant.workers || []).length) {
        box.appendChild(el('h5', 'clh-h5', 'Workers'));
        var wl = el('div', 'clh-list');
        ant.workers.forEach(function (w) {
          var item = el('div', 'clh-item');
          /* BOTH NAMES, because they answer different questions. The display name is
             what an operator calls the ant; the id is what an event's `ant_name`
             carries, so it is what someone reading a record or a log is matching on.
             Showing only the id was the defect this replaced; showing only the name
             would strand anyone holding a log line. */
          item.appendChild(el('span', 'clh-item-n', w.name || w.id));
          item.appendChild(el('span', 'clh-item-s', w.id));
          if (w.enabled === false) item.classList.add('clh-res-disabled');
          wl.appendChild(item);
        });
        box.appendChild(wl);
      }
      return box;
    }

    /* ── §11 approvals ─────────────────────────────────────────────────────── */
    function approvalPanel(frame) {
      var pend = frame.approvals || [];
      var box = el('div');
      box.appendChild(closeButton());
      box.appendChild(el('div', 'clh-kick', 'APPROVAL BOUNDARY'));
      box.appendChild(el('h4', 'clh-h', 'Awaiting the Queen'));

      if (frame.meta.history) {
        box.appendChild(el('p', 'clh-note',
          'The approvals queue is a record of NOW. A reconstructed frame cannot say which '
        + 'approvals were pending at that moment, so none are shown.'));
        return box;
      }
      if (!pend.length) { box.appendChild(el('p', 'clh-note', 'Nothing is waiting.')); return box; }

      pend.forEach(function (a) {
        var card = el('div', 'clh-appr');
        card.appendChild(el('div', 'clh-appr-t', a.title || a.actionType || 'Approval needed'));
        card.appendChild(row('Action', a.actionType));
        card.appendChild(row('Role', a.role));
        card.appendChild(row('Task', shortId(a.taskId)));
        card.appendChild(row('Placement', a.resolved
          ? ('resolved to ' + a.sector)
          : 'unresolved — shown at the Queen, not attached to a route',
          a.resolved ? '' : 'clh-unresolved'));

        var btns = el('div', 'clh-appr-btns');
        [['approve', 'APPROVE'], ['reject', 'REJECT']].forEach(function (pair) {
          var b = el('button', 'clh-btn clh-' + pair[0], pair[1]);
          b.type = 'button';
          b.disabled = !!busy[a.approvalId] || typeof window.doApproval !== 'function';
          if (typeof window.doApproval !== 'function')
            b.title = 'The console action path is unavailable; this view will not pretend otherwise.';
          b.addEventListener('click', function () {
            if (busy[a.approvalId]) return;
            busy[a.approvalId] = true;
            render();                                   // the control goes pending, visibly
            Promise.resolve()
              .then(function () { return window.doApproval(a.approvalId, pair[0]); })
              .catch(function (e) {
                try { console.warn('[colony-live] approval failed: ' + (e && e.message)); } catch (e2) { }
              })
              .then(function () {
                // The queue is authoritative: the card clears when the next poll says
                // it is gone, NOT because the button was pressed.
                delete busy[a.approvalId]; render();
              });
          });
          btns.appendChild(b);
        });
        card.appendChild(btns);
        box.appendChild(card);
      });
      return box;
    }

    /* ── §15 Micromound ────────────────────────────────────────────────────── */
    function moundPanel(frame) {
      var fleet = frame.mound;
      var box = el('div');
      box.appendChild(closeButton());
      box.appendChild(el('div', 'clh-kick', 'MICROMOUND'));
      box.appendChild(el('h4', 'clh-h', 'Physical devices'));

      if (!fleet) {
        box.appendChild(el('p', 'clh-note',
          'This colony reports no Micromound fleet. Either the module is not built in, or the '
        + 'fleet listing was not readable.'));
        return box;
      }
      box.appendChild(row('Global stop', fleet.globalStop ? 'ENGAGED' : 'not engaged'));
      box.appendChild(row('Command path', fleet.commandPath ? 'available' : 'not available'));
      if (!fleet.present) {
        box.appendChild(el('p', 'clh-note', 'The fleet listing answered: no devices are enrolled.'));
        return box;
      }

      fleet.mounds.forEach(function (m) {
        box.appendChild(el('h5', 'clh-h5', m.name || m.moundId));
        box.appendChild(row('Status', m.status || '—'));
        box.appendChild(row('Class', TIER_LABEL[m.tier] || m.tier || '—'));
        box.appendChild(row('Enrolled', m.enrolled ? 'yes' : 'no — no public key bound'));
        box.appendChild(row('Last beat', m.lastSeen ? (m.lastSeen + '  (' + ago(m.lastSeen) + ')') : 'never'));
        box.appendChild(row('Stopped', m.stopped ? 'yes' : 'no'));
        box.appendChild(row('Quiesced', m.quiesced ? 'yes' : 'no'));
        /* WAITING TO BE COLLECTED, not delivered. The colony never dials a mound;
           everything issued lands in a downlink queue the device drains on its
           next beat, and the count is the honest way to say so. */
        box.appendChild(row('Awaiting collection',
          m.pendingDownlink === null || m.pendingDownlink === undefined
            ? 'not reported'
            : m.pendingDownlink + (m.pendingDownlink === 1 ? ' item' : ' items')));
        box.appendChild(row('Charter', m.charterId ? shortId(m.charterId) : 'none — observe only'));
        box.appendChild(row('Lease expires', m.leaseExpiresAt));
        box.appendChild(row('Capabilities', (m.capabilities || []).join(', ')));
        box.appendChild(row('tier', m.tier));
        box.appendChild(row('mound_id', m.moundId));

        /* PER-MOUND STOP AND RESUME, and nothing else that mutates.
           This is the one control that has to be reachable from wherever the
           operator happens to be looking, because the reason to reach for it is
           that something is going wrong right now. It posts through the host —
           the only file in this feature that touches the network — and the host
           re-reads the fleet, so the panel reports the colony's answer rather
           than assuming its own request succeeded.

           The GLOBAL stop is deliberately absent, here as in micromound.js: it
           is a file on disk precisely so that no API flow can clear it, and a
           button that appeared to would teach an operator the opposite. */
        var btns = el('div', 'clh-appr-btns');
        var stopBtn = el('button', 'clh-btn clh-' + (m.stopped ? 'approve' : 'reject'),
                         m.stopped ? 'RESUME MOUND' : 'STOP THIS MOUND');
        stopBtn.type = 'button';
        stopBtn.disabled = !o.onMoundStop || !!busy[m.moundId];
        stopBtn.title = m.stopped
          ? 'Clear this device’s stop. Its next sync carries the change.'
          : 'Stop this device. Its next sync carries the stop order; nothing is dialled out to it.';
        stopBtn.addEventListener('click', function () {
          if (!o.onMoundStop || busy[m.moundId]) return;
          busy[m.moundId] = true; render();
          Promise.resolve(o.onMoundStop(m.moundId, !m.stopped))
            .catch(function () { })
            .then(function () { delete busy[m.moundId]; render(); });
        });
        btns.appendChild(stopBtn);
        box.appendChild(btns);
      });

      /* EVERYTHING ELSE AN OPERATOR CAN DO TO A DEVICE ALREADY EXISTS, on the
         Micromound console: mint and unlink, charters, manifests, mission
         dispatch, the evidence feed and the resolver. Rebuilding any of it here
         would be a second implementation of a form whose vocabulary is a closed
         PROTOCOL set — the exact shape of defect class 5, and the one place it
         would hurt most, since the two copies would disagree the first time the
         protocol gained a value. So this hands over instead. */
      var console_ = el('button', 'clh-btn', 'OPEN MICROMOUND CONSOLE');
      console_.type = 'button';
      console_.title = 'Charters, manifests, mission dispatch, enrollment and the evidence feed';
      console_.disabled = typeof window.go !== 'function';
      if (console_.disabled)
        console_.title = 'The console router is unavailable; this view will not pretend otherwise.';
      console_.addEventListener('click', function () {
        if (typeof window.go === 'function') window.go('/tools/micromound');
      });
      box.appendChild(console_);

      box.appendChild(el('p', 'clh-note',
        'Stop always wins, and the colony never dials a mound — a stop, like every other order, is '
      + 'collected on the device’s next beat. Online/offline is the colony’s verdict, computed from '
      + 'the beat interval and the configured grace; this panel shows the recorded fields.'));
      return box;
    }

    /* ── The active-work strip ─────────────────────────────────────────────── */
    function renderStrip(frame) {
      strip.textContent = '';
      if (frame.meta.history) { strip.classList.add('clh-hidden'); return; }

      // WHAT IS RUNNING, from task status. Not a step index derived from a clock.
      var running = [];
      (frame.sectors || []).forEach(function (s) {
        (s.runningTasks || []).forEach(function (t) { running.push({ sector: s, task: t }); });
      });

      var pend = (frame.approvals || []).length;
      if (!running.length && !pend) { strip.classList.add('clh-hidden'); return; }

      strip.appendChild(el('div', 'clh-kick', 'ACTIVE WORK'));

      if (running.length) {
        strip.appendChild(el('div', 'clh-strip-id', shortId(running[0].task.missionId || '')));

        /* The segments are the CHAMBERS currently holding a running task — not a
           seven-step plan with a progress bar. The colony knows what is running; it
           publishes no step count, and drawing one would invent the mission's shape. */
        var segs = el('div', 'clh-segs');
        running.slice(0, 12).forEach(function (r) {
          var seg = el('button', 'clh-seg');
          seg.type = 'button';
          seg.title = r.sector.label + ' · task ' + shortId(r.task.taskId);
          seg.addEventListener('click', function () {
            if (can('focus')) live.focus(r.sector.id);
            selection = { kind: 'sector', sectorId: r.sector.id }; render();
          });
          segs.appendChild(seg);
        });
        strip.appendChild(segs);

        var chambers = {};
        running.forEach(function (r) { chambers[r.sector.id] = true; });
        strip.appendChild(el('div', 'clh-strip-t',
          running.length + ' task' + (running.length === 1 ? '' : 's') + ' running in '
          + Object.keys(chambers).length + ' chamber' + (Object.keys(chambers).length === 1 ? '' : 's')));
      } else {
        strip.appendChild(el('div', 'clh-strip-t', 'No task is running.'));
      }

      if (pend) {
        var warn = el('div', 'clh-strip-appr');
        warn.appendChild(el('span', null, pend + ' awaiting a decision — the colony stops here'));
        var go = el('button', 'clh-btn clh-approve', 'REVIEW');
        go.type = 'button';
        go.addEventListener('click', function () { selection = { kind: 'approval' }; render(); });
        warn.appendChild(go);
        strip.appendChild(warn);
      }
      strip.classList.remove('clh-hidden');
    }

    /* ── Render ────────────────────────────────────────────────────────────── */
    function renderNotice(frame) {
      var lines = [];
      if (frame.meta.history) {
        lines.push('HISTORY — ' + frame.meta.history.at);
        lines.push('Chambers are the colony’s CURRENT sectors; only their contents are reconstructed.');
        lines.push(frame.meta.history.coverage + '.');
      } else if (!frame.meta.hydrated) {
        lines.push('Waiting for the snapshot. Events are buffered, not guessed.');
      }
      if (frame.meta.partialHistory) lines.push('Partial history: older records than those held were not read.');

      if (!lines.length) { notice.classList.add('clh-hidden'); return; }
      notice.textContent = '';
      lines.forEach(function (t) { notice.appendChild(el('div', null, t)); });
      notice.classList.remove('clh-hidden');
      notice.classList.toggle('clh-history', !!frame.meta.history);
    }

    function renderPanel(frame) {
      panel.textContent = '';
      var body = null;
      if (selection && selection.kind === 'sector') body = sectorPanel(frame, selection.sectorId);
      else if (selection && selection.kind === 'record') body = recordPanel(selection.record);
      else if (selection && selection.kind === 'ant') body = antPanel(selection.ant);
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
      renderStrip(frame);

      var b = topo.historyBounds();
      historyBtn.classList.toggle('clh-hidden', !b.available);
      historyBtn.textContent = mode === 'history' ? 'LIVE' : 'HISTORY';
      if (moundBtn) moundBtn.classList.toggle('clh-hidden', !(frame.mound && frame.mound.present));

      var pending = frame.meta.history ? 0 : (frame.approvals || []).length;
      apprBtn.classList.toggle('clh-hidden', pending === 0);
      apprBtn.textContent = 'APPROVALS ' + pending;

      if (mode === 'history' && stamp) {
        stamp.textContent = frame.meta.history
          ? (frame.meta.history.recordsShown + ' of ' + frame.meta.history.recordsHeld + ' held records')
          : '';
        coverage.textContent = b.from ? (b.from + '  →  ' + b.to) : '';
      }
    }

    function render() { renderFrame(mode === 'history' && historyAt ? topo.historyAt(historyAt) : scene); }

    function setScene(s) {
      scene = s;
      // A new live scene must not yank the operator out of a reconstruction.
      if (mode === 'history') { renderFrame(topo.historyAt(historyAt)); return; }
      renderFrame(s);
    }

    buildBar();
    buildTimeline();

    return {
      setScene: setScene,
      /* CLICKING THE MICROMOUND OPENS THE MICROMOUND, not a generic chamber card.
         Every other chamber is a group of registry roles and the chamber inspector
         is the right panel for it. The mound is a PHYSICAL DEVICE with a stop, a
         charter, a lease and an enrollment — a card listing "registry roles: 0"
         answers none of the questions an operator opens it to ask. */
      selectSector: function (id) {
        selection = !id ? null
          : id === 'mound' ? { kind: 'mound' }
          : { kind: 'sector', sectorId: id };
        render();
      },
      selectRecord: function (rec) { selection = { kind: 'record', record: rec }; render(); },
      selectAnt: function (ant) { selection = { kind: 'ant', ant: ant }; render(); },
      showApprovals: function () { selection = { kind: 'approval' }; render(); },
      onDepth: function () { render(); },
      mode: function () { return mode; },
      destroy: function () {
        // The bar may live in #colony-viewbar rather than under `root`, so it needs removing
        // by hand — otherwise toggling 3D off leaves a dead row of buttons in the viewbar.
        if (bar && bar.parentNode) bar.parentNode.removeChild(bar);
        if (root.parentNode) root.parentNode.removeChild(root);
      }
    };
  }

  window.ColonyHud = { create: create };
})();
