/* ANTHILL — Topology-first Dashboard workspace runtime (v2.14.3, Stage 2: panel shell).
 *
 * Served same-origin like app.js: the console runs under CSP script-src 'self', so there is no
 * inline JavaScript here and no inline on*= handlers — every control is a real <button> with a
 * data-wsact attribute, dispatched by one delegated listener.
 *
 * Scope of this stage: the panel SHELL only — registry, render, header controls, collapse,
 * minimize, hide, pin, Modules menu, layout lock, and persistence. Drag/resize (Stage 3), tab
 * groups (Stage 4), and the topology canvas underneath (Stage 6) come later. The whole runtime is
 * inert unless the server reports dashboard_workspace_enabled.
 *
 * Correctness of the saved layout lives server-side in DashboardWorkspaceState (validation,
 * clamping, off-screen recovery, profile isolation). This file owns interaction only, and never
 * writes a shape the server has not sanitised.
 */
(function () {
  'use strict';

  var W = {
    enabled: false,
    panels: {},          // id -> definition
    order: [],           // registration order (stable Modules-menu ordering)
    state: null,         // sanitized workspace state from the server
    profile: 'desktop',
    root: null,
    saveTimer: null,
  };

  var PROFILE_BREAKPOINT = 900; // must match DashboardWorkspaceState.CompactBreakpoint

  function profileForViewport() {
    return window.innerWidth < PROFILE_BREAKPOINT ? 'compact' : 'desktop';
  }

  function placements() {
    if (!W.state || !W.state.profiles) return {};
    return W.state.profiles[W.profile] || {};
  }

  function placement(id) {
    var p = placements()[id];
    if (p) return p;
    // Server merges new panels on next load; use a safe local default until then.
    return { display_state: 'visible', placement_mode: 'floating', x: 40, y: 40,
             width: 380, height: 240, expanded_height: 240, z: 1, pinned: false,
             dock_side: null, tab_group: null, opacity: 'solid' };
  }

  /* ---- Registration ---------------------------------------------------------------------- */

  /** register({id,title,render,collapsible,minimizable,hideable,pinnable,refreshPolicy}) */
  function register(def) {
    if (!def || !def.id || typeof def.render !== 'function') return;
    if (!W.panels[def.id]) W.order.push(def.id);
    W.panels[def.id] = Object.assign({
      title: def.id, collapsible: true, minimizable: true, hideable: true,
      pinnable: true, refreshPolicy: 'visible',
    }, def);
  }

  function panelIds() { return W.order.slice(); }

  /* ---- Persistence ------------------------------------------------------------------------ */

  async function load() {
    try {
      var r = await window.api('/ui/state');
      if (r && r.success && r.data && r.data.dashboard_workspace) {
        W.state = r.data.dashboard_workspace;
      }
    } catch (e) { /* keep whatever we have; the shell must still render */ }
    if (!W.state) W.state = { schema_version: 1, locked: true, focus_mode: false, profiles: {} };
    if (!W.state.profiles) W.state.profiles = {};
    if (!W.state.profiles[W.profile]) W.state.profiles[W.profile] = {};
  }

  /** Debounced save AFTER interaction, never continuously (spec: no save-per-pixel). */
  function save() {
    if (W.saveTimer) clearTimeout(W.saveTimer);
    W.saveTimer = setTimeout(async function () {
      W.saveTimer = null;
      try {
        var current = await window.api('/ui/state');
        var doc = (current && current.success && current.data) ? current.data : {};
        doc.dashboard_workspace = W.state;   // only this key changes; ants/layout untouched
        await window.api('/ui/state', 'PUT', doc);
      } catch (e) { /* a failed save must never break the console */ }
    }, 600);
  }

  function setPlacement(id, changes) {
    var profiles = W.state.profiles[W.profile] = W.state.profiles[W.profile] || {};
    profiles[id] = Object.assign(placement(id), changes);
    save();
  }

  /* ---- Rendering -------------------------------------------------------------------------- */

  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) n.textContent = text;
    return n;
  }

  function headerButton(action, id, label, glyph, pressed) {
    var b = el('button', 'ws-hbtn');
    b.type = 'button';
    b.setAttribute('data-wsact', action);
    b.setAttribute('data-wsid', id);
    b.setAttribute('aria-label', label);
    b.title = label;
    if (pressed !== undefined) b.setAttribute('aria-pressed', pressed ? 'true' : 'false');
    b.textContent = glyph;
    return b;
  }

  function renderPanel(def) {
    var p = placement(def.id);
    if (p.display_state === 'hidden' || p.display_state === 'minimized') return null;
    if (W.state.focus_mode && !p.pinned) return null;

    var collapsed = p.display_state === 'collapsed';
    var frame = el('section', 'ws-panel' + (collapsed ? ' ws-collapsed' : '') + (p.pinned ? ' ws-pinned' : ''));
    frame.id = 'ws-panel-' + def.id;
    frame.setAttribute('data-wspanel', def.id);
    frame.setAttribute('aria-label', def.title);
    frame.style.left = p.x + 'px';
    frame.style.top = p.y + 'px';
    frame.style.width = p.width + 'px';
    frame.style.height = collapsed ? 'auto' : (p.height + 'px');
    frame.style.zIndex = String(p.z || 1);

    var head = el('header', 'ws-head');
    head.setAttribute('data-wsdrag', def.id); // Stage 3 attaches dragging here
    var title = el('h3', 'ws-title', def.title);
    head.appendChild(title);

    var controls = el('div', 'ws-controls');
    if (def.collapsible) {
      var c = headerButton('toggle-collapse', def.id, collapsed ? 'Expand panel' : 'Collapse panel', collapsed ? '▸' : '▾');
      c.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
      controls.appendChild(c);
    }
    if (def.pinnable) controls.appendChild(headerButton('toggle-pin', def.id, p.pinned ? 'Unpin panel' : 'Pin panel', '📌', p.pinned));
    if (def.minimizable) controls.appendChild(headerButton('minimize', def.id, 'Minimize panel', '—'));
    if (def.hideable) controls.appendChild(headerButton('hide', def.id, 'Hide panel', '✕'));
    head.appendChild(controls);
    frame.appendChild(head);

    if (!collapsed) {
      var body = el('div', 'ws-body');
      body.id = 'ws-body-' + def.id;
      frame.appendChild(body);
      // Resize grip: visible only while unlocked (CSS), keyboard users resize via the panel menu.
      var grip = el('div', 'ws-resize');
      grip.setAttribute('data-wsresize', def.id);
      grip.setAttribute('aria-hidden', 'true');
      frame.appendChild(grip);
      try {
        def.render(body);
      } catch (e) {
        body.textContent = '';
        body.appendChild(el('div', 'ws-error', 'Panel failed to render: ' + (e && e.message ? e.message : 'unknown error')));
      }
    }
    return frame;
  }

  function renderTray() {
    var mins = panelIds().filter(function (id) { return placement(id).display_state === 'minimized'; });
    var tray = el('div', 'ws-tray');
    tray.id = 'ws-tray';
    if (!mins.length) { tray.hidden = true; return tray; }
    tray.appendChild(el('span', 'ws-tray-label', 'Minimized'));
    mins.forEach(function (id) {
      var b = el('button', 'ws-tray-item');
      b.type = 'button';
      b.setAttribute('data-wsact', 'restore');
      b.setAttribute('data-wsid', id);
      b.textContent = (W.panels[id] && W.panels[id].title) || id;
      tray.appendChild(b);
    });
    return tray;
  }

  function renderToolbar() {
    var bar = el('div', 'ws-toolbar');
    bar.id = 'ws-toolbar';

    var lock = el('button', 'ws-tbtn');
    lock.type = 'button';
    lock.setAttribute('data-wsact', 'toggle-lock');
    lock.setAttribute('aria-pressed', W.state.locked ? 'true' : 'false');
    lock.textContent = W.state.locked ? '🔒 Locked' : '🔓 Customize';
    lock.title = W.state.locked ? 'Unlock to customize the workspace' : 'Lock the layout';
    bar.appendChild(lock);

    var modules = el('button', 'ws-tbtn');
    modules.type = 'button';
    modules.setAttribute('data-wsact', 'toggle-modules');
    modules.setAttribute('aria-expanded', 'false');
    modules.setAttribute('aria-controls', 'ws-modules');
    modules.textContent = '☰ Modules';
    bar.appendChild(modules);

    var focus = el('button', 'ws-tbtn');
    focus.type = 'button';
    focus.setAttribute('data-wsact', 'toggle-focus');
    focus.setAttribute('aria-pressed', W.state.focus_mode ? 'true' : 'false');
    focus.textContent = W.state.focus_mode ? '⤢ Exit focus' : '⤢ Focus';
    bar.appendChild(focus);

    var reset = el('button', 'ws-tbtn');
    reset.type = 'button';
    reset.setAttribute('data-wsact', 'reset-layout');
    reset.textContent = '⤾ Reset layout';
    reset.title = 'Restore the default panel arrangement (ant names, colours, and positions are untouched)';
    bar.appendChild(reset);

    return bar;
  }

  function renderModules() {
    var menu = el('div', 'ws-modules');
    menu.id = 'ws-modules';
    menu.hidden = true;
    menu.setAttribute('role', 'group');
    menu.setAttribute('aria-label', 'Dashboard modules');
    panelIds().forEach(function (id) {
      var def = W.panels[id];
      var p = placement(id);
      var row = el('button', 'ws-module-row');
      row.type = 'button';
      row.setAttribute('data-wsact', 'toggle-visible');
      row.setAttribute('data-wsid', id);
      var on = p.display_state !== 'hidden';
      row.setAttribute('aria-pressed', on ? 'true' : 'false');
      row.textContent = (on ? '✓ ' : '  ') + def.title;
      menu.appendChild(row);
    });
    return menu;
  }

  function render() {
    if (!W.enabled || !W.root) return;
    W.root.textContent = '';
    W.root.className = 'ws-root' + (W.state.locked ? ' ws-locked' : ' ws-customize')
      + (W.state.focus_mode ? ' ws-focus' : '');

    W.root.appendChild(renderToolbar());
    W.root.appendChild(renderModules());

    var layer = el('div', 'ws-panel-layer');
    layer.id = 'ws-panel-layer';
    panelIds().forEach(function (id) {
      var node = renderPanel(W.panels[id]);
      if (node) layer.appendChild(node);
    });
    var guides = el('div', 'ws-guides');       // alignment guides drawn during a drag
    guides.id = 'ws-guides';
    guides.setAttribute('aria-hidden', 'true');
    layer.appendChild(guides);
    W.root.appendChild(layer);
    W.root.appendChild(renderTray());
  }

  /* ---- Actions ------------------------------------------------------------------------------ */

  function bringToFront(id) {
    var maxZ = 1;
    var ps = placements();
    Object.keys(ps).forEach(function (k) { maxZ = Math.max(maxZ, ps[k].z || 1); });
    setPlacement(id, { z: maxZ + 1 });
  }

  var ACTIONS = {
    'toggle-collapse': function (id) {
      var p = placement(id);
      if (p.display_state === 'collapsed') setPlacement(id, { display_state: 'visible', height: p.expanded_height || p.height });
      else setPlacement(id, { display_state: 'collapsed', expanded_height: p.height });
      render();
    },
    'toggle-pin': function (id) { setPlacement(id, { pinned: !placement(id).pinned }); render(); },
    'minimize': function (id) { setPlacement(id, { display_state: 'minimized' }); render(); },
    'hide': function (id) { setPlacement(id, { display_state: 'hidden' }); render(); },
    'restore': function (id) { setPlacement(id, { display_state: 'visible' }); bringToFront(id); render(); },
    'toggle-visible': function (id) {
      var hidden = placement(id).display_state === 'hidden';
      setPlacement(id, { display_state: hidden ? 'visible' : 'hidden' });
      render();
      var m = document.getElementById('ws-modules');
      if (m) m.hidden = false; // keep the menu open while toggling several modules
      var btn = document.querySelector('[data-wsact="toggle-modules"]');
      if (btn) btn.setAttribute('aria-expanded', 'true');
    },
    'toggle-lock': function () { W.state.locked = !W.state.locked; save(); render(); },
    'toggle-focus': function () { W.state.focus_mode = !W.state.focus_mode; save(); render(); },
    'toggle-modules': function () {
      var m = document.getElementById('ws-modules');
      var btn = document.querySelector('[data-wsact="toggle-modules"]');
      if (!m) return;
      m.hidden = !m.hidden;
      if (btn) btn.setAttribute('aria-expanded', m.hidden ? 'false' : 'true');
    },
    'reset-layout': function () {
      // Layout only. Ant names, colours, positions, and map preferences are a different key and
      // are never touched here (server enforces the same invariant).
      W.state.profiles[W.profile] = {};
      W.state.focus_mode = false;
      save();
      render();
    },
  };

  function onClick(e) {
    var target = e.target.closest ? e.target.closest('[data-wsact]') : null;
    if (!target || !W.root || !W.root.contains(target)) return;
    var action = ACTIONS[target.getAttribute('data-wsact')];
    if (!action) return;
    e.preventDefault();
    action(target.getAttribute('data-wsid'));
  }

  /* ---- Stage 3: drag, resize, snap ------------------------------------------------------------
   * Pointer Events only (one code path for mouse/pen/touch — no synthesized double-fire).
   * Arbitration rules, per the design doc:
   *   - a gesture starting on a panel header moves that panel and never pans the map;
   *   - a gesture starting on a resize handle resizes and never drags;
   *   - header BUTTONS keep their clicks (drag ignores them entirely);
   *   - while locked, nothing here engages, so the topology beneath receives the gesture.
   * Movement is applied with requestAnimationFrame against a live style transform; state is
   * written once at pointerup (never per frame), then clamped by the server on the next load.  */

  var SNAP = 8;            // px: alignment threshold
  var MIN_W = 200, MIN_H = 80;
  var drag = null;         // {id,mode,startX,startY,origin,frame,pending,raf,guides}

  function workspaceRect() {
    var layer = document.getElementById('ws-panel-layer');
    return layer ? layer.getBoundingClientRect() : { width: window.innerWidth, height: window.innerHeight };
  }

  /** Edges of every other visible panel — the candidates a dragged panel can align to. */
  function snapTargets(exceptId) {
    var out = { x: [0], y: [0] };
    var r = workspaceRect();
    out.x.push(Math.round(r.width));
    out.y.push(Math.round(r.height));
    panelIds().forEach(function (id) {
      if (id === exceptId) return;
      var p = placement(id);
      if (p.display_state === 'hidden' || p.display_state === 'minimized') return;
      out.x.push(p.x, p.x + p.width);
      out.y.push(p.y, p.y + p.height);
    });
    return out;
  }

  /** Snap unless the operator holds a modifier (spec: snapping must be bypassable). */
  function applySnap(value, candidates, bypass) {
    if (bypass) return { value: value, guide: null };
    for (var i = 0; i < candidates.length; i++) {
      if (Math.abs(candidates[i] - value) <= SNAP) return { value: candidates[i], guide: candidates[i] };
    }
    return { value: value, guide: null };
  }

  function clearGuides() {
    var g = document.getElementById('ws-guides');
    if (g) g.textContent = '';
  }

  function drawGuides(gx, gy) {
    var g = document.getElementById('ws-guides');
    if (!g) return;
    g.textContent = '';
    if (gx != null) { var v = el('div', 'ws-guide ws-guide-v'); v.style.left = gx + 'px'; g.appendChild(v); }
    if (gy != null) { var h = el('div', 'ws-guide ws-guide-h'); h.style.top = gy + 'px'; g.appendChild(h); }
  }

  function beginGesture(e, id, mode) {
    if (W.state.locked) return;              // locked: the map owns every gesture
    if (e.button !== undefined && e.button !== 0) return;
    if (e.target.closest && e.target.closest('button')) return;  // header controls keep their clicks
    var frame = document.getElementById('ws-panel-' + id);
    if (!frame) return;

    var p = placement(id);
    drag = {
      id: id, mode: mode, frame: frame,
      startX: e.clientX, startY: e.clientY,
      origin: { x: p.x, y: p.y, w: p.width, h: p.height },
      pending: { x: p.x, y: p.y, w: p.width, h: p.height },
      raf: 0,
    };
    bringToFront(id);
    frame.style.zIndex = String((placement(id).z || 1));
    frame.classList.add('ws-dragging');
    try { e.target.setPointerCapture && e.target.setPointerCapture(e.pointerId); } catch (_) {}
    e.preventDefault();
    e.stopPropagation();                      // the topology never sees this gesture
  }

  function moveGesture(e) {
    if (!drag) return;
    var dx = e.clientX - drag.startX, dy = e.clientY - drag.startY;
    var bypass = e.altKey || e.metaKey;       // hold Alt/Cmd to place freely
    var t = snapTargets(drag.id);
    var r = workspaceRect();

    if (drag.mode === 'move') {
      var nx = applySnap(drag.origin.x + dx, t.x, bypass);
      var ny = applySnap(drag.origin.y + dy, t.y, bypass);
      // Never lose a panel: keep a grabbable header edge inside the workspace.
      drag.pending.x = Math.max(-(drag.origin.w - 64), Math.min(nx.value, Math.round(r.width) - 64));
      drag.pending.y = Math.max(0, Math.min(ny.value, Math.round(r.height) - 64));
      drag.guides = { x: nx.guide, y: ny.guide };
    } else {
      var nw = applySnap(drag.origin.w + dx, t.x.map(function (v) { return v - drag.origin.x; }), bypass);
      var nh = applySnap(drag.origin.h + dy, t.y.map(function (v) { return v - drag.origin.y; }), bypass);
      drag.pending.w = Math.max(MIN_W, Math.min(nw.value, Math.round(r.width)));
      drag.pending.h = Math.max(MIN_H, Math.min(nh.value, Math.round(r.height)));
      drag.guides = { x: null, y: null };
    }

    if (!drag.raf) {
      drag.raf = requestAnimationFrame(function () {
        drag.raf = 0;
        if (!drag) return;
        drag.frame.style.left = drag.pending.x + 'px';
        drag.frame.style.top = drag.pending.y + 'px';
        drag.frame.style.width = drag.pending.w + 'px';
        drag.frame.style.height = drag.pending.h + 'px';
        drawGuides(drag.guides && drag.guides.x, drag.guides && drag.guides.y);
      });
    }
  }

  function endGesture() {
    if (!drag) return;
    if (drag.raf) cancelAnimationFrame(drag.raf);
    drag.frame.classList.remove('ws-dragging');
    clearGuides();
    // Persist ONCE, at the end of the interaction — never per frame.
    setPlacement(drag.id, {
      x: Math.round(drag.pending.x), y: Math.round(drag.pending.y),
      width: Math.round(drag.pending.w), height: Math.round(drag.pending.h),
      expanded_height: Math.round(drag.pending.h),
    });
    drag = null;
  }

  document.addEventListener('pointerdown', function (e) {
    if (!W.enabled || !W.root) return;
    var handle = e.target.closest && e.target.closest('[data-wsresize]');
    if (handle && W.root.contains(handle)) { beginGesture(e, handle.getAttribute('data-wsresize'), 'resize'); return; }
    var head = e.target.closest && e.target.closest('[data-wsdrag]');
    if (head && W.root.contains(head)) beginGesture(e, head.getAttribute('data-wsdrag'), 'move');
  });
  document.addEventListener('pointermove', moveGesture);
  document.addEventListener('pointerup', endGesture);
  document.addEventListener('pointercancel', endGesture);

  /* ---- Boot ---------------------------------------------------------------------------------- */

  async function init(rootEl, enabled) {
    W.enabled = !!enabled;
    W.root = rootEl || null;
    if (!W.enabled || !W.root) return false;   // inert unless the server turned it on
    W.profile = profileForViewport();
    await load();
    render();
    return true;
  }

  // One delegated listener for the whole workspace — registered once, never per panel.
  document.addEventListener('click', onClick);

  // Profile switches (desktop <-> compact) re-render from the OTHER profile's saved placements;
  // neither profile overwrites the other (server keeps them isolated).
  window.addEventListener('resize', function () {
    if (!W.enabled) return;
    var next = profileForViewport();
    if (next === W.profile) return;
    W.profile = next;
    if (W.state && !W.state.profiles[next]) W.state.profiles[next] = {};
    render();
  });

  window.AnthillWorkspace = {
    init: init,
    register: register,
    render: render,
    panelIds: panelIds,
    _state: function () { return W.state; },     // test/debug surface only
    _placement: placement,
  };
})();
