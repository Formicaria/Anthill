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

  /* ---- Tab groups (Stage 4) -------------------------------------------------------------- *
   * A group is addressed as "g:<gid>" wherever a panel id is expected. placement() and
   * setPlacement() translate those refs onto the group's own record, which means the entire
   * Stage 3 machinery — pointer drag, resize, snap guides, z-order, save-at-pointerup — drives
   * groups with no second implementation. Group membership and geometry are validated in C#
   * (SanitizeTabGroups): a group with fewer than two panels dissolves and its survivor floats.
   * ------------------------------------------------------------------------------------------ */
  function tabGroups() { W.state.tab_groups = W.state.tab_groups || {}; return W.state.tab_groups; }
  function isGroupRef(ref) { return typeof ref === 'string' && ref.slice(0, 2) === 'g:'; }
  function groupIdOf(ref) { return ref.slice(2); }
  function groupOf(panelId) { return placement(panelId).tab_group; }
  function groupRefs() { return Object.keys(tabGroups()).map(function (g) { return 'g:' + g; }); }

  function placement(id) {
    if (isGroupRef(id)) {
      var g = tabGroups()[groupIdOf(id)] || {};
      return { display_state: 'visible', placement_mode: 'tabbed', pinned: false,
               x: g.x || 0, y: g.y || 0, width: g.width || 460, height: g.height || 280,
               z: g.z || 1, dock_side: null, tab_group: groupIdOf(id), opacity: 'solid' };
    }
    var p = placements()[id];
    if (p) return p;
    // v2.14.9: fall back to the panel's REGISTERED defaultPlacement so a first-run dashboard opens
    // in its designed layout instead of stacking every panel at one corner. The server still owns
    // validation/clamping once a layout has been saved.
    var d = (W.panels[id] && W.panels[id].defaultPlacement) || {};
    return { display_state: d.display_state || 'visible', placement_mode: d.mode || 'floating',
             x: d.x != null ? d.x : 40, y: d.y != null ? d.y : 40,
             width: d.width || 380, height: d.height || 240,
             expanded_height: d.height || 240, z: 1, pinned: false,
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
        // v2.15.0: overlays are app.js's key. Holding a copy here would let a stale value ride
        // along on the next panel save and clobber a fresh overlay change.
        delete W.state.topology_overlays;
      }
    } catch (e) { /* keep whatever we have; the shell must still render */ }
    if (!W.state) W.state = { schema_version: 1, locked: true, focus_mode: false, profiles: {} };
    if (!W.state.profiles) W.state.profiles = {};
    if (!W.state.profiles[W.profile]) W.state.profiles[W.profile] = {};
  }

  /**
   * Debounced save AFTER interaction, never continuously (spec: no save-per-pixel).
   *
   * v2.15.0: this used to run its own GET/PUT cycle on a 600ms timer, racing app.js's 350ms
   * writer over the same document — whichever PUT landed second discarded the other's change.
   * It now registers a mutator with the single writer in app.js, so both surfaces share one
   * debounce, one read, and one write. It deliberately assigns only the keys it owns and never
   * touches topology_overlays, which belongs to app.js (see load(), which strips it).
   */
  function save() {
    if (window.AnthillUiState) {
      window.AnthillUiState.queue(function (doc) {
        doc.dashboard_workspace = Object.assign({}, doc.dashboard_workspace, W.state);
      });
      return;
    }
    // Fallback for the (unsupported) case of this file loading without app.js.
    if (W.saveTimer) clearTimeout(W.saveTimer);
    W.saveTimer = setTimeout(async function () {
      W.saveTimer = null;
      try {
        var current = await window.api('/ui/state');
        var doc = (current && current.success && current.data) ? current.data : {};
        doc.dashboard_workspace = Object.assign({}, doc.dashboard_workspace, W.state);
        await window.api('/ui/state', 'PUT', doc);
      } catch (e) { /* a failed save must never break the console */ }
    }, 600);
  }

  function setPlacement(id, changes) {
    if (isGroupRef(id)) {
      var g = tabGroups()[groupIdOf(id)];
      if (!g) return;
      ['x', 'y', 'z'].forEach(function (k) { if (changes[k] !== undefined) g[k] = changes[k]; });
      if (changes.width !== undefined) g.width = changes.width;
      if (changes.height !== undefined) g.height = changes.height;
      save();
      return;
    }
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

  /**
   * A tab group renders as one frame whose header is a real tablist. Only the ACTIVE panel's
   * body is rendered, so inactive tabs cost nothing — the refreshPolicy:'visible' contract keeps
   * holding, and a stacked panel is genuinely paused rather than merely hidden.
   */
  function renderTabGroup(gid) {
    var g = tabGroups()[gid];
    if (!g || !g.panels || g.panels.length < 2) return null;   // C# dissolves these; be defensive
    var members = g.panels.filter(function (id) { return W.panels[id]; });
    if (members.length < 2) return null;
    var active = members.indexOf(g.active) >= 0 ? g.active : members[0];
    var def = W.panels[active];
    var ref = 'g:' + gid;
    var p = placement(ref);

    if (W.state.focus_mode) return null;   // groups are never pinned; focus mode shows pinned only

    var frame = el('section', 'ws-panel ws-group');
    frame.id = 'ws-panel-' + ref;
    frame.setAttribute('data-wspanel', ref);
    frame.setAttribute('aria-label', 'Panel group: ' + members.map(function (id) {
      return W.panels[id].title;
    }).join(', '));
    frame.style.left = p.x + 'px';
    frame.style.top = p.y + 'px';
    frame.style.width = p.width + 'px';
    frame.style.height = p.height + 'px';
    frame.style.zIndex = String(p.z || 1);

    var head = el('header', 'ws-head ws-group-head');
    head.setAttribute('data-wsdrag', ref);

    var tabs = el('div', 'ws-tabs');
    tabs.setAttribute('role', 'tablist');
    tabs.setAttribute('aria-label', 'Panels in this group');
    members.forEach(function (id) {
      var on = id === active;
      var t = el('button', 'ws-tab' + (on ? ' on' : ''), W.panels[id].title);
      t.type = 'button';
      t.setAttribute('role', 'tab');
      t.setAttribute('aria-selected', on ? 'true' : 'false');
      t.setAttribute('tabindex', on ? '0' : '-1');   // roving tabindex; arrows move between tabs
      t.setAttribute('data-wsact', 'tab-select');
      t.setAttribute('data-wsid', gid + '|' + id);
      tabs.appendChild(t);
    });
    head.appendChild(tabs);

    var controls = el('div', 'ws-controls');
    // Every drag-only capability needs a non-drag equivalent (accessibility rule).
    controls.appendChild(headerButton('tab-move-left', gid + '|' + active, 'Move tab left', '‹'));
    controls.appendChild(headerButton('tab-move-right', gid + '|' + active, 'Move tab right', '›'));
    controls.appendChild(headerButton('tab-detach', gid + '|' + active, 'Detach ' + def.title + ' from group', '⧉'));
    head.appendChild(controls);
    frame.appendChild(head);

    var body = el('div', 'ws-body');
    body.id = 'ws-body-' + active;
    body.setAttribute('role', 'tabpanel');
    frame.appendChild(body);
    var grip = el('div', 'ws-resize');
    grip.setAttribute('data-wsresize', ref);
    grip.setAttribute('aria-hidden', 'true');
    frame.appendChild(grip);
    try { def.render(body); }
    catch (e) {
      body.textContent = '';
      body.appendChild(el('div', 'ws-error', 'Panel failed to render: ' + (e && e.message ? e.message : 'unknown error')));
    }
    return frame;
  }

  /* ---- Docking (v2.15.0) ------------------------------------------------------------------ *
   * Docked panels live in one of four edge rails. Geometry is deliberately thin here: the rail
   * lays panels out with flexbox and the ONLY number the client stores is dock_size, which the
   * server clamps to MaxDockFraction so a dock can never bury the topology. That split is the
   * whole reason docking was safe to add — the plan deferred it because hand-rolled dock geometry
   * is where window managers accumulate bugs, so the geometry that matters lives in tested C#.
   * ------------------------------------------------------------------------------------------ */
  var DOCK_SIDES = ['left', 'right', 'top', 'bottom'];

  function dockedOn(side) {
    return panelIds().filter(function (id) {
      var p = placement(id);
      return p.placement_mode === 'docked' && p.dock_side === side
          && p.display_state !== 'hidden' && p.display_state !== 'minimized';
    }).sort(function (a, b) { return (placement(a).dock_order || 0) - (placement(b).dock_order || 0); });
  }

  /** Rail thickness = the largest dock_size any of its members asked for. */
  function railSize(side) {
    var ids = dockedOn(side);
    if (!ids.length) return 0;
    return ids.reduce(function (m, id) { return Math.max(m, placement(id).dock_size || 320); }, 0);
  }

  function renderDockRail(side) {
    var ids = dockedOn(side);
    if (!ids.length) return null;
    if (W.state.focus_mode) return null;

    var rail = el('div', 'ws-dock ws-dock-' + side);
    rail.id = 'ws-dock-' + side;
    rail.setAttribute('data-wsdock', side);
    var size = railSize(side);
    if (side === 'left' || side === 'right') rail.style.width = size + 'px';
    else rail.style.height = size + 'px';

    ids.forEach(function (id) {
      var def = W.panels[id];
      var frame = el('section', 'ws-panel ws-docked');
      frame.id = 'ws-panel-' + id;
      frame.setAttribute('data-wspanel', id);
      frame.setAttribute('aria-label', def.title + ' (docked ' + side + ')');

      var head = el('header', 'ws-head');
      head.setAttribute('data-wsdrag', id);       // drag out of the rail to refloat
      head.appendChild(el('h3', 'ws-title', def.title));
      var controls = el('div', 'ws-controls');
      controls.appendChild(headerButton('undock', id, 'Undock ' + def.title, '⇱'));
      if (def.minimizable) controls.appendChild(headerButton('minimize', id, 'Minimize panel', '—'));
      if (def.hideable) controls.appendChild(headerButton('hide', id, 'Hide panel', '✕'));
      head.appendChild(controls);
      frame.appendChild(head);

      var body = el('div', 'ws-body');
      body.id = 'ws-body-' + id;
      frame.appendChild(body);
      try { def.render(body); }
      catch (e) {
        body.textContent = '';
        body.appendChild(el('div', 'ws-error', 'Panel failed to render: ' + (e && e.message ? e.message : 'unknown error')));
      }
      rail.appendChild(frame);
    });

    // Rail resize grip: adjusts dock_size for every panel on this edge at once.
    var grip = el('div', 'ws-dock-grip');
    grip.setAttribute('data-wsdockresize', side);
    grip.setAttribute('aria-hidden', 'true');
    rail.appendChild(grip);
    return rail;
  }

  /** Drop-zone hints, shown only while a panel is being dragged. */
  function renderDockZones() {
    var wrap = el('div', 'ws-dockzones');
    wrap.id = 'ws-dockzones';
    wrap.setAttribute('aria-hidden', 'true');
    DOCK_SIDES.forEach(function (side) {
      var z = el('div', 'ws-dockzone ws-dockzone-' + side);
      z.setAttribute('data-wsdockzone', side);
      wrap.appendChild(z);
    });
    return wrap;
  }

  /** Which dock zone is the pointer in? Null when it is nowhere near an edge. */
  function dockZoneAt(pt) {
    if (!pt) return null;
    var r = workspaceRect();
    var x = pt.x - r.left, y = pt.y - r.top;
    if (x < 0 || y < 0 || x > r.width || y > r.height) return null;
    var edge = 56;
    if (x <= edge) return 'left';
    if (x >= r.width - edge) return 'right';
    if (y <= edge) return 'top';
    if (y >= r.height - edge) return 'bottom';
    return null;
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

      // v2.15.0: keyboard-reachable equivalent of drag-to-group / drag-out. Dragging is the
      // discoverable path; per the accessibility rule it must never be the ONLY path.
      if (on && !W.state.locked) {
        var gid = groupOf(id);
        var g = el('button', 'ws-module-sub');
        g.type = 'button';
        g.setAttribute('data-wsact', gid ? 'tab-detach' : 'group-with');
        g.setAttribute('data-wsid', gid ? (gid + '|' + id) : id);
        g.textContent = gid ? '    ⧉ Detach from group' : '    ⧉ Group with another panel';
        menu.appendChild(g);

        var d = el('button', 'ws-module-sub');
        d.type = 'button';
        if (p.placement_mode === 'docked') {
          d.setAttribute('data-wsact', 'undock');
          d.setAttribute('data-wsid', id);
          d.textContent = '    ⇱ Undock';
        } else {
          // Cycle through the edges so every dock side is reachable without a pointer.
          var nextSide = DOCK_SIDES[(DOCK_SIDES.indexOf(p.dock_side) + 1) % DOCK_SIDES.length];
          d.setAttribute('data-wsact', 'dock-to');
          d.setAttribute('data-wsid', id + '|' + nextSide);
          d.textContent = '    ⇲ Dock ' + nextSide;
        }
        menu.appendChild(d);
      }
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
      if (groupOf(id)) return;                 // rendered inside its group's frame instead
      if (placement(id).placement_mode === 'docked') return;   // rendered in its edge rail
      var node = renderPanel(W.panels[id]);
      if (node) layer.appendChild(node);
    });
    Object.keys(tabGroups()).forEach(function (gid) {
      var node = renderTabGroup(gid);
      if (node) layer.appendChild(node);
    });
    var guides = el('div', 'ws-guides');       // alignment guides drawn during a drag
    guides.id = 'ws-guides';
    guides.setAttribute('aria-hidden', 'true');
    layer.appendChild(guides);
    DOCK_SIDES.forEach(function (side) {
      var rail = renderDockRail(side);
      if (rail) W.root.appendChild(rail);
    });
    W.root.appendChild(renderDockZones());
    W.root.appendChild(layer);
    W.root.appendChild(renderTray());
  }

  /* ---- Actions ------------------------------------------------------------------------------ */

  function bringToFront(id) {
    var maxZ = 1;
    var ps = placements();
    Object.keys(ps).forEach(function (k) { maxZ = Math.max(maxZ, ps[k].z || 1); });
    var gs = tabGroups();
    Object.keys(gs).forEach(function (k) { maxZ = Math.max(maxZ, gs[k].z || 1); });
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

    /* ---- Dock actions ---- */
    'undock': function (id) {
      var p = placement(id);
      setPlacement(id, { placement_mode: 'floating', dock_side: null, dock_order: 0,
                         x: 120, y: 120, width: p.width || 380, height: p.height || 240 });
      render();
    },
    /* Non-drag docking, from the Modules menu. Ids arrive as "<panelId>|<side>". */
    'dock-to': function (ref) {
      var parts = String(ref).split('|');
      dockPanel(parts[0], parts[1]);
      render();
    },

    /* ---- Tab group actions. Ids arrive as "<gid>|<panelId>". ---- */
    'tab-select': function (ref) {
      var parts = String(ref).split('|'), g = tabGroups()[parts[0]];
      if (!g) return;
      g.active = parts[1]; save(); render();
      var t = document.querySelector('[data-wsact="tab-select"][data-wsid="' + ref + '"]');
      if (t) t.focus();                       // keep focus on the tab the user just activated
    },
    'tab-move-left':  function (ref) { moveTab(ref, -1); },
    'tab-move-right': function (ref) { moveTab(ref, 1); },
    'tab-detach': function (ref) {
      var parts = String(ref).split('|'), gid = parts[0], pid = parts[1];
      var g = tabGroups()[gid];
      if (!g) return;
      g.panels = g.panels.filter(function (x) { return x !== pid; });
      // Drop the detached panel just below the group so it is visibly separate, never off-screen.
      setPlacement(pid, { tab_group: null, placement_mode: 'floating',
                          x: (g.x || 0) + 24, y: (g.y || 0) + 32,
                          width: g.width || 460, height: g.height || 280 });
      if (g.panels.length < 2) dissolveGroup(gid);      // mirrors the C# rule exactly
      else if (g.active === pid) g.active = g.panels[0];
      save(); render();
    },
    /* Non-drag grouping, from the Modules menu: stack this panel onto the first other visible
       one. Drag-to-group is the discoverable path; this is the keyboard-reachable equivalent. */
    'group-with': function (id) {
      var target = panelIds().filter(function (o) {
        return o !== id && !groupOf(o) && placement(o).display_state === 'visible';
      })[0];
      if (target) { groupPanels(target, id); render(); }
    },
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

  function dockPanel(id, side) {
    if (DOCK_SIDES.indexOf(side) < 0) return;
    // Leaving a tab group is implicit: a panel cannot be docked and tabbed at once (the server
    // enforces the same rule, so the two can never disagree about where a panel lives).
    var gid = groupOf(id);
    if (gid) {
      var g = tabGroups()[gid];
      if (g) {
        g.panels = g.panels.filter(function (x) { return x !== id; });
        if (g.panels.length < 2) dissolveGroup(gid);
        else if (g.active === id) g.active = g.panels[0];
      }
    }
    setPlacement(id, { placement_mode: 'docked', dock_side: side, tab_group: null,
                       dock_order: dockedOn(side).length,
                       dock_size: placement(id).dock_size || 320 });
  }

  function moveTab(ref, delta) {
    var parts = String(ref).split('|'), g = tabGroups()[parts[0]];
    if (!g) return;
    var i = g.panels.indexOf(parts[1]), j = i + delta;
    if (i < 0 || j < 0 || j >= g.panels.length) return;
    g.panels.splice(j, 0, g.panels.splice(i, 1)[0]);
    save(); render();
  }

  function dissolveGroup(gid) {
    var g = tabGroups()[gid];
    if (!g) return;
    (g.panels || []).forEach(function (pid) {
      setPlacement(pid, { tab_group: null, placement_mode: 'floating',
                          x: g.x || 0, y: g.y || 0, width: g.width || 460, height: g.height || 280 });
    });
    delete tabGroups()[gid];
  }

  /** Stack `moving` onto `host`, creating the group if `host` is not already in one. */
  function groupPanels(host, moving) {
    if (host === moving) return;
    var gid = groupOf(host);
    if (!gid) {
      var hp = placement(host);
      gid = 'g' + Date.now().toString(36);
      tabGroups()[gid] = { panels: [host], active: host,
                           x: hp.x, y: hp.y, width: hp.width, height: hp.height, z: hp.z || 1 };
      setPlacement(host, { tab_group: gid, placement_mode: 'tabbed' });
    }
    var g = tabGroups()[gid];
    if (g.panels.indexOf(moving) < 0) g.panels.push(moving);
    g.active = moving;
    setPlacement(moving, { tab_group: gid, placement_mode: 'tabbed' });
    save();
  }

  /** Arrow-key navigation across a tablist, per the WAI-ARIA tabs pattern. */
  function onTabKeydown(e) {
    var tab = e.target.closest ? e.target.closest('[role="tab"]') : null;
    if (!tab || !W.root || !W.root.contains(tab)) return;
    var keys = { ArrowLeft: -1, ArrowRight: 1, Home: 'first', End: 'last' };
    if (!(e.key in keys)) return;
    var list = Array.prototype.slice.call(tab.parentNode.querySelectorAll('[role="tab"]'));
    var i = list.indexOf(tab);
    var next = keys[e.key] === 'first' ? 0
             : keys[e.key] === 'last' ? list.length - 1
             : (i + keys[e.key] + list.length) % list.length;
    e.preventDefault();
    var el2 = list[next];
    if (el2) ACTIONS['tab-select'](el2.getAttribute('data-wsid'));
  }

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

  function showDockHint(side) {
    var wrap = document.getElementById('ws-dockzones');
    if (!wrap) return;
    wrap.classList.toggle('on', !!side);
    DOCK_SIDES.forEach(function (s2) {
      var z = wrap.querySelector('[data-wsdockzone="' + s2 + '"]');
      if (z) z.classList.toggle('hot', s2 === side);
    });
  }
  function clearDockHint() { showDockHint(null); }

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

    drag.last = { x: e.clientX, y: e.clientY };   // where the drop lands (tab grouping / docking)
    if (drag.mode === 'move' && !isGroupRef(drag.id)) showDockHint(dockZoneAt(drag.last));
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

  /**
   * Which panel header is under the pointer, excluding the one being dragged? Resolved from the
   * real DOM at drop time rather than from cached rectangles, so it cannot disagree with what the
   * operator can actually see.
   */
  function dropTargetPanel(pt, exceptRef) {
    if (!pt) return null;
    var stack = document.elementsFromPoint ? document.elementsFromPoint(pt.x, pt.y) : [];
    for (var i = 0; i < stack.length; i++) {
      var head = stack[i].closest && stack[i].closest('[data-wsdrag]');
      if (!head) continue;
      var ref = head.getAttribute('data-wsdrag');
      if (ref === exceptRef) continue;
      return ref;
    }
    return null;
  }

  function endGesture() {
    if (!drag) return;
    if (drag.raf) cancelAnimationFrame(drag.raf);
    drag.frame.classList.remove('ws-dragging');
    clearGuides();
    clearDockHint();

    // Dropping into an edge zone docks. Checked BEFORE tab grouping: an edge zone is an explicit
    // aim at the edge, whereas a header underneath it may just be what happens to be there.
    if (drag.mode === 'move' && !isGroupRef(drag.id)) {
      var zone = dockZoneAt(drag.last);
      if (zone) {
        dockPanel(drag.id, zone);
        drag = null;
        clearDockHint();
        render();
        return;
      }
    }

    // Drop a panel onto another panel's (or group's) header to stack them into tabs. Only in
    // 'move' mode, and never for a group dropped onto anything — merging two groups silently
    // would lose one group's geometry, so it is simply not offered.
    if (drag.mode === 'move' && !isGroupRef(drag.id)) {
      var onto = dropTargetPanel(drag.last, drag.id);
      if (onto) {
        var host = isGroupRef(onto) ? (tabGroups()[groupIdOf(onto)] || {}).active : onto;
        if (host && host !== drag.id) {
          groupPanels(host, drag.id);
          drag = null;
          render();
          return;
        }
      }
    }

    // Persist ONCE, at the end of the interaction — never per frame.
    setPlacement(drag.id, {
      x: Math.round(drag.pending.x), y: Math.round(drag.pending.y),
      width: Math.round(drag.pending.w), height: Math.round(drag.pending.h),
      expanded_height: Math.round(drag.pending.h),
    });
    drag = null;
  }

  /* Rail resizing writes dock_size for every panel on that edge, so it is its own small gesture
     rather than a special case threaded through the panel drag path. */
  var railDrag = null;
  document.addEventListener('pointermove', function (e) {
    if (!railDrag) return;
    var r = workspaceRect();
    var size = railDrag.side === 'left'   ? e.clientX - r.left
             : railDrag.side === 'right'  ? r.right - e.clientX
             : railDrag.side === 'top'    ? e.clientY - r.top
             :                              r.bottom - e.clientY;
    // Mirror the server's invariant locally so the drag feels bounded instead of snapping back
    // after the round trip. The server clamp remains authoritative.
    var axis = (railDrag.side === 'left' || railDrag.side === 'right') ? r.width : r.height;
    railDrag.size = Math.max(180, Math.min(Math.round(size), Math.round(axis * 0.60)));
    var rail = document.getElementById('ws-dock-' + railDrag.side);
    if (rail) {
      if (railDrag.side === 'left' || railDrag.side === 'right') rail.style.width = railDrag.size + 'px';
      else rail.style.height = railDrag.size + 'px';
    }
    e.preventDefault();
  });
  document.addEventListener('pointerup', function () {
    if (!railDrag) return;
    dockedOn(railDrag.side).forEach(function (id) { setPlacement(id, { dock_size: railDrag.size }); });
    railDrag = null;
    render();
  });

  document.addEventListener('pointerdown', function (e) {
    if (!W.enabled || !W.root) return;
    if (!W.state.locked) {
      var railHandle = e.target.closest && e.target.closest('[data-wsdockresize]');
      if (railHandle && W.root.contains(railHandle)) {
        railDrag = { side: railHandle.getAttribute('data-wsdockresize'), size: 0 };
        e.preventDefault();
        return;
      }
    }
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
  document.addEventListener('keydown', onTabKeydown);
  /* Focus mode hides every unpinned panel, so its exit must never depend on finding a specific
     button. Escape always leaves it, and the toolbar toggle stays tab-reachable regardless. */
  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape' || !W.enabled) return;
    if (W.state && W.state.focus_mode) {
      W.state.focus_mode = false;
      save();
      render();
      var btn = W.root && W.root.querySelector('[data-wsact="toggle-focus"]');
      if (btn) btn.focus();
    }
  });

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
