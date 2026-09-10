/* ─────────────────────────────────────────────────────────────────────────────
   COLONY HOME — the landing page around Colony Live (design doc §17, stage 4).

   Drives three things on #page-colony and nothing else:
     1. FOCUS MODE   body.colony-focus — the stage is the page: nav rail, header and both side
                     columns fold away. The landing default; "Console" brings the chrome back.
                     Scoped in CSS with :has(#page-colony.active), so leaving the page restores
                     the chrome without this file having to know about navigation.
     2. THE LIVE BAR mission line (from the SAME state app.js already polls — lastGraphData,
                     colonyRunning, the header's mission goal, the approvals badge), the needs-you
                     chip, and the view buttons that call ColonyLive's public API.
     3. THE COMPOSER a doorway, not a second pipeline. §3 still holds: Chat is the one mission
                     entry. This resolves WHERE the conversation lives (a project, a new project,
                     or the Questions project for a plain question), sets the hand-off state Chat
                     already honours (chatPendingProjectId / chatComposingNew), navigates to Chat
                     and calls chatSend(mode). Streaming, refusals, attachments, policy — all Chat's.

   Boundary rule: no fetch that app.js does not already make, except the project list the scope
   menu needs (GET /projects, the same call the Chat picker makes) and POST /projects when the
   operator names a new one (again, the picker's own call).

   Globals it reaches (classic scripts share one global lexical scope; this loads after app.js,
   the Colony Live assets and the host, and touches them only at event time): go, api,
   apiCacheBust, chatSend, chatPendingProjectId, chatActiveId, chatComposingNew, lastGraphData,
   colonyRunning, setColonyPref, nodes, showInspector, PAGE_ENTER, and window.ColonyHost — the
   one door to the renderer (ColonyHost.live()) and to its scene (ColonyHost.onScene).
   No timer: the bar refreshes when the scene does.
   ───────────────────────────────────────────────────────────────────────────── */
(function () {
  'use strict';
  var FOCUS_KEY = 'anthill.colony.focus';
  var QUESTIONS_PROJECT = 'Questions';
  var $ = function (id) { return document.getElementById(id); };
  var busy = false, projectsAt = 0, projects = [];

  function liveApi() { return (window.ColonyHost && ColonyHost.live()) || null; }
  // ---- focus mode -----------------------------------------------------------------------------
  function focusOn() { return document.body.classList.contains('colony-focus'); }
  function setFocus(on) {
    document.body.classList.toggle('colony-focus', !!on);
    var b = $('clb-focus');
    if (b) { b.textContent = on ? 'Console' : 'Focus'; b.title = on ? 'Show the console around the colony' : 'Full-screen colony'; }
    try { localStorage.setItem(FOCUS_KEY, on ? '1' : '0'); } catch (e) { }
    // The canvas takes its size from its container; re-measure once layout has settled.
    requestAnimationFrame(function () { if (typeof resize === 'function') { try { resize(); } catch (e) { } } });
  }
  function initialFocus() { var v = null; try { v = localStorage.getItem(FOCUS_KEY); } catch (e) { } return v !== '0'; }

  // ---- live bar -------------------------------------------------------------------------------
  function markView(act) {
    document.querySelectorAll('#clb-views .clb-btn').forEach(function (b) { b.classList.toggle('on', b.dataset.homeact === act); });
  }
  function view(act) {
    var btn = document.querySelector('[data-homeact="' + act + '"]'); if (btn && btn.disabled) return;
    // Mounds is NAVIGATION, not a camera move, so it is answered before the renderer is consulted —
    // the registry opens whether or not the colony view has finished loading. v0.3.8.124.
    if (act === 'mounds') { go('/colony/mounds'); return; }
    var live = liveApi();
    // v0.3.8.125: with no renderer there is nothing to move. This used to fall through to the
    // classic canvas's `colonyResetView`, which was a real answer while there were two renderers;
    // now the absence of one means the failure state is on screen, and its Retry is the control
    // that matters.
    if (!live) return;
    if (act === 'survey') live.survey();
    else if (act === 'mission') live.focus('queen');
    else if (act === 'memory') live.focus('memory');
    else if (act === 'follow') live.followMission();
    else if (act === 'resetview') { live.resetView(); act = 'survey'; }
    markView(act);
  }
  var lastScene = null;
  function syncViewButtons() {
    // A view that has nowhere to go is disabled rather than silently doing nothing — which since
    // v0.3.8.124 leaves only Follow, since it is the one that still needs a running task to ride.
    var sc = lastScene;
    var running = !!(sc && (sc.sectors || []).some(function (x) { return (x.runningTasks || []).length; }));
    var b;
    /* MOUNDS IS ALWAYS AVAILABLE, BECAUSE IT NOW OPENS THE REGISTRY. v0.3.8.124.
       It used to focus the `mound` sector — the built-in fleet chamber — and was disabled whenever
       no DEVICE had enrolled. So the button beside `+ Mound` sat greyed out on a colony that already
       had mound chambers in it, which reads as broken rather than as "no device yet": the operator
       can see the chambers, and the control next to the one that made them is dead.

       The registry is the right destination anyway. It lists every mound including INFRASTRUCTURE,
       it is where settings and deletion live, and it is a page rather than a camera move — so it
       has somewhere to go whether or not a device has ever beaten. */
    if ((b = document.querySelector('[data-homeact="mounds"]'))) {
      b.disabled = false;
      b.title = 'The mound registry — every mound chamber, its settings, and where one is deleted';
    }
    // ALWAYS OFFERED (v0.3.8.122). It used to appear only when the fleet was empty, because it was
    // the door to enrolment; it now adds a chamber to the operator's own view and a fleet of six is
    // exactly when you want six of them.
    if ((b = $('clb-addmound'))) b.style.display = '';
    if ((b = document.querySelector('[data-homeact="follow"]'))) { b.disabled = !running; b.title = running ? 'Ride the active mission circuit' : 'No task is running — nothing to follow'; }
  }
  /* ── The mission this composer started, followed without leaving ─────────────────────────────
     v0.3.8.130. No timer and no fetch, because this file may have neither: the scene already
     arrives on every colony event, and `lastGraphData` is the same task list the bar above reads.
     What this adds is a FILTER — the tasks of one mission — so the chip describes the work the
     operator just asked for rather than whatever the colony happens to be doing. */
  var watching = null;

  function beginWatch(handed) {
    if (!handed || !handed.conversationId) { watching = null; renderWatch(); return; }
    // NO MISSION ID MEANS NO MISSION. The turn was refused, or it stopped at the approval gate
    // before anything was created. Saying "starting…" forever is the chip describing a mission
    // that does not exist — so it says what happened and stays clickable, because the reason is
    // in the thread and the thread is one click away.
    watching = {
      missionId: handed.missionId || null,
      conversationId: handed.conversationId,
      note: handed.note || '',
      blocked: !handed.missionId,
      ready: !handed.missionId,
    };
    renderWatch();
  }

  function watchTasks() {
    var g = (typeof lastGraphData !== 'undefined') ? lastGraphData : null;
    var all = (g && Array.isArray(g.nodes)) ? g.nodes : [];
    if (!watching || !watching.missionId) return [];
    return all.filter(function (t) { return t && t.mission_id === watching.missionId; });
  }

  function renderWatch() {
    var b = $('ccp-watch'); if (!b) return;
    if (!watching) { b.style.display = 'none'; b.className = 'ccp-watch'; return; }
    if (watching.blocked) {
      b.style.display = '';
      b.className = 'ccp-watch ready';
      b.textContent = 'no mission started · open it →';
      b.title = watching.note || 'The turn did not start a mission. Open the conversation to see why.';
      return;
    }
    var tasks = watchTasks();
    var done = tasks.filter(function (t) { return /complete/.test(t.status || ''); }).length;
    var failed = tasks.filter(function (t) { return /fail/.test(t.status || ''); }).length;
    var live = tasks.filter(function (t) { return /running|pending|queued/.test(t.status || ''); }).length;
    // Ready means every task this mission planned has reached a terminal state. An empty list is
    // NOT ready: it is a mission whose plan has not landed in the graph yet, and calling that
    // complete would hand the operator a finished-looking chip over work that never started.
    watching.ready = tasks.length > 0 && live === 0 && (done + failed) === tasks.length;
    b.style.display = '';
    b.className = 'ccp-watch' + (watching.ready ? ' ready' : '');
    b.textContent = !tasks.length ? 'mission starting…'
      : watching.ready ? ('mission ' + (failed ? 'finished with ' + failed + ' failed' : 'complete') + ' · open it →')
      : ('mission running · ' + done + '/' + tasks.length);
    b.title = watching.ready ? 'Open this mission in Chat' : 'The colony is working on it';
  }

  function refreshBar() {
    var dot = $('clb-dot'), goal = $('clb-goal'), fill = $('clb-prog-fill'), count = $('clb-count'), needs = $('clb-needs'), needsTxt = $('clb-needs-txt');
    if (!dot || !goal) return;
    syncViewButtons();
    var running = (typeof colonyRunning !== 'undefined') && !!colonyRunning;
    var hdr = $('mission-goal');
    var text = hdr ? (hdr.textContent || '').trim() : '';
    var idle = !running || !text || /^idle\b/i.test(text);
    dot.classList.toggle('on', !idle);
    goal.classList.toggle('idle', idle);
    goal.textContent = idle ? 'colony idle' : ('mission active · ' + text);
    var g = (typeof lastGraphData !== 'undefined') ? lastGraphData : null;
    var nodes = (g && Array.isArray(g.nodes)) ? g.nodes : [];
    var done = nodes.filter(function (t) { return /complete/.test(t.status || ''); }).length;
    // Progress belongs to the ACTIVE mission; an idle colony still holds the last graph, and 9/9
    // beside "colony idle" reads as a contradiction rather than a memory.
    var prog = $('clb-prog');
    if (prog) prog.style.display = idle ? 'none' : '';
    if (fill) fill.style.width = (!idle && nodes.length) ? Math.round(done / nodes.length * 100) + '%' : '0';
    if (count) count.textContent = (!idle && nodes.length) ? (done + '/' + nodes.length) : '';
    var badge = $('approval-count');
    var n = badge ? parseInt(badge.textContent, 10) || 0 : 0;
    if (needs) { needs.style.display = n > 0 ? '' : 'none'; if (needsTxt) needsTxt.textContent = n + (n === 1 ? ' needs you' : ' need you'); }
  }

  // ---- environment ----------------------------------------------------------------------------
  var ENV_KEY = 'anthill.colony.env';
  function consoleIsLight() { return document.documentElement.dataset.theme === 'light'; }
  function resolveEnv(v) { return v === 'auto' ? (consoleIsLight() ? 'light' : 'space') : v; }
  function applyEnv(v) {
    var live = liveApi();
    if (live) live.setOptions({ env: resolveEnv(v) });
    var sel = $('clb-env'); if (sel && sel.value !== v) sel.value = v;
    try { localStorage.setItem(ENV_KEY, v); } catch (e) { }
  }
  // `plane` was removed with the ground plane (v0.3.8.122) and maps to `void`: the same black field
  // without the floor. A stored value is rewritten on read so the select, the renderer and
  // localStorage agree from the first frame instead of drifting until the operator touches the menu.
  function initialEnv() {
    var v = null; try { v = localStorage.getItem(ENV_KEY); } catch (e) { }
    if (v === 'plane') { v = 'void'; try { localStorage.setItem(ENV_KEY, v); } catch (e) { } }
    return /^(auto|strata|space|light|nebula|void)$/.test(v || '') ? v : 'auto';
  }
  // Auto follows the console theme live: switching Settings › Theme to light turns the page to paper.
  if (window.MutationObserver) new window.MutationObserver(function () { if (initialEnv() === 'auto') applyEnv('auto'); }).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

  // ---- view options + sector panel --------------------------------------------------------------
  function popShow(on) { var p = $('clb-viewpop'), b = $('clb-viewbtn'); if (!p) return; p.style.display = on ? '' : 'none'; if (b) b.setAttribute('aria-expanded', on ? 'true' : 'false'); }
  var conduitAuto = true;
  function applyView() {
    var live = liveApi(), mo = $('clb-motion'), lb = $('clb-labels'), tr = $('clb-trails'), cd = $('clb-cdens'), cb = $('clb-cbright'), cc = $('clb-ccolor'), la = $('clb-linkalpha');
    // motion + trails go through app.js's validated preference path, which owns the vocabulary
    if (typeof setColonyPref === 'function') { if (mo) setColonyPref('motion', mo.value); if (tr) setColonyPref('pheromones', tr.value === 'off' ? 'off' : 'all'); }
    var conduits = { density: cd ? cd.value : 'normal', bright: cb ? Number(cb.value) : 1, color: (!conduitAuto && cc) ? cc.value : null };
    var links = { opacity: la ? Number(la.value) : .125 };
    if (live) live.setOptions({ labels: lb ? lb.value : 'all', conduits: conduits, links: links });
    var ab = document.querySelector('[data-homeact="conduitauto"]'); if (ab) ab.classList.toggle('on', conduitAuto);
    try { localStorage.setItem('anthill.colony.view', JSON.stringify({ motion: mo && mo.value, labels: lb && lb.value, trails: tr && tr.value, conduits: conduits, links: links })); } catch (e) { }
  }
  function restoreView() {
    var v = null; try { v = JSON.parse(localStorage.getItem('anthill.colony.view') || 'null'); } catch (e) { }
    if (!v) { applyView(); return; }
    var mo = $('clb-motion'), lb = $('clb-labels'), tr = $('clb-trails'), cd = $('clb-cdens'), cb = $('clb-cbright'), cc = $('clb-ccolor'), la = $('clb-linkalpha');
    if (mo && v.motion) mo.value = v.motion; if (tr && v.trails) tr.value = v.trails;
    // `normal` (.121), `fixed` and `zoom` (.122) all folded into `all` at .123 — one setting for
    // "show everything", earned by zoom. A browser remembering any of the three heals to it rather
    // than assigning a value the <select> no longer offers, which would leave the control blank.
    if (lb && v.labels) lb.value = /^(normal|fixed|zoom)$/.test(v.labels) ? 'all' : v.labels;
    if (la && v.links && isFinite(v.links.opacity)) la.value = v.links.opacity;
    if (v.conduits) { if (cd && v.conduits.density) cd.value = v.conduits.density; if (cb && v.conduits.bright) cb.value = v.conduits.bright; if (v.conduits.color) { conduitAuto = false; if (cc) cc.value = v.conduits.color; } else conduitAuto = true; }
    applyView();
  }
  /* ---- the mound registry (page-mounds) ------------------------------------------------------
     WHY THIS EXISTS RATHER THAN A SETTINGS PAGE PER MOUND. `+ Mound` adds as many chambers as an
     operator wants, so "open the micromound settings" stopped naming a destination — there is no
     single mound to settle. Clicking INTO a chamber opens that one's settings; this is the fleet,
     and deleting a chamber is a fleet-level act that belongs here rather than buried in one
     chamber's panel where you have to already be inside the thing you want to remove.

     Everything shown is a LABEL. Deleting a row removes the operator's chamber from their own view;
     an enrolled device is untouched and keeps answering under the identity its one-time token gave
     it. The row says so, because a delete button beside the word "mound" invites the other reading. */
  function renderMounds() {
    var box = $('mounds-list'); if (!box) return;
    var live = liveApi();
    if (!live || !live.listMounds) {
      box.innerHTML = '<div class="muted">The colony view has not loaded yet — open Colony › Live once, then come back.</div>';
      return;
    }
    /* A MOUND CHAMBER NOBODY HAS IS NOT A MOUND CHAMBER. v0.3.8.125.

       The built-in `mound` sector shipped in the registry whether or not a device had ever
       enrolled: a row reading "MICROMOUND · 0 ants · built in · not yours to delete", under a
       heading that says "every mound chamber in your colony". It is not one — it is the seat a
       fleet chamber will occupy once there is a fleet, and the renderer already knows that, which
       is why it draws nothing there (`s.present` is false until the snapshot reports a fleet).

       This is the same defect the `unassigned` chamber was deleted for in `.122`, one sector over:
       an empty compartment does not report a gap, it just occupies a seat and invites the reader to
       wonder what is wrong. The list now asks the same question the renderer does.

       `added` chambers are exempt: an operator who pressed `+ Mound` made a label-only chamber on
       purpose and must be able to find it in order to delete it again. */
    var list = live.listMounds().filter(function (m) { return m.present || m.removable; });
    if (!list.length) {
      box.innerHTML = '<div class="muted">No mound chambers. Use <strong>+ Mound</strong> on Colony › Live to add one.</div>';
      return;
    }
    box.innerHTML = list.map(function (m) {
      /* NOT EVERY MOUND'S SETTINGS ARE MICROMOUND SETTINGS. v0.3.8.124.

         INFRASTRUCTURE is a mound in every way this registry cares about — it is drawn as one, it
         is conduited to the Queen like one, and it belongs on the page that lists them. What it is
         NOT is a micromound: it has no device, no enrolment token and no charter, so the micromound
         console had nothing to show for it and would have offered an operator a form for hardware
         that does not exist. Its settings are the Infrastructure page, which is where its eight
         real roles have always been configured.

         So the destination is per-row rather than per-page. `settings` names where this row goes,
         the renderer decides it, and a mound that is a device goes to the device console. */
      var infra = m.id === 'infrastructure';
      return '<div class="mound-row" data-mound="' + escapeHtml(m.id) + '">'
        + '<span class="mound-dot" style="background:' + escapeHtml(m.color) + '"></span>'
        + '<span class="mound-name">' + escapeHtml(m.label) + '</span>'
        + '<span class="muted mound-facts">' + m.residents + ' ant' + (m.residents === 1 ? '' : 's')
        + (infra ? ' · built in, no device' : m.removable ? ' · label only' : ' · built in') + '</span>'
        + '<button class="btn btn-sm" data-homeact="' + (infra ? 'infraopen' : 'moundopen') + '"'
        + ' data-mound-id="' + escapeHtml(m.id) + '">Settings</button>'
        + (m.removable
          ? '<button class="btn btn-sm clb-danger" data-homeact="moundremove" data-mound-id="' + escapeHtml(m.id) + '">Delete chamber</button>'
          : '<span class="muted mound-facts">not yours to delete</span>')
        + '</div>';
    }).join('');
  }

  /* ONE MOUND'S SETTINGS, NOT "THE" MOUND SETTINGS. There can be many, so the destination has to
     carry WHICH — the console's established way of doing that is a pending id set before `go()`,
     the same shape `chatPendingProjectId` and `projectViewId` use. The Micromound console reads it
     on entry and selects that mound; absent one it opens on the fleet, which is what it always did. */
  function openMoundSettings(id) {
    try { window.micromoundPendingId = id || null; } catch (e) { }
    go('/tools/micromound');
  }

  var sectorId = null;
  function showSector(s) {
    var box = $('clb-sector'); if (!box) return;
    sectorId = s ? s.id : null;
    if (!s) { box.style.display = 'none'; return; }
    box.style.display = '';
    var dot = $('clb-sector-dot'), name = $('clb-sector-name'), facts = $('clb-sector-facts');
    if (dot) dot.style.background = s.color;
    if (name) name.value = s.label;
    var c = s.counts || {};
    if (facts) facts.textContent = (c.records ? c.records + ' record' + (c.records === 1 ? '' : 's') : 'no records') + (c.verified ? ' (' + c.verified + ' verified)' : '') + ' · ' + (c.residents || 0) + ' resident' + (c.residents === 1 ? '' : 's') + (c.running ? ' · ' + c.running + ' running' : '');
    var live = liveApi(), st = live && live.getSectorStyle(s.id);
    if (st) { var col = $('clb-sec-color'), gl = $('clb-sec-glow'), br = $('clb-sec-bright'); if (col) col.value = st.color || st.defaultColor; if (gl) gl.value = st.glow; if (br) br.value = st.bright; if (dot) dot.style.background = st.color || st.defaultColor; }
    // ONE DOOR, NOT TWO. v0.3.8.124 — the panel offered `settings →` beside `registry →`, which
    // meant two routes to a mound's configuration and a mound chamber that behaved unlike every
    // other chamber. Settings live in the registry now, for every mound including INFRASTRUCTURE,
    // and this offers the way there and nothing else.
    var isMound = live && live.isMound && live.isMound(s.id);
    var bD = $('clb-mound-registry');
    if (bD) bD.style.display = isMound ? '' : 'none';
  }
  var recordAnt = null;
  function showResident(h) {
    var box = $('clb-record'); if (!box) return;
    var res = h && h.resident; if (!res) { box.style.display = 'none'; recordAnt = null; return; }
    recordAnt = String(res.parent || res.roleId || '').toLowerCase();
    antId = res.roleId; lastResident = res;
    $('clb-record-title').textContent = res.name || res.roleId;
    // the editable half: the registry id never changes; the operator sets how it is shown here
    var live = liveApi(), edit = $('clb-ant-edit'), nm = $('clb-ant-name'), col = $('clb-ant-color');
    if (edit) edit.style.display = '';
    if (nm) { nm.value = (res.name && res.name !== res.registryName) ? res.name : ''; nm.placeholder = res.registryName || res.roleId; }
    if (col && live) { var st = live.getSectorStyle(sectorId) || {}; col.value = res.color || st.color || st.defaultColor || '#c9cfdc'; }
    // v0.3.8.130: the summary line and the status chip are HIDDEN for an ant. They said
    // "role · verifier · idle · trail 0.54 · 8✓ 5✗ · 2 workers" and "idle" — every one of which the
    // inspector below states in a labelled row, in more detail, and one of which (the chip) was the
    // same word twice on one screen. A panel that answers a question in two places has to be read
    // twice to find out the answers agree.
    //
    // Hidden and not deleted, because `showRecord` renders a TRAIL RECORD through the same two
    // elements — type, ant, mission, task, time, and a verification tag that has no equivalent
    // anywhere else in this panel. Removing the markup would have taken a record's only description
    // with it, which is the shape of fix that turns one complaint into two.
    setRecordSummary(false);
    box.style.display = '';
    // THE INSPECTOR IS HERE NOW. v0.3.8.124 moved the ant's telemetry into this panel; v0.3.8.125
    // moved the rest of it — purpose, permissions, tools, runtime facts, live task load — by
    // giving this panel its own inspector host and having app.js render the same markup into it.
    //
    // `showInspector` is app.js's, and it is reached the way it always was: colony-host.js resolves
    // the clicked resident against the roster and calls it. What this file does is make sure the
    // element is VISIBLE and, when the resident has no registry role behind it, say so — a mound's
    // ants are presentation-only, and an inspector that rendered nothing for them looked broken.
    showAntStats(res);
    showAntDetail(res);
  }

  /* ── The inspector half, for residents the roster does not contain ───────────────────────────
     v0.3.8.125. A micromound's ants are drawn from the mound roster, not from `/colony/registry`,
     so `nodes.find` in colony-host.js does not resolve them and `showInspector` is never called.
     Before this release that left the panel showing a name, a colour and nothing else, which read
     as an inspector that had failed rather than as an ant that has no registry role.

     They are still fully customizable — the name and colour above are the renderer's, and they are
     what a mound's ants have always had. This says which half applies and why, instead of leaving
     an empty panel to be interpreted. */
  function showAntDetail(res) {
    var host = $('clb-ant-detail'); if (!host) return;
    host.style.display = '';

    /* RENDERED HERE, NOT HOPED FOR. v0.3.8.126.

       `.125` left this to colony-host.js, which has its own `resident` listener that resolves the
       clicked ant against `nodes` and calls `showInspector`. Two independent listeners for one
       event, with this one's outcome depending on the other having already run and having resolved
       — and when it had not, the panel simply showed nothing, with no error and nothing to read.

       The panel that names the ant renders the ant. `showInspector` is still app.js's, and still
       the only implementation; what changed is that this asks for it rather than assuming somebody
       else did. */
    var who = String(res.roleId || '').toLowerCase();
    var n = (typeof nodes !== 'undefined')
      ? nodes.find(function (x) { return x.worker === who || x.ant === who || x.id === who; })
      : null;

    if (n && typeof showInspector === 'function') { showInspector(n); return; }

    /* A resident the roster does not contain. A micromound's ants come from the mound roster, not
       from /colony/registry, so there is no registry role behind them and nothing for the inspector
       to show — which is a fact about the ant, not a failure, and reads as a broken panel unless it
       is said. Their name and colour are the renderer's and work exactly as every other ant's do. */
    host.innerHTML = '<div class="ad-empty">This ant belongs to a mound, not to the colony '
      + 'registry — it has no role, purpose, permissions or tools of its own. Its name and colour '
      + 'are yours to set above.</div>';
  }

  /* ── The ant's own telemetry, in the panel that named it ────────────────────────────────────
     Lifetime task counts, success rate and average duration for whichever ant was clicked. This is
     what the Ant Inspector page was for; the page is gone and the question it answered is now asked
     by clicking the thing you are asking about.

     THE FETCH IS NOT HERE, AND THAT IS A RULE RATHER THAN A PREFERENCE. `ColonyLiveGuardTests`
     holds that colony-host.js is the only file in this feature that reaches the network: the host
     hydrates, the reducer and the renderer consume, and this file resolves a project for its
     composer and nothing more. So the ant tab borrows `antTelemetry` from app.js rather than
     fetching for itself.

     A WORKER HAS NO COUNTERS OF ITS OWN, and this says so rather than showing zeros. `/ants/stats`
     is keyed by ROLE; a worker's work is counted against its parent, so showing an empty card for
     `backend_coder` would read as "this worker has never run" when the truth is "the colony counts
     this under coder". */
  async function showAntStats(res) {
    var host = $('clb-ant-stats'); if (!host) return;
    var roleId = String(res.roleId || '');
    if (!roleId) { host.style.display = 'none'; return; }
    host.style.display = '';

    if (res.worker) {
      host.innerHTML = '<div class="clb-ant-note">Counted under <b>' + escapeHtml(String(res.parent || '—')) + '</b> — '
        + 'the colony records tasks against the role, not each worker.</div>';
      return;
    }

    host.innerHTML = '<div class="clb-ant-note">Reading telemetry…</div>';
    try {
      // `antTelemetry` lives in app.js, not here. colony-host.js is the only file in this feature
      // that may reach the network — the host hydrates, the view consumes — so the ant tab borrows
      // app.js's reader rather than opening a second door.
      var data = (typeof antTelemetry === 'function') ? await antTelemetry() : null;
      var s = data && (data.ants || {})[roleId];
      if (!s) {
        host.innerHTML = '<div class="clb-ant-note">No tasks recorded for this ant yet.</div>';
        return;
      }
      var total = s.total || 0, ok = s.complete || 0, fail = s.failed || 0, skip = s.skipped || 0;
      var rate = total ? Math.round(ok / total * 100) : null;
      var rateColor = rate == null ? 'var(--dim)' : rate >= 70 ? 'var(--green)' : rate >= 40 ? 'var(--queen)' : 'var(--red)';
      // The verifier's "failed" is a REJECTION, not a fault — it did its job and the answer was no.
      // The inspector drew that distinction and it survives the move.
      var failLabel = roleId === 'verifier' ? 'Reject' : 'Failed';

      host.innerHTML =
        '<div class="ac-stats">'
        + '<div class="ac-stat"><div class="n" style="color:var(--text)">' + total + '</div><div class="l">Tasks</div></div>'
        + '<div class="ac-stat"><div class="n" style="color:var(--green)">' + ok + '</div><div class="l">Done</div></div>'
        + '<div class="ac-stat"><div class="n" style="color:' + (fail ? 'var(--red)' : 'var(--dim)') + '">' + fail + '</div><div class="l">' + failLabel + '</div></div>'
        + '<div class="ac-stat"><div class="n" style="color:' + rateColor + '">' + (rate == null ? '—' : rate + '%') + '</div><div class="l">Success</div></div>'
        + '</div>'
        + '<div class="ac-bar">'
        + '<i style="width:' + (total ? ok / total * 100 : 0) + '%;background:var(--green)"></i>'
        + '<i style="width:' + (total ? fail / total * 100 : 0) + '%;background:var(--red)"></i>'
        + '<i style="width:' + (total ? skip / total * 100 : 0) + '%;background:var(--dim)"></i>'
        + '</div>'
        + '<div class="ac-sub">avg ' + (s.avg_seconds ? Number(s.avg_seconds).toFixed(1) + 's' : '—') + '/task'
        + (skip ? ' · ' + skip + ' skipped' : '') + (s.running ? ' · ' + s.running + ' running' : '') + '</div>';
      // v0.3.8.125: the per-ant event disclosure is gone. It was a SECOND request for twelve event
      // rows, and the panel below it already carries the ant's live task load — running, completed,
      // failed, and the tasks themselves. Counters and current work here; the event log lives on
      // the Events page, which is what that page is for.
    } catch (e) {
      host.innerHTML = '<div class="clb-ant-note">Telemetry unavailable: ' + escapeHtml((e && e.message) || 'unknown error') + '</div>';
    }
  }
  /** The shared summary line + verification chip: a RECORD's only description, an ant's duplicate. */
  function setRecordSummary(on) {
    var meta = $('clb-record-meta'); if (meta) meta.style.display = on ? '' : 'none';
    var tag = $('clb-record-verif');
    if (tag && tag.parentNode) tag.parentNode.style.display = on ? '' : 'none';
  }

  function showRecord(r) {
    var box = $('clb-record'); if (!box) return;
    if (!r) { box.style.display = 'none'; recordAnt = null; return; }
    var rec = r.record || {}; recordAnt = String(rec.ant || '').toLowerCase(); antId = null;
    var edit = $('clb-ant-edit'); if (edit) edit.style.display = 'none';
    var stats = $('clb-ant-stats'); if (stats) { stats.style.display = 'none'; stats.innerHTML = ''; }
    var det = $('clb-ant-detail'); if (det) det.style.display = 'none';
    setRecordSummary(true);
    $('clb-record-title').textContent = rec.title || rec.type || 'record';
    $('clb-record-meta').textContent = [rec.type, rec.ant, rec.mission && ('mission ' + String(rec.mission).slice(0, 8)), rec.taskId && ('task ' + String(rec.taskId).slice(0, 8)), rec.time].filter(Boolean).join(' · ');
    var v = rec.verif || 'not_scanned', tag = $('clb-record-verif');
    tag.textContent = v.replace(/_/g, ' ');
    tag.className = 'clb-record-tag' + (v === 'verified' ? ' ok' : v === 'refused' ? ' bad' : '');
    box.style.display = '';
  }
  function applySectorStyle() {
    var live = liveApi(); if (!live || !sectorId) return;
    var col = $('clb-sec-color'), gl = $('clb-sec-glow'), br = $('clb-sec-bright'), st = live.getSectorStyle(sectorId);
    live.setSectorStyle(sectorId, { color: (col && st && col.value !== st.defaultColor) ? col.value : null, glow: gl ? Number(gl.value) : 1, bright: br ? Number(br.value) : 1 });
    var dot = $('clb-sector-dot'); if (dot && col) dot.style.background = col.value;
  }
  // ---- the ant inspector (a resident card with a name and a colour the operator may set) ----------
  var antId = null;
  function applyAntStyle() {
    var live = liveApi(); if (!live || !antId) return;
    var nm = $('clb-ant-name'), col = $('clb-ant-color'), info = live.getSectorStyle(sectorId) || {};
    var base = info.color || info.defaultColor || '#c9cfdc';
    live.setAntStyle(antId, { name: nm ? nm.value : null, color: (col && col.value !== base) ? col.value : null });
    var t = $('clb-record-title'); if (t && nm) t.textContent = nm.value.trim() || (lastResident && lastResident.registryName) || antId;
  }
  var lastResident = null;
  function renameSector() { var live = liveApi(), name = $('clb-sector-name'); if (live && sectorId && name && name.value.trim()) { live.renameSector(sectorId, name.value.trim()); name.value = name.value.trim().toUpperCase(); name.blur(); } }

  // ---- composer -------------------------------------------------------------------------------
  function setState(text, err) { var el = $('ccp-state'); if (!el) return; el.textContent = text || ''; el.classList.toggle('err', !!err); }
  function autosize() { var t = $('ccp-input'); if (!t) return; t.style.height = 'auto'; t.style.height = Math.min(160, t.scrollHeight) + 'px'; }
  function scopeChanged() {
    var sel = $('ccp-scope'), name = $('ccp-newname'), run = $('ccp-run');
    if (!sel) return;
    var isNew = sel.value === 'new', isQ = sel.value === 'q';
    if (name) { name.style.display = isNew ? '' : 'none'; if (isNew) name.focus(); }
    // A plain question is a chat turn; running work needs a project to run IN.
    if (run) { run.disabled = isQ; run.title = isQ ? 'Pick or name a project to run work in' : 'Run — the Queen plans it and the colony carries it out (Ctrl+Enter)'; }
  }
  async function loadProjects(force) {
    var sel = $('ccp-scope'); if (!sel) return;
    if (!force && Date.now() - projectsAt < 30000) return;
    var r = await api('/projects');
    if (!r || !r.success) return;
    projectsAt = Date.now();
    projects = ((r.data && r.data.projects) || []).filter(function (p) { return !p.archived; });
    var keep = sel.value;
    Array.prototype.slice.call(sel.options).forEach(function (o) { if (o.value !== 'q' && o.value !== 'new') o.remove(); });
    if (projects.length) {
      var grp = document.createElement('optgroup'); grp.label = 'Run in project';
      projects.forEach(function (p) {
        if ((p.name || '') === QUESTIONS_PROJECT) return;   // reachable as "Just a question"
        var o = document.createElement('option'); o.value = p.id; o.textContent = p.name || 'Untitled'; grp.appendChild(o);
      });
      if (grp.children.length) sel.appendChild(grp);
    }
    if (Array.prototype.some.call(sel.options, function (o) { return o.value === keep; })) sel.value = keep;
    scopeChanged();
  }
  async function ensureQuestionsProject() {
    await loadProjects(true);
    var hit = projects.filter(function (p) { return (p.name || '') === QUESTIONS_PROJECT; })[0];
    if (hit) return hit.id;
    var c = await api('/projects', 'POST', { name: QUESTIONS_PROJECT, description_md: 'Plain questions to the colony — conversations that are not tied to a piece of work.' });
    if (c && c.success && c.data && c.data.id) { apiCacheBust('/projects'); projectsAt = 0; return c.data.id; }
    throw new Error((c && c.message) || 'Could not create the Questions project.');
  }
  async function resolveProject() {
    var sel = $('ccp-scope'), name = $('ccp-newname');
    var v = sel ? sel.value : 'q';
    if (v === 'q') return ensureQuestionsProject();
    if (v === 'new') {
      var nm = (name && name.value || '').trim();
      if (!nm) { if (name) name.focus(); throw new Error('Name the project first.'); }
      var c = await api('/projects', 'POST', { name: nm });
      if (!(c && c.success && c.data && c.data.id)) throw new Error((c && c.message) || 'Could not create the project.');
      apiCacheBust('/projects'); projectsAt = 0; if (name) name.value = '';
      return c.data.id;
    }
    return v;
  }
  async function send(mode) {
    var input = $('ccp-input'); if (!input || busy) return;
    var msg = (input.value || '').trim(); if (!msg) { input.focus(); return; }
    if (typeof chatSend !== 'function') { setState('Chat is not loaded.', true); return; }
    if (mode === 'mission' && $('ccp-scope') && $('ccp-scope').value === 'q') mode = 'chat';
    watching = null; renderWatch();   // a new send replaces the mission the chip was following
    busy = true; setState(mode === 'mission' ? 'Choosing where the work runs…' : 'Sending…');
    try {
      var pid = await resolveProject();
      // The hand-off Chat already honours: a project chosen before the conversation exists, and
      // an explicit new conversation that auto-open must not override.
      chatPendingProjectId = pid; chatActiveId = null; chatComposingNew = true;
      // v0.3.8.130 — A MISSION IS WATCHED FROM HERE; AN ANSWER IS READ OVER THERE.
      //
      // Both lanes still hand their text to Chat and neither runs a pipeline of its own — that is
      // the decision this file is held to, and it is unchanged. What changed is the NAVIGATION,
      // which was never part of it. `Ask` produces a streamed answer whose whole value is in the
      // thread, so it goes there. `Run mission` starts work that takes minutes, and sending the
      // operator away from the colony to watch a progress bar — then back to the colony to watch
      // the ants — is the back-and-forth this release exists to end.
      // v0.3.8.130 — THE GATE, CHOSEN HERE. `chatPendingPolicy` is the hand-off Chat already
      // honours for a conversation that does not exist yet (v0.3.8.53), so the composer sets it
      // rather than inventing a second policy path. Without it every mission started from the
      // colony inherited `ask`, stopped at the first side effect, and the operator had no control
      // on this screen to say otherwise — which is what "the mission never starts" actually was.
      var pol = $('ccp-policy');
      if (pol && pol.value) chatPendingPolicy = pol.value;
      var watched = (mode === 'mission');
      if (!watched) go('/chat');
      var el = $('chat-input'); if (!el) throw new Error('Chat composer missing.');
      el.value = msg; input.value = ''; autosize(); setState('');
      var handed = await chatSend(mode);
      if (watched) beginWatch(handed);
    } catch (e) {
      setState((e && e.message) || 'Could not send.', true);
    } finally { busy = false; }
  }

  // The renderer announces focus changes; the sector panel follows them. The host fires onLive
  // with every renderer it creates (and null when it tears one down), so a remount re-hooks.
  function hookLive(live) {
    if (!live) { showSector(null); showRecord(null); return; }
    live.on('sector', function (s) { showSector(s); showRecord(null); markView(null); });
    // v0.3.8.124 — there is no `moundsettings` event any more. A mound chamber's second click used
    // to open its settings page and now does nothing special: a chamber is a chamber, and settings
    // are reached from the registry, which is the one place that lists every mound.
    live.on('deselect', function () { showSector(null); showRecord(null); });
    live.on('record', function (r) { showRecord(r); });
    live.on('resident', function (h) { showResident(h); });
    applyEnv(initialEnv()); restoreView();
  }

  // ---- wiring ---------------------------------------------------------------------------------
  function onAct(e) {
    var b = e.target.closest('[data-homeact]'); if (!b) return;
    var act = b.dataset.homeact;
    if (act === 'focus') setFocus(!focusOn());
    else if (act === 'needs') go('/chat');
    else if (act === 'openwatch') {
      if (watching && (watching.ready || watching.blocked)) {
        var cid = watching.conversationId;
        go('/chat');
        if (cid && typeof chatOpen === 'function') { chatComposingNew = false; chatOpen(cid); }
      }
    }
    else if (act === 'viewmenu') { var p = $('clb-viewpop'); popShow(p && p.style.display === 'none'); }
    else if (act === 'resetlayout') { var lv = liveApi(); if (lv) lv.resetLayout(); popShow(false); }
    else if (act === 'conduitauto') { conduitAuto = !conduitAuto; applyView(); }
    else if (act === 'moundregistry') { go('/colony/mounds'); }
    else if (act === 'moundremove') {
      var ld = liveApi(), rid = b.dataset.moundId || null;
      if (ld && rid && ld.removeMound && ld.removeMound(rid)) { if (sectorId === rid) showSector(null); renderMounds(); }
    }
    else if (act === 'moundopen') { openMoundSettings(b.dataset.moundId || null); }
    // INFRASTRUCTURE's settings are the Infrastructure page, not a micromound form for a device it
    // does not have. v0.3.8.124 — see the comment in renderMounds.
    else if (act === 'infraopen') { go('/tools/infrastructure'); }
    else if (act === 'secstylereset') { var l2 = liveApi(); if (l2 && sectorId) { l2.setSectorStyle(sectorId, { color: null, glow: 1, bright: 1 }); showSector(l2.sectorInfo(sectorId) && Object.assign({}, l2.sectorInfo(sectorId), { records: [] })); } }
    else if (act === 'antstylereset') { var l3 = liveApi(); if (l3 && antId) { l3.setAntStyle(antId, { name: null, color: null }); var nm2 = $('clb-ant-name'); if (nm2) nm2.value = ''; var t2 = $('clb-record-title'); if (t2 && lastResident) t2.textContent = lastResident.registryName || antId; } }
    else if (act === 'addmound') {
      // v0.3.8.122 — ADDS A CHAMBER, it no longer navigates away. The old behaviour sent an
      // operator to the Micromound console to mint a token, which is the enrolment story and not
      // this button's job: this one is the colony's own labelling layer. The chamber's name, its
      // colour and its ants' names are presentation and reach no device — a mound is enrolled by
      // one-time token and answers under its own identity whatever the colony calls it.
      var lm = liveApi(); if (lm && lm.addMound) lm.addMound();
    }
    /* v0.3.9 — MEMORY OPENS THE VAULT. `view('memory')` still flies the camera to the chamber;
       what changed is that the chamber now has something to browse, and the panel is part of that
       view rather than a control you have to find. Survey and Esc close it, because the operator
       asked for the panel and the Memory view to be one mode. */
    else if (act === 'memory') {
      view('memory');
      if (typeof MemoryVault !== 'undefined') MemoryVault.open();
    }
    else if (act === 'survey') {
      view('survey');
      if (typeof MemoryVault !== 'undefined') MemoryVault.close();
    }
    else if (act === 'ask') send('chat');
    else if (act === 'run') send('mission');
    else {
      /* v0.3.9.1 — THE VAULT BELONGS TO THE MEMORY VIEW, so it leaves with it.
         `.9` closed it on Survey and on Esc and nothing else, so Mission, Mounds and Follow all
         flew the camera somewhere the panel was not about and left it standing over the result.
         Every remaining act here is a VIEW change; a view that is not Memory closes it. */
      view(act);
      if (typeof MemoryVault !== 'undefined') MemoryVault.close();
    }
  }
  function init() {
    var page = $('page-colony'); if (!page) return;
    page.addEventListener('click', onAct);
    /* THE REGISTRY IS A DIFFERENT PAGE ELEMENT, AND THAT IS WHY DELETE DID NOTHING. v0.3.8.123.

       `onAct` was bound to `#page-colony` alone. The mound registry lives in `#page-mounds`, so its
       Settings and Delete buttons emitted clicks that reached no handler at all — the operator's
       report was simply "the delete micromound button doesnt work in the mound directory", and it
       never could have: the listener was on a different subtree. Bound here rather than moved to
       `document`, because a document-level handler would start answering for every `data-homeact`
       anywhere in the console, which is a much larger claim than this file should make. */
    var mpage = $('page-mounds'); if (mpage) mpage.addEventListener('click', onAct);
    var input = $('ccp-input');
    if (input) {
      input.addEventListener('input', autosize);
      input.addEventListener('keydown', function (e) {
        if (e.key !== 'Enter' || e.shiftKey) return;
        e.preventDefault();
        send((e.ctrlKey || e.metaKey) ? 'mission' : 'chat');
      });
    }
    var sel = $('ccp-scope'); if (sel) sel.addEventListener('change', scopeChanged);
    var env = $('clb-env'); if (env) { env.value = initialEnv(); env.addEventListener('change', function () { applyEnv(env.value); }); }
    ['clb-motion', 'clb-labels', 'clb-trails', 'clb-cdens', 'clb-cbright', 'clb-linkalpha'].forEach(function (id) { var el = $(id); if (el) el.addEventListener((id === 'clb-cbright' || id === 'clb-linkalpha') ? 'input' : 'change', applyView); });
    var cc = $('clb-ccolor'); if (cc) cc.addEventListener('input', function () { conduitAuto = false; applyView(); });
    ['clb-sec-color', 'clb-sec-glow', 'clb-sec-bright'].forEach(function (id) { var el = $(id); if (el) el.addEventListener('input', applySectorStyle); });
    var an = $('clb-ant-name'); if (an) { an.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); applyAntStyle(); an.blur(); } if (e.key === 'Escape') e.stopPropagation(); }); an.addEventListener('blur', function () { if (antId) applyAntStyle(); }); }
    var ac = $('clb-ant-color'); if (ac) ac.addEventListener('input', applyAntStyle);
    // the sector panel's own inputs must not be swallowed by the stage's click handler
    ['clb-sector', 'clb-record', 'clb-viewpop'].forEach(function (id) { var el = $(id); if (el) el.addEventListener('keydown', function (e) { if (e.key === 'Escape' && e.target.tagName === 'INPUT') e.stopPropagation(); }); });
    document.addEventListener('click', function (e) { if (!e.target.closest('.clb-pop-wrap')) popShow(false); });
    var sn = $('clb-sector-name'); if (sn) sn.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); renameSector(); } if (e.key === 'Escape') { e.stopPropagation(); sn.blur(); } });
    if (sn) sn.addEventListener('blur', function () { if (sectorId) renameSector(); });
    var nm = $('ccp-newname'); if (nm) nm.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); send('mission'); } });
    setFocus(initialFocus());
    scopeChanged();
    // Page entry: keep the existing hook (it reclaims the canvas) and add ours.
    if (typeof PAGE_ENTER === 'object') {
      var prev = PAGE_ENTER['colony'];
      PAGE_ENTER['colony'] = function () {
        if (typeof prev === 'function') prev();
        loadProjects(false); refreshBar();
        if (window.ColonyHost) ColonyHost.hydrate();   // a no-op once hydrated; the fix for enabling before sign-in
        setTimeout(function () { var i = $('ccp-input'); if (i && !focusOn()) return; if (i) i.focus(); }, 60);
      };
    }
    // The header's mission line and the approvals badge are written by app.js's own pollers; the
    // bar re-reads them on every scene the reducer publishes (graph, approvals, events all publish).
    // The registry renders on entry rather than on a timer: it is a list of the operator's own
    // chambers, and it changes only when they change it.
    if (typeof PAGE_ENTER !== 'undefined') PAGE_ENTER['mounds'] = renderMounds;
    if (window.ColonyHost) { ColonyHost.onLive(hookLive); ColonyHost.onScene(function (sc) { lastScene = sc; if (page.classList.contains('active')) { refreshBar(); renderWatch(); } }); }
    refreshBar();
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init); else init();
})();
