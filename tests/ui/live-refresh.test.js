// v0.3.8.52 — the event-driven refresh layer, executed.
//
// `liveRefresh` is the thing that finally made the SSE stream do something. Before it, app.js
// defined `onColonyEvent` and nothing in 10,000 lines called it: the colony pushed events and every
// panel still ran its own 2.5-6s timer. So the behaviour under test is new, load-bearing, and
// exactly the kind of thing that looks obviously correct and is not — the coalescing window and the
// event filter are both easy to get subtly wrong in a way no one notices until a mission fan-out
// makes the console hammer the API.
//
// Run with: node --test tests/ui/live-refresh.test.js
const test = require('node:test');
const assert = require('node:assert');
const { functionSource, source } = require('./_app-source.js');

// liveRefresh talks to three ambient things — setInterval, setTimeout and onColonyEvent — so it is
// evaluated against fakes. Time is driven by hand rather than by waiting, which keeps the suite
// fast and, more importantly, deterministic.
function harness() {
  const intervals = [];
  const timeouts = [];
  const subscribers = [];

  const scope = {
    setInterval: (fn, ms) => { intervals.push({ fn, ms }); return intervals.length; },
    setTimeout: (fn, ms) => { timeouts.push({ fn, ms }); return timeouts.length; },
    onColonyEvent: fn => { subscribers.push(fn); return () => {}; },
    console: { warn() {} },
  };

  // eslint-disable-next-line no-new-func
  const factory = new Function(...Object.keys(scope), `${functionSource('liveRefresh')}\nreturn liveRefresh;`);
  const liveRefresh = factory(...Object.values(scope));

  return {
    liveRefresh,
    intervals,
    subscribers,
    emit(event_type) { for (const fn of subscribers) fn({ event_type }); },
    /** Fire every timeout currently queued, as the event loop eventually would. */
    flush() { const due = timeouts.splice(0); for (const t of due) t.fn(); },
    pendingTimeouts: () => timeouts.length,
  };
}

test('liveRefresh registers the idle interval as a fallback', () => {
  const h = harness();
  const calls = [];

  h.liveRefresh(() => calls.push('tick'), { idleMs: 30000 });

  assert.strictEqual(h.intervals.length, 1);
  assert.strictEqual(h.intervals[0].ms, 30000);

  // The safety net still works on its own — this is the property that keeps a dropped stream from
  // looking like a dead colony, and the reason the timers were slowed rather than deleted.
  h.intervals[0].fn();
  assert.deepStrictEqual(calls, ['tick']);
});

test('a matching event triggers a refresh without waiting for the interval', () => {
  const h = harness();
  const calls = [];

  h.liveRefresh(() => calls.push('refresh'), { idleMs: 30000, on: t => t.startsWith('mission_') });
  h.emit('mission_started');

  assert.deepStrictEqual(calls, [], 'the refresh is scheduled, not run synchronously');
  h.flush();
  assert.deepStrictEqual(calls, ['refresh']);
});

test('a non-matching event does not trigger a refresh', () => {
  const h = harness();
  const calls = [];

  h.liveRefresh(() => calls.push('refresh'), { idleMs: 30000, on: t => t.startsWith('approval_') });
  h.emit('mission_started');
  h.flush();

  assert.deepStrictEqual(calls, []);
});

test('an omitted filter means every event refreshes — the Event Log case', () => {
  const h = harness();
  const calls = [];

  h.liveRefresh(() => calls.push('refresh'), { idleMs: 30000 });
  h.emit('anything_at_all');
  h.flush();

  assert.deepStrictEqual(calls, ['refresh']);
});

test('a burst of events coalesces into ONE refresh', () => {
  // The property that makes this an improvement rather than a regression. A mission fan-out emits
  // task events in bursts of dozens within a second; one refresh per event would be strictly worse
  // than the steady polling it replaced.
  const h = harness();
  const calls = [];

  h.liveRefresh(() => calls.push('refresh'), { idleMs: 30000, on: () => true });
  for (let i = 0; i < 50; i++) h.emit('task_completed');

  assert.strictEqual(h.pendingTimeouts(), 1, '50 events must schedule exactly one refresh');
  h.flush();
  assert.deepStrictEqual(calls, ['refresh']);
});

test('a later burst refreshes again once the first has fired', () => {
  // Coalescing must not latch: after the pending refresh runs, the next event schedules a new one.
  const h = harness();
  const calls = [];

  h.liveRefresh(() => calls.push('refresh'), { idleMs: 30000, on: () => true });

  h.emit('task_completed');
  h.flush();
  h.emit('task_completed');
  h.flush();

  assert.deepStrictEqual(calls, ['refresh', 'refresh']);
});

test('a throwing panel does not break the subscription for later events', () => {
  const h = harness();
  let calls = 0;

  h.liveRefresh(() => { calls++; throw new Error('panel blew up'); }, { idleMs: 30000, on: () => true });

  h.emit('task_completed');
  assert.doesNotThrow(() => h.flush(), 'a failing refresh must not propagate out of the timer');
  h.emit('task_completed');
  h.flush();

  assert.strictEqual(calls, 2, 'the second event still refreshes after the first threw');
});

test('liveRefresh defaults the coalescing window rather than leaving it undefined', () => {
  const h = harness();

  h.liveRefresh(() => {}, { idleMs: 30000, on: () => true });
  h.emit('task_completed');

  // An undefined delay would make setTimeout fire immediately and defeat the coalescing above.
  assert.strictEqual(typeof h.intervals[0].ms, 'number');
  assert.strictEqual(h.pendingTimeouts(), 1);
});

// ---------------------------------------------------------------------------------------------
// The wiring, not just the mechanism.
// ---------------------------------------------------------------------------------------------

test('the fast pollers were actually retired, not merely wrapped', () => {
  // The point of the change was fewer timer-driven requests. If someone reinstates a 2.5s interval
  // for these panels, the mechanism above would still pass all its tests.
  const startPolling = functionSource('startPolling');

  for (const gone of ['setInterval(pollStatus', 'setInterval(pollJobs', 'setInterval(pollEvents',
                      'setInterval(pollGraph', 'setInterval(pollApprovals', 'setInterval(pollConversations']) {
    assert.ok(!startPolling.includes(gone), `${gone} must go through liveRefresh, not a bare timer`);
  }

  for (const wired of ['liveRefresh(pollStatus', 'liveRefresh(pollJobs', 'liveRefresh(pollEvents',
                       'liveRefresh(pollGraph', 'liveRefresh(pollApprovals', 'liveRefresh(pollConversations']) {
    assert.ok(startPolling.includes(wired), `${wired} must be wired to the stream`);
  }
});

test('onColonyEvent has real subscribers — the gap this release closed', () => {
  // Before v0.3.8.52 this count was exactly one: the definition, and no callers. The stream existed
  // and drove nothing. A regression here would silently restore that state, because every panel
  // would keep working off its fallback timer and nothing would look broken.
  const calls = [...source.matchAll(/onColonyEvent\s*\(/g)];

  assert.ok(calls.length >= 2,
    `onColonyEvent must be subscribed to, not just defined — found ${calls.length} occurrence(s)`);
});
