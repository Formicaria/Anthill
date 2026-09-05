/* ─────────────────────────────────────────────────────────────────────────────
   KNOWLEDGE — the organizational knowledge console. v0.3.8.121.

   What the colony knows, where it learned it, and where its sources disagree.
   The knowledge itself lives in FORAGER; this renders what ANTHILL's
   /knowledge/* routes return and interprets none of it.

   ── THE ONE THING THIS AREA EXISTS TO DO ────────────────────────────────────
   Answer "why does ANTHILL believe this?" in one click. Every statement shown
   carries its support level, its status, and a control that opens the exact
   source excerpt behind it. A knowledge UI that shows conclusions without
   provenance is a UI that asks to be trusted, which is the opposite of the
   point.

   ── WHAT IT WILL NOT DO ─────────────────────────────────────────────────────
   It computes no confidence, resolves no conflict, and never presents a
   contested statement as settled. FORAGER classifies; ANTHILL carries the
   classification across; this draws it. Where the sources disagree, both sides
   are shown with the disagreement named — never one side silently chosen.

   It never fabricates progress. Ingestion percentages come from FORAGER's
   persisted stage rows; there is no timer here advancing a bar.

   ── STATES ──────────────────────────────────────────────────────────────────
   Three, and they are deliberately distinguishable, because collapsing them is
   how an operator ends up debugging the wrong thing:
     · not configured   — knowledge_enabled is false; nothing is wrong
     · unreachable      — configured, FORAGER is not answering
     · empty            — it answered, and knows nothing about this query

   CSP: no inline handlers. Controls use the console's `data-onclick`
   dispatcher, which resolves through `window`, so every handler is a global.
   ───────────────────────────────────────────────────────────────────────────── */

let knStatus = null;      // last /knowledge/status payload
let knProject = '';       // ANTHILL project id scoping the view ('' = configured default)
let knLastQuery = '';

/* Support levels, in the words the operator should read. The key is what the
   API sends (the C# enum name); the value is how it is shown. An unknown level
   is shown as UNKNOWN rather than guessed — see KnowledgeSupport.Unknown. */
const KN_SUPPORT = {
  DirectFact: { label: 'Direct fact', cls: 'kn-s-direct' },
  SupportedInference: { label: 'Supported inference', cls: 'kn-s-supported' },
  UncertainInference: { label: 'Uncertain inference', cls: 'kn-s-uncertain' },
  UnverifiedClaim: { label: 'Unverified claim', cls: 'kn-s-unverified' },
  Unknown: { label: 'Unknown support', cls: 'kn-s-unknown' },
};

const KN_STATUS = {
  Active: 'kn-st-active', Superseded: 'kn-st-superseded', Disputed: 'kn-st-disputed',
  Unresolved: 'kn-st-unresolved', Stale: 'kn-st-stale', Archived: 'kn-st-archived',
};

function knSay(msg, ok) {
  const el = document.getElementById('kn-say');
  if (!el) return;
  el.textContent = msg || '';
  el.className = 'kn-say' + (msg ? (ok ? ' kn-ok' : ' kn-bad') : '');
}

function knSupport(level) {
  return KN_SUPPORT[level] || KN_SUPPORT.Unknown;
}

/** 0.9 → "0.90". Shown, never used as a threshold — that judgement is the reader's. */
function knConf(v) {
  return (typeof v === 'number' && isFinite(v)) ? v.toFixed(2) : '—';
}

/* ── entry ────────────────────────────────────────────────────────────────── */

async function loadKnowledge() {
  const host = document.getElementById('kn-body');
  if (!host) return;
  host.innerHTML = '<div class="hud-state"><div class="hud-spinner"></div>Checking the knowledge base…</div>';

  try {
    const r = await api('/knowledge/status');
    if (!r || !r.success) {
      host.innerHTML = `<div class="hud-state err">${escapeHtml((r && r.message) || 'Knowledge status is unreadable.')}</div>`;
      return;
    }
    knStatus = r.data || {};
    knRenderShell(host);
  } catch (e) {
    host.innerHTML = `<div class="hud-state err">${escapeHtml(e.message || 'Knowledge status is unreadable.')}</div>`;
  }
}

/* ── the gate ─────────────────────────────────────────────────────────────────
   ONE KEY, AND THE OTHERS ARE NAMED RATHER THAN OFFERED. v0.3.8.124.

   `knowledge_enabled` is the only knowledge setting the settings surface will
   write, and that is the whole design rather than a first instalment. It starts
   or stops using what the config file already says; the endpoint, the token,
   the remote permission and the project map decide WHO the colony trusts and
   WHAT a mission may read, so those stay a file edit. Where one of them is what
   is actually standing in the operator's way, this page says which key and
   where — a toggle that silently could not help is worse than a sentence that
   explains.
   ───────────────────────────────────────────────────────────────────────────── */

/** True when this operator may write settings at all. Mirrors `/settings` POST,
    which is gated on manage_settings — admin-only in the shipped role set. */
function knMayToggle() {
  return ROLE === 'admin';
}

/**
 * Flip the gate and re-read the page.
 *
 * `KnowledgeModule` re-reads its options on every call, so this takes effect on
 * the next request rather than at the next restart — the message says so,
 * because the homelab gate beside it needs a restart and an operator who has
 * used that one will otherwise assume this one does too.
 */
async function knSetGate(on) {
  if (!knMayToggle()) return;
  knSay(on ? 'Enabling…' : 'Disabling…', true);
  try {
    const r = await api('/settings', 'POST', { knowledge_enabled: !!on });
    if (!r || !r.success) { knSay((r && r.message) || 'The setting could not be written.', false); return; }
    await loadKnowledge();
    knSay(on
      ? 'Knowledge enabled. Live on the next request — no restart needed.'
      : 'Knowledge disabled. Missions continue without organizational knowledge.', true);
  } catch (e) {
    knSay(e.message || 'The setting could not be written.', false);
  }
}

/** A non-loopback endpoint that the file has not permitted. Enabling with this
    true configures nothing useful: every request is refused at the client. */
function knRemoteBlocked(s) {
  const ep = s.configured_endpoint || s.endpoint || '';
  if (!ep || s.allow_remote) return false;
  try {
    const h = new URL(ep).hostname.toLowerCase();
    return !(h === 'localhost' || h === '127.0.0.1' || h === '::1' || h === '[::1]' || /^127\./.test(h));
  } catch (_) { return false; }
}

/**
 * The on/off row shown once knowledge IS on — reachable or not.
 *
 * Returns markup, never null, so the two call sites can concatenate it
 * unconditionally; when the operator cannot toggle, it degrades to a statement
 * of what is on rather than disappearing, because "knowledge is enabled" is
 * worth reading even by someone who may not change it.
 */
function knGateBar(s) {
  const ep = escapeHtml(s.configured_endpoint || s.endpoint || '—');
  let right;
  if (s.gate_env_pinned) {
    right = `<span class="kn-sub">pinned by <code>${escapeHtml(s.gate_env_var || '')}</code> — the config file cannot change it</span>`;
  } else if (knMayToggle()) {
    right = '<button class="kn-btn" data-onclick="knSetGate(false)">Disable knowledge</button>';
  } else {
    right = '<span class="kn-sub">changing this needs <code>manage_settings</code></span>';
  }

  return '<div class="kn-gate">'
    + '<span>Knowledge: <b class="kn-ok">enabled</b></span>'
    + `<span class="kn-sub">${ep}</span>`
    + '<span class="kn-gate-sp"></span>'
    + right
    + '</div>';
}

function knRenderShell(host) {
  const s = knStatus || {};

  // Not configured. Not an error — say what it is, offer the switch, and stop.
  if (!s.enabled) {
    const ep = s.configured_endpoint || '';
    let action;
    if (s.gate_env_pinned) {
      // The switch is pinned by the environment. Offering a button here would
      // write config.json, lose to the variable on re-projection, and leave the
      // page looking exactly as it does now — the button that appears to do
      // nothing. Name the variable instead.
      action = '<p class="kn-lede kn-bad">This colony pins the switch in its environment: '
        + `<code>${escapeHtml(s.gate_env_var || 'ANTHILL_KNOWLEDGE_ENABLED')}</code> is set, and it `
        + 'overrides the config file. Change it where the process environment is defined — a toggle '
        + 'here would be overridden the moment it was applied.</p>';
    } else if (!knMayToggle()) {
      action = '<p class="kn-sub">Enabling knowledge needs <code>manage_settings</code>.</p>';
    } else {
      action = '<button class="kn-btn kn-primary" data-onclick="knSetGate(true)">Enable knowledge</button>'
        + (knRemoteBlocked(s)
            ? '<p class="kn-lede kn-bad" style="margin-top:8px">The configured endpoint is not on '
              + 'loopback, and <code>knowledge_forager_allow_remote</code> is off — requests will be '
              + 'refused until that key is set in the config file. It is deliberately not editable '
              + 'here: FORAGER has no authentication of its own, so reaching one across a network is '
              + 'a decision to make in the file.</p>'
            : '');
    }

    host.innerHTML = '<div class="kn-card"><h3>Knowledge is not configured</h3>'
      + '<p class="kn-lede">This colony has no organizational knowledge base. Knowledge comes from '
      + 'FORAGER, a separate local application that turns documents into evidence-backed, traceable '
      + 'statements.</p>'
      + `<p class="kn-sub">Endpoint: <code>${escapeHtml(ep || 'not set')}</code></p>`
      + '<p class="kn-lede">Switching it on here sets <code>knowledge_enabled</code>. The endpoint, '
      + 'the access token and the project map stay in the config file — they decide which service '
      + 'the colony trusts and which knowledge a mission may read, so they are not editable from a '
      + 'browser. Set <code>knowledge_forager_endpoint</code> and map this colony\'s projects with '
      + '<code>knowledge_project_map</code>; see <code>docs/FORAGER_INTEGRATION.md</code>.</p>'
      + '<p class="kn-lede">Missions run normally without it.</p>'
      + action
      + '<div class="kn-say" id="kn-say"></div></div>';
    return;
  }

  // Configured but not answering. Distinguished from "knows nothing" on purpose.
  if (!s.reachable) {
    host.innerHTML = '<div class="kn-card"><h3>The knowledge base is not responding</h3>'
      + `<p class="kn-lede">${escapeHtml(s.reason || 'FORAGER did not answer.')}</p>`
      + `<p class="kn-sub">Endpoint: <code>${escapeHtml(s.endpoint || s.configured_endpoint || '—')}</code></p>`
      + '<p class="kn-lede">Missions continue without organizational knowledge. Retrieval will report '
      + 'itself unavailable rather than answering from assumption.</p>'
      + '<button class="kn-btn" data-onclick="loadKnowledge()">Check again</button>'
      // Offered HERE too, because "it is on and not answering" is exactly when an operator wants to
      // switch it off — leaving the control only on the working page would mean the one state you
      // cannot leave is the broken one.
      + knGateBar(s)
      + '<div class="kn-say" id="kn-say"></div></div>';
    return;
  }

  const projects = Array.isArray(s.projects) ? s.projects : [];
  const backendNote = s.search_backend === 'sqlite-fts5'
    ? 'ranked full-text search'
    : 'substring fallback — no stemming or ranking';

  host.innerHTML =
    '<div class="kn-card">'
    + knGateBar(s)
    + '<div class="kn-searchrow">'
    + '<input id="kn-q" class="kn-input" type="search" placeholder="Search organizational knowledge…" '
    + 'autocomplete="off" aria-label="Search organizational knowledge">'
    + '<button class="kn-btn kn-primary" data-onclick="knSearch()">Search</button>'
    + '<button class="kn-btn" data-onclick="knRetrieve()" title="Assemble evidence-backed context, the way an agent receives it">Retrieve context</button>'
    + '<button class="kn-btn" data-onclick="knEntity()" title="Look the query up as a person, project, customer or product">Look up entity</button>'
    + '</div>'
    + '<div class="kn-controls">'
    + (projects.length
        ? '<label class="kn-lbl">Project <select id="kn-project" class="kn-select" data-onchange="knSetProject()">'
          + '<option value="">(default)</option>'
          + projects.map(p => `<option value="${escapeHtml(p)}">${escapeHtml(p)}</option>`).join('')
          + '</select></label>'
        : '<span class="kn-sub">No project map configured — using the default knowledge base.</span>')
    + '<label class="kn-lbl"><input type="checkbox" id="kn-hist"> Include superseded</label>'
    + `<span class="kn-sub">${escapeHtml(s.search_backend || '—')} · ${escapeHtml(backendNote)}`
    + (s.model_provider ? ` · extraction: ${escapeHtml(s.model_provider)}` : '')
    + '</span>'
    + '</div>'
    + '<div class="kn-say" id="kn-say"></div>'
    + '</div>'
    + '<div class="kn-cols">'
    + '<div id="kn-results" class="kn-results"><div class="kn-empty">Search the knowledge base, or open the conflicts below to see where its sources disagree.</div></div>'
    + '<div id="kn-detail" class="kn-detail"><div class="kn-empty">Select a statement to see its evidence.</div></div>'
    + '</div>'
    + '<div class="kn-card"><h3>Conflicts</h3>'
    + '<p class="kn-lede">Where two sources say different things. ANTHILL never picks a side on your behalf.</p>'
    + '<div id="kn-conflicts"><div class="hud-state">Loading…</div></div></div>'
    + '<div class="kn-card"><h3>Sources</h3>'
    + '<p class="kn-lede">The documents this knowledge was extracted from. Duplicates and superseded versions are kept, never overwritten.</p>'
    + '<div id="kn-sources"><div class="hud-state">Loading…</div></div></div>'
    + '<div class="kn-card"><h3>Processing</h3>'
    + '<p class="kn-lede">Ingestion runs in FORAGER. Progress below is its persisted stage state, not an estimate.</p>'
    + '<div id="kn-jobs"><div class="hud-state">Loading…</div></div></div>';

  const box = document.getElementById('kn-q');
  if (box) box.addEventListener('keydown', (e) => { if (e.key === 'Enter') knSearch(); });

  knLoadConflicts();
  knLoadSources();
  knLoadJobs();
}

function knSetProject() {
  knProject = (document.getElementById('kn-project')?.value || '');
  knLoadConflicts();
  knLoadSources();
  knLoadJobs();
  if (knLastQuery) knSearch();
}

/** The scope every request carries. Empty means the configured default. */
function knScopeQs(prefix) {
  return knProject ? `${prefix}project=${encodeURIComponent(knProject)}` : '';
}

/* ── search ───────────────────────────────────────────────────────────────── */

async function knSearch() {
  const q = (document.getElementById('kn-q')?.value || '').trim();
  const results = document.getElementById('kn-results');
  if (!results) return;
  if (!q) { knSay('Enter something to search for.', false); return; }
  knLastQuery = q;
  knSay('');

  const hist = document.getElementById('kn-hist')?.checked ? '&include_historical=true' : '';
  results.innerHTML = '<div class="hud-state"><div class="hud-spinner"></div>Searching…</div>';

  try {
    const r = await api(`/knowledge/search?q=${encodeURIComponent(q)}&limit=25${hist}&${knScopeQs('')}`);
    if (!r || !r.success) {
      results.innerHTML = `<div class="hud-state err">${escapeHtml((r && r.message) || 'Search failed.')}</div>`;
      return;
    }
    knRenderHits(results, r.data || {});
  } catch (e) {
    results.innerHTML = `<div class="hud-state err">${escapeHtml(e.message || 'Search failed.')}</div>`;
  }
}

function knRenderHits(host, data) {
  const hits = data.hits || [];
  if (!hits.length) {
    // "Searched and found nothing" is a real answer and is worded as one — it is not
    // the same as "the knowledge base is empty" or "the question is unanswerable".
    host.innerHTML = '<div class="kn-empty">Nothing in the knowledge base matches that. '
      + 'The base was searched and had no match — that is not evidence the answer is unknown to '
      + 'the organization, only that no document here states it.</div>';
    return;
  }

  host.innerHTML = `<div class="kn-sub">${hits.length} statement(s) · ${escapeHtml(data.backend || '')} · ${escapeHtml(String(data.took_ms || 0))}ms</div>`
    + hits.map(h => {
      const sup = knSupport(h.support);
      return '<div class="kn-hit">'
        + `<div class="kn-stmt">${escapeHtml(h.statement || h.title || '(no statement)')}</div>`
        + '<div class="kn-meta">'
        + `<span class="kn-pill ${sup.cls}">${escapeHtml(sup.label)}</span>`
        + `<span class="kn-pill ${KN_STATUS[h.status] || ''}">${escapeHtml(h.status || '')}</span>`
        + `<span class="kn-sub">conf ${escapeHtml(knConf(h.confidence))}</span>`
        + `<span class="kn-sub">${escapeHtml(h.type || '')}</span>`
        + (h.contested ? '<span class="kn-pill kn-st-disputed">contested</span>' : '')
        + (h.evidence_count === 0 ? '<span class="kn-pill kn-st-unresolved">no evidence</span>' : '')
        + '</div>'
        + (h.why ? `<div class="kn-why">${escapeHtml(h.why)}</div>` : '')
        + `<button class="kn-btn kn-sm" data-onclick="knOpen('${escapeHtml(h.knowledge_id)}')">Why do we believe this?</button>`
        + '</div>';
    }).join('');
}

/* ── detail + evidence ────────────────────────────────────────────────────── */

async function knOpen(id) {
  const panel = document.getElementById('kn-detail');
  if (!panel) return;
  panel.innerHTML = '<div class="hud-state"><div class="hud-spinner"></div>Loading evidence…</div>';

  try {
    const [item, evidence] = await Promise.all([
      api(`/knowledge/items/${encodeURIComponent(id)}?${knScopeQs('')}`),
      api(`/knowledge/items/${encodeURIComponent(id)}/evidence?${knScopeQs('')}`),
    ]);

    if (!item || !item.success) {
      panel.innerHTML = `<div class="hud-state err">${escapeHtml((item && item.message) || 'Not readable.')}</div>`;
      return;
    }

    const d = item.data || {};
    const ev = (evidence && evidence.success && evidence.data && evidence.data.evidence) || [];
    const sup = knSupport(d.support);

    panel.innerHTML = '<div class="kn-dhead">Knowledge item</div>'
      + `<div class="kn-stmt kn-big">${escapeHtml(d.statement || '')}</div>`
      + '<div class="kn-meta">'
      + `<span class="kn-pill ${sup.cls}">${escapeHtml(sup.label)}</span>`
      + `<span class="kn-pill ${KN_STATUS[d.status] || ''}">${escapeHtml(d.status || '')}</span>`
      + `<span class="kn-sub">confidence ${escapeHtml(knConf(d.confidence))}</span>`
      + '</div>'
      + (d.status === 'Superseded' && d.superseded_by
          ? `<div class="kn-warn">Superseded by <code>${escapeHtml(d.superseded_by)}</code>. It was true of its time; it is not the current state.</div>`
          : '')
      + (d.contested
          ? '<div class="kn-warn">This statement is contested — another source disagrees. See Conflicts.</div>'
          : '')
      + (d.support === 'UnverifiedClaim'
          ? '<div class="kn-warn">Unverified claim: a source asserts this, and nothing supports it.</div>'
          : '')
      + '<dl class="kn-facts">'
      + knFact('Type', d.type)
      + knFact('Subject', d.subject)
      + knFact('Attribute', d.attribute_key ? `${d.attribute_key} = ${d.attribute_value ?? ''}` : null)
      + knFact('Effective date', d.effective_date)
      + knFact('Confidentiality', d.confidentiality)
      + knFact('Extractor', d.extractor)
      + knFact('Id', d.knowledge_id)
      + '</dl>'
      + '<div class="kn-dhead">Evidence</div>'
      + (ev.length ? ev.map(knEvidenceBlock).join('')
          : '<div class="kn-warn">No located evidence. This statement is unresolved: something asserted '
            + 'it and the supporting text cannot be found. Check the source before relying on it.</div>');
  } catch (e) {
    panel.innerHTML = `<div class="hud-state err">${escapeHtml(e.message || 'Not readable.')}</div>`;
  }
}

function knFact(label, value) {
  if (value === null || value === undefined || value === '') return '';
  return `<dt>${escapeHtml(label)}</dt><dd>${escapeHtml(String(value))}</dd>`;
}

function knEvidenceBlock(e) {
  return '<div class="kn-ev">'
    + `<div class="kn-evsrc">${escapeHtml(e.source_name || e.source_id || '')}`
    + (e.location ? ` <span class="kn-sub">· ${escapeHtml(e.location)}</span>` : '')
    + '</div>'
    + (e.excerpt ? `<blockquote class="kn-quote">${escapeHtml(e.excerpt)}</blockquote>` : '')
    + (e.missing_excerpt
        ? '<div class="kn-warn">The quoted text could not be located in the source any more.</div>'
        : '')
    + '<div class="kn-sub">'
    + (e.extractor ? `${escapeHtml(e.extractor)} · ` : '')
    + `confidence ${escapeHtml(knConf(e.confidence))}`
    + (e.excerpt_hash ? ` · <code>${escapeHtml(String(e.excerpt_hash).slice(0, 12))}</code>` : '')
    + '</div></div>';
}

/* ── retrieve: the context an agent actually receives ─────────────────────── */

async function knRetrieve() {
  const q = (document.getElementById('kn-q')?.value || '').trim();
  const panel = document.getElementById('kn-detail');
  if (!panel) return;
  if (!q) { knSay('Enter a question to retrieve context for.', false); return; }

  panel.innerHTML = '<div class="hud-state"><div class="hud-spinner"></div>Assembling context…</div>';
  try {
    const r = await api('/knowledge/retrieve', 'POST', {
      query: q,
      project: knProject || null,
      include_historical: !!document.getElementById('kn-hist')?.checked,
    });
    if (!r || !r.success) {
      panel.innerHTML = `<div class="hud-state err">${escapeHtml((r && r.message) || 'Retrieval failed.')}</div>`;
      return;
    }
    const d = r.data || {};
    // The rendered block is shown VERBATIM. This is the exact text a model is given,
    // and being able to read it is the difference between a knowledge feature you can
    // audit and one you have to take on faith.
    panel.innerHTML = '<div class="kn-dhead">Context as the model receives it</div>'
      + `<div class="kn-sub">${escapeHtml(String(d.facts ? d.facts.length : 0))} fact(s) · `
      + `${escapeHtml(String(d.open_conflicts || 0))} open conflict(s) · ${escapeHtml(String(d.took_ms || 0))}ms`
      + (d.truncated ? ' · truncated' : '') + '</div>'
      + (d.degradation ? `<div class="kn-warn">Partial retrieval — ${escapeHtml(d.degradation)}</div>` : '')
      + `<pre class="kn-pre">${escapeHtml(d.rendered || '')}</pre>`;
  } catch (e) {
    panel.innerHTML = `<div class="hud-state err">${escapeHtml(e.message || 'Retrieval failed.')}</div>`;
  }
}

/* ── entities ─────────────────────────────────────────────────────────────── */

/** Resolve a name to a canonical entity — how "Bob Smith" and "Robert Smith" turn out to be one. */
async function knEntity() {
  const name = (document.getElementById('kn-q')?.value || '').trim();
  const panel = document.getElementById('kn-detail');
  if (!panel) return;
  if (!name) { knSay('Enter a name to look up.', false); return; }

  panel.innerHTML = '<div class="hud-state"><div class="hud-spinner"></div>Looking up…</div>';
  try {
    const r = await api(`/knowledge/entities?name=${encodeURIComponent(name)}&${knScopeQs('')}`);
    if (!r || !r.success) {
      panel.innerHTML = `<div class="hud-state err">${escapeHtml((r && r.message) || 'Lookup failed.')}</div>`;
      return;
    }
    const list = (r.data && r.data.entities) || [];
    if (!list.length) {
      panel.innerHTML = `<div class="kn-empty">No entity named ${escapeHtml(name)} is known in this scope.</div>`;
      return;
    }
    panel.innerHTML = '<div class="kn-dhead">Entities</div>' + list.map(e =>
      '<div class="kn-ev">'
      + `<div class="kn-evsrc">${escapeHtml(e.name || '')} <span class="kn-sub">${escapeHtml(e.type || '')}</span></div>`
      + (e.aliases && e.aliases.length
          ? `<div class="kn-sub">also known as: ${escapeHtml(e.aliases.join(', '))}</div>` : '')
      + `<div class="kn-sub">${escapeHtml(String(e.mention_count || 0))} mention(s) · confidence ${escapeHtml(knConf(e.confidence))} · <code>${escapeHtml(e.entity_id || '')}</code></div>`
      + '</div>').join('');
  } catch (e) {
    panel.innerHTML = `<div class="hud-state err">${escapeHtml(e.message || 'Lookup failed.')}</div>`;
  }
}

/* ── sources ──────────────────────────────────────────────────────────────── */

async function knLoadSources() {
  const host = document.getElementById('kn-sources');
  if (!host) return;
  try {
    const r = await api(`/knowledge/sources?${knScopeQs('')}`);
    if (!r || !r.success) {
      host.innerHTML = `<div class="kn-empty">${escapeHtml((r && r.message) || 'Sources are not readable.')}</div>`;
      return;
    }
    const list = (r.data && r.data.sources) || [];
    if (!list.length) { host.innerHTML = '<div class="kn-empty">No documents have been registered yet.</div>'; return; }

    host.innerHTML = list.map(s => '<div class="kn-hit">'
      + `<div class="kn-stmt">${escapeHtml(s.name || s.source_id || '')}</div>`
      + '<div class="kn-meta">'
      + `<span class="kn-pill">${escapeHtml(s.type || '')}</span>`
      + `<span class="kn-pill kn-st-${escapeHtml(s.processing_status || '')}">${escapeHtml(s.processing_status || '')}</span>`
      + (s.authoritative ? '<span class="kn-pill kn-st-Active">authoritative</span>' : '')
      + (s.duplicate_of ? '<span class="kn-pill kn-st-superseded">duplicate</span>' : '')
      + (s.superseded_by ? '<span class="kn-pill kn-st-superseded">superseded</span>' : '')
      + `<span class="kn-sub">${escapeHtml(String(s.chunk_count || 0))} chunk(s)</span>`
      + (s.document_date ? `<span class="kn-sub">${escapeHtml(s.document_date)}</span>` : '')
      + '</div>'
      + (s.content_hash ? `<div class="kn-sub"><code>${escapeHtml(String(s.content_hash).slice(0, 16))}</code></div>` : '')
      + '</div>').join('');
  } catch (e) {
    host.innerHTML = '<div class="kn-empty">Sources are not readable.</div>';
  }
}

/* ── conflicts ────────────────────────────────────────────────────────────── */

async function knLoadConflicts() {
  const host = document.getElementById('kn-conflicts');
  if (!host) return;
  try {
    const r = await api(`/knowledge/conflicts?${knScopeQs('')}`);
    if (!r || !r.success) {
      host.innerHTML = `<div class="kn-empty">${escapeHtml((r && r.message) || 'Conflicts are not readable.')}</div>`;
      return;
    }
    const list = (r.data && r.data.conflicts) || [];
    if (!list.length) { host.innerHTML = '<div class="kn-empty">No open conflicts. Every statement agrees with its neighbours.</div>'; return; }

    host.innerHTML = list.map(c => '<div class="kn-conflict">'
      + `<div class="kn-cft">${escapeHtml(c.type || '')}`
      + (c.attribute_key ? ` <span class="kn-sub">on ${escapeHtml(c.attribute_key)}</span>` : '')
      + ` <span class="kn-pill kn-st-disputed">${escapeHtml(c.status || '')}</span></div>`
      + (c.description ? `<div class="kn-lede">${escapeHtml(c.description)}</div>` : '')
      + (c.suggested_resolution
          ? `<div class="kn-sub">Suggested, <strong>not applied</strong>: ${escapeHtml(c.suggested_resolution)}</div>`
          : '<div class="kn-sub">No suggestion — the evidence does not favour either side.</div>')
      + '<div class="kn-cflinks">'
      + (c.knowledge_ids || []).map(id =>
          `<button class="kn-btn kn-sm" data-onclick="knOpen('${escapeHtml(id)}')">${escapeHtml(id)}</button>`).join('')
      + '</div></div>').join('');
  } catch (e) {
    host.innerHTML = '<div class="kn-empty">Conflicts are not readable.</div>';
  }
}

/* ── ingestion status ─────────────────────────────────────────────────────── */

async function knLoadJobs() {
  const host = document.getElementById('kn-jobs');
  if (!host) return;
  try {
    const r = await api(`/knowledge/jobs?${knScopeQs('')}`);
    if (!r || !r.success) {
      host.innerHTML = `<div class="kn-empty">${escapeHtml((r && r.message) || 'Processing state is not readable.')}</div>`;
      return;
    }
    const jobs = (r.data && r.data.jobs) || [];
    if (!jobs.length) { host.innerHTML = '<div class="kn-empty">Nothing has been processed into this knowledge base yet.</div>'; return; }

    host.innerHTML = jobs.map(j => {
      const pct = Math.round((j.progress || 0) * 100);
      const stages = (j.stages || []).map(s =>
        `<span class="kn-stage kn-stage-${escapeHtml(s.status || '')}" title="${escapeHtml(s.name || '')}: ${escapeHtml(s.status || '')} (${escapeHtml(String(s.processed || 0))} processed, ${escapeHtml(String(s.failed || 0))} failed)">${escapeHtml((s.name || '').replace(/_/g, ' '))}</span>`
      ).join('');
      return '<div class="kn-job">'
        + `<div class="kn-jobhead"><code>${escapeHtml(j.job_id || '')}</code> `
        + `<span class="kn-pill kn-st-${escapeHtml(j.status || '')}">${escapeHtml(j.status || '')}</span>`
        + `<span class="kn-sub">${escapeHtml(String(pct))}%</span></div>`
        + `<div class="kn-bar"><div class="kn-barfill" style="width:${pct}%"></div></div>`
        + `<div class="kn-stages">${stages}</div>`
        + (j.error ? `<div class="kn-warn">${escapeHtml(j.error)}</div>` : '')
        + (!j.terminal
            ? `<button class="kn-btn kn-sm" data-onclick="knCancel('${escapeHtml(j.job_id)}')">Cancel</button>`
            : (j.status === 'failed'
                ? `<button class="kn-btn kn-sm" data-onclick="knRetry('${escapeHtml(j.job_id)}')">Retry from checkpoint</button>`
                : ''))
        + '</div>';
    }).join('');
  } catch (e) {
    host.innerHTML = '<div class="kn-empty">Processing state is not readable.</div>';
  }
}

async function knJobAction(id, action, describe) {
  knSay('…', true);
  try {
    const r = await api(`/knowledge/jobs/${encodeURIComponent(id)}/${action}?${knScopeQs('')}`, 'POST');
    if (r && r.success === false) {
      knSay(`${describe} refused: ${(r.error || r.message || 'no reason given')}`, false);
      return;
    }
    knSay(`${describe} accepted.`, true);
    knLoadJobs();
  } catch (e) {
    knSay(`${describe} failed: ${(e && e.message) ? e.message : 'request error'}`, false);
  }
}

function knCancel(id) { knJobAction(id, 'cancel', 'Cancellation'); }
function knRetry(id) { knJobAction(id, 'retry', 'Retry'); }

PAGE_ENTER['knowledge'] = () => loadKnowledge();
