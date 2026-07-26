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
