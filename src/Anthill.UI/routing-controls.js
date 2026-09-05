/* ROUTING CONTROLS — which model does the work, and for whom. v0.3.8.124.
 *
 * Was `inspector-routing.js`, the per-role selectors on the Ant Inspector page. That page is gone
 * and routing moved into PROJECTS, so this file moved with it and is named for what it does rather
 * than for the page it used to sit on.
 *
 * WHY ROUTING BECAME A PROJECT'S DECISION. It was colony-wide: one priority model and fourteen
 * per-role routes in `config.json`. An operator running one project against a local model and
 * another against Claude had no way to say so — every change was to the whole colony, and the
 * workaround was rewriting the routes between missions. The plumbing was half-built and had been
 * for two releases: `Project.DefaultProvider` / `DefaultModel` were persisted, writable, and read
 * by nothing.
 *
 * WHAT AN EMPTY ROW MEANS, AND WHY IT IS NOT "NO MODEL". A role this project does not name INHERITS
 * the colony's route. That is the difference between a project being a set of overrides an operator
 * fills in as they care to, and a fourteen-row form they have to complete before the project can run
 * anything. The rows say so in words, and each one shows what it inherits, because "inherits
 * llama3.3" and "unrouted" look identical if you only draw the blank.
 *
 * Classic script, deferred, loaded after app.js. Everything is reached at CALL time and nothing at
 * parse time — the previous version bound two `getElementById(...).addEventListener` handlers at the
 * top level, which meant deleting the page those ids lived on would have thrown during load and
 * taken the whole console with it. */

let obsProvModels = new Map();

function obsProviderLabel(p) {
  if (p === 'ollama') return 'Ollama (local)';
  const a = AGENT_LABEL(p); if (a) return a + ' (agent)';
  return p;
}

/** What can actually RUN: installed local models, configured catalogs, installed agents. */
async function loadRoutableModels() {
  obsProvModels = new Map();
  try {
    const rj = await api('/routes/json');
    if (rj && rj.success && rj.data) {
      for (const m of (rj.data.available_models || [])) {
        if (!obsProvModels.has(m.provider)) obsProvModels.set(m.provider, []);
        obsProvModels.get(m.provider).push({ model: m.model, label: m.label });
      }
    }
  } catch { /* an empty map renders every current route as unavailable, which is honest */ }
  return obsProvModels;
}

/* v0.3.8.52: escaped, in a quoted attribute and in text.
 *
 * Two sources feed `models`, and only one of them is ours. `antcfgCatalog` is ProviderCatalog.All,
 * a hardcoded list in the SDK — safe today, and safe only for as long as it stays hardcoded.
 * `availableModels` is the tag list read back from a LOCAL OLLAMA over HTTP, which is a separate
 * process serving whatever names its models were pulled under. A `"` in one of those ends the
 * attribute value and everything after it parses as markup.
 *
 * Escaping both rather than only the Ollama branch: which array a value came from is not visible
 * at this line, and a rule that depends on remembering that distinction is one edit from being
 * wrong. `curModel` is escaped for the same reason — it is a stored config value. */
function antcfgModelOptions(provider, curModel) {
  const models = provider === 'ollama' ? availableModels : ((antcfgCatalog.find(p => p.provider === provider) || {}).models || []);
  const opts = models.map(m => `<option value="${escapeHtml(m)}"${m === curModel ? ' selected' : ''}>${escapeHtml(m)}</option>`).join('');
  const extra = curModel && !models.includes(curModel) ? `<option value="${escapeHtml(curModel)}" selected>${escapeHtml(curModel)} (current)</option>` : '';
  return `<option value="">— none —</option>${opts}${extra}`;
}

function antcfgProviderOptions(curProvider) {
  const providers = [{ provider: 'ollama', name: 'Ollama (local)' }, ...antcfgCatalog];
  return providers.map(p => {
    const connected = p.provider === 'ollama' || antcfgConfigured.has(p.provider);
    const label = connected ? p.name : `${p.name} (not connected)`;
    const selected = p.provider === (curProvider || 'ollama') ? ' selected' : '';
    return `<option value="${p.provider}"${selected}>${label}</option>`;
  }).join('');
}

/* ── The project's routing panel ────────────────────────────────────────────────────────────── */

let pvRoutingId = null, pvRoutingData = null;

/**
 * Render the routing controls for one project into `#pv-routing`.
 *
 * Reads `/projects/{id}/routes`, which returns the project's own priority, its per-role overrides,
 * AND what each role inherits from the colony when it has none. That third field is why the whole
 * thing is one server call: a page that showed only the overrides could not tell an operator the
 * difference between a role following the colony's model and a role with nothing set.
 */
async function renderProjectRouting(projectId) {
  const host = document.getElementById('pv-routing');
  if (!host) return;
  pvRoutingId = projectId || null;
  if (!projectId) { host.innerHTML = ''; return; }

  host.innerHTML = '<div class="muted" style="font-size:11px;">Reading this project’s model routing…</div>';

  try {
    await Promise.all([fetchModelNames(), fetchProviderCatalog(), loadRoutableModels()]);
    const r = await api('/projects/' + encodeURIComponent(projectId) + '/routes');
    if (!r || !r.success) throw new Error((r && r.message) || 'could not read routing');
    pvRoutingData = r.data || {};
  } catch (e) {
    host.innerHTML = `<div class="muted" style="font-size:11px;color:var(--red)">Routing unavailable: ${escapeHtml(e.message || 'unknown error')}</div>`;
    return;
  }

  const d = pvRoutingData;
  const active = !!d.priority_active;

  host.innerHTML = `
    <div style="font-size:13px;font-weight:700;margin:16px 0 2px">Models for this project</div>
    <div style="font-size:10px;color:var(--muted);line-height:1.45;margin-bottom:10px">
      Every mission and every chat turn in this project routes through these. A role you leave alone
      follows the colony’s own route — this is a set of overrides, not a form to fill in.
    </div>

    <div class="antcfg-card" style="margin-bottom:12px">
      <div style="font-size:12px;font-weight:700;margin-bottom:2px">Priority model</div>
      <div style="font-size:10px;color:var(--muted);line-height:1.45;margin-bottom:8px">
        When set, <strong>every ant in this project tries this model first</strong>, whatever its own
        route says below. Each role’s own route is kept and is what the project falls back to if this
        model is unhealthy — clearing this restores every choice below exactly as it was.
      </div>
      <div class="antcfg-field">
        <label>Provider</label>
        <select id="pv-prio-provider" class="antcfg-model">${antcfgProviderOptions(d.priority_provider || 'ollama')}</select>
      </div>
      <div class="antcfg-field">
        <label>Model</label>
        <select id="pv-prio-model" class="antcfg-model">${antcfgModelOptions(d.priority_provider || 'ollama', d.priority_model || '')}</select>
      </div>
      <div style="display:flex;gap:8px;align-items:center;margin-top:6px">
        <button class="btn btn-primary" id="pv-prio-save" style="font-size:10px">Save priority</button>
        <span class="save-msg" id="pv-prio-msg" style="font-size:10px"></span>
      </div>
      <div style="font-size:10px;margin-top:6px;color:${active ? 'var(--queen)' : 'var(--dim)'}">
        ${active
          ? 'Active — every ant here tries ' + escapeHtml(d.priority_model || '') + ' first.'
          : (d.colony_priority_active
              ? 'Not set — this project follows the colony’s own priority model.'
              : 'Not set — each role uses its route below, or the colony’s.')}
      </div>
    </div>

    <div id="pv-routes-list"></div>`;

  renderProjectRouteRows();

  document.getElementById('pv-prio-provider')?.addEventListener('change', function () {
    const m = document.getElementById('pv-prio-model');
    if (m) m.innerHTML = antcfgModelOptions(this.value, '');
  });
  document.getElementById('pv-prio-save')?.addEventListener('click', saveProjectPriority);
}

/** One row per routable role: what it uses here, and what it would use if left alone. */
function renderProjectRouteRows() {
  const list = document.getElementById('pv-routes-list');
  if (!list || !pvRoutingData) return;

  list.innerHTML = (pvRoutingData.roles || []).map(r => {
    const provs = [...obsProvModels.keys()];
    const curP = r.overridden ? (r.provider || 'ollama') : '';
    const provOpts = `<option value=""${curP ? '' : ' selected'}>— follow the colony —</option>`
      + (curP && !provs.includes(curP) ? `<option value="${escapeHtml(curP)}" selected>${escapeHtml(curP)} ⚠</option>` : '')
      + provs.map(p => `<option value="${escapeHtml(p)}"${p === curP ? ' selected' : ''}>${escapeHtml(obsProviderLabel(p))}</option>`).join('');

    const models = obsProvModels.get(curP) || [];
    const curM = r.overridden ? (r.model || '') : '';
    const known = models.some(m => m.model === curM);
    const modelOpts = (known || !curM ? [] : [`<option value="${escapeHtml(curM)}" selected>⚠ ${escapeHtml(curM)} (unavailable)</option>`])
      .concat(models.map(m => `<option value="${escapeHtml(m.model)}"${m.model === curM ? ' selected' : ''}>${escapeHtml(m.model)}</option>`)).join('');

    return `<div class="pv-route-row" data-role="${escapeHtml(r.role)}">
      <span class="pv-route-name">${escapeHtml(r.role)}</span>
      <select class="provider-input pv-route-prov" data-role="${escapeHtml(r.role)}" aria-label="Provider for ${escapeHtml(r.role)}">${provOpts}</select>
      <select class="provider-input pv-route-model" data-role="${escapeHtml(r.role)}" aria-label="Model for ${escapeHtml(r.role)}" ${curP ? '' : 'hidden'}>${modelOpts}</select>
      <span class="pv-route-inherit muted">${r.overridden
        ? ''
        : 'colony: ' + escapeHtml((r.colony_provider || '—') + ' · ' + (r.colony_model || '—'))}</span>
      <span class="save-msg" data-pv-msg="${escapeHtml(r.role)}"></span>
    </div>`;
  }).join('');

  list.querySelectorAll('.pv-route-prov').forEach(sel => sel.addEventListener('change', () => {
    const role = sel.dataset.role, p = sel.value;
    const mSel = list.querySelector(`.pv-route-model[data-role="${CSS.escape(role)}"]`);
    // AN EMPTY PROVIDER CLEARS THE OVERRIDE. It does not mean "no model" — a project cannot
    // un-route a role, only decline to override it, and the server reads both-empty as "inherit".
    if (!p) { if (mSel) mSel.hidden = true; saveProjectRoute(role, '', ''); return; }
    const models = obsProvModels.get(p) || [];
    if (mSel) {
      mSel.hidden = false;
      mSel.innerHTML = models.map((m, i) => `<option value="${escapeHtml(m.model)}"${i === 0 ? ' selected' : ''}>${escapeHtml(m.model)}</option>`).join('');
    }
    saveProjectRoute(role, p, models.length ? models[0].model : '');
  }));

  list.querySelectorAll('.pv-route-model').forEach(sel => sel.addEventListener('change', () => {
    const role = sel.dataset.role;
    const p = list.querySelector(`.pv-route-prov[data-role="${CSS.escape(role)}"]`)?.value || '';
    saveProjectRoute(role, p, sel.value);
  }));
}

async function saveProjectRoute(role, provider, model) {
  if (!pvRoutingId) return;
  const msg = document.querySelector(`[data-pv-msg="${CSS.escape(role)}"]`);
  try {
    const r = await api('/projects/' + encodeURIComponent(pvRoutingId) + '/routes', 'POST', { role, provider, model });
    if (msg) {
      msg.textContent = (r && r.success) ? (provider ? 'Saved' : 'Follows the colony') : ((r && r.message) || 'Save failed.');
      msg.className = 'save-msg ' + (r && r.success ? 'text-green' : 'text-red');
    }
    // The row's "colony: …" hint and the priority banner both read from the same payload, so the
    // panel is re-read rather than patched in place — one source, and no half-updated row.
    if (r && r.success) { pvRoutingData = null; renderProjectRouting(pvRoutingId); }
  } catch (e) {
    if (msg) { msg.textContent = e.message || 'Save failed.'; msg.className = 'save-msg text-red'; }
  }
}

/**
 * The project's priority model, saved through `PATCH /projects/{id}`.
 *
 * Posted even when EMPTY, because clearing it is a real operator decision — "stop promoting that
 * model here" has to be expressible, and a save that only ever wrote non-empty values would make
 * the priority impossible to turn off from the console. The same rule the colony-wide one has kept
 * since v3.8.1.
 */
async function saveProjectPriority() {
  if (!pvRoutingId) return;
  const p = document.getElementById('pv-prio-provider'), m = document.getElementById('pv-prio-model');
  const msg = document.getElementById('pv-prio-msg');
  if (!p || !m) return;
  try {
    const r = await api('/projects/' + encodeURIComponent(pvRoutingId), 'PATCH', {
      default_provider: m.value ? p.value : '',
      default_model: m.value || '',
    });
    if (!r || !r.success) throw new Error((r && r.message) || 'priority update rejected');
    if (msg) { msg.textContent = 'Saved'; msg.className = 'save-msg text-green'; }
    renderProjectRouting(pvRoutingId);
  } catch (e) {
    if (msg) { msg.textContent = e.message || 'Save failed.'; msg.className = 'save-msg text-red'; }
  }
}
