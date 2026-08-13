// v0.3.8.52 — the CSP handler micro-parser, executed.
//
// This is the most security-load-bearing pure code in the console and it had no tests.
//
// v2.6.3 removed every inline `on*=` handler so the page could run under `script-src 'self'`
// without `unsafe-inline`. The handlers became `data-onclick="someFn('an-id')"` attributes, read
// back with getAttribute() and interpreted by a hand-written parser at the bottom of app.js —
// deliberately NOT eval, because eval would have given back exactly the capability the CSP change
// was made to remove.
//
// A parser that stands in for eval has to be judged on what it REFUSES as much as on what it runs,
// and it also has to round-trip values faithfully: an id that loses a backslash is a silent
// misdispatch. Both properties are asserted here against the real functions lifted from app.js.
//
// Run with: node --test tests/ui/handler-parser.test.js
const test = require('node:test');
const assert = require('node:assert');
const { loadFunctions } = require('./_app-source.js');

const { splitTop, coerce, escapeHtml, jsArg } = loadFunctions('splitTop', 'coerce', 'escapeHtml', 'jsArg');

// The HTML parser decodes entities before the attribute value ever reaches the interpreter. Tests
// that skip this step are testing a pipeline the browser does not run.
function decodeEntities(s) {
  return s.replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>')
          .replace(/&quot;/g, '"').replace(/&#39;/g, "'");
}

// ---------------------------------------------------------------------------------------------
// splitTop — argument splitting that respects nesting and quoting.
// ---------------------------------------------------------------------------------------------

test('splitTop separates top-level arguments', () => {
  assert.deepStrictEqual(splitTop('a,b,c', ',').map(s => s.trim()), ['a', 'b', 'c']);
});

test('splitTop does not split on a separator inside quotes', () => {
  // The case that matters: an id containing a comma must arrive as ONE argument.
  assert.deepStrictEqual(splitTop("'x,y', 3", ',').map(s => s.trim()), ["'x,y'", '3']);
});

test('splitTop does not split on a separator inside brackets or braces', () => {
  assert.deepStrictEqual(splitTop('{a:1,b:2}, [3,4]', ',').map(s => s.trim()), ['{a:1,b:2}', '[3,4]']);
});

test('splitTop honours a backslash-escaped quote rather than ending the string early', () => {
  // If the escape were ignored, the string would close at the apostrophe and the rest of the
  // argument list would be reinterpreted as code.
  assert.deepStrictEqual(splitTop("'it\\'s, fine', 2", ',').map(s => s.trim()), ["'it\\'s, fine'", '2']);
});

test('splitTop drops a trailing empty segment rather than emitting a blank argument', () => {
  assert.deepStrictEqual(splitTop('a;', ';').map(s => s.trim()), ['a']);
});

// ---------------------------------------------------------------------------------------------
// coerce — turning one argument's text into a value.
// ---------------------------------------------------------------------------------------------

test('coerce maps the literal keywords to real values, not to strings', () => {
  assert.strictEqual(coerce('true'), true);
  assert.strictEqual(coerce('false'), false);
  assert.strictEqual(coerce('null'), null);
  assert.strictEqual(coerce('undefined'), undefined);
});

test('coerce binds this and event to the dispatching element and event', () => {
  const el = { tag: 'the element' };
  const ev = { tag: 'the event' };

  assert.strictEqual(coerce('this', el, ev), el);
  assert.strictEqual(coerce('event', el, ev), ev);
});

test('coerce reads integers and decimals as numbers', () => {
  assert.strictEqual(coerce('42'), 42);
  assert.strictEqual(coerce('-7'), -7);
  assert.strictEqual(coerce('1.5'), 1.5);
});

test('coerce unescapes a quoted string back to its literal value', () => {
  assert.strictEqual(coerce("'plain'"), 'plain');
  assert.strictEqual(coerce("'it\\'s'"), "it's");
  assert.strictEqual(coerce('"double"'), 'double');
});

test('coerce parses object and array arguments', () => {
  assert.deepStrictEqual(coerce('{a:1}'), { a: 1 });
  assert.deepStrictEqual(coerce("['x','y']"), ['x', 'y']);
});

test('coerce yields undefined for anything it does not recognise, rather than guessing', () => {
  // The refusal that keeps this from being eval: a bare identifier is NOT resolved to a variable,
  // so `doThing(document.cookie)` passes undefined instead of the cookie.
  assert.strictEqual(coerce('document.cookie'), undefined);
  assert.strictEqual(coerce('window'), undefined);
  assert.strictEqual(coerce('someVariable'), undefined);
  assert.strictEqual(coerce('1+1'), undefined);
  assert.strictEqual(coerce('{bad json'), undefined);
});

// ---------------------------------------------------------------------------------------------
// jsArg — the nested-context escape, and the round trip it promises.
// ---------------------------------------------------------------------------------------------

test('jsArg survives the full attribute round trip for an apostrophe', () => {
  // The property the v3.8.34 comment claims, tested end to end rather than reasoned about:
  // escape for the JS layer, then the HTML layer; the browser decodes the entity; the interpreter
  // unescapes the backslash; the value that comes out is the value that went in.
  const id = "o'brien-42";
  const attribute = `selectThing('${jsArg(id)}')`;
  const asTheParserSeesIt = decodeEntities(attribute);

  const args = splitTop(asTheParserSeesIt.slice(asTheParserSeesIt.indexOf('(') + 1, -1), ',');
  assert.strictEqual(coerce(args[0]), id);
});

test('jsArg survives the round trip for a backslash', () => {
  // Stripping the character instead of escaping it would silently change the id, which is why the
  // implementation escapes rather than sanitises.
  const id = 'a\\b';
  const attribute = `selectThing('${jsArg(id)}')`;
  const asTheParserSeesIt = decodeEntities(attribute);

  const args = splitTop(asTheParserSeesIt.slice(asTheParserSeesIt.indexOf('(') + 1, -1), ',');
  assert.strictEqual(coerce(args[0]), id);
});

test('jsArg prevents an injected apostrophe from appending a second statement', () => {
  // The attack the double escape exists to stop. escapeHtml ALONE is not enough here, because the
  // HTML parser decodes &#39; back into a real apostrophe before the interpreter sees it.
  const hostile = "x'); stealSession('";
  const decodedNaive = decodeEntities(`go('${escapeHtml(hostile)}')`);
  const decodedCorrect = decodeEntities(`go('${jsArg(hostile)}')`);

  // Naive escaping: the interpreter would see more than one statement.
  assert.ok(splitTop(decodedNaive, ';').length > 1, 'precondition — escapeHtml alone leaks a statement break');
  // Correct escaping: one statement, and the payload arrives as inert data.
  assert.strictEqual(splitTop(decodedCorrect, ';').length, 1);
  const args = splitTop(decodedCorrect.slice(decodedCorrect.indexOf('(') + 1, -1), ',');
  assert.strictEqual(coerce(args[0]), hostile);
});

test('jsArg renders null and undefined as an empty argument', () => {
  assert.strictEqual(jsArg(null), '');
  assert.strictEqual(jsArg(undefined), '');
});
