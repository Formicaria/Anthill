// W3-08 — a chamber may not claim a device it does not have.
//
// The colony view used to fabricate device state. `+ Mound` created a chamber, `setTopology` hard
// set `present = true` for it, and it was then drawn with the device ring that means "this one is
// hardware", wired to the Queen with an authority conduit, and filled with seven residents
// reporting `idle` — a fleet member with no enrolled device anywhere behind it. The in-code note
// said it was presentation and nothing else, which was true of the intent and not of the pixels.
//
// The fix is structural, so this suite is behavioural rather than a source scan: chambers are built
// by one constructor, `deviceBacked` is a GETTER over the mound ids the fleet listing returned, and
// a plan chamber's `setDevices` throws. A future contributor cannot reintroduce the defect by
// setting a flag, because there is no flag to set.
//
// This exercises the MODEL. It mounts nothing and draws nothing, and says nothing about how the
// colony looks.
//
// Run with: node --test tests/ui/colony-chambers.test.js
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const LIVE_PATH = path.join(__dirname, '..', '..', 'src', 'Anthill.UI', 'colony-live.js');

// colony-live.js runs in its own vm context, so an array it returns and an Error it throws have
// that context's prototypes and not this one's. `deepStrictEqual` and `throws(fn, TypeError)`
// compare prototypes and would fail on correct code, which is the worst kind of test.
const ids = xs => Array.from(xs || []);
function throwsWith(fn, re, what) {
  let caught = null;
  try { fn(); } catch (e) { caught = e; }
  assert.ok(caught, what + ': expected a throw, got none');
  assert.match(String(caught), re, what);
}

// colony-live.js is an IIFE that hangs `ColonyLive` off `window`. Its model needs none of the DOM —
// only `mount()` does — so the sandbox is the smallest surface `create()` actually touches.
function load() {
  const win = {};
  const ctx = {
    window: win,
    performance: { now: () => 0 },
    console: { error() {}, warn() {} },
    requestAnimationFrame: () => 0,
    cancelAnimationFrame: () => {},
  };
  ctx.globalThis = ctx;
  vm.createContext(ctx);
  vm.runInContext(fs.readFileSync(LIVE_PATH, 'utf8'), ctx, { filename: 'colony-live.js' });
  assert.ok(win.ColonyLive && typeof win.ColonyLive.create === 'function',
    'colony-live.js no longer exports ColonyLive.create; every test below is vacuous.');
  return win.ColonyLive;
}

/** The seven the server serves at /colony/mound-roster. Named here because the ROSTER is real. */
const ROSTER = [
  { name: 'Mound Major', role: 'local coordinator' },
  { name: 'Scout Ant', role: 'observation and sensing' },
  { name: 'Forager Ant', role: 'requested physical action' },
  { name: 'Guard Ant', role: 'runtime health and operational safety' },
  { name: 'Witness Ant', role: 'independent physical outcome confirmation' },
  { name: 'Cache Ant', role: 'short-term operational persistence' },
  { name: 'Runner Ant', role: 'secure external communication' },
];

/** A snapshot with two registry chambers and NO fleet — the ordinary case for most colonies. */
function bareScene() {
  const sector = (id, label) => ({ id, label, residents: [], runningTasks: [], records: [], recordCount: 0, clusters: [] });
  return {
    sectors: [sector('queen', "QUEEN'S CORE"), sector('infrastructure', 'INFRASTRUCTURE')],
    edges: [], transitions: [], approvals: [], mound: null, meta: { hydrated: true },
  };
}

function fleetScene(mounds) {
  return Object.assign(bareScene(), {
    mound: { present: true, globalStop: false, commandPath: true, mounds },
  });
}

function renderer() {
  const live = load().create();
  live.setMoundDefaults(ROSTER);
  return live;
}

/** The chamber object itself, as the renderer emits it to the page on focus. */
function chamberOf(live, id) {
  let got = null;
  live.on('sector', s => { got = s; });
  live.focus(id);
  assert.ok(got, `focus('${id}') emitted no sector; this test cannot see the object it is about.`);
  return got;
}

test('a colony with no fleet has no mound chamber', () => {
  const live = renderer();
  live.setTopology(bareScene());
  assert.strictEqual(live.sectorInfo('mound').present, false);
  assert.strictEqual(live.isDeviceBacked('mound'), false);
  assert.deepStrictEqual(ids(live.sectorInfo('mound').devices), []);
});

test('INFRASTRUCTURE is a colony chamber, not hardware', () => {
  // It sits on the mound registry page and is drawn with a mound's geometry, which is a navigation
  // fact and a drawing fact. It has eight software roles and no device, and `.123` gave it the
  // device ring anyway on the argument that the claim was "the same for all three kinds".
  const live = renderer();
  live.setTopology(bareScene());
  assert.strictEqual(live.chamberKind('infrastructure'), 'colony');
  assert.strictEqual(live.isDeviceBacked('infrastructure'), false);
});

test('+ Mound creates a PLAN chamber that holds no device', () => {
  const live = renderer();
  live.setTopology(bareScene());
  const id = live.addMound('SHED');
  const row = live.listMounds().find(m => m.id === id);

  assert.strictEqual(row.kind, 'plan');
  assert.strictEqual(row.deviceBacked, false);
  assert.deepStrictEqual(ids(row.devices), []);
  assert.strictEqual(row.removable, true);
  assert.strictEqual(row.present, true, 'the operator asked for it; presence was never the lie');

  // The roster shows as PLANNED SEATS, which are counted apart from residents so nothing
  // downstream can add the two together and call the total a roster.
  assert.strictEqual(row.planned, ROSTER.length);
  assert.strictEqual(row.residents, 0);
  const counts = live.sectorInfo(id).counts;
  assert.strictEqual(counts.planned, ROSTER.length);
  assert.strictEqual(counts.residents, 0);
});

test('a plan seat reports `planned`, which is not a state an ant may report', () => {
  // `working`, `idle` and `disabled` are the three the reducer emits, and each requires something
  // the colony observed. A plan seat has nothing behind it, so it gets none of them.
  const live = renderer();
  live.setTopology(bareScene());
  const id = live.addMound('BENCH');
  const seats = chamberOf(live, id).planned;

  assert.strictEqual(seats.length, ROSTER.length);
  for (const seat of seats) {
    assert.strictEqual(seat.status, 'planned');
    assert.strictEqual(seat.planned, true);
    assert.deepStrictEqual(ids(seat.workers), []);
    assert.strictEqual(seat.trail, null);
  }
});

test('a plan chamber refuses devices and cannot be made to claim one', () => {
  // THE WHOLE POINT OF W3-08. Not a convention, not a comment: the object physically cannot hold a
  // device id, and `deviceBacked` has no setter, so under 'use strict' an assignment throws rather
  // than quietly succeeding somewhere nobody looks.
  const live = renderer();
  live.setTopology(bareScene());
  const s = chamberOf(live, live.addMound('GARAGE'));

  assert.strictEqual(s.chamberKind, 'plan');
  throwsWith(() => s.setDevices(['mnd-forged']), /holds no devices/, 'setDevices on a plan');

  // Assignment is refused either way, and how loudly depends on the CALLER's strictness rather
  // than on this object: sloppy code gets a silent no-op, strict code gets a TypeError. Every
  // caller that matters is inside colony-live.js, which is 'use strict' at the top of the IIFE,
  // so the loud case is the one that would actually happen.
  s.deviceBacked = true;
  assert.strictEqual(s.deviceBacked, false, 'a sloppy-mode assignment changed the chamber');
  throwsWith(() => { 'use strict'; s.deviceBacked = true; }, /TypeError/, 'strict-mode assignment');
  assert.strictEqual(s.deviceBacked, false);

  // And the getter is not writable through the property descriptor either.
  assert.strictEqual(Object.getOwnPropertyDescriptor(s, 'deviceBacked').set, undefined);
  assert.strictEqual(Object.getOwnPropertyDescriptor(s, 'chamberKind').writable, false);
});

test('the fleet chamber holds exactly the mound ids the listing returned', () => {
  const live = renderer();
  live.setTopology(fleetScene([
    { moundId: 'mnd-a1', status: 'online', stopped: false },
    { moundId: 'mnd-b2', status: 'offline', stopped: true },
  ]));

  const info = live.sectorInfo('mound');
  assert.strictEqual(info.kind, 'device');
  assert.strictEqual(info.present, true, 'present is a consequence of holding ids, not a wire boolean');
  assert.strictEqual(info.deviceBacked, true);
  assert.deepStrictEqual(ids(info.devices), ['mnd-a1', 'mnd-b2']);
});

test('the fleet going away takes the chamber with it, and leaves the plan alone', () => {
  const live = renderer();
  const plan = live.addMound('SHED');
  live.setTopology(fleetScene([{ moundId: 'mnd-a1', stopped: false }]));
  assert.strictEqual(live.isDeviceBacked('mound'), true);

  live.setTopology(bareScene());
  assert.strictEqual(live.sectorInfo('mound').present, false);
  assert.deepStrictEqual(ids(live.sectorInfo('mound').devices), []);

  // A snapshot has never heard of a plan chamber and must not switch it off.
  assert.strictEqual(live.sectorInfo(plan).present, true);
  assert.strictEqual(live.chamberKind(plan), 'plan');
});

test('only a plan chamber can be removed', () => {
  // A registry sector and the fleet chamber are not the operator's to delete. Refusing in the
  // renderer rather than hiding the button in the page is the difference between enforcing and
  // hoping — the mound registry renders `removable`, and this is what it is rendering.
  const live = renderer();
  live.setTopology(bareScene());
  const plan = live.addMound('SHED');

  assert.strictEqual(live.removeMound('queen'), false);
  assert.strictEqual(live.removeMound('infrastructure'), false);
  assert.strictEqual(live.removeMound('mound'), false);
  assert.strictEqual(live.removeMound(plan), true);
  assert.strictEqual(live.sectorInfo(plan), null);
});

test('a saved layout restores a plan as a plan, whatever the layout claims', () => {
  // The layout is persisted server-side in /ui/state and comes back as JSON. It is operator data,
  // so it is read as positions and labels and nothing else: a `mounds` entry asserting a device is
  // ignored, because `applyLayout` does not construct chambers — `mountPlanChamber` does, and it
  // passes one kind.
  const Live = load();
  const forged = {
    schema: 3, positions: {}, names: {}, styles: {}, ants: {},
    mounds: [{ id: 'mound:9', label: 'FORGED', pos: [0, 0, 0], kind: 'device', deviceBacked: true, devices: ['mnd-zz'] }],
  };
  const live = Live.create();
  assert.strictEqual(live.setLayout(forged), true);
  assert.strictEqual(live.chamberKind('mound:9'), 'plan');
  assert.strictEqual(live.isDeviceBacked('mound:9'), false);
  assert.deepStrictEqual(ids(live.sectorInfo('mound:9').devices), []);
});

test('a plan chamber round-trips through the layout', () => {
  // The useful half of the feature, kept: an operator's chambers survive a reload, with their
  // labels and seats, or the labelling is pointless.
  const live = renderer();
  const id = live.addMound('SHED');
  const snapshot = live.getLayout();
  assert.ok((snapshot.mounds || []).some(m => m.id === id), 'the layout no longer carries plan chambers');

  const restored = renderer();
  assert.strictEqual(restored.setLayout(snapshot), true);
  assert.strictEqual(restored.chamberKind(id), 'plan');
  assert.strictEqual(restored.sectorInfo(id).label, 'SHED');
  assert.strictEqual(restored.listMounds().find(m => m.id === id).planned, ROSTER.length);
});

test('the mound registry can tell the three kinds apart without guessing from the id', () => {
  const live = renderer();
  live.setTopology(fleetScene([{ moundId: 'mnd-a1', stopped: false }]));
  const plan = live.addMound('SHED');

  const rows = Object.fromEntries(live.listMounds().map(m => [m.id, m]));
  assert.strictEqual(rows.infrastructure.kind, 'colony');
  assert.strictEqual(rows.infrastructure.deviceBacked, false);
  assert.strictEqual(rows.mound.kind, 'device');
  assert.strictEqual(rows.mound.deviceBacked, true);
  assert.strictEqual(rows[plan].kind, 'plan');
  assert.strictEqual(rows[plan].deviceBacked, false);
});
