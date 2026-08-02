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

   Deliberately NOT implemented yet (the directive asks the architecture to
   support them, not to ship them): reorder, resize, pin, saved layouts. Each is
   an ordering/size decision, and both are already data on the widget record —
   `order` and `size` — so adding persistence later means saving two fields, not
   restructuring this file.
   ───────────────────────────────────────────────────────────────────────────── */
(function () {
  'use strict';

  var G = {
    widgets: {},        // id -> definition
    order: [],          // registration order; the render order until layouts are savable
    root: null,
    mounted: false,
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

  /** The frame every widget gets: title, icon, states, refresh. */
  function frame(def) {
    var w = el('div', 'dg-widget');
    w.setAttribute('data-size', def.size);
    w.setAttribute('data-widget-id', def.id);
    w.setAttribute('role', 'region');
    w.setAttribute('aria-label', def.title);

    var head = el('div', 'dg-head');
    if (def.icon) head.appendChild(el('span', 'dg-icon', def.icon));
    head.appendChild(el('span', 'dg-title', def.title));

    var sub = el('span', 'dg-sub');
    sub.setAttribute('data-dg-sub', def.id);
    head.appendChild(sub);

    var refresh = el('button', 'dg-act', '↻');
    refresh.type = 'button';
    refresh.title = 'Refresh ' + def.title;
    refresh.setAttribute('aria-label', 'Refresh ' + def.title);
    refresh.addEventListener('click', function () { G.refresh(def.id); });
    head.appendChild(refresh);

    var body = el('div', 'dg-body');
    body.setAttribute('data-dg-body', def.id);

    w.appendChild(head);
    w.appendChild(body);
    return { widget: w, body: body };
  }

  function loading(def) {
    var s = el('div', 'dg-state');
    s.appendChild(el('span', 'dg-spin'));
    s.appendChild(el('span', null, 'Loading ' + def.title.toLowerCase() + '…'));
    return s;
  }

  /**
   * Render one widget into its frame. A renderer that throws produces an ERROR
   * state in that widget only — one failing widget must never blank the
   * dashboard, which is the whole point of an always-open operations console.
   */
  function mount(def, body) {
    body.textContent = '';
    body.appendChild(loading(def));
    try {
      body.textContent = '';
      def.render(body);
      if (!body.firstChild) {
        var e = el('div', 'dg-state');
        e.appendChild(el('span', null, def.empty));
        body.appendChild(e);
      }
    } catch (err) {
      body.textContent = '';
      var f = el('div', 'dg-state err');
      f.appendChild(el('span', null, 'Widget failed: ' + ((err && err.message) || 'unknown error')));
      body.appendChild(f);
    }
  }

  G.refresh = function (id) {
    var def = G.widgets[id];
    if (!def || !G.root) return;
    var body = G.root.querySelector('[data-dg-body="' + id + '"]');
    if (body) mount(def, body);
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
    rootEl.textContent = '';

    G.order.forEach(function (id) {
      var def = G.widgets[id];
      if (!def) return;
      var f = frame(def);
      rootEl.appendChild(f.widget);
      mount(def, f.body);
    });
    G.mounted = true;
  };

  G.ids = function () { return G.order.slice(); };

  window.AnthillGrid = G;
})();
