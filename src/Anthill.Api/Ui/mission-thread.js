/* ANTHILL — Missions conversation: reconciliation and state machines (v2.17.1).
 *
 * WHY THIS FILE EXISTS
 * --------------------
 * v2.16.0 rendered the Missions thread with `thread.innerHTML = rows.map(...).join('')` on every
 * jobs poll — every three seconds. That destroyed and rebuilt the entire conversation DOM even
 * when the mission data was byte-identical, taking with it: open <details> disclosures, already
 * loaded activity reports, the `data-loaded` markers, keyboard focus, text selection, and the
 * scroll position. It also re-announced all forty exchanges through the live region on every poll.
 *
 * The repair is an incremental, keyed update. All of the decision-making for that update lives
 * HERE, deliberately free of any DOM reference, because this repo has no browser test harness:
 * pure functions can be proven with `node --test` (built into the Node 20 that CI already
 * installs) without adding a framework or a build pipeline. app.js keeps the DOM work only.
 *
 * Loaded in the browser as window.AnthillMissionThread; required directly by the tests.
 */
(function (root, factory) {
  var api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;   // node --test
  else root.AnthillMissionThread = api;                                     // browser
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  /** Fields that actually change what an exchange looks like. Nothing else is compared. */
  var RENDER_FIELDS = ['goal', 'status', 'final_result', 'user_result', 'success_score', 'saved_at', 'created_at'];

  function answerOf(m) {
    return (m && (m.final_result || m.user_result)) || '';
  }

  /**
   * A stable fingerprint of the render-affecting fields.
   *
   * Deliberately NOT JSON.stringify(mission): that is sensitive to property order and to fields
   * the thread never displays, so an irrelevant backend addition would look like a change and
   * trigger a rebuild — which is the bug this whole file exists to prevent. Values are length-
   * prefixed so that neighbouring fields cannot combine into the same string.
   */
  function missionFingerprint(m) {
    if (!m) return '';
    var parts = [];
    for (var i = 0; i < RENDER_FIELDS.length; i++) {
      var v = m[RENDER_FIELDS[i]];
      var s = (v === null || v === undefined) ? '' : String(v);
      parts.push(s.length + ':' + s);
    }
    return parts.join('|');
  }

  function missionId(m) {
    return m && m.id !== undefined && m.id !== null ? String(m.id) : '';
  }

  /**
   * Work out the minimum set of changes between what is on screen and what the server just sent.
   *
   * `prev` is a Map of id -> fingerprint (what is currently rendered). The returned plan is
   * ordered oldest-first, which is the order the thread displays: newest answer nearest the
   * composer.
   */
  function reconcileThread(prev, missions) {
    var prevMap = prev instanceof Map ? prev : new Map(prev || []);
    var list = Array.isArray(missions) ? missions : [];

    // The API returns newest-first; the conversation reads oldest-first.
    var ordered = list.slice().reverse();

    var order = [], added = [], updated = [], next = new Map(), seen = new Set();
    for (var i = 0; i < ordered.length; i++) {
      var m = ordered[i], id = missionId(m);
      if (!id || seen.has(id)) continue;          // defensive: never render a duplicate row
      seen.add(id);
      var print = missionFingerprint(m);
      order.push(id);
      next.set(id, print);
      if (!prevMap.has(id)) added.push(id);
      else if (prevMap.get(id) !== print) updated.push(id);
    }

    // Removed only when the server genuinely stopped returning the mission.
    var removed = [];
    prevMap.forEach(function (_v, id) { if (!seen.has(id)) removed.push(id); });

    // Order changes matter even when no individual row did (a re-ranked list still needs moving).
    var prevOrder = Array.from(prevMap.keys());
    var orderChanged = prevOrder.length !== order.length;
    if (!orderChanged) {
      for (var k = 0; k < order.length; k++) {
        if (prevOrder[k] !== order[k]) { orderChanged = true; break; }
      }
    }

    return {
      order: order,
      added: added,
      updated: updated,
      removed: removed,
      fingerprints: next,
      orderChanged: orderChanged,
      changed: added.length > 0 || updated.length > 0 || removed.length > 0 || orderChanged,
    };
  }

  /**
   * Rejects stale responses.
   *
   * Page entry and the three-second jobs poll can both request the thread, so two fetches can be
   * in flight at once. Without this, a slow earlier response can land after a newer one and
   * overwrite current state with older data.
   */
  function RequestGate() {
    var current = 0;
    return {
      next: function () { return ++current; },
      isCurrent: function (token) { return token === current; },
      /** Invalidate everything in flight (used when leaving the page). */
      cancelAll: function () { current++; },
    };
  }

  var ACTIVITY_STATES = ['idle', 'loading', 'loaded', 'error'];

  /**
   * Per-mission activity (the "Show activity" disclosure) state, held OUTSIDE the DOM so it
   * survives any re-render, and so a failed report can be retried.
   *
   * v2.16.0 marked `dataset.loaded = '1'` *before* the request resolved, so a report that timed
   * out was permanently stuck: reopening the disclosure saw the marker and never retried.
   */
  function ActivityStore() {
    var states = new Map();   // id -> 'idle' | 'loading' | 'loaded' | 'error'
    var open = new Set();     // ids whose disclosure is expanded
    var errors = new Map();   // id -> message

    function stateOf(id) { return states.get(String(id)) || 'idle'; }

    return {
      states: ACTIVITY_STATES,
      stateOf: stateOf,
      errorOf: function (id) { return errors.get(String(id)) || ''; },
      isOpen: function (id) { return open.has(String(id)); },
      setOpen: function (id, isOpen) {
        var k = String(id);
        if (isOpen) open.add(k); else open.delete(k);
      },

      /**
       * Claim the right to fetch this report. Returns false when a fetch is already in flight or
       * the report is already loaded, which is what prevents duplicate concurrent requests.
       */
      begin: function (id) {
        var k = String(id), s = stateOf(k);
        if (s === 'loading' || s === 'loaded') return false;
        states.set(k, 'loading');
        errors.delete(k);
        return true;
      },
      succeed: function (id) { states.set(String(id), 'loaded'); errors.delete(String(id)); },
      fail: function (id, message) {
        var k = String(id);
        states.set(k, 'error');
        errors.set(k, message || 'The mission report could not be loaded.');
      },
      /** Return to idle so a failed report can be fetched again. */
      retry: function (id) {
        var k = String(id);
        if (stateOf(k) === 'loaded') return false;   // nothing to retry
        states.set(k, 'idle');
        errors.delete(k);
        return true;
      },
      forget: function (id) {
        var k = String(id);
        states.delete(k); errors.delete(k); open.delete(k);
      },
    };
  }

  /** Distance from the bottom, in px, within which the thread follows new content. */
  var FOLLOW_THRESHOLD_PX = 96;

  /**
   * Should the thread scroll to the newest exchange?
   *
   * Measured BEFORE the update and applied after. Reading older history must never be interrupted,
   * so this is false unless the viewer was already near the bottom. A thread shorter than its
   * viewport counts as "at the bottom" so the first exchanges behave sensibly.
   */
  function shouldFollowBottom(metrics, threshold) {
    if (!metrics) return false;
    var t = typeof threshold === 'number' ? threshold : FOLLOW_THRESHOLD_PX;
    var scrollTop = Number(metrics.scrollTop) || 0;
    var scrollHeight = Number(metrics.scrollHeight) || 0;
    var clientHeight = Number(metrics.clientHeight) || 0;
    if (scrollHeight <= clientHeight) return true;
    return (scrollHeight - scrollTop - clientHeight) <= t;
  }

  /**
   * What should the live region say?
   *
   * v2.16.0 put aria-live on the thread itself, so a screen reader re-announced all forty
   * exchanges every three seconds. Only genuinely new or newly answered missions are worth
   * speaking, and only the most recent one.
   */
  function announcementFor(plan, missionsById) {
    if (!plan || !plan.changed) return '';
    var byId = missionsById instanceof Map ? missionsById : new Map(missionsById || []);
    var interesting = plan.added.concat(plan.updated);
    for (var i = interesting.length - 1; i >= 0; i--) {
      var m = byId.get(interesting[i]);
      if (!m) continue;
      var status = String(m.status || '');
      if (status === 'complete' || status === 'partial' || status === 'failed') {
        return 'Mission ' + status + ': ' + String(m.goal || '').slice(0, 80);
      }
    }
    return '';
  }

  /**
   * Composer state machine for dispatch.
   *
   * v2.16.0 cleared the textarea before the request and, on failure, re-enabled the input without
   * restoring the text or surfacing the error — the operator lost what they typed and was told
   * nothing. `text` is carried through so a failure can put it back.
   */
  function composerReducer(state, event) {
    var s = state || { phase: 'idle', text: '', error: '' };
    switch (event && event.type) {
      case 'submit':
        if (s.phase === 'sending') return s;                       // blocks double-submit
        if (!event.text || !String(event.text).trim()) return s;    // empty directives do nothing
        return { phase: 'sending', text: String(event.text), error: '' };
      case 'accepted':
        return { phase: 'idle', text: '', error: '' };
      case 'failed':
        return { phase: 'idle', text: s.text, error: String((event && event.message) || 'Dispatch failed.') };
      case 'edit':
        return { phase: s.phase, text: String(event.text || ''), error: '' };
      default:
        return s;
    }
  }

  return {
    RENDER_FIELDS: RENDER_FIELDS,
    FOLLOW_THRESHOLD_PX: FOLLOW_THRESHOLD_PX,
    ACTIVITY_STATES: ACTIVITY_STATES,
    answerOf: answerOf,
    missionId: missionId,
    missionFingerprint: missionFingerprint,
    reconcileThread: reconcileThread,
    RequestGate: RequestGate,
    ActivityStore: ActivityStore,
    shouldFollowBottom: shouldFollowBottom,
    announcementFor: announcementFor,
    composerReducer: composerReducer,
  };
});
