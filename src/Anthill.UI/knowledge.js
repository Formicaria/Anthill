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
let knBases = [];         // knowledge bases FORAGER reports (GET /knowledge/projects)
let knColonyProjects = []; // this colony's own projects — what a binding can be made FOR
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

    /* v0.3.8.158 — AND WHAT FORAGER ACTUALLY HOLDS. Fetched here rather than inside the renderer so
       the page draws once with everything it needs: a picker that appears a beat after the card it
       lives in is a picker an operator has already scrolled past. Only asked when the credential
       works — an unauthenticated call would 401 and the answer would be an empty list, which reads
       as "FORAGER has no projects" and is a different and much worse sentence. */
    knBases = [];
    if (knStatus.enabled && knStatus.reachable && knStatus.authenticated !== false && knMayManage()) {
      try {
        const p = await api('/knowledge/projects');
        if (p && p.success && p.data && Array.isArray(p.data.projects)) knBases = p.data.projects;
      } catch (_) { /* the page works without the picker; the bind row says so */ }
    }

    /* v0.3.8.160 — AND THIS COLONY'S OWN PROJECTS, which is the half `.158` dropped.
       A binding is FROM an ANTHILL project TO a FORAGER knowledge base. `.158` rebuilt the page
       with the FORAGER half and no way to choose the ANTHILL half, so every Bind wrote the
       DEFAULT — and `Queen.ResolveKnowledgeScope` refuses to let a MISSION fall back to the
       default on purpose. The page was offering the one binding that cannot feed the colony. */
    knColonyProjects = [];
    try {
      const cp = await api('/projects');
      if (cp && cp.success && cp.data && Array.isArray(cp.data.projects))
        knColonyProjects = cp.data.projects.filter(x => !x.archived);
    } catch (_) { /* the selector degrades to the default, which the row then labels honestly */ }

    // One project and nothing chosen yet is not an ambiguity — it is the answer.
    if (!knProject && knColonyProjects.length === 1) knProject = knColonyProjects[0].id;

    knRenderShell(host);
  } catch (e) {
    host.innerHTML = `<div class="hud-state err">${escapeHtml(e.message || 'Knowledge status is unreadable.')}</div>`;
  }
}

/* ── the gate ─────────────────────────────────────────────────────────────────
   ONE KEY, AND THE OTHERS ARE NAMED RATHER THAN OFFERED. v0.3.8.124.

   v0.3.8.157/.158 — this used to say `knowledge_enabled` was the ONLY knowledge
   setting the settings surface would write. Four are now writable: the switch,
   the study schedule, the endpoint (loopback addresses only — the server refuses
   the rest), and the credential (written, never read back). What stays a file
   edit is `knowledge_forager_allow_remote`, the one that widens WHO the colony
   may talk to. Where a file-only key is what stands in the operator's way, this
   page names it — a toggle that silently could not help is worse than a sentence
   that explains.
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
 * because the infrastructure gate beside it needs a restart and an operator who has
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
function knGateBar(s, showEndpoint = true) {
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
    + (showEndpoint ? `<span class="kn-sub">${ep}</span>` : '')
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

    host.innerHTML = '<div class="kn-card"><h3>Connect a knowledge base</h3>'
      + '<p class="kn-lede">Knowledge comes from FORAGER, a separate local application that turns '
      + 'documents into evidence-backed statements. Missions run normally without it.</p>'
      + `<p class="kn-sub">Endpoint: <code>${escapeHtml(ep || 'not set — set knowledge_forager_endpoint in the config file')}</code></p>`
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

  const bases = knBases;            // what FORAGER says it holds (GET /knowledge/projects)
  const map = s.project_map || {};
  const bound = knProject ? map[knProject] : (s.default_project || '');
  const signedOut = s.authenticated === false;

  /* v0.3.8.158 — THE PAGE IS A STATE, NOT A PILE OF CARDS.
     A knowledge integration is either not connected, connected but not signed in, signed in but not
     bound, or working — and exactly one of those is true at a time. The previous page drew all four
     at once and let the operator work out which they were in, which is why "connected" sat above a
     field asking them to type a project ref they had no way to know. */
  host.innerHTML =
    knStatusCard(s, bound, signedOut)
    + (signedOut ? '' : knWorkCard(s, bound, bases))
    + '<div class="kn-cols">'
    + '<div id="kn-results" class="kn-results"><div class="kn-empty">Search the knowledge base.</div></div>'
    + '<div id="kn-detail" class="kn-detail"><div class="kn-empty">Select a statement to see its evidence.</div></div>'
    + '</div>'
    + '<details class="kn-card" id="kn-import"><summary>Import documents</summary>'
    + knIngestForm()
    + '<div id="kn-jobs"><div class="hud-state">Loading…</div></div></details>'
    + '<details class="kn-card"><summary>Sources</summary>'
    + '<div id="kn-sources"><div class="hud-state">Loading…</div></div></details>'
    + '<details class="kn-card"><summary>Conflicts</summary>'
    + '<p class="kn-sub">Where two sources disagree. ANTHILL never picks a side for you.</p>'
    + '<div id="kn-conflicts"><div class="hud-state">Loading…</div></div></details>'
    + '<details class="kn-card"><summary>Review proposals</summary>'
    + '<p class="kn-sub">Where the colony disagreed with a stored statement. Accepting records that '
    + 'you agreed; it does not change the knowledge base.</p>'
    + '<div id="kn-reviews"><div class="hud-state">Loading…</div></div></details>'
    + knBindingsCard(s);

  const box = document.getElementById('kn-q');
  if (box) box.addEventListener('keydown', (e) => { if (e.key === 'Enter') knSearch(); });

  knLoadConflicts();
  knLoadSources();
  knLoadJobs();
  knLoadReviews();
}

/**
 * ONE LINE THAT SAYS WHERE YOU STAND, AND THE ONE THING TO DO NEXT.
 *
 * THREE STATES, DRAWN AS THREE. `/ready` is public on FORAGER and everything carrying knowledge is
 * not, so "it answered" and "it will answer us" are different facts and the console used to show
 * only the first — CONNECTED above a page whose every call was refused 401.
 */
function knStatusCard(s, bound, signedOut) {
  const endpoint = escapeHtml(s.configured_endpoint || s.endpoint || '');
  const dot = signedOut ? 'kn-bad' : 'kn-ok';
  const word = signedOut ? 'signed out' : 'connected';

  let body = '';
  if (signedOut) {
    body = '<p class="kn-lede">FORAGER answered, and refused this colony\'s credential. Knowledge '
      + 'retrieval will return nothing until it has one.</p>'
      /* THE WORDS FORAGER ACTUALLY USES. v0.3.8.159 — the first cut of these steps said
         "Settings → API tokens", which is what the ROUTE is called (`POST /api/settings/tokens`)
         and not what the operator sees. FORAGER's Settings page calls the section "Programs that
         may use Forager" and the control "Give a program access". An instruction that names a menu
         the person is looking at and cannot find is worse than no instruction: it tells them the
         page is out of date, and they are right. */
      + '<ol class="kn-steps">'
      + '<li>In FORAGER, open <b>Settings</b> and find <b>Programs that may use Forager</b>.</li>'
      + '<li>Click <b>Give a program access</b>. Call it <i>Anthill</i>, and let it read and ingest '
      + '(add review as well if the colony should raise review proposals). All projects, unless you '
      + 'want this colony limited to some of them.</li>'
      + '<li>Copy the key it shows — it starts <code>fgr_</code> and FORAGER shows it once.</li>'
      + '<li>Paste it here.</li>'
      + '</ol>'
      + (knMayToggle()
          ? '<div class="kn-bindrow">'
            + '<input id="kn-token" class="kn-input" type="password" autocomplete="off" '
            + 'placeholder="fgr_…" aria-label="FORAGER integration token">'
            + '<button class="kn-btn kn-primary" data-onclick="knSetToken()">Save token</button>'
            + '</div>'
            + '<p class="kn-sub">Stored in this colony\'s config and never shown again — not here, '
            + 'not in the settings response, not in the example file.</p>'
          : '<p class="kn-sub">Setting the credential needs <code>manage_settings</code>.</p>');
  }

  return '<div class="kn-card kn-status">'
    + knSteps(s, bound, signedOut)
    + '<div class="kn-statusline">'
    + `<span class="kn-dot ${dot}"></span><b>${word}</b>`
    + `<code class="kn-ep">${endpoint}</code>`
    + (s.version ? `<span class="kn-sub">FORAGER ${escapeHtml(s.version)}</span>` : '')
    + (signedOut ? '' : `<span class="kn-sub">${escapeHtml(s.search_backend || '')}</span>`)
    + '<span class="kn-gate-sp"></span>'
    + knGateBar(s, false)
    + '</div>'
    + body
    + (s.token_set && !signedOut && knMayToggle()
        ? '<details class="kn-sub"><summary>Replace the credential</summary>'
          + '<div class="kn-bindrow">'
          + '<input id="kn-token" class="kn-input" type="password" autocomplete="off" '
          + 'placeholder="fgr_…" aria-label="FORAGER integration token">'
          + '<button class="kn-btn" data-onclick="knSetToken()">Save token</button>'
          + '</div></details>'
        : '')
    + knEndpointRow(s)
    + '<div class="kn-say" id="kn-say-conn"></div>'
    + '</div>';
}

/**
 * WHERE YOU ARE IN THE FOUR STEPS. v0.3.9.1.
 *
 * The operator's report was that they still did not understand how to make the colony study what
 * FORAGER holds — after a release that rebuilt this page around exactly that path. The page said
 * what was true at each moment and never said what the PATH was, so an operator who had done three
 * of four things could not see which one was missing.
 *
 * Four steps, ticked from the colony's own state. Nothing here is a new fact; it is the facts
 * already on this page, arranged as the sequence they actually are.
 */
function knSteps(s, bound, signedOut) {
  var connected = !!(s && s.enabled && s.reachable);
  var authed = connected && !signedOut;
  var studying = (s && s.auto_study) === 'on';

  function step(n, done, label, note) {
    return '<li class="kn-step' + (done ? ' done' : '') + '">'
      + '<span class="kn-tick">' + (done ? '✓' : n) + '</span>'
      + '<span><b>' + escapeHtml(label) + '</b>'
      + (note ? ' <span class="kn-sub">' + note + '</span>' : '') + '</span></li>';
  }

  return '<ol class="kn-flow">'
    + step(1, connected, 'Connect to FORAGER',
        connected ? '' : 'FORAGER is not answering on ' + escapeHtml(s.configured_endpoint || 'the configured endpoint') + '.')
    + step(2, authed, 'Give this colony a credential',
        authed ? '' : 'FORAGER → Settings → Programs that may use Forager.')
    + step(3, !!bound, 'Bind a project to a knowledge base',
        bound ? '' : (knProject
          ? 'Pick one below.'
          : 'Choose the ANTHILL project first — the console default is not a binding a mission can use.'))
    // STUDY IS THE STEP THE OPERATOR COULD NOT FIND, and it names its own requirement: a mission
    // studies knowledge through its PROJECT's binding, so the button cannot exist for the default.
    + step(4, !!bound && studying, 'Study it',
        !bound ? 'Available once a project is bound.'
          : studying ? 'Every 6 hours, 25 documents a pass.'
          : 'Press <b>Study</b> below to read new documents once, or tick <b>Study new documents automatically</b>.')
    + '</ol>';
}

/** The card you work in once the colony can actually talk to FORAGER. */
function knWorkCard(s, bound, bases) {
  return '<div class="kn-card">'
    + knProjectRow()
    + knBaseRow(s, bound, bases)
    + '<div class="kn-say" id="kn-say-bind"></div>'
    + '</div>'
    + '<div class="kn-card">'
    + '<div class="kn-searchrow">'
    + '<input id="kn-q" class="kn-input" type="search" placeholder="Ask what the organization knows…" '
    + 'autocomplete="off" aria-label="Search organizational knowledge">'
    + '<button class="kn-btn kn-primary" data-onclick="knSearch()">Search</button>'
    + '<button class="kn-btn" data-onclick="knRetrieve()" title="Assemble evidence-backed context, the way an agent receives it">Context</button>'
    + '<button class="kn-btn" data-onclick="knEntity()" title="Look the query up as a person, project, customer or product">Entity</button>'
    + '</div>'
    + '<label class="kn-lbl"><input type="checkbox" id="kn-hist"> Include superseded</label>'
    + '<div class="kn-say" id="kn-say"></div>'
    + '</div>';
}

/**
 * WHICH KNOWLEDGE BASE THIS PROJECT READS — A LIST, NOT A SPELLING TEST. v0.3.8.158.
 *
 * The field used to be free text with a paragraph explaining that FORAGER published no way to
 * enumerate its projects. It publishes one: `GET /api/projects`, read from the running service's own
 * routes rather than assumed from a contract note written against 0.1.4. So the operator picks from
 * what is actually there, with the document and statement counts beside each name, and P11 in the
 * shared contract is closed by the producer having done it.
 */
function knBaseRow(s, bound, bases) {
  const project = knColonyProjects.find(x => x.id === knProject);
  const who = knProject
    ? `<b>${escapeHtml(project ? project.name : knProject)}</b>`
    : 'The console default';

  if (bound) {
    const base = bases.find(b => b.project_ref === bound);
    return `<p class="kn-lede">${who} reads <b>${escapeHtml(base ? base.name : bound)}</b>`
      + (base ? ` <span class="kn-sub">${base.source_count} document(s), ${base.knowledge_count} statement(s)</span>` : '')
      + '</p>'
      + knDefaultWarning()
      + (knMayManage()
          ? '<div class="kn-bindrow">'
            + '<button class="kn-btn kn-primary" data-onclick="knImport()">Import documents</button>'
            + `<button class="kn-btn" data-onclick="knSeed('${jsArg(knProject)}')" title="Run the colony over every document it has not studied yet, up to 25">Study</button>`
            + `<button class="kn-btn" data-onclick="knUnbind('${jsArg(knProject)}')">Unbind</button>`
            + '</div>'
            + knScheduleRow(s)
          : '');
  }

  if (!knMayManage()) {
    return `<p class="kn-lede">${who} has no knowledge base bound. Binding one needs <code>manage_knowledge</code>.</p>`;
  }

  if (!bases.length) {
    return `<p class="kn-lede">${who} has no knowledge base bound, and FORAGER is holding none yet.</p>`
      + '<p class="kn-sub">Create a project in FORAGER, put some documents in it, then reload this page.</p>'
      + '<button class="kn-btn" data-onclick="loadKnowledge()">Reload</button>';
  }

  return `<p class="kn-lede">${who} has no knowledge base bound, so its missions retrieve nothing `
    + 'and say so — never someone else\'s knowledge. Pick one:</p>'
    + knDefaultWarning()
    + '<div class="kn-bindrow">'
    + '<select id="kn-bind-base" class="kn-select" aria-label="FORAGER knowledge base">'
    + bases.map(b => `<option value="${escapeHtml(b.project_ref)}">${escapeHtml(b.name)}`
        + ` — ${b.source_count} document(s), ${b.knowledge_count} statement(s)</option>`).join('')
    + '</select>'
    + '<button class="kn-btn kn-primary" data-onclick="knBind()">Bind</button>'
    + '</div>';
}

/**
 * WHICH ANTHILL PROJECT THIS BINDING IS FOR. v0.3.8.160.
 *
 * A binding has two halves and `.158` shipped one of them. Restored as a SELECT over the colony's
 * own projects rather than the text field it used to be, for the same reason the FORAGER half is a
 * select: an operator should not have to know an id to use a page about the thing the id names.
 */
function knProjectRow() {
  if (!knColonyProjects.length) {
    return '<p class="kn-sub">This colony has no projects yet. A binding belongs to a project — '
         + 'make one on the Projects page, then come back.</p>';
  }

  return '<label class="kn-lbl">Knowledge for '
    + '<select id="kn-project" class="kn-select" data-onchange="knSetProject()">'
    + knColonyProjects.map(p => `<option value="${escapeHtml(p.id)}"${p.id === knProject ? ' selected' : ''}>`
        + `${escapeHtml(p.name || p.id)}</option>`).join('')
    + `<option value=""${knProject ? '' : ' selected'}>the console default</option>`
    + '</select></label>';
}

/**
 * THE DEFAULT IS NOT A BINDING, AND SAYING SO IS THE WHOLE POINT.
 *
 * `Queen.ResolveKnowledgeScope` refuses a mission whose project is unmapped rather than falling
 * back to the default — a mission reading a knowledge base that is not its own is the single
 * failure the map exists to prevent. So a colony that bound ONLY the default has configured the
 * console and nothing else, and every mission still retrieves nothing. That is a sentence the page
 * has to say where the control is, not in a doc.
 */
function knDefaultWarning() {
  if (knProject) return '';
  return '<p class="kn-sub kn-bad">The default is used by this page only. A MISSION never falls '
       + 'back to it — bind the project itself, or the colony still retrieves nothing.</p>';
}

/**
 * THE STUDY SCHEDULE, AS A SWITCH. v0.3.8.157 — shown only where it means something, which is under
 * a bound knowledge base: a schedule with nothing to study is a control that cannot do anything.
 */
function knScheduleRow(s) {
  const on = (s && s.auto_study) === 'on';
  const state = on
    ? 'Studying new documents every 6 hours, 25 a pass.'
    : 'The colony studies this base only when you press Study.';

  if (!knMayToggle()) {
    return `<p class="kn-sub">Automatic study: <b>${on ? 'on' : 'off'}</b>. ${escapeHtml(state)}</p>`;
  }

  return '<label class="kn-lbl"><input type="checkbox" id="kn-autostudy"' + (on ? ' checked' : '')
    + ' data-onchange="knSetAutoStudy()"> Study new documents automatically</label>'
    + `<span class="kn-sub"> ${escapeHtml(state)}</span>`;
}

async function knSetAutoStudy() {
  const on = !!document.getElementById('kn-autostudy')?.checked;
  knSayBind(on ? 'Turning the schedule on…' : 'Turning the schedule off…', true);
  try {
    const r = await api('/settings', 'POST', { knowledge_auto_study: on ? 'on' : 'off' });
    if (!r || !r.success) { knSayBind((r && r.message) || 'The setting could not be written.', false); return; }
    await loadKnowledge();
    knSayBind(on ? 'Automatic study is on.' : 'Automatic study is off. Study still works on demand.', true);
  } catch (e) {
    knSayBind((e && e.message) || 'The setting could not be written.', false);
  }
}

/**
 * THE CREDENTIAL. Written, never read back — the whole basis on which a `Secret` was allowed onto
 * the settings surface at all. The field is cleared on success rather than left holding the value.
 */
async function knSetToken() {
  const el = document.getElementById('kn-token');
  const value = (el?.value || '').trim();
  if (!value) { knSayConn('Paste the token FORAGER showed you when you created it.', false); return; }

  knSayConn('Saving…', true);
  try {
    const r = await api('/settings', 'POST', { knowledge_forager_token: value });
    if (el) el.value = '';
    if (!r || !r.success) { knSayConn((r && r.message) || 'The token could not be written.', false); return; }
    await loadKnowledge();
    // THE EFFECT, NOT THE MESSAGE: the probe re-runs on reload and answers the only question that
    // matters — does FORAGER accept it.
    knSayConn(knStatus && knStatus.authenticated === false
      ? 'FORAGER refused that token. Check it was copied whole and has not been revoked.'
      : 'Token saved — FORAGER accepted it.', knStatus ? knStatus.authenticated !== false : true);
  } catch (e) {
    knSayConn((e && e.message) || 'The token could not be written.', false);
  }
}

function knSayConn(msg, ok) {
  const el = document.getElementById('kn-say-conn');
  if (!el) return;
  el.textContent = msg || '';
  el.className = 'kn-say' + (msg ? (ok ? ' kn-ok' : ' kn-bad') : '');
}

/**
 * WHERE FORAGER IS. Writable because moving it to another port is ordinary; LOOPBACK ONLY, because
 * pointing the colony's source of organizational fact at another host is the decision the config
 * file exists for. The server refuses the write; this says so before it happens.
 */
function knEndpointRow(s) {
  if (!knMayToggle()) return '';
  const ep = (s && (s.configured_endpoint || s.endpoint)) || '';

  return '<details class="kn-sub"><summary>Change where FORAGER is</summary>'
    + '<div class="kn-bindrow">'
    + `<input id="kn-endpoint" class="kn-input" type="text" value="${escapeHtml(ep)}" `
    + 'placeholder="http://127.0.0.1:8790" aria-label="FORAGER endpoint">'
    + '<button class="kn-btn" data-onclick="knSetEndpoint()">Save</button>'
    + '</div>'
    + '<p class="kn-sub">This machine only. A FORAGER on another host needs '
    + '<code>knowledge_forager_allow_remote</code> in the config file first.</p>'
    + '</details>';
}

async function knSetEndpoint() {
  const value = (document.getElementById('kn-endpoint')?.value || '').trim();
  knSayConn('Saving…', true);
  try {
    const r = await api('/settings', 'POST', { knowledge_forager_endpoint: value });
    if (!r || !r.success) { knSayConn((r && r.message) || 'The endpoint could not be written.', false); return; }

    // THE EFFECT, NOT THE MESSAGE. `ApplySettingsUpdate` refuses a non-loopback endpoint by NOT
    // APPLYING it, and still answers success for the request as a whole.
    await loadKnowledge();
    const now = (knStatus && (knStatus.configured_endpoint || knStatus.endpoint)) || '';
    if (value && now !== value) {
      knSayConn('Refused: that endpoint is not on this machine, and knowledge_forager_allow_remote '
        + 'is off in the config file. Still using ' + now + '.', false);
      return;
    }
    knSayConn('Endpoint saved. Live on the next request — no restart needed.', true);
  } catch (e) {
    knSayConn((e && e.message) || 'The endpoint could not be written.', false);
  }
}

/** Open the import panel and put the cursor in it. The button and the panel are one action. */
function knImport() {
  const panel = document.getElementById('kn-import');
  if (!panel) return;
  panel.open = true;
  panel.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  document.getElementById('kn-ingest-paths')?.focus();
}

function knSetProject() {
  knProject = (document.getElementById('kn-project')?.value || '');
  // A REDRAW, not just a reload: which project is selected decides what the bind row says and
  // whether the default warning is showing, and those are the controls the operator is looking at.
  const host = document.getElementById('kn-body');
  if (host) knRenderShell(host);
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

/* ── knowledge bases: what is bound to what ───────────────────────────────────
   v0.3.8.153 — THE CONTROL THAT ANSWERS THE REFUSAL.

   Every Knowledge panel in the operator's build read "No knowledge base is
   mapped for this project. Map it in knowledge_project_map, or set
   knowledge_default_project" — a refusal naming two config keys, shown by a UI
   with no way to set either. Correct, and unactionable without a text editor and
   a restart. `.148` shipped `POST /knowledge/project-map` and recorded the
   missing panel as a UI GAP so the deferral could be CHECKED rather than only
   asserted; this is the panel.

   THE KNOWLEDGE BASE IS TYPED, NOT PICKED, and that is the honest shape rather
   than a shortcut. Every FORAGER path is project-ROOTED — `projects/{id}/…` —
   and the integration has never specified a way to ask which ids exist. That is
   P11 in the shared contract, producer-side and not yet served. A picker with
   nothing to pick from would have to invent `GET /api/projects`, which is the
   second implementation §1 forbids, arriving as a 404 in the field. So the field
   is free text, and the panel says plainly why.
   ───────────────────────────────────────────────────────────────────────────── */

/** True when this operator may write the map. Mirrors the route's Manage gate. */
function knMayManage() {
  return ROLE === 'admin';
}

function knBindingsCard(s) {
  const map = (s && s.project_map) || {};
  const seeded = (s && s.seeded_counts) || {};
  const fallback = (s && s.default_project) || '';
  const rows = Object.keys(map).sort();

  const body = rows.length
    ? '<table class="kn-table"><thead><tr><th>ANTHILL project</th><th>Knowledge base</th>'
      + '<th>Studied</th>' + (knMayManage() ? '<th></th>' : '') + '</tr></thead><tbody>'
      + rows.map(p =>
          '<tr><td><code>' + escapeHtml(p) + '</code></td>'
        + '<td><code>' + escapeHtml(map[p]) + '</code></td>'
        + '<td class="kn-sub">' + escapeHtml(String((seeded && seeded[p]) || 0)) + '</td>'
        + (knMayManage()
            ? '<td><button class="kn-btn kn-sm" data-onclick="knSeed(\'' + jsArg(p) + '\')">Study</button>'
              + '<button class="kn-btn kn-sm" data-onclick="knUnbind(\'' + jsArg(p) + '\')">Unbind</button></td>'
            : '')
        + '</tr>').join('')
      + '</tbody></table>'
    : '<div class="kn-empty">Nothing bound yet.</div>';

  return '<details class="kn-card"><summary>All bindings</summary>'
    + '<p class="kn-sub">Which knowledge base each project reads. A project with no binding refuses '
    + 'rather than guessing, and a mission never falls back to the default — reading a knowledge base '
    + 'that is not its own is the failure the mapping exists to prevent.</p>'
    + body
    + '<p class="kn-sub">Default (console only): '
    + (fallback ? '<code>' + escapeHtml(fallback) + '</code>' : '<i>none</i>') + '</p>'
    + (knMayManage()
        ? '<div class="kn-bindrow">'
          + '<input id="kn-map-project" class="kn-input" type="text" placeholder="ANTHILL project id (blank = default)" aria-label="ANTHILL project id">'
          + '<input id="kn-map-base" class="kn-input" type="text" placeholder="FORAGER project ref" aria-label="FORAGER knowledge base">'
          + '<button class="kn-btn" data-onclick="knBindOther()">Bind</button>'
          + '</div>'
          + '<div class="kn-say" id="kn-say-map"></div>'
        : '<p class="kn-sub">Changing a binding needs <code>manage_knowledge</code>.</p>')
    + '</details>';
}

function knSayBind(msg, ok) {
  const el = document.getElementById('kn-say-bind');
  if (!el) return;
  el.textContent = msg || '';
  el.className = 'kn-say' + (msg ? (ok ? ' kn-ok' : ' kn-bad') : '');
}

/**
 * Bind, rebind or set the default.
 *
 * An empty project sets the DEFAULT rather than erroring, which is the route's own rule and is
 * stated in the placeholder — the two must agree or the field lies about what it does.
 */
async function knBind() {
  const base = (document.getElementById('kn-bind-base')?.value || '').trim();
  if (!base) { knSayBind('Enter the FORAGER project ref to bind to.', false); return; }
  await knWriteBinding(knProject, base, knSayBind);
}

/** The same write, from the folded table, for a project that is not the one selected above. */
async function knBindOther() {
  const say = (msg, ok) => {
    const el = document.getElementById('kn-say-map');
    if (el) { el.textContent = msg || ''; el.className = 'kn-say' + (msg ? (ok ? ' kn-ok' : ' kn-bad') : ''); }
  };
  const base = (document.getElementById('kn-map-base')?.value || '').trim();
  if (!base) { say('Enter the FORAGER project ref to bind to.', false); return; }
  await knWriteBinding((document.getElementById('kn-map-project')?.value || '').trim(), base, say);
}

/**
 * ONE WRITER FOR TWO CONTROLS. The row at the top and the table below it bind the same thing; two
 * copies of this call is how one of them ends up sending a field the other stopped sending.
 *
 * An empty project sets the DEFAULT rather than erroring, which is the route's own rule and is
 * stated in the placeholder — the two must agree or the field lies about what it does.
 */
async function knWriteBinding(project, base, say) {
  say('Binding…', true);
  try {
    const r = await api('/knowledge/project-map', 'POST', { project: project, knowledge_base: base });
    if (!r || !r.success) { say((r && (r.error || r.message)) || 'The binding could not be written.', false); return; }
    await loadKnowledge();
    say(r.message || 'Bound.', true);
  } catch (e) {
    say((e && e.message) || 'The binding could not be written.', false);
  }
}

/**
 * Run the colony over a bound knowledge base. v0.3.8.154.
 *
 * One click is one PASS, not a subscription: it seeds what is new and says how much is left. That is
 * deliberate — a button that silently enrolled a knowledge base into continuous work would be an
 * automation decision made by a click that did not look like one.
 */
async function knSeed(project) {
  knSayBind('Queueing missions…', true);
  try {
    const r = await api('/knowledge/seed', 'POST', { project: project });
    if (!r || !r.success) { knSayBind((r && (r.error || r.message)) || 'Nothing was queued.', false); return; }
    await loadKnowledge();
    knSayBind(r.message || 'Queued.', true);
  } catch (e) {
    knSayBind((e && e.message) || 'Nothing was queued.', false);
  }
}

/** Remove one binding. The project then refuses rather than falling back — that is the point. */
async function knUnbind(project) {
  knSayBind('Unbinding…', true);
  try {
    const r = await api('/knowledge/project-map', 'POST', { project: project, knowledge_base: '' });
    if (!r || !r.success) { knSayBind((r && (r.error || r.message)) || 'The binding could not be removed.', false); return; }
    await loadKnowledge();
    knSayBind(r.message || 'Unbound.', true);
  } catch (e) {
    knSayBind((e && e.message) || 'The binding could not be removed.', false);
  }
}

/* ── starting ingestion ───────────────────────────────────────────────────────
   v0.3.8.153 — `POST /knowledge/jobs` HAD NO CALLER ANYWHERE IN THE CONSOLE.

   The Processing card could list jobs, cancel them and retry them, and could not
   start one. Every ingestion had to be started by hand against the API. The
   route has existed since `.121` with a workspace fence in front of it; what was
   missing was a text box.

   PATHS ARE THE OPERATOR'S OWN, and the fence is the server's. `POST
   /knowledge/jobs` resolves every path through `WorkspacePathGuard` BEFORE
   anything is sent, and refuses an escape by throwing; FORAGER has its own
   allowed-roots fence on the far side. Neither is trusted to be the only one,
   and this field is trusted by neither.
   ───────────────────────────────────────────────────────────────────────────── */

/**
 * PICK FILES, OR A WHOLE FOLDER. v0.3.8.160.
 *
 * The panel used to be a textarea asking for paths inside the colony workspace, one per line —
 * a fence expressed as a chore. Material an operator wants in a knowledge base lives wherever they
 * keep it, and the workspace guard exists to stop the COLONY reaching arbitrary files, not to make
 * a person move their documents before they can hand them over.
 *
 * `webkitdirectory` is what makes "choose a folder" possible in a browser at all. It is
 * non-standard, universally implemented, and degrades to nothing worse than a second button that
 * does the same as the first — so the file picker beside it is not a fallback to apologise for.
 *
 * THE PATH FIELD SURVIVES, folded, and that is not indecision: a folder of ten thousand documents
 * already sitting on this machine should be named, not uploaded through a browser, and FORAGER
 * reads it directly. Two ways in, for two genuinely different cases.
 */
function knIngestForm() {
  if (!knMayManage()) {
    return '<p class="kn-sub">Starting an import needs <code>manage_knowledge</code>.</p>';
  }
  return '<div class="kn-ingest">'
    + '<p class="kn-lede">Choose documents from anywhere on this machine. FORAGER parses them; '
    + 'ANTHILL never reads them.</p>'
    + '<div class="kn-bindrow">'
    + '<input type="file" id="kn-files" class="kn-file" multiple data-onchange="knUpload(false)" '
    + 'aria-label="Choose files to import">'
    + '<input type="file" id="kn-folder" class="kn-file" webkitdirectory directory multiple '
    + 'data-onchange="knUpload(true)" aria-label="Choose a folder to import">'
    + '</div>'
    + '<div class="kn-bindrow">'
    + '<label class="kn-lbl"><input type="checkbox" id="kn-ingest-force"> Re-read files FORAGER has already seen</label>'
    + '</div>'
    + '<p class="kn-sub">Up to 400 files and 100 MB per import — FORAGER\'s own limit. Larger sets go '
    + 'in batches, or by path below.</p>'
    + '<details><summary class="kn-sub">Import by path instead (for material already on this machine)</summary>'
    + '<textarea id="kn-ingest-paths" class="kn-input" rows="2" '
    + 'placeholder="C:\\Users\\you\\Documents\\handbook" aria-label="Paths to import"></textarea>'
    + '<button class="kn-btn" data-onclick="knStartIngest()">Import by path</button>'
    + '<p class="kn-sub">Resolved against the colony workspace and refused if it leaves it. FORAGER '
    + 'has its own allowed-roots fence on the far side.</p>'
    + '</details>'
    + '<div class="kn-say" id="kn-say-ingest"></div></div>';
}

/**
 * Hand the chosen files to the colony, which hands them to FORAGER.
 *
 * `webkitRelativePath` is kept as the name when a folder was picked, so a document set arrives with
 * its shape rather than as a flat pile of basenames.
 */
async function knUpload(fromFolder) {
  const input = document.getElementById(fromFolder ? 'kn-folder' : 'kn-files');
  const chosen = Array.from((input && input.files) || []);
  if (!chosen.length) return;

  const body = new FormData();
  body.append('project', knProject);
  body.append('force', document.getElementById('kn-ingest-force')?.checked ? 'true' : 'false');
  for (const file of chosen) body.append('files', file, file.webkitRelativePath || file.name);

  knSayIngest(`Uploading ${chosen.length} file(s)…`, true);
  try {
    // FormData goes as multipart with a boundary the browser sets; `api()` must not stringify it or
    // name a content type of its own.
    const r = await api('/knowledge/upload', 'POST', body, 300000);
    if (input) input.value = '';
    if (!r || !r.success) { knSayIngest((r && (r.error || r.message)) || 'The import did not start.', false); return; }
    knSayIngest(r.message || 'Import started.', true);
    knLoadJobs();
  } catch (e) {
    knSayIngest((e && e.message) || 'The import did not start.', false);
  }
}

function knSayIngest(msg, ok) {
  const el = document.getElementById('kn-say-ingest');
  if (!el) return;
  el.textContent = msg || '';
  el.className = 'kn-say' + (msg ? (ok ? ' kn-ok' : ' kn-bad') : '');
}

async function knStartIngest() {
  const raw = (document.getElementById('kn-ingest-paths')?.value || '');
  const paths = raw.split('\n').map(p => p.trim()).filter(p => p.length > 0);
  if (!paths.length) { knSayIngest('Enter at least one folder or file to ingest.', false); return; }

  knSayIngest('Queueing…', true);
  try {
    const r = await api('/knowledge/jobs', 'POST', {
      project: knProject,
      paths: paths,
      force: !!document.getElementById('kn-ingest-force')?.checked,
    });
    if (!r || !r.success) { knSayIngest((r && (r.error || r.message)) || 'Ingestion was not started.', false); return; }
    knSayIngest(r.message || 'Ingestion queued.', true);
    knLoadJobs();
  } catch (e) {
    knSayIngest((e && e.message) || 'Ingestion was not started.', false);
  }
}

/* ── review proposals ─────────────────────────────────────────────────────────
   v0.3.8.155 — THE OTHER END OF A TOOL THAT HAD NEITHER END.

   `knowledge_review` shipped at `.121`: described, argued for, and named by no
   role's contract, raising a proposal that reached the event log and stopped.
   The researcher can now raise one and this is where it is answered.

   ACCEPTING IS NOT APPLYING, and the card says so where the button is rather
   than in a doc. §1 gives FORAGER the classification and the ranking, and there
   is no producer endpoint for applying a review — that is P13 in the contract.
   A button labelled "Accept" beside a claim that the knowledge base changed
   would be the most expensive kind of lie this console could tell.
   ───────────────────────────────────────────────────────────────────────────── */

async function knLoadReviews() {
  const host = document.getElementById('kn-reviews');
  if (!host) return;
  try {
    const r = await api('/knowledge/reviews');
    if (!r || !r.success) {
      host.innerHTML = `<div class="kn-empty">${escapeHtml((r && r.message) || 'Proposals are not readable.')}</div>`;
      return;
    }
    const reviews = (r.data && r.data.reviews) || [];
    if (!reviews.length) {
      host.innerHTML = '<div class="kn-empty">No review proposals. The colony has not disagreed with anything it read.</div>';
      return;
    }

    host.innerHTML = reviews.map(v => {
      const pending = v.status === 'pending';
      return '<div class="kn-review">'
        + `<div class="kn-jobhead"><span class="kn-pill kn-st-${escapeHtml(v.status || '')}">${escapeHtml(v.status || '')}</span> `
        + `<b>${escapeHtml(v.action || '')}</b> <code>${escapeHtml(v.knowledge_id || '')}</code></div>`
        + `<p class="kn-lede">${escapeHtml(v.rationale || '')}</p>`
        + (v.mission_id ? `<p class="kn-sub">Raised by ${escapeHtml(v.proposed_by || 'a worker')} in mission <code>${escapeHtml(v.mission_id)}</code></p>` : '')
        + (v.decided_by ? `<p class="kn-sub">Decided by ${escapeHtml(v.decided_by)}${v.decision_note ? ': ' + escapeHtml(v.decision_note) : ''}</p>` : '')
        + (pending && knMayManage()
            ? `<button class="kn-btn kn-sm kn-primary" data-onclick="knDecideReview('${jsArg(v.id)}', true)">Accept</button>`
              + `<button class="kn-btn kn-sm" data-onclick="knDecideReview('${jsArg(v.id)}', false)">Decline</button>`
            : '')
        + '</div>';
    }).join('') + '<div class="kn-say" id="kn-say-review"></div>';
  } catch (e) {
    host.innerHTML = '<div class="kn-empty">Proposals are not readable.</div>';
  }
}

async function knDecideReview(id, accept) {
  const say = document.getElementById('kn-say-review');
  const tell = (msg, ok) => { if (say) { say.textContent = msg; say.className = 'kn-say' + (ok ? ' kn-ok' : ' kn-bad'); } };
  tell('Recording…', true);
  try {
    const r = await api('/knowledge/reviews/' + encodeURIComponent(id) + '/decide', 'POST', { accept: !!accept });
    if (!r || !r.success) { tell((r && (r.error || r.message)) || 'The decision was not recorded.', false); return; }
    await knLoadReviews();
    tell(r.message || 'Recorded.', true);
  } catch (e) {
    tell((e && e.message) || 'The decision was not recorded.', false);
  }
}

PAGE_ENTER['knowledge'] = () => loadKnowledge();
