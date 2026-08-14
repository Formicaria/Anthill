// v0.3.8.52 — HTML escaping, executed rather than trusted.
//
// The console builds most of its DOM by interpolating into template strings and assigning
// innerHTML. That is fine while every interpolated value is escaped, and it is a scripting bug the
// moment one is not — so this file does two different jobs, and both matter:
//
//   1. It EXECUTES the real `escapeHtml` lifted out of app.js and checks it against the characters
//      that actually break out of text and attribute contexts.
//   2. It GREPS the real app.js for the specific sinks that were unescaped before this release, so
//      a future edit that reintroduces one fails here instead of shipping.
//
// The second job is a regression guard, not a general audit. It cannot prove the other ~270
// innerHTML sites are clean; it proves these named ones stay fixed. Written that way deliberately —
// a test that claimed to prove the general case would be lying about its own reach.
//
// Run with: node --test tests/ui/escaping.test.js
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const APP = path.join(__dirname, '..', '..', 'src', 'Anthill.UI', 'app.js');
const src = fs.readFileSync(APP, 'utf8');

// Lift the real one-line implementation out of the file and evaluate it, so these assertions are
// about the function the console actually runs — not a copy that could drift from it.
function loadEscapeHtml() {
  const marker = 'function escapeHtml(s){';
  const start = src.indexOf(marker);
  assert.ok(start >= 0, 'escapeHtml not found in app.js — it was renamed or removed');
  const end = src.indexOf('\n', start);
  const decl = src.slice(start, end);
  // eslint-disable-next-line no-eval
  return eval('(' + decl.replace('function escapeHtml', 'function') + ')');
}

const escapeHtml = loadEscapeHtml();

test('escapeHtml neutralises every character that can break out of markup', () => {
  assert.strictEqual(escapeHtml('<script>'), '&lt;script&gt;');
  assert.strictEqual(escapeHtml('a & b'), 'a &amp; b');
  assert.strictEqual(escapeHtml('say "hi"'), 'say &quot;hi&quot;');
  assert.strictEqual(escapeHtml("it's"), 'it&#39;s');
});

test('escapeHtml closes the quoted-attribute escape, not just the text one', () => {
  // The specific shape the model-id fix was about: a double quote ends the attribute value, and
  // everything after it is parsed as further attributes rather than as data.
  const hostile = 'llama3" onload="alert(1)';
  const rendered = `<option value="${escapeHtml(hostile)}">`;

  assert.ok(!/value="[^"]*"\s+onload=/.test(rendered), 'escaped value must not open a new attribute');
  assert.ok(rendered.includes('&quot;'), 'the quote must be entity-encoded');
});

test('escapeHtml renders null and undefined as empty, never as the string "null"', () => {
  // Interpolating a missing field is common here, and `String(null)` would put the word "null" in
  // front of the operator.
  assert.strictEqual(escapeHtml(null), '');
  assert.strictEqual(escapeHtml(undefined), '');
});

test('escapeHtml coerces non-strings rather than throwing', () => {
  assert.strictEqual(escapeHtml(42), '42');
  assert.strictEqual(escapeHtml(false), 'false');
});

test('escapeHtml is not idempotent, so nothing may escape twice', () => {
  // Documented rather than incidental: `&` is itself escaped, so applying the helper to an already
  // escaped string double-encodes it and the operator sees "&amp;lt;". Every call site must escape
  // exactly once, at the interpolation.
  assert.strictEqual(escapeHtml(escapeHtml('<b>')), '&amp;lt;b&amp;gt;');
});

// ---------------------------------------------------------------------------------------------
// Regression guards against the specific sinks fixed in v0.3.8.52.
// ---------------------------------------------------------------------------------------------

test('no catch block interpolates a raw e.message into markup', () => {
  // Seven of these existed before v0.3.8.52. An exception message is not developer-authored text:
  // it can carry a server response body, a URL, or a model id straight into innerHTML.
  const raw = [...src.matchAll(/\$\{e\.message\}/g)];

  assert.strictEqual(raw.length, 0,
    `found ${raw.length} unescaped \${e.message} interpolation(s) — use \${escapeHtml(e.message)}`);
});

test('the error sinks still exist in escaped form, so the guard above cannot pass by deletion', () => {
  // Without this, someone could satisfy the previous test by removing the error reporting entirely.
  const escaped = [...src.matchAll(/\$\{escapeHtml\(e\.message\)\}/g)];

  assert.ok(escaped.length >= 7,
    `expected at least the 7 escaped e.message sinks, found ${escaped.length}`);
});

test('model ids are escaped in the ant-config option list, in both attribute and text position', () => {
  // v0.3.8.55: antcfgModelOptions moved to inspector-routing.js — the inspector/routing domain's
  // own console asset (the app.js size guard's split rule). The guard follows the code.
  const routingSrc = fs.readFileSync(
    path.join(__dirname, '..', '..', 'src', 'Anthill.UI', 'inspector-routing.js'), 'utf8');
  const at = routingSrc.indexOf('function antcfgModelOptions');
  assert.ok(at >= 0, 'antcfgModelOptions not found in inspector-routing.js — renamed or moved again');
  const fn = routingSrc.slice(at);
  const body = fn.slice(0, fn.indexOf('\n}'));

  assert.ok(body.includes('value="${escapeHtml(m)}"'),
    'the model id must be escaped inside the value attribute');
  assert.ok(!/<option value="\$\{m\}"/.test(body),
    'raw model id in an attribute — an Ollama tag containing a quote breaks out of it');
  assert.ok(!/>\$\{m\}</.test(body), 'raw model id in text position');
  assert.ok(!/\$\{curModel\}/.test(body), 'raw curModel interpolation');
});
