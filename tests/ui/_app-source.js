// v0.3.8.52 — shared plumbing for the console's node --test suites.
//
// The console has no build step and no module system (that is the point of the self-contained
// binary: app.js is embedded as a resource and served as one file). So a test cannot `require` a
// function out of it. What it CAN do is read app.js as text, cut out the exact declaration it wants,
// and evaluate that — which is what navigation.test.js has done since v0.3.8.48 and what this
// generalises, so each new suite does not hand-roll its own slicer.
//
// The value of doing it this way rather than copying the function into the test: a copy passes
// forever after the original changes. This fails the moment the real declaration is renamed,
// removed, or broken.
//
// NOT a test file — the name has no `.test.` in it, so `node --test tests/ui/` skips it.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert');

const APP_PATH = path.join(__dirname, '..', '..', 'src', 'Anthill.UI', 'app.js');
const source = fs.readFileSync(APP_PATH, 'utf8');

/**
 * Index just past the `}` that closes the block opening at `open`.
 *
 * Naive brace counting is wrong for this file and fails in a way that looks like a missing
 * function: `cssColor` contains /^#[0-9a-fA-F]{3,8}$/, and those braces are regex quantifiers, not
 * blocks. So quotes, template literals, comments and regex literals are all skipped over.
 *
 * The regex-literal test is the usual heuristic — a `/` is a literal when the last meaningful
 * character before it cannot end an expression. It is not a JS parser and does not need to be; it
 * needs to be right about the handful of declarations these suites lift.
 */
function matchBrace(text, open) {
  assert.strictEqual(text[open], '{', 'matchBrace must start on an opening brace');
  let depth = 0;
  let prev = '';
  for (let i = open; i < text.length; i++) {
    const c = text[i];
    const next = text[i + 1];

    if (c === '/' && next === '/') { i = text.indexOf('\n', i); if (i < 0) break; continue; }
    if (c === '/' && next === '*') { i = text.indexOf('*/', i) + 1; continue; }

    if (c === '"' || c === "'" || c === '`') {
      for (i++; i < text.length; i++) {
        if (text[i] === '\\') { i++; continue; }
        if (text[i] === c) break;
      }
      prev = c;
      continue;
    }

    if (c === '/' && !'})]'.includes(prev) && !/[\w$]/.test(prev)) {
      for (i++; i < text.length; i++) {
        if (text[i] === '\\') { i++; continue; }
        if (text[i] === '[') { while (i < text.length && text[i] !== ']') { if (text[i] === '\\') i++; i++; } continue; }
        if (text[i] === '/') break;
      }
      prev = '/';
      continue;
    }

    if (c === '{') depth++;
    else if (c === '}') { depth--; if (depth === 0) return i + 1; }

    if (!/\s/.test(c)) prev = c;
  }
  throw new Error('unbalanced braces while slicing app.js');
}

/** The full source text of a `function NAME(...) { ... }` declaration, as written in app.js. */
function functionSource(name) {
  const decl = new RegExp(`function\\s+${name}\\s*\\(`);
  const m = decl.exec(source);
  assert.ok(m, `function ${name} not found in app.js — renamed or removed?`);
  const open = source.indexOf('{', m.index + m[0].length - 1);
  return source.slice(m.index, matchBrace(source, open));
}

/**
 * Evaluate one or more of app.js's function declarations and hand back callables.
 *
 * Order matters when one calls another — `jsArg` calls `escapeHtml`, so both must be loaded
 * together, and they end up sharing one scope exactly as they do in the browser.
 */
function loadFunctions(...names) {
  const decls = names.map(functionSource).join('\n');
  const exported = `{${names.join(',')}}`;
  // eslint-disable-next-line no-new-func
  return new Function(`${decls}\nreturn ${exported};`)();
}

module.exports = { APP_PATH, source, functionSource, loadFunctions, matchBrace };
