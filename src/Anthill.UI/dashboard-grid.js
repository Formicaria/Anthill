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
    // `spans` holds a FRACTION of the row (0 < f <= 1), not a column count. A count would mean a
    // different width at every breakpoint — half the dashboard at 12 columns is a quarter of it at
    // 24 — and the operator arranged a proportion, not a number of tracks. Heights are px, because
    // a height means the same thing at any width.
    // v0.3.8.56 (operator's third correction — FREE PLACEMENT): `pos` is each widget's own
    // cell rect origin {x:0..5, y:0..}. A widget lives WHERE THE OPERATOR PUT IT; order[] keeps
    // tab order, stacking on narrow screens, and the auto-placement of anything unplaced.
    layout: { hidden: {}, order: null, locked: true, spans: {}, heights: {}, pos: {} },
    defaults: null,     // the host's first-run view; also what "Reset layout" restores
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
   * Size every widget to its content, so no card holds more empty space than it needs.
   *
   * Measured, not inferred. Most "No jobs yet." messages come from the app's OWN renderers inside
   * adopted nodes, not from this framework's empty state — so asking "did I render a placeholder"
   * misses almost every case that matters. Actual content height is the only signal that sees both.
   *
   * This started as an empty-card fix and became a general one, because the screenshot showed the
   * real cause: a populated widget with 140px of content was also holding a 230px card, so the
   * FLOOR was padding the rows, not the idle widgets stretching them. Fitting every widget to its
   * content took ~11% off the dashboard's height. Grid rows still take the tallest card in the row,
   * which keeps bottoms aligned — ragged rows read as broken rather than dense.
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

  // Resize bounds. The floor is a header plus a usable line of content — below that a widget is
  // a title bar that lies about having content. The ceiling stops one widget from becoming a page.
  var MIN_H = 96;
  var MAX_H = 1600;

  // At or below this column count the grid is a single stack and operator widths do not apply.
  var STACK_BELOW_COLS = 4;
  // ---- FREE PLACEMENT (v0.3.8.56, operator's third correction) --------------------------------
  // Order-based flow could never say "put it THERE": every insertion repacked the whole board,
  // and the live DOM re-insertion on dragover made widgets churn under a mere click. Placement is
  // explicit now: each widget owns a cell rect (x, y, w, h); a drag targets a cell; widgets are
  // pushed DOWN only when actually overlapped; a hole the operator makes is a hole that stays.
  // Nothing in the DOM moves during a drag — only inline grid positions change.
  G._hCells = {};   // auto-measured heights in cells (markQuiet writes; user heights outrank)

  function widthCellsOf(id) {
    var f = G.layout.spans[id];
    if (typeof f === 'number' && f > 0 && f <= 1) return Math.max(1, Math.min(6, Math.round(f * 6)));
    var size = (G.widgets[id] || {}).size;
    return size === 'small' ? 1 : size === 'large' ? 3 : size === 'colony' ? 6 : 2;
  }
  function heightCellsOf(id) {
    var h = G.layout.heights[id];
    if (typeof h === 'number' && h > 0) {
      var cell = cellHeight();
      var gap = parseFloat(getComputedStyle(G.root).rowGap) || 0;
      return Math.max(1, Math.min(6, Math.round((h + gap) / (cell + gap))));
    }
    return G._hCells[id] || ((G.widgets[id] || {}).size === 'colony' ? COLONY_CELLS : 1);
  }
  function overlapsRect(a, b) {
    return a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
  }
  function rectsFor(draftPos) {
    var pos = draftPos || G.layout.pos;
    return effectiveOrder().filter(function (id) { return !G.isHidden(id) && G.widgets[id]; })
      .map(function (id) {
        var p = pos[id];
        var w = widthCellsOf(id);
        // A widget widened after it was placed must not hang off the board's right edge.
        return { id: id, x: p ? Math.min(p.x, 6 - w) : -1, y: p ? p.y : -1, w: w, h: heightCellsOf(id) };
      });
  }
  /** First-fit top-left for anything unplaced, in reading order; placed rects are respected. */
  function autoPlace(rects) {
    var fixed = rects.filter(function (r) { return r.x >= 0 && r.y >= 0; });
    rects.forEach(function (r) {
      if (r.x >= 0 && r.y >= 0) return;
      for (var y = 0; y < 500; y++) {
        for (var x = 0; x + r.w <= 6; x++) {
          var probe = { x: x, y: y, w: r.w, h: r.h };
          var clash = fixed.some(function (f) { return overlapsRect(probe, f); });
          if (!clash) { r.x = x; r.y = y; fixed.push(r); return; }
        }
      }
    });
    return rects;
  }
  /** The pinned rect (a live drag) stays put; anything it lands on is pushed DOWN past it, in
   * reading order. No upward compaction: gaps are the operator's to keep. */
  function resolveCollisions(rects, pinnedId) {
    var fixed = [];
    rects.forEach(function (r) { if (r.id === pinnedId) fixed.push(r); });
    rects.slice().sort(function (a, b) { return a.y - b.y || a.x - b.x; }).forEach(function (r) {
      if (r.id === pinnedId) return;
      var guard = 0;
      while (guard++ < 300 && fixed.some(function (f) { return overlapsRect(r, f); })) r.y++;
      fixed.push(r);
    });
    return rects;
  }
  /** Write every widget's inline grid position from its cell rect. Stack mode (narrow) clears
   * inline placement and lets the breakpoint's own single-column flow apply. */
  function placeAll(draftPos, pinnedId) {
    if (!G.root) return;
    // A live drag OWNS the board. Re-places that arrive without a draft mid-gesture — the 4s
    // remeasure, a poller's widget refresh, a render — used to stomp the preview back to the
    // SAVED positions under the pointer, then the stale same-cell check kept the preview from
    // ever coming back: the exact churn reported over the colony widget. They re-apply the
    // drag's draft instead, and nothing adopts positions while the gesture is in the air.
    if (!draftPos && dragDraft && dragId) { draftPos = dragDraft; pinnedId = dragId; }
    var cols = columnCount();
    var widgets = G.root.querySelectorAll('.dg-widget');
    if (cols <= STACK_BELOW_COLS) {
      Array.prototype.forEach.call(widgets, function (w) { w.style.gridColumn = ''; w.style.gridRow = ''; });
      return;
    }
    var per = Math.max(1, Math.round(cols / 6));
    var rects = resolveCollisions(autoPlace(rectsFor(draftPos)), pinnedId);
    var byId = {};
    rects.forEach(function (r) { byId[r.id] = r; });
    // Outside a drag preview, the resolved arrangement IS the model — first-run auto-placement
    // and collision pushes are adopted so the next render starts from what is on screen.
    if (!draftPos) rects.forEach(function (r) { G.layout.pos[r.id] = { x: r.x, y: r.y }; });
    Array.prototype.forEach.call(widgets, function (w) {
      var r = byId[w.getAttribute('data-widget-id')];
      if (!r) return;
      w.style.gridColumn = (r.x * per + 1) + ' / span ' + (r.w * per);
      w.style.gridRow = (r.y + 1) + ' / span ' + r.h;
    });
  }

  /* v0.3.8.56 (operator's second correction) — THE CELL GRID.
   * Free-height masonry made every widget a different height and the board read as clunky. The
   * unit is a CELL now: rows are 6 cells wide, --dg-cell-h tall, and every widget occupies
   * (a)x(b) whole cells — every combination available to every widget. markQuiet still measures
   * content, but the answer it writes is CELLS: enough to hold the content (capped at
   * AUTO_MAX_CELLS so a long list scrolls instead of growing the page), one for the quiet.
   * Operator sizes are cells too, quantized on resize. Content fills its cell and scrolls. */
  var WIDTH_CELLS = 6;        // a row of the board, whatever the column count underneath
  var AUTO_MAX_CELLS = 2;     // auto-fit ceiling; the operator may size taller by hand
  var COLONY_CELLS = 2;       // the map's default home
  function cellHeight() {
    return parseFloat(getComputedStyle(G.root).getPropertyValue('--dg-cell-h')) || 236;
  }
  function cellsForPx(px, rowGap) {
    var cell = cellHeight();
    return Math.max(1, Math.min(WIDTH_CELLS, Math.ceil((px + rowGap) / (cell + rowGap))));
  }

  function markQuiet() {
    if (!G.root) return;
    requestAnimationFrame(function () {
      var rootCs = getComputedStyle(G.root);
      var rowGap = parseFloat(rootCs.rowGap) || 0;
      Array.prototype.forEach.call(G.root.querySelectorAll('.dg-widget'), function (w) {
        var id = w.getAttribute('data-widget-id');
        var cells = 1;

        // Clear any legacy inline floor BEFORE measuring — earlier builds wrote exact-content
        // min-heights, and a floor left in place would be part of its own measurement.
        w.style.minHeight = '';

        if (w.hasAttribute('data-user-h')) {
          // An operator-set height is not a measurement to be improved on. Without this the 4s
          // remeasure would quietly undo every resize a few seconds after it was made.
          return;   // heightCellsOf reads the stored height directly
        } else if (w.getAttribute('data-size') === 'colony') {
          cells = COLONY_CELLS;   // the map is never "quiet" and owns a two-cell home
        } else {
          var body = w.querySelector('.dg-body');
          var content = 0;
          if (body) {
            var only = body.children.length === 1 ? body.firstElementChild : null;
            var placeholder = only && only.classList && only.classList.contains('dg-state');
            if (placeholder) {
              content = parseFloat(getComputedStyle(only).minHeight) || 0;
            } else {
              Array.prototype.forEach.call(body.children, function (n) {
                content += n.getBoundingClientRect().height;
              });
            }
          }
          if (content > 0) {
            w.classList.toggle('dg-quiet', content < QUIET_BELOW_PX);
            var head = w.querySelector('.dg-head');
            var headH = head ? head.getBoundingClientRect().height : 0;
            // Enough cells to hold the content, never more than the auto ceiling — past it the
            // body scrolls, because a widget must never grow the page to fit a long list.
            cells = Math.min(AUTO_MAX_CELLS, cellsForPx(headH + content + 24, rowGap));
          }
        }

        if (id) G._hCells[id] = cells;
      });
      placeAll();   // heights feed the rects; collisions resolve; positions land
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
    wireDragAndDrop(rootEl);
    watchResize();
    watchBreakpoint();
    G.render();
  };

  /**
   * Re-resolve saved widths when the grid changes shape.
   *
   * The inline span is a COLUMN COUNT derived from a proportion, so it is only correct for the
   * column count it was computed against. Without this, a widget sized to three quarters at 12
   * columns kept its literal span of 9 when the window narrowed to a 4-column grid — clamped to
   * full width, silently wrong, and it looked like the proportion had never been stored at all.
   */
  function watchBreakpoint() {
    var last = null;
    var timer = null;
    window.addEventListener('resize', function () {
      clearTimeout(timer);
      timer = setTimeout(function () {
        if (!G.root) return;
        var cols = columnCount();
        if (cols === last) return;      // same grid shape: nothing to re-resolve
        last = cols;
        Object.keys(G._frames).forEach(function (id) { applySize(id, G._frames[id].widget); });
        markQuiet();                    // --dg-cell-h changes with the breakpoint too
      }, 120);
    });
  }

  // ---- direct manipulation: drag to arrange, corner to size ------------------------------------

  var dragId = null;
  var dragDraft = null;   // live preview positions; committed on drop, discarded on cancel

  /**
   * The pointer→cell ruler, FROZEN at dragstart.
   *
   * v0.3.8.56 (watched live, 165 oscillations in one session): the mapping used to read the
   * root's LIVE rect on every dragover — but each preview changes the board's height, the
   * scroller clamps, the rect shifts by exactly the dragged widget's height, and the same
   * pointer maps to a new cell: a feedback loop with the widget's own height as its amplitude
   * (the colony bounced rows 5↔7, two cells, forever). The ruler is captured once in DOCUMENT
   * space when the drag starts, and the board's height is locked for the gesture, so a preview
   * can never move the thing it is measured against.
   */
  var dragRuler = null;
  function captureDragRuler() {
    var rect = G.root.getBoundingClientRect();
    var cs = getComputedStyle(G.root);
    var padL = parseFloat(cs.paddingLeft) || 0, padT = parseFloat(cs.paddingTop) || 0;
    var innerW = rect.width - padL - (parseFloat(cs.paddingRight) || 0);
    dragRuler = {
      docLeft: rect.left + window.scrollX + padL,
      docTop: rect.top + window.scrollY + padT,
      cellW: innerW / 6,
      rowStep: cellHeight() + (parseFloat(cs.rowGap) || 0),
    };
    // Lock the board's height for the gesture: a preview that shrinks the page invites a scroll
    // clamp, and a scroll clamp moves every viewport-relative coordinate.
    G.root.style.minHeight = rect.height + 'px';
  }
  function releaseDragRuler() {
    dragRuler = null;
    G.root.style.minHeight = '';
  }
  function cellAt(x, y, wCells) {
    var r = dragRuler;
    if (!r) return { x: 0, y: 0 };
    var cx = Math.floor((x + window.scrollX - r.docLeft) / r.cellW);
    var cy = Math.floor((y + window.scrollY - r.docTop) / r.rowStep);
    return {
      x: Math.max(0, Math.min(6 - wCells, cx)),
      y: Math.max(0, cy),
    };
  }

  /**
   * Drag to arrange, using the native drag-and-drop API.
   *
   * Native rather than pointer-tracked-with-a-floating-clone on purpose: a clone that follows the
   * cursor is a layer stacked over the grid, and a layer that can float over other content is the
   * failure mode this layout replaced. The browser's own drag image costs nothing and needs no
   * stacking order.
   *
   * Delegated to the root so a widget added later is draggable without re-wiring, and so the
   * handlers survive the frame caching that makes reordering non-destructive.
   */
  var RESIZE_CORNER_PX = 18;

  function wireDragAndDrop(rootEl) {
    // The resize grip and the drag handle occupy the same element, and drag wins by default — the
    // browser starts dragging the card before its own resizer sees the press, so the corner would
    // be unusable. Draggability is therefore suspended while the pointer is in the corner, and
    // restored on release. Measured from the bottom-right because that is where `resize: both`
    // puts the grip.
    rootEl.addEventListener('mousedown', function (e) {
      if (G.layout.locked) return;
      var w = e.target.closest && e.target.closest('.dg-widget');
      if (!w) return;
      var r = w.getBoundingClientRect();
      var inCorner = (r.right - e.clientX) <= RESIZE_CORNER_PX && (r.bottom - e.clientY) <= RESIZE_CORNER_PX;
      w.draggable = !inCorner;

      // Release the height floor for the duration of the drag. A widget carries a min-height —
      // the breakpoint's floor, or the exact content height written by the auto-fit pass — and the
      // browser's resizer cannot drag a box below its own min-height. So the first resize worked,
      // set a new floor at whatever height it landed on, and every attempt after that could only
      // grow. Resizing was quietly one-way. The floor is restored on release.
      //
      // v0.3.8.56 (field report: "cannot be made smaller once they lock"): the class alone was
      // HALF a release. `.dg-sizing { min-height: 0 }` beats the CSS floor but loses to the
      // INLINE min-height that applySize (operator height) and markQuiet (auto-fit) write — so a
      // widget that had EVER been sized or fitted still could not shrink: rect ≥ inline floor,
      // the release snap re-read the floored height, and the old floor was stored right back.
      // The inline floor is cleared for the drag too; markQuiet restores the right one after.
      if (inCorner) { w.classList.add('dg-sizing'); w.style.minHeight = ''; }
    });

    rootEl.addEventListener('mouseup', function () {
      if (G.layout.locked) return;
      Array.prototype.forEach.call(rootEl.querySelectorAll('.dg-widget'), function (w) { w.draggable = true; });
    });

    rootEl.addEventListener('dragstart', function (e) {
      if (G.layout.locked) return;
      var w = e.target.closest && e.target.closest('.dg-widget');
      if (!w) return;
      dragId = w.getAttribute('data-widget-id');
      w.classList.add('dg-dragging');
      // The preview works on a COPY: the saved layout is untouched until the drop commits, so a
      // cancelled drag costs nothing and a mere click moves nothing at all.
      dragDraft = {};
      Object.keys(G.layout.pos).forEach(function (k) {
        dragDraft[k] = { x: G.layout.pos[k].x, y: G.layout.pos[k].y };
      });
      captureDragRuler();
      // The end-zone: the grid ends at its content, so "below the last row" was OUTSIDE the
      // container and dragover stopped firing there. Padding opens while a drag is live.
      rootEl.classList.add('dg-drag-live');
      try { e.dataTransfer.setData('text/plain', dragId); e.dataTransfer.effectAllowed = 'move'; } catch (err) { /* older engines */ }
    });

    /**
     * Live preview, free placement: the pointer names a CELL, the dragged widget claims it, and
     * only the widgets it actually lands on are pushed down to make room — everything else stays
     * exactly where the operator put it. Nothing in the DOM moves; only grid positions restyle.
     */
    rootEl.addEventListener('dragover', function (e) {
      if (G.layout.locked || !dragId || !dragDraft) return;
      e.preventDefault();                       // required, or the browser refuses the drop
      e.dataTransfer.dropEffect = 'move';

      var target = cellAt(e.clientX, e.clientY, widthCellsOf(dragId));
      var cur = dragDraft[dragId];
      if (cur && cur.x === target.x && cur.y === target.y) return;   // same cell: nothing to do
      dragDraft[dragId] = target;
      placeAll(dragDraft, dragId);
    });

    rootEl.addEventListener('drop', function (e) {
      if (G.layout.locked || !dragId || !dragDraft) return;
      e.preventDefault();
      // Commit what is on screen: the dragged widget at its chosen cell, and the pushes the
      // preview already resolved around it.
      var rects = resolveCollisions(autoPlace(rectsFor(dragDraft)), dragId);
      rects.forEach(function (r) { G.layout.pos[r.id] = { x: r.x, y: r.y }; });
      dragId = null; dragDraft = null;
      releaseDragRuler();
      G.persist();
      placeAll();
    });

    rootEl.addEventListener('dragend', function () {
      rootEl.classList.remove('dg-drag-live');
      Array.prototype.forEach.call(G.root.querySelectorAll('.dg-dragging'),
        function (w) { w.classList.remove('dg-dragging'); });
      releaseDragRuler();
      // A cancelled drag (Escape, or a drop outside the grid) never reaches `drop`; the preview
      // was a draft, so putting things back is just re-placing from the saved positions.
      if (dragId) { dragId = null; dragDraft = null; placeAll(); }
    });
  }

  /**
   * Corner resize, snapped to whole grid columns.
   *
   * Uses the browser's own `resize` grip (CSS `resize: both`) rather than a custom handle, because
   * a handle pinned to a widget's corner wants absolute positioning and the grid is not allowed to
   * declare any. Native resize writes an inline width/height; those are read, snapped to the column
   * rhythm, stored as a fraction, and then CLEARED — the grid owns width, the operator owns the
   * proportion. Leaving the inline width in place would freeze the widget at one breakpoint's pixel
   * measurement.
   *
   * Snapping happens on RELEASE, not while the pointer is down. It was previously debounced off
   * size changes, which meant a pause mid-drag fired the snap while the operator was still holding
   * the grip: the inline width was cleared underneath them, the browser carried on resizing from
   * the width it still believed in, and the widget fought the cursor and landed on the wrong size.
   * The end of a drag is a real event, so it is worth waiting for rather than inferring from a
   * gap in a stream of them.
   *
   * Listened for on the document because the pointer is routinely released outside the widget —
   * and outside the grid — at the end of a resize.
   */
  function watchResize() {
    document.addEventListener('mouseup', snapAnyResized);
    document.addEventListener('pointerup', snapAnyResized);
  }

  function snapAnyResized() {
    if (!G.root) return;
    Array.prototype.forEach.call(G.root.querySelectorAll('.dg-sizing, .dg-widget'), function (w) {
      var sized = w.style.width || w.style.height;          // only a native resize sets these
      w.classList.remove('dg-sizing');                      // floor comes back either way
      if (sized && !G.layout.locked) snapToGrid(w);
    });
  }

  function snapToGrid(w) {
    var id = w.getAttribute('data-widget-id');
    if (!id || !G.widgets[id]) return;
    var cols = columnCount();
    var cs = getComputedStyle(G.root);
    var gap = parseFloat(cs.columnGap || cs.gap) || 0;
    var rootW = G.root.clientWidth
      - (parseFloat(cs.paddingLeft) || 0) - (parseFloat(cs.paddingRight) || 0);
    var colW = (rootW - gap * (cols - 1)) / cols;
    var px = w.getBoundingClientRect().width;
    var span = Math.max(1, Math.min(cols, Math.round((px + gap) / (colW + gap))));
    // The dragged INLINE height is the operator's intent; the rect is that height as constrained
    // by whatever floor survived the drag. Preferring the inline value means a shrink is stored
    // as the shrink that was asked for, even if a floor briefly resisted it.
    var h = parseFloat(w.style.height) || w.getBoundingClientRect().height;

    // Hand width back to the grid before storing, or the inline width outlives this breakpoint.
    w.style.width = '';
    w.style.height = '';
    G.setSpanFraction(id, span / cols);
    G.setHeight(id, h);
  }

  /**
   * Lay the grid out from the current layout.
   *
   * MOVES cached frames rather than rebuilding them. appendChild on an existing node relocates it,
   * so reordering costs nothing and — critically — never destroys a widget body. A hidden widget's
   * frame is detached but RETAINED, with its adopted content still inside it, so re-showing it is
   * a re-append rather than a re-adoption of a node that no longer exists.
   *
   * Widgets are moved ONLY when their position actually changes. Appending every widget in order
   * was correct but rebuilt the whole sequence on every render: each append relocates a node to the
   * end, so the content height collapsed and regrew mid-loop and the scroller clamped to the top.
   * Hiding one widget near the bottom of a long dashboard threw the operator back to the first row.
   */
  G.render = function () {
    var rootEl = G.root;
    if (!rootEl) return;
    rootEl.classList.toggle('dg-unlocked', !G.layout.locked);

    // Restored after the moves. Removing a widget legitimately shortens the page, so the browser
    // may clamp this to the new maximum — that is fine and still lands the operator where they
    // were looking, rather than at the top.
    var scroller = scrollParentOf(rootEl);
    var keepTop = scroller ? scroller.scrollTop : 0;

    // Walks the existing children alongside the desired order; anything already in position is
    // left untouched, so a hide or a reorder moves one node instead of seventeen.
    var cursor = rootEl.firstChild;

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
      applySize(id, f.widget);
      // Draggable only while unlocked: a dashboard being read must not move when a click slips.
      f.widget.draggable = !G.layout.locked;
      f.widget.classList.toggle('dg-resizable', !G.layout.locked);
      if (G.isHidden(id)) {
        if (f.widget.parentNode) f.widget.parentNode.removeChild(f.widget);
      } else if (f.widget === cursor) {
        cursor = cursor.nextSibling;       // already in the right place: touch nothing
      } else {
        rootEl.insertBefore(f.widget, cursor);
      }
    });
    if (scroller) scroller.scrollTop = keepTop;
    markQuiet();
  };

  /** The nearest ancestor that actually scrolls, so a re-render can put it back where it was. */
  function scrollParentOf(node) {
    for (var n = node.parentNode; n && n.nodeType === 1; n = n.parentNode) {
      var oy = getComputedStyle(n).overflowY;
      if ((oy === 'auto' || oy === 'scroll') && n.scrollHeight > n.clientHeight) return n;
    }
    return null;
  }

  /** Columns in the grid right now. Read from CSS so the breakpoints stay the single source. */
  function columnCount() {
    var n = parseInt(getComputedStyle(G.root).getPropertyValue('--dg-cols'), 10);
    return n > 0 ? n : 12;
  }

  /**
   * Apply the operator's size overrides to one widget.
   *
   * The stored fraction is resolved against the CURRENT column count, so a widget dragged to half
   * width stays half width when the window changes shape, and can never be given a span wider than
   * the grid — which would push it out of the row and leave a hole beside it.
   */
  function applySize(id, widget) {
    var f = G.layout.spans[id];
    var cols = columnCount();
    // At the narrowest breakpoint the grid is a stack: every widget is full width by design,
    // because a 3-of-4 card leaves a one-column orphan beside it and reads as a mistake. The
    // override is REMOVED rather than clamped, so the breakpoint's own rule applies and the saved
    // proportion is waiting unchanged when the window widens again.
    if (typeof f === 'number' && f > 0 && f <= 1 && cols > STACK_BELOW_COLS) {
      // v0.3.8.56 (cell grid): the fraction resolves through CELLS — round to sixths first, then
      // to this breakpoint's columns — so a layout saved before the cell grid (quarters, raw
      // column counts) lands on a cell boundary instead of straddling one.
      var cells = Math.max(1, Math.min(6, Math.round(f * 6)));
      widget.style.setProperty('--dg-span', String(Math.max(1, Math.round(cells * cols / 6))));
    } else {
      widget.style.removeProperty('--dg-span');
    }

    var h = G.layout.heights[id];
    if (typeof h === 'number' && h > 0) {
      widget.setAttribute('data-user-h', '1');   // markQuiet must not fight the operator
    } else {
      widget.removeAttribute('data-user-h');
    }
  }

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
    // v0.3.8.56 (free placement): the arrows swap CELL RECT ORIGINS with the neighbour in
    // reading order (y, then x) — the keyboard path to the same "put it there" the drag has.
    var rects = resolveCollisions(autoPlace(rectsFor()), null)
      .sort(function (a, b) { return a.y - b.y || a.x - b.x; });
    var i = -1;
    rects.forEach(function (r, k) { if (r.id === id) i = k; });
    if (i < 0) return;
    var j = i + delta;
    if (j < 0 || j >= rects.length) return;
    var a = rects[i], b = rects[j];
    G.layout.pos[a.id] = { x: b.x, y: b.y };
    G.layout.pos[b.id] = { x: a.x, y: a.y };
    G.persist(); G.render();
  }

  G.setLocked = function (locked) { G.layout.locked = !!locked; G.persist(); G.render(); };

  /**
   * Move `id` so it sits directly before `beforeId` (or last, when beforeId is null).
   *
   * Drop targets are expressed as "before which widget" rather than as an index, because the index
   * of the dragged widget shifts the moment it is removed from the list — computing the insertion
   * point first and splicing second is how drop-one-place-to-the-right lands one place too far.
   */
  G.moveBefore = function (id, beforeId) {
    if (!G.widgets[id] || id === beforeId) return;
    var order = effectiveOrder();
    var from = order.indexOf(id);
    if (from < 0) return;
    order.splice(from, 1);
    var at = beforeId ? order.indexOf(beforeId) : -1;
    if (at < 0) order.push(id); else order.splice(at, 0, id);
    G.layout.order = order;
    G.persist(); G.render();
  };

  /** Set a widget's width as a fraction of the row. Clamped to something usable. */
  G.setSpanFraction = function (id, fraction) {
    if (!G.widgets[id] || typeof fraction !== 'number' || !isFinite(fraction)) return;
    // v0.3.8.56 (cell grid): snap to whole CELLS — sixths of a row — and store the fraction, so
    // the width means the same cells at every breakpoint rather than the pixels of this one.
    var cells = Math.max(1, Math.min(6, Math.round(fraction * 6)));
    G.layout.spans[id] = cells / 6;
    if (G._frames[id]) applySize(id, G._frames[id].widget);
    markQuiet();   // v0.3.8.56: a width change rewraps content — the masonry span re-measures
    G.persist();
  };

  /** Set a widget's height. v0.3.8.56: quantized to whole cells at the store, so what persists
   * is a cell count in pixel clothing and every reader rounds the same way. */
  G.setHeight = function (id, px) {
    if (!G.widgets[id] || typeof px !== 'number' || !isFinite(px)) return;
    var cell = cellHeight();
    var gap = parseFloat(getComputedStyle(G.root).rowGap) || 0;
    var cells = Math.max(1, Math.min(6, Math.round((px + gap) / (cell + gap))));
    G.layout.heights[id] = Math.round(cells * cell + (cells - 1) * gap);
    if (G._frames[id]) applySize(id, G._frames[id].widget);
    markQuiet();   // v0.3.8.56: the masonry span must follow the new height immediately
    G.persist();
  };

  /**
   * Back to the shipped arrangement.
   *
   * "Shipped" means the host's default view when it supplies one (G.defaults), NOT "every widget
   * visible in registration order". Once a curated first-run view exists, resetting to
   * show-everything resets to an arrangement no one ever chose — and it silently outranks the
   * default for anyone who has ever pressed the button, since the result is then saved.
   */
  G.resetLayout = function () {
    var d = G.defaults || {};
    G.layout = {
      hidden: d.hidden ? JSON.parse(JSON.stringify(d.hidden)) : {},
      order: d.order ? d.order.slice() : null,
      locked: true,
      spans: d.spans ? JSON.parse(JSON.stringify(d.spans)) : {},
      heights: d.heights ? JSON.parse(JSON.stringify(d.heights)) : {},
      // v0.3.8.56: the shipped view is a curated PLACEMENT now, not just an order — reset restores
      // its positions rather than forgetting them, or the button would produce an auto-packed
      // arrangement nobody chose. A host without default positions still auto-places.
      pos: d.pos ? JSON.parse(JSON.stringify(d.pos)) : {},
    };
    // Inline overrides live on the element, so clearing the model is not enough to clear the view.
    Object.keys(G._frames).forEach(function (id) {
      var w = G._frames[id].widget;
      w.style.removeProperty('--dg-span');
      w.style.minHeight = '';
      w.style.gridColumn = '';
      w.style.gridRow = '';
      w.removeAttribute('data-user-h');
    });
    G.persist(); G.render();
  };

  /**
   * Size overrides arrive from storage, which means they arrive from something that could be old,
   * hand-edited, or written by a different release. Anything that is not a sane number is dropped
   * rather than trusted: a bad span silently breaks the row it lands in.
   */
  function sanitizeSizes(raw, validate) {
    var out = {};
    if (raw && typeof raw === 'object') {
      Object.keys(raw).forEach(function (id) {
        var v = raw[id];
        if (typeof v === 'number' && isFinite(v) && validate(v)) out[id] = v;
      });
    }
    return out;
  }

  G.applyLayout = function (saved) {
    if (!saved || typeof saved !== 'object') return;
    G.layout.hidden = (saved.hidden && typeof saved.hidden === 'object') ? saved.hidden : {};
    G.layout.order = Array.isArray(saved.order) ? saved.order.slice() : null;
    G.layout.locked = saved.locked !== false;
    G.layout.spans = sanitizeSizes(saved.spans, function (v) { return v > 0 && v <= 1; });
    G.layout.heights = sanitizeSizes(saved.heights, function (v) { return v >= MIN_H && v <= MAX_H; });
    // v0.3.8.56 (free placement): positions arrive from a document that could be old or
    // hand-edited; an off-board or non-numeric rect origin is dropped, and autoPlace re-homes
    // that widget rather than letting it break the board.
    G.layout.pos = {};
    if (saved.pos && typeof saved.pos === 'object') {
      Object.keys(saved.pos).forEach(function (k) {
        var v = saved.pos[k];
        if (v && typeof v.x === 'number' && isFinite(v.x) && v.x >= 0 && v.x < 6
              && typeof v.y === 'number' && isFinite(v.y) && v.y >= 0 && v.y < 500) {
          G.layout.pos[k] = { x: Math.round(v.x), y: Math.round(v.y) };
        }
      });
    }
    if (G.mounted) G.render();
  };

  G.persist = function () {
    if (typeof G.onLayoutChange === 'function') {
      G.onLayoutChange({ hidden: G.layout.hidden, order: effectiveOrder(), locked: G.layout.locked,
                         spans: G.layout.spans, heights: G.layout.heights, pos: G.layout.pos });
    }
  };

  window.AnthillGrid = G;
})();
