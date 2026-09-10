/* ─────────────────────────────────────────────────────────────────────────────
   THE MEMORY VAULT — an Obsidian-like browser over everything the colony remembers.
   v0.3.9.

   WHAT IT IS. Clicking MEMORY in the live colony view flies the camera to the Memory
   chamber and opens this panel on the right: a tree of every kind of record, a search
   that crosses all of them, and a card that opens over the 3D view when you pick one.

   READ-ONLY, structurally. Every call here is a GET. Nothing in this file writes to
   the colony's memory, and that is the operator's own decision rather than a first
   instalment — a vault you can edit is a different feature with a different argument.

   THE SERVER OWNS THE SHAPE. Kinds, chambers, counts, groups and links all come from
   `/memory/vault`; this file renders what it is told. The one thing it must never do
   is re-derive which chamber a record belongs to — that mapping decides where a DOT
   goes in the 3D view, and two answers to it would put a record in a chamber the
   counts say it is not in.
   ───────────────────────────────────────────────────────────────────────────── */

var MemoryVault = (function () {
  'use strict';

  var open = false;
  var state = {
    q: '', kind: '', group: 'facet', groupKey: '', project: '', outcome: '',
    from: '', to: '', expanded: {}, selected: null, records: [], kinds: [], groups: [], total: 0,
  };
  var knowledge = null;   // the derived branch, fetched once per open
  var dots = [];          // EVERY record, lean — what the chambers are seated from

  function $(id) { return document.getElementById(id); }
  function esc(s) { return (typeof escapeHtml === 'function') ? escapeHtml(s == null ? '' : String(s)) : String(s == null ? '' : s); }

  /** The colours the chamber uses, so a row in the tree and its dot read as the same thing. */
  var KIND_COLOR = {
    mission: '#e0567f', task: '#7ea6ff', artifact: '#63d3a6', evidence: '#d9b054',
    result: '#c58cf0', event: '#7c8798', trail: '#f08a5d', skill: '#4fd1c5',
    conversation: '#9aa8ff', turn: '#6b7788', knowledge: '#8ad6ff',
  };

  function colorFor(kind) { return KIND_COLOR[kind] || '#8b95a5'; }

  /* ── data ──────────────────────────────────────────────────────────────── */

  function params() {
    var p = [];
    if (state.q) p.push('q=' + encodeURIComponent(state.q));
    if (state.kind) p.push('kind=' + encodeURIComponent(state.kind));
    if (state.project) p.push('project=' + encodeURIComponent(state.project));
    if (state.outcome) p.push('outcome=' + encodeURIComponent(state.outcome));
    if (state.from) p.push('from=' + encodeURIComponent(state.from));
    if (state.to) p.push('to=' + encodeURIComponent(state.to));
    p.push('group=' + encodeURIComponent(state.group));
    p.push('limit=300');
    return p.length ? ('?' + p.join('&')) : '';
  }

  async function load() {
    var host = $('mv-tree');
    if (host && !state.records.length) host.innerHTML = '<div class="hud-state"><div class="hud-spinner"></div>Reading memory…</div>';
    try {
      var r = await api('/memory/vault' + params());
      if (!r || !r.success || !r.data) { fail((r && r.message) || 'Memory is unreadable.'); return; }
      state.records = r.data.records || [];
      state.kinds = r.data.kinds || [];
      state.groups = r.data.groups || [];
      state.total = r.data.total || 0;
      render();
      loadDots();
    } catch (e) { fail((e && e.message) || 'Memory is unreadable.'); }
  }

  /**
   * EVERY record, for the chambers. Separate from the tree's page on purpose: the tree shows what
   * you asked for, three hundred rows at a time, and the chamber shows the WHOLE vault — the
   * operator's answer to "which records are dots" was everything, no cap. One lean call, re-fetched
   * only when the grouping changes, because grouping is the one thing that moves a dot.
   */
  async function loadDots() {
    try {
      var r = await api('/memory/vault/dots?group=' + encodeURIComponent(state.group), 'GET', null, 60000);
      dots = (r && r.success && r.data && r.data.dots) || [];
    } catch (_) { dots = []; }
    pushToChambers();
  }

  /**
   * HAND THE CHAMBERS WHAT THE TREE IS SHOWING.
   *
   * The live view already seats records on a lattice and orders them into strata when a chamber is
   * focused — that machinery is older than this panel. What it was fed was the topology reducer's
   * recent slice; this feeds it the vault, in the chamber the SERVER assigned, grouped by whatever
   * the tree is grouped by. Switch the grouping and the chamber re-forms along the same cut,
   * because the cluster a dot sits in IS the folder the tree filed it under.
   */
  function pushToChambers() {
    var live = null;
    try { live = (typeof liveApi === 'function') ? liveApi() : null; } catch (_) { live = null; }
    if (!live || !live.setVaultRecords) return;

    var byChamber = {};
    dots.concat(knowledge || []).forEach(function (rec) {
      var c = rec.chamber || 'memory';
      (byChamber[c] = byChamber[c] || []).push(rec);
    });
    live.setVaultRecords(byChamber, state.group);
  }

  function fail(msg) {
    var host = $('mv-tree');
    if (host) host.innerHTML = '<div class="hud-state err">' + esc(msg) + '</div>';
  }

  /* ── rendering ─────────────────────────────────────────────────────────── */

  function render() {
    var host = $('mv-tree');
    if (!host) return;

    // GROUPS FIRST, KINDS INSIDE — the operator's "kind, then topic" read from the other end:
    // the second level is what SWITCHES, so it is the one the tree lets you re-cut. Choosing a
    // grouping re-folders the same records rather than fetching different ones.
    var rows = [];
    var byKind = {};
    state.records.forEach(function (rec) { (byKind[rec.kind] = byKind[rec.kind] || []).push(rec); });

    state.kinds.forEach(function (k) {
      var items = byKind[k.key] || [];
      var isOpen = !!state.expanded[k.key];
      rows.push('<div class="mv-folder' + (isOpen ? ' on' : '') + '" data-mvkind="' + esc(k.key) + '">'
        + '<span class="mv-caret">' + (isOpen ? '▾' : '▸') + '</span>'
        + '<span class="mv-dot" style="background:' + colorFor(k.key) + '"></span>'
        + '<span class="mv-name">' + esc(k.label) + '</span>'
        + '<span class="mv-count">' + k.count + '</span></div>');

      if (!isOpen) return;
      if (!items.length) {
        rows.push('<div class="mv-empty">Nothing on this page — narrow the search or open the kind on its own.</div>');
        return;
      }
      items.slice(0, 400).forEach(function (rec) {
        rows.push(leaf(rec));
      });
      if (k.count > items.length) {
        rows.push('<div class="mv-more" data-mvonly="' + esc(k.key) + '">'
          + 'Showing ' + items.length + ' of ' + k.count + ' — open this kind on its own</div>');
      }
    });

    if (knowledge) {
      var kOpen = !!state.expanded.knowledge;
      rows.push('<div class="mv-folder' + (kOpen ? ' on' : '') + '" data-mvkind="knowledge">'
        + '<span class="mv-caret">' + (kOpen ? '▾' : '▸') + '</span>'
        + '<span class="mv-dot" style="background:' + colorFor('knowledge') + '"></span>'
        + '<span class="mv-name">Knowledge consulted</span>'
        + '<span class="mv-count">' + knowledge.length + '</span></div>');
      if (kOpen) knowledge.forEach(function (rec) { rows.push(leaf(rec)); });
    }

    host.innerHTML = rows.join('') || '<div class="mv-empty">No memory matches that.</div>';

    var note = $('mv-total');
    if (note) note.textContent = state.total + ' record' + (state.total === 1 ? '' : 's');
    renderGroups();
  }

  function leaf(rec) {
    var sel = state.selected && state.selected.id === rec.id;
    return '<div class="mv-leaf' + (sel ? ' on' : '') + '" data-mvid="' + esc(rec.id) + '" title="' + esc(rec.title) + '">'
      + '<span class="mv-dot sm" style="background:' + colorFor(rec.kind) + ';opacity:' + bright(rec.outcome) + '"></span>'
      + '<span class="mv-name">' + esc(rec.title || '(untitled)') + '</span>'
      + (rec.when ? '<span class="mv-when">' + esc(String(rec.when).slice(0, 10)) + '</span>' : '')
      + '</div>';
  }

  /** Brightness carries the verdict — the same rule the dots use, so the two cannot disagree. */
  function bright(outcome) {
    var o = (outcome || '').toLowerCase();
    if (!o) return 0.55;
    if (o.indexOf('verified') >= 0 || o === 'succeeded' || o === 'passed' || o === 'completed') return 1;
    if (o.indexOf('fail') >= 0 || o === 'timed_out' || o === 'escalated') return 0.8;
    return 0.7;
  }

  function renderGroups() {
    var host = $('mv-groups');
    if (!host) return;
    if (!state.groups.length) { host.innerHTML = ''; return; }
    host.innerHTML = state.groups.slice(0, 24).map(function (g) {
      var on = state.groupKey === g.key;
      return '<button class="mv-chip' + (on ? ' on' : '') + '" data-mvgroup="' + esc(g.key) + '">'
        + esc(g.label) + ' <span class="mv-count">' + g.count + '</span></button>';
    }).join('');
  }

  /* ── the card ──────────────────────────────────────────────────────────── */

  async function select(id) {
    // THE TREE'S PAGE FIRST, THEN THE WHOLE VAULT. A dot clicked in the chamber is very often not
    // on the three hundred rows the tree is showing, and "nothing happened" would be the worst
    // possible answer to clicking the thing this release exists to make clickable.
    var rec = state.records.concat(knowledge || [], dots).find(function (r) { return r.id === id; });
    if (!rec) return;
    state.selected = rec;
    render();

    // THE CAMERA FIRST, so the card lands where the eye already is.
    try {
      var live = (typeof liveApi === 'function') ? liveApi() : null;
      if (live && live.focusRecord) live.focusRecord(rec.id, rec.chamber);
    } catch (_) { /* the panel is useful without the 3D view; never let it take the card down */ }

    card(rec, null);
    try {
      var r = await api('/memory/vault/links?id=' + encodeURIComponent(rec.id));
      var links = (r && r.success && r.data && r.data.links) || [];
      card(rec, links);
      var live2 = (typeof liveApi === 'function') ? liveApi() : null;
      if (live2 && live2.showLinks) live2.showLinks(rec.id, links);
    } catch (_) { /* links are an enrichment, not the record */ }
  }

  function card(rec, links) {
    var host = $('mv-card');
    if (!host) return;
    host.hidden = false;
    host.innerHTML =
      '<div class="mv-card-head">'
      + '<span class="mv-dot" style="background:' + colorFor(rec.kind) + '"></span>'
      + '<b>' + esc(rec.kind) + '</b>'
      + '<span class="mv-sub">' + esc(rec.chamber) + '</span>'
      + '<span class="mv-gap"></span>'
      + '<button class="mv-x" data-mvclose="1" aria-label="Close">✕</button>'
      + '</div>'
      + '<div class="mv-card-title">' + esc(rec.title || '(untitled)') + '</div>'
      + (rec.subtitle ? '<div class="mv-sub">' + esc(rec.subtitle) + '</div>' : '')
      + '<div class="mv-meta">'
      + (rec.when ? '<span>' + esc(String(rec.when).replace('T', ' ').slice(0, 19)) + '</span>' : '')
      + (rec.outcome ? '<span>' + esc(rec.outcome) + '</span>' : '')
      + (rec.project_id ? '<span>' + esc(rec.project_id) + '</span>' : '')
      + '</div>'
      + (links === null
          ? '<div class="mv-sub">Reading its links…</div>'
          : (links.length
              ? '<div class="mv-links">' + links.map(function (l) {
                  return '<button class="mv-chip' + (l.derived ? ' derived' : '') + '" data-mvid="' + esc(l.id) + '" '
                    + 'title="' + esc(l.relation) + (l.derived ? ' — derived, not recorded' : '') + '">'
                    + '<span class="mv-dot sm" style="background:' + colorFor(l.kind) + '"></span>'
                    + esc(l.relation) + ': ' + esc(l.title || l.id) + '</button>';
                }).join('') + '</div>'
              : '<div class="mv-sub">Nothing links to this one.</div>'))
      + (rec.href ? '<div class="mv-open"><a href="#' + esc(rec.href) + '" data-mvgo="' + esc(rec.href) + '">Open the full record →</a></div>' : '');
  }

  function closeCard() {
    var host = $('mv-card');
    if (host) { host.hidden = true; host.innerHTML = ''; }
    state.selected = null;
    var live = (typeof liveApi === 'function') ? liveApi() : null;
    if (live && live.showLinks) live.showLinks(null, []);
    render();
  }

  /* ── events ────────────────────────────────────────────────────────────── */

  function onClick(e) {
    var t = e.target;
    var folder = t.closest && t.closest('[data-mvkind]');
    var leafEl = t.closest && t.closest('[data-mvid]');
    var chip = t.closest && t.closest('[data-mvgroup]');
    var only = t.closest && t.closest('[data-mvonly]');
    var go = t.closest && t.closest('[data-mvgo]');

    if (t.closest && t.closest('[data-mvclose]')) { closeCard(); return; }
    if (go) { if (typeof window.go === 'function') window.go(go.dataset.mvgo); return; }
    if (only) { state.kind = only.dataset.mvonly; state.expanded[state.kind] = true; load(); return; }
    if (leafEl) { select(leafEl.dataset.mvid); return; }
    if (chip) {
      state.groupKey = state.groupKey === chip.dataset.mvgroup ? '' : chip.dataset.mvgroup;
      applyGroupFilter();
      return;
    }
    if (folder) {
      var k = folder.dataset.mvkind;
      state.expanded[k] = !state.expanded[k];
      if (k === 'knowledge' && state.expanded[k] && !knowledge) { loadKnowledge(); return; }
      render();
    }
  }

  /**
   * The second level is a FILTER over the same query rather than a different fetch, which is what
   * makes switching the grouping cheap: the server is asked once, and re-cutting is local.
   */
  function applyGroupFilter() {
    if (state.group === 'project') state.project = state.groupKey;
    else if (state.group === 'facet') state.outcome = state.groupKey;
    else if (state.group === 'topic') state.q = state.groupKey;
    load();
  }

  async function loadKnowledge() {
    try {
      var r = await api('/memory/vault/knowledge');
      knowledge = (r && r.success && r.data && r.data.records) || [];
    } catch (_) { knowledge = []; }
    render();
    pushToChambers();
  }

  function bind() {
    var panel = $('mv-panel');
    if (!panel || panel.dataset.bound) return;
    panel.dataset.bound = '1';
    panel.addEventListener('click', onClick);

    var box = $('mv-q');
    if (box) {
      var timer = null;
      box.addEventListener('input', function () {
        clearTimeout(timer);
        timer = setTimeout(function () { state.q = box.value.trim(); state.groupKey = ''; load(); }, 220);
      });
    }
    var sel = $('mv-group');
    if (sel) sel.addEventListener('change', function () {
      state.group = sel.value; state.groupKey = '';
      state.project = ''; state.outcome = '';
      load();
    });
    var x = $('mv-close');
    if (x) x.addEventListener('click', function () { MemoryVault.close(); });

    // ESC CLOSES, and it is bound on the document rather than the panel because the operator's
    // hands are on the 3D view as often as on the tree.
    if (!document.body.dataset.mvEsc) {
      document.body.dataset.mvEsc = '1';
      document.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape' || !open) return;
        if (e.target && /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName)) return;
        var card = $('mv-card');
        if (card && !card.hidden) { closeCard(); return; }   // one Esc closes the card, two the panel
        MemoryVault.close();
      });
    }

    var clear = $('mv-clear');
    if (clear) clear.addEventListener('click', function () {
      state.q = ''; state.kind = ''; state.groupKey = ''; state.project = ''; state.outcome = '';
      state.from = ''; state.to = '';
      if (box) box.value = '';
      load();
    });
  }

  return {
    open: function () {
      var panel = $('mv-panel');
      if (!panel) return;
      panel.hidden = false;
      open = true;
      document.body.classList.add('mv-on');
      bind();
      if (!state.records.length) load();
    },
    close: function () {
      var panel = $('mv-panel');
      if (panel) panel.hidden = true;
      open = false;
      document.body.classList.remove('mv-on');
      closeCard();
    },
    isOpen: function () { return open; },
    /** The 3D layer calls this when a DOT is clicked, so both directions land in one place. */
    selectFromScene: function (id) { select(id); },
    colorFor: colorFor,
    brightnessFor: bright,
  };
})();
