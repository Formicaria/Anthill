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
    var live = liveApi();
    if (!live) { if (act === 'resetview' && typeof colonyResetView === 'function') colonyResetView(); return; }
    if (act === 'survey') live.survey();
    else if (act === 'mission') live.focus('queen');
    else if (act === 'memory') live.focus('memory');
    else if (act === 'mounds') live.focus('mound');
    else if (act === 'follow') live.followMission();
    else if (act === 'resetview') { live.resetView(); act = 'survey'; }
    markView(act);
  }
  var lastScene = null;
  function syncViewButtons() {
    // A view that has nowhere to go is disabled rather than silently doing nothing: Mounds needs a
    // mound in the fleet, Follow needs a running task to ride with.
    var sc = lastScene, mound = !!(sc && sc.mound && sc.mound.present);
    var running = !!(sc && (sc.sectors || []).some(function (x) { return (x.runningTasks || []).length; }));
    var b;
    if ((b = document.querySelector('[data-homeact="mounds"]'))) { b.disabled = !mound; b.title = mound ? 'Approach the Micromound' : 'No mound in the fleet — nothing to approach'; }
    if ((b = document.querySelector('[data-homeact="follow"]'))) { b.disabled = !running; b.title = running ? 'Ride the active mission circuit' : 'No task is running — nothing to follow'; }
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

  function syncToggle() {
    var b = $('clb-3d'), on = document.body.classList.contains('colony-live-on');
    if (b) { b.textContent = on ? 'Classic 2D' : 'Live 3D'; b.classList.toggle('on', !on); }
  }
  // ---- environment ----------------------------------------------------------------------------
  var ENV_KEY = 'anthill.colony.env';
  function applyEnv(v) {
    var live = liveApi();
    if (live) live.setOptions({ env: v });
    var sel = $('clb-env'); if (sel && sel.value !== v) sel.value = v;
    try { localStorage.setItem(ENV_KEY, v); } catch (e) { }
  }
  function initialEnv() { var v = null; try { v = localStorage.getItem(ENV_KEY); } catch (e) { } return /^(strata|plane|space|nebula|void)$/.test(v || '') ? v : 'space'; }

  // ---- view options + sector panel --------------------------------------------------------------
  function popShow(on) { var p = $('clb-viewpop'), b = $('clb-viewbtn'); if (!p) return; p.style.display = on ? '' : 'none'; if (b) b.setAttribute('aria-expanded', on ? 'true' : 'false'); }
  function applyView() {
    var live = liveApi(), mo = $('clb-motion'), lb = $('clb-labels'), tr = $('clb-trails');
    // motion + trails go through app.js's validated preference path (it also feeds the classic canvas)
    if (typeof setColonyPref === 'function') { if (mo) setColonyPref('motion', mo.value); if (tr) setColonyPref('pheromones', tr.value === 'off' ? 'off' : 'all'); }
    if (live && lb) live.setOptions({ labels: lb.value });
    try { localStorage.setItem('anthill.colony.view', JSON.stringify({ motion: mo && mo.value, labels: lb && lb.value, trails: tr && tr.value })); } catch (e) { }
  }
  function restoreView() {
    var v = null; try { v = JSON.parse(localStorage.getItem('anthill.colony.view') || 'null'); } catch (e) { }
    if (!v) return;
    var mo = $('clb-motion'), lb = $('clb-labels'), tr = $('clb-trails');
    if (mo && v.motion) mo.value = v.motion; if (lb && v.labels) lb.value = v.labels; if (tr && v.trails) tr.value = v.trails;
    applyView();
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
  }
  var recordAnt = null;
  function showResident(h) {
    var box = $('clb-record'); if (!box) return;
    var res = h && h.resident; if (!res) { box.style.display = 'none'; recordAnt = null; return; }
    recordAnt = String(res.parent || res.roleId || '').toLowerCase();
    $('clb-record-title').textContent = res.name || res.roleId;
    var tr = res.trail && isFinite(res.trail.strength) ? res.trail : null;
    $('clb-record-meta').textContent = [res.worker ? 'worker of ' + res.parent : 'role', res.roleId, res.status, tr ? ('trail ' + Number(tr.strength).toFixed(2) + ' · ' + (tr.successes || 0) + '✓ ' + (tr.failures || 0) + '✗') : 'no trail recorded', res.workers ? res.workers + ' worker' + (res.workers === 1 ? '' : 's') : ''].filter(Boolean).join(' · ');
    var tag = $('clb-record-verif'); tag.textContent = res.status || 'idle'; tag.className = 'clb-record-tag' + (res.status === 'working' ? ' ok' : res.status === 'disabled' ? ' bad' : '');
    var open = $('clb-record-open'); if (open) open.style.display = '';
    box.style.display = '';
  }
  function showRecord(r) {
    var box = $('clb-record'); if (!box) return;
    if (!r) { box.style.display = 'none'; recordAnt = null; return; }
    var rec = r.record || {}; recordAnt = String(rec.ant || '').toLowerCase();
    $('clb-record-title').textContent = rec.title || rec.type || 'record';
    $('clb-record-meta').textContent = [rec.type, rec.ant, rec.mission && ('mission ' + String(rec.mission).slice(0, 8)), rec.taskId && ('task ' + String(rec.taskId).slice(0, 8)), rec.time].filter(Boolean).join(' · ');
    var v = rec.verif || 'not_scanned', tag = $('clb-record-verif');
    tag.textContent = v.replace(/_/g, ' ');
    tag.className = 'clb-record-tag' + (v === 'verified' ? ' ok' : v === 'refused' ? ' bad' : '');
    var open = $('clb-record-open'); if (open) open.style.display = recordAnt && recordAnt !== '—' ? '' : 'none';
    box.style.display = '';
  }
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
    busy = true; setState(mode === 'mission' ? 'Choosing where the work runs…' : 'Sending…');
    try {
      var pid = await resolveProject();
      // The hand-off Chat already honours: a project chosen before the conversation exists, and
      // an explicit new conversation that auto-open must not override.
      chatPendingProjectId = pid; chatActiveId = null; chatComposingNew = true;
      go('/chat');
      var el = $('chat-input'); if (!el) throw new Error('Chat composer missing.');
      el.value = msg; input.value = ''; autosize(); setState('');
      await chatSend(mode);
    } catch (e) {
      setState((e && e.message) || 'Could not send.', true);
    } finally { busy = false; }
  }

  // The renderer announces focus changes; the sector panel follows them. The host fires onLive
  // with every renderer it creates (and null when it tears one down), so a 2D→3D toggle re-hooks.
  function hookLive(live) {
    syncToggle();
    if (!live) { showSector(null); showRecord(null); return; }
    live.on('sector', function (s) { showSector(s); showRecord(null); markView(null); });
    live.on('deselect', function () { showSector(null); showRecord(null); });
    live.on('record', function (r) { showRecord(r); });
    live.on('resident', function (h) { showResident(h); });
    applyEnv(initialEnv()); restoreView(); syncToggle();
  }

  // ---- wiring ---------------------------------------------------------------------------------
  function onAct(e) {
    var b = e.target.closest('[data-homeact]'); if (!b) return;
    var act = b.dataset.homeact;
    if (act === 'focus') setFocus(!focusOn());
    else if (act === 'needs') go('/chat');
    else if (act === 'viewmenu') { var p = $('clb-viewpop'); popShow(p && p.style.display === 'none'); }
    else if (act === 'openant') { if (recordAnt && typeof nodes !== 'undefined' && typeof showInspector === 'function') { var n = nodes.find(function (x) { return x.ant === recordAnt || x.worker === recordAnt || x.id === recordAnt; }); if (n) { setFocus(false); showInspector(n); } } }
    else if (act === 'resetlayout') { var lv = liveApi(); if (lv) lv.resetLayout(); popShow(false); }
    else if (act === 'toggle3d') { if (window.ColonyHost) ColonyHost.toggle(); syncToggle(); }
    else if (act === 'ask') send('chat');
    else if (act === 'run') send('mission');
    else view(act);
  }
  function init() {
    var page = $('page-colony'); if (!page) return;
    page.addEventListener('click', onAct);
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
    ['clb-motion', 'clb-labels', 'clb-trails'].forEach(function (id) { var el = $(id); if (el) el.addEventListener('change', applyView); });
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
        setTimeout(function () { var i = $('ccp-input'); if (i && !focusOn()) return; if (i) i.focus(); }, 60);
      };
    }
    // The header's mission line and the approvals badge are written by app.js's own pollers; the
    // bar re-reads them on every scene the reducer publishes (graph, approvals, events all publish).
    if (window.ColonyHost) { ColonyHost.onLive(hookLive); ColonyHost.onScene(function (sc) { lastScene = sc; if (page.classList.contains('active')) refreshBar(); }); }
    refreshBar(); syncToggle();
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init); else init();
})();
