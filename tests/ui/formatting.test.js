// v0.3.8.52 — the display formatters, executed.
//
// These are small, but they are on the read path of nearly every panel, and "small and everywhere"
// is where an off-by-one in a unit boundary lives for a year without anyone noticing. Assertions
// are written against BEHAVIOUR at the boundaries rather than against pretty-printed samples.
//
// Run with: node --test tests/ui/formatting.test.js
const test = require('node:test');
const assert = require('node:assert');
const { loadFunctions } = require('./_app-source.js');

const { humanBytes, fmtTime } = loadFunctions('humanBytes', 'fmtTime');

test('humanBytes shows an em dash for a missing value, not "0 B" or "null"', () => {
  // A missing byte count and a genuinely empty file are different facts and must not render alike.
  assert.strictEqual(humanBytes(null), '—');
  assert.strictEqual(humanBytes(undefined), '—');
  assert.strictEqual(humanBytes(0), '0 B');
});

test('humanBytes stays in bytes right up to the boundary', () => {
  assert.strictEqual(humanBytes(1), '1 B');
  assert.strictEqual(humanBytes(1023), '1023 B');
  assert.strictEqual(humanBytes(1024), '1.0 KB');
});

test('humanBytes shows one decimal below 10 units and none at or above', () => {
  // The readability rule the implementation encodes: precision where it distinguishes, none where
  // it is noise.
  assert.strictEqual(humanBytes(1536), '1.5 KB');
  assert.strictEqual(humanBytes(1024 * 9.5), '9.5 KB');
  assert.strictEqual(humanBytes(1024 * 10), '10 KB');
  assert.strictEqual(humanBytes(1024 * 1023), '1023 KB');
});

test('humanBytes climbs through every unit it defines', () => {
  assert.strictEqual(humanBytes(1024 ** 2), '1.0 MB');
  assert.strictEqual(humanBytes(1024 ** 3), '1.0 GB');
  assert.strictEqual(humanBytes(1024 ** 4), '1.0 TB');
});

test('humanBytes saturates at terabytes rather than running off its unit list', () => {
  // Deliberate: the array ends at TB, so a petabyte reads as "1024 TB". Asserted so that anyone
  // extending the units sees this was a known stop, not an oversight.
  assert.strictEqual(humanBytes(1024 ** 5), '1024 TB');
});

test('humanBytes never renders a bare number without a unit', () => {
  for (const n of [0, 1, 999, 1024, 1024 ** 3, 1024 ** 5]) {
    assert.match(humanBytes(n), /^[\d.]+ (B|KB|MB|GB|TB)$/, `${n} must carry a unit`);
  }
});

test('fmtTime renders an empty string for a missing timestamp', () => {
  // Panels interpolate this directly, so a falsy input must produce nothing rather than the word
  // "null" or the epoch.
  assert.strictEqual(fmtTime(''), '');
  assert.strictEqual(fmtTime(null), '');
  assert.strictEqual(fmtTime(undefined), '');
});

test('fmtTime renders a valid ISO timestamp as hours and minutes', () => {
  // Asserted by SHAPE, not by value: the output goes through toLocaleTimeString, so the exact text
  // depends on the runner's timezone and locale and pinning it would make this fail on a machine
  // in a different zone rather than on a real regression.
  assert.match(fmtTime('2026-08-12T14:35:00Z'), /\d{1,2}:\d{2}/);
});

test('fmtTime does not throw on an unparseable timestamp', () => {
  // Honest about what actually happens: the `catch` branch that would fall back to
  // iso.substring(11,16) is unreachable, because toLocaleTimeString returns the string
  // "Invalid Date" rather than throwing. So the guarantee this function really offers is "never
  // throws", not "degrades to the raw substring". Asserted as the former.
  assert.doesNotThrow(() => fmtTime('not-a-date'));
  assert.strictEqual(typeof fmtTime('not-a-date'), 'string');
});
