// v0.3.8.52 — the value sanitisers that guard CSS and markup contexts.
//
// `cssColor` is the console's only client-side validator, and it is client-side for a stated
// reason: ant accent colours come from uiState.castes[caste].color, which the operator sets and
// which UiStateStore round-trips verbatim by design ("the UI owns the shape"). The server does not
// inspect it, so this function is the entire gate between a stored string and a `style=""`
// attribute. It had no tests.
//
// Run with: node --test tests/ui/sanitizers.test.js
const test = require('node:test');
const assert = require('node:assert');
const { loadFunctions, source } = require('./_app-source.js');

const { cssColor, escapeHtml } = loadFunctions('cssColor', 'escapeHtml');

test('cssColor accepts hex literals at every length CSS allows', () => {
  for (const hex of ['#fff', '#ffff', '#7fa0bc', '#7fa0bcff']) {
    assert.strictEqual(cssColor(hex), hex, `${hex} is a valid hex colour`);
  }
});

test('cssColor accepts a custom-property reference', () => {
  assert.strictEqual(cssColor('var(--ant-worker)'), 'var(--ant-worker)');
});

test('cssColor accepts a bare named colour', () => {
  assert.strictEqual(cssColor('rebeccapurple'), 'rebeccapurple');
});

test('cssColor rejects anything that could carry a second CSS declaration', () => {
  // The shape that matters: a semicolon or a closing quote would end the declaration and let the
  // rest of the value become new CSS.
  const hostile = [
    'red;background:url(http://evil/x)',
    '#fff" onload="alert(1)',
    'expression(alert(1))',
    'url(javascript:alert(1))',
    '</style><script>alert(1)</script>',
  ];

  for (const value of hostile) {
    assert.strictEqual(cssColor(value), '#7fa0bc', `${value} must fall back to the neutral colour`);
  }
});

test('cssColor rejects a hex string that is too long to be a colour', () => {
  assert.strictEqual(cssColor('#0123456789'), '#7fa0bc');
});

test('cssColor rejects a var() reference with anything but a plain custom property inside', () => {
  assert.strictEqual(cssColor('var(--x);color:red'), '#7fa0bc');
  assert.strictEqual(cssColor('var(--x, url(evil))'), '#7fa0bc');
});

test('cssColor honours the caller fallback and defaults to the neutral ant colour', () => {
  assert.strictEqual(cssColor('nonsense!', '#123456'), '#123456');
  assert.strictEqual(cssColor(null), '#7fa0bc');
  assert.strictEqual(cssColor(''), '#7fa0bc');
  assert.strictEqual(cssColor(undefined), '#7fa0bc');
});

test('cssColor trims surrounding whitespace rather than rejecting on it', () => {
  assert.strictEqual(cssColor('  #abc  '), '#abc');
});

test('every colour reaching a style attribute goes through cssColor', () => {
  // A regression guard on the boundary, not on the function. The validator only helps at the sites
  // that call it, and an interpolated `.color` that skips it reopens the hole this closed in
  // v2.14.13.
  const offenders = [...source.matchAll(/style="[^"]*\$\{(?!\s*cssColor)([^}]*\.color[^}]*)\}/g)]
    .map(m => m[1].trim());

  assert.deepStrictEqual(offenders, [],
    `colour values interpolated into a style attribute without cssColor: ${offenders.join(', ')}`);
});

test('escapeHtml and cssColor are independent gates, not substitutes', () => {
  // Documenting the division of labour: escaping a colour would produce valid-looking CSS from a
  // hostile value (entities are decoded before the CSS parser runs), and cssColor does not escape.
  assert.strictEqual(escapeHtml('red;x:1'), 'red;x:1');
  assert.strictEqual(cssColor('red;x:1'), '#7fa0bc');
});
