/* ─────────────────────────────────────────────────────────────────────────────
   ANTHILL dashboard grid (v3.3.0) — the widget framework.

   Replaces the floating-panel workspace. The paradigm it removes: widgets as
   absolutely-positioned frames layered over the colony canvas, each carrying its
   own x/y/w/h, z-order, dock edge and tab group. That produced overlap, clipping
   at unexpected viewport sizes, and widgets stranded off-screen — and it made
   the Colony a background rather than a display.

   What is deliberately KEPT from the old engine, because it is the good part:
   a widget declares `render(el)` and mounts an EXISTING element by id. Every
   data renderer in app.js keeps writing to the same node it always wrote to.
   This framework changes where widgets sit, not what they contain — which is
   why a layout replacement does not become a rewrite of the whole console.

   SHIPPED here: hide/show, reorder, lock, reset, and layout persistence. These
   were originally deferred as "architecture supports them, not shipped" — but
   the workspace this replaces already HAD them, so deferring was not scoping,
   it was a capability regression. Persistence goes through the host's single
   ui_state writer; a second independent writer against the same document is how
   app.js and the old workspace used to discard each other's changes.

   Still NOT implemented: per-widget resize and pin. Both are size decisions and
   `size` is already data on the widget record, so they remain additive.
   ───────────────────────────────────────────────────────────────────────────── */
(function () {
  'use strict';

  var G = {
    widgets: {},        // id -> definition
    order: [],          // registration order = the default arrangement
    root: null,
    mounted: false,
    // Operator layout: which widgets are hidden, and the order they render in. Persisted through
    // the host's single ui_state writer rather than a second cycle of its own — app.js and the
    // old workspace once ran independent debounced read-modify-writes against the same document
    // and silently discarded each other's changes.
    layout: { hidden: {}, order: null, locked: true },
    _menuOpen: false,   // view state, not layout: never persisted
    _frames: {},        // id -> {widget, head, body}; built once, moved thereafter
    onLayoutChange: null,   // set by the host to persist
  };

  var SIZES = { small: 1, medium: 1, large: 1, colony: 1 };

  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) n.textContent = text;
    return n;
  }

  /**
   * register({ id, title, icon, size, render, empty })
   *
   * `size` is an operational judgement, not a pixel measurement: how important
   * is this widget relative to the others. The breakpoints decide what that
   * means in columns at any given viewport.
   */
  G.register = function (def) {
    if (!def || !def.id || typeof def.render !== 'function') return;
    if (!G.widgets[def.id]) G.order.push(def.id);
    G.widgets[def.id] = {
      id: def.id,
      title: def.title || def.id,
      icon: def.icon || '',
      size: SIZES[def.size] ? def.size : 'medium',
      render: def.render,
      empty: def.empty || 'No data yet.',
    };
  };

  /**
   * The frame every widget gets: title, icon, states, refresh.
   *
   * Frames are built ONCE and cached. This is not an optimisation — it is required for
   * correctness. A widget body ADOPTS an existing element (same node, same id, same writer), so
   * destroying a frame destroys the console's real content, not a copy of it. Re-rendering by
   * clearing the root wiped every widget body the first time a saved layout loaded.
   */
  function frame(def) {
    var w = el('div', 'dg-widget');
    w.setAttribute('data-size', def.size);
    w.setAttribute('data-widget-id', def.id);
    w.setAttribute('role', 'region');
    w.setAttribute('aria-label', def.title);

    var head = el('div', 'dg-head');

    var body = el('div', 'dg-body');
    body.setAttribute('data-dg-body', def.id);

    w.appendChild(head);
    w.appendChild(body);
    fillHead(def, head);
    return { widget: w, head: head, body: body };
  }

  /** Rebuild only the header. Safe to call on every render: it never touches adopted content. */
  function fillHead(def, head) {
    head.textContent = '';
    if (def.icon) head.appendChild(el('span', 'dg-icon', def.icon));
    head.appendChild(el('span', 'dg-title', def.title));

    var sub = el('span', 'dg-sub');
    sub.setAttribute('data-dg-sub', def.id);
    head.appendChild(sub);

    // Customise controls, present only when the layout is unlocked. Buttons rather than drag:
    // dragging is discoverable but must never be the ONLY path — the workspace established that
    // rule for accessibility and it survives the layout engine.
    if (!G.layout.locked) {
      [['◀', -1, 'Move ' + def.title + ' earlier'], ['▶', 1, 'Move ' + def.title + ' later']]
        .forEach(function (spec) {
          var b = el('button', 'dg-act', spec[0]);
          b.type = 'button'; b.title = spec[2]; b.setAttribute('aria-label', spec[2]);
          b.addEventListener('click', function () { G.move(def.id, spec[1]); });
          head.appendChild(b);
        });
      var hide = el('button', 'dg-act', '✕');
      hide.type = 'button';
      hide.title = 'Hide ' + def.title;
      hide.setAttribute('aria-label', 'Hide ' + def.title);
      hide.addEventListener('click', function () { G.setHidden(def.id, true); });
      head.appendChild(hide);
    }

    var refresh = el('button', 'dg-act', '↻');
    refresh.type = 'button';
    refresh.title = 'Refresh ' + def.title;
    refresh.setAttribute('aria-label', 'Refresh ' + def.title);
    refresh.addEventListener('click', function () { G.refresh(def.id); });
    head.appendChild(refresh);
  }

  function loading(def) {
    var s = el('div', 'dg-state');
    s.appendChild(el('span', 'dg-spin'));
    s.appendChild(el('span', null, 'Loading ' + def.title.toLowerCase() + '…'));
    return s;
  }

  /**
   * Render one widget into its frame.
   *
   * NON-DESTRUCTIVE by construction. A renderer here ADOPTS an existing element — the same node the
   * data path already writes to — so clearing the body would not reset a view, it would delete the
   * console's real content and leave the renderer writing to a detached node. That is what the
   * refresh button used to do. Only framework-owned placeholders are ever removed.
   *
   * A renderer that throws produces an ERROR state in THAT widget only: on a console meant to be
   * left open all day, one bad renderer must not blank the dashboard.
   */
  function mount(def, body) {
    // Drop any placeholder this framework put there last time; never touch anything else.
    Array.prototype.slice.call(body.children).forEach(function (n) {
      if (n.classList && n.classList.contains('dg-state')) body.removeChild(n);
    });

    var placeholder = null;
    if (!body.firstChild) { placeholder = loading(def); body.appendChild(placeholder); }

    try {
      def.render(body);
    } catch (err) {
      if (placeholder && placeholder.parentNode) placeholder.parentNode.removeChild(placeholder);
      var f = el('div', 'dg-state err');
      f.appendChild(el('span', null, 'Widget failed: ' + ((err && err.message) || 'unknown error')));
      body.appendChild(f);
      return;
    }

    if (placeholder && placeholder.parentNode) placeholder.parentNode.removeChild(placeholder);

    if (!body.firstChild) {
      var e = el('div', 'dg-state');
      e.appendChild(el('span', null, def.empty));
      body.appendChild(e);
    }
  }

  /**
   * Mark widgets that have nothing to say, so they stop occupying a full card.
   *
   * Measured, not inferred. Most "No jobs yet." messages come from the app's OWN renderers inside
   * adopted nodes, not from this framework's empty state — so asking "did I render a placeholder"
   * misses almost every case that matters. Actual content height is the only signal that sees both.
   *
   * Runs after layout (rAF) because content height is meaningless before the browser has laid the
   * grid out.
   *
   * The floor for a quiet widget is COMPUTED here rather than declared in CSS. Two failed attempts
   * are the reason, and both are worth remembering:
   *
   *   - A fixed smaller floor (96px) is a guess about content that varies; it clipped four widgets
   *     whose one-line empty state rendered 81px tall.
   *   - Removing the floor — `min-height: 0` or `max-content` — collapses the grid row to 2px and
   *     the cards then overlap each other. `.dg-widget` sets `overflow: hidden`, which makes it a
   *     scroll container, and a scroll container contributes nothing to an `auto` row's height. The
   *     row must be given a definite size by someone; CSS cannot derive it.
   *
   * So JS, which has already measured the content, writes the exact height that content needs.
   */
  var QUIET_BELOW_PX = 64;
  function markQuiet() {
    if (!G.root) return;
    requestAnimationFrame(function () {
      Array.prototype.forEach.call(G.root.querySelectorAll('.dg-widget'), function (w) {
        if (w.getAttribute('data-size') === 'colony') return;    // the map is never "quiet"
        var body = w.querySelector('.dg-body');
        if (!body) return;

        // Measure at the NATURAL height. A floor left over from the previous pass would be included
        // in this pass's measurement, and the card would grow a little on every cycle.
        w.style.minHeight = '';

        var only = body.children.length === 1 ? body.firstElementChild : null;
        var placeholder = only && only.classList && only.classList.contains('dg-state');
        var content = 0;
        if (placeholder) {
          // A placeholder is stretched (`height: 100%`) to centre itself in a full card, so its box
          // reports the card's height, not its own. Its intrinsic minimum is the honest number.
          content = parseFloat(getComputedStyle(only).minHeight) || 0;
        } else {
          Array.prototype.forEach.call(body.children, function (n) {
            content += n.getBoundingClientRect().height;
          });
        }

        var quiet = content > 0 && content < QUIET_BELOW_PX;
        w.classList.toggle('dg-quiet', quiet);
        if (!quiet) return;

        var head = w.querySelector('.dg-head');
        var cs = getComputedStyle(body);
        var pad = (parseFloat(cs.paddingTop) || 0) + (parseFloat(cs.paddingBottom) || 0);
        var borders = w.offsetHeight - w.clientHeight;
        var headH = head ? head.getBoundingClientRect().height : 0;
        w.style.minHeight = Math.ceil(headH + content + pad + borders) + 'px';
      });
    });
  }

  G.refresh = function (id) {
    var def = G.widgets[id];
    if (!def || !G.root) return;
    var f = G._frames[id];
    if (f) { mount(def, f.body); markQuiet(); }
  };

  G.refreshAll = function () { G.order.forEach(G.refresh); };

  /** Set the small right-aligned status text in a widget header. */
  G.setStatus = function (id, text) {
    if (!G.root) return;
    var s = G.root.querySelector('[data-dg-sub="' + id + '"]');
    if (s) s.textContent = text || '';
  };

  /**
   * Build the grid. Ordering rule: the Colony is pinned to the visual centre —
   * everything registered before it renders above, everything after renders
   * below. That is what makes the layout read as "operations console with a
   * mission display" rather than a list of cards that happens to contain a map.
   */
  G.mount = function (rootEl) {
    if (!rootEl) return;
    G.root = rootEl;
    rootEl.classList.add('dg-root');
    G.mounted = true;
    G.render();
  };

  /**
   * Lay the grid out from the current layout.
   *
   * MOVES cached frames rather than rebuilding them. appendChild on an existing node relocates it,
   * so reordering costs nothing and — critically — never destroys a widget body. A hidden widget's
   * frame is detached but RETAINED, with its adopted content still inside it, so re-showing it is
   * a re-append rather than a re-adoption of a node that no longer exists.
   */
  G.render = function () {
    var rootEl = G.root;
    if (!rootEl) return;
    rootEl.classList.toggle('dg-unlocked', !G.layout.locked);

    effectiveOrder().forEach(function (id) {
      var def = G.widgets[id];
      if (!def) return;
      var f = G._frames[id];
      if (!f) {
        f = G._frames[id] = frame(def);
        mount(def, f.body);          // first build only: adopt the renderer's element
      } else {
        fillHead(def, f.head);       // lock state may have changed
      }
      if (G.isHidden(id)) { if (f.widget.parentNode) f.widget.parentNode.removeChild(f.widget); }
      else rootEl.appendChild(f.widget);   // append = move into the right position
    });
    markQuiet();
  };

  /** Re-evaluate which widgets are quiet. Cheap, and safe to call on a timer. */
  G.remeasure = function () { markQuiet(); };

  G.ids = function () { return G.order.slice(); };

  /**
   * The toolbar: lock/unlock, the widget list, reset. Rendered into a host-supplied element so the
   * grid does not have to know where the page wants it.
   *
   * The widget list is what makes hiding RECOVERABLE. The workspace learned this the hard way —
   * hiding every overlay had to stay undoable from a control that is always present, or an
   * operator could hide something and have no way back to it.
   */
  G.renderToolbar = function (host) {
    if (!host) return;
    host.textContent = '';
    // dg-keep is what exempts the toolbar from the rule that takes classic page content out of
    // flow. Assigning className outright would drop it on every re-render — and the exemption is
    // needed most right after a re-render, so the toolbar would vanish the first time the operator
    // toggled Customise.
    host.classList.add('dg-toolbar', 'dg-keep');

    var lock = el('button', 'dg-tool', G.layout.locked ? '\u25a0 Customise' : '\u2713 Done');
    lock.type = 'button';
    lock.title = G.layout.locked ? 'Unlock the dashboard to reorder or hide widgets' : 'Lock the dashboard';
    lock.setAttribute('aria-pressed', G.layout.locked ? 'false' : 'true');
    lock.addEventListener('click', function () { G.setLocked(!G.layout.locked); G.renderToolbar(host); });
    host.appendChild(lock);

    var widgets = el('button', 'dg-tool', '\u2637 Widgets');
    widgets.type = 'button';
    // Open state survives the re-render. Toggling a widget rebuilds this toolbar, which used to
    // close the picker \u2014 so hiding three widgets meant opening the menu three times, and the list
    // you were working through vanished the moment you used it.
    widgets.setAttribute('aria-expanded', G._menuOpen ? 'true' : 'false');
    var menu = el('div', 'dg-menu');
    menu.hidden = !G._menuOpen;
    widgets.addEventListener('click', function () {
      G._menuOpen = !G._menuOpen;
      menu.hidden = !G._menuOpen;
      widgets.setAttribute('aria-expanded', G._menuOpen ? 'true' : 'false');
    });

    effectiveOrder().forEach(function (id) {
      var def = G.widgets[id];
      if (!def) return;
      var row = el('button', 'dg-menu-row', (G.isHidden(id) ? '\u2610 ' : '\u2611 ') + def.title);
      row.type = 'button';
      row.setAttribute('aria-pressed', G.isHidden(id) ? 'false' : 'true');
      row.addEventListener('click', function () { G.setHidden(id, !G.isHidden(id)); G.renderToolbar(host); });
      menu.appendChild(row);
    });

    var reset = el('button', 'dg-tool', '\u21ba Reset layout');
    reset.type = 'button';
    reset.title = 'Restore the default arrangement';
    reset.addEventListener('click', function () { G.resetLayout(); G.renderToolbar(host); });

    host.appendChild(widgets);
    host.appendChild(reset);
    host.appendChild(menu);
  };

  // ---- operator layout ------------------------------------------------------------------------

  /** The render order: the operator's saved order where present, registration order otherwise. */
  function effectiveOrder() {
    var saved = G.layout.order;
    if (!saved || !saved.length) return G.order.slice();
    var known = saved.filter(function (id) { return G.widgets[id]; });
    // Widgets added by a later release are appended rather than dropped — a saved layout must not
    // be able to hide a NEW widget the operator has never seen. That is the same defect that kept
    // the Mission Composer invisible for four releases.
    G.order.forEach(function (id) { if (known.indexOf(id) < 0) known.push(id); });
    return known;
  }

  G.isHidden = function (id) { return !!G.layout.hidden[id]; };

  G.setHidden = function (id, hidden) {
    if (!G.widgets[id]) return;
    if (hidden) G.layout.hidden[id] = true; else delete G.layout.hidden[id];
    G.persist(); G.render();
  };

  G.move = function (id, delta) {
    var order = effectiveOrder();
    var i = order.indexOf(id);
    if (i < 0) return;
    var j = i + delta;
    if (j < 0 || j >= order.length) return;
    order.splice(j, 0, order.splice(i, 1)[0]);
    G.layout.order = order;
    G.persist(); G.render();
  };

  G.setLocked = function (locked) { G.layout.locked = !!locked; G.persist(); G.render(); };

  /** Back to the shipped arrangement: nothing hidden, registration order, locked. */
  G.resetLayout = function () {
    G.layout = { hidden: {}, order: null, locked: true };
    G.persist(); G.render();
  };

  G.applyLayout = function (saved) {
    if (!saved || typeof saved !== 'object') return;
    G.layout.hidden = (saved.hidden && typeof saved.hidden === 'object') ? saved.hidden : {};
    G.layout.order = Array.isArray(saved.order) ? saved.order.slice() : null;
    G.layout.locked = saved.locked !== false;
    if (G.mounted) G.render();
  };

  G.persist = function () {
    if (typeof G.onLayoutChange === 'function') {
      G.onLayoutChange({ hidden: G.layout.hidden, order: effectiveOrder(), locked: G.layout.locked });
    }
  };

  window.AnthillGrid = G;
})();
