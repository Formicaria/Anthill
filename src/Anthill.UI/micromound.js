/* ─────────────────────────────────────────────────────────────────────────────
   MICROMOUND — the operator console. v0.3.8.115.

   `.60` shipped the uplink. `.114` shipped the command path and said plainly that
   its console had not been built: six routes were recorded in
   `ConsoleRouteCoverageTests.NoConsoleSurface` as "UI GAP", reachable over the
   API and rendered by nothing. This file is those gaps closed.

   ── EVERY FIELD HERE WAS READ OFF THE CONTRACT, NOT THE SPEC ────────────────
   `.114` named a new defect class for the opposite: a wire shape invented from
   PROTOCOL.md rather than read from the client, which made enrolment impossible
   through the front door while every test — both ends of it ours — passed. So
   the forms below were built from the declaring types:

     · `MissionStep`, `StepCondition`, `CapabilityLimits`, `WorkerDefinition`,
       `HardwareBinding` and the four closed vocabularies (step ops, condition
       ops, runtime types, offline behaviours) come from `Micromound.Protocol` —
       the shared assembly the module references, not a copy.
     · `MoundCreateRequest`, `CharterBody`, `ConfigBody`, `MissionBody` and
       `MoundStopRequest` come from `ApiHost.Micromound.cs`, which is what
       actually deserializes them.

   Where a field is optional it is sent only when the operator filled it in, so
   an empty form posts an empty object rather than a wall of nulls the server
   then has to treat as intent.

   ── WHAT THIS FILE WILL NOT DO ──────────────────────────────────────────────
   It computes no status. `MicromoundWidgets.StatusOf` reads the beat interval
   and the configured missed-beat grace; `.115` widened that method and the fleet
   listing now carries its verdict, so this renders what the colony decided.

   It never says a charter, manifest or mission was DELIVERED. The colony never
   dials a mound (PROTOCOL.md §1) — a device behind NAT in a shed dials in — so
   everything issued here lands in a downlink queue and the honest word is
   "awaiting collection", which is the field the API returns and the word shown.

   CSP: no inline handlers. Buttons use the console's `data-onclick` dispatcher,
   which resolves through `window`, so every handler is a global here.
   ───────────────────────────────────────────────────────────────────────────── */

/** Operator-facing name for the wire tier. The identifier is always shown too. */
const MM_TIER = { edge_queen: 'Mound Major', deterministic_controller: 'Deterministic Controller' };

/** Closed vocabularies, from Micromound.Protocol. Not free text, and not ours to extend. */
const MM_STEP_OPS = ['sense', 'act', 'routine', 'verify', 'report'];
const MM_COND_OPS = ['lt', 'lte', 'gt', 'gte', 'eq', 'neq'];
const MM_RUNTIME_TYPES = ['deterministic', 'algorithmic', 'sensor', 'actuator', 'reasoning'];
const MM_OFFLINE = ['continue', 'drain', 'suspend'];
const MM_REASONING = ['none', 'remote', 'local'];
/* `hazardous` is a real ActionClass and is deliberately absent: MicromoundCharters
   refuses a charter that asks for it, so offering it here would be a control whose
   only outcome is a refusal. The ceiling an operator can grant stops at controlled. */
const MM_CEILINGS = ['observe', 'benign', 'controlled'];
const MM_ORIGINS = ['user', 'queen', 'workflow', 'automation', 'system'];

let mmFleet = null;          // the last fleet payload, verbatim
let mmSelected = '';         // mound_id the forms act on
let mmMintedToken = null;    // shown once, never re-fetchable

/* ── helpers ──────────────────────────────────────────────────────────────── */

/** Comma/newline separated input → a trimmed list. Empty in, empty list out. */
function mmList(id) {
  const v = (document.getElementById(id)?.value || '').trim();
  if (!v) return [];
  return v.split(/[\n,]+/).map(s => s.trim()).filter(Boolean);
}
function mmVal(id) { return (document.getElementById(id)?.value || '').trim(); }
function mmNum(id) {
  const v = mmVal(id);
  if (v === '') return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : null;
}
function mmChecked(id) { return !!document.getElementById(id)?.checked; }

/** `k=v` per line → an object. The settings and parameters maps both use it. */
function mmPairs(id, numeric) {
  const out = {};
  (document.getElementById(id)?.value || '').split('\n').forEach(line => {
    const at = line.indexOf('=');
    if (at < 1) return;
    const k = line.slice(0, at).trim();
    const raw = line.slice(at + 1).trim();
    if (!k) return;
    if (numeric) { const n = Number(raw); if (Number.isFinite(n)) out[k] = n; }
    else out[k] = raw;
  });
  return out;
}

function mmSay(msg, ok) {
  const el = document.getElementById('mm-say');
  if (!el) return;
  el.textContent = msg || '';
  el.className = 'mm-say' + (msg ? (ok ? ' mm-ok' : ' mm-bad') : '');
}

/** Every mutation lands here, so failure is reported the same way everywhere. */
async function mmPost(path, body, describe) {
  mmSay('…', true);
  try {
    const r = await api(path, 'POST', body);
    if (r && r.success === false) { mmSay(describe + ' refused: ' + (r.error || r.message || 'no reason given'), false); return null; }
    mmSay(describe + ' accepted.', true);
    return (r && r.data) || r;
  } catch (e) {
    mmSay(describe + ' failed: ' + (e && e.message ? e.message : 'request error'), false);
    return null;
  }
}

/* ── The fleet ────────────────────────────────────────────────────────────── */

async function loadMicromound() {
  const host = document.getElementById('mm-fleet');
  if (!host) return;
  try {
    const r = await api('/micromound/mounds');
    mmFleet = (r && r.data) || r;
  } catch (e) {
    // A colony built without the module does not map this route. That is an
    // ordinary configuration, not a fault, and it is said as such.
    host.innerHTML = '<div class="mm-empty">This colony reports no Micromound fleet. '
      + 'Either the module is not built in, or the fleet listing is not readable with '
      + 'your permissions.</div>';
    return;
  }
  mmRenderFleet();
}

function mmRenderFleet() {
  const host = document.getElementById('mm-fleet');
  if (!host || !mmFleet) return;

  const items = mmFleet.items || [];
  const status = mmFleet.status || {};
  const pending = mmFleet.pending_downlink || {};

  const banner = mmFleet.global_stop
    ? `<div class="mm-stop">GLOBAL STOP ENGAGED — no mound acts. Cleared only by removing
         <code>${escapeHtml(mmFleet.stop_file || '')}</code> on disk; deliberately out of this API's reach.</div>`
    : '';

  if (!items.length) {
    host.innerHTML = banner + '<div class="mm-empty">No devices are enrolled. Mint an enrollment '
      + 'token below to adopt one.</div>';
    return;
  }

  host.innerHTML = banner + `<table class="mm-table">
    <thead><tr>
      <th>Device</th><th>Class</th><th>Status</th><th>Last beat</th>
      <th>Charter</th><th>Lease</th><th>Queued</th><th>Actions</th>
    </tr></thead>
    <tbody>${items.map(m => {
    const id = m.mound_id || '';
    const st = status[id] || 'unknown';
    return `<tr class="${id === mmSelected ? 'mm-sel' : ''}">
        <td>
          <button class="mm-link" data-onclick="mmSelect('${escapeHtml(id)}')">${escapeHtml(m.name || id)}</button>
          <div class="mm-sub">${escapeHtml(id)}</div>
        </td>
        <td>${escapeHtml(MM_TIER[m.tier] || m.tier || '—')}<div class="mm-sub">${escapeHtml(m.tier || '')}</div></td>
        <td><span class="mm-st mm-st-${escapeHtml(st)}">${escapeHtml(st)}</span></td>
        <td>${escapeHtml(m.last_seen || 'never')}</td>
        <td>${m.charter_id ? escapeHtml(m.charter_id.slice(0, 12)) + '…' : '<span class="mm-sub">none — observe only</span>'}</td>
        <td>${escapeHtml(m.lease_expires_at || '—')}</td>
        <td>${Number(pending[id] || 0)}</td>
        <td class="mm-acts">
          <button class="mm-btn" data-onclick="mmStop('${escapeHtml(id)}',${m.stopped ? 'false' : 'true'})">${m.stopped ? 'Resume' : 'Stop'}</button>
          <button class="mm-btn" data-onclick="mmEvidence('${escapeHtml(id)}')">Evidence</button>
          <button class="mm-btn mm-danger" data-onclick="mmUnlink('${escapeHtml(id)}')">Unlink</button>
        </td>
      </tr>`;
  }).join('')}</tbody></table>
  <div class="mm-note">Status is the colony's verdict, computed from the beat interval and the
    configured missed-beat grace. "Queued" is downlink <em>awaiting collection</em> — the colony
    never dials a mound, so nothing here has been delivered until the device beats.</div>`;
}

function mmSelect(id) {
  mmSelected = id;
  document.querySelectorAll('[data-mm-target]').forEach(el => { el.textContent = id || '(none)'; });
  mmRenderFleet();
  mmSetupLoad();
  mmSay('Forms now act on ' + id + '.', true);
}

/* ── Adoption and retirement ──────────────────────────────────────────────── */

async function mmMint() {
  const moundId = mmVal('mm-new-id');
  if (!moundId) { mmSay('A mound id is required.', false); return; }
  const d = await mmPost('/micromound/mounds', {
    mound_id: moundId, name: mmVal('mm-new-name'), tier: mmVal('mm-new-tier')
  }, 'Enrollment token');
  if (!d) return;
  // Shown once. The store holds a hash; there is no re-issue and no self-service
  // re-key, so this is said before the operator navigates away from it.
  mmMintedToken = d;
  const box = document.getElementById('mm-token');
  box.className = 'mm-token';
  box.innerHTML = `<div class="mm-token-h">Enrollment token for <code>${escapeHtml(d.mound_id || moundId)}</code></div>
    <code class="mm-token-v">${escapeHtml(d.token || '')}</code>
    <div class="mm-note">Shown ONCE. The colony stores only a hash — there is no re-issue and no
      self-service re-key. Copy it to the device now. Expires ${escapeHtml(d.expires_at || 'unknown')}.</div>`;
  loadMicromound();
}

async function mmUnlink(id) {
  if (!await uiConfirm(`Unlink Micromound '${id}'?\n\nThis removes its charters, queued downlink, `
    + `evidence, action records and token. The device is NOT told — its next beat is refused as an `
    + `unknown mound, and re-adopting it needs a freshly minted token.`)) return;
  const d = await mmPost('/micromound/unlink', { mound_id: id }, 'Unlink');
  if (d && d.note) mmSay('Unlinked. ' + d.note, true);
  if (id === mmSelected) mmSelect('');
  loadMicromound();
}

async function mmStop(id, stopped) {
  const path = stopped ? '/micromound/stop' : '/micromound/stop/resume';
  const d = await mmPost(path, { mound_id: id }, stopped ? 'Stop' : 'Resume');
  if (d) mmSay(`'${id}' is now ${d.stopped ? 'STOPPED — its next sync carries the stop order' : 'running'}.`, true);
  loadMicromound();
}

/* ── Charters — authority, and the only form gated on Approve ─────────────── */

async function mmIssueCharter() {
  if (!mmSelected) { mmSay('Select a device first.', false); return; }

  const limits = {};
  (document.getElementById('mm-ch-limits')?.value || '').split('\n').forEach(line => {
    // `capability key=value key=value` — one capability per line. Keys are the
    // five CapabilityLimits fields and nothing else is sent.
    const parts = line.trim().split(/\s+/).filter(Boolean);
    if (parts.length < 2) return;
    const cap = parts.shift(), obj = {};
    parts.forEach(p => {
      const at = p.indexOf('=');
      if (at < 1) return;
      const k = p.slice(0, at), n = Number(p.slice(at + 1));
      if (['max_on_s', 'min_off_s', 'min', 'max', 'max_rate_per_h'].includes(k) && Number.isFinite(n)) obj[k] = n;
    });
    if (Object.keys(obj).length) limits[cap] = obj;
  });

  const body = {
    mound_id: mmSelected,
    capabilities: mmList('mm-ch-caps'),
    routines: mmList('mm-ch-routines'),
    action_ceiling: mmVal('mm-ch-ceiling') || 'observe',
    duration_s: mmNum('mm-ch-duration'),
    lease_ttl_s: mmNum('mm-ch-lease'),
    mission_ref: mmVal('mm-ch-ref'),
    safe_state: mmVal('mm-ch-safe'),
    evidence_required_for: mmList('mm-ch-evidence'),
    evidence_min_interval_s: mmNum('mm-ch-evint')
  };
  if (Object.keys(limits).length) body.limits = limits;

  const d = await mmPost('/micromound/charters', body, 'Charter');
  if (d) mmSay(`Charter ${String(d.charter_id || '').slice(0, 12)}… issued at ceiling `
    + `'${d.action_ceiling}', expires ${d.expires_at}. ${d.awaiting_collection} item(s) awaiting `
    + `collection — the mound picks them up on its next beat.`, true);
  loadMicromound();
}

/* ── Manifests — the hardware map, which grants nothing ───────────────────── */

function mmWorkerRows() {
  // One worker per line: name | purpose | runtime_type | consumes | exposes | ceiling | offline | requires_reasoning
  const out = [];
  (document.getElementById('mm-cf-workers')?.value || '').split('\n').forEach(line => {
    const f = line.split('|').map(s => s.trim());
    if (!f[0]) return;
    const w = { name: f[0], purpose: f[1] || '' };
    if (MM_RUNTIME_TYPES.includes(f[2])) w.runtime_type = f[2];
    if (f[3]) w.consumes = f[3].split(/[,\s]+/).filter(Boolean);
    if (f[4]) w.exposes = f[4].split(/[,\s]+/).filter(Boolean);
    if (MM_CEILINGS.includes(f[5])) w.action_ceiling = f[5];
    if (MM_OFFLINE.includes(f[6])) w.offline_behaviour = f[6];
    if (f[7]) w.requires_reasoning = /^(true|yes|1)$/i.test(f[7]);
    out.push(w);
  });
  return out;
}

async function mmIssueConfig() {
  if (!mmSelected) { mmSay('Select a device first.', false); return; }

  // One binding per line: `device driver k=v k=v`
  const hardware = [];
  (document.getElementById('mm-cf-hw')?.value || '').split('\n').forEach(line => {
    const parts = line.trim().split(/\s+/).filter(Boolean);
    if (parts.length < 2) return;
    const device = parts.shift(), driver = parts.shift(), settings = {};
    parts.forEach(p => { const at = p.indexOf('='); if (at > 0) settings[p.slice(0, at)] = p.slice(at + 1); });
    hardware.push({ device: device, driver: driver, settings: settings });
  });

  const body = {
    mound_id: mmSelected,
    hardware: hardware,
    capabilities: mmList('mm-cf-caps'),
    routines: mmList('mm-cf-routines'),
    reasoning_mode: mmVal('mm-cf-reasoning') || 'none',
    safe_state: mmVal('mm-cf-safe')
  };
  const workers = mmWorkerRows();
  if (workers.length) body.workers = workers;

  const d = await mmPost('/micromound/config', body, 'Manifest');
  if (d) mmSay(`Manifest ${String(d.manifest_id || '').slice(0, 12)}… authored with ${d.devices} device(s), `
    + `${d.awaiting_collection} awaiting collection. NOT in force: the mound validates it against its `
    + `own drivers and may still refuse — that refusal arrives as an ack, never inferred from silence.`, true);
  loadMicromound();
}

/* ── Missions — the one place an approval is owed ─────────────────────────── */

function mmSteps() {
  /* One step per line:
       step_id | op | capability | params | evidence_tag | confirms | condition
     params:    `k=v k=v`  (numeric — MissionStep.parameters is a double map)
     condition: `source_step op value`
     `routine_id` is taken from the capability column when op is `routine`, which
     is the field the protocol actually reads for that op. */
  const out = [];
  (document.getElementById('mm-ms-steps')?.value || '').split('\n').forEach(line => {
    const f = line.split('|').map(s => s.trim());
    if (!f[0] || !MM_STEP_OPS.includes(f[1])) return;
    const step = { step_id: f[0], op: f[1] };
    if (f[1] === 'routine') step.routine_id = f[2] || '';
    else step.capability = f[2] || '';
    if (f[3]) {
      const p = {};
      f[3].split(/\s+/).forEach(kv => {
        const at = kv.indexOf('=');
        if (at < 1) return;
        const n = Number(kv.slice(at + 1));
        if (Number.isFinite(n)) p[kv.slice(0, at)] = n;
      });
      if (Object.keys(p).length) step.parameters = p;
    }
    if (f[4]) step.evidence_tag = f[4];
    if (f[5]) step.confirms = f[5];
    if (f[6]) {
      const c = f[6].split(/\s+/).filter(Boolean);
      if (c.length === 3 && MM_COND_OPS.includes(c[1]) && Number.isFinite(Number(c[2])))
        step.condition = { source_step: c[0], op: c[1], value: Number(c[2]) };
    }
    out.push(step);
  });
  return out;
}

async function mmDispatch() {
  if (!mmSelected) { mmSay('Select a device first.', false); return; }
  const steps = mmSteps();
  if (!steps.length) { mmSay('No valid steps. Each line needs at least `step_id | op` with a known op.', false); return; }

  const d = await mmPost('/micromound/missions', {
    mound_id: mmSelected,
    steps: steps,
    origin: mmVal('mm-ms-origin') || 'user',
    reason: mmVal('mm-ms-reason'),
    worker: mmVal('mm-ms-worker'),
    duration_s: mmNum('mm-ms-duration')
  }, 'Mission');
  if (!d) return;

  if (d.dispatched) {
    mmSay(`Mission ${String(d.mission_id || '').slice(0, 12)}… signed under charter `
      + `${String(d.charter_id || '').slice(0, 12)}…, ${d.awaiting_collection} awaiting collection.`, true);
    document.getElementById('mm-ms-id').value = d.mission_id || '';
  } else if (d.approval_required) {
    // ANTHILL's one approval queue — §19, no second framework. The operator answers
    // it where they answer everything else, and the ordinary dispatcher carries it out.
    mmSay(`Policy requires a person: ${d.reason}. Parked as approval `
      + `${String(d.approval_id || '').slice(0, 12)}… in the approvals queue — nothing was queued `
      + `for the device, because a mission parked in a downlink queue is authority nobody granted.`, true);
    if (typeof pollApprovals === 'function') pollApprovals();
  }
  loadMicromound();
}

async function mmMission() {
  const id = mmVal('mm-ms-id');
  if (!id) { mmSay('A mission id is required.', false); return; }
  const box = document.getElementById('mm-mission-out');
  try {
    const r = await api('/micromound/missions/' + encodeURIComponent(id));
    const d = (r && r.data) || r;
    if (!d || (r && r.success === false)) { box.textContent = (r && r.error) || 'No such mission.'; return; }
    box.innerHTML = `<div class="mm-kv"><span>Mound</span><span>${escapeHtml(d.mound_id || '')}</span></div>
      <div class="mm-kv"><span>Charter</span><span>${escapeHtml(d.charter_id || '')}</span></div>
      <div class="mm-kv"><span>Expires</span><span>${escapeHtml(d.expires_at || '')}</span></div>
      <div class="mm-kv"><span>Device says</span><span>${escapeHtml(d.device_state || 'nothing reported')}
        ${d.device_detail ? '— ' + escapeHtml(d.device_detail) : ''}</span></div>
      <div class="mm-kv"><span>Colony verified</span><span>${d.colony_verified ? 'yes' : 'no'}
        (${Number(d.verified_actions || 0)} of ${Number(d.actions || 0)} actions)</span></div>
      <div class="mm-kv"><span>Detail</span><span>${escapeHtml(d.detail || '')}</span></div>
      <div class="mm-note">Both verdicts are shown and never merged. The disagreement is how you tell
        missing proof from a valve that actually failed.</div>`;
  } catch (e) { box.textContent = 'Could not read that mission.'; }
}

/* ── The resolver — answers a question and issues nothing ─────────────────── */

async function mmResolve() {
  const cap = mmVal('mm-rs-cap');
  if (!cap) { mmSay('A capability or routine id is required.', false); return; }
  const box = document.getElementById('mm-resolve-out');
  try {
    const r = await api('/micromound/resolve?capability=' + encodeURIComponent(cap)
      + '&origin=' + encodeURIComponent(mmVal('mm-rs-origin') || 'user'));
    const d = (r && r.data) || r;
    const items = (d && d.items) || [];
    if (!items.length) { box.innerHTML = '<div class="mm-empty">No mound is registered at all.</div>'; return; }
    // EVERY mound, not only the eligible ones — "nothing can do this" and "one
    // could, but its lease lapsed" are different answers and a filter merges them.
    box.innerHTML = `<div class="mm-note">${Number(d.eligible || 0)} of ${items.length} eligible for
        <code>${escapeHtml(cap)}</code> at origin <code>${escapeHtml(d.origin || '')}</code>.</div>`
      + items.map(c => `<div class="mm-cand ${c.Eligible || c.eligible ? 'mm-ok' : ''}">
          <strong>${escapeHtml(c.Name || c.name || c.MoundId || c.moundId || '')}</strong>
          <span class="mm-sub">${escapeHtml(c.Status || c.status || '')}</span>
          <div class="mm-sub">${((c.Blockers || c.blockers || []).map(escapeHtml).join(' · ')) || 'no blockers'}</div>
        </div>`).join('');
  } catch (e) { box.textContent = 'The resolver did not answer.'; }
}

/* ── Evidence ─────────────────────────────────────────────────────────────── */

async function mmEvidence(moundId) {
  const box = document.getElementById('mm-evidence-out');
  const path = moundId ? '/micromound/evidence?mound_id=' + encodeURIComponent(moundId) : '/micromound/evidence';
  try {
    const r = await api(path);
    const d = (r && r.data) || r;
    if (moundId) {
      const items = (d && d.items) || [];
      box.innerHTML = items.length
        ? `<div class="mm-note">Recent beats from <code>${escapeHtml(moundId)}</code>.</div>`
        + '<pre class="mm-pre">' + escapeHtml(JSON.stringify(items, null, 2)) + '</pre>'
        : `<div class="mm-empty">No beats recorded for ${escapeHtml(moundId)}.</div>`;
      return;
    }
    box.innerHTML = `<div class="mm-note">Fleet evidence feed, updated ${escapeHtml((d && d.updated_at) || '—')}.</div>`
      + '<pre class="mm-pre">' + escapeHtml(JSON.stringify((d && d.feed) || {}, null, 2)) + '</pre>';
  } catch (e) { box.textContent = 'The evidence feed is not readable.'; }
}

/* THE COLONY CHAMBER PANEL — v0.3.8.122.

   Colony Live can hold many mound chambers, so "the micromound settings" stopped naming one thing.
   Colony › Live and Colony › Mounds both hand the id over in `window.micromoundPendingId`, this page
   reads it once and shows THAT chamber, and deleting here removes it from the live colony
   immediately — the operator does not delete something and then find it still drawn.

   WHAT A DELETE HERE DOES AND DOES NOT DO. It removes a chamber: a label in this operator's colony
   view, with its name, its colour and its ants' names. It does not retire a device, revoke a token
   or stop anything. An enrolled mound keeps answering under the identity its one-time token gave
   it, which is the whole reason the labelling layer was safe to add. The panel says so in words,
   because a Delete button next to "micromound" reads as the other thing. */
function mmChamber() {
  const box = document.getElementById('mm-chamber');
  if (!box) return;
  const id = (typeof window !== 'undefined' && window.micromoundPendingId) || null;
  const live = (window.ColonyHost && ColonyHost.live && ColonyHost.live()) || null;
  const chamber = (id && live && live.listMounds) ? live.listMounds().find(m => m.id === id) : null;
  if (!chamber) { box.style.display = 'none'; box.innerHTML = ''; return; }
  box.style.display = '';
  box.innerHTML =
    '<h3>Colony chamber · ' + escapeHtml(chamber.label) + '</h3>'
    + '<div class="mm-lede">This is how this mound appears in <strong>your</strong> colony view — its name, its '
    + 'colour and its ' + chamber.residents + ' ant names. None of it reaches a device: an enrolled mound keeps '
    + 'answering under the identity its enrollment token gave it, whatever you call it here.</div>'
    + '<button class="btn btn-sm" id="mm-chamber-del">Delete chamber</button>'
    + '<span class="mm-sub" style="margin-left:8px">Removes the chamber from the colony view only. No device is retired.</span>';
  const del = document.getElementById('mm-chamber-del');
  if (del) del.onclick = () => {
    if (live && live.removeMound && live.removeMound(chamber.id)) {
      try { window.micromoundPendingId = null; } catch (e) { }
      mmChamber();
      if (typeof toast === 'function') toast('Chamber removed from the colony view. No device was touched.');
    }
  };
}

/* ═══ THE SET-UP CARD — v0.3.8.123 ═══════════════════════════════════════════
   Everything below the Advanced fold on this page is the contract itself, and every field in it is
   real: a capability id, an action-class enum, a lease TTL in seconds, a `device_limits` map keyed
   by capability, evidence expressed as glob patterns. None of them is a question a person can
   answer, which is what the operator meant by "less of a json file communicated as settings".

   ── WHERE THE TRANSLATION LIVES, AND WHY IT IS NOT HERE ─────────────────────
   `MicromoundAuthoring` on the server compiles the answers below into exactly the `CharterRequest`
   and `ConfigurationRequest` the two existing services already take. This file renders the
   questions and posts them; it computes no ceiling, derives no limit and decides no policy. That
   division is the same one the fleet listing lives under — the console renders what the colony
   decided — and it exists so a second, browser-side idea of what a charter means cannot drift from
   the server's, which is how the sector map broke and had to be moved server-side at `.115`.

   ── ONLY WHAT THE DEVICE REPORTED IS OFFERED ────────────────────────────────
   The capability picker is populated from `reported`, which is `MoundRecord.Capabilities` — what
   the mound said at enrolment that it physically has. A form built here therefore cannot ask for
   something the charter issuer would refuse, so the operator finds out at the moment they would
   have made the mistake rather than after a round trip.

   ── THE PREVIEW IS THE POINT ────────────────────────────────────────────────
   Every edit posts to `/micromound/authoring/preview`, which compiles and issues NOTHING, and the
   answer comes back as refusals, warnings and the two documents the save would write. So the
   operator sees the JSON they used to have to author — as an outcome they can check, not as a
   thing they have to produce. Nothing is hidden; what changed is who writes it.

   ── A SAVE FROM HERE NEVER DELETES ADVANCED WORK ────────────────────────────
   A manifest and a charter are complete replacements, so saving a simple form writes the WHOLE
   document and anything the form does not know about would be gone. The projection hands back an
   `advanced` block holding exactly those parts — manifest-declared workers, a reasoning mode,
   grants with no device row — this file posts it straight back untouched, and `unrepresented` names
   every one of them on screen. Carrying something silently and losing it silently are one bug
   apart, so it is carried AND said. */

let mmSetup = null;        // the last form the server projected, as it will be posted back
let mmSetupMeta = null;    // catalog, roster, reported capabilities, vocabularies
let mmPreviewTimer = 0;

const MM_CONTROL_LABEL = {
  manual_only: 'Manual only — a person asks, every time',
  ask_first: 'Ask first — anything may propose, a person answers',
  within_limits: 'On its own, within the limits below',
};
const MM_LEVEL_LABEL = {
  watch_only: 'Watch only — it reads and reports, nothing moves',
  reversible: 'Small reversible things — a light, a fan',
  physical: 'Real physical action, inside the limits below',
};

async function mmSetupLoad() {
  const box = document.getElementById('mm-setup-body');
  if (!box) return;
  if (!mmSelected) { mmSetup = null; box.innerHTML = '<div class="mm-empty">Choose a mound above to set it up.</div>'; return; }
  box.innerHTML = '<div class="mm-empty">Reading this mound…</div>';
  try {
    const r = await api('/micromound/authoring/' + encodeURIComponent(mmSelected));
    const d = (r && r.data) || r;
    mmSetup = d.form || null;
    mmSetupMeta = d;
    mmSetupRender();
  } catch (e) {
    mmSetup = null;
    box.innerHTML = '<div class="mm-empty">This mound could not be read: ' + escapeHtml((e && e.message) || 'unknown error') + '</div>';
  }
}

/** A capability id as a sentence, from the server's catalog. Unknown ids still read. */
function mmCap(id) {
  const rows = (mmSetupMeta && mmSetupMeta.catalog) || [];
  return rows.find(c => c.id === id) || { id: id, label: id, kind: 'sensor', unit: '', verifiable: false };
}
function mmActs(id) { return mmCap(id).kind === 'actuator'; }

function mmSetupRender() {
  const box = document.getElementById('mm-setup-body');
  if (!box || !mmSetup) return;
  const meta = mmSetupMeta || {};
  const reported = meta.reported || [];
  const ants = meta.ants || [];
  const devices = mmSetup.devices || [];
  const used = devices.map(d => d.capability);
  const spare = reported.filter(c => used.indexOf(c) < 0);

  const opt = (list, chosen, label) => list.map(v =>
    '<option value="' + escapeHtml(v) + '"' + (v === chosen ? ' selected' : '') + '>'
    + escapeHtml(label ? label(v) : v) + '</option>').join('');

  let html = '';

  if (meta.enrolled === false)
    html += '<div class="mm-refuse">This mound has not enrolled yet, so nothing can be signed for it. '
      + 'Mint a token above and let the device present it first.</div>';
  if (meta.stopped)
    html += '<div class="mm-refuse">A stop is in force for this mound. Configuration and charters are '
      + 'both things a stop takes precedence over, so a save will be refused until it is cleared.</div>';

  html += '<div class="mm-grid">'
    + '<div class="mm-f mm-wide"><label for="mm-su-purpose">What is this mound for?</label>'
    + '<input id="mm-su-purpose" data-mm-form="purpose" placeholder="Greenhouse bench 2" value="' + escapeHtml(mmSetup.purpose || '') + '"></div>'
    + '<div class="mm-f"><label for="mm-su-control">Who decides when it acts?</label><select id="mm-su-control" data-mm-form="control_mode">'
    + opt(meta.control_modes || ['manual_only'], mmSetup.control_mode, v => MM_CONTROL_LABEL[v] || v) + '</select></div>'
    + '<div class="mm-f"><label for="mm-su-level">How far may it go?</label><select id="mm-su-level" data-mm-form="action_level">'
    + opt(meta.action_levels || ['watch_only'], mmSetup.action_level, v => MM_LEVEL_LABEL[v] || v) + '</select></div>'
    // The one question that replaced "what should it do offline?" — see MicromoundAuthoring's
    // fourth decision. This has a mechanism behind it; that one had nowhere to go.
    + '<div class="mm-f"><label for="mm-su-checkin">Must check in every (minutes)</label>'
    + '<input id="mm-su-checkin" data-mm-form="check_in_minutes" type="number" min="1" max="1440" value="' + escapeHtml(String(mmSetup.check_in_minutes || 15)) + '">'
    + '<span class="mm-sub">Out of contact for longer and it goes to its safe state on its own.</span></div>'
    + '<div class="mm-f"><label for="mm-su-days">Re-authorise after (days)</label>'
    + '<input id="mm-su-days" data-mm-form="authority_days" type="number" min="1" max="365" value="' + escapeHtml(String(mmSetup.authority_days || 7)) + '"></div>'
    + '<div class="mm-f"><label for="mm-su-proof">Proof no more often than (seconds)</label>'
    + '<input id="mm-su-proof" data-mm-form="proof_interval_s" type="number" min="1" value="' + escapeHtml(String(mmSetup.proof_interval_s || 60)) + '"></div>'
    + '<div class="mm-f"><label for="mm-su-safe">Safe state</label>'
    + '<input id="mm-su-safe" data-mm-form="safe_state" value="' + escapeHtml(mmSetup.safe_state || 'all_actuators_off') + '">'
    + '<span class="mm-sub">What it de-energises into. Every charter must be compatible with it.</span></div>'
    + '</div>';

  html += '<h4 style="margin:14px 0 4px">What it is wired to</h4>';
  if (!reported.length)
    html += '<div class="mm-empty">This mound has not reported any capabilities, so there is nothing to '
      + 'assign yet. A device reports what it physically has when it enrols.</div>';

  devices.forEach((d, i) => {
    const c = mmCap(d.capability);
    const unit = c.unit ? ' (' + c.unit + ')' : '';
    html += '<div class="mm-dev" data-mm-dev="' + i + '">'
      + '<div class="mm-dev-hd"><strong>' + escapeHtml(c.label) + '</strong>'
      + '<span class="mm-kind">' + escapeHtml(c.kind) + '</span>'
      + '<code class="mm-sub">' + escapeHtml(d.capability) + '</code>'
      + '<button class="btn btn-sm" data-mm-drop="' + i + '" style="margin-left:auto">Remove</button></div>'
      + '<div class="mm-grid">'
      + '<div class="mm-f"><label>What it does here</label><input data-mm-row="' + i + '" data-mm-field="purpose" placeholder="bench 2 soil probe" value="' + escapeHtml(d.purpose || '') + '"></div>'
      + '<div class="mm-f"><label>Which ant holds it</label><select data-mm-row="' + i + '" data-mm-field="assigned_ant">'
      + opt(ants, d.assigned_ant) + '</select></div>';

    // Only an actuator gets bounds, because only an actuator has anything for a bound to apply to —
    // the server refuses a run time on a thermometer, and offering one here would invite that.
    if (mmActs(d.capability)) {
      html += '<div class="mm-f"><label>Never below' + escapeHtml(unit) + '</label><input data-mm-row="' + i + '" data-mm-field="safe_min" type="number" step="any" value="' + escapeHtml(d.safe_min == null ? '' : String(d.safe_min)) + '"></div>'
        + '<div class="mm-f"><label>Never above' + escapeHtml(unit) + '</label><input data-mm-row="' + i + '" data-mm-field="safe_max" type="number" step="any" value="' + escapeHtml(d.safe_max == null ? '' : String(d.safe_max)) + '"></div>'
        + '<div class="mm-f"><label>Longest single run (s)</label><input data-mm-row="' + i + '" data-mm-field="max_run_s" type="number" step="any" value="' + escapeHtml(d.max_run_s == null ? '' : String(d.max_run_s)) + '"></div>'
        + '<div class="mm-f"><label>Rest between runs (s)</label><input data-mm-row="' + i + '" data-mm-field="min_rest_s" type="number" step="any" value="' + escapeHtml(d.min_rest_s == null ? '' : String(d.min_rest_s)) + '"></div>'
        + '<div class="mm-f"><label>At most, per hour</label><input data-mm-row="' + i + '" data-mm-field="max_per_hour" type="number" step="any" value="' + escapeHtml(d.max_per_hour == null ? '' : String(d.max_per_hour)) + '"></div>';
      if (c.verifiable) {
        const sensors = devices.filter(x => !mmActs(x.capability)).map(x => x.capability);
        html += '<div class="mm-f"><label>How do we confirm it acted?</label><select data-mm-row="' + i + '" data-mm-field="verified_by">'
          + '<option value="">Nothing checks it</option>'
          + sensors.map(sv => '<option value="' + escapeHtml(sv) + '"' + (sv === d.verified_by ? ' selected' : '') + '>' + escapeHtml(mmCap(sv).label) + '</option>').join('')
          + '</select></div>';
      }
    } else {
      html += '<div class="mm-f mm-wide"><span class="mm-sub">This one only reads, so it has no limits to set '
        + 'and nothing to confirm.</span></div>';
    }
    html += '</div></div>';
  });

  if (spare.length)
    html += '<div class="mm-f" style="margin-top:8px"><label for="mm-su-add">Add something the device reported</label>'
      + '<select id="mm-su-add"><option value="">Choose…</option>'
      + spare.map(sv => '<option value="' + escapeHtml(sv) + '">' + escapeHtml(mmCap(sv).label) + ' — ' + escapeHtml(sv) + '</option>').join('')
      + '</select></div>';
  else if (reported.length)
    html += '<div class="mm-sub" style="margin-top:8px">Everything this mound reported is assigned.</div>';

  (meta.unrepresented || []).forEach(u => {
    html += '<div class="mm-carry">Kept as it is, and not editable here: ' + escapeHtml(u)
      + '. Saving from this card does not remove it.</div>';
  });

  html += '<div id="mm-su-preview"></div>'
    + '<button class="mm-btn" id="mm-su-save">Save and issue</button>'
    + '<span class="mm-sub" style="margin-left:8px">Writes the hardware map first, then the authority. '
    + 'Both wait in the downlink queue until the device beats — the colony never dials a mound.</span>';

  box.innerHTML = html;
  mmSetupBind();
  mmSetupPreview();
}

/** Every control on the card, wired without an inline handler. CSP has no exceptions here. */
function mmSetupBind() {
  const box = document.getElementById('mm-setup-body');
  if (!box) return;

  box.querySelectorAll('[data-mm-form]').forEach(el => {
    el.addEventListener('change', () => {
      const key = el.dataset.mmForm;
      mmSetup[key] = el.type === 'number' ? Number(el.value) : el.value;
      mmSetupPreview();
    });
  });

  box.querySelectorAll('[data-mm-row]').forEach(el => {
    el.addEventListener('change', () => {
      const row = mmSetup.devices[Number(el.dataset.mmRow)];
      if (!row) return;
      const field = el.dataset.mmField;
      // A blank number is ABSENT, not zero. Zero is a legitimate bound — "never above 0" means
      // something — so the two have to stay distinguishable all the way to the server.
      row[field] = el.type === 'number' ? (el.value.trim() === '' ? null : Number(el.value)) : el.value;
      mmSetupPreview();
    });
  });

  box.querySelectorAll('[data-mm-drop]').forEach(el => {
    el.addEventListener('click', () => {
      mmSetup.devices.splice(Number(el.dataset.mmDrop), 1);
      mmSetupRender();
    });
  });

  const add = document.getElementById('mm-su-add');
  if (add) add.addEventListener('change', () => {
    const id = add.value;
    if (!id) return;
    mmSetup.devices = mmSetup.devices || [];
    // `device` and `driver` are left blank on purpose: the server derives both, and the derived
    // device name is what lets the projection pair the binding back to its capability on reload.
    // `default_ant` likewise comes from the catalog rather than being decided here: which of the
    // seven holds a capability is ANTS.md's division, and this file does not get a copy of it.
    mmSetup.devices.push({ capability: id, device: '', driver: '', purpose: '',
      assigned_ant: mmCap(id).default_ant || (mmSetupMeta && mmSetupMeta.ants || [''])[0],
      safe_min: null, safe_max: null, max_run_s: null, min_rest_s: null, max_per_hour: null,
      verified_by: '', settings: null });
    mmSetupRender();
  });

  const save = document.getElementById('mm-su-save');
  if (save) save.addEventListener('click', mmSetupSave);
}

/** Compile without issuing, debounced, and show what the save would write. */
function mmSetupPreview() {
  if (mmPreviewTimer) clearTimeout(mmPreviewTimer);
  mmPreviewTimer = setTimeout(async () => {
    const out = document.getElementById('mm-su-preview');
    if (!out || !mmSetup) return;
    try {
      const r = await api('/micromound/authoring/preview', 'POST', mmSetup);
      mmSetupShowPlan((r && r.data) || r, out);
    } catch (e) {
      out.innerHTML = '<div class="mm-refuse">' + escapeHtml((e && e.message) || 'This could not be checked.') + '</div>';
    }
  }, 260);
}

function mmSetupShowPlan(plan, out) {
  if (!plan) { out.innerHTML = ''; return; }
  let html = '';
  (plan.refusals || []).forEach(x => { html += '<div class="mm-refuse">' + escapeHtml(x) + '</div>'; });
  (plan.warnings || []).forEach(x => { html += '<div class="mm-warn">' + escapeHtml(x) + '</div>'; });
  // The compiled documents, shown rather than hidden. The operator no longer has to WRITE this —
  // that is the whole change — but they are still entitled to see exactly what will be signed.
  if (plan.compiled)
    html += '<details style="margin-top:8px"><summary class="mm-sub">What this will actually issue</summary>'
      + '<pre class="mm-pre">' + escapeHtml(JSON.stringify(plan.compiled, null, 2)) + '</pre></details>';
  out.innerHTML = html;
}

async function mmSetupSave() {
  if (!mmSetup) return;
  const d = await mmPost('/micromound/authoring', mmSetup, 'Set-up');
  if (!d) return;
  const out = document.getElementById('mm-su-preview');
  if (out) mmSetupShowPlan(d, out);
  mmSay('Saved. Manifest ' + (d.manifest_id || '—').slice(0, 8) + ' and charter '
    + (d.charter_id || '—').slice(0, 8) + ' are awaiting collection ('
    + (d.awaiting_collection || 0) + ' queued). The mound validates both against its own drivers '
    + 'and may still refuse — that answer arrives as an ack, never inferred.', true);
  loadMicromound();
  mmSetupLoad();
}

PAGE_ENTER['micromound'] = () => { loadMicromound(); mmChamber(); mmSetupLoad(); };
