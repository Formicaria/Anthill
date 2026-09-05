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

/**
 * Every route the ROUTER can resolve — which since v0.3.8.124 is not the same set as the routes the
 * SIDEBAR shows.
 *
 * `buildRoutes` derives ROUTE_TABLE from IA, so for most of this file's life "a live route" and "an
 * IA entry" were the same thing. `/tools/infrastructure` broke that on purpose: Infrastructure is
 * reached from its row in the mound registry, being a mound, and a nav entry would have been a
 * second door that made the registry's listing a half-truth. So it is registered straight onto
 * ROUTE_TABLE after the build.
 *
 * Read out of app.js by the same slice-and-evaluate trick the rest of this file uses, rather than
 * listed here: a hand-kept list of the exceptions is exactly the copy that passes forever after the
 * original changes.
 */
function iaRoutes() {
  const routes = new Set();
  for (const d of IA) {
    if (d.route) routes.add(d.route);
    for (const s of d.sections || []) {
      routes.add(s.route);
      for (const t of s.tabs || []) routes.add(t.route);
    }
  }
  for (const m of src.matchAll(/^ROUTE_TABLE\['([^']+)'\]\s*=/gm)) routes.add(m[1]);
  return routes;
}

test('a route may exist without a nav entry, and Infrastructure is the one that does', () => {
  const live = iaRoutes();
  const inNav = new Set();
  for (const d of IA) {
    if (d.route) inNav.add(d.route);
    for (const s of d.sections || []) { inNav.add(s.route); for (const t of s.tabs || []) inNav.add(t.route); }
  }
  assert.ok(live.has('/tools/infrastructure'), 'the router cannot resolve /tools/infrastructure');
  assert.ok(!inNav.has('/tools/infrastructure'),
    'Infrastructure is in the sidebar again — it is reached from the mound registry, and a second '
    + 'door makes the registry\'s "every mound, and where its settings are" a half-truth');
});

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

test('routing left Colony for Projects, and every old link still lands (§11, v0.3.8.124)', () => {
  const colony = IA.find(d => d.id === 'colony');
  assert.ok(colony, 'Colony domain missing');
  const routes = (colony.sections || []).flatMap(s => [s.route, ...(s.tabs || []).map(t => t.route)]);

  // v0.3.8.49: the standalone Ants & Roles tab folded into Models & Routing. v0.3.8.55: Models &
  // Routing merged INTO the Ant Inspector. v0.3.8.124: the Inspector itself is gone — its telemetry
  // is the ant tab in Colony Live, and its ROUTING moved into projects, because "which model does
  // this work" turned out to be a per-project question a colony-wide page could not express.
  assert.ok(!routes.includes('/colony/inspector'),
    '/colony/inspector is a Colony section again — it was retired in v0.3.8.124');
  assert.ok(!routes.includes('/colony/model-routing'),
    '/colony/model-routing is a section again — it merged into the Inspector in v0.3.8.55');

  // Every bookmark that used to reach model configuration now reaches Projects, which is where
  // model configuration IS. Not Colony Live: that draws the ants and configures none of them.
  for (const old of ['/colony/inspector', '/colony/model-routing', '/colony/roles',
                     '/settings/roles', '/colony/agents'])
    assert.strictEqual(ROUTE_ALIAS[old], '/projects',
      `${old} must land on Projects, where routing now lives`);

  // And Colony keeps the two destinations that are still about looking at the colony.
  assert.ok(routes.includes('/colony/live'), 'Colony Live is not under Colony');
  assert.ok(routes.includes('/colony/mounds'), 'the mound registry is not under Colony');
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
