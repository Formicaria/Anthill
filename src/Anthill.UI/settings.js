/* SETTINGS RAIL — the Settings domain. v0.3.8.145.
 *
 * One page with its own left rail (same topology as the Chat rail), a searchable list of
 * destinations, per-setting help, changed-from-default markers, one sticky save bar, and a
 * separate Danger zone. Built to the operator's design handoff ("Settings rail"); every hex value
 * in the prototype is a `var(--…)` token here so all five palettes keep working.
 *
 * WHAT IS AND IS NOT A SETTING. Three scopes, and a row says which it is:
 *   · server  — a key `/settings` accepts. Saved in one partial POST of only the DIRTY keys, the
 *               way `selectOllamaModel` always saved one key: the endpoint merges, it does not
 *               replace, so a stale snapshot cannot clobber a key the page never showed.
 *   · route   — a role's model, saved through `POST /routes/{role}`, which is the one path that
 *               keeps the live routing table and the persisted config in step (v0.3.8.96).
 *   · device  — this browser only: palette, reduce motion, API base URL, fallback poll interval.
 *               Noted on the section head ("saved in this browser only") because a device setting
 *               that looked colony-wide would be the mis-scoped edit the console has a name for.
 *
 * WHAT IT REFUSES TO PRETEND. The design's Security page shows "capability gates" as a second
 * set of switches for the same facts the Colony page edits. This codebase has ONE key per
 * capability, and one editor per fact is a rule here (ConsoleOneLevelTests), so Security carries
 * only the keys Colony does not: the operator shell and the auto-apply lane. The Danger zone's
 * unlock word is the colony's name and the SERVER checks it (`confirmation_mismatch`); the page
 * disabling a button is not the gate. "Compact & prune" is `/maintenance/flush`, which is what
 * that endpoint has always done — prune backups and old events, then VACUUM.
 *
 * Classic script, deferred, loaded after app.js. Nothing binds at parse time — every listener is
 * delegated from the page root at call time, so a missing element is a no-op rather than a load
 * failure that takes the console with it. No untrusted value ever reaches a `data-on*` attribute:
 * rows are addressed by `data-sr` keys the schema declares, never by anything a server returned.
 */

const SR = {
  page: 'account', query: '', saved: {}, values: {}, defaults: {}, ranges: {},
  open: {}, confirms: {}, loaded: false, loading: null, extra: {},
  toastTimer: null, navPrior: null, rawOpen: false, inviteOpen: false, busy: false,
};

/* ---- device-scoped settings ------------------------------------------------------------------ */
const SR_DEVICE_KEYS = {
  'device:theme':        { get: () => { try { return localStorage.getItem('anthill-theme') || 'default'; } catch (_) { return 'default'; } },
                           set: v => { if (typeof applyTheme === 'function') applyTheme(v); try { localStorage.setItem('anthill-theme', v); } catch (_) {} }, def: 'default' },
  'device:reducemotion': { get: () => { try { return localStorage.getItem('anthill-reduce-motion') === '1'; } catch (_) { return false; } },
                           set: v => { try { localStorage.setItem('anthill-reduce-motion', v ? '1' : '0'); } catch (_) {} srApplyReduceMotion(); }, def: false },
  'device:apibase':      { get: () => { try { return localStorage.getItem('anthill_apibase') || ''; } catch (_) { return ''; } },
                           set: v => { if (typeof setApiBase === 'function') setApiBase((v || '').trim()); }, def: '' },
  'device:pollidle':     { get: () => { try { return Number(localStorage.getItem('anthill-poll-idle-s') || 0) || 0; } catch (_) { return 0; } },
                           set: v => { try { const n = Number(v) || 0; if (n > 0) localStorage.setItem('anthill-poll-idle-s', String(n)); else localStorage.removeItem('anthill-poll-idle-s'); } catch (_) {} }, def: 0 },
};

function srApplyReduceMotion() {
  let on = false; try { on = localStorage.getItem('anthill-reduce-motion') === '1'; } catch (_) {}
  document.documentElement.toggleAttribute('data-reduce-motion', on);
}

/* ---- the pages -------------------------------------------------------------------------------- */
const SR_THEMES = [
  ['default', 'Formicaria — cream on ink, magenta action'], ['classic', 'Classic — blue-grey slate'],
  ['light', 'Light — paper & ink'], ['hermes', 'Hermes — warm amber on umber'], ['contrast', 'High contrast — black & bright'],
];

const SR_ROLE_HELP = {
  queen: 'Plans the mission and delegates tasks.', researcher: 'Searches and reads sources.', research: 'Searches and reads sources.',
  coder: 'Writes and edits code. A code-tuned model pays off here.', verifier: 'Reviews results before they reach you.',
  tester: 'Runs and interprets tests.', soldier: 'Adversarial review of results.', medic: 'Diagnoses failures and proposes recovery.',
  archivist: 'Files what the colony learned.', scribe: 'Writes the record.', planner: 'Shapes the plan.',
};

const srT = (key, label, help, x) => Object.assign({ type: 'toggle', key, label, help }, x || {});
const srN = (key, label, unit, help, x) => Object.assign({ type: 'number', key, label, unit, help }, x || {});
const srX = (key, label, help, x) => Object.assign({ type: 'text', key, label, help }, x || {});
const srS = (key, label, options, help, x) => Object.assign({ type: 'select', key, label, options, help }, x || {});
const srI = (label, value, help, x) => Object.assign({ type: 'info', label, value, help }, x || {});
const srA = (label, action, act, help, x) => Object.assign({ type: 'action', label, action, act, help }, x || {});
const srD = (id, label, action, help) => ({ type: 'danger', id, label, action, help });

function srDot(ok) { return ok === true ? 'var(--green)' : ok === false ? 'var(--red)' : 'var(--amber)'; }
function srMs(n) { return n == null ? '' : ' · ' + Math.round(n) + ' ms'; }

const SR_PAGES = [
  { id: 'account', group: 'Console', title: 'Account', sub: 'Who you are on this console and how it looks on this device.', vis: 'all',
    build() {
      const admin = (typeof ROLE !== 'undefined' && ROLE === 'admin');
      return [
        { title: 'Operator', rows: [
          srI('Signed in as', (typeof USERNAME !== 'undefined' && USERNAME) || '—', 'Your login name. Accounts are managed under Users.'),
          srI('Role', (typeof ROLE !== 'undefined' && ROLE) || '—', 'Admins see every settings page; other roles see Account only.'),
          admin ? srA('Password', 'Change…', 'password', 'Sign-in password for this account. Changing it signs you out everywhere.')
                : srI('Password', 'managed by an admin', 'Ask an administrator to reset it under Users.'),
        ] },
        { title: 'This device', note: 'saved in this browser only', rows: [
          srS('device:theme', 'Console palette', SR_THEMES, 'Applies instantly, before the page paints.'),
          srT('device:reducemotion', 'Reduce motion', 'Stops the colony animation and pulsing status dots.'),
        ] },
      ];
    } },
  { id: 'connection', group: 'Console', title: 'Connection', sub: 'How this console reaches the ANTHILL server and how the server is exposed on the network.',
    build(x) {
      const st = x.conn || {};
      const s = SR.saved;
      // THE EFFECTIVE BIND, not the file's. `ANTHILL_HOST`/`ANTHILL_PORT` override config.json, so
      // /status is the only place that knows what the process actually listened on; the config
      // value is what it would use next boot without the environment.
      const host = st.api_host || s.api_host, port = st.api_port || s.api_port;
      const bind = (host || '—') + (port ? ':' + port : '');
      const dep = (st.deployment || '').toLowerCase();
      const streamOn = (typeof _evtCtl !== 'undefined') && !!_evtCtl;
      return [
        { title: 'API server', rows: [
          srX('device:apibase', 'API base URL', 'Leave blank when the console is served by ANTHILL itself. Set it when this page is hosted elsewhere.', { placeholder: 'http://host:8713' }),
          srI('Status', st.ok === true ? 'connected' + srMs(st.ms) : st.ok === false ? 'unreachable' : 'checking…', 'Round trip to /status.', { dot: srDot(st.ok) }),
          srI('Running as', (dep === 'server' ? 'server' : dep === 'desktop' ? 'desktop' : '—') + ' · ' + bind
            + (st.reachable_ip && host === '0.0.0.0' ? ' · reachable at ' + st.reachable_ip : ''),
            'Desktop app or server, and the address it actually bound to. ANTHILL_HOST / ANTHILL_PORT override the configured value.'),
        ] },
        { title: 'Event stream', rows: [
          srI('Live stream', streamOn ? 'connected' : 'reconnecting — polling', 'Server-sent events that drive every live panel. Polling takes over if it drops.', { dot: srDot(streamOn ? true : null) }),
          srN('device:pollidle', 'Fallback poll interval', 's', 'How often panels poll when the stream is down. Blank keeps each panel\'s own default (20–30 s). Applies after reload.', { range: [5, 120] }),
        ] },
      ];
    } },
  { id: 'models', group: 'Colony', title: 'Models', sub: 'The local Ollama models the colony can use, and which role uses which. Cloud providers are connected under Tools → Integrations.', actions: [['refresh', '↺ Refresh']],
    build(x) {
      const oll = x.ollama || {};
      const models = oll.models || [];
      const modelOpts = models.map(m => [m.name, m.name + (m.size ? ' · ' + (m.size / 1e9).toFixed(1) + ' GB' : '')]);
      if (SR.values.ollama_model && !modelOpts.some(o => o[0] === SR.values.ollama_model)) modelOpts.unshift([SR.values.ollama_model, SR.values.ollama_model + ' · not installed']);
      const rj = x.routes || {};
      const avail = (rj.available_models || []).map(m => [m.provider + '|' + m.model, m.label || (m.model + ' · ' + m.provider)]);
      const routeRows = (rj.roles || []).map(r => {
        const k = 'route:' + r.role; const cur = (r.provider || '') + '|' + (r.model || '');
        const opts = avail.slice(); if (cur !== '|' && !opts.some(o => o[0] === cur)) opts.unshift([cur, (r.model || '') + ' · ' + (r.provider || '') + (r.available === false ? ' · unavailable' : '')]);
        return srS(k, r.role.charAt(0).toUpperCase() + r.role.slice(1), opts, SR_ROLE_HELP[r.role] || '');
      });
      return [
        { title: 'Local model (Ollama)', rows: [
          srX('ollama_host', 'Ollama host', 'Address of the machine running Ollama. Use another machine\'s LAN address to offload the GPU work.',
            { more: 'ANTHILL only needs HTTP access to Ollama\'s API port (11434 by default). If Ollama runs on another box, start it with OLLAMA_HOST=0.0.0.0 so it listens on the LAN.' }),
          srI('Reachability', oll.ok === true ? 'reachable' + srMs(oll.ms) + ' · ' + models.length + ' model' + (models.length === 1 ? '' : 's') : oll.ok === false ? (oll.error || 'unreachable') : 'checking…', 'Checked when this page opens and on Refresh.', { dot: srDot(oll.ok) }),
          srS('ollama_model', 'Default model', modelOpts.length ? modelOpts : [['', 'no models installed — ollama pull <model>']], 'Used by every role that has no route of its own.'),
        ] },
        { title: 'Routes by role', intro: 'Which model each role talks to. Per-project overrides live in the project\'s own Settings tab.', rows: routeRows.length ? routeRows : [srI('Routes', rj.roles ? 'none' : 'loading…', '')] },
      ];
    } },
  { id: 'colony', group: 'Colony', title: 'Colony', sub: 'How the ants run: what they are allowed to do, and how much they may use per mission.',
    build() {
      return [
        { title: 'Identity', rows: [
          srX('colony_name', 'Colony name', 'What this installation calls itself. It is the word you type to unlock an action in the Danger zone.'),
        ] },
        { title: 'What ants may do', intro: 'Capabilities for every mission. Each side effect still passes the conversation\'s approval policy before it happens.', rows: [
          srT('web_search_enabled', 'Web search', 'Ants may search the web while researching.'),
          srT('file_tools_enabled', 'Read files', 'Ants may read files inside a project\'s allowed folders.'),
          srT('file_writing_enabled', 'Write files', 'Ants may propose new files and edits. You approve before anything is written.'),
          srT('patch_application_enabled', 'Apply approved changes', 'Approved changes land on disk automatically instead of waiting for you to apply them.',
            { more: 'Without this, an approved change sits in Changes until you press Apply. With it, approval and apply are one step. A backup of the database is written before every mission either way.' }),
          srT('shell_tool_enabled', 'Shell commands', 'Ants may run commands on this host. This is remote code execution.', { caution: true }),
          srT('parallel_execution_enabled', 'Parallel execution', 'Independent tasks run side by side instead of one after another.'),
          srT('spec_ingestion_enabled', 'Spec ingestion', 'Requirement documents in a project are read into the mission context.'),
        ] },
        { title: 'Limits per mission', intro: 'Raise them for bigger jobs; lower them on small machines.', rows: [
          srN('max_parallel_workers', 'Parallel workers', '', 'How many tasks run at once. More is faster but uses more RAM and VRAM.',
            { more: 'Each worker holds its own model context in memory. On an 8 GB machine with an 8B model, 2–4 is realistic; 16 only makes sense with a dedicated GPU box on the LAN.' }),
          srN('max_web_searches_per_mission', 'Web searches', '/ mission', 'Searches the research role may run before it must work from what it has.'),
          srN('max_sources_per_mission', 'Sources', '/ mission', 'Pages fetched and read per mission.'),
          srN('max_context_packet_chars', 'Context packet', 'chars', 'Max text handed to the model per step. Bigger is slower and needs more memory.'),
          srN('long_input_threshold', 'Long-input threshold', 'chars', 'Inputs above this are summarised before use.'),
          srN('max_section_chars', 'Section size', 'chars', 'How large a chunk documents are split into.'),
        ] },
        { title: 'Conversations', intro: 'Ceilings for every new conversation. A live conversation keeps the budget it was created with.', rows: [
          srN('conversation_max_missions', 'Missions per conversation', '', 'How many missions one chat may start over its lifetime. The approval gate still decides each one, so a bigger budget never means more autonomy — only more room.'),
          srN('conversation_max_turns', 'Turns', '/ conversation', 'Messages a conversation may hold before it must be continued in a new one.'),
          srN('conversation_max_tool_calls', 'Tool calls', '/ conversation', 'Tool invocations across every mission a conversation starts.'),
          srN('conversation_max_seconds', 'Time budget', 's', 'Wall-clock seconds of work a conversation may spend.'),
        ] },
      ];
    } },
  { id: 'automation', group: 'Colony', title: 'Automation', sub: 'The Director: whether the colony may start missions on its own from project backlogs, and how hard it may push.',
    build() {
      return [
        { title: 'Director', rows: [
          srT('autonomy_enabled', 'Autonomy', 'The Director may start missions on its own from the project backlog.',
            { caution: true, more: 'Off by default. When on, the Director wakes every poll interval, picks the highest-priority objective whose gates it can satisfy, and runs it as a normal mission — same approvals, same backups.' }),
          srT('autonomy_learning_enabled', 'Learning', 'The Director adjusts objective priority from past outcomes.'),
          srN('autonomy_poll_seconds', 'Poll interval', 's', 'How often the Director looks for work.'),
        ] },
        { title: 'Rate limits', intro: 'Hard ceilings the Director can never exceed, whatever it learns.', rows: [
          srN('autonomy_max_missions_per_hour', 'Missions per hour', '', ''),
          srN('autonomy_max_missions_per_day', 'Missions per day', '', ''),
          srN('autonomy_max_consecutive_failures', 'Stop after consecutive failures', '', 'The Director pauses itself and asks you to look.'),
          srN('autonomy_concurrency', 'Concurrent autonomous missions', '', 'Bounded by the live resource governor, not by a fixed cap.'),
        ] },
        { title: 'Scheduling', rows: [
          srN('autonomy_aging_minutes', 'Aging', 'min', 'How long an objective waits before its priority starts rising.'),
          srN('autonomy_priority_bias_max', 'Learning bias cap', '%', 'Maximum priority shift learning may apply.'),
          srN('autonomy_retire_min_runs', 'Retire after', 'runs', 'Objectives that never succeed are retired after this many attempts.'),
          srN('autonomy_loop_window', 'Loop window', 'missions', 'How far back the Director looks when detecting a loop.'),
        ] },
      ];
    } },
  { id: 'security', group: 'Access', title: 'Security & Gates', sub: 'The install\'s posture, the gates the colony must pass, and the one directory it may touch.',
    build(x) {
      const s = SR.saved, st = (x && x.status) || {};
      const authOn = s.api_auth_enabled !== false;
      // The RUNNING values (see Connection): what this process is doing, not what the file says.
      const host = st.api_host || s.api_host, port = st.api_port || s.api_port;
      const profile = st.safety_profile || s.safety_profile;
      const lan = host === '0.0.0.0';
      const gitUser = (SR.values.autonomy_autoapply_git_username || '').trim();
      return [
        { title: 'Posture', rows: [
          srI('Authentication', authOn ? 'operator login · required' : 'DISABLED', authOn ? '' : 'Anyone who can reach this address can act as an operator.', { dot: srDot(authOn) }),
          srI('Safety profile', profile || '—', 'Shipped default. Strict profiles disable shell and auto-apply outright.'),
          srI('Network bind', (host || '—') + (port ? ':' + port : '') + (lan ? ' · LAN reachable' : ' · this machine only'), lan ? 'Anyone on your network can reach the login page.' : '', { dot: lan ? 'var(--amber)' : 'var(--green)' }),
          srI('Secrets at rest', 'encrypted · AES-256-GCM', 'Provider keys are never sent back to the browser.', { dot: 'var(--green)' }),
        ] },
        { title: 'Operator shell', intro: 'Off by default and fails closed. Turning it on widens what an administrator can do from this console — review before enabling on a network-reachable install.', rows: [
          srT('operator_shell_enabled', 'Operator shell', 'Enables the Terminal page — a direct terminal into this host as the ANTHILL service user.', { caution: true }),
          srX('operator_shell_dir', 'Default directory', 'Where a Terminal session starts. Blank uses the workspace root.'),
        ] },
        { title: 'Workspace boundary', rows: [
          srX('agent_workspace_dir', 'Workspace directory', 'The only directory file and coder ants may read from and propose changes against. Paths outside it are always rejected.'),
        ] },
        { title: 'Autonomous auto-apply', danger: true, intro: 'Lets the Director apply coder patches without human review — only paths matching the allowlist, and only if the workspace still builds and tests green afterwards. Inert until at least one path glob is added.', rows: [
          srT('autonomy_autoapply_enabled', 'Auto-apply', 'Also requires Write files and Apply approved changes under Colony.', { highRisk: true }),
          srX('autonomy_autoapply_paths', 'Allowlist path globs', 'One per line. Empty means nothing is eligible.', { multiline: true, placeholder: 'docs/**\nsrc/**/*.cs' }),
          srN('autonomy_autoapply_max_lines', 'Max lines per patch', 'lines', ''),
          srN('autonomy_autoapply_verify_timeout', 'Verify timeout', 's', ''),
          srX('autonomy_autoapply_verify_cmd', 'Verify command', 'Blank runs dotnet build && dotnet test.', { placeholder: 'dotnet build && dotnet test' }),
          srT('autonomy_autoapply_git_commit', 'Git-commit verified changes', 'After a green verify, commit the change on the standalone branch (never main).'),
          srX('autonomy_autoapply_git_username', 'Git user', 'Names the standalone branch' + (gitUser ? ': ' + gitUser + '-anthill' : ' (<user>-anthill).')),
          srX('autonomy_autoapply_git_remote', 'Git remote', 'Where the standalone branch is pushed.', { placeholder: 'origin' }),
          srX('autonomy_autoapply_git_ssh_key_path', 'SSH deploy key path', 'A key that may push the standalone branch and nothing else.'),
          srT('autonomy_autoapply_git_push', 'Push branch to origin', 'After commit, push the standalone branch via the SSH deploy key. Never pushes or merges main.'),
        ] },
      ];
    } },
  { id: 'users', group: 'Access', title: 'Users', sub: 'Who may sign in to this console and what each of them may do.', custom: 'users', build() { return []; } },
  { id: 'diagnostics', group: 'System', title: 'Diagnostics', sub: 'This installation: version, health, and the report to attach when something goes wrong.', actions: [['copyreport', 'Copy report'], ['refresh', '↺ Refresh']], custom: 'diag',
    build(x) {
      const h = x.health || {}, m = x.maint || {}, oll = x.ollama || {}, jobs = x.jobs || null, st = x.status || {};
      const streamOn = (typeof _evtCtl !== 'undefined') && !!_evtCtl;
      const workers = SR.saved.api_job_workers;
      const running = jobs ? jobs.filter(j => j.status === 'running').length : null;
      const diskPct = m.disk_total_bytes ? Math.round(100 * m.disk_free_bytes / m.disk_total_bytes) : null;
      return [
        { title: 'Health', rows: [
          srI('Version', h.version ? 'v' + h.version : '—', ''),
          srI('Ollama', oll.ok === true ? 'reachable' + srMs(oll.ms) : oll.ok === false ? (oll.error || 'unreachable') : 'checking…', '', { dot: srDot(oll.ok) }),
          srI('Event stream', streamOn ? 'connected' : 'reconnecting — polling', '', { dot: srDot(streamOn ? true : null) }),
          srI('Job workers', workers != null ? (running != null ? running + ' busy of ' + workers : workers + ' configured') : '—', 'API mission queue workers. Chat-started missions run outside the queue.'),
          srI('Native kernel', st.native_kernel || h.native_kernel || '—', (st.native_kernel || h.native_kernel) === 'managed-fallback' ? 'The managed implementation is in use; the native kernel is optional.' : ''),
          srI('Safety profile', st.safety_profile || SR.saved.safety_profile || '—', 'Governs which capabilities may be enabled at all. Set under Security & Gates.'),
          srI('Database', m.db_bytes != null ? srBytes(m.db_bytes) + ' · SQLite' : '—', ''),
          srI('Disk free', m.disk_total_bytes ? srBytes(m.disk_free_bytes) + ' of ' + srBytes(m.disk_total_bytes) : '—', '', diskPct == null ? {} : { dot: diskPct < 10 ? 'var(--red)' : diskPct < 25 ? 'var(--amber)' : 'var(--green)' }),
        ] },
        { title: 'Storage', intro: 'A full backup is written before every mission; the newest ' + (SR.saved.max_db_backups ?? '—') + ' are kept.', rows: [
          srI('Backups', m.backup_count != null ? m.backup_count + ' · ' + srBytes(m.backup_bytes) : '—', ''),
          srN('max_db_backups', 'Backups to keep', '', 'Older backups are pruned on the next compact.'),
          srA('Compact database', 'Compact & prune', 'compact', 'Prunes backups beyond the keep count and events past retention, then compacts the database. Safe to run any time.'),
        ] },
      ];
    } },
  { id: 'readiness', group: 'System', title: 'Readiness', sub: 'Whether this colony is qualified to run unattended — its thresholds and its own account of its wiring.', actions: [['cert', '⇩ Certification'], ['report', 'Write report']], custom: 'readiness', build() { return []; } },
  { id: 'terminal', group: 'System', title: 'Terminal', sub: 'A direct shell into the host, as the account ANTHILL runs under. Admin only; enabled by the Operator shell gate under Security & Gates.', custom: 'terminal', build() { return []; } },
  { id: 'danger', group: 'System', title: 'Danger zone', sub: 'Nothing here can be undone. Type the colony name to unlock an action.', danger: true,
    build(x) {
      const m = x.maint || {};
      return [
        { title: 'Irreversible actions', danger: true, rows: [
          srD('reset', 'Reset configuration', 'Reset', 'Every setting back to defaults. Connection, workspace, model routes, pricing and the colony name are kept; missions, projects and history are untouched.'),
          srD('backups', 'Delete all backups', 'Delete', (m.backup_count != null ? 'Frees ' + srBytes(m.backup_bytes) + ' across ' + m.backup_count + ' file(s). ' : '') + 'The live database is untouched; the next mission writes a fresh backup.'),
          srD('wipe', 'Wipe colony memory', 'Wipe', 'Deletes every mission (with its tasks, events, patches and approvals), every conversation, all evidence and artifacts, and the pheromone trails learned from them. Projects, objectives, schedules, users and providers remain. Refused while a mission is running.'),
        ] },
      ];
    } },
  // Hidden from the rail: the event log keeps its route (`/settings/system`) for Ctrl+L and old
  // bookmarks, but it is not a settings page and the design removes it from the list.
];

/* ---- helpers ---------------------------------------------------------------------------------- */
function srBytes(b) { b = Number(b) || 0; if (b < 1024) return b + ' B'; if (b < 1048576) return (b / 1024).toFixed(1) + ' KB'; if (b < 1073741824) return (b / 1048576).toFixed(1) + ' MB'; return (b / 1073741824).toFixed(2) + ' GB'; }
function srEsc(s) { return typeof escapeHtml === 'function' ? escapeHtml(s == null ? '' : String(s)) : String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]); }
function srPage(id) { return SR_PAGES.find(p => p.id === id) || SR_PAGES[0]; }
function srCanSee(p) { return p.vis === 'all' || (typeof ROLE !== 'undefined' && ROLE === 'admin'); }
function srIsDevice(k) { return k.indexOf('device:') === 0; }
function srIsRoute(k) { return k.indexOf('route:') === 0; }
function srRowValue(r) { return Object.prototype.hasOwnProperty.call(SR.values, r.key) ? SR.values[r.key] : (SR.saved[r.key]); }
function srNorm(v) { return Array.isArray(v) ? v.join('\n') : (typeof v === 'boolean' || v == null) ? v : String(v); }
function srDefaultOf(r) {
  if (srIsDevice(r.key)) return SR_DEVICE_KEYS[r.key] ? SR_DEVICE_KEYS[r.key].def : undefined;
  if (srIsRoute(r.key)) return 'ollama|' + (SR.saved.ollama_model || '');
  return SR.defaults[r.key];
}
function srChanged(r) { if (!r.key) return false; const d = srDefaultOf(r); if (d === undefined) return false; return srNorm(srRowValue(r)) !== srNorm(d); }
function srDirty(k) { return srNorm(SR.values[k]) !== srNorm(SR.saved[k]); }
function srDirtyKeys() { return Object.keys(SR.values).filter(srDirty); }
function srAllRows() { const out = []; for (const p of SR_PAGES) { let secs = []; try { secs = p.build(SR.extra) || []; } catch (_) { secs = []; } for (const s of secs) for (const r of s.rows) out.push({ r, p }); } return out; }
function srLabelOf(k) { const hit = srAllRows().find(x => x.r.key === k); return hit ? hit.r.label : k; }
function srMatches(r, q) { return !q || ((r.label || '') + ' ' + (r.help || '')).toLowerCase().indexOf(q) >= 0; }
function srRangeOf(r) { if (r.range) return r.range; const rg = SR.ranges[r.key]; if (rg && (rg.min != null || rg.max != null)) return [rg.min, rg.max]; return null; }
function srFmt(v) { return typeof v === 'number' ? v.toLocaleString('en-US') : (v === '' || v == null ? '—' : String(v)); }

/* ---- data ------------------------------------------------------------------------------------- */
async function srLoadCore() {
  const [s, d] = await Promise.all([api('/settings'), api('/settings/defaults').catch(() => null)]);
  if (!(s && s.success && s.data)) throw new Error((s && s.message) || 'Settings unavailable.');
  SR.saved = Object.assign({}, s.data);
  for (const k of Object.keys(SR_DEVICE_KEYS)) SR.saved[k] = SR_DEVICE_KEYS[k].get();
  if (d && d.success && d.data) { SR.defaults = d.data.defaults || {}; SR.ranges = d.data.ranges || {}; }
  SR.values = Object.assign({}, SR.saved);
  SR.loaded = true;
}

async function srLoadPageData(id) {
  const x = SR.extra;
  const timed = async (fn) => { const t0 = performance.now(); const v = await fn(); return [v, performance.now() - t0]; };
  const ollama = async () => {
    try {
      const [r, ms] = await timed(() => fetch(url('/ollama/models'), { headers: { 'Authorization': 'Bearer ' + TOKEN } }));
      const data = await r.json();
      const models = (data.models || []).map(m => ({ name: m.name || m.model || 'unknown', size: m.size || 0 })).sort((a, b) => b.size - a.size);
      x.ollama = (!models.length && data.error) ? { ok: false, error: data.message || 'Ollama unreachable', models: [] } : { ok: true, ms, models };
    } catch (e) { x.ollama = { ok: false, error: e.message, models: [] }; }
  };
  if (id === 'models') {
    await Promise.all([ollama(), (async () => { try { const r = await api('/routes/json'); x.routes = (r && r.success && r.data) || {}; for (const role of (x.routes.roles || [])) { const k = 'route:' + role.role; SR.saved[k] = (role.provider || '') + '|' + (role.model || ''); if (!srDirty(k)) SR.values[k] = SR.saved[k]; } } catch (_) { x.routes = {}; } })()]);
  } else if (id === 'connection') {
    try {
      const [r, ms] = await timed(() => api('/status'));
      const d = (r && r.data) || {};
      x.status = d;
      x.conn = { ok: !!(r && r.success), ms, api_host: d.api_host, api_port: d.api_port, reachable_ip: d.reachable_ip,
        deployment: (typeof lastSystemSummary !== 'undefined' && lastSystemSummary && lastSystemSummary.deployment_mode) || '' };
    } catch (_) { x.conn = { ok: false }; }
  } else if (id === 'diagnostics' || id === 'danger') {
    await Promise.all([
      (async () => { try { const r = await fetch(url('/health')); x.health = await r.json().then(j => j.data || j); } catch (_) { x.health = {}; } })(),
      (async () => { try { const r = await api('/maintenance/stats'); x.maint = (r && r.success && r.data) || {}; } catch (_) { x.maint = {}; } })(),
      id === 'diagnostics' ? ollama() : Promise.resolve(),
      id === 'diagnostics' ? (async () => { try { const r = await api('/jobs'); x.jobs = (r && r.success && r.data) || []; } catch (_) { x.jobs = null; } })() : Promise.resolve(),
      id === 'diagnostics' ? (async () => { try { const r = await api('/status'); x.status = (r && r.data) || {}; } catch (_) { x.status = {}; } })() : Promise.resolve(),
      // The raw report is what an operator attaches to a bug report: the diagnostics text, then the
      // effective configuration and the model/route report, each under its own heading.
      id === 'diagnostics' ? (async () => {
        const part = async (label, path) => { try { return '=== ' + label + ' ===\n' + (await apiText(path)); } catch (e) { return '=== ' + label + ' ===\nunavailable: ' + (e && e.message || e); } };
        const parts = await Promise.all([part('diagnostics', '/diagnostics'), part('config', '/config'), part('models', '/models')]);
        x.diag = parts.join('\n\n');
      })() : Promise.resolve(),
    ]);
  } else if (id === 'security') {
    try { const r = await api('/status'); x.status = (r && r.data) || {}; } catch (_) { x.status = {}; }
  } else if (id === 'users') {
    try { const r = await api('/users'); x.users = (r && r.success && r.data) || []; x.usersError = r && !r.success ? r.message : ''; } catch (e) { x.users = []; x.usersError = e.message; }
  } else if (id === 'readiness') {
    await Promise.all([
      (async () => { try { const r = await api('/readiness/json'); x.readiness = (r && r.success && r.data) || null; } catch (_) { x.readiness = null; } })(),
      (async () => { try { const r = await api('/colony/introspection'); x.introspection = (r && r.success && r.data) || null; } catch (_) { x.introspection = null; } })(),
    ]);
  }
}

/* ---- entry / exit ------------------------------------------------------------------------------ */
async function settingsOpen(stab) {
  const id = SR_PAGES.some(p => p.id === stab) ? stab : 'account';
  SR.page = id;
  srNavEnter();
  srRenderRail(); srRenderHead();
  const content = document.getElementById('sr-content');
  if (!SR.loaded && content) content.innerHTML = '<div class="sr-empty">Loading settings…</div>';
  const token = (SR.loading = {});
  try {
    if (!SR.loaded) await srLoadCore();
    await srLoadPageData(id);
  } catch (e) {
    if (content && SR.loading === token) content.innerHTML = '<div class="sr-empty sr-err">' + srEsc(e && e.message || e) + '</div>';
    return;
  }
  if (SR.loading !== token || SR.page !== id) return;   // the operator moved on while this loaded
  srRender();
  if (id === 'terminal' && typeof initShell === 'function') { try { initShell(); } catch (_) {} }
}

function srNavEnter() {
  if (SR.navPrior === null) SR.navPrior = document.body.classList.contains('nav-collapsed');
  let choice = null; try { choice = localStorage.getItem('settings-nav-choice'); } catch (_) {}
  document.body.classList.toggle('nav-collapsed', choice !== 'open');
}
function settingsLeave() {
  if (SR.navPrior === null) return;
  document.body.classList.toggle('nav-collapsed', SR.navPrior);
  SR.navPrior = null;
}
/** The rail's own collapse button, while Settings is open, records a Settings-specific choice. */
function settingsNavToggled(collapsed) {
  try { localStorage.setItem('settings-nav-choice', collapsed ? 'collapsed' : 'open'); } catch (_) {}
}

/* ---- rendering -------------------------------------------------------------------------------- */
function srRender() { srRenderRail(); srRenderHead(); srRenderContent(); srRenderSaveBar(); }

function srRenderRail() {
  const rail = document.getElementById('sr-rail-list'); if (!rail) return;
  const q = SR.query.trim().toLowerCase();
  const dirty = srDirtyKeys();
  const rows = q || dirty.length ? srAllRows() : [];
  const dirtyOn = pid => dirty.some(k => rows.some(x => x.r.key === k && x.p.id === pid));
  let html = '', last = null;
  for (const p of SR_PAGES) {
    if (!srCanSee(p)) continue;
    if (p.group !== last) { html += '<div class="sr-group">' + srEsc(p.group) + '</div>'; last = p.group; }
    const active = p.id === SR.page;
    const hits = q ? rows.filter(x => x.p.id === p.id && srMatches(x.r, q)).length : 0;
    const hasRows = rows.some(x => x.p.id === p.id) || p.custom;
    const dim = q && !hits && hasRows && !p.custom;
    html += '<div class="sr-item' + (active ? ' active' : '') + (p.danger ? ' danger' : '') + (dim ? ' dim' : '') + '" data-page="' + p.id + '" role="button" tabindex="0">'
      + '<span>' + srEsc(p.title) + '</span>'
      + (dirtyOn(p.id) ? '<span class="sr-dirty-dot" title="Unsaved changes"></span>' : '')
      + (q && hits && !active ? '<span class="sr-hits">' + hits + '</span>' : '')
      + '</div>';
  }
  rail.innerHTML = html;
}

function srRenderHead() {
  const p = srPage(SR.page);
  const head = document.getElementById('sr-head'); if (!head) return;
  head.innerHTML = '<div><h1 class="sr-title' + (p.danger ? ' danger' : '') + '">' + srEsc(p.title) + '</h1><div class="sr-sub">' + srEsc(p.sub) + '</div></div>'
    + ((p.actions && p.actions.length) ? '<div class="sr-hdr-acts">' + p.actions.map(a => '<button class="btn btn-ghost sr-hdr-act" data-act="' + a[0] + '">' + srEsc(a[1]) + '</button>').join('') + '</div>' : '');
}

function srRowHtml(r) {
  const key = r.key || '';
  const v = key ? srRowValue(r) : r.value;
  const changed = srChanged(r);
  const dirty = key ? srDirty(key) : false;
  const openKey = key || r.label;
  const isOpen = !!SR.open[openKey];
  const range = r.type === 'number' ? srRangeOf(r) : null;
  const def = key ? srDefaultOf(r) : undefined;
  let control = '';
  switch (r.type) {
    case 'toggle':
      control = '<div class="sr-toggle' + (v ? ' on' : '') + '" role="switch" aria-checked="' + (v ? 'true' : 'false') + '" tabindex="0" data-sr="' + srEsc(key) + '"><span class="sr-knob"></span></div>';
      break;
    case 'number':
      control = '<div class="sr-numwrap' + (changed ? ' changed' : '') + '"><input type="number" class="sr-input sr-num" data-sr="' + srEsc(key) + '" value="' + srEsc(v == null ? '' : v) + '"' + (range && range[0] != null ? ' min="' + range[0] + '"' : '') + (range && range[1] != null ? ' max="' + range[1] + '"' : '') + '>' + (r.unit ? '<span class="sr-unit">' + srEsc(r.unit) + '</span>' : '') + '</div>';
      break;
    case 'text':
      control = r.multiline
        ? '<textarea class="sr-input sr-text' + (changed ? ' changed' : '') + '" rows="3" data-sr="' + srEsc(key) + '" placeholder="' + srEsc(r.placeholder || '') + '">' + srEsc(srNorm(v) == null ? '' : srNorm(v)) + '</textarea>'
        : '<input type="text" class="sr-input sr-text' + (changed ? ' changed' : '') + '" data-sr="' + srEsc(key) + '" value="' + srEsc(v == null ? '' : v) + '" placeholder="' + srEsc(r.placeholder || '') + '">';
      break;
    case 'select':
      control = '<select class="sr-input sr-select' + (changed ? ' changed' : '') + '" data-sr="' + srEsc(key) + '">' + (r.options || []).map(o => { const ov = Array.isArray(o) ? o[0] : o, ol = Array.isArray(o) ? o[1] : o; return '<option value="' + srEsc(ov) + '"' + (String(ov) === String(v == null ? '' : v) ? ' selected' : '') + '>' + srEsc(ol) + '</option>'; }).join('') + '</select>';
      break;
    case 'info':
      control = '<span class="sr-info">' + (r.dot ? '<span class="sr-dot" style="background:' + r.dot + '"></span>' : '') + srEsc(r.value) + '</span>';
      break;
    case 'action':
      control = '<button class="btn btn-ghost sr-act" data-act="' + srEsc(r.act) + '">' + srEsc(r.action) + '</button>';
      break;
    case 'danger': {
      const name = SR.saved.colony_name || 'anthill';
      const conf = SR.confirms[r.id] || '';
      const locked = conf.trim() !== name;
      control = '<div class="sr-dangerctl"><input type="text" class="sr-input sr-confirm" data-danger="' + srEsc(r.id) + '" value="' + srEsc(conf) + '" placeholder="' + srEsc(name) + '" autocomplete="off" spellcheck="false"><button class="sr-danger-btn" data-danger="' + srEsc(r.id) + '"' + (locked ? ' disabled' : '') + '>' + srEsc(r.action) + '</button></div>';
      break;
    }
  }
  const meta = r.type === 'number' && key
    ? '<div class="sr-meta"><span>' + (range ? srEsc((range[0] != null ? srFmt(range[0]) : '') + ' – ' + (range[1] != null ? srFmt(range[1]) : '')) : '') + '</span>'
      + (changed && def !== undefined ? '<a href="#" class="sr-reset" data-sr="' + srEsc(key) + '">default ' + srEsc(srFmt(def)) + ' · reset</a>' : (def !== undefined ? '<span>default</span>' : '')) + '</div>'
    : '';
  return '<div class="sr-row' + (dirty ? ' dirty' : '') + '" data-key="' + srEsc(key) + '">'
    + '<div class="sr-left"><div class="sr-label">' + srEsc(r.label)
    + (r.caution ? '<span class="sr-badge caution">CAUTION</span>' : '') + (r.highRisk ? '<span class="sr-badge risk">HIGHEST RISK</span>' : '')
    + (changed ? '<span class="sr-changed">changed</span>' : '') + '</div>'
    + ((r.help || r.more) ? '<div class="sr-help">' + srEsc(r.help || '') + (r.more ? ' <a href="#" class="sr-more" data-open="' + srEsc(openKey) + '">' + (isOpen ? 'Less' : 'Learn more') + '</a>' : '') + '</div>' : '')
    + (r.more && isOpen ? '<div class="sr-moretext">' + srEsc(r.more) + '</div>' : '')
    + '</div><div class="sr-right">' + control + meta + '</div></div>';
}

function srRenderContent() {
  const p = srPage(SR.page);
  const content = document.getElementById('sr-content'); if (!content) return;
  const q = SR.query.trim().toLowerCase();
  let secs = []; try { secs = p.build(SR.extra) || []; } catch (e) { content.innerHTML = '<div class="sr-empty sr-err">' + srEsc(e.message) + '</div>'; return; }
  const total = secs.reduce((n, s) => n + s.rows.length, 0);
  const shown = secs.map(s => Object.assign({}, s, { rows: s.rows.filter(r => srMatches(r, q)) })).filter(s => s.rows.length);
  let html = '';
  if (q && !shown.length && total) html += '<div class="sr-empty">Nothing on this page matches “' + srEsc(SR.query.trim()) + '”. The count beside each rail item shows where it does.</div>';
  for (const s of shown) {
    html += '<section class="sr-section' + (s.danger ? ' danger' : '') + '"><div class="sr-sechead"><span>' + srEsc(s.title) + '</span>' + (s.note ? '<span class="sr-secnote">' + srEsc(s.note) + '</span>' : '') + '</div>'
      + (s.intro ? '<div class="sr-intro">' + srEsc(s.intro) + '</div>' : '')
      + s.rows.map(srRowHtml).join('') + '</section>';
  }
  if (!q && p.custom === 'users') html += srUsersHtml();
  if (!q && p.custom === 'diag') html += srDiagRawHtml();
  if (!q && p.custom === 'readiness') html += srReadinessHtml();
  content.innerHTML = html;
  const term = document.getElementById('sr-static-terminal');
  if (term) term.style.display = (p.custom === 'terminal' && !q) ? '' : 'none';
}

function srRenderSaveBar() {
  const bar = document.getElementById('sr-savebar'); if (!bar) return;
  const dirty = srDirtyKeys();
  if (!dirty.length) { bar.hidden = true; return; }
  bar.hidden = false;
  const el = bar.querySelector('.sr-savecount'); if (el) el.textContent = dirty.length + ' unsaved ' + (dirty.length === 1 ? 'change' : 'changes');
  const list = bar.querySelector('.sr-savelist'); if (list) list.textContent = dirty.map(srLabelOf).join(' · ');
}

function srRefreshRow(key) {
  // Re-render one row's markers without rebuilding the page, so a field keeps focus while typing.
  const row = document.querySelector('#sr-content .sr-row[data-key="' + (window.CSS && CSS.escape ? CSS.escape(key) : key) + '"]'); if (!row) return;
  const hit = srAllRows().find(x => x.r.key === key); if (!hit) return;
  const r = hit.r, changed = srChanged(r), dirty = srDirty(key);
  row.classList.toggle('dirty', dirty);
  const lbl = row.querySelector('.sr-label'); const had = lbl && lbl.querySelector('.sr-changed');
  if (lbl && changed && !had) lbl.insertAdjacentHTML('beforeend', '<span class="sr-changed">changed</span>');
  if (lbl && !changed && had) had.remove();
  row.querySelectorAll('.sr-numwrap,.sr-text,.sr-select').forEach(c => c.classList.toggle('changed', changed));
  const meta = row.querySelector('.sr-meta');
  if (meta) {
    const range = srRangeOf(r), def = srDefaultOf(r);
    meta.innerHTML = '<span>' + (range ? srEsc((range[0] != null ? srFmt(range[0]) : '') + ' – ' + (range[1] != null ? srFmt(range[1]) : '')) : '') + '</span>'
      + (changed && def !== undefined ? '<a href="#" class="sr-reset" data-sr="' + srEsc(key) + '">default ' + srEsc(srFmt(def)) + ' · reset</a>' : (def !== undefined ? '<span>default</span>' : ''));
  }
  const tg = row.querySelector('.sr-toggle'); if (tg) { const v = !!srRowValue(r); tg.classList.toggle('on', v); tg.setAttribute('aria-checked', v ? 'true' : 'false'); }
}

/* ---- custom blocks ---------------------------------------------------------------------------- */
function srUsersHtml() {
  const x = SR.extra, users = x.users || [];
  const me = (typeof USERNAME !== 'undefined' && USERNAME) || '';
  const roles = ['admin', 'coordinator', 'infrastructure_operator'];
  const initials = u => (u.username || '?').replace(/[^a-z0-9]/gi, '').slice(0, 2).toUpperCase() || '?';
  const seen = u => u.last_login_at ? srAgo(u.last_login_at) : 'never';
  let html = '<section class="sr-section"><div class="sr-sechead"><span>Operators</span><button class="btn btn-primary sr-invite-toggle">+ Invite operator</button></div>';
  if (SR.inviteOpen) html += '<div class="sr-invite"><input class="sr-input sr-text" id="sr-nu-user" placeholder="username" autocomplete="off"><input class="sr-input sr-text" id="sr-nu-pass" type="password" placeholder="password" autocomplete="new-password"><select class="sr-input sr-select" id="sr-nu-role">' + roles.map(r => '<option value="' + r + '">' + r + '</option>').join('') + '</select><button class="btn btn-primary sr-invite-send">Create</button><button class="btn btn-ghost sr-invite-toggle">Cancel</button></div>';
  if (x.usersError) html += '<div class="sr-empty sr-err">' + srEsc(x.usersError) + '</div>';
  else if (!users.length) html += '<div class="sr-empty">No accounts.</div>';
  for (const u of users) {
    const self = u.username === me;
    html += '<div class="sr-user' + (u.active === false ? ' inactive' : '') + '" data-user="' + srEsc(u.username) + '">'
      + '<div class="sr-avatar' + (self ? ' me' : '') + '">' + srEsc(initials(u)) + '</div>'
      + '<div><div class="sr-uname">' + srEsc(u.username) + (self ? ' <span class="sr-you">you</span>' : '') + (u.active === false ? ' <span class="sr-badge caution">DISABLED</span>' : '') + '</div><div class="sr-help">created ' + srEsc(u.created_at ? String(u.created_at).slice(0, 10) : '—') + '</div></div>'
      + '<select class="sr-input sr-select sr-user-role"' + (self ? ' disabled title="Ask another admin to change your role"' : '') + '>' + roles.map(r => '<option value="' + r + '"' + (u.role === r ? ' selected' : '') + '>' + r + '</option>').join('') + '</select>'
      + '<span class="sr-mono">' + srEsc(seen(u)) + '</span>'
      + '<div class="sr-useracts">' + (self ? '<span class="sr-you">You</span>'
        : '<button class="btn btn-ghost sr-user-act" data-uact="password">Reset pw</button><button class="btn btn-ghost sr-user-act" data-uact="active">' + (u.active === false ? 'Enable' : 'Disable') + '</button><button class="btn btn-ghost sr-user-act sr-remove" data-uact="remove">Remove</button>') + '</div>'
      + '</div>';
  }
  return html + '</section>';
}
function srAgo(iso) { const t = Date.parse(iso); if (!t) return String(iso); const s = Math.max(0, (Date.now() - t) / 1000); if (s < 60) return 'now'; if (s < 3600) return Math.round(s / 60) + ' min ago'; if (s < 86400) return Math.round(s / 3600) + ' h ago'; return Math.round(s / 86400) + ' d ago'; }

function srDiagRawHtml() {
  const txt = SR.extra.diag == null ? 'Loading…' : SR.extra.diag;
  return '<section class="sr-section"><div class="sr-sechead"><span>Raw report</span><span class="sr-rawacts"><button class="btn btn-ghost sr-raw-copy">Copy</button><button class="btn btn-ghost sr-raw-toggle">' + (SR.rawOpen ? 'Collapse' : 'Expand') + '</button></span></div>'
    + '<pre class="sr-raw' + (SR.rawOpen ? ' open' : '') + '" id="sr-raw">' + srEsc(txt) + '</pre></section>';
}

function srReadinessHtml() {
  const d = SR.extra.readiness;
  let html = '';
  if (!d) return '<div class="sr-empty sr-err">Readiness snapshot unavailable.</div>';
  const attestable = new Set(d.attestable_ids || []);
  const rows = [...(d.checks || [])].sort((a, b) => (a.satisfied ? 1 : 0) - (b.satisfied ? 1 : 0));
  html += '<div class="sr-statement' + (d.ready ? ' ok' : '') + '">' + srEsc((d.ready ? 'READY — ' : 'NOT READY — ') + (d.statement || '') + ' (' + d.satisfied + '/' + d.total + ')') + '</div>';
  html += '<section class="sr-section"><div class="sr-sechead"><span>Thresholds</span></div>';
  if (!rows.length) html += '<div class="sr-empty">No thresholds defined.</div>';
  for (const c of rows) {
    const color = c.satisfied ? 'var(--green)' : 'var(--red)';
    html += '<div class="sr-check" data-check="' + srEsc(c.id) + '"><span class="sr-glow" style="background:' + color + ';box-shadow:0 0 6px ' + color + '"></span>'
      + '<div><div class="sr-label">' + srEsc(c.title) + '</div><div class="sr-help">' + srEsc(c.detail || '') + '</div>'
      + (attestable.has(c.id) ? '<div class="sr-attest"><input type="text" class="sr-input sr-text sr-attest-note" placeholder="Attestation note (why you are satisfied it holds)"><button class="btn btn-ghost sr-attest" data-sat="1">Attest: holds</button><button class="btn btn-ghost sr-attest" data-sat="0">Attest: does not hold</button></div>' : '')
      + '</div><span class="sr-mono" style="color:' + color + '">' + (c.satisfied ? 'holds' : 'does not hold') + '</span><span class="sr-mono sr-dim">' + srEsc(c.kind || '') + '</span></div>';
  }
  html += '</section>';
  const i = SR.extra.introspection;
  html += '<section class="sr-section"><div class="sr-sechead"><span>Introspection</span></div>';
  if (!i) html += '<div class="sr-empty sr-err">Introspection unavailable.</div>';
  else {
    const on = v => v ? 'on' : 'off';
    const chip = (l, v, tone) => '<span class="sr-chip"><span>' + srEsc(l) + '</span> <b' + (tone ? ' style="color:' + tone + '"' : '') + '>' + srEsc(String(v)) + '</b></span>';
    const findings = i.config_health || [];
    html += '<div class="sr-chips">' + chip('Version', i.version) + chip('Activation tier', i.activation_tier) + chip('Autonomy', on(i.autonomy_enabled))
      + chip('Stop engaged', i.stop_engaged ? 'YES' : 'no', i.stop_engaged ? 'var(--red)' : '') + chip('Director', i.director_running ? 'running' : 'idle')
      + chip('File writing', on(i.can_write_files)) + chip('Patch application', on(i.can_apply_patches)) + chip('Auto-apply', on(i.auto_apply_enabled))
      + chip('Running jobs', i.running_jobs) + chip('V3 qualified', i.v3_qualified ? 'yes' : 'no', i.v3_qualified ? 'var(--green)' : 'var(--dim)') + '</div>'
      + '<div class="sr-help" style="margin-top:8px">Executable roles: ' + srEsc((i.executable_roles || []).join(', ')) + '</div>'
      + (findings.length ? findings.map(f => '<div class="sr-check"><span class="sr-glow" style="background:var(--red);box-shadow:0 0 6px var(--red)"></span><div><div class="sr-label">' + srEsc((f.severity || '').toUpperCase() + ' · ' + (f.combination || '')) + '</div><div class="sr-help">' + srEsc(f.detail || '') + '</div></div></div>').join('')
        : '<div class="sr-help" style="margin-top:8px;color:var(--green)">Configuration is healthy — no incompatible combinations.</div>');
  }
  return html + '</section>';
}

/* ---- toast ------------------------------------------------------------------------------------ */
function srToast(msg, ok) {
  const t = document.getElementById('sr-toast'); if (!t) return;
  t.textContent = ''; t.insertAdjacentHTML('beforeend', (ok !== false ? '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"></polyline></svg>' : '') + srEsc(msg));
  t.classList.toggle('err', ok === false); t.hidden = false;
  clearTimeout(SR.toastTimer); SR.toastTimer = setTimeout(() => { t.hidden = true; }, ok === false ? 5000 : 2400);
}

/* ---- saving ----------------------------------------------------------------------------------- */
function srSetValue(key, v) {
  SR.values[key] = v;
  srRefreshRow(key); srRenderSaveBar(); srRenderRail();
}

async function srSave() {
  if (SR.busy) return;
  const dirty = srDirtyKeys(); if (!dirty.length) return;
  const server = {}, routes = [], device = [];
  for (const k of dirty) {
    if (srIsDevice(k)) device.push(k);
    else if (srIsRoute(k)) routes.push(k);
    else server[k] = SR.values[k];
  }
  // Shape the server keys the way the endpoint expects them. `autonomy_autoapply_paths` is a list;
  // numbers travel as numbers; a blank workspace directory is refused here because the runtime
  // would read it as "no boundary", which is not a value an operator can mean by clearing a box.
  if (Object.prototype.hasOwnProperty.call(server, 'agent_workspace_dir') && !String(server.agent_workspace_dir || '').trim()) { srToast('Workspace directory cannot be blank.', false); return; }
  if (Object.prototype.hasOwnProperty.call(server, 'colony_name') && !String(server.colony_name || '').trim()) { srToast('Colony name cannot be blank.', false); return; }
  if (Object.prototype.hasOwnProperty.call(server, 'autonomy_autoapply_paths')) server.autonomy_autoapply_paths = String(server.autonomy_autoapply_paths || '').split('\n').map(s => s.trim()).filter(Boolean);
  for (const k of Object.keys(server)) { const row = srAllRows().find(x => x.r.key === k); if (row && row.r.type === 'number') { const n = Number(server[k]); if (isNaN(n)) { srToast(row.r.label + ' must be a number.', false); return; } server[k] = n; } }
  SR.busy = true;
  const btn = document.getElementById('sr-save'); if (btn) btn.disabled = true;
  try {
    const failures = [];
    if (Object.keys(server).length) {
      const r = await api('/settings', 'POST', server);
      if (!(r && r.success)) failures.push((r && r.message) || 'Settings save failed.');
    }
    for (const k of routes) {
      const role = k.slice('route:'.length); const [provider, model] = String(SR.values[k]).split('|');
      const r = await api('/routes/' + encodeURIComponent(role), 'POST', { provider, model });
      if (!(r && r.success)) failures.push(role + ': ' + ((r && r.message) || 'route not saved'));
    }
    for (const k of device) { try { SR_DEVICE_KEYS[k].set(SR.values[k]); } catch (e) { failures.push(k + ': ' + e.message); } }
    if (typeof apiCacheBust === 'function') apiCacheBust('');
    if (typeof pollModelInfo === 'function') { try { pollModelInfo(); } catch (_) {} }
    // Re-read the truth rather than trusting the edit: what the server kept is what the page shows.
    SR.loaded = false; await srLoadCore(); await srLoadPageData(SR.page);
    srRender();
    if (failures.length) srToast(failures.join(' · '), false);
    else srToast('Saved · applies to the next mission');
  } catch (e) { srToast('Save failed: ' + (e && e.message || e), false); }
  finally { SR.busy = false; if (btn) btn.disabled = false; }
}

function srDiscard() { SR.values = Object.assign({}, SR.saved); srRender(); }

/* ---- actions ---------------------------------------------------------------------------------- */
async function srHeaderAction(act) {
  const p = srPage(SR.page);
  if (act === 'refresh') { await srLoadPageData(p.id); srRender(); srToast('Refreshed'); return; }
  if (act === 'copyreport') { try { await navigator.clipboard.writeText(SR.extra.diag || ''); srToast('Report copied'); } catch (_) { srToast('Clipboard unavailable — select the raw report and copy it.', false); } return; }
  if (act === 'cert') {
    try {
      const r = await fetch(url('/readiness/certification'), { headers: { 'Authorization': 'Bearer ' + TOKEN } });
      if (!r.ok) { srToast('Certification unavailable (HTTP ' + r.status + ').', false); return; }
      const blob = await r.blob(); const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = 'anthill-readiness-certification.txt'; document.body.appendChild(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(a.href), 2000);
    } catch (e) { srToast('Certification download failed: ' + e.message, false); }
    return;
  }
  if (act === 'report') {
    const r = await api('/readiness/qualification-report', 'POST', {});
    srToast(r && r.success ? (r.message || 'Report written.') : ((r && r.message) || 'Report failed.'), !!(r && r.success));
    return;
  }
}

async function srRowAction(act) {
  if (act === 'password') {
    const me = (typeof USERNAME !== 'undefined' && USERNAME) || '';
    const pw = await uiPrompt('New password for ' + me + ' — you will be signed out everywhere once it changes.', { title: 'Change password', password: true, ok: 'Change' });
    if (!pw) return;
    const r = await api('/users/' + encodeURIComponent(me), 'PATCH', { password: pw });
    srToast(r && r.success ? 'Password changed — sign in again.' : ((r && r.message) || 'Password not changed.'), !!(r && r.success));
    return;
  }
  if (act === 'compact') {
    const r = await api('/maintenance/flush', 'POST', {});
    if (r && r.success) { srToast(r.message || 'Compacted.'); await srLoadPageData('diagnostics'); srRender(); }
    else srToast((r && r.message) || 'Compact failed.', false);
    return;
  }
}

async function srDangerAction(id) {
  const name = SR.saved.colony_name || 'anthill';
  const typed = (SR.confirms[id] || '').trim();
  if (typed !== name) { srToast('Type the colony name (' + name + ') to unlock this action.', false); return; }
  const ep = id === 'reset' ? '/maintenance/reset-config' : id === 'backups' ? '/maintenance/delete-backups' : '/maintenance/wipe-memory';
  const what = id === 'reset' ? 'Reset every setting to its default?' : id === 'backups' ? 'Delete every database backup?' : 'Wipe every mission, conversation, artifact and learned trail?';
  const ok = await uiConfirm(what + ' This cannot be undone.', { title: 'Danger zone', ok: id === 'reset' ? 'Reset' : id === 'backups' ? 'Delete' : 'Wipe', danger: true });
  if (!ok) return;
  const r = await api(ep, 'POST', { confirm: typed });
  if (r && r.success) {
    SR.confirms[id] = '';
    srToast(r.message || 'Done.');
    SR.loaded = false; try { await srLoadCore(); await srLoadPageData(SR.page); } catch (_) {}
    srRender();
    if (typeof apiCacheBust === 'function') apiCacheBust('');
  } else srToast((r && r.message) || 'Refused.', false);
}

async function srUserAction(username, act, rowEl) {
  if (act === 'role') {
    const role = rowEl.querySelector('.sr-user-role').value;
    const r = await api('/users/' + encodeURIComponent(username), 'PATCH', { role });
    srToast(r && r.success ? username + ' is now ' + role + '.' : ((r && r.message) || 'Role not changed.'), !!(r && r.success));
  } else if (act === 'password') {
    const pw = await uiPrompt('New password for ' + username + '. Their sessions are signed out.', { title: 'Reset password', password: true, ok: 'Reset' });
    if (!pw) return;
    const r = await api('/users/' + encodeURIComponent(username), 'PATCH', { password: pw });
    srToast(r && r.success ? 'Password reset for ' + username + '.' : ((r && r.message) || 'Not reset.'), !!(r && r.success));
  } else if (act === 'active') {
    const u = (SR.extra.users || []).find(x => x.username === username); const active = !(u && u.active !== false);
    const r = await api('/users/' + encodeURIComponent(username), 'PATCH', { active });
    srToast(r && r.success ? (username + (active ? ' enabled.' : ' disabled.')) : ((r && r.message) || 'Not changed.'), !!(r && r.success));
  } else if (act === 'remove') {
    const ok = await uiConfirm('Remove ' + username + '? Their sessions end immediately.', { title: 'Remove account', ok: 'Remove', danger: true });
    if (!ok) return;
    const r = await api('/users/' + encodeURIComponent(username), 'DELETE');
    srToast(r && r.success ? username + ' removed.' : ((r && r.message) || 'Not removed.'), !!(r && r.success));
  }
  await srLoadPageData('users'); srRender();
}

async function srInvite() {
  const user = (document.getElementById('sr-nu-user') || {}).value || '', pass = (document.getElementById('sr-nu-pass') || {}).value || '', role = (document.getElementById('sr-nu-role') || {}).value || 'coordinator';
  if (!user.trim() || !pass) { srToast('Username and password are required.', false); return; }
  const r = await api('/users', 'POST', { username: user.trim(), password: pass, role });
  if (r && r.success) { SR.inviteOpen = false; srToast('Account created: ' + user.trim()); await srLoadPageData('users'); srRender(); }
  else srToast((r && r.message) || 'Not created.', false);
}

async function srAttest(checkId, satisfied, note) {
  const r = await api('/readiness/attest', 'POST', { threshold_id: checkId, satisfied, note });
  srToast(r && r.success ? 'Attestation recorded.' : ((r && r.message) || 'Attestation failed.'), !!(r && r.success));
  if (r && r.success) { await srLoadPageData('readiness'); srRender(); }
}

/* ---- wiring (delegated from the page root; nothing binds at parse time) ------------------------ */
function srWire() {
  const root = document.getElementById('page-settings'); if (!root || root.dataset.srWired) return;
  root.dataset.srWired = '1';
  srApplyReduceMotion();

  root.addEventListener('click', e => {
    const t = e.target;
    const item = t.closest('.sr-item[data-page]');
    if (item) { const id = item.dataset.page; if (typeof go === 'function') go('/settings/' + id); else settingsOpen(id); return; }
    const tg = t.closest('.sr-toggle[data-sr]'); if (tg) { const k = tg.dataset.sr; srSetValue(k, !srRowValue({ key: k })); return; }
    const more = t.closest('.sr-more[data-open]'); if (more) { e.preventDefault(); SR.open[more.dataset.open] = !SR.open[more.dataset.open]; srRenderContent(); return; }
    const reset = t.closest('.sr-reset[data-sr]'); if (reset) { e.preventDefault(); const k = reset.dataset.sr; const hit = srAllRows().find(x => x.r.key === k); if (hit) { srSetValue(k, srDefaultOf(hit.r)); srRenderContent(); } return; }
    if (t.closest('#sr-save')) { srSave(); return; }
    if (t.closest('#sr-discard')) { srDiscard(); return; }
    const ha = t.closest('.sr-hdr-act[data-act]'); if (ha) { srHeaderAction(ha.dataset.act); return; }
    const ra = t.closest('.sr-act[data-act]'); if (ra) { srRowAction(ra.dataset.act); return; }
    const db = t.closest('.sr-danger-btn[data-danger]'); if (db && !db.disabled) { srDangerAction(db.dataset.danger); return; }
    if (t.closest('.sr-invite-toggle')) { SR.inviteOpen = !SR.inviteOpen; srRenderContent(); if (SR.inviteOpen) { const f = document.getElementById('sr-nu-user'); if (f) f.focus(); } return; }
    if (t.closest('.sr-invite-send')) { srInvite(); return; }
    const ua = t.closest('.sr-user-act[data-uact]'); if (ua) { const row = ua.closest('.sr-user'); srUserAction(row.dataset.user, ua.dataset.uact, row); return; }
    if (t.closest('.sr-raw-toggle')) { SR.rawOpen = !SR.rawOpen; srRenderContent(); return; }
    if (t.closest('.sr-raw-copy')) { srHeaderAction('copyreport'); return; }
    const at = t.closest('.sr-attest[data-sat]'); if (at) { const c = at.closest('.sr-check'); const note = (c.querySelector('.sr-attest-note') || {}).value || ''; srAttest(c.dataset.check, at.dataset.sat === '1', note.trim()); return; }
  });

  root.addEventListener('input', e => {
    const t = e.target;
    if (t.id === 'sr-search') { SR.query = t.value; srRenderRail(); srRenderContent(); return; }
    if (t.classList.contains('sr-confirm')) { SR.confirms[t.dataset.danger] = t.value; const btn = t.parentElement.querySelector('.sr-danger-btn'); if (btn) btn.disabled = t.value.trim() !== (SR.saved.colony_name || 'anthill'); return; }
    if (t.classList.contains('sr-input') && t.dataset.sr) {
      const k = t.dataset.sr;
      const v = t.type === 'number' ? (t.value === '' ? '' : Number(t.value)) : t.value;
      srSetValue(k, v);
    }
  });

  root.addEventListener('change', e => {
    const t = e.target;
    if (t.tagName === 'SELECT' && t.classList.contains('sr-input') && t.dataset.sr) { srSetValue(t.dataset.sr, t.value); return; }
    if (t.classList.contains('sr-user-role')) { const row = t.closest('.sr-user'); srUserAction(row.dataset.user, 'role', row); return; }
  });

  root.addEventListener('keydown', e => {
    const t = e.target;
    if (t.classList && t.classList.contains('sr-toggle') && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); t.click(); return; }
    if (t.classList && t.classList.contains('sr-item') && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); t.click(); return; }
    if (t.id === 'sr-search' && e.key === 'Escape') { t.value = ''; SR.query = ''; srRenderRail(); srRenderContent(); t.blur(); return; }
    if (t.id === 'sr-nu-pass' && e.key === 'Enter') { srInvite(); return; }
  });

  // Unsaved edits survive a nav-away only in memory; a refresh with dirty keys asks first.
  window.addEventListener('beforeunload', e => { if (srDirtyKeys().length) { e.preventDefault(); e.returnValue = ''; } });
}

/** `/` focuses the settings search while the page is open (called from app.js's global keydown). */
function settingsFocusSearch() { const s = document.getElementById('sr-search'); if (s) { s.focus(); s.select(); return true; } return false; }

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', srWire); else srWire();
