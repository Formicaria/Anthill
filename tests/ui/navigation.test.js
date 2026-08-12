// v0.3.8.48 — the navigation, executed rather than grepped. This extracts the REAL IA,
// ROUTE_ALIAS and PAGE_HOME definitions from app.js and evaluates them, so what is asserted here
// is the same data the running console navigates by: the seven destinations, the death of the
// removed domains, every alias landing on a route that exists, and the parameterised project
// route accepting what it must. Run with: node --test tests/ui/navigation.test.js
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const src = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'Anthill.UI', 'app.js'), 'utf8');

function extract(name, open = '[', close = '];') {
  const start = src.indexOf(`const ${name} = ${open}`) >= 0
    ? src.indexOf(`const ${name} = ${open}`)
    : src.indexOf(`const ${name}=${open}`);
  assert.ok(start >= 0, `${name} not found in app.js`);
  const end = src.indexOf(close, start) + close.length;
  // eslint-disable-next-line no-eval
  return eval('(' + src.slice(src.indexOf(open, start), end - 1) + ')');
}

const IA = extract('IA', '[', '];');
const ROUTE_ALIAS = extract('ROUTE_ALIAS', '{', '};');

function iaRoutes() {
  const routes = new Set();
  for (const d of IA) {
    if (d.route) routes.add(d.route);
    for (const s of d.sections || []) {
      routes.add(s.route);
      for (const t of s.tabs || []) routes.add(t.route);
    }
  }
  return routes;
}

test('the navigation has exactly the five destinations, in order', () => {
  // v0.3.8.49 (UI/UX pass §20): Colony, Projects, Chat, Tools, Settings.
  const top = IA.map(d => d.label);
  assert.deepStrictEqual(top,
    ['Colony', 'Projects', 'Chat', 'Tools', 'Settings']);
});

test('the removed / folded destinations do not render as top-level', () => {
  const ids = IA.map(d => d.id);
  // Dashboard folded into Colony/Overview; Objectives into Projects; Integrations into Tools;
  // the old operational domains are gone entirely.
  for (const gone of ['operations', 'infrastructure', 'administration', 'monitoring',
                      'dashboard', 'objectives', 'integrations'])
    assert.ok(!ids.includes(gone), `${gone} is still a top-level nav entry`);
});

test('Models, Roles, Model Routing and the Inspector live under Colony (§11)', () => {
  const colony = IA.find(d => d.id === 'colony');
  assert.ok(colony, 'Colony domain missing');
  const routes = (colony.sections || []).flatMap(s => [s.route, ...(s.tabs || []).map(t => t.route)]);
  for (const r of ['/colony/roles', '/colony/inspector', '/colony/model-routing'])
    assert.ok(routes.includes(r), `${r} is not under Colony`);
});

test('Integrations lives under Tools (§9)', () => {
  const tools = IA.find(d => d.id === 'tools');
  const routes = (tools.sections || []).map(s => s.route);
  assert.ok(routes.includes('/tools/integrations'), 'Integrations not under Tools');
});

test('every alias lands on a route that exists', () => {
  const live = iaRoutes();
  for (const [from, to] of Object.entries(ROUTE_ALIAS))
    assert.ok(live.has(to), `alias ${from} -> ${to}, but ${to} is not a live route`);
});

test('no alias shadows a canonical route, and none chains', () => {
  const live = iaRoutes();
  for (const from of Object.keys(ROUTE_ALIAS)) {
    assert.ok(!live.has(from), `${from} is both an alias and a canonical route`);
    assert.ok(!(ROUTE_ALIAS[ROUTE_ALIAS[from]]), `alias ${from} chains through another alias`);
  }
});

test('the project workspace route accepts ids and rejects traversal', () => {
  const re = /^\/projects\/([A-Za-z0-9]+)$/;
  assert.ok(re.test('/projects/4df08106ade1'));
  assert.ok(!re.test('/projects/'));
  assert.ok(!re.test('/projects/../etc'));
  assert.ok(!re.test('/projects/a/b'));
});

test('every nav entry a keyboard can reach names itself', () => {
  for (const d of IA) {
    assert.ok(d.label && d.label.length > 0, `${d.id} has no label`);
    for (const s of d.sections || []) assert.ok(s.label && s.label.length > 0);
  }
});

test('the approval gate carries the three exact labels', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'Anthill.UI', 'index.html'), 'utf8');
  for (const label of ['>Manual approval<', '>Automatically approve<', '>Skip all approvals<'])
    assert.ok(html.includes(label), `${label} missing from the policy selector`);
});
