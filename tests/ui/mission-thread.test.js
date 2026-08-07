/* Behavioural tests for the Missions conversation reconciler (v2.17.1).
 *
 * These prove the properties that the v2.16.0 implementation violated. They run on `node --test`,
 * built into the Node 20 that CI already installs for the JS parse check — no framework, no build
 * pipeline, no new dependency.
 *
 * The DOM work in app.js is driven entirely by the decisions made here, so proving these proves
 * the behaviour that matters: unchanged data must not cause a rebuild, and open/loaded activity
 * must survive an update.
 */
const test = require('node:test');
const assert = require('node:assert/strict');
// v3.8.17 (refactor phase 6): the console assets moved from src/Anthill.Api/Ui/ to src/Anthill.UI/.
const MT = require('../../src/Anthill.UI/mission-thread.js');

// Shaped like a real /missions/json row: that endpoint returns `answer`, NOT final_result.
const mission = (id, over = {}) => Object.assign({
  id, goal: 'goal ' + id, status: 'complete',
  answer: 'answer ' + id, answer_truncated: false,
  success_score: 1, created_at: '2026-07-26T10:00:00Z',
}, over);

/** The API returns newest-first. */
const feed = (...ms) => ms.slice().reverse();

test('unchanged mission data produces no work at all', () => {
  const a = feed(mission('m1'), mission('m2'));
  const first = MT.reconcileThread(new Map(), a);
  assert.equal(first.changed, true);
  assert.deepEqual(first.order, ['m1', 'm2']);

  // Same payload again — the poll case that used to rebuild the whole DOM every 3 seconds.
  const second = MT.reconcileThread(first.fingerprints, a);
  assert.equal(second.changed, false, 'identical data must not report a change');
  assert.deepEqual(second.added, []);
  assert.deepEqual(second.updated, []);
  assert.deepEqual(second.removed, []);
  assert.equal(second.orderChanged, false);

  // A *different object* with identical values must also be a no-op (fresh parse each poll).
  const third = MT.reconcileThread(first.fingerprints, feed(mission('m1'), mission('m2')));
  assert.equal(third.changed, false, 'value-equal payloads must compare equal');
});

test('a field the thread does not display never triggers a change', () => {
  const base = MT.reconcileThread(new Map(), feed(mission('m1')));
  const noisy = mission('m1', { debug_result: 'a completely different trace', irrelevant: 42 });
  const plan = MT.reconcileThread(base.fingerprints, feed(noisy));
  assert.equal(plan.changed, false, 'only render-affecting fields may count as a change');
});

test('a changed mission updates only its own exchange', () => {
  const before = MT.reconcileThread(new Map(), feed(mission('m1'), mission('m2'), mission('m3')));
  const plan = MT.reconcileThread(
    before.fingerprints,
    feed(mission('m1'), mission('m2', { status: 'failed', final_result: 'it broke' }), mission('m3')));
  assert.deepEqual(plan.updated, ['m2']);
  assert.deepEqual(plan.added, []);
  assert.deepEqual(plan.removed, []);
});

test('a new mission is appended, oldest first, without disturbing the rest', () => {
  const before = MT.reconcileThread(new Map(), feed(mission('m1'), mission('m2')));
  const plan = MT.reconcileThread(before.fingerprints, feed(mission('m1'), mission('m2'), mission('m3')));
  assert.deepEqual(plan.added, ['m3']);
  assert.deepEqual(plan.updated, []);
  assert.deepEqual(plan.order, ['m1', 'm2', 'm3'], 'newest exchange sits last, next to the composer');
});

test('a row is removed only when the server stops returning it', () => {
  const before = MT.reconcileThread(new Map(), feed(mission('m1'), mission('m2')));
  const plan = MT.reconcileThread(before.fingerprints, feed(mission('m1')));
  assert.deepEqual(plan.removed, ['m2']);

  // An empty response is treated as "all gone", not as a transient blip to ignore.
  const wiped = MT.reconcileThread(before.fingerprints, []);
  assert.deepEqual(wiped.removed.sort(), ['m1', 'm2']);
});

test('a queued -> running -> complete transition is seen as an update each time', () => {
  let prev = new Map();
  const seq = ['queued', 'running', 'complete'];
  for (const status of seq) {
    const plan = MT.reconcileThread(prev, feed(mission('m1', { status, final_result: status === 'complete' ? 'done' : '' })));
    assert.equal(plan.changed, true, `${status} must register`);
    prev = plan.fingerprints;
  }
  // ...and then settles.
  const settled = MT.reconcileThread(prev, feed(mission('m1', { status: 'complete', final_result: 'done' })));
  assert.equal(settled.changed, false);
});

test('duplicate ids in one payload never produce two rows', () => {
  const plan = MT.reconcileThread(new Map(), feed(mission('m1'), mission('m1')));
  assert.deepEqual(plan.order, ['m1']);
});

test('open and loaded activity state survives mission updates', () => {
  const store = MT.ActivityStore();
  assert.equal(store.stateOf('m1'), 'idle');

  assert.equal(store.begin('m1'), true);
  store.succeed('m1');
  store.setOpen('m1', true);

  // The mission changes and its row is patched — the activity state is held outside the DOM.
  const before = MT.reconcileThread(new Map(), feed(mission('m1')));
  MT.reconcileThread(before.fingerprints, feed(mission('m1', { status: 'partial' })));

  assert.equal(store.stateOf('m1'), 'loaded', 'a loaded report must not be discarded by an update');
  assert.equal(store.isOpen('m1'), true, 'an expanded disclosure must stay expanded');
});

test('duplicate concurrent report requests are prevented', () => {
  const store = MT.ActivityStore();
  assert.equal(store.begin('m1'), true, 'first request proceeds');

  // The v2.16.0 bug was marking the report loaded BEFORE the response arrived. An in-flight
  // report must read as 'loading', so a re-render mid-fetch cannot mistake it for finished
  // content and skip showing the spinner (or skip the eventual retry affordance).
  assert.equal(store.stateOf('m1'), 'loading', 'an in-flight report is loading, never loaded');

  assert.equal(store.begin('m1'), false, 'a second request while loading is refused');
  store.succeed('m1');
  assert.equal(store.stateOf('m1'), 'loaded');
  assert.equal(store.begin('m1'), false, 'an already loaded report is not refetched');
});

test('a failed report is retryable; a loaded one is not re-fetched', () => {
  const store = MT.ActivityStore();
  store.begin('m1');
  store.fail('m1', 'timed out');

  assert.equal(store.stateOf('m1'), 'error');
  assert.equal(store.errorOf('m1'), 'timed out', 'the operator must be told what went wrong');

  assert.equal(store.retry('m1'), true);
  assert.equal(store.stateOf('m1'), 'idle', 'retry returns it to fetchable');
  assert.equal(store.begin('m1'), true, 'and the fetch may now proceed');
  store.succeed('m1');
  assert.equal(store.retry('m1'), false, 'a loaded report has nothing to retry');
});

test('a stale thread response cannot overwrite a newer one', () => {
  const gate = MT.RequestGate();
  const slowOld = gate.next();      // page entry
  const fastNew = gate.next();      // poll overtakes it

  assert.equal(gate.isCurrent(fastNew), true);
  assert.equal(gate.isCurrent(slowOld), false, 'the older in-flight request must be rejected');

  gate.cancelAll();
  assert.equal(gate.isCurrent(fastNew), false, 'leaving the page invalidates everything in flight');
});

test('reading older history is never dragged to the bottom', () => {
  // Scrolled well up in a long thread.
  assert.equal(MT.shouldFollowBottom({ scrollTop: 0, scrollHeight: 4000, clientHeight: 600 }), false);
  assert.equal(MT.shouldFollowBottom({ scrollTop: 1200, scrollHeight: 4000, clientHeight: 600 }), false);
});

test('a viewer already at the bottom follows a newly arriving answer', () => {
  assert.equal(MT.shouldFollowBottom({ scrollTop: 3400, scrollHeight: 4000, clientHeight: 600 }), true);
  // Just inside the threshold still counts as following.
  assert.equal(MT.shouldFollowBottom({ scrollTop: 3320, scrollHeight: 4000, clientHeight: 600 }), true);
  // Just outside it does not.
  assert.equal(MT.shouldFollowBottom({ scrollTop: 3200, scrollHeight: 4000, clientHeight: 600 }), false);
  // A thread shorter than its viewport is trivially "at the bottom".
  assert.equal(MT.shouldFollowBottom({ scrollTop: 0, scrollHeight: 200, clientHeight: 600 }), true);
});

test('the live region announces one meaningful result, not the whole thread', () => {
  const ms = [mission('m1'), mission('m2', { status: 'failed', goal: 'check backups' })];
  const byId = new Map(ms.map(m => [m.id, m]));

  const plan = MT.reconcileThread(new Map(), feed(...ms));
  const said = MT.announcementFor(plan, byId);
  assert.match(said, /^Mission failed: check backups/, 'announces the newest finished mission only');
  assert.equal(said.includes('m1'), false, 'and does not read out the rest of the thread');

  // Nothing changed -> nothing said. This is the every-three-seconds case.
  const quiet = MT.reconcileThread(plan.fingerprints, feed(...ms));
  assert.equal(MT.announcementFor(quiet, byId), '');

  // A still-running mission is not worth announcing.
  const running = [mission('m3', { status: 'running', final_result: '' })];
  const rp = MT.reconcileThread(new Map(), feed(...running));
  assert.equal(MT.announcementFor(rp, new Map(running.map(m => [m.id, m]))), '');
});

test('dispatch failure restores a usable composer with the text intact', () => {
  let s = MT.composerReducer(undefined, { type: 'edit', text: 'restart the proxmox node' });
  s = MT.composerReducer(s, { type: 'submit', text: 'restart the proxmox node' });
  assert.equal(s.phase, 'sending');

  // A second submit while sending is ignored — no double dispatch.
  const dup = MT.composerReducer(s, { type: 'submit', text: 'restart the proxmox node' });
  assert.equal(dup, s, 'double-submit must be a no-op while sending');

  s = MT.composerReducer(s, { type: 'failed', message: 'colony unreachable' });
  assert.equal(s.phase, 'idle', 'the composer must not stay disabled after a failure');
  assert.equal(s.text, 'restart the proxmox node', 'the typed directive must not be lost');
  assert.equal(s.error, 'colony unreachable', 'the error must be visible, not swallowed');
});

test('a successful dispatch clears the composer', () => {
  let s = MT.composerReducer({ phase: 'idle', text: 'do it', error: '' }, { type: 'submit', text: 'do it' });
  s = MT.composerReducer(s, { type: 'accepted' });
  assert.deepEqual(s, { phase: 'idle', text: '', error: '' });
});

test('empty or whitespace-only directives do nothing', () => {
  const idle = { phase: 'idle', text: '', error: '' };
  assert.equal(MT.composerReducer(idle, { type: 'submit', text: '' }).phase, 'idle');
  assert.equal(MT.composerReducer(idle, { type: 'submit', text: '   \n ' }).phase, 'idle');
});

test('mission text is compared by value, so injected markup cannot smuggle a false no-change', () => {
  const evil = '<img src=x onerror=alert(1)>';
  const a = MT.reconcileThread(new Map(), feed(mission('m1', { goal: evil })));
  const b = MT.reconcileThread(a.fingerprints, feed(mission('m1', { goal: evil + ' ' })));
  assert.equal(b.changed, true, 'a genuine text change must still be detected');
  assert.equal(MT.missionFingerprint({ goal: 'a|b' }), MT.missionFingerprint({ goal: 'a|b' }));
  // Length-prefixing stops two fields merging into one identical string.
  assert.notEqual(
    MT.missionFingerprint({ goal: 'ab', status: '' }),
    MT.missionFingerprint({ goal: 'a', status: 'b' }));
});


test('the answer comes from the field /missions/json actually returns', () => {
  // v2.18.2 regression: /missions/json projects id/goal/status/score/timestamps plus `answer`.
  // It has never returned final_result or user_result, so reading only those yielded '' for every
  // mission and the thread showed "Working — no answer recorded yet" forever.
  const row = { id: 'm1', goal: 'g', status: 'complete', answer: 'the backups are healthy' };
  assert.equal(MT.answerOf(row), 'the backups are healthy');

  // A mission with no answer yet is still correctly treated as pending.
  assert.equal(MT.answerOf({ id: 'm2', status: 'running' }), '');
  assert.equal(MT.answerOf({ id: 'm3', status: 'running', answer: '' }), '');

  // Fallbacks retained for the shapes that DO carry them (report endpoint, memory views).
  assert.equal(MT.answerOf({ final_result: 'synthesized' }), 'synthesized');
  assert.equal(MT.answerOf({ user_result: 'raw' }), 'raw');
  assert.equal(MT.answerOf({ final_result: 'synthesized', user_result: 'raw' }), 'synthesized');
  assert.equal(MT.answerOf(null), '');
});

test('an arriving answer is detected as a change', () => {
  // The exact sequence that looked broken: a running mission finishes and gains its answer.
  const running = { id: 'm1', goal: 'check backups', status: 'running', answer: '' };
  const done = { id: 'm1', goal: 'check backups', status: 'complete', answer: 'all healthy' };

  const first = MT.reconcileThread(new Map(), [running]);
  assert.equal(MT.answerOf(running), '');

  const second = MT.reconcileThread(first.fingerprints, [done]);
  assert.equal(second.changed, true, 'the arriving answer must register as a change');
  assert.deepEqual(second.updated, ['m1']);
  assert.equal(MT.answerOf(done), 'all healthy');
});

test('a truncated answer is flagged so the UI can point at the full output', () => {
  assert.equal(MT.answerIsTruncated({ answer: 'x', answer_truncated: true }), true);
  assert.equal(MT.answerIsTruncated({ answer: 'x', answer_truncated: false }), false);
  assert.equal(MT.answerIsTruncated({ answer: 'x' }), false);
  // Flipping only the truncation flag still counts as a render change.
  const a = MT.reconcileThread(new Map(), [{ id: 'm1', answer: 'x', answer_truncated: false }]);
  const b = MT.reconcileThread(a.fingerprints, [{ id: 'm1', answer: 'x', answer_truncated: true }]);
  assert.equal(b.changed, true);
});
