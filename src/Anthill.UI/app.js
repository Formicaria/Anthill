'use strict';

var uiReady = false;

// -- Config & API -------------------------------------------------------------
let TOKEN    = localStorage.getItem('anthill_token') || '';
let API_BASE = localStorage.getItem('anthill_apibase') || '';

function setToken(t)  { TOKEN = t;    localStorage.setItem('anthill_token', t); }
function setApiBase(u){ API_BASE = u; localStorage.setItem('anthill_apibase', u); }

function url(path){ return API_BASE ? API_BASE.replace(/\/$/,'') + path : path; }

// V2.2 Pass D — centralized API layer: auth-flood gate, in-flight GET deduplication, TTL cache
// with stale-while-revalidate, request timeouts, and 429 backoff. Root-cause fixes:
// - "Too many attempts": ~7 pollers each retried through a dead session; every 401 fed the
//   server's AuthLimiter. Now the FIRST 401 flips the auth-lost gate and every later call
//   short-circuits locally (zero network) until re-login clears it (enterApp sets authLost=false).
// - Slow Overview/Patch Center: identical GETs from parallel pollers now share one in-flight
//   promise and a per-path TTL cache; hidden tabs serve cache instead of hitting the API.
const API_TTL={'/status':10000,'/events/json':3000,'/jobs':5000,'/missions/json':10000,
  '/colony/registry':30000,'/pheromones/json':20000,'/autonomy/status':10000,'/objectives':15000,
  '/patches':30000,'/homelab/dashboard':10000,'/homelab/summary':10000,'/homelab/approvals/unified':10000,
  '/system/summary':15000,'/update/check':300000,'/homelab/integrations':15000};
const _apiCache=new Map(), _apiInflight=new Map(); let _api429Until=0;
function _ttlFor(path){ const base=path.split('?')[0]; let best=5000;
  for(const k in API_TTL){ if(base===k||base.startsWith(k)){ best=API_TTL[k]; } } return best; }
function apiCacheBust(prefix){ for(const k of Array.from(_apiCache.keys())) if(!prefix||k.startsWith(prefix)) _apiCache.delete(k); }

// ---- live event stream (v3.8.3) -------------------------------------------
// The colony now pushes. This is the first half of retiring the pollers: rather than rewriting
// every /events/json caller at once, an arriving event invalidates the cached copy so the very
// next read is fresh instead of serving something up to three seconds old. Panels keep their
// existing refresh logic and simply stop showing stale data. Phase 6 replaces the polling itself.
//
// fetch() rather than EventSource, deliberately. EventSource cannot set request headers, so
// adopting it would have meant accepting the auth token as a query parameter — putting a live
// credential into proxy logs, browser history and Referer headers to save a few lines of parsing.
// Streaming the response by hand keeps the Authorization header exactly where every other call
// puts it.
const _evtSubs = new Set();
let _evtCtl = null, _evtBackoff = 1000;

function onColonyEvent(fn){ _evtSubs.add(fn); return () => _evtSubs.delete(fn); }

function _evtDispatch(ev){
  apiCacheBust('/events/json');
  // Mission-shaped events change more than the log: the mission list, the graph, the status line.
  if(ev.event_type && (ev.event_type.startsWith('mission_') || ev.event_type.startsWith('task_'))){
    apiCacheBust('/missions/json'); apiCacheBust('/status');
  }
  for(const fn of _evtSubs){ try{ fn(ev); }catch(e){ console.warn('event subscriber failed', e); } }
}

async function startEventStream(){
  if(_evtCtl) return;                                   // already connected
  if(typeof authLost!=='undefined' && authLost) return; // logged out: do not touch the network
  _evtCtl = new AbortController();
  try{
    const r = await fetch(url('/events/stream'), {
      headers: { 'Authorization': 'Bearer '+TOKEN, 'Accept': 'text/event-stream' },
      signal: _evtCtl.signal,
    });
    if(!r.ok || !r.body) throw new Error('stream unavailable (HTTP '+r.status+')');
    _evtBackoff = 1000; // a successful connect resets the ladder
    const reader = r.body.getReader(), dec = new TextDecoder();
    let buf = '';
    for(;;){
      const { value, done } = await reader.read();
      if(done) break;
      buf += dec.decode(value, { stream:true });
      // Frames are separated by a blank line. Anything after the last one is a partial frame and
      // stays in the buffer — a chunk boundary can land mid-JSON, and parsing early throws.
      const frames = buf.split('\n\n'); buf = frames.pop();
      for(const frame of frames){
        const line = frame.split('\n').find(l => l.startsWith('data:'));
        if(!line) continue;   // ':' comment — the heartbeat
        try{ _evtDispatch(JSON.parse(line.slice(5).trim())); }catch{ /* ignore a malformed frame */ }
      }
    }
  }catch(e){
    if(_evtCtl && _evtCtl.signal.aborted) return;  // deliberate teardown, not a failure
  }finally{
    _evtCtl = null;
  }
  // Reconnect with backoff. The stream is an optimisation over polling, never a dependency of it:
  // if it never comes back, every panel still works exactly as it did before this existed.
  _evtBackoff = Math.min(_evtBackoff * 2, 30000);
  setTimeout(startEventStream, _evtBackoff);
}

function stopEventStream(){ if(_evtCtl){ const c=_evtCtl; _evtCtl=null; c.abort(); } }

/**
 * v0.3.8.52 — the second half of retiring the pollers, which v3.8.3 named and left undone.
 *
 * The stream shipped, `onColonyEvent` shipped, and NOTHING EVER SUBSCRIBED. Grep it before this
 * release: one definition, zero callers. So the whole live-push mechanism did exactly one thing —
 * bust the GET cache — while every panel kept its 2.5-to-6-second timer. The colony pushed and the
 * console still asked.
 *
 * `liveRefresh` closes that. A panel names the events it cares about and an IDLE interval, and gets:
 *
 *   - a refresh within `coalesceMs` of a matching event arriving, instead of up to `idleMs` later;
 *   - a timer at `idleMs`, which is now a SAFETY NET rather than the mechanism.
 *
 * The interval is kept, deliberately, and this is the design decision worth stating plainly rather
 * than quietly reversing. app.js's own stream comment promises "the stream is an optimisation over
 * polling, never a dependency of it: if it never comes back, every panel still works exactly as it
 * did before", and the UI README calls the pollers a deliberate safety net. Deleting the timers
 * would make a dropped SSE connection indistinguishable from a dead colony. So the fast timers are
 * retired — 2.5s becomes 20s, 4s becomes 30s — and correctness under a lost stream is preserved.
 * The measurable win is real: the idle console makes roughly a fifth of the requests it used to,
 * and reacts sooner when something actually happens.
 *
 * Coalescing matters more than it looks. A mission fan-out emits task events in bursts of dozens
 * inside a second; refreshing per event would replace steady polling with something worse. One
 * trailing refresh per burst is what the panel actually needs.
 */
function liveRefresh(fn, opts){
  const idleMs = opts.idleMs;
  const match = opts.on;                       // (eventType) => boolean; omitted means every event
  const coalesceMs = opts.coalesceMs || 250;
  let pending = null;

  setInterval(fn, idleMs);

  onColonyEvent(ev => {
    const type = (ev && ev.event_type) || '';
    if(match && !match(type)) return;
    if(pending) return;                        // a refresh is already scheduled for this burst
    pending = setTimeout(() => { pending = null; try{ fn(); }catch(e){ console.warn('live refresh failed', e); } }, coalesceMs);
  });
}

// The event families the panels below key off. Named once: a panel that tests `event_type` inline
// is a panel whose refresh trigger silently stops matching when an event is renamed.
const EVT_MISSION = t => t.startsWith('mission_') || t.startsWith('task_');
const EVT_APPROVAL = t => t.startsWith('approval_') || t.startsWith('patch_');
const EVT_JOB = t => t.startsWith('job_') || t.startsWith('mission_');

// v0.3.8.46: timeoutMs is a parameter because one number cannot serve every endpoint — the plan
// preview runs a real planner through a routed model and 10s guaranteed a timeout for any agent
// CLI provider. Callers that expect slow, honest work say so; everyone else keeps the default.
async function api(path, method='GET', body=null, timeoutMs=10000) {
  const isGet=method==='GET'&&!body;
  // Auth gate: while logged out, only auth endpoints may touch the network.
  if(typeof authLost!=='undefined'&&authLost&&!path.startsWith('/auth'))
    return { success:false, message:'Not signed in.', status:401 };
  const now=Date.now();
  if(isGet){
    const hit=_apiCache.get(path);
    if(hit&&(now-hit.at<_ttlFor(path)||document.hidden||now<_api429Until)) return hit.data; // fresh, hidden-tab, or backoff → cached
    if(_apiInflight.has(path)) return _apiInflight.get(path); // dedupe identical in-flight GETs
    if(now<_api429Until&&hit) return hit.data;
    if(now<_api429Until) return { success:false, message:'Rate limited — backing off.', status:429 };
  }
  const run=(async()=>{
    const ctl=new AbortController(); const timer=setTimeout(()=>ctl.abort(),timeoutMs);
    try{
      const opts = { method, headers: { 'Authorization': 'Bearer '+TOKEN, 'Content-Type': 'application/json' }, signal:ctl.signal };
      if (body) opts.body = JSON.stringify(body);
      const r = await fetch(url(path), opts);
      if (r.status === 401) { if(typeof onUnauthorized==='function') onUnauthorized(); throw new Error('unauthorized'); }
      if (r.status === 429) {
        const ra=parseInt(r.headers.get('Retry-After')||'15',10);
        _api429Until=Date.now()+Math.min(Math.max(ra,5),120)*1000; // respect Retry-After, clamp 5-120s
      }
      const text = await r.text();
      let out;
      if (!text) out={ success:false, message:`Empty response (HTTP ${r.status})${r.status===404?' — this build may be missing the endpoint; redeploy?':''}`, status:r.status };
      else { try { out=JSON.parse(text); } catch { out={ success:false, message:`Non-JSON response (HTTP ${r.status})`, status:r.status, raw:text.slice(0,200) }; } }
      if(isGet&&out&&out.success!==false) _apiCache.set(path,{at:Date.now(),data:out});
      return out;
    }catch(e){
      if(e&&e.message==='unauthorized') throw e;
      const stale=isGet?_apiCache.get(path):null;
      if(stale) return stale.data; // stale-while-error: keep the UI alive, refresh next poll
      return { success:false, message:(e&&e.name==='AbortError')?('Request timed out ('+Math.round(timeoutMs/1000)+'s).'):('Request failed: '+((e&&e.message)||'network error')), status:0 };
    }finally{ clearTimeout(timer); if(isGet)_apiInflight.delete(path); }
  })();
  if(isGet)_apiInflight.set(path,run);
  return run;
}
// Mutations must show fresh data next read: bust the GET cache after any successful POST/PUT/DELETE.
(function(){ const _m=api; window.api=async function(p,method='GET',body=null,timeoutMs=10000){
  const out=await _m(p,method,body,timeoutMs);
  if(method!=='GET'&&out&&out.success!==false) apiCacheBust('');
  return out; };})();
// Same auth gate for text endpoints (approvals/events text views) — no network while logged out.
(function(){ const _t=apiText; window.apiText=async function(p){
  if(typeof authLost!=='undefined'&&authLost) throw new Error('unauthenticated');
  return _t(p); };})();
async function apiText(path) {
  const r = await fetch(url(path), { headers: { 'Authorization': 'Bearer '+TOKEN } });
  if (r.status === 401) { if(typeof onUnauthorized==='function') onUnauthorized(); throw new Error('unauthorized'); }
  return r.text();
}

/**
 * Log a polling failure ONCE per distinct message, not once per tick.
 *
 * Found during the v3.7.1 browser sweep: the console held 4,478 errors, every one of them a
 * six-second poll re-reporting the same "unauthenticated" or "Failed to fetch" it reported last
 * time. Those two are the NORMAL state while logged out or while the API restarts, so the loudest
 * thing in the console was the thing that mattered least — and a real error arriving in that stream
 * is invisible. Repeats are counted and reported when the state finally changes, so nothing is lost.
 */
var _pollLastErr = {};
function pollWarnOnce(scope, err){
  var msg = (err && err.message) || String(err);
  var prev = _pollLastErr[scope];
  if(prev && prev.msg === msg){ prev.n++; return; }
  if(prev && prev.n > 0) console.warn(scope+': previous error repeated '+prev.n+'× ('+prev.msg+')');
  _pollLastErr[scope] = { msg: msg, n: 0 };
  console.error(scope, err);
}

// -- Authentication ------------------------------------------------------------
const setupEl = document.getElementById('setup-overlay');
let ROLE = localStorage.getItem('anthill_role') || '';
let USERNAME = localStorage.getItem('anthill_user') || '';
let pollingStarted = false;

function setSession(token, role, username){
  setToken(token); ROLE=role; USERNAME=username;
  localStorage.setItem('anthill_role', role||'');
  localStorage.setItem('anthill_user', username||'');
}
function clearSession(){
  TOKEN=''; ROLE=''; USERNAME='';
  ['anthill_token','anthill_role','anthill_user'].forEach(k=>localStorage.removeItem(k));
}

async function rawPost(path, body){
  const r = await fetch(url(path), { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body) });
  return r.json();
}

// -- In-app confirm / prompt --------------------------------------------------
// Promise-based replacements for native window.confirm()/prompt(). The native dialogs block the
// renderer's main thread until dismissed — which hung the Autonomy Stop button (and froze the page
// under automation) — and look out of place in the HUD. These are non-blocking, themed, and
// keyboard-navigable (Enter=confirm, Esc/backdrop=cancel).
function uiConfirm(message, opts){
  opts = opts || {};
  return new Promise(resolve=>{
    const ov=document.createElement('div');
    ov.className='ui-modal-ov';
    ov.innerHTML=`<div class="ui-modal" role="dialog" aria-modal="true">
      <div class="ui-modal-title">${escapeHtml(opts.title||'Confirm')}</div>
      <div class="ui-modal-msg">${escapeHtml(message||'')}</div>
      <div class="ui-modal-actions">
        <button class="btn btn-ghost" data-act="cancel">${escapeHtml(opts.cancel||'Cancel')}</button>
        <button class="btn ${opts.danger?'btn-danger':'btn-primary'}" data-act="ok">${escapeHtml(opts.ok||'Confirm')}</button>
      </div></div>`;
    const done=(v)=>{ document.removeEventListener('keydown',onKey); ov.remove(); resolve(v); };
    const onKey=(e)=>{ if(e.key==='Escape'){e.preventDefault();done(false);} else if(e.key==='Enter'){e.preventDefault();done(true);} };
    ov.addEventListener('click',e=>{ if(e.target===ov){done(false);return;} const a=e.target.closest('[data-act]'); if(a)done(a.dataset.act==='ok'); });
    document.addEventListener('keydown',onKey);
    document.body.appendChild(ov);
    const okb=ov.querySelector('[data-act="ok"]'); if(okb)okb.focus();
  });
}
function uiPrompt(message, opts){
  opts = opts || {};
  return new Promise(resolve=>{
    const ov=document.createElement('div');
    ov.className='ui-modal-ov';
    ov.innerHTML=`<div class="ui-modal" role="dialog" aria-modal="true">
      <div class="ui-modal-title">${escapeHtml(opts.title||'Input')}</div>
      <div class="ui-modal-msg">${escapeHtml(message||'')}</div>
      <input class="form-input ui-modal-input" type="${opts.password?'password':'text'}" style="width:100%;margin-top:10px;font-family:var(--mono);" />
      <div class="ui-modal-actions">
        <button class="btn btn-ghost" data-act="cancel">Cancel</button>
        <button class="btn btn-primary" data-act="ok">OK</button>
      </div></div>`;
    const inp=ov.querySelector('.ui-modal-input');
    if(opts.value) inp.value=opts.value; // v2.5.4 R4: prefill for edit-in-place flows
    const done=(v)=>{ document.removeEventListener('keydown',onKey); ov.remove(); resolve(v); };
    const onKey=(e)=>{ if(e.key==='Escape'){e.preventDefault();done(null);} };
    ov.addEventListener('click',e=>{ if(e.target===ov){done(null);return;} const a=e.target.closest('[data-act]'); if(a)done(a.dataset.act==='ok'?(inp.value||''):null); });
    inp.addEventListener('keydown',e=>{ if(e.key==='Enter'){e.preventDefault();done(inp.value||'');} });
    document.addEventListener('keydown',onKey);
    document.body.appendChild(ov);
    inp.focus();
  });
}

function showAuth(mode, err){
  document.getElementById('app-shell').classList.add('hidden');
  setupEl.style.display='flex';
  document.getElementById('auth-login').style.display = mode==='login'?'block':'none';
  document.getElementById('auth-setup').style.display = mode==='setup'?'block':'none';
  const el = document.getElementById(mode==='setup'?'setup-err-msg':'auth-err');
  if(el) el.textContent = err||'';
}

let authLost=false;
function onUnauthorized(){
  authLost=true;
  clearSession();
  // Drop the stream too. Left running, its reconnect ladder would keep hitting the API with a dead
  // token — exactly the 401 storm the auth-lost gate above exists to prevent.
  stopEventStream();
  // Re-assert the login screen on ANY 401 while the app shell is still showing — not just the first.
  // The old early-return meant that if a session went invalid mid-flight (e.g. the server rotating its
  // session secret during a redeploy), the page could stay stuck half-loaded behind failing background
  // polls instead of bouncing to login. Guarding on the shell's visibility redirects reliably without
  // re-running showAuth once we're already on the login view.
  const shell=document.getElementById('app-shell');
  if(shell && !shell.classList.contains('hidden')) showAuth('login','Session expired — please sign in again.');
}

async function bootAuth(){
  let status;
  try { status = await (await fetch(url('/auth/status'))).json(); }
  catch(e){ showAuth('login','Cannot reach API. Check the API Base URL and try again.'); return; }
  if(status?.data?.setup_required){
    // v0.3.8.91: the token row appears only when the API says one is needed, so the loopback and
    // desktop cases keep the two-field form they have always had.
    const row=document.getElementById('setup-token-row');
    if(row) row.style.display = status?.data?.setup_token_required ? '' : 'none';
    showAuth('setup'); return;
  }
  if(TOKEN){
    try{
      const me=await api('/auth/me');
      if(me.success){ setSession(TOKEN, me.data.role, me.data.username); enterApp(); return; }
    } catch{ /* fall through */ }
  }
  showAuth('login');
}

function enterApp(){
  authLost=false;
  setupEl.style.display='none';
  document.getElementById('app-shell').classList.remove('hidden');
  applyRoleVisibility();
  if(!pollingStarted){ pollingStarted=true; startPolling(); }
  startEventStream();   // v0.3.8.52: live push now DRIVES the panels; the timers are the fallback
  // Restore nav collapse state
  if(localStorage.getItem('nav-collapsed')==='1') document.body.classList.add('nav-collapsed');
  applyNarrowNav();
}

/**
 * v3.8.34: collapse the sidebar on its own when the viewport cannot afford it.
 *
 * `#nav-rail` was a flat `width:var(--nav-w)` — 240px at every size. The console has seven narrow
 * breakpoints and not one of them touched the rail, so at 760px it held roughly a third of the
 * screen and the content it pushed aside got the rest. `.nav-collapsed` already styles exactly the
 * state that is wanted; nothing was asking for it.
 *
 * Driven from matchMedia rather than a media query so the EXISTING collapsed rules are reused. A
 * CSS breakpoint would have to restate the ten `.nav-collapsed` selectors, and two copies of one
 * appearance is how they come to disagree.
 *
 * The operator's stored choice is never written here. Narrow forces collapse; returning to a wide
 * viewport restores whatever they last chose deliberately — so resizing a window cannot silently
 * rewrite a preference the operator set on purpose.
 */
var _narrowNav = window.matchMedia ? window.matchMedia('(max-width: 900px)') : null;
function applyNarrowNav(){
  if(!_narrowNav) return;
  if(_narrowNav.matches) document.body.classList.add('nav-collapsed');
  else document.body.classList.toggle('nav-collapsed', localStorage.getItem('nav-collapsed')==='1');
}
if(_narrowNav){
  // addEventListener on MediaQueryList is the modern form; addListener is the fallback that keeps
  // this working on older WebKit, which is a real target for a console opened from a NAS or phone.
  if(_narrowNav.addEventListener) _narrowNav.addEventListener('change', applyNarrowNav);
  else if(_narrowNav.addListener) _narrowNav.addListener(applyNarrowNav);
}

// Login
document.getElementById('login-btn').addEventListener('click', doLogin);
document.getElementById('login-pass').addEventListener('keydown',e=>{ if(e.key==='Enter') doLogin(); });
document.getElementById('login-user').addEventListener('keydown',e=>{ if(e.key==='Enter') document.getElementById('login-pass').focus(); });

async function doLogin(){
  const u=document.getElementById('login-user').value.trim();
  const p=document.getElementById('login-pass').value;
  const base=document.getElementById('apibase-input').value.trim();
  const err=document.getElementById('auth-err'); err.textContent='';
  if(!u||!p){ err.textContent='Enter your username and password.'; return; }
  setApiBase(base||'');
  try{
    const r=await rawPost('/auth/login',{username:u,password:p});
    if(r.success){ document.getElementById('login-pass').value=''; setSession(r.data.token,r.data.role,r.data.username); enterApp(); }
    else err.textContent=r.message||'Login failed.';
  }catch(e){ err.textContent='Cannot reach API: '+e.message; }
}

// First-run setup
document.getElementById('setup-btn').addEventListener('click', doSetup);
document.getElementById('setup-pass2').addEventListener('keydown',e=>{ if(e.key==='Enter') doSetup(); });

async function doSetup(){
  const u=document.getElementById('setup-user').value.trim()||'admin';
  const p=document.getElementById('setup-pass').value;
  const p2=document.getElementById('setup-pass2').value;
  const base=document.getElementById('setup-apibase').value.trim();
  const err=document.getElementById('setup-err-msg'); err.textContent='';
  if(p.length<8){ err.textContent='Password must be at least 8 characters.'; return; }
  if(p!==p2){ err.textContent='Passwords do not match.'; return; }
  setApiBase(base||'');
  const tok=(document.getElementById('setup-token')||{}).value;
  try{
    const r=await rawPost('/auth/setup',{username:u,password:p,setup_token:(tok||'').trim()});
    if(r.success){ setSession(r.data.token,r.data.role,r.data.username); enterApp(); }
    else err.textContent=r.message||'Setup failed.';
  }catch(e){ err.textContent='Cannot reach API: '+e.message; }
}

document.getElementById('logout-btn').addEventListener('click', async ()=>{
  try{ await api('/auth/logout','POST'); }catch{}
  clearSession(); location.reload();
});

// Role visibility: admins see Config section; coordinators see only colony + missions + events
function applyRoleVisibility(){
  const admin = ROLE==='admin';
  // v1.10.0: the Homelab page is visible to admins and homelab operators; write forms are admin-only
  // (manage_homelab_integrations is not granted to the homelab_operator role).
  // v2.6: the sidebar is config-driven; per-item visibility (all|admin|hl) is applied in buildNav().
  if(typeof buildNav==='function') buildNav();
  ['hl-host-form','hl-svc-form','hl-dep-form','hl-import-btn','hl-check-form','hl-pve-sync','hl-dev-form','hl-risk-analyze','hl-inc-form','hl-inc-resolve'].forEach(id=>{
    const el=document.getElementById(id); if(el) el.style.display=admin?'':'none';
  });
  // approval bell
  const bell=document.getElementById('approval-bell');
  if(bell) bell.style.display=admin?'':'none';
  // approvals card on colony page
  if(!admin){ const ac=document.getElementById('approvals-card'); if(ac) ac.style.display='none'; }
  // User chip in nav footer
  const nameEl=document.getElementById('nav-user-name');
  const roleEl=document.getElementById('nav-user-role');
  if(nameEl) nameEl.textContent=USERNAME||'?';
  if(roleEl) roleEl.textContent=admin?'Administrator':(ROLE==='homelab_operator'?'Homelab Operator':'Coordinator');
  const avatarEl=document.getElementById('nav-user-avatar');
  if(avatarEl){
    const initials=(USERNAME||'AH').substring(0,2).toUpperCase();
    avatarEl.textContent=initials;
    avatarEl.style.background=admin
      ?'linear-gradient(135deg,var(--queen),var(--queen-deep))'
      :'linear-gradient(135deg,#3b82f6,#6366f1)';
  }
}

// -- Page Routing (v2.6 Console Redesign — IA + hash routing layer; docs/CONSOLE_REDESIGN.md) --
// Internal page ids are UNCHANGED (every existing caller of showPage(id) keeps working). The new
// enterprise IA is a presentation + routing layer on top: a config-driven grouped sidebar, real
// deep-linkable routes, breadcrumbs, and contextual sub-navigation for the grouped domains.
const PAGE_TITLES = {
  overview:'Dashboard', colony:'Topology', missions:'Missions', results:'Mission Results',
  patches:'Changes & Approvals', objboard:'Objectives', antobs:'Roles', events:'Events',
  activity:'Activity', pheromones:'Memory & Signals', homelab:'Infrastructure', antconfig:'Roles',
  autonomy:'Automation', security:'Security', shell:'Terminal', settings:'Settings', users:'Users',
  chat:'Chat', projects:'Projects', toolsview:'Tools',
  readiness:'Readiness', projectview:'Project', integrations:'Integrations'
};
const PAGE_ENTER = {};  // registered per-page onEnter callbacks (set later in script)
PAGE_ENTER['overview']=()=>{
  if(typeof pollHealth==='function') pollHealth();
  if(typeof pollHud==='function') pollHud();
  // v2.14.13: with the workspace on, the dashboard owns the topology. Without it, this is a no-op
  // and the Colony page keeps the canvas exactly as before.
  if(document.getElementById('ws-topology')) topologyMountTo('dashboard');
  // v0.3.8.55: the hosted topology gets the same wake-up the dedicated page gets — legend and
  // pheromone HUD used to load only through showPage('colony'). Host-agnostic on purpose: the
  // canvas can live in the ws workspace OR a dashboard-grid widget, and the wake follows the
  // CANVAS, not the host type.
  setTimeout(()=>{
    const area=document.getElementById('colony-canvas-area');
    if(area && area.closest('#page-overview')){
      if(typeof renderColonyLegend==='function') renderColonyLegend();
      if(typeof pollColonyPheromones==='function') pollColonyPheromones();
    }
  },80);
  // v0.3.8.55: the Director widget ships visible by default — fill it on entry, not first poll.
  if(typeof pollDirectorWidget==='function') pollDirectorWidget();
};
// The Colony page reclaims the canvas only when the dashboard is NOT hosting it. With the
// workspace live, showPage() has already redirected /colony to the dashboard, so this never runs
// and the topology stays mounted in one place for the whole session.
PAGE_ENTER['colony']=()=>{ if(!workspaceHostsTopology()) topologyMountTo('colony'); };
PAGE_ENTER['chat']=()=>{ if(typeof loadChat==='function') loadChat();
  // v0.3.8.42: re-entering Chat restores the colony layer's mount if it was open when the
  // operator left — the Colony page legitimately reclaims the canvas on its own entry, so Chat
  // must reclaim it on its. chatColonyOpen is declared later in the script; page entry only ever
  // fires after the whole script has run.
  if(typeof chatColonyOpen!=='undefined'&&chatColonyOpen&&typeof topologyMountTo==='function') topologyMountTo('chat');
  if(typeof lastAgentCatalog!=='undefined'&&!lastAgentCatalog && typeof refreshAgentCatalog==='function') refreshAgentCatalog();
  document.getElementById('chat-input')?.focus(); };

// Domain icons (reuse the pre-redesign nav glyph set).
const IAICON = {
  // v3.8.39: without an entry here buildNav writes the string "undefined" into the nav icon,
  // because it interpolates IAICON[d.id] unguarded.
  chat:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 11.5a8.4 8.4 0 0 1-9 8.4 8.9 8.9 0 0 1-4-.9L3 21l1.9-4.6A8.4 8.4 0 0 1 12 3.1a8.4 8.4 0 0 1 9 8.4z"/></svg>',
  projects:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></svg>',
  tools:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14.7 6.3a4 4 0 0 0 5 5l-9.4 9.4a2.1 2.1 0 0 1-3-3z"/></svg>',
  // v0.3.8.42 (§7): the monitoring icon retired with its domain.
  // v0.3.8.41: a target, not a clock. The clock was the tell — see the IA entry below.
  objectives:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="4"/><circle cx="12" cy="12" r="1"/></svg>',
  dashboard:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/></svg>',
  operations:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><circle cx="12" cy="12" r="6"/><circle cx="12" cy="12" r="2"/></svg>',
  infrastructure:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="3" width="20" height="7" rx="2"/><rect x="2" y="14" width="20" height="7" rx="2"/><line x1="6" y1="6.5" x2="6.01" y2="6.5"/><line x1="6" y1="17.5" x2="6.01" y2="17.5"/></svg>',
  colony:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="5" r="2"/><circle cx="5" cy="19" r="2"/><circle cx="19" cy="19" r="2"/><line x1="12" y1="7" x2="5" y2="17"/><line x1="12" y1="7" x2="19" y2="17"/></svg>',
  security:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/></svg>',
  // v0.3.8.48: the two new top-level ids. The old domain icons above stay harmlessly unused
  // until the next cleanup pass; an id WITHOUT an entry writes "undefined" into the rail.
  integrations:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 3v4a2 2 0 0 1-2 2H3M15 21v-4a2 2 0 0 1 2-2h4M21 9h-4a2 2 0 0 1-2-2V3M3 15h4a2 2 0 0 1 2 2v4"/></svg>',
  settings:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.07 4.93l-1.41 1.41M4.93 4.93l1.41 1.41M19.07 19.07l-1.41-1.41M4.93 19.07l1.41-1.41M12 2v2M12 20v2M2 12h2M20 12h2"/></svg>',
  administration:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.07 4.93l-1.41 1.41M4.93 4.93l1.41 1.41M19.07 19.07l-1.41-1.41M4.93 19.07l1.41-1.41M12 2v2M12 20v2M2 12h2M20 12h2"/></svg>'
};

// The information architecture. vis: 'all' | 'admin' | 'hl' (admin OR homelab_operator).
//
// v0.3.8.49 — the five-destination product IA (UI/UX pass §20). Colony, Projects, Chat, Tools,
// Settings. Everything that describes how a Colony's ants work — the visualization, roles, models,
// model routing, the inspector, automation — now lives UNDER Colony, where an operator forms the
// mental model of their colony without detouring through global settings. Objectives folded into
// Projects (the project workspace owns its Objectives tab); Integrations folded into Tools;
// Providers/Model-Routing moved from Settings into Colony; the standalone Dashboard is Colony's
// own Overview. Every route the restructure removed resolves through ROUTE_ALIAS below, so
// bookmarks and deep links keep working.
const IA = [
  { type:'domain', id:'colony', label:'Colony', vis:'all', sections:[
    // The living colony — topology, ant activity, pheromone signals, mission state. This is the
    // page the old standalone Dashboard became: one place for the colony at a glance.
    { label:'Overview', route:'/colony', page:'overview', vis:'all' },
    // v0.3.8.55 (field report): Models & Routing merged INTO the Ant Inspector — one box per
    // role carries route, gates, telemetry and profile; the colony-wide priority and the
    // orchestration roles (planner, conversation) sit above the grid on the same page.
    { label:'Ant Inspector', route:'/colony/inspector', page:'antobs', vis:'admin' },
    // v0.3.8.55 (field report): Automation moved into Projects — the Director's backlog is
    // project work, and it now lives beside the projects it feeds (right-hand column there).
  ]},
  { type:'item', id:'projects', label:'Projects', route:'/projects', page:'projects', vis:'all' },
  { type:'item', id:'chat', label:'Chat', route:'/chat', page:'chat', vis:'all' },
  { type:'domain', id:'tools', label:'Tools', vis:'all', sections:[
    { label:'Capabilities', route:'/tools', page:'toolsview', vis:'all' },
    // Integrations and external services are tools — they live where the tools do (§9).
    { label:'Integrations', route:'/tools/integrations', page:'integrations', vis:'admin' },
    // v0.3.8.56 (field report): the standalone Providers destination is GONE — provider keys are
    // configured inline per integration (Tools → Integrations → Configure), and one surface for
    // one job beats two. /tools/providers aliases to Integrations.
    // The colony's memory and learning signals belong beside the capabilities that use them.
    { label:'Memory & Signals', route:'/tools/memory', page:'pheromones', vis:'admin' },
  ]},
  { type:'domain', id:'settings', label:'Settings', vis:'admin', sections:[
    { label:'General', route:'/settings/general', page:'settings', vis:'admin' },
    // Security owns the capability/approval gates (§13) — one authoritative gate system. The gate
    // DEFINITION lives here; the approval INTERACTION happens in Chat.
    { label:'Security & Gates', route:'/settings/security', page:'security', vis:'admin' },
    { label:'Users', route:'/settings/users', page:'users', vis:'admin' },
    { label:'System', route:'/settings/system', page:'events', vis:'admin' },
    { label:'Readiness', route:'/settings/readiness', page:'readiness', vis:'admin' },
    { label:'Terminal', route:'/settings/terminal', page:'shell', vis:'admin' },
  ]},
];

// Derived: route -> descriptor, page -> canonical home route, domain -> first-section route.
const ROUTE_TABLE = {};
const PAGE_HOME = {};
const DOMAIN_HOME = {};
(function buildRoutes(){
  for(const d of IA){
    if(d.type==='item'){
      ROUTE_TABLE[d.route]={page:d.page,hlsub:null,vis:d.vis||'all',crumb:[d.label],domain:d.id,section:null,tabs:null,activeTab:null};
      DOMAIN_HOME[d.id]=d.route;
      if(!PAGE_HOME[d.page]) PAGE_HOME[d.page]=d.route;
      continue;
    }
    if(d.sections&&d.sections.length&&!DOMAIN_HOME[d.id]) DOMAIN_HOME[d.id]=d.sections[0].route;
    for(const s of (d.sections||[])){
      const tabs=s.tabs?s.tabs.map(t=>({label:t.label,route:t.route,vis:t.vis||s.vis||d.vis||'all'})):null;
      ROUTE_TABLE[s.route]={page:s.page,hlsub:s.hlsub||null,view:s.view||null,stab:s.stab||null,vis:s.vis||d.vis||'all',crumb:[d.label,s.label],domain:d.id,section:s.route,tabs,activeTab:tabs?s.route:null};
      if(!PAGE_HOME[s.page]) PAGE_HOME[s.page]=s.route;
      if(s.tabs) for(const t of s.tabs){
        ROUTE_TABLE[t.route]={page:t.page,hlsub:t.hlsub||null,view:t.view||null,stab:t.stab||null,vis:t.vis||s.vis||d.vis||'all',crumb:[d.label,s.label,t.label],domain:d.id,section:s.route,tabs,activeTab:t.route};
        if(!PAGE_HOME[t.page]) PAGE_HOME[t.page]=t.route;
      }
    }
  }
})();
// Deterministic canonical home per page (first-occurrence is ambiguous for shared pages like homelab).
Object.assign(PAGE_HOME,{
  overview:'/colony', colony:'/colony', missions:'/projects',
  activity:'/settings/system',
  results:'/projects', events:'/settings/system',
  patches:'/chat', objboard:'/projects',
  pheromones:'/tools/memory', homelab:'/tools/integrations',
  antconfig:'/colony/inspector', antobs:'/colony/inspector',
  autonomy:'/projects', security:'/settings/security',
  shell:'/settings/terminal', settings:'/settings/general', users:'/settings/users',
  integrations:'/tools/integrations', projectview:'/projects', readiness:'/settings/readiness'
});
// Homelab in-page sub-nav (data-sub) → the canonical route that owns that sub-page, so the
// existing #hl-subnav buttons drive breadcrumbs / sidebar / URL through the router (v2.6 Phase 2).
const HLSUB_ROUTE={
  overview:'/infrastructure/overview', virtualization:'/infrastructure/compute',
  containers:'/infrastructure/containers', storage:'/infrastructure/storage',
  networking:'/infrastructure/network', services:'/infrastructure/services',
  monitoring:'/infrastructure/health', automation:'/infrastructure/automation',
  apps:'/infrastructure/apps', alerts:'/infrastructure/alerts', activity:'/infrastructure/activity'
};
// Legacy hash → new route (URL migration; §9 of the proposal).
const LEGACY_REDIRECT={
  overview:'/colony', colony:'/colony', missions:'/projects',
  events:'/settings/system', results:'/projects',
  patches:'/chat', objboard:'/projects',
  pheromones:'/tools/memory', homelab:'/tools/integrations',
  antconfig:'/colony/inspector', antobs:'/colony/inspector',
  autonomy:'/projects', security:'/settings/security',
  shell:'/settings/terminal', settings:'/settings/general', users:'/settings/users'
};
// v0.3.8.42 (§7): routes that MOVED when the Monitoring domain dissolved. Bookmarks and deep
// links keep working; the router resolves these before the table lookup.
const ROUTE_ALIAS={
  // v0.3.8.49 — every route earlier restructures removed, resolved to its new home in the
  // five-destination IA. Bookmarks are promises; these keep them.
  // Dashboard and Objectives lost their standalone destinations this pass.
  '/dashboard':'/colony',
  '/objectives':'/projects',
  '/integrations':'/tools/integrations',
  '/tools-view':'/tools',
  // v0.3.8.49: the standalone Ants & Roles tab folded into Models & Routing.
  '/colony/roles':'/colony/inspector',
  // v0.3.8.55: Models & Routing merged into the Ant Inspector.
  '/colony/model-routing':'/colony/inspector',
  '/colony/model-routing/providers':'/tools/integrations',
  // Roles / Inspector / Automation moved from Settings into Colony.
  '/settings/roles':'/colony/inspector',
  '/colony/topology':'/colony',
  '/colony/agents':'/colony/inspector',
  '/colony/agents/configure':'/colony/inspector',
  '/colony/agents/inspect':'/colony/inspector',
  '/colony/agents/coding':'/tools/integrations',
  '/colony/signals':'/tools/memory',
  '/administration/users':'/settings/users',
  '/administration/settings':'/settings/general',
  '/administration/terminal':'/settings/terminal',
  '/administration/readiness':'/settings/readiness',
  '/security/posture':'/settings/security',
  '/security/access':'/tools/integrations',
  '/infrastructure/overview':'/tools/integrations',
  '/infrastructure/compute':'/tools/integrations',
  '/infrastructure/containers':'/tools/integrations',
  '/infrastructure/storage':'/tools/integrations',
  '/infrastructure/network':'/tools/integrations',
  '/infrastructure/services':'/tools/integrations',
  '/infrastructure/health':'/tools/integrations',
  '/infrastructure/alerts':'/tools/integrations',
  '/infrastructure/activity':'/tools/integrations',
  '/infrastructure/automation':'/tools/integrations',
  '/infrastructure/apps':'/tools/integrations',
  '/operations/missions':'/projects',
  '/operations/missions/console':'/projects',
  '/operations/missions/history':'/projects',
  '/operations/missions/activity':'/settings/system',
  '/operations/missions/events':'/settings/system',
  '/operations/changes':'/chat',
  '/operations/approvals':'/chat',
  // v0.3.8.56: the Providers destination folded into Integrations' per-provider Configure.
  '/tools/providers':'/tools/integrations',
  // v0.3.8.55: Automation folded into Projects — every automation route lands there now.
  '/colony/automation':'/projects',
  '/operations/automation':'/projects',
  '/operations/automation/director':'/projects',
  '/operations/automation/objectives':'/projects',
  '/operations/automation/rules':'/tools/integrations',
  '/monitoring/activity':'/settings/system',
  '/monitoring/activity/events':'/settings/system',
  '/monitoring/activity/results':'/projects',
  '/monitoring/activity/changes':'/chat',
  '/monitoring/activity/runs':'/projects',
  '/monitoring/activity/infra':'/tools/integrations',
  '/monitoring/alerts':'/tools/integrations',
  '/scheduled':'/projects',
};

function canSee(vis){
  if(vis==='admin') return ROLE==='admin';
  if(vis==='hl') return ROLE==='admin'||ROLE==='homelab_operator';
  return true;
}

// Render the grouped sidebar from IA config into #nav-scroll (called on enter / role change).
// Enter/Space activate a div acting as a control (keyboard a11y for the config-driven nav).
function navKeyActivate(el,fn){
  el.addEventListener('keydown',e=>{ if(e.key==='Enter'||e.key===' '){ e.preventDefault(); fn(); } });
}
function buildNav(){
  const root=document.getElementById('nav-scroll'); if(!root) return;
  root.innerHTML='';
  // v0.3.8.49 (§8/§20 redo): EVERY primary destination is a flat nav-item — one style, one font,
  // one colour, no sidebar dropdowns. A domain (Colony, Tools, Settings) navigates to its home page
  // and its sub-sections render as in-page tabs (#domain-subnav), the way "just added pages" reads.
  // The old collapsible nav-domain/nav-child tree is gone: it was the source of the mismatched
  // typography and the tiny chevrons, and it duplicated navigation the content-area tabs already do.
  for(const d of IA){
    const isDomain=d.type==='domain';
    if(!isDomain && !canSee(d.vis)) continue;
    let route, domainId=null;
    if(isDomain){
      const sections=(d.sections||[]).filter(s=>canSee(s.vis||d.vis));
      if(!sections.length) continue;
      route=DOMAIN_HOME[d.id]||sections[0].route; domainId=d.id;
    } else {
      route=d.route;
    }
    const it=document.createElement('div');
    it.className='nav-item'; it.dataset.route=route;
    if(domainId) it.dataset.domain=domainId;
    it.setAttribute('role','link'); it.tabIndex=0; it.setAttribute('aria-label',d.label);
    it.innerHTML='<span class="nav-icon">'+(IAICON[d.id]||'')+'</span><span class="nav-label">'+escapeHtml(d.label)+'</span>';
    it.addEventListener('click',()=>go(route)); navKeyActivate(it,()=>go(route));
    root.appendChild(it);
  }
}

// Navigate to a route (nav clicks / tabs / router). push=false replaces history (back/forward, boot).
function go(route,push){
  if(ROUTE_ALIAS[route]) route=ROUTE_ALIAS[route];   // v0.3.8.42 (§7): moved routes stay reachable
  // v0.3.8.48: the one parameterised route — a project workspace. Deep-linkable, reload-safe.
  const pm=/^\/projects\/([A-Za-z0-9]+)$/.exec(route);
  if(pm){
    projectViewId=pm[1];
    showPage('projectview',{route:route,noHistory:true});
    try{ if(push===false) history.replaceState(null,'','#'+route); else history.pushState(null,'','#'+route); }catch{}
    return;
  }
  const r=ROUTE_TABLE[route]; if(!r) return;
  if(!canSee(r.vis)) return;
  showPage(r.page,{route:route,hlsub:r.hlsub,view:r.view,stab:r.stab,noHistory:true});
  try{ if(push===false) history.replaceState(null,'','#'+route); else history.pushState(null,'','#'+route); }catch{}
}

/**
 * v2.14.15: does the dashboard currently host the topology?
 *
 * Keyed off the layer actually existing rather than off the flag, so a workspace that failed to
 * initialise for any reason leaves the Colony route working exactly as before. This is the whole
 * safety property of the redirect below.
 */
function workspaceHostsTopology(){ return !!document.getElementById('ws-topology'); }

function showPage(id,o){
  o=o||{};
  // v2.14.15 (Stage 9 groundwork): with the workspace live, the Dashboard IS the colony view — it
  // holds the topology, the inspector, the jobs list, and the mission bar. Sending /colony there
  // is what makes the topology PERSISTENT instead of being yanked back and forth between two
  // hosts every time the operator navigates. With the workspace off this is a no-op.
  if(id==='colony' && workspaceHostsTopology()){
    id='overview';
    o=Object.assign({},o,{route:PAGE_HOME['overview']||o.route});
  }
  // v0.3.8.55: the standalone Automation page folded into Projects (right-hand column). Every
  // caller that still says showPage('autonomy') — attention chips, the command palette, the 'a'
  // shortcut — lands on Projects with the Director panel alive.
  if(id==='autonomy'){
    id='projects';
    o=Object.assign({},o,{route:'/projects'});
  }
  // v0.3.8.56: the hidden Coding Agents page folded into Integrations.
  if(id==='agentcli'){
    id='integrations';
    o=Object.assign({},o,{route:'/tools/integrations'});
  }
  // v0.3.8.55: Models & Routing merged into the Ant Inspector — same remap for old callers.
  if(id==='antconfig'){
    id='antobs';
    o=Object.assign({},o,{route:'/colony/inspector'});
  }
  try{ localStorage.setItem('last-page',id); }catch{} // reopen where you left off
  document.querySelectorAll('.page').forEach(p=>p.classList.remove('active'));
  const pg=document.getElementById('page-'+id);
  if(pg) pg.classList.add('active');
  const route=o.route||PAGE_HOME[id]||('/'+id);
  updateChrome(route,id);
  if(!o.noHistory){ try{ history.replaceState(null,'','#'+route); }catch{} }
  if(id==='homelab'){
    if(o.hlsub && typeof hlSubShow==='function') hlSubShow(o.hlsub,true); // chrome already set by go/showPage
    else if(typeof hlSubRestore==='function') hlSubRestore();             // reopen last sub + sync chrome
  }
  // v2.6 Phase 3: Patch Center splits into two route-driven views over the same list — Approvals
  // (pending queue) and Changes (full history). Purely presentational: drives the status filter,
  // no DOM moves. Set before PAGE_ENTER['patches'] so its loadPatches() uses the right filter.
  if(id==='patches'){
    const approvals=(o.view==='approvals');
    if(typeof pcFilters!=='undefined'){
      pcFilters.status=approvals?'pending':'';
      if(typeof applyPcFiltersToInputs==='function') applyPcFiltersToInputs();
    }
    const t=document.getElementById('pc-title'), sub=document.getElementById('pc-sub');
    if(t) t.textContent=approvals?'Approvals':'Changes';
    if(sub) sub.textContent=approvals
      ? 'Patch proposals awaiting your decision — nothing touches disk until you approve and apply.'
      : 'Every coder patch proposal — inspect, compare, approve, apply, and roll back. Full history.';
  }
  if(typeof PAGE_ENTER[id]==='function') PAGE_ENTER[id]();
  // v2.6 Phase 3: Colony → Model Routing opens the Settings page pre-switched to its Models/Providers
  // tab (route-driven, reuses the existing settings tab machinery). Administration → Settings with no
  // stab resets to the Connection tab so each route lands deterministically.
  if(id==='settings'){
    const isMR=(o.route||'').indexOf('/colony/model-routing')===0; // Colony → Model Routing view
    // v0.3.8.55 (field report): Providers moved to Tools → Providers. Its strip tab is GONE, so
    // the pane switches directly here — the tab-click machinery only works for tabs that exist.
    const isProv=(o.route||'').indexOf('/tools/providers')===0;
    const stab=o.stab||'connection';
    const tabEl=document.querySelector('.settings-tab[data-tab="'+stab+'"]');
    if(tabEl) tabEl.click();
    else if(stab==='providers'){
      document.querySelectorAll('.settings-tab').forEach(x=>x.classList.remove('active'));
      document.querySelectorAll('.settings-pane').forEach(x=>x.classList.remove('active'));
      document.getElementById('tab-providers')?.classList.add('active');
      if(typeof loadProvidersTab==='function') loadProvidersTab();
    }
    // Dedicated views hide the Settings tab strip and take their own header; plain Settings
    // keeps the strip and its own title.
    const strip=document.getElementById('settings-tabs');
    if(strip) strip.style.display=(isMR||isProv)?'none':'';
    const st=document.getElementById('set-title'), ss=document.getElementById('set-sub');
    if(st) st.textContent=isMR?'Model Routing':isProv?'Providers':'Settings';
    if(ss) ss.textContent=isMR
      ? 'Provider connections and per-role model routes for the colony.'
      : isProv
      ? 'External model providers — keys, connections, and their curated model catalogs.'
      : 'Colony configuration, model routes, and system diagnostics';
  }
  if(id==='colony') setTimeout(()=>{ resize(); buildNodes(); renderColonyLegend(); pollColonyPheromones(); },50);
  // v2.14.13: one layout read per navigation, so the render loop stops drawing a map that is
  // sitting inside a display:none page.
  setTimeout(refreshTopologyAwake,60);
}

// Breadcrumb + active-nav + contextual sub-nav for the current route.
function updateChrome(route,id){
  const r=ROUTE_TABLE[route];
  const cb=document.getElementById('hdr-crumbs');
  if(cb){
    const labels=(r&&r.crumb)?r.crumb.slice():[PAGE_TITLES[id]||id];
    const targets=[];
    if(r){
      targets.push(DOMAIN_HOME[r.domain]||null);            // domain crumb → its first section
      if(labels.length>=2) targets.push(r.section||route);  // section crumb → the section route
      if(labels.length>=3) targets.push(null);              // tab crumb is the current view
    }
    cb.innerHTML=labels.map((c,i)=>{
      const cur=i===labels.length-1;
      const t=targets[i];
      const link=(!cur&&t&&ROUTE_TABLE[t]&&canSee(ROUTE_TABLE[t].vis))?t:null;
      const attr=link?(' role="link" tabindex="0" data-onclick="go(\''+link+'\')"'):'';
      return '<span class="crumb'+(cur?' cur':'')+(link?' link':'')+'"'+attr+'>'+escapeHtml(c)+'</span>';
    }).join('<span class="crumb-sep">&#8250;</span>');
  }
  // v0.3.8.49 (§8 redo): highlight the ONE flat nav-item — by domain for a domain route, else by
  // route. There are no nav-children to clear any more.
  document.querySelectorAll('#nav-scroll .nav-item').forEach(n=>n.classList.remove('active'));
  const activeRoute=(r&&r.section)?r.section:route;
  let el=(r&&r.domain&&document.querySelector('#nav-scroll .nav-item[data-domain="'+r.domain+'"]'))
        ||document.querySelector('#nav-scroll .nav-item[data-route="'+activeRoute+'"]')
        ||document.querySelector('#nav-scroll .nav-item[data-route="'+route+'"]');
  if(el) el.classList.add('active');
  // The domain's sub-sections render as in-page tabs, not a sidebar dropdown. Built from the IA
  // sections of the current route's domain, so Colony/Tools/Settings each open with their own tab
  // strip and a plain item (Projects, Chat) shows none.
  const sn=document.getElementById('domain-subnav');
  if(sn){
    const dom=(r&&r.domain)?IA.find(x=>x.type==='domain'&&x.id===r.domain):null;
    const sections=dom?(dom.sections||[]).filter(s=>canSee(s.vis||dom.vis)):[];
    if(sections.length>1){
      const activeSection=(r&&r.section)||route;
      const html=sections.map(s=>'<button class="subnav-tab'+(s.route===activeSection?' active':'')+'" data-onclick="go(\''+s.route+'\')">'+escapeHtml(s.label)+'</button>').join('');
      sn.innerHTML=html; sn.style.display='flex';
    } else if(r&&r.tabs&&r.tabs.length){
      const active=r.activeTab||route;
      sn.innerHTML=r.tabs.filter(t=>canSee(t.vis||'all'))
        .map(t=>'<button class="subnav-tab'+(t.route===active?' active':'')+'" data-onclick="go(\''+t.route+'\')">'+escapeHtml(t.label)+'</button>').join('');
      sn.style.display='flex';
    } else { sn.innerHTML=''; sn.style.display='none'; }
  }
}

// Hash router: resolve #/domain/section (or a legacy #page) to a route. Returns true if it navigated.
function router(){
  let h=(location.hash||'').replace(/^#/,'');
  if(!h) return false;
  if(!h.startsWith('/') && LEGACY_REDIRECT[h]) h=LEGACY_REDIRECT[h];
  if(ROUTE_ALIAS[h]) h=ROUTE_ALIAS[h];   // v0.3.8.42 (§7): moved routes stay reachable
  if(/^\/projects\/[A-Za-z0-9]+$/.test(h)){ go(h,false); return; }
  const r=ROUTE_TABLE[h];
  if(r && canSee(r.vis)){ go(h,false); return true; }
  return false;
}
window.addEventListener('popstate',()=>router());

// Nav collapse toggle
document.getElementById('nav-collapse-btn').addEventListener('click',()=>{
  document.body.classList.toggle('nav-collapsed');
  localStorage.setItem('nav-collapsed', document.body.classList.contains('nav-collapsed')?'1':'0');
  setTimeout(()=>{ if(document.getElementById('page-colony')?.classList.contains('active')) resize(); },220);
});

// v0.3.8.42 (§3): the mission composers are gone — Chat is the one mission entry, and the three
// surfaces that used to compete with it (colony bar, Missions console, dashboard Mission Command)
// now point there. enableInput, the Play⇄Stop composer state and dispatchMission went with them;
// stopping a run lives on the jobs list (Cancel, durable) and in Chat itself.

// -- Settings overlay (now a page) --------------------------------------------
let settingsModelInfo = null;

function openSettings(){
  document.getElementById('si-username').textContent = USERNAME||'—';
  document.getElementById('si-role').textContent = ROLE==='admin'?'Administrator':(ROLE||'—');
  document.getElementById('settings-apibase').value = API_BASE;
  showPage('settings');
  loadSettingsInfo();
}

document.getElementById('settings-save').addEventListener('click', saveSettings);
async function saveSettings(){
  const base=document.getElementById('settings-apibase').value.trim();
  setApiBase(base);
  try{
    const r=await api('/status');
    if(r.success) setConnected(true);
  }catch{ setConnected(false); }
}

PAGE_ENTER['settings']=()=>{
  document.getElementById('si-username').textContent=USERNAME||'—';
  document.getElementById('si-role').textContent=ROLE==='admin'?'Administrator':(ROLE||'—');
  document.getElementById('settings-apibase').value=API_BASE;
  loadSettingsInfo();
};

// -- Canvas --------------------------------------------------------------------
const canvas = document.getElementById('c');
const ctx = canvas.getContext('2d');
let W, H, cx, cy;

function resize(){
  const el=document.getElementById('colony-canvas-area');
  if(!el) return;
  // Render at the display's real pixel density so the colony stays sharp on HiDPI screens:
  // backing store scaled by devicePixelRatio, all drawing kept in logical (CSS px) coordinates.
  const dpr=window.devicePixelRatio||1;
  W=el.clientWidth; H=el.clientHeight;
  canvas.width=Math.round(W*dpr); canvas.height=Math.round(H*dpr);
  canvas.style.width=W+'px'; canvas.style.height=H+'px';
  ctx.setTransform(dpr,0,0,dpr,0,0);
  cx=W/2; cy=H/2;
}
resize();
window.addEventListener('resize',()=>{ resize(); buildNodes(); refreshTopologyAwake(); });

let camX=0,camY=0,camZ=1,tX=0,tY=0,tZ=1;

const ROLE_COLORS={
  queen:'#fbbf24',director:'#f59e0b',planner:'#a78bfa',constraint:'#f43f5e',
  researcher:'#38bdf8',file:'#67e8f9',web:'#4ade80',coder:'#fb923c',
  ui_cartographer:'#2dd4bf',builder:'#c084fc',verifier:'#f87171',tester:'#60a5fa',
  soldier:'#ef4444',medic:'#34d399',archivist:'#facc15',quartermaster:'#94a3b8',scribe:'#e879f9'
};
let colonyRegistry=null,colonyView='command',showHandoffs=true;
let antRuntimeStatus={};   // v2.14.13: roleId -> /colony/registry runtime_status entry
const ANT_MAP={
  researcher:{ color:ROLE_COLORS.researcher, label:'ResearcherAnt', role:'CONTEXT' },
  web:       { color:ROLE_COLORS.web,        label:'WebAnt',        role:'EXTERNAL' },
  file:      { color:ROLE_COLORS.file,       label:'FileAnt',       role:'WORKSPACE' },
  coder:     { color:ROLE_COLORS.coder,      label:'CoderAnt',      role:'CODE' },
  builder:   { color:ROLE_COLORS.builder,    label:'BuilderAnt',    role:'OUTPUT' },
  verifier:  { color:ROLE_COLORS.verifier,   label:'VerifierAnt',   role:'VERIFY' },
};
const ANT_R=15, QUEEN_R=26;
const colonyActivity={ researcher:0, web:0, file:0, coder:0, builder:0, verifier:0 };
// v0.3.8.49 (§18) — discrete per-ant STATE, derived from the SAME real /graph task statuses that
// already drive `colonyActivity`. The scalar answers "how busy"; this answers "doing what", which
// is what the colony view has to communicate: idle, working, waiting, communicating, blocked,
// awaiting approval, completed, failed. Keyed by ant id AND worker id; filled in pollGraph().
const colonyAntState={};
// Ants currently emitting an ant-to-ant signal this frame (a live handoff edge). Rebuilt each poll.
let colonyCommunicating=new Set();
// True when the colony is blocked on a human decision — a pending approval. Ties the map to the
// Chat approval surface (§1): the ant that proposed the change reads as "awaiting approval".
let colonyAwaitingApproval=false;
// The colony state vocabulary and its colour, one source for the ring, the legend and the inspector.
const STATE_COLORS={
  working:'#34d399', waiting:'#fbbf24', communicating:'#22d3ee', blocked:'#fb923c',
  awaiting_approval:'#f59e0b', completed:'#60a5fa', failed:'#f87171', idle:'#5b6b7f',
};
const STATE_LABEL={
  working:'working', waiting:'waiting', communicating:'communicating', blocked:'blocked',
  awaiting_approval:'awaiting approval', completed:'completed', failed:'failed', idle:'idle',
};
// Higher wins when an ant holds several tasks at once: a blockage or an approval must surface over
// a concurrent running task, because those are the states an operator has to act on.
const STATE_RANK={ idle:0, completed:1, waiting:2, failed:3, working:4, communicating:5, blocked:6, awaiting_approval:7 };
function antTaskState(status){
  switch(status){
    case 'running': return 'working';
    case 'ready': case 'pending': return 'waiting';
    case 'blocked': return 'blocked';
    case 'failed': return 'failed';
    case 'complete': case 'completed': case 'succeeded': return 'completed';
    default: return 'idle';
  }
}
let totalModelCalls=0, modelCallRate=0, colonyRunning=false;
let nodes=[], edges=[], dataFlowEdges=[], particles=[], selectedNode=null, hoveredNode=null;

function prop(o,...names){for(const n of names){if(o&&Object.prototype.hasOwnProperty.call(o,n))return o[n];}return undefined;}
function roleId(r){return prop(r,'roleId','RoleId')||'';}
function roleName(r){return prop(r,'displayName','DisplayName')||roleId(r);}
function roleColony(r){return prop(r,'colony','Colony')||'Colony';}
function rolePurpose(r){return prop(r,'purpose','Purpose')||'';}
function roleEnabled(r){return prop(r,'enabled','Enabled')!==false;}
// v0.3.8.55 (field report: "6/12 roles false by default"): the registry's role RECORDS carry the
// STATIC Executable flag — false for every gated specialist, by design, forever — while the
// effective answer (static OR its gate is open) ships right beside them as executable_roles.
// This adapter read the record and told the operator six running roles were off. It now consults
// the effective list first; the record is only the fallback for a registry without one.
function roleExecutable(r){
  const id=roleId(r);
  const eff=colonyRegistry&&(colonyRegistry.executable_roles||colonyRegistry.ExecutableRoles);
  if(Array.isArray(eff)) return eff.some(x=>String(x).toLowerCase()===String(id).toLowerCase());
  return prop(r,'executable','Executable')===true;
}
function roleWorkers(r){return prop(r,'workers','Workers')||[];}
function rolePerms(r){return prop(r,'permissions','Permissions')||{};}
function roleAllowedTools(r){return prop(r,'allowedTools','AllowedTools')||[];}
function roleForbiddenTools(r){return prop(r,'forbiddenTools','ForbiddenTools')||[];}
function roleAllowedPaths(r){return prop(r,'allowedPaths','AllowedPaths')||[];}
function roleForbiddenPaths(r){return prop(r,'forbiddenPaths','ForbiddenPaths')||[];}
function workerId(w){return prop(w,'workerId','WorkerId')||'';}
function workerName(w){return prop(w,'displayName','DisplayName')||workerId(w);}
function workerPurpose(w){return prop(w,'purpose','Purpose')||'';}
function workerPerms(w){return prop(w,'permissions','Permissions')||{};}
function permList(p){
  const map=[['ReadWorkspace','read workspace'],['WriteWorkspace','write workspace'],['ReadMemory','read memory'],['WriteMemory','write memory'],['UseWeb','web'],['RunShell','shell'],['RunAllowlistedChecks','checks'],['ProposePatches','patch proposals'],['ApplyPatches','apply patches']];
  return map.filter(([k])=>p[k]||p[k.charAt(0).toLowerCase()+k.slice(1)]).map(([,v])=>v);
}
function activeRoles(){
  const roles=new Set();
  (lastGraphData?.nodes||[]).forEach(t=>{if(t.assigned_ant)roles.add(t.assigned_ant);});
  return roles;
}
function visibleRoles(){
  const roles=(colonyRegistry?.roles||colonyRegistry?.Roles||[]).filter(r=>!['queen','director'].includes(roleId(r)));
  if(colonyView==='active'){
    const ar=activeRoles();
    return roles.filter(r=>ar.has(roleId(r)));
  }
  return roles;
}
// v2.14.5: chambers are a LAYOUT of the live colony canvas, not a second renderer. The old
// 'group' arc-spread only nudged roles along one arc; real chambers cluster each colony around
// its own centre so the structure reads at a glance — and every canvas capability (drag, pan,
// zoom, pulses, pheromones, inspector, handoff edges) keeps working unchanged underneath.
// v2.14.9: SEVEN functional chambers, fixed by purpose rather than derived from each role's
// Colony string (which produced ~14 near-singleton chambers). Every registry role maps to exactly
// one chamber; unknown/future roles land in Infrastructure Works rather than spawning their own.
const CHAMBER_MAP = {
  "Queen's Core":        ['queen','director','planner','constraint'],
  'Intelligence Nexus':  ['researcher','file','web','ui_cartographer'],
  'The Forge':           ['coder','builder','scribe'],
  'Validation Bastion':  ['verifier','tester','soldier','medic'],
  'Memory Vault':        ['archivist','change_archivist'],
  'Infrastructure Works':['quartermaster','inventory','proxmox','storage','backup'],
  'Network Watch':       ['network_scout','health','security_scout'],
};
const CHAMBER_ORDER = Object.keys(CHAMBER_MAP);
const CHAMBER_FALLBACK = 'Infrastructure Works';
const ROLE_CHAMBER = (()=>{
  const m={};
  CHAMBER_ORDER.forEach(c=>CHAMBER_MAP[c].forEach(r=>{ m[r]=c; }));
  return m;
})();
/** Chamber for a role id — never invents a chamber for an unmapped role. */
function chamberFor(roleIdStr){ return ROLE_CHAMBER[String(roleIdStr||'').toLowerCase()]||CHAMBER_FALLBACK; }

let CHAMBERS={};   // colony -> {x,y,label}; rebuilt by buildNodes in chamber mode, drawn by drawBg

/* ---------------------------------------------------------------------------------------------
 * v2.14.12 REPAIR: colony map preferences + chamber geometry.
 *
 * v2.14.5 through v2.14.10 added CALL SITES for everything below — drawChambers reads
 * colonyMotion/colonyLabels, drawNode reads colonyLabels, maybeSpawn reads colonyMotion, the
 * render loop reads colonyPheromones, buildNodes calls chamberCentres, the mouse handlers call
 * chamberAt/moveChamber/persistChamber, and the viewbar has reset-view/reset-layout buttons —
 * but the DEFINITIONS were never actually written to this file. The result:
 *
 *   - loop() threw a ReferenceError on `colonyPheromones` every frame, AFTER drawing the
 *     background and edges but BEFORE nodes.forEach — so the map showed edges radiating from an
 *     empty centre and no ants at all, and nothing downstream (particles, activity decay) ran.
 *   - buildNodes() threw on `chamberCentres` in chamber mode after pushing only queen+director,
 *     which is why the Chambers view rendered as a single line.
 *   - the Motion/Labels/Pheromones selects and the View/Layout reset buttons had no listeners,
 *     so they were inert.
 *
 * `node --check` cannot catch this: an undeclared identifier is a runtime ReferenceError, not a
 * syntax error. RegressionGuardTests.UiIntegrity_ColonyAndChamberSymbolsAreDeclared now fails the
 * build on any colony-prefixed or chamber-prefixed symbol used without a declaration.
 * ------------------------------------------------------------------------------------------- */

const COLONY_PREF_VALUES={
  motion:['off','low','normal','high'],
  labels:['off','active','all'],
  pheromones:['off','active','all'],
};
// Reduced motion is honoured as a floor, never overridden upward by a stored preference.
const colonyReducedMotion = !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
let colonyMotion     = colonyReducedMotion ? 'off' : 'normal';
let colonyLabels     = 'all';
let colonyPheromones = 'all';

/** Seed the preferences from the markup so the `selected` options stay the single source of truth. */
function loadColonyPrefs(){
  const read=(id,fb)=>{ const el=document.getElementById(id); return el&&el.value?el.value:fb; };
  colonyLabels     = read('cv-labels','all');
  colonyPheromones = read('cv-pher','all');
  colonyMotion     = colonyReducedMotion ? 'off' : read('cv-motion','normal');
  if(colonyReducedMotion){
    const sel=document.getElementById('cv-motion');
    if(sel){ sel.value='off'; sel.title='Forced off: your system requests reduced motion'; }
  }
}

/** Apply one preference. Values are validated against COLONY_PREF_VALUES — never trusted raw. */
function setColonyPref(kind,value){
  const allowed=COLONY_PREF_VALUES[kind];
  if(!allowed||allowed.indexOf(value)<0) return;
  if(kind==='motion'){ colonyMotion=colonyReducedMotion?'off':value; if(colonyMotion==='off') particles=[]; }
  else if(kind==='labels'){ colonyLabels=value; }
  else if(kind==='pheromones'){ colonyPheromones=value; if(value==='off') pheroMotes.length=0; }
  if(colonyLive) colonyLive.setOptions({motion:colonyMotion, labels:colonyLabels, trails:colonyPheromones!=='off'});
}

/**
 * Chamber centres. The Queen's Core holds the middle because it is the control plane, not a peer
 * cluster; the remaining chambers sit on a ring around it. Operator drags are stored as OFFSETS
 * (uiState.chambers[name] = {dx,dy}) against the computed base, so a chamber stays where it was
 * put across resizes and across changes to the chamber order.
 */
function chamberCentres(names){
  const all=Array.isArray(names)?names:[];
  const ring=all.filter(n=>n!=="Queen's Core");
  const off=uiState.chambers||{};
  // v2.16.0: chambers hold more area now that roles and workers are properly spaced, so the ring
  // they sit on grows with them — otherwise neighbouring chambers overlap and the view is muddier
  // than the version it replaced.
  const R=Math.min(W,H)*0.38;
  const out={};
  const place=(name,bx,by)=>{
    const o=off[name]||{};
    const dx=Number(o.dx)||0, dy=Number(o.dy)||0;
    out[name]={x:bx+dx,y:by+dy,bx:bx,by:by,label:name};
  };
  if(all.indexOf("Queen's Core")>=0) place("Queen's Core",cx,cy);
  ring.forEach((name,i)=>{
    const a=(i/Math.max(1,ring.length))*Math.PI*2 - Math.PI/2;
    place(name, cx+Math.cos(a)*R, cy+Math.sin(a)*R);
  });
  return out;
}

/** Ring radius that actually encloses the chamber's ants; 0 when the chamber is empty. */
function chamberRadius(name){
  const c=CHAMBERS[name];
  if(!c) return 0;
  let max=0, count=0;
  nodes.forEach(n=>{
    if(n.chamber!==name) return;
    count++;
    max=Math.max(max, Math.hypot(n.x-c.x,n.y-c.y)+(n.r||10));
  });
  return count ? Math.max(58, max+22) : 0;
}

/** Chamber under a world point, preferring the tightest ring so overlaps resolve predictably. */
function chamberAt(wx,wy){
  if(colonyView!=='group'||!CHAMBERS) return null;
  let best=null,bestR=Infinity;
  Object.keys(CHAMBERS).forEach(name=>{
    const c=CHAMBERS[name], r=chamberRadius(name);
    if(!r||r>=bestR) return;
    if(Math.hypot(wx-c.x,wy-c.y)<=r){ best=name; bestR=r; }
  });
  return best;
}

/** Move a chamber and carry its ants, preserving their positions relative to the ring. */
function moveChamber(name,dx,dy){
  const c=CHAMBERS[name];
  if(!c||(!dx&&!dy)) return;
  c.x+=dx; c.y+=dy;
  nodes.forEach(n=>{ if(n.chamber===name){ n.x+=dx; n.y+=dy; } });
}

/** Persist a dragged chamber: the ring offset, plus the ants that travelled with it. */
function persistChamber(name){
  const c=CHAMBERS[name];
  if(!c) return;
  uiState.chambers=uiState.chambers||{};
  uiState.chambers[name]={dx:Math.round(c.x-c.bx),dy:Math.round(c.y-c.by)};
  // The ants moved too, so their own positions are saved as well — matching what a single-ant
  // drag already does. Without this a reload would snap them out of the ring they were put in.
  nodes.forEach(n=>{ if(n.chamber===name) uiState.positions[n.id]={x:Math.round(n.x),y:Math.round(n.y)}; });
  saveUiState();
}

/** Reset pan and zoom only. Sets the camera TARGETS so the loop eases there instead of snapping. */
function colonyResetView(){ tX=0; tY=0; tZ=1; }
/** v0.3.8.49 — zoom about the viewport centre by a factor, clamped like the wheel path. Keeps the
 *  centre point fixed so the map grows/shrinks in place rather than drifting. */
function colonyZoom(factor){
  const nz=Math.max(.2,Math.min(4,tZ*factor));
  // Zoom about the canvas centre (cx,cy in world = screen centre): tX/tY stay put for a centre zoom.
  tZ=nz;
}

/**
 * Reset layout: drops dragged ant positions and chamber offsets, then rebuilds. Deliberately does
 * NOT touch caste names, colours, or model routes — those are settings, not layout.
 */
function colonyResetLayout(){
  uiState.positions={};
  uiState.chambers={};
  buildNodes();
  colonyResetView();
  saveUiState();
}

function colonyAngleFor(role,index,total){
  if(colonyView!=='group') return -90 + index*(360/Math.max(1,total));
  const c=roleColony(role),ci=Math.max(0,CHAMBER_ORDER.indexOf(c));
  return -115 + ci*(230/Math.max(1,CHAMBER_ORDER.length-1)) + (index%3-1)*7;
}
// v0.3.8.42 (§9/§14): registry failure is a STATE, not a console.warn. Both branches of this
// release fixed it independently; this is the union. What the operator sees: with cached roles,
// the map keeps drawing them and the legend marks them STALE with when, why, and a retry; with
// nothing cached, the map shows the two core nodes (Queen and Director are structural facts of
// the product, not runtime discoveries) and the legend says exactly what failed — never the
// six-role fiction buildNodes used to invent, which marked every invented ant Enabled and
// Executable and was six releases stale besides (twelve roles run by default since v0.3.8.41).
let colonyRegistryProblem=null;   // {message, at} | null — "loaded and now failing" keeps its roles
let colonyRegistryAt=0;           // when the roles on screen were actually fetched
async function loadColonyRegistry(){
  try{
    const r=await api('/colony/registry');
    if(r&&r.success){
      colonyRegistry=r.data; colonyRegistryProblem=null; colonyRegistryAt=Date.now();
      if(colonyTopo) colonyTopo.ingestRegistry(r.data);
      // v2.14.13: truthful per-role runtime state for the inspector, from the same fetch.
      antRuntimeStatus={};
      (r.data.runtime_status||[]).forEach(st=>{ antRuntimeStatus[String(st.role_id||'').toLowerCase()]=st; });
      buildNodes();renderColonyLegend();
      // v0.3.8.55: the config panel lives inside the merged inspector page now.
      if(document.getElementById('page-antobs')?.classList.contains('active')) openAntConfig();
      return;
    }
    // A structured failure is still a failure. `if(r.success)` with no else meant an authorisation
    // refusal or a 500 left the previous drawing on screen with nothing saying it was old.
    colonyRegistryProblem={message:(r&&r.message)||'registry request rejected', at:Date.now()};
  }catch(e){ colonyRegistryProblem={message:e.message||'registry unreachable', at:Date.now()}; }
  buildNodes();renderColonyLegend();
}
function colonyRegistryRetry(){
  apiCacheBust('/colony/registry');
  const el=document.getElementById('chud-legend-problem');
  if(el) el.textContent='Retrying…';
  loadColonyRegistry();
}

function buildNodes(){
  nodes=[]; edges=[];
  const bR=Math.min(W,H)*0.27;
  nodes.push({ id:'queen',ant:'queen',x:cx,y:cy,label:'Queen',role:'CORE',colony:'Core',purpose:'Central mission authority',color:ROLE_COLORS.queen,r:QUEEN_R,pp:0,activity:1,nodeType:'core',chamber:"Queen's Core" });
  nodes.push({ id:'director',ant:'director',x:cx,y:cy-bR*.48,label:'Director',role:'AUTONOMY',colony:'Core',purpose:'Objective lifecycle and autonomy control',color:ROLE_COLORS.director,r:18,pp:1,activity:0,nodeType:'core',chamber:"Queen's Core" });
  edges.push({from:'queen',to:'director'});
  // v0.3.8.42 (§9): no fabricated roster — the full forensics live on loadColonyRegistry above.
  // With no registry the map draws the control plane only; visibleRoles() answers [] then, and
  // the guard here says so explicitly rather than relying on the optional chain.
  const roles = colonyRegistry ? visibleRoles() : [];
  // v2.14.5 chamber mode: cluster each colony around its own centre. Roles fan out inside their
  // chamber instead of sharing one global ring, so groups are visually distinct on the SAME canvas.
  const chamberMode=colonyView==='group';
  CHAMBERS = chamberMode ? chamberCentres(CHAMBER_ORDER) : {};
  const perChamber={};
  roles.forEach(r=>{ const c=chamberFor(roleId(r)); perChamber[c]=(perChamber[c]||0)+1; });
  const seen={};
  /* v2.16.0 chamber layout: one angular SECTOR per role.
   *
   * The old layout muddied because two things fought each other — roles sat on a ring capped at
   * 46px while their workers were placed 72px out, and the worker bearing came from
   * colonyAngleFor(), which in chamber mode derives from the CHAMBER's index and is therefore
   * identical for every role in it. So each role's workers landed on the neighbouring role.
   *
   * Now each role owns 1/n of the chamber's circle and its workers are laid on an arc inside that
   * sector, which makes cross-role collision geometrically impossible rather than merely
   * unlikely. Radii are derived from the arc length actually needed, so a chamber with five roles
   * or a role with four workers grows instead of packing tighter.
   * Verified: zero overlapping node pairs across all seven chambers, 15px tightest gap. */
  const CHAMBER_ROLE_GAP = 60;    // arc length reserved per role on the role ring
  const CHAMBER_WORKER_GAP = 30;  // arc length reserved per worker on its role's arc
  const CHAMBER_SECTOR_USE = 0.82;// fraction of a sector its workers may span (rest is margin)
  const chamberSlot={};           // roleId -> {cx,cy,angle,sector,r1}

  roles.forEach((r,i)=>{
    const id=roleId(r),rad=colonyAngleFor(r,i,roles.length)*Math.PI/180;
    let bx=cx+Math.cos(rad)*bR, by=cy+Math.sin(rad)*bR;
    if(chamberMode){
      const c=chamberFor(id), centre=CHAMBERS[c];
      if(centre){
        const k=(seen[c]=(seen[c]||0)), n=perChamber[c];
        // Role ring scales with membership instead of being capped at 46px. The old cap meant a
        // five-role chamber packed its roles ~46px apart while their workers sat 72px out — the
        // workers of one role landed on top of the next role entirely.
        const sector = (Math.PI*2)/Math.max(1,n);
        // Ring radius from the arc each role needs, not a magic cap.
        const r1 = n<=1 ? 0 : Math.max(58, (n*CHAMBER_ROLE_GAP)/(Math.PI*2));
        const a = n>1 ? (-Math.PI/2 + (k+0.5)*sector) : 0;   // sector CENTRE, starting at 12 o'clock
        chamberSlot[id]={cx:centre.x,cy:centre.y,angle:a,sector:sector,r1:r1};
        bx=centre.x+Math.cos(a)*r1; by=centre.y+Math.sin(a)*r1;
        seen[c]=k+1;
      }
    }
    const color=ROLE_COLORS[id]||'#7fa0bc';
    nodes.push({id,ant:id,x:bx,y:by,label:roleName(r),role:roleColony(r),colony:roleColony(r),purpose:rolePurpose(r),enabled:roleEnabled(r),executable:roleExecutable(r),permissions:rolePerms(r),allowedTools:roleAllowedTools(r),forbiddenTools:roleForbiddenTools(r),color,r:ANT_R,pp:i,activity:0,nodeType:'role',workers:roleWorkers(r),chamber:chamberFor(id)});
    edges.push({from:id==='planner'||id==='constraint'?'queen':'director',to:id});
    const showWorkers=colonyView==='expanded'||colonyView==='group'||colonyView==='active';
    if(showWorkers){
      const activeWorkers=new Set((lastGraphData?.nodes||[]).map(t=>t.assigned_worker).filter(Boolean));
      const ws=roleWorkers(r).filter(w=>colonyView!=='active'||activeWorkers.has(workerId(w)));
      const slot = chamberMode ? chamberSlot[id] : null;
      // Worker arc radius: far enough out that this role's workers fit inside its OWN sector.
      const r2 = slot ? Math.max(slot.r1+46, (ws.length*CHAMBER_WORKER_GAP)/(slot.sector*CHAMBER_SECTOR_USE)) : 0;
      const span = slot ? slot.sector*CHAMBER_SECTOR_USE : 0;
      ws.forEach((w,wi)=>{
        let wx,wy;
        if(slot){
          const off = ws.length===1 ? 0 : (wi-(ws.length-1)/2)*(span/ws.length);
          const cr = slot.angle+off;
          wx=slot.cx+Math.cos(cr)*r2; wy=slot.cy+Math.sin(cr)*r2;
        } else {
          const sR=Math.max(54,bR*.30);
          const spread=ws.length===1?0:(wi-(ws.length-1)/2)*24;
          const cr=(colonyAngleFor(r,i,roles.length)+spread)*Math.PI/180;
          wx=bx+Math.cos(cr)*sR; wy=by+Math.sin(cr)*sR;
        }
        const wid=workerId(w);
        nodes.push({id:wid,ant:id,worker:wid,x:wx,y:wy,label:workerName(w),role:roleName(r),colony:roleColony(r),purpose:workerPurpose(w),permissions:workerPerms(w),allowedTools:prop(w,'allowedTools','AllowedTools')||[],forbiddenTools:prop(w,'forbiddenTools','ForbiddenTools')||[],color,r:10,pp:wi,activity:0,nodeType:'worker',parent:id,chamber:chamberFor(id)});
        edges.push({from:id,to:wid});
      });
    }
  });
  applyUiState();
}
buildNodes();

document.querySelectorAll('#colony-viewbar .cv-btn').forEach(btn=>{
  btn.addEventListener('click',()=>{
    const view=btn.dataset.view;
    const toggle=btn.dataset.toggle;
    const act=btn.dataset.colonyact;   // v2.14.12: reset buttons share this one dispatch path
    if(act==='reset-view'){ colonyResetView(); return; }
    if(act==='reset-layout'){ colonyResetLayout(); return; }
    // v0.3.8.49: on-screen zoom — the Ctrl/Cmd+wheel bargain is undiscoverable, so the buttons
    // zoom about the canvas centre with no modifier required. Eases via the camera targets.
    if(act==='zoom-in'){ colonyZoom(1.2); return; }
    if(act==='zoom-out'){ colonyZoom(1/1.2); return; }
    if(act==='live3d'){ toggleColonyLive(!colonyLive); return; }
    if(view){
      colonyView=view;
      document.querySelectorAll('#colony-viewbar [data-view]').forEach(b=>b.classList.toggle('on',b.dataset.view===view));
      buildNodes();renderColonyLegend();
    }
    if(toggle==='handoffs'){
      showHandoffs=!showHandoffs;
      btn.classList.toggle('on',showHandoffs);
      if(!showHandoffs) dataFlowEdges=[];
    }
  });
});

// v2.14.12: CSP-safe preference wiring — delegated by data attribute, no inline handlers.
document.querySelectorAll('#colony-viewbar [data-colonypref]').forEach(sel=>{
  sel.addEventListener('change',()=>setColonyPref(sel.dataset.colonypref,sel.value));
});
loadColonyPrefs();

/* Colony Live (design doc §17, stage 3) — the opt-in 3D formicarium view (design doc §17, stage 3). Mounted into
 * the SAME #colony-canvas-area that re-parents between Colony, Dashboard and Chat, so it rides along;
 * the classic canvas stays the default and the permanent fallback. Fed from the handlers this file
 * already runs (pollGraph, loadColonyRegistry, pollApprovals, the event stream) — never a second
 * fetch. Renderer + projection live in colony-live.js / colony-topology.js; this file only toggles. */
let colonyLive=null, colonyTopo=null;
const COLONY_LIVE_KEY='anthill.colony.view3d';
function toggleColonyLive(on){
  const area=document.getElementById('colony-canvas-area'), classic=document.getElementById('c');
  if(!area||!classic) return;
  if(on&&!(window.ColonyLive&&window.ColonyTopology)){ console.warn('Colony Live assets not loaded; classic canvas kept'); on=false; }
  if(on&&!colonyLive){
    colonyTopo=ColonyTopology.create();
    colonyLive=ColonyLive.create();
    colonyTopo.onScene(s=>colonyLive&&colonyLive.setTopology(s));
    colonyLive.mount(area);
    colonyLive.setOptions({motion:colonyMotion, labels:colonyLabels, trails:colonyPheromones!=='off'});
    // A record names the ant that wrote it; clicking one opens the existing Agent Inspector for
    // that ant. Event-derived facts only, read-only — the record index is a future contract (§19).
    colonyLive.on('record',r=>{ const who=String(r?.record?.ant||'').toLowerCase(); const n=nodes.find(x=>x.ant===who||x.worker===who||x.id===who); if(n) showInspector(n); });
    if(lastGraphData) colonyTopo.ingestGraph(lastGraphData);
    if(colonyRegistry) colonyTopo.ingestRegistry(colonyRegistry);
  }else if(!on&&colonyLive){ colonyLive.destroy(); colonyLive=null; colonyTopo=null; }
  classic.style.display=on?'none':'';
  document.querySelectorAll('#colony-viewbar [data-colonyact="live3d"]').forEach(b=>b.classList.toggle('on',!!on));
  try{ localStorage.setItem(COLONY_LIVE_KEY,on?'1':'0'); }catch(e){}
}
// One subscription for the page's life (toggling must not stack listeners). Record growth only on
// real record-creating events — recordSectorOf() returns null for everything else.
onColonyEvent(ev=>{ if(!colonyTopo) return; colonyTopo.ingestEvent(ev); const sec=ColonyTopology.recordSectorOf(ev); if(sec&&colonyLive) colonyLive.addRecordPoint(sec); });
// Deferred scripts run in document order, so by DOMContentLoaded the split assets exist.
document.addEventListener('DOMContentLoaded',()=>{ let want=false; try{ want=localStorage.getItem(COLONY_LIVE_KEY)==='1'; }catch(e){} if(want) toggleColonyLive(true); });

/**
 * v2.14.5: chamber rings + labels, drawn in WORLD space so they pan, zoom, and sit beneath the
 * ants like the rest of the map. Only rendered in chamber mode; every other view is untouched.
 * This replaces the separate SVG chamber map — same information, one renderer.
 */
function chamberStats(name){
  const members=nodes.filter(n=>n.chamber===name);
  const counts=lastGraphData||{};
  let running=0, failed=0, active=0, dormant=0;
  members.forEach(m=>{
    if(m.activity>0.05) active++;
    if(m.executable===false) dormant++;
    const key=m.nodeType==='worker'?m.worker:m.ant;
    running+=(counts.running_counts&&counts.running_counts[key])||0;
    failed+=(counts.failed_counts&&counts.failed_counts[key])||0;
  });
  const health = failed>0 ? 'alert' : active>0 ? 'live' : dormant===members.length ? 'dormant' : 'idle';
  return {members,running,failed,active,dormant,health};
}

/** One dominant status colour per chamber — alert wins, then live, then idle, then dormant. */
function chamberColor(health){
  return health==='alert' ? 'rgba(255,90,120,'
       : health==='live'  ? 'rgba(34,211,238,'
       : health==='idle'  ? 'rgba(150,175,200,'
                          : 'rgba(132,150,172,';
}

/**
 * v2.14.15: per-health ring opacity.
 *
 * Dormant chambers used to draw at stroke .12 / fill .012 against .30 / .035 for everything else,
 * which read as "this chamber is missing" rather than "this chamber is on standby". Network Watch
 * is entirely non-executable ants (network_scout, health, security_scout are all Executable:false),
 * so it was always dormant and always looked broken. Standby is now a STEADY, clearly visible
 * state — dimmer and cooler than a working chamber, but unmistakably present.
 */
function chamberAlpha(health,grabbed){
  if(grabbed) return {stroke:0.70,fill:0.045,label:1};
  return health==='dormant'
    ? {stroke:0.26,fill:0.026,label:0.78}
    : {stroke:0.34,fill:0.038,label:0.94};
}

function drawChambers(){
  if(colonyView!=='group'||!CHAMBERS) return;
  Object.keys(CHAMBERS).forEach(name=>{
    const c=CHAMBERS[name];
    const st=chamberStats(name);
    if(!st.members.length) return;
    const rad=chamberRadius(name);
    const p=w2s(c.x,c.y), sr=rad*camZ;
    const grabbed=draggingChamber===name;
    const base=chamberColor(st.health);
    // Subtle activity pulse, only when something is actually happening (and never under
    // reduced motion / Motion=off).
    // Only chambers with real activity breathe. Idle and standby are deliberately STEADY: a
    // pulsing ring means "work is happening here", so pulsing everything would make it meaningless.
    const pulsing = st.health!=='dormant' && st.active>0 && colonyMotion!=='off';
    const beat = pulsing ? Math.sin(performance.now()/480) : 0;
    const pulse = beat*(colonyMotion==='high'?0.20:0.15);
    const a = chamberAlpha(st.health,grabbed);

    ctx.save();
    ctx.beginPath();
    ctx.arc(p.x,p.y,sr,0,Math.PI*2);
    ctx.strokeStyle=base+Math.max(0.10,a.stroke+pulse)+')';
    ctx.lineWidth=grabbed?2:(pulsing?1.6:1.2);
    ctx.stroke();
    ctx.fillStyle=base+Math.max(0.008,a.fill+pulse/4)+')';
    ctx.fill();

    if(colonyLabels!=='off'){
      const cw=Math.max(9,Math.round(10*camZ));
      ctx.textAlign='center';
      ctx.font=`600 ${cw}px var(--mono,monospace)`;
      ctx.fillStyle=base+a.label+')';
      ctx.fillText(chamberLabel(name), p.x, p.y-sr-8);
      // Chamber summary only — the detail lives on the ants themselves.
      const bits=[st.active+'/'+st.members.length+' active'];
      if(st.running>0) bits.push(st.running+' running');
      if(st.failed>0) bits.push(st.failed+' failed');
      if(st.health==='dormant') bits.push('standby');
      ctx.font=`${Math.max(8,Math.round(8.5*camZ))}px var(--mono,monospace)`;
      ctx.fillStyle=base+(st.health==='dormant'?0.55:0.66)+')';
      ctx.fillText(bits.join(' · '), p.x, p.y-sr+cw+2);
    }
    ctx.restore();
  });
}

function updateNodeActivity(){
  // v0.3.8.49 (§18): ants that are the SOURCE of a live data-flow edge this frame are communicating —
  // an ant-to-ant handoff in progress. Computed from the real dependency edges buildDataFlowEdges
  // derives from the task graph, so the signal is a real interaction, not decoration.
  colonyCommunicating=new Set(dataFlowEdges.map(e=>e.from));
  nodes.forEach(n=>{
    if(n.id==='queen'){n.activity=colonyRunning?1:.3;n.state=colonyRunning?'working':'idle';return;}
    const key=n.nodeType==='worker'?n.worker:n.ant;
    n.activity=Math.min(1,Math.max(0,colonyRunning?(colonyActivity[key]??colonyActivity[n.ant]??0):0));
    // Discrete state: the ant's own state, lifted to 'communicating' when it is actively handing
    // off AND not already in a more urgent state (blocked / awaiting approval outrank a handoff).
    let st=colonyAntState[key]||colonyAntState[n.ant]||'idle';
    if(colonyCommunicating.has(n.id) && (STATE_RANK[st]??0) < STATE_RANK.communicating) st='communicating';
    n.state=st;
  });
}

function h2rgb(h){return[parseInt(h.slice(1,3),16),parseInt(h.slice(3,5),16),parseInt(h.slice(5,7),16)];}

function spawnParticle(e){
  const fn=nodes.find(n=>n.id===e.from),tn=nodes.find(n=>n.id===e.to);
  if(!fn||!tn||tn.activity===0) return;
  const [r,g,b]=h2rgb(tn.color);
  particles.push({fn,tn,t:0,speed:.003+tn.activity*.006,r,g,b,a:.6+tn.activity*.4,sz:1.5+tn.activity*2});
}

let lastSpawn=0;
function maybeSpawn(ts){
  if(!colonyRunning) return;
  if(colonyMotion==='off') return;                    // v2.14.5: motion preference governs particles
  const rate = colonyMotion==='low' ? 0.4 : colonyMotion==='high' ? 1.6 : 1;
  const dt=(200-modelCallRate*80)/rate;
  if(ts-lastSpawn<Math.max(80,dt)) return;
  lastSpawn=ts;
  const active=edges.filter(e=>{const tn=nodes.find(n=>n.id===e.to);return tn&&tn.activity>0;});
  const n=Math.min(4,active.length);
  for(let i=0;i<n;i++) spawnParticle(active[Math.floor(Math.random()*active.length)]);
}

function w2s(wx,wy){return{x:(wx-cx)*camZ+cx+camX,y:(wy-cy)*camZ+cy+camY};}
function s2w(sx,sy){return{x:(sx-cx-camX)/camZ+cx,y:(sy-cy-camY)/camZ+cy};}
function qb(fp,cp,tp,t){return{x:(1-t)**2*fp.x+2*(1-t)*t*cp.x+t**2*tp.x,y:(1-t)**2*fp.y+2*(1-t)*t*cp.y+t**2*tp.y};}
function cpFor(fp,tp){const dx=tp.x-fp.x,dy=tp.y-fp.y;return{x:fp.x+dx*.5+dy*.1,y:fp.y+dy*.5-dx*.1};}

function drawBg(){
  const g=ctx.createRadialGradient(cx,cy*.5,0,cx,cy,Math.max(W,H)*.85);
  g.addColorStop(0,'#1c2d40');g.addColorStop(1,'#0f1923');
  ctx.fillStyle=g;ctx.fillRect(0,0,W,H);
  ctx.save();ctx.globalAlpha=.028;ctx.strokeStyle='#fff';ctx.lineWidth=1;
  const gs=56*camZ,ox=((camX%gs)+gs)%gs,oy=((camY%gs)+gs)%gs;
  for(let x=ox-gs;x<W+gs;x+=gs){ctx.beginPath();ctx.moveTo(x,0);ctx.lineTo(x,H);ctx.stroke();}
  for(let y=oy-gs;y<H+gs;y+=gs){ctx.beginPath();ctx.moveTo(0,y);ctx.lineTo(W,y);ctx.stroke();}
  ctx.restore();
}

function drawEdge(e){
  const fn=nodes.find(n=>n.id===e.from),tn=nodes.find(n=>n.id===e.to);
  if(!fn||!tn) return;
  const fp=w2s(fn.x,fn.y),tp=w2s(tn.x,tn.y),cp=cpFor(fp,tp);
  const [r,g,b]=h2rgb(tn.color);
  ctx.beginPath();ctx.moveTo(fp.x,fp.y);ctx.quadraticCurveTo(cp.x,cp.y,tp.x,tp.y);
  ctx.strokeStyle='rgba(255,255,255,0.035)';ctx.lineWidth=(3+tn.activity*3)*camZ;ctx.stroke();
  if(tn.activity>0){
    ctx.beginPath();ctx.moveTo(fp.x,fp.y);ctx.quadraticCurveTo(cp.x,cp.y,tp.x,tp.y);
    ctx.strokeStyle=`rgba(${r},${g},${b},${tn.activity*.28})`;ctx.lineWidth=(1+tn.activity*1.5)*camZ;ctx.stroke();
  }
}

function drawDataFlowEdge(e,ts){
  const fn=nodes.find(n=>n.id===e.from),tn=nodes.find(n=>n.id===e.to);
  if(!fn||!tn) return;
  const fp=w2s(fn.x,fn.y),tp=w2s(tn.x,tn.y);
  const dx=tp.x-fp.x,dy=tp.y-fp.y;
  const mx=(fp.x+tp.x)/2,my=(fp.y+tp.y)/2;
  const perp={x:-dy*.35,y:dx*.35};
  const cp={x:mx+perp.x,y:my+perp.y};
  const [r,g,b]=h2rgb(fn.color);
  const pulse=.4+Math.sin(ts*.002+e.phase)*0.25;
  ctx.beginPath();ctx.moveTo(fp.x,fp.y);ctx.quadraticCurveTo(cp.x,cp.y,tp.x,tp.y);
  ctx.setLineDash([4*camZ,5*camZ]);
  // v0.3.8.55 (field report: "transfer visuals don't look right"): the dashes MARCH toward the
  // receiving ant. The dash pattern was static — only its alpha pulsed — so an active handoff
  // read as a flickering dotted line rather than data in motion. The offset advances with time
  // (negative: from source toward target), the one visual signature every earlier "nice" version
  // of this had; the alpha pulse stays as the secondary heartbeat.
  ctx.lineDashOffset=-(ts*.02%(9*camZ))*camZ;
  ctx.strokeStyle=`rgba(${r},${g},${b},${pulse*.22})`;ctx.lineWidth=1.5*camZ;ctx.stroke();
  ctx.setLineDash([]);ctx.lineDashOffset=0;
  // A packet travelling the curve, tail behind it — the transfer itself, not just its route.
  const tt=(ts*.0004+e.phase)%1;
  for(let i=2;i>=0;i--){
    const pp=qb(fp,cp,tp,Math.max(0,tt-i*.05));
    ctx.beginPath();ctx.arc(pp.x,pp.y,Math.max(1.2,(2.6-i*.7)*camZ),0,Math.PI*2);
    ctx.fillStyle=`rgba(${r},${g},${b},${(0.7-i*.22)*pulse})`;ctx.fill();
  }
  const t2=qb(fp,cp,tp,.92),sz=5*camZ;
  const ax=tp.x-t2.x,ay=tp.y-t2.y,al=Math.hypot(ax,ay)||1;
  ctx.beginPath();
  ctx.moveTo(t2.x+ax/al*sz*1.2,t2.y+ay/al*sz*1.2);
  ctx.lineTo(t2.x-ay/al*sz*.5,t2.y+ax/al*sz*.5);
  ctx.lineTo(t2.x+ay/al*sz*.5,t2.y-ax/al*sz*.5);
  ctx.closePath();
  ctx.fillStyle=`rgba(${r},${g},${b},${pulse*.55})`;ctx.fill();
}

function drawParticles(){
  particles.forEach(p=>{
    const fp=w2s(p.fn.x,p.fn.y),tp=w2s(p.tn.x,p.tn.y),cp=cpFor(fp,tp);
    for(let i=3;i>=0;i--){const tt=Math.max(0,p.t-i*.02),pp=qb(fp,cp,tp,tt);ctx.beginPath();ctx.arc(pp.x,pp.y,p.sz*(4-i)/4*camZ,0,Math.PI*2);ctx.fillStyle=`rgba(${p.r},${p.g},${p.b},${p.a*(1-i/4)*.3})`;ctx.fill();}
    const pos=qb(fp,cp,tp,p.t);
    const gg=ctx.createRadialGradient(pos.x,pos.y,0,pos.x,pos.y,p.sz*5*camZ);
    gg.addColorStop(0,`rgba(${p.r},${p.g},${p.b},.3)`);gg.addColorStop(1,`rgba(${p.r},${p.g},${p.b},0)`);
    ctx.beginPath();ctx.arc(pos.x,pos.y,p.sz*5*camZ,0,Math.PI*2);ctx.fillStyle=gg;ctx.fill();
    ctx.beginPath();ctx.arc(pos.x,pos.y,p.sz*camZ,0,Math.PI*2);ctx.fillStyle=`rgba(${p.r},${p.g},${p.b},${p.a})`;ctx.fill();
  });
}

function drawNode(n,ts){
  const sp=w2s(n.x,n.y),r=n.r*camZ;
  if(r<3) return;
  const [cr,cg,cb]=h2rgb(n.color);
  const act=n.activity,sel=selectedNode&&selectedNode.id===n.id,hov=hoveredNode&&hoveredNode.id===n.id;
  const pulse=Math.sin(ts*.0018+n.pp)*.1+.9;
  if(act>0||hov){ctx.beginPath();ctx.arc(sp.x,sp.y,r*(1.7+Math.sin(ts*.0014+n.pp)*.15),0,Math.PI*2);ctx.fillStyle=`rgba(${cr},${cg},${cb},${(hov?.1:0)+act*.06})`;ctx.fill();}
  if(sel||hov){ctx.beginPath();ctx.arc(sp.x,sp.y,r*(hov?1.8:2),0,Math.PI*2);ctx.strokeStyle=`rgba(${cr},${cg},${cb},${hov?.55:.4})`;ctx.lineWidth=hov?2:1.5;ctx.setLineDash([5,5]);ctx.stroke();ctx.setLineDash([]);}
  ctx.save();
  ctx.shadowColor=`rgba(${cr},${cg},${cb},${act*.5+(hov?.3:0)})`;ctx.shadowBlur=r*(act>0||hov?2.2:.5);
  const grad=ctx.createRadialGradient(sp.x-r*.25,sp.y-r*.25,0,sp.x,sp.y,r);
  const alpha=act>0||hov?.95:.3;
  grad.addColorStop(0,`rgba(${Math.min(255,cr+55)},${Math.min(255,cg+55)},${Math.min(255,cb+40)},${alpha})`);
  grad.addColorStop(1,`rgba(${cr},${cg},${cb},${alpha*.85})`);
  ctx.beginPath();ctx.arc(sp.x,sp.y,r,0,Math.PI*2);ctx.fillStyle=grad;ctx.fill();
  ctx.restore();
  ctx.beginPath();ctx.arc(sp.x,sp.y,r,0,Math.PI*2);
  ctx.strokeStyle=`rgba(${cr},${cg},${cb},${act>0||hov?.6:.2})`;ctx.lineWidth=sel||hov?2:1.2;ctx.stroke();
  ctx.beginPath();ctx.arc(sp.x,sp.y,r*.45,0,Math.PI*2);ctx.fillStyle=`rgba(255,255,255,${act>0?.1:.03})`;ctx.fill();
  if(r>7&&camZ>.42){
    ctx.font=`${Math.max(9,10*camZ)}px 'Segoe UI',sans-serif`;ctx.textAlign='center';
    // v2.14.5: label density is an operator preference (off | active-only | all). Hovered and
    // selected ants always keep their label so inspection never goes blind.
    const showLabel = colonyLabels==='all' || hov || (colonyLabels==='active' && act>0);
    // v2.14.9: visible-only ants (gate closed / not executable) read as STANDBY — dimmed and
    // marked, deliberately not styled like a failure.
    const standby = n.executable===false && n.nodeType!=='core';
    if(showLabel){
      ctx.fillStyle='rgba(8,16,26,.8)';ctx.fillText(n.label,sp.x+1,sp.y+r*1.8+1);
      ctx.fillStyle=act>0||hov?`rgba(${cr},${cg},${cb},1)`:`rgba(${cr},${cg},${cb},${standby?.30:.45})`;
      ctx.fillText(n.label,sp.x,sp.y+r*1.8);
      if(standby&&(hov||colonyLabels==='all')){
        ctx.save();
        ctx.font=`${Math.max(7,Math.round(7.5*camZ))}px var(--mono,monospace)`;
        ctx.fillStyle='rgba(150,170,190,.55)';
        ctx.fillText('standby',sp.x,sp.y+r*1.8+Math.max(8,9*camZ));
        ctx.restore();
      }
    }
  }
  if(act>0&&r>6){ctx.beginPath();ctx.arc(sp.x+r*.65,sp.y-r*.65,Math.max(2,3*camZ)*pulse,0,Math.PI*2);ctx.fillStyle=`rgba(${cr},${cg},${cb},.9)`;ctx.fill();}
  // v0.3.8.49 (§18): the discrete-state ring — a coloured arc around the ant in its state's colour, so
  // blocked/awaiting-approval/failed/working read at a glance rather than being inferred from a
  // glow. Idle draws nothing (a quiet colony stays quiet). The two states an operator must act on —
  // blocked and awaiting approval — pulse; the rest are steady.
  const st=n.state;
  if(st && st!=='idle' && r>=4){
    const [sr,sg,sb]=h2rgb(STATE_COLORS[st]||STATE_COLORS.idle);
    const urgent=(st==='blocked'||st==='awaiting_approval'||st==='failed');
    const ringPulse=urgent?(Math.sin(ts*.006+n.pp)*.28+.72):1;
    ctx.beginPath();ctx.arc(sp.x,sp.y,r*1.42,0,Math.PI*2);
    ctx.strokeStyle=`rgba(${sr},${sg},${sb},${(urgent?.9:.7)*ringPulse})`;
    ctx.lineWidth=Math.max(1.4,2.1*camZ);
    if(st==='waiting'||st==='completed'){ctx.setLineDash([4*camZ,3*camZ]);}
    ctx.stroke();ctx.setLineDash([]);
    // A small filled state dot at 4 o'clock, so the state is legible even when rings overlap.
    if(r>7){ctx.beginPath();ctx.arc(sp.x+r*1.0,sp.y+r*1.0,Math.max(2,2.6*camZ),0,Math.PI*2);
      ctx.fillStyle=`rgba(${sr},${sg},${sb},.95)`;ctx.fill();}
  }
}

// -- Mouse (all coords via getBoundingClientRect) ------------------------------
let dragging=false,dragStart={x:0,y:0},dragCam={x:0,y:0};
const tooltip=document.getElementById('tooltip');
let draggingNode=null,nodeDragOff={x:0,y:0},nodeMoved=false;
let draggingChamber=null,chamberDragLast={x:0,y:0},chamberMoved=false;

function getCanvasLocal(e){
  const rect=canvas.getBoundingClientRect();
  return{x:e.clientX-rect.left,y:e.clientY-rect.top};
}

canvas.addEventListener('mousedown',e=>{
  const cl=getCanvasLocal(e);
  const wp=s2w(cl.x,cl.y);
  const hit=nodes.find(n=>Math.hypot(n.x-wp.x,n.y-wp.y)<n.r*2.2);
  if(hit){ draggingNode=hit; nodeDragOff={x:wp.x-hit.x,y:wp.y-hit.y}; nodeMoved=false; dragStart={x:e.clientX,y:e.clientY}; canvas.style.cursor='grabbing'; return; }
  // v2.14.7: grabbing the chamber body (ring interior, not an ant) moves the WHOLE chamber and
  // carries its ants along. Ant hit-testing runs first, so individual ants stay independently
  // draggable; empty canvas still pans the camera.
  const ch=chamberAt(wp.x,wp.y);
  if(ch){ draggingChamber=ch; chamberDragLast={x:wp.x,y:wp.y}; chamberMoved=false; canvas.style.cursor='grabbing'; return; }
  dragging=true;dragStart={x:e.clientX,y:e.clientY};dragCam={x:camX,y:camY};
});

canvas.addEventListener('mousemove',e=>{
  if(draggingNode){
    const cl=getCanvasLocal(e);const wp=s2w(cl.x,cl.y);
    draggingNode.x=wp.x-nodeDragOff.x; draggingNode.y=wp.y-nodeDragOff.y;
    nodeMoved=true; hoveredNode=null; tooltip.style.display='none'; return;
  }
  if(draggingChamber){
    const cl=getCanvasLocal(e);const wp=s2w(cl.x,cl.y);
    moveChamber(draggingChamber, wp.x-chamberDragLast.x, wp.y-chamberDragLast.y);
    chamberDragLast={x:wp.x,y:wp.y}; chamberMoved=true;
    hoveredNode=null; tooltip.style.display='none'; return;
  }
  if(dragging){camX=dragCam.x+(e.clientX-dragStart.x);camY=dragCam.y+(e.clientY-dragStart.y);tX=camX;tY=camY;hoveredNode=null;tooltip.style.display='none';return;}
  const cl=getCanvasLocal(e);const wp=s2w(cl.x,cl.y);
  const hit=nodes.find(n=>Math.hypot(n.x-wp.x,n.y-wp.y)<n.r*2.2);
  canvas.style.cursor=hit?'grab':(chamberAt(wp.x,wp.y)?'grab':'default');
  if(hit){
    hoveredNode=hit;
    const tasks=hit.nodeType==='worker'
      ? (lastGraphData?.worker_counts?.[hit.worker]??0)
      : (lastGraphData?.ant_counts?.[hit.ant]??0);
    const act=hit.id==='queen'?'Command':colonyRunning&&hit.activity>0?'Active':'Idle';
    document.getElementById('tt-name').textContent=`${hit.label} (${hit.role})`;
    document.getElementById('tt-name').style.color=hit.color;
    document.getElementById('tt-status').textContent=act;
    document.getElementById('tt-tasks').textContent=tasks;
    document.getElementById('tt-activity').textContent=Math.round(hit.activity*100)+'%';
    tooltip.style.display='block';
    const tx=e.clientX+14,ty=e.clientY-10;
    tooltip.style.left=(tx+150>window.innerWidth?e.clientX-tooltip.offsetWidth-10:tx)+'px';
    tooltip.style.top=ty+'px';
  } else { hoveredNode=null;tooltip.style.display='none'; }
});

canvas.addEventListener('mouseleave',()=>{hoveredNode=null;tooltip.style.display='none';});

canvas.addEventListener('mouseup',e=>{
  if(draggingNode){
    const n=draggingNode; draggingNode=null; canvas.style.cursor='grab';
    if(nodeMoved){ persistNodePosition(n); } else { showInspector(n); }
    return;
  }
  if(draggingChamber){
    const name=draggingChamber; draggingChamber=null; canvas.style.cursor='grab';
    if(chamberMoved) persistChamber(name);
    return;
  }
  const moved=Math.abs(e.clientX-dragStart.x)+Math.abs(e.clientY-dragStart.y);
  dragging=false;
  if(moved<5){
    const cl=getCanvasLocal(e);const wp=s2w(cl.x,cl.y);
    const hit=nodes.find(n=>Math.hypot(n.x-wp.x,n.y-wp.y)<n.r*2.5);
    if(hit) showInspector(hit);
    else{selectedNode=null;const ad=document.getElementById('agent-detail');if(ad)ad.innerHTML='<div class="ad-empty">Click or hover a colony node<br>to inspect the agent</div>';}
  }
});

canvas.addEventListener('dblclick',e=>{
  const cl=getCanvasLocal(e);const wp=s2w(cl.x,cl.y);
  const hit=nodes.find(n=>Math.hypot(n.x-wp.x,n.y-wp.y)<n.r*2.5);
  if(hit){ openRename(hit,e.clientX,e.clientY); return; }
  // v2.14.10: double-clicking a chamber renames it, mirroring ant rename exactly.
  const ch=chamberAt(wp.x,wp.y);
  if(ch) openChamberRename(ch,e.clientX,e.clientY);
});

canvas.addEventListener('wheel',e=>{
  // v3.3.0: the colony is a WIDGET inside a scrolling dashboard now, not the page background.
  // Swallowing every wheel event used to be free — the workspace page could not scroll — but it
  // now traps the operator: scrolling down to reach the widgets BELOW the Colony zoomed the map
  // and the page never moved. Zoom takes a modifier; a plain wheel scrolls the dashboard. This is
  // the same bargain embedded maps strike on scrolling pages, and for the same reason.
  // v0.3.8.55 (field report: "can't zoom on colony view"): on the DEDICATED Colony page the
  // canvas is the page — there is nothing below to scroll to, so the modifier bargain protects
  // nothing and just makes zoom look broken. Plain wheel zooms there; embedded in the scrolling
  // dashboard the Ctrl/Cmd bargain stands, for the same reason embedded maps strike it.
  const dedicated=document.getElementById('page-colony')?.classList.contains('active');
  if(!(e.ctrlKey||e.metaKey||dedicated)) return;      // no preventDefault: let the page scroll
  e.preventDefault();
  const cl=getCanvasLocal(e);
  const zf=e.deltaY<0?1.1:.91,nz=Math.max(.2,Math.min(4,tZ*zf));
  const wx=(cl.x-cx-tX)/tZ+cx,wy=(cl.y-cy-tY)/tZ+cy;
  tZ=nz;tX=cl.x-cx-(wx-cx)*nz;tY=cl.y-cy-(wy-cy)*nz;
},{passive:false});

function statusColor(s){return{complete:'var(--green)',failed:'var(--red)',running:'var(--queen)',blocked:'var(--purple)'}[s]||'var(--dim)';}

/* ---------------------------------------------------------------------------------------------
 * v2.14.13 — Editable Ant Inspector (Stage 3e).
 *
 * Clicking an ant opens this in the existing #colony-right Agent Inspector card. It SHOWS the
 * ant's truthful runtime state and contract, and EDITS exactly three things, each through a
 * persistence path that already exists:
 *
 *   name   -> uiState.castes[role].name   (the same key the double-click rename writes)
 *   colour -> uiState.castes[role].color  (via casteColor/applyUiState)
 *   model  -> POST /settings {model_routes} with normal auth — never a new write path
 *
 * It never grants a capability and never edits permissions, tool allowlists, or path allowlists;
 * those are contract-owned and display-only here.
 * ------------------------------------------------------------------------------------------- */

/** The caste an inspector edit applies to. Workers inherit their caste's name and colour. */
function inspectorCasteFor(n){
  if(!n) return null;
  if(n.id==='queen') return 'queen';
  return n.ant||null;
}

function runtimeStatusFor(roleIdStr){
  return antRuntimeStatus[String(roleIdStr||'').toLowerCase()]||null;
}

/** This node's real pheromone strength — the same trails the canvas draws, not a proxy. */
function inspectorTrailStrength(n){
  const t=antTrailStrengths();
  return Math.max(t[n.id]||0, t[n.worker]||0, t[n.ant]||0);
}

/**
 * Whether this caste has a model route at all. Mirrors the Ant Config rule exactly so the two
 * surfaces cannot disagree about which ants are routable.
 */
function casteIsRoutable(caste){
  if(caste==='queen'||caste==='director') return false;
  const reg=registryRoleById(caste);
  return reg ? roleExecutable(reg)!==false : false;
}

/** The editor block. Returns '' for nodes with no caste (nothing safe to edit). */
function inspectorEditorHtml(n){
  const caste=inspectorCasteFor(n);
  if(!caste) return '';
  const name=casteName(caste), color=cssColor(casteColor(caste));
  const rt=runtimeStatusFor(caste);
  const routable=casteIsRoutable(caste);
  const cur=modelRoutes[caste]||{};
  const provider=uiState.castes[caste]?.provider||cur.provider||'ollama';
  const model=uiState.castes[caste]?.model||cur.model||'';

  const scope=n.nodeType==='worker'
    ? `<div class="ad-note">Editing the <b>${escapeHtml(casteName(caste))}</b> caste — workers inherit its name and colour.</div>`
    : '';

  let modelBlock;
  if(!routable){
    // Deliberately not a disabled control that looks editable: say why there is nothing to set.
    const why = caste==='queen'||caste==='director'
      ? 'Control plane — routes missions, never runs a model itself.'
      : (rt&&rt.unavailability_reason) || 'Not executable — no model route applies.';
    modelBlock=`<div class="ad-note">${escapeHtml(why)}</div>`;
  } else if(!antRouteCatalogReady){
    modelBlock=`<div class="ad-note">Loading model catalog…</div>`;
  } else {
    modelBlock=`
      <label class="ad-lbl" for="ins-provider">Provider</label>
      <select class="ad-input" id="ins-provider" data-insact="provider">${antcfgProviderOptions(provider)}</select>
      <label class="ad-lbl" for="ins-model">Model route</label>
      <select class="ad-input" id="ins-model" data-provider="${escapeHtml(provider)}">${antcfgModelOptions(provider,model)}</select>`;
  }

  return `
    <div class="ad-edit">
      ${scope}
      <label class="ad-lbl" for="ins-name">Display name</label>
      <input class="ad-input" id="ins-name" type="text" maxlength="28" value="${escapeHtml(name)}"
             aria-label="Display name for ${escapeHtml(name)}">
      <label class="ad-lbl" for="ins-color">Accent colour</label>
      <input class="ad-input ad-input-color" id="ins-color" type="color" value="${cssColor(color)}"
             aria-label="Accent colour for ${escapeHtml(name)}">
      ${modelBlock}
      <div class="ad-edit-actions">
        <button class="btn btn-ghost" data-insact="save" data-caste="${escapeHtml(caste)}">Save</button>
        <span class="ad-msg" id="ins-msg" role="status" aria-live="polite"></span>
      </div>
    </div>`;
}

/** Runtime + chamber + pheromone rows. Every value here comes from real state or is omitted. */
function inspectorFactsHtml(n){
  const caste=inspectorCasteFor(n);
  const rt=caste?runtimeStatusFor(caste):null;
  const strength=inspectorTrailStrength(n);
  const reg=caste?registryRoleById(caste):null;
  const paths=reg?(roleAllowedPaths(reg).slice(0,4).join(', ')):'';
  const forbiddenPaths=reg?(roleForbiddenPaths(reg).slice(0,4).join(', ')):'';
  return `
    ${n.chamber?`<div class="ad-row"><span class="ad-key">Chamber</span><span class="ad-val">${escapeHtml(chamberLabel(n.chamber))}</span></div>`:''}
    ${rt?`<div class="ad-row"><span class="ad-key">Runtime</span><span class="ad-val" title="${escapeHtml(rt.unavailability_reason||'')}">${escapeHtml(rt.status_label||rt.runtime_kind||'')}</span></div>
    <div class="ad-row"><span class="ad-key">Planner</span><span class="ad-val">${rt.planner_eligible?'eligible':'not eligible'}</span></div>`:''}
    ${rt&&rt.runtime_available===false&&rt.unavailability_reason?`<div class="ad-note">${escapeHtml(rt.unavailability_reason)}</div>`:''}
    <div class="ad-row"><span class="ad-key">Pheromone</span><span class="ad-val">${strength>0?strength.toFixed(2):'no trail yet'}</span></div>
    ${paths?`<div class="ad-section">Workspace Paths</div>
    <div class="ad-fine">Allowed: ${escapeHtml(paths)}${forbiddenPaths?`<br>Forbidden: ${escapeHtml(forbiddenPaths)}`:''}</div>`:''}`;
}

async function inspectorSave(caste){
  const msg=document.getElementById('ins-msg');
  const set=(t,c)=>{ if(msg){ msg.style.color=c; msg.textContent=t; } };
  const nameEl=document.getElementById('ins-name');
  const colEl=document.getElementById('ins-color');
  const provEl=document.getElementById('ins-provider');
  const modEl=document.getElementById('ins-model');

  const nm=(nameEl&&nameEl.value||'').trim().slice(0,28);
  const col=cssColor(colEl&&colEl.value, casteColor(caste));
  const patch={color:col};
  if(nm) patch.name=nm;

  if(provEl&&modEl&&modEl.value){
    const provider=provEl.value||'ollama', model=modEl.value;
    patch.model=model;
    patch.provider=provider!=='ollama'?provider:undefined;
    try{ await saveModelRoute({[caste]:{provider,model}}); }
    catch(e){ set('Route failed: '+((e&&e.message)||'error'),'var(--red)'); return; }
  }

  uiState.castes[caste]=Object.assign({},uiState.castes[caste],patch);
  applyUiState(); saveUiState();
  set('Saved.','var(--green)');
  setTimeout(()=>{ const m=document.getElementById('ins-msg'); if(m&&m.textContent==='Saved.') m.textContent=''; },2500);
  if(selectedNode) showInspector(selectedNode);
}

// CSP-safe delegated dispatch: the inspector body is re-rendered constantly, so the listener
// lives on the stable container rather than on the controls themselves.
document.getElementById('agent-detail').addEventListener('click',e=>{
  const btn=e.target.closest('[data-insact="save"]');
  if(btn) inspectorSave(btn.dataset.caste);
});
document.getElementById('agent-detail').addEventListener('change',e=>{
  const sel=e.target.closest('[data-insact="provider"]');
  if(!sel) return;
  const modEl=document.getElementById('ins-model');
  if(modEl){ modEl.dataset.provider=sel.value; modEl.innerHTML=antcfgModelOptions(sel.value,''); }
});
/* ---------------------------------------------------------------------------------------------
 * v2.14.14 Stage 7 — topology overlays.
 *
 * The canvas chrome is a set of independently hideable, re-anchorable overlays rather than fixed
 * furniture. State lives in dashboard_workspace.topology_overlays so the C# sanitizer validates
 * it (unknown ids dropped, unknown anchors reset to top-left), and it applies on the Colony page
 * and on the dashboard alike — the chrome belongs to the topology, not to either host.
 *
 * The Overlays button is itself NOT an overlay. If it could be hidden, hiding everything would be
 * unrecoverable without editing ui_state.json by hand.
 * ------------------------------------------------------------------------------------------- */
const TOPOLOGY_OVERLAYS = {
  viewbar: { el:'colony-viewbar', label:'View controls',     anchor:'top-right'     },
  legend:  { el:'chud-legend',    label:'Caste legend',      anchor:'top-left'      },
  signals: { el:'chud-phero',     label:'Learning signals',  anchor:'top-left'      },
  hints:   { el:'zoom-hint',      label:'Interaction hints', anchor:'bottom-center' },
};
const OVERLAY_ANCHORS=['top-left','top-center','top-right','bottom-left','bottom-center','bottom-right'];

/** Read persisted overlay state out of a /ui/state document, falling back to the defaults. */
function overlayStateFrom(doc){
  const saved=((doc||{}).dashboard_workspace||{}).topology_overlays||{};
  const out={};
  Object.keys(TOPOLOGY_OVERLAYS).forEach(id=>{
    const def=TOPOLOGY_OVERLAYS[id], got=saved[id]||{};
    out[id]={
      visible: got.visible!==false,
      anchor: OVERLAY_ANCHORS.indexOf(got.anchor)>=0 ? got.anchor : def.anchor,
      collapsed: got.collapsed===true,   // v0.3.8.55: the legends fold to their header
    };
  });
  return out;
}

function overlayState(id){
  uiState.overlays=uiState.overlays||{};
  if(!uiState.overlays[id]){
    const def=TOPOLOGY_OVERLAYS[id];
    uiState.overlays[id]={visible:true,anchor:def?def.anchor:'top-left'};
  }
  return uiState.overlays[id];
}

/**
 * Overlays are moved into one of six anchor SLOTS rather than positioned individually. Two
 * overlays sharing an anchor then stack in the slot's flex flow instead of drawing on top of each
 * other — which is what the legend and the signals panel do by default, exactly as they always
 * looked when they were hard-coded siblings.
 */
function applyOverlayState(){
  Object.keys(TOPOLOGY_OVERLAYS).forEach(id=>{
    const el=document.getElementById(TOPOLOGY_OVERLAYS[id].el);
    if(!el) return;
    const st=overlayState(id);
    el.classList.add('topo-ov');
    el.classList.toggle('ov-hidden',!st.visible);
    // v0.3.8.55 (field report): the legend panels COLLAPSE to their header — distinct from
    // hiding (the Overlays menu) because a folded legend still shows it exists and reopens with
    // one click. Only panels with a .chud-body fold; the class is inert on the rest.
    el.classList.toggle('ov-collapsed',!!st.collapsed);
    const caret=el.querySelector('.ov-caret'); if(caret) caret.textContent=st.collapsed?'▸':'▾';
    // Hidden chrome must leave the tab order, or keyboard users tab into invisible controls.
    el.setAttribute('aria-hidden',st.visible?'false':'true');
    const slot=document.querySelector('[data-ovslot="'+st.anchor+'"]');
    if(slot&&el.parentElement!==slot) slot.appendChild(el);
  });
  refreshOverlayMenu();
}

function setOverlay(id,changes){
  if(!TOPOLOGY_OVERLAYS[id]) return;
  const st=overlayState(id);
  if(typeof changes.visible==='boolean') st.visible=changes.visible;
  if(typeof changes.collapsed==='boolean') st.collapsed=changes.collapsed;
  if(changes.anchor&&OVERLAY_ANCHORS.indexOf(changes.anchor)>=0) st.anchor=changes.anchor;
  applyOverlayState();
  saveUiState();
}
// v0.3.8.55: one delegated handler folds/unfolds whichever legend header was clicked — the
// headers are re-rendered with their panels, so per-render listeners would leak or vanish.
document.addEventListener('click',e=>{
  const hd=e.target.closest&&e.target.closest('[data-ovcollapse]');
  if(!hd) return;
  const id=hd.dataset.ovcollapse;
  setOverlay(id,{collapsed:!overlayState(id).collapsed});
});

function resetOverlays(){
  uiState.overlays={};
  Object.keys(TOPOLOGY_OVERLAYS).forEach(id=>overlayState(id));
  applyOverlayState();
  saveUiState();
}

/**
 * v2.15.2: overlay control moved into the workspace Modules menu.
 *
 * It previously lived in a separate "Overlays" button pinned to the canvas, which meant two
 * different places to manage what is on screen. The Modules menu already lists every panel, so
 * overlays belong in the same list — and that menu lives in the always-present workspace toolbar,
 * so hiding every overlay is still recoverable, which was the standalone button's only real job.
 *
 * dashboard-workspace.js renders the rows; this is the bridge it reads and writes through. Kept
 * deliberately small so the two modules share state rather than each keeping their own copy.
 */
window.AnthillTopologyOverlays = {
  list: function(){
    return Object.keys(TOPOLOGY_OVERLAYS).map(function(id){
      var st=overlayState(id);
      return { id:id, label:TOPOLOGY_OVERLAYS[id].label, visible:st.visible, anchor:st.anchor };
    });
  },
  anchors: function(){ return OVERLAY_ANCHORS.slice(); },
  set: function(id,changes){ setOverlay(id,changes); },
  reset: function(){ resetOverlays(); },
};

/** Re-render whichever surface is currently showing overlay rows. */
function refreshOverlayMenu(){
  if(window.AnthillWorkspace && typeof window.AnthillWorkspace.rerender==='function')
    window.AnthillWorkspace.rerender();
}



function showInspector(n){
  // v0.3.8.61 (caught live): the inspector pane is the Agent Inspector WIDGET's body now, and a
  // hidden widget's frame is detached — getElementById returns null and every canvas click threw.
  // Selection state still updates so the pane is correct the moment the widget is shown again.
  selectedNode=n;
  if(!document.getElementById('agent-detail')) return;
  // The model catalog is only needed once an ant is actually inspected. Re-render exactly once
  // when it lands, and only if this same node is still the selection.
  if(!antRouteCatalogReady) ensureAntRouteCatalog().then(()=>{ if(selectedNode===n) showInspector(n); });
  const act=n.activity,pct=Math.round(act*100);
  const tasks=n.nodeType==='worker'
    ? (lastGraphData?.worker_counts?.[n.worker]??0)
    : (lastGraphData?.ant_counts?.[n.ant]??0);
  // v0.3.8.49 (§18): the inspector names the ant's discrete STATE, not just Active/Idle — the same
  // word the ring shows, so hovering an amber ring and reading the panel agree.
  const statusLine=n.id==='queen'?'Commander':(n.state&&n.state!=='idle'?STATE_LABEL[n.state]:(colonyRunning&&act>0?'Active':'Idle'));
  // Real live task load for this caste from the current mission graph.
  const antTasks=(lastGraphData?.nodes||[]).filter(t=>n.nodeType==='worker'?t.assigned_worker===n.worker:t.assigned_ant===n.ant);
  const tRunning=antTasks.filter(t=>t.status==='running').length;
  const tDone=antTasks.filter(t=>t.status==='complete').length;
  const tFailed=antTasks.filter(t=>t.status==='failed').length;
  const perms=permList(n.permissions||{}).join(', ')||'read/status only';
  const tools=(n.allowedTools||[]).slice(0,6).join(', ')||'none listed';
  const forbidden=(n.forbiddenTools||[]).slice(0,5).join(', ')||'none listed';
  const tele=n.nodeType==='worker'?workerTelemetryById(n.worker):null;
  const usage=tele?.usage||{},audit=tele?.audit||{},metric=tele?.metric||{};
  const totalRuntimeTasks=usage.task_count??usage.taskCount??usage.TaskCount??tasks;
  const auditCount=audit.audit_count??audit.auditCount??audit.AuditCount??0;
  const metricCount=metric.metric_count??metric.metricCount??metric.MetricCount??0;
  const avgElapsed=usage.avg_elapsed_seconds??usage.avgElapsedSeconds??usage.AvgElapsedSeconds??0;
  document.getElementById('agent-detail').innerHTML=`
    <div class="ad-name">${escapeHtml(n.label)}</div>
    <div class="ad-type" style="color:${cssColor(n.color)}">${escapeHtml(n.role)} · ${escapeHtml(n.worker||n.ant||'queen')}</div>
    <div class="ad-row"><span class="ad-key">Status</span><span class="ad-val ${colonyRunning&&act>0?'active':'idle'}">${statusLine}</span></div>
    <div class="ad-row"><span class="ad-key">Type</span><span class="ad-val">${escapeHtml(n.nodeType||'role')}</span></div>
    ${n.parent?`<div class="ad-row"><span class="ad-key">Parent</span><span class="ad-val">${escapeHtml(n.parent)}</span></div>`:''}
    ${n.colony?`<div class="ad-row"><span class="ad-key">Colony</span><span class="ad-val">${escapeHtml(n.colony)}</span></div>`:''}
    ${n.enabled===false?`<div class="ad-row"><span class="ad-key">Enabled</span><span class="ad-val" style="color:var(--red)">false</span></div>`:''}
    ${inspectorFactsHtml(n)}
    <div class="ad-row"><span class="ad-key">Activity</span><span class="ad-val">${pct}%</span></div>
    <div class="ad-row"><span class="ad-key">Task Count</span><span class="ad-val">${tasks}</span></div>
    ${n.nodeType==='worker'?`<div class="ad-row"><span class="ad-key">Runtime Tasks</span><span class="ad-val">${totalRuntimeTasks}</span></div>
    <div class="ad-row"><span class="ad-key">Audits</span><span class="ad-val">${auditCount}</span></div>
    <div class="ad-row"><span class="ad-key">Metrics</span><span class="ad-val">${metricCount}</span></div>
    <div class="ad-row"><span class="ad-key">Avg Time</span><span class="ad-val">${avgElapsed}s</span></div>`:''}
    <div class="ad-section">Configure</div>
    ${inspectorEditorHtml(n)}
    ${n.purpose?`<div class="ad-section">Purpose</div><div style="font-size:10px;line-height:1.5;color:var(--muted)">${escapeHtml(n.purpose)}</div>`:''}
    <div class="ad-section">Permissions</div>
    <div style="font-size:10px;line-height:1.5;color:var(--muted)">${escapeHtml(perms)}</div>
    <div class="ad-section">Tools</div>
    <div style="font-size:10px;line-height:1.5;color:var(--muted)">Allowed: ${escapeHtml(tools)}<br>Forbidden: ${escapeHtml(forbidden)}</div>
    <div class="ad-section">Live Task Load</div>
    <div class="ad-bar"><div class="ad-bar-fill" style="width:${pct}%;background:linear-gradient(90deg,${cssColor(n.color)}88,${cssColor(n.color)})"></div></div>
    <div class="ad-row" style="margin-top:6px"><span class="ad-key">Running</span><span class="ad-val">${tRunning}</span></div>
    <div class="ad-row"><span class="ad-key">Completed</span><span class="ad-val" style="color:${tDone?'var(--green)':''}">${tDone}</span></div>
    <div class="ad-row"><span class="ad-key">Failed</span><span class="ad-val" style="color:${tFailed?'var(--red)':''}">${tFailed}</span></div>
    ${(()=>{ // v1.8.23: worker sub-caste breakdown for this ant (Ant Capability Profiles).
      const wc={};
      antTasks.forEach(t=>{ const w=t.assigned_worker||t.assigned_agent; if(w&&w!==n.ant){wc[w]=(wc[w]||0)+1;} });
      const ent=Object.entries(wc);
      if(!ent.length) return '';
      return `<div class="ad-section">Workers</div>`+ent.map(([w,c])=>`
        <div class="ad-row"><span class="ad-key" title="${escapeHtml(w)}">${escapeHtml(w.split('.').pop())}</span><span class="ad-val">${c}</span></div>`).join('');
    })()}
    ${antTasks.slice(0,4).map(t=>`
      <div style="margin-top:6px;padding:6px 8px;background:rgba(255,255,255,.03);border-radius:4px;border:1px solid var(--border)">
        <div style="font-size:9px;color:var(--dim);margin-bottom:2px">${escapeHtml(t.assigned_worker||t.assigned_ant)} · ${escapeHtml(t.task_type||'task')} · <span style="color:${cssColor(statusColor(t.status))}">${escapeHtml(t.status)}</span></div>
        <div style="font-size:10px;color:var(--muted)">${escapeHtml((t.title||'').substring(0,55))}</div>
      </div>`).join('')||''}`;
}

// -- Render Loop ---------------------------------------------------------------
/* ---------------------------------------------------------------------------------------------
 * v2.14.13 Stage 6 — the topology IS the dashboard canvas.
 *
 * There is exactly ONE canvas, one render loop, and one polling path. Rather than instantiating a
 * second renderer for the dashboard, the existing #colony-canvas-area element is RE-PARENTED
 * between its two hosts: the Colony page and the workspace topology layer. Same node, same
 * listeners, same state — so ant drag, chamber drag, panning, zoom, and the inspector all keep
 * working without being reimplemented or duplicated.
 *
 * The canvas takes its size from its container, so resize() must run AFTER the move lands, on the
 * next frame, once layout has settled. Measuring during the move yields 0x0 and collapses every
 * ant onto the origin.
 * ------------------------------------------------------------------------------------------- */
let topologyHost='colony';          // 'colony' | 'dashboard'
let topologyHome=null;              // original DOM slot, captured before the first move

function topologyCaptureHome(){
  if(topologyHome) return;
  const area=document.getElementById('colony-canvas-area');
  if(area&&area.parentElement) topologyHome={parent:area.parentElement,next:area.nextSibling};
}

/** Re-measure after the element has actually been laid out in its new host. */
function topologyRemeasure(){
  requestAnimationFrame(()=>{ resize(); buildNodes(); refreshTopologyAwake(); });
}

function topologyMountTo(where){
  const area=document.getElementById('colony-canvas-area');
  if(!area) return;
  topologyCaptureHome();
  let target=null;
  if(where==='dashboard') target=document.getElementById('ws-topology');
  else if(where==='chat') target=document.getElementById('chat-colony-mount');   // v3.8.39
  else if(topologyHome) target=topologyHome.parent;
  if(!target) return;
  if(area.parentElement===target && where===topologyHost) return;   // already correct: no churn
  if(where==='dashboard'||where==='chat') target.appendChild(area);
  else target.insertBefore(area, topologyHome.next);
  topologyHost=where;
  topologyRemeasure();
}

/**
 * Whether the map is worth drawing. Deliberately conservative: only a backgrounded tab and a
 * zero-sized canvas suppress rendering. Occlusion-based throttling is NOT claimed here — a
 * wrong "it's hidden" is how the canvas silently freezes, and this repo has paid for that twice.
 */
let topologyAwake=true;
function refreshTopologyAwake(){
  // Measure the container rather than trusting the last resize(): clientWidth is 0 whenever an
  // ancestor is display:none, which is exactly the "user navigated away" case. That makes this a
  // fact about the DOM, not a guess about visibility.
  //
  // v0.3.8.55 (field report: "UI needs a refresh"): document.hidden is GONE from this gate — the
  // third payment for a wrong "it's hidden". Embedded webviews (the desktop shell) and occluded
  // windows report visibilityState 'hidden' while the operator is looking straight at the canvas,
  // which latched the loop off until a manual reload. The genuine backgrounded-tab case needs no
  // help from us: the browser stops firing requestAnimationFrame there all by itself.
  const el=document.getElementById('colony-canvas-area');
  topologyAwake = !!el && el.clientWidth>0 && el.clientHeight>0;
}
document.addEventListener('visibilitychange',refreshTopologyAwake);
// v0.3.8.55 (field report: "default layout scrambled"): navigation re-measures at a FIXED 50ms,
// but the dashboard-grid lays its widgets out on its own clock — a canvas measured before its
// widget settles computes the ring around a stale centre and the map lands scrambled until a
// manual refresh. The observer makes layout follow the CONTAINER's actual size, whenever it
// changes, debounced one frame so a drag-resize doesn't relayout per-pixel.
(function(){
  const area=document.getElementById('colony-canvas-area');
  if(!area || typeof ResizeObserver==='undefined') return;
  let raf=null;
  new ResizeObserver(()=>{
    if(raf) return;
    raf=requestAnimationFrame(()=>{ raf=null; resize(); buildNodes(); refreshTopologyAwake(); });
  }).observe(area);
})();

// v0.3.8.43 (SOW §4): reduced motion, honored at the render loop. When the operator asks for
// reduced motion and the colony is genuinely idle (no running mission), the map redraws at 4fps —
// enough to stay current, still, and honest — and returns to full rate the moment real work
// starts, because at that point the motion IS the information.
const REDUCED_MOTION = typeof matchMedia==='function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
let _lastReducedDraw=0;
function loop(ts){
  requestAnimationFrame(loop);
  // v2.14.13: the topology now renders on the dashboard too, i.e. effectively always. Skip the
  // draw when the tab is backgrounded or the canvas has no area; the rAF keeps ticking so the
  // map resumes instantly with no re-init.
  if(!topologyAwake) return;
  if(REDUCED_MOTION && !colonyRunning){
    if(ts-_lastReducedDraw<250) return;
    _lastReducedDraw=ts;
  }
  camZ+=(tZ-camZ)*.1;camX+=(tX-camX)*.1;camY+=(tY-camY)*.1;
  drawBg();
  drawChambers();                  // v2.14.5: chamber grouping lives on this canvas, under the edges
  edges.forEach(e=>drawEdge(e));
  if(colonyPheromones!=='off') drawPheromoneField(); // Real-pheromone drift under structural edges
  if(showHandoffs) dataFlowEdges.forEach(e=>drawDataFlowEdge(e,ts));
  maybeSpawn(ts);
  particles=particles.filter(p=>(p.t+=p.speed)<=1);
  drawParticles();
  nodes.forEach(n=>{
    // NB: parentheses matter — this used to parse as `colonyActivity || (0-n.activity)`,
    // which accumulated activity unboundedly every frame (tooltips showing >100%).
    if(n.id!=='queen'){const key=n.worker||n.ant;n.activity=Math.min(1,Math.max(0,n.activity+((colonyActivity[key]||0)-n.activity)*.06));}
    drawNode(n,ts);
  });
}
requestAnimationFrame(loop);

// -- Real data polling ----------------------------------------------------------
let lastGraphData=null,lastEventText='',activeJobId=null,jobPollTimer=null,connected=false;
let selectedJobId=null;

function setConnected(ok){
  connected=ok;
  renderStatusChip(); // the header chip's online dot reflects API + backend health
}

function setEl(id,val){ const e=document.getElementById(id); if(e) e.textContent=val; }

async function pollStatus(){
  try{
    const r=await api('/status'); if(!r.success) return;
    const d=r.data;
    setEl('s-calls',d.model_calls??0);
    setEl('s-tasks',d.tasks??0);
    setEl('s-events',d.events??0);        setEl('ov-events',d.events??0);
    setEl('s-approvals',d.pending_approvals??0); setEl('ov-approvals',d.pending_approvals??0);
    modelCallRate=Math.max(0,(d.model_calls??0)-totalModelCalls);
    totalModelCalls=d.model_calls??0;
    setConnected(true);
    const pending=d.pending_approvals??0;
    const bc=document.getElementById('bell-count');
    bc.style.display=pending>0?'block':'none';
    bc.textContent=pending;
    // v0.3.8.49 (§18): a pending approval is a colony state, not just a badge — the map shows the
    // change-proposing ants awaiting approval so the operator sees WHERE the decision sits.
    colonyAwaitingApproval=pending>0;
    if(colonyAwaitingApproval){ ['coder','builder'].forEach(k=>{ colonyAntState[k]='awaiting_approval'; }); updateNodeActivity(); }
  }catch{ setConnected(false); }
}

// -- System Health panel (Overview) --------------------------------------------
async function pollHealth(){
  // Only fetch when the Overview is the visible page — keeps it cheap.
  if(!document.getElementById('page-overview')?.classList.contains('active')) return;
  try{
    const [a,m,ev]=await Promise.all([api('/autonomy/status'),api('/maintenance/stats'),api('/events/json?limit=600')]);
    const s=a.data||{}, d=m.data||{};
    // Autonomy
    const running=!!s.running, killed=!!s.kill_switch_engaged, enabled=!!s.enabled;
    // v3.8.34: a presentation layer over the autonomy state, per the status vocabulary rule.
    //
    // This read RUNNING / HALTED / IDLE / OFF, and HALTED was red — the loudest thing on a default
    // dashboard, with nothing saying what it meant or whether it was bad. It is not a fault: the
    // kill switch is engaged BY an operator, from a confirmation that spells that out. Red for a
    // state someone chose on purpose teaches people to ignore red.
    //
    // The internal state stays available on hover rather than being thrown away.
    const autoState=running?'RUNNING':(killed?'HALTED':(enabled?'IDLE':'OFF'));
    const AUTO_LABEL={RUNNING:'Working',HALTED:'Stopped',IDLE:'Ready',OFF:'Off'};
    const AUTO_WHY={
      RUNNING:'The colony is working through its own objectives right now.',
      HALTED:'Automatic work is stopped by the safety switch. Nothing runs on its own until you clear it — you can still send missions yourself.',
      IDLE:'Automatic work is switched on, with nothing to do at the moment.',
      OFF:'Automatic work is switched off. The colony only runs missions you send it.',
    };
    setEl('ov-h-auto',AUTO_LABEL[autoState]||autoState);
    const autoEl=document.getElementById('ov-h-auto');
    if(autoEl) autoEl.title=(AUTO_WHY[autoState]||'')+' (internal state: '+autoState+')';
    // Amber, not red: stopped-on-purpose is not a failure. Red stays for things that broke.
    autoEl && (autoEl.style.color=running?'var(--green)':(killed?'var(--amber,#f59e0b)':'var(--muted)'));
    // Amber dot to match the amber text — a state the operator chose is not an error. Guarded for
    // the same reason as pollHud: these are widget bodies, and a hidden widget has none.
    const autoDot=document.getElementById('ov-h-auto-dot');
    if(autoDot) autoDot.className='health-dot '+(running?'ok':(killed?'warn':''));
    setEl('ov-h-auto-day',`${s.missions_last_day??0}/${s.max_missions_per_day??'—'}`);
    setEl('ov-h-auto-backlog',`${(s.backlog_pending??0)+(s.backlog_active??0)}`);
    setEl('ov-h-auto-conc',`${s.concurrency_effective??s.concurrency_configured??1}/${s.concurrency_configured??1}`);
    // Storage
    const free=d.disk_free_bytes||0, total=d.disk_total_bytes||1;
    const usedPct=Math.round((1-free/total)*100);
    setEl('ov-h-disk',`${humanBytes(free)} free`);
    const bar=document.getElementById('ov-h-disk-bar'); bar.style.width=usedPct+'%';
    const diskWarn=usedPct>=85, diskDanger=usedPct>=93;
    bar.style.background=diskDanger?'var(--red)':(diskWarn?'var(--queen)':'var(--green)');
    const diskDot=document.getElementById('ov-h-disk-dot');
    if(diskDot) diskDot.className='health-dot '+(diskDanger?'err':(diskWarn?'warn':'ok'));
    setEl('ov-h-db',humanBytes(d.db_bytes));
    setEl('ov-h-backups',`${d.backup_count||0} · ${humanBytes(d.backup_bytes)}`);
    // Coder patch health (from recent events)
    const c={}; (ev.data?.events||[]).forEach(e=>{const t=e.event_type||'';c[t]=(c[t]||0)+1;});
    const ok=c['patch_set_created']||0, fail=c['patch_proposal_parse_failed']||0, applied=c['autonomy_autoapply_applied']||0;
    const rate=(ok+fail)>0?Math.round(ok/(ok+fail)*100):null;
    setEl('ov-h-coder',rate==null?'—':rate+'%');
    const coderEl=document.getElementById('ov-h-coder');
    if(coderEl) coderEl.style.color=rate==null?'var(--muted)':(rate>=80?'var(--green)':(rate>=50?'var(--queen)':'var(--red)'));
    const coderDot=document.getElementById('ov-h-coder-dot');
    if(coderDot) coderDot.className='health-dot '+(rate==null?'':(rate>=80?'ok':(rate>=50?'warn':'err')));
    setEl('ov-h-coder-ok',ok); setEl('ov-h-coder-fail',fail); setEl('ov-h-coder-applied',applied);
    // Alert line — nudge reclaimable disk
    const alert=document.getElementById('ov-health-alert');
    const reclaimable=(d.backup_count||0)>(d.max_db_backups||10);
    if(diskWarn && reclaimable){ alert.style.display=''; alert.textContent=`⚠ ${humanBytes(d.backup_bytes)} of backups reclaimable · Settings → Maintenance → Flush Cache`; }
    else alert.style.display='none';
  }catch{}
}

// -- HUD design-system helpers (v1.8.23) --------------------------------------
function hudBadge(status,label){ const s=(status||'unknown').toString().toLowerCase();
  return `<span class="hud-badge ${s}">${escapeHtml(label||s)}</span>`; }
function hudRisk(level){ const l=(level||'unknown').toString().toLowerCase();
  return `<span class="hud-risk ${l}">${escapeHtml(l)}</span>`; }
// Map a mission/objective/job status onto a canonical HUD badge state.
// v3.8.34: presentation layer over the job status vocabulary, in the shape ATTEMPT_STATE_LABEL
// already established — worded as the fact rather than the verdict. Only statuses whose raw name
// misleads are translated; the rest pass through, because renaming a clear state to a friendlier
// one costs the operator the word they will find in the logs.
const JOB_STATUS_LABEL = {
  // Not a failure and not a success: the colony stopped on purpose and wants a person to decide.
  escalated: 'needs you',
  timed_out: 'timed out',   // v0.3.8.42: the raw status rendered with its underscore
};
const JOB_STATUS_TITLE = {
  escalated: 'The colony stopped and handed this to you — nothing broke, it declined to continue without your judgment. Internal state: escalated.',
};
// v0.3.8.42: ONE terminal-status set, keyed to ApiJobRegistry.IsTerminalStatus
// (complete | failed | cancelled | timed_out | escalated). Before this, three call sites each
// carried their own subset: the active-job poller omitted 'cancelled' and 'timed_out' (locking the
// composer forever after a cancel), and the jobs list omitted 'timed_out' (a timed-out run had no
// View Result and no Re-run). 'partial' stays for rows persisted before v2.26.0 mapped it away.
const JOB_TERMINAL_STATUSES=['complete','failed','cancelled','timed_out','escalated','partial'];
const jobIsTerminal=s=>JOB_TERMINAL_STATUSES.includes(s);
function hudStatusClass(st){ st=(st||'').toString().toLowerCase();
  if(st==='complete') return 'completed'; if(st==='partial') return 'warning';
  if(st==='reverted') return 'rejected'; // v2.7.0: undone patch — neutral/terminal styling
  if(['running','active','queued','complete','partial','failed','paused','pending','approved','applied','rejected','completed','stopped','looping','idle','proposed'].includes(st)) return st==='proposed'?'pending':st;
  return 'unknown'; }

// v0.3.8.42 (§3): the Mission Command working modes (ovMode / OV_MODE_TEXT / setOvMode) retired
// with the dashboard composer. The constraint wording they prepended is planner vocabulary an
// operator can still type in Chat verbatim; nothing model-facing changed.

// -- Overview command dashboard (v1.8.23) -------------------------------------
// v2.2.6: shared stores — pollHud is the single computer of core state + hardware signals;
// the ov2 cards are pure renderers of these (no second state machine, no drift).
let CORE_STATE=null; // {state,text,sub,at}
let GOV=null;        // {load,memFree,lat,backendOk,concEff,concCfg,at}
async function pollHud(){
  if(!document.getElementById('page-overview')?.classList.contains('active')) return;
  let st,au,jb,pt,ob,mn,ph,cfg;
  try{
    [st,au,jb,pt,ob,mn,ph,cfg]=await Promise.all([
      api('/status'), api('/autonomy/status'), api('/jobs'),
      api('/patches?limit=200'), api('/objectives'), api('/missions/json?limit=8'),
      // Provider circuit-breaker health. Isolated so a hiccup here can never blank the whole HUD.
      api('/providers/health').catch(()=>({success:false})),
      // v0.3.8.35 — configuration findings. RuntimeConfigValidator has produced severity-tagged
      // findings for combinations that cannot work (an enabled feature whose dependency is off, a
      // gate that contradicts another) since v2.x, exposed at /config/health, and the console had
      // never asked. A validator whose output nobody reads is the same defect as a status field
      // nobody consumes — see StatusFieldConsumerTests.
      api('/config/health').catch(()=>({success:false})),
    ]);
  }catch{ return; } // transient; existing panels keep their last values
  const s=(st&&st.data)||{}, a=(au&&au.data)||{};
  const jobs=Array.isArray(jb&&jb.data)?jb.data:[];
  const patches=Array.isArray(pt&&pt.data)?pt.data:[];
  const objectives=Array.isArray(ob&&ob.data)?ob.data:[];
  const missions=Array.isArray(mn&&mn.data)?mn.data:[];
  const sum=(typeof lastSystemSummary==='object'&&lastSystemSummary)?lastSystemSummary:{};

  // -- Derived state (memoized locals) --
  const activeJobs=jobs.filter(j=>['running','queued'].includes((j.status||'').toLowerCase())).length;
  const pendingApprovals=s.pending_approvals??0;
  const highRiskPatches=patches.filter(p=>(p.risk==='high')&&(p.status==='proposed'||p.approval_status==='pending')).length;
  const failedMissions=missions.filter(m=>(m.status||'')==='failed').length;
  const retiredObjectives=objectives.filter(o=>o.retired_code==='looping_goals'||o.end_reason==='retired_looping').length;
  const failedObjectives=objectives.filter(o=>o.end_reason==='failed').length;
  const activeObjectives=objectives.filter(o=>['active','pending'].includes((o.status||'').toLowerCase()) && o.retired_code!=='looping_goals' && (o.status||'')!=='done').length;
  const providerDown = sum.use_ollama && sum.ollama_reachable===false;
  // v3.8.33 — Ollama up, but no usable model. The server has computed this since v2.4.3 and NOTHING
  // read it, so the chip stayed green while every ant call failed with `model not found`. That is
  // the "implemented, tested, unreachable" pattern, in the console.
  const modelUnusable = sum.use_ollama && sum.ollama_reachable===true &&
    (sum.model_resolved===false || sum.ollama_model_present===false);
  // Circuit-breaker health: routes that have tripped (open) or are probing back (half_open).
  const providers=Array.isArray(ph&&ph.data&&ph.data.providers)?ph.data.providers:[];
  const degradedProviders=providers.filter(p=>['open','half_open'].includes(String(p.state||'').toLowerCase()));
  const autoRunning=!!a.running, autoKilled=!!a.kill_switch_engaged, autoEnabled=!!a.enabled;
  const warnings=failedMissions+retiredObjectives+failedObjectives+highRiskPatches+(providerDown?1:0)+(degradedProviders.length?1:0);

  // v2.2.6: the hidden #hud-strip writers are gone (the strip was retired in v2.2.1; rendering
  // into display:none elements every 6s was dead work, and several lookups were unguarded).

  // -- Central system core — SINGLE source of truth (v2.2.6) --
  // pollHud sees the full picture (autonomy, objectives, patches, provider); it computes the core
  // state and publishes it for renderOv2Core so the visible card can't drift or under-report.
  let coreState='idle', coreText='IDLE', coreSub='standby';
  if(failedMissions>0||providerDown||retiredObjectives>0||failedObjectives>0){
    coreState='alert'; coreText='ALERT';
    coreSub=providerDown?'provider offline':failedMissions>0?'mission failed':'objective retired';
  } else if(pendingApprovals>0||highRiskPatches>0){
    coreState='action'; coreText='OPERATOR ACTION';
    coreSub=highRiskPatches>0?`${highRiskPatches} high-risk patch`:`${pendingApprovals} awaiting review`;
  } else if(autoRunning){ coreState='autonomy'; coreText='AUTONOMY ONLINE'; coreSub=`${activeObjectives} active objective${activeObjectives===1?'':'s'}`; }
  else if(activeJobs>0){ coreState='mission'; coreText='MISSION ACTIVE'; coreSub=`${activeJobs} in flight`; }
  else { coreState='idle'; coreText='IDLE'; coreSub=autoEnabled?'autonomy standby':'awaiting directive'; }
  CORE_STATE={state:coreState,text:coreText,sub:coreSub,at:Date.now()};
  if(typeof renderOv2CoreOrb==='function') renderOv2CoreOrb(); // refresh the visible card in place

  // -- Hardware / environment metrics (v2.2.6): published for the Resource Usage card instead of
  // being rendered into the deleted hidden #hud-metrics row (those lookups were unguarded).
  const sig=a.governor_signals||{};
  GOV={load:sig.load_per_core, memFree:sig.memory_available_fraction, lat:sig.backend_latency_ms,
    backendOk:sig.backend_ok, concEff:a.concurrency_effective, concCfg:a.concurrency_configured, at:Date.now()};
  setEl('ov-events-sub', (s.events??0)+' events logged');

  // -- Operator attention --
  const attn=[];
  if(pendingApprovals>0) attn.push({sev:'warning',title:`${pendingApprovals} pending patch approval${pendingApprovals===1?'':'s'}`,reason:'Review and approve or reject in Changes.',go:"showPage('patches')"});
  if(highRiskPatches>0) attn.push({sev:'error',title:`${highRiskPatches} high-risk patch waiting`,reason:'High-risk change needs careful review before apply.',go:"openPatches({risk:'high'})"});
  if(providerDown) attn.push({sev:'error',title:'Model backend unreachable',reason:`Ollama at ${escapeHtml(sum.ollama_host||'?')} did not respond.`,go:"showPage('settings')"});
  // v0.3.8.35 — configuration findings, highest severity first. These name a COMBINATION that
  // cannot work, which is exactly the class of problem an operator cannot deduce from any single
  // settings page.
  const cfgFindings=(cfg&&cfg.success&&Array.isArray(cfg.data&&cfg.data.findings))?cfg.data.findings:[];
  for(const f of cfgFindings.slice(0,3)){
    const sev=String(f.severity||'').toLowerCase()==='error'?'error':'warning';
    attn.push({sev,title:'Configuration: '+escapeHtml(f.combination||'invalid combination'),
      reason:escapeHtml(f.detail||'This combination of settings cannot work as configured.'),
      go:"showPage('settings')"});
  }
  if(modelUnusable) attn.push({sev:'error',title:'No usable model',
    reason: escapeHtml(sum.model_choice_reason || `Ollama is running at ${sum.ollama_host||'?'} but the configured model is not installed.`),
    go:"showPage('settings')"});
  if(degradedProviders.length){
    const d=degradedProviders[0], more=degradedProviders.length>1?` +${degradedProviders.length-1} more`:'';
    const detail=String(d.state||'').toLowerCase()==='open'
      ? `${escapeHtml(String(d.route||'a route'))} is cooling down after repeated timeouts (${Math.round(d.seconds_until_close||0)}s left)`
      : `${escapeHtml(String(d.route||'a route'))} is probing to recover`;
    attn.push({sev:'warning',title:`Model provider degraded${more}`,reason:`${detail}. Calls fast-fail until it recovers.`,go:"showPage('settings')"});
  }
  if(failedMissions>0) attn.push({sev:'error',title:`${failedMissions} recent mission failed`,reason:'Open Results to see what went wrong.',go:"showPage('results')"});
  retiredObjectives>0 && attn.push({sev:'warning',title:`${retiredObjectives} objective retired for looping`,reason:'The Director stopped a looping objective.',go:"showPage('autonomy')"});
  failedObjectives>0 && attn.push({sev:'error',title:`${failedObjectives} objective failed`,reason:'Circuit breaker tripped after repeated failures.',go:"showPage('autonomy')"});
  const attnList=document.getElementById('hud-attn-list');
  setEl('attn-count', attn.length?`${attn.length} item${attn.length===1?'':'s'}`:'');
  const attnPanel=document.getElementById('hud-attn-panel');
  attnPanel&&attnPanel.classList.toggle('glow-warn', attn.length>0);
  // v3.8.34: every write below is to a WIDGET BODY, and a hidden widget has no body in the DOM.
  //
  // `attnPanel` was guarded and `attnList` — the very next line, same widget — was not, so on any
  // dashboard without Operator Attention this threw `Cannot set properties of null` and took the
  // REST OF pollHud with it: the missions, changes and objectives summaries below never ran, on
  // every poll, forever. DEFAULT_DASHBOARD_VIEW ships operator-attention hidden, so this was the
  // default first-run dashboard, and the symptom was silent — four panels that simply stayed empty.
  //
  // Guarded individually rather than behind one early return: which widgets are on screen is the
  // operator's choice, and hiding one must not stop the others from updating.
  if(attnList){
    if(!attn.length){ attnList.innerHTML='<div class="hud-state">✓ No operator action required.</div>'; }
    else attnList.innerHTML=attn.slice(0,6).map(it=>
      `<div class="hud-attn-item" data-onclick="${it.go}"><span class="t-dot ${it.sev}" style="margin-top:5px"></span>`+
      `<div class="a-body"><div class="a-title">${escapeHtml(it.title)}</div><div class="a-reason">${it.reason}</div></div></div>`).join('');
  }

  // -- Summaries --
  const msEl=document.getElementById('ov-sum-missions');
  if(msEl) msEl.innerHTML = missions.length ? missions.slice(0,5).map(m=>
    `<div style="display:flex;align-items:center;gap:8px;padding:5px 2px;cursor:pointer;border-bottom:1px solid rgba(30,51,84,.4)" data-onclick="openResults('${m.id}')">`+
    hudBadge(hudStatusClass(m.status),m.status)+
    `<span style="flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:11px">${escapeHtml(m.goal||'')}</span>`+
    `<span style="color:var(--dim);font-size:9px;white-space:nowrap">${fmtTime(m.saved_at||m.created_at)||''}</span></div>`).join('')
    : '<div class="hud-state">No missions yet — describe what you want done in Mission Command.</div>';

  const pc={proposed:0,approved:0,applied:0,rejected:0,failed:0}; patches.forEach(p=>{ if(pc[p.status]!=null) pc[p.status]++; });
  const chip=(label,val,cls)=>`<span class="hud-badge ${cls}" style="justify-content:center">${label} ${val}</span>`;
  const pxEl=document.getElementById('ov-sum-patches');
  if(pxEl) pxEl.innerHTML = patches.length ?
    `<div style="display:flex;flex-wrap:wrap;gap:6px">`+
      chip('Pending',pc.proposed,'pending')+chip('Approved',pc.approved,'approved')+chip('Applied',pc.applied,'applied')+
      chip('Rejected',pc.rejected,'rejected')+(pc.failed?chip('Failed',pc.failed,'failed'):'')+
      (highRiskPatches?`<span class="hud-risk high" style="justify-content:center">${highRiskPatches} high-risk</span>`:'')+`</div>`
    : '<div class="hud-state">No change proposals yet — missions run in Patch Proposal or Full Build/Test mode create them.</div>';

  const oc={active:0,paused:0,done:0,looping:0,failed:0};
  objectives.forEach(o=>{ const st=(o.status||'').toLowerCase();
    if(o.retired_code==='looping_goals'||o.end_reason==='retired_looping') oc.looping++;
    else if(o.end_reason==='failed') oc.failed++;
    else if(st==='done') oc.done++; else if(st==='paused') oc.paused++;
    else if(st==='active'||st==='pending') oc.active++; });
  const objEl=document.getElementById('ov-sum-objectives');
  if(objEl) objEl.innerHTML = objectives.length ?
    `<div style="display:flex;flex-wrap:wrap;gap:6px">`+
      chip('Active',oc.active,'active')+chip('Completed',oc.done,'completed')+chip('Paused',oc.paused,'paused')+
      (oc.looping?chip('Looping',oc.looping,'looping'):'')+(oc.failed?chip('Failed',oc.failed,'failed'):'')+`</div>`
    : '<div class="hud-state">No objectives yet — add one under Operations → Automation to give the colony standing work.</div>';
}

function renderJobList(jobs, listId, badgeId, limit){
  const badge=document.getElementById(badgeId);
  if(badge) badge.textContent=jobs.length;
  const list=document.getElementById(listId);
  if(!list) return;
  // v3.8.34: "job" is not a second concept — ApiJobRegistry calls this an ApiMissionJob, POST
  // /missions answers "Mission queued.", and every row carries a mission_id. Naming the queue
  // after the thing in it costs the operator nothing and removes an invented distinction.
  if(!jobs.length){ list.innerHTML='<div style="font-size:10px;color:var(--dim);text-align:center;padding:12px 0;">No mission runs yet — dispatching from Mission Command queues one here.</div>'; return; }
  list.innerHTML=jobs.slice(0,limit||8).map(j=>{
    const isRunning=j.status==='running';
    // v3.8.34: 'escalated' is terminal. Omitting it here would leave an adaptively-stopped run
    // with no View Result and no Re-run — finished work the operator could not open.
    // v0.3.8.42: the shared set — 'timed_out' was omitted here, leaving a timed-out run with no
    // View Result and no Re-run, the same defect one status over. A queued run is cancellable
    // (the registry supports cancel-while-queued) so it gets the Cancel button too.
    const isDone=jobIsTerminal(j.status);
    const canCancel=isRunning||j.status==='queued';
    // v2.7.0: colour the outcome so the list is scannable — green done, red failed, amber stopped early.
    const oc=j.outcome||'';
    const reasonCol=oc==='completed'?'var(--green)':oc==='failed'?'var(--red)':(oc==='timed_out'||oc==='cancelled'||oc==='partial')?'var(--amber)':'var(--muted)';
    let dur=''; if(j.started_at&&j.finished_at){ const s=Math.max(0,Math.round((new Date(j.finished_at)-new Date(j.started_at))/1000)); dur=' · '+(s<60?s+'s':Math.floor(s/60)+'m '+(s%60)+'s'); }
    return `<div class="job-item${selectedJobId===j.id?' selected':''}" data-id="${j.id}" data-onclick="selectJob('${j.id}')">
      <div class="job-top">
        <span class="job-status ${j.status}"${JOB_STATUS_TITLE[j.status]?` title="${escapeHtml(JOB_STATUS_TITLE[j.status])}"`:''}>${escapeHtml(JOB_STATUS_LABEL[j.status]||j.status)}</span>
        <span class="job-goal">${(j.goal||'').substring(0,42)}${(j.goal||'').length>42?'…':''}</span>
      </div>
      <div style="font-size:9px;color:var(--dim);font-family:var(--mono);margin-bottom:3px">${fmtTime(j.created_at)}${dur}</div>
      ${j.reason?`<div style="font-size:9px;color:${reasonCol};margin-bottom:3px" title="${escapeHtml(j.reason)}">${escapeHtml(j.reason)}</div>`:''}
      <div class="job-actions">
        ${isDone?`<button class="job-btn view" data-onclick="event.stopPropagation();openJobResult('${j.id}','${j.mission_id||''}')">View Result</button>`
          :isRunning?`<button class="job-btn view" data-onclick="event.stopPropagation();selectJob('${j.id}')">View Status</button>`:''}
        ${canCancel?`<button class="job-btn cancel" data-onclick="event.stopPropagation();cancelJob('${j.id}')">Cancel</button>`:''}
        ${isDone?`<button class="job-btn" data-onclick="event.stopPropagation();reRunJob('${j.id}')" title="Dispatch a new mission with the same directive">↻ Re-run</button>`:''}
      </div>
    </div>`;
  }).join('');
}

async function pollJobs(){
  try{
    const r=await api('/jobs'); if(!r.success) return;
    const jobs=r.data||[];
    const running=jobs.find(j=>j.status==='running');
    const queued =jobs.find(j=>j.status==='queued');
    const current=running||queued;
    colonyRunning=!!current;
    const dot=document.getElementById('mission-dot'),goalEl=document.getElementById('mission-goal');
    if(running){dot.className='dot running';goalEl.textContent=running.goal.substring(0,80)+(running.goal.length>80?'…':'');}
    else if(queued){dot.className='dot active';goalEl.textContent='Queued: '+queued.goal.substring(0,70);}
    else{dot.className='dot';goalEl.textContent='Idle — no active mission';}
    // Render to all job list containers
    renderJobList(jobs,'jobs-list','jobs-badge',8);
    renderJobList(jobs,'ov-jobs-list','ov-jobs-badge',5);
    renderJobList(jobs,'ms-jobs-list','ms-jobs-badge',20);
    // v2.16.0: keep the conversation in step with job progress, so an answer appears in the
    // thread as soon as its mission finishes rather than on the next manual refresh.
    // v2.17.1: refresh the conversation from the poll, but only bust the mission cache when a
    // mission actually finished — otherwise the 10s TTL keeps this cheap. Rendering is now
    // incremental, so an unchanged payload costs no DOM work at all.
    if(document.getElementById('page-missions')?.classList.contains('active')){
      // v0.3.8.42: the shared terminal set — timed-out and escalated runs also settle the thread.
      const settledNow=jobs.filter(j=>jobIsTerminal(j.status)).length;
      const settled=settledNow>0&&msLastSettledCount!==settledNow;
      if(settled) msLastSettledCount=settledNow;
      loadMissionThread(settled?{fresh:true}:undefined);
    }
    // Nav badge
    const navBadge=document.getElementById('nav-jobs-badge');
    if(navBadge){ navBadge.style.display=jobs.length>0?'':'none'; navBadge.textContent=jobs.length; }
  }catch{}
}

async function pollGraph(){
  try{
    const r=await api('/graph'); if(!r.success) return;
    lastGraphData=r.data;
    if(colonyTopo) colonyTopo.ingestGraph(r.data);   // Colony Live (design doc §17, stage 3) rides this poll
    // True live activity from current task states — not "share of all tasks in the mission".
    // running task(s) = fully active (1.0); only queued work (ready/pending) = warming (0.35);
    // everything terminal = idle (0). Always in [0,1], so the tooltip can never exceed 100%.
    const live={};
    (r.data?.nodes||[]).forEach(t=>{
      const a=t.assigned_ant; if(!a) return;
      const keys=[a,t.assigned_worker].filter(Boolean);
      keys.forEach(k=>{ if(!live[k]) live[k]={running:0,queued:0}; });
      keys.forEach(k=>{
        if(t.status==='running') live[k].running++;
        else if(t.status==='ready'||t.status==='pending') live[k].queued++;
      });
    });
    Object.keys(colonyActivity).forEach(k=>delete colonyActivity[k]);
    Object.keys(live).forEach(k=>{const s=live[k];colonyActivity[k]=!s?0:(s.running>0?1:(s.queued>0?0.35:0));});
    // v0.3.8.49 (§18): discrete state per ant/worker from the real task statuses — the highest-ranked
    // state an ant currently holds. Rebuilt fresh each poll so a cleared mission reads idle again.
    Object.keys(colonyAntState).forEach(k=>delete colonyAntState[k]);
    (r.data?.nodes||[]).forEach(t=>{
      const cand=antTaskState(t.status);
      [t.assigned_ant,t.assigned_worker].filter(Boolean).forEach(k=>{
        const cur=colonyAntState[k]||'idle';
        if((STATE_RANK[cand]??0)>=(STATE_RANK[cur]??0)) colonyAntState[k]=cand;
      });
    });
    // Awaiting approval (§1 tie-in): the change-proposing ants read as awaiting approval while a
    // human decision is pending — the same signal the Chat gate shows, mirrored on the map.
    if(colonyAwaitingApproval){
      ['coder','builder'].forEach(k=>{ colonyAntState[k]='awaiting_approval'; });
    }
    updateNodeActivity();
    if(selectedNode) showInspector(selectedNode);
    buildDataFlowEdges(r.data?.nodes||[]);
    if(colonyView==='active'){ buildNodes(); updateNodeActivity(); }
    renderColonyLegend(); // Keep the caste legend's live state in sync
  }catch{}
}

function buildDataFlowEdges(taskNodes){
  const taskById={};
  taskNodes.forEach(t=>{taskById[t.id]=t;});
  const seen=new Set();
  const newEdges=[];
  taskNodes.forEach(t=>{
    const toAnt=t.assigned_worker||t.assigned_ant;
    (t.dependency_ids||[]).forEach(depId=>{
      const dep=taskById[depId];
      if(!dep) return;
      const fromAnt=dep.assigned_worker||dep.assigned_ant;
      if(fromAnt===toAnt) return;
      const key=`${fromAnt}?${toAnt}`;
      if(seen.has(key)) return;
      seen.add(key);
      const fromNode=nodes.find(n=>n.id===fromAnt);
      const toNode=nodes.find(n=>n.id===toAnt);
      if(!fromNode||!toNode) return;
      const existing=dataFlowEdges.find(e=>e.from===fromAnt&&e.to===toAnt);
      if(!existing) newEdges.push({from:fromAnt,to:toAnt,phase:Math.random()*Math.PI*2,count:1});
      else existing.count=(existing.count||1)+1;
    });
  });
  const existingKeys=new Set(dataFlowEdges.map(e=>`${e.from}?${e.to}`));
  newEdges.filter(e=>!existingKeys.has(`${e.from}?${e.to}`)).forEach(e=>dataFlowEdges.push(e));
  if(!colonyRunning&&taskNodes.length===0) dataFlowEdges=[];
}

// -- Colony Live Canvas 2.0 · caste legend + real pheromone trails --
// v0.3.8.42 (§9): CHUD_CASTES deleted — it was the legend's hardcoded six-role stand-in.
let pheromoneTrails=[], pheromoneIntensity=0, pheroMotes=[];

function renderColonyLegend(){
  const el=document.getElementById('chud-legend'); if(!el || typeof ANT_MAP==='undefined') return;
  const roles=(colonyRegistry?.roles||colonyRegistry?.Roles||[]).filter(r=>!['queen','director'].includes(roleId(r)));

  // v0.3.8.42 (§9/§14): the legend states the registry's real condition instead of padding it —
  // both branches of this release removed the six-role stand-in independently; this is the union.
  // Never loaded and no failure yet → "Reading…". Failed with nothing cached → name the failure,
  // offer retry, list nothing. Failed with cached roles → keep them visible, marked STALE with
  // when and why. Loaded → the complete roster, uncapped (v2.14.15).
  // ONE element, ONE id, in every branch — two template branches each declaring the id is a
  // duplicate to the guard even though only one renders (it reads templates, not the DOM).
  let problemText='', retry='';
  if(colonyRegistryProblem){
    const since=fmtTime(new Date(colonyRegistryProblem.at).toISOString());
    problemText = roles.length
      ? `Roles are STALE (last refresh failed at ${escapeHtml(since)}: ${escapeHtml(colonyRegistryProblem.message)})`
      : `Role registry unavailable — ${escapeHtml(colonyRegistryProblem.message)}`;
    retry = `<button class="conv-btn" id="chud-legend-retry">Retry</button>`;
  } else if(!roles.length){
    // The third state: never loaded, not failed either — say so, claim nothing.
    problemText = 'Reading the colony registry…';
  }
  const notice = problemText
    ? `<div class="chud-problem" id="chud-legend-problem">${problemText}</div>`+retry
    : '';

  // v2.14.15: no arbitrary cap. This used to .slice(0,15), which silently dropped all eight
  // homelab ants from the legend while they were still drawn on the canvas.
  const casteRows = roles.map(roleId).map(a=>{
    const am=ANT_MAP[a]||{label:roleName(roles.find(r=>roleId(r)===a)||{RoleId:a}),color:ROLE_COLORS[a]||'#7fa0bc'}; if(!am) return '';
    const on=(colonyActivity[a]||0)>0;
    // v0.3.8.52: through cssColor, like every other style-attribute colour. ANT_MAP[caste].color is
    // not the static literal it looks like here — applyUiState() overwrites it with
    // casteColor(caste), i.e. uiState.castes[caste].color, the operator-set value that v2.14.13
    // added cssColor to validate. Three sites had been missed; the boundary now has a test.
    return `<div class="chud-caste ${on?'on':''}"><span class="dot" style="color:${cssColor(am.color)};background:${cssColor(am.color)}"></span>${escapeHtml(am.label)}</div>`;
  }).join('');
  // v0.3.8.49 (§18): a legend for the STATE ring colours — only the states currently present on the
  // map, so a quiet colony shows a short list and a busy one explains every ring the operator sees.
  const liveStates=[...new Set(nodes.map(n=>n.state).filter(s=>s&&s!=='idle'))]
    .sort((a,b)=>(STATE_RANK[b]??0)-(STATE_RANK[a]??0));
  const stateRows = liveStates.length
    ? `<div class="chud-state-hd">STATES</div>`+liveStates.map(s=>
        `<div class="chud-caste on"><span class="dot" style="color:${STATE_COLORS[s]};background:${STATE_COLORS[s]}"></span>${escapeHtml(STATE_LABEL[s]||s)}</div>`).join('')
    : '';
  // v0.3.8.55 (field report): a header the panel can FOLD to — click collapses to this line.
  const hd=`<div class="chud-hd" data-ovcollapse="legend" role="button" tabindex="0" title="Collapse or expand the caste legend">Caste legend<span class="ov-caret">${overlayState('legend').collapsed?'▸':'▾'}</span></div>`;
  el.innerHTML = hd + `<div class="chud-body">` + notice + casteRows + stateRows + `</div>`;
  document.getElementById('chud-legend-retry')?.addEventListener('click', colonyRegistryRetry);
}

// Real pheromone memory ? the HUD trail bars + a global intensity that drives the canvas drift.
async function pollColonyPheromones(){
  // v0.3.8.55 (field report: "pheromone visuals don't work"): the old gate — colony page only —
  // meant the HUD and canvas drift never received data on whichever page actually hosted the
  // topology (ws workspace or a dashboard-grid widget). The gate now follows the CANVAS: poll
  // whenever its container is actually laid out, whoever hosts it.
  const cArea=document.getElementById('colony-canvas-area');
  if(!cArea || cArea.clientWidth===0 || cArea.clientHeight===0) return;
  try{
    const r=await api('/pheromones/json'); if(!r||!r.success) return;
    const trails=(r.data||[]).slice().sort((a,b)=>(+b.strength||0)-(+a.strength||0));
    // v2.14.6: keep enough rows for per-ant emission (the HUD still shows only the top few).
    pheromoneTrails=trails.slice(0,80);
    const topN=pheromoneTrails.slice(0,3);
    pheromoneIntensity = topN.length ? Math.max(0,Math.min(1, topN.reduce((s,t)=>s+(+t.strength||0),0)/topN.length)) : 0;
    const body=document.getElementById('chud-phero-body'); if(!body) return;
    body.innerHTML = pheromoneTrails.length ? pheromoneTrails.slice(0,4).map(t=>{
      const s=Math.max(0,Math.min(1,+t.strength||0)), pct=Math.round(s*100);
      const col=s>=.6?'var(--green)':s>=.3?'var(--queen)':'var(--red)';
      const key=(t.trail_key||'—').split(':').slice(-1)[0];
      return `<div class="chud-trail"><div class="tk" title="${escapeHtml(t.trail_key||'')}">${escapeHtml(key)}</div><div class="tb"><i style="width:${pct}%;background:${col}"></i></div></div>`;
    }).join('') : '<div class="chud-empty">No trails yet — run missions to build colony memory.</div>';
    // v2.24.0: after the learning reset every pre-boundary trail sits at the neutral 0.5, so this
    // list is a wall of identical bars. Without saying so it reads as a broken subsystem rather
    // than as memory awaiting re-verification.
    const m=r.meta||{};
    if(m.legacy_trails>0){
      body.insertAdjacentHTML('beforeend',
        `<div class="chud-empty">${m.legacy_trails}/${m.total_trails} trails reset to neutral on `
        + `${escapeHtml(String(m.learning_reset||'the learning boundary').slice(0,10))} — they re-differentiate `
        + `as missions reach completed_verified.</div>`);
    }
  }catch{}
}

// Additive canvas pass: motes drifting from the castes toward the Queen, density + opacity scaled by
// real pheromone intensity. CSS-cheap, guarded (invisible when there is no colony memory yet).
/**
 * v2.14.6: the pheromone field now tells the TRUTH per ant.
 *
 * Previously every mote picked a random ant and the only real input was one global average of the
 * top three trail strengths — so a mote leaving CoderAnt implied nothing about the coder's own
 * memory. Now each mote is emitted by a SPECIFIC ant whose own `ant:<role>` / `worker:<id>` trail
 * is strong, with emission share and brightness proportional to that ant's recorded strength.
 * Reading the map is now sound: heavier drift from an ant means that ant's approaches are the ones
 * currently working. Data source is unchanged (`/pheromones/json` — real persisted memory); only
 * the mapping from data to pixels became honest.
 */
function antTrailStrengths(){
  const out={};
  (pheromoneTrails||[]).forEach(t=>{
    const key=String(t.trail_key||''), s=Math.max(0,Math.min(1,+t.strength||0));
    if(s<=0) return;
    const m=/^(?:ant|worker):(.+)$/.exec(key);
    if(!m) return;                                  // capability/model/tool trails are not per-ant
    const id=m[1].trim();
    out[id]=Math.max(out[id]||0, s);                // strongest signal wins for that ant
  });
  return out;
}

function drawPheromoneField(){
  const queen=nodes.find(n=>n.id==='queen'); if(!queen) return;
  const strengths=antTrailStrengths();
  // Emitters are ants that actually HAVE a trail; a worker trail can also credit its parent role.
  const emitters=nodes.filter(n=>n.id!=='queen'
    && (strengths[n.id]>0 || (n.worker && strengths[n.worker]>0) || (n.ant && strengths[n.ant]>0))
    // v2.14.12: "Active" narrows the field to ants doing work right now; "All" shows every trail.
    && (colonyPheromones!=='active' || n.activity>0));
  if(!emitters.length){ pheroMotes.length=0; return; }

  const strengthOf=n=>Math.max(strengths[n.id]||0, strengths[n.worker]||0, strengths[n.ant]||0);
  const total=emitters.reduce((s,n)=>s+strengthOf(n),0);
  if(total<=0){ pheroMotes.length=0; return; }

  // Mote budget scales with how much real signal exists, and each ant's share is proportional to
  // ITS strength — not an average smeared across the colony.
  const budget=Math.min(28, Math.round(4+total*6));
  const want={};
  emitters.forEach(n=>{ want[n.id]=Math.max(1, Math.round(budget*(strengthOf(n)/total))); });

  const have={};
  pheroMotes.forEach(m=>{ if(m.fromId) have[m.fromId]=(have[m.fromId]||0)+1; });
  // Retire motes whose emitter lost its trail or over-emitted; then top up the deficits.
  pheroMotes=pheroMotes.filter(m=>{
    if(!want[m.fromId]) return false;
    if(have[m.fromId]>want[m.fromId]){ have[m.fromId]--; return false; }
    return true;
  });
  emitters.forEach(n=>{
    const deficit=want[n.id]-(pheroMotes.filter(m=>m.fromId===n.id).length);
    for(let i=0;i<deficit;i++)
      pheroMotes.push({fromId:n.id, t:Math.random(), speed:.0015+Math.random()*.002});
  });

  const qp=w2s(queen.x,queen.y);
  pheroMotes.forEach(m=>{
    m.t+=m.speed; if(m.t>=1) m.t=0;                 // same emitter every cycle — the path is real
    const src=nodes.find(n=>n.id===m.fromId); if(!src) return;
    const fp=w2s(src.x,src.y);
    const x=fp.x+(qp.x-fp.x)*m.t, y=fp.y+(qp.y-fp.y)*m.t;
    const s=strengthOf(src);
    // Brightness carries that ant's own strength, so a weak trail reads as a faint thread.
    const a=(0.08+s*0.30)*(1-Math.abs(m.t-0.5)*1.2);
    if(a<=0) return;
    ctx.beginPath();ctx.arc(x,y,(1.2+s*1.1)*camZ,0,Math.PI*2);
    ctx.fillStyle=`rgba(251,191,36,${a})`;ctx.fill();
  });
}

function evTypeClass(t){
  if(!t) return '';
  if(t.includes('fail')||t.includes('error')||t.includes('reject')) return 'type-error';
  if(t.includes('patch')||t.includes('apply')) return 'type-patch';
  if(t.includes('approval')) return 'type-approval';
  if(t.includes('mission')) return 'type-mission';
  if(t.includes('task')) return 'type-task';
  return '';
}
function evCardClass(t){
  if(!t) return '';
  if(t.includes('fail')||t.includes('error')||t.includes('reject')) return 'ev-error';
  if(t.includes('patch')||t.includes('apply')) return 'ev-patch';
  if(t.includes('approval')) return 'ev-approval';
  if(t.includes('mission')) return 'ev-mission';
  return '';
}

let evTotalSeen=0;
async function pollEvents(){
  try{
    const text=await apiText('/events');
    if(text===lastEventText) return;
    lastEventText=text;
    if(text.startsWith('No events')) return;
    const allBlocks=text.split('\n\n').slice(1).filter(b=>b.trim()&&b.includes('['));
    if(!allBlocks.length) return;
    const prevCount=evTotalSeen;
    evTotalSeen=allBlocks.length;
    const newCount=evTotalSeen-prevCount;
    setEl('ev-count',`${evTotalSeen} events`);
    if(typeof notifIngest==='function') notifIngest(allBlocks); // v1.8.25 notification center feed
    function renderFeed(blocks, containerId, maxItems){
      const list=document.getElementById(containerId); if(!list) return;
      const wasAtBottom=list.scrollHeight-list.scrollTop-list.clientHeight<40;
      list.innerHTML=blocks.slice(0,maxItems).map((block,idx)=>{
        const lines=block.trim().split('\n');
        const l0=lines[0]||'';
        const typeMatch=l0.match(/^\[([^\]]+)\]/);
        const evType=typeMatch?typeMatch[1]:'event';
        const isoTs=l0.replace(/^\[[^\]]+\]\s*/,'').substring(0,19);
        const timeStr=isoTs.replace('T',' ');
        const l1=lines[1]||'';
        const antMatch=l1.match(/Ant:\s*([^\s|]+)/);
        const ant=antMatch?antMatch[1].trim():'—';
        const missionMatch=l1.match(/Mission:\s*([^\s|]+)/);
        const missionShort=(missionMatch?missionMatch[1].trim():'').substring(0,12);
        const taskMatch=l1.match(/Task:\s*([^\s|]+)/);
        const taskShort=(taskMatch?taskMatch[1].trim():'').substring(0,10);
        const msg=lines.slice(2).join(' ').replace(/\.\.\.\[.+?\]/g,'').trim().substring(0,160);
        const col=ANT_MAP[ant]?.color||'var(--dim)';
        const tc=evTypeClass(evType),cc=evCardClass(evType);
        const isNew=idx<newCount&&newCount>0;
        return `<div class="ev${cc?' '+cc:''}${isNew?' fresh':''}">
          <div class="ev-top">
            <span class="ev-type ${tc}">${evType}</span>
            <span class="ev-ant" style="color:${col}">${ant}</span>
            <span class="ev-time">${timeStr}</span>
          </div>
          <div class="ev-meta">mission:${missionShort||'—'} · task:${taskShort||'—'}</div>
          <div class="ev-msg">${msg||'—'}</div>
        </div>`;
      }).join('');
      if(newCount>0) list.scrollTop=0;
      setTimeout(()=>list.querySelectorAll('.fresh').forEach(el=>el.classList.remove('fresh')),3000);
    }
    renderFeed(allBlocks,'feed-list',50);
    renderFeed(allBlocks,'ov-feed-list',20);
  }catch{}
}

function parseApprovalsText(text){
  if(!text||text.startsWith('No approval')) return [];
  const body=text.replace(/^[^\n]*\n\n?/,'');
  const rawBlocks=body.split(/\n-{40,}\n/);
  const results=[];
  for(const block of rawBlocks){
    if(!block.trim()||!block.includes('Approval ID:')) continue;
    const get=(key)=>{const m=block.match(new RegExp(key+':\\s*(.+)'));return m?m[1].trim():'';}
    const id=get('Approval ID'); if(!id) continue;
    results.push({id,status:get('Status'),action_type:get('Action'),target_id:get('Target ID'),title:get('Title'),file_path:get('File'),created_at:get('Created At')});
  }
  return results;
}

async function pollApprovals(){
  try{
    const text=await apiText('/approvals');
    const items=parseApprovalsText(text);
    if(colonyTopo) colonyTopo.ingestApprovals(items);
    const card=document.getElementById('approvals-card');
    const bellCount=document.getElementById('bell-count');
    setEl('approval-count',items.length);
    bellCount.style.display=items.length>0?'block':'none';
    bellCount.textContent=items.length;
    if(!items.length){if(card)card.style.display='none';return;}
    if(card)card.style.display='block';
    document.getElementById('approvals-list').innerHTML=items.slice(0,5).map(a=>{
      const filePath=a.file_path||(a.title||'').replace('Approve patch proposal for ','').trim();
      return `<div class="approval-item">
        <div class="approval-title">${(a.title||a.action_type||'Approval needed').substring(0,60)}</div>
        ${filePath?`<div style="font-size:9px;color:var(--blue);font-family:var(--mono);margin-bottom:3px;word-break:break-all">${filePath}</div>`:''}
        <div class="approval-meta">${a.action_type||'patch_proposal'} · ${fmtTime(a.created_at)||'now'}</div>
        <div style="font-size:8px;color:var(--dim);margin-bottom:5px;font-family:var(--mono)">${a.id.substring(0,20)}…</div>
        <div class="approval-btns">
          <button class="approval-btn approve" data-onclick="doApproval('${a.id}','approve')">✓ Approve &amp; Queue Apply</button>
          <button class="approval-btn reject" data-onclick="doApproval('${a.id}','reject')">✕ Reject</button>
        </div>
      </div>`;
    }).join('');
  }catch(e){ pollWarnOnce('pollApprovals', e); }
}

async function doApproval(id,action,kind){
  try{
    // v2.3.0: the unified queue carries more than patches — route the decision by kind
    // (APPROVALS.md: decisions flow through each kind's own endpoints).
    const endpoint=(kind==='homelab_action')
      ?`/homelab/actions/${encodeURIComponent(id)}/${action==='approve'?'approve':'reject'}`
      :(action==='approve'?`/approve/${id}`:`/reject/${id}`);
    await fetch(url(endpoint),{method:'POST',headers:{'Authorization':'Bearer '+TOKEN}});
    pollApprovals(); pollStatus();
    if(kind==='homelab_action'&&typeof loadHlActions==='function') loadHlActions();
  }catch(e){console.error('Approval error',e);}
}

async function cancelJob(id){
  // v3.8.34: the only bare confirmation in the console — every other one states its consequence,
  // including "Cancel all" directly above it. Worded from what the server actually does: the token
  // aborts the in-flight model call and stops the scheduler, and ComputeOutcome then records
  // "Cancelled by operator — N/M tasks finished before stopping", so finished tasks survive.
  if(!await uiConfirm('Stop this mission run? Tasks that already finished are kept; the step in '
                    + 'progress is abandoned and the run ends there.')) return;
  try{ await api(`/jobs/${id}/cancel`,'POST'); pollJobs(); }catch(e){console.error('Cancel',e);}
}
// v2.7.0: re-dispatch a finished/cancelled mission with the exact same directive (the mode prefix
// is already baked into the stored goal, so the retry runs in the same mode). One-click retry for a
// mission that timed out, was cancelled, or failed.
async function reRunJob(id){
  let goal='';
  try{ const r=await api('/jobs/'+id); if(r&&r.success&&r.data) goal=r.data.goal||''; }catch(e){}
  if(!goal){ if(typeof pcToast==='function') pcToast('Could not load the original directive.',false); return; }
  if(!await uiConfirm('Re-run this mission with the same directive?\n\n'+goal.slice(0,200)+(goal.length>200?'…':''))) return;
  submitMissionGoal(goal);
}

// -- Header status chip + popover ----------------------------------------------
let lastSystemSummary=null, lastUpdateInfo=null;
const LOCAL_ICON='<svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="3" width="20" height="14" rx="2"/><line x1="8" y1="21" x2="16" y2="21"/><line x1="12" y1="17" x2="12" y2="21"/></svg>';
const CLOUD_ICON='<svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M18 10h-1.26A8 8 0 1 0 9 20h9a5 5 0 0 0 0-10z"/></svg>';

// v0.3.8.52 — the last color emoji leave the console (the UI-alignment sweep's final open item:
// "remaining emoji/symbol icons swept to the open-source set"). 📁📄🕘📎★☆☰⚖ rendered in the OS's
// emoji palette, one island of color in a themed monochrome UI. Same 24-box stroke grammar as
// IAICON above, sized for inline text; fill-mode glyphs (the pinned star) opt in per shape.
const INLINE_ICON=(body,size)=>`<svg width="${size||12}" height="${size||12}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="vertical-align:-1px">${body}</svg>`;
const GLYPH={
  folder: INLINE_ICON('<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>'),
  file:   INLINE_ICON('<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/>'),
  clock:  INLINE_ICON('<circle cx="12" cy="12" r="9"/><polyline points="12 7 12 12 15.5 14"/>'),
  list:   INLINE_ICON('<line x1="8" y1="6" x2="21" y2="6"/><line x1="8" y1="12" x2="21" y2="12"/><line x1="8" y1="18" x2="21" y2="18"/><line x1="3" y1="6" x2="3.01" y2="6"/><line x1="3" y1="12" x2="3.01" y2="12"/><line x1="3" y1="18" x2="3.01" y2="18"/>'),
  scale:  INLINE_ICON('<path d="M12 3v18M8 21h8M4 7h16"/><path d="M6 7l-3 6a3.4 3.4 0 0 0 6 0z"/><path d="M18 7l-3 6a3.4 3.4 0 0 0 6 0z"/>'),
  star:     INLINE_ICON('<polygon points="12 3 14.9 8.9 21.4 9.8 16.7 14.4 17.8 20.9 12 17.8 6.2 20.9 7.3 14.4 2.6 9.8 9.1 8.9"/>'),
  starFill: INLINE_ICON('<polygon fill="currentColor" points="12 3 14.9 8.9 21.4 9.8 16.7 14.4 17.8 20.9 12 17.8 6.2 20.9 7.3 14.4 2.6 9.8 9.1 8.9"/>'),
  refresh:  INLINE_ICON('<polyline points="23 4 23 10 17 10"/><path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10"/>'),
};

async function pollModelInfo(){
  try{
    const r=await api('/system/summary'); if(!r.success) return;
    lastSystemSummary=r.data; renderStatusChip(); if(document.getElementById('status-pop').classList.contains('show')) renderStatusPop();
  }catch{}
}

/** An `agent:` provider id as the operator knows it. Null for anything else. */
function AGENT_LABEL(provider){
  const p=String(provider||'');
  if(!p.startsWith('agent:')) return null;
  const a=(lastAgentCatalog||[]).find(x=>x.provider===p);
  return a ? a.name.replace(/ \(agent\)$/,'') : p.slice('agent:'.length);
}
let lastAgentCatalog = null;

function renderStatusChip(){
  const s=lastSystemSummary; if(!s) return;
  const providers=s.routing_mode!=='local';
  const modeTxt=s.routing_mode==='local'?'Local':s.routing_mode==='providers'?'Providers':'Mixed';
  const modeCol=s.routing_mode==='local'?'var(--green)':'var(--purple)';
  const modeEl=document.getElementById('sc-mode'); modeEl.style.color=modeCol;
  modeEl.querySelector('.sc-mode-icon').innerHTML=providers?CLOUD_ICON:LOCAL_ICON;
  setEl('sc-mode-txt',modeTxt);
  // v0.3.8.41 — the chip names what the ANTS ARE ROUTED TO, not the Ollama default.
  //
  // It read `default_model`, which is the local-model fallback and has nothing to do with per-role
  // routing. Route every ant to Claude Code and the chip still said gemma4:31b — the console
  // advertising a model no role was using, while the boot log said otherwise three lines apart.
  //
  // Derived from `routes`, which is the same list the popover already renders, so the chip and the
  // detail beneath it cannot disagree. One distinct pair means one name; several means a count,
  // because inventing a winner among genuinely mixed routes would be a different lie.
  const routes = s.routes || [];
  const routeProviders = [...new Set(routes.map(r => r.provider || ''))];
  const models = [...new Set(routes.map(r => r.model || ''))];
  if (routes.length && routeProviders.length === 1) {
    // Grouped by PROVIDER before model, and that ordering is the point. Routing every ant to
    // Claude Code can still leave a stale model string on some rows — the agent ignores it — and
    // counting distinct models would report "2 models" for a colony that is entirely on one agent.
    // The provider is the answer to "what is running my ants"; the model only refines it.
    const label = AGENT_LABEL(routeProviders[0]) || (models.length === 1 ? models[0] : routeProviders[0]);
    setEl('sc-model', label);
  } else if (routeProviders.length > 1) {
    setEl('sc-model', routeProviders.length + ' providers');
  } else {
    setEl('sc-model', s.default_model || '—');
  }
  const modelEl = document.getElementById('sc-model');
  if (modelEl) modelEl.title = routes.length
    ? routes.map(r => r.role + ' → ' + (r.provider || '?') + ':' + (r.model || '?')).join('\n')
    : '';

  // Online dot reflects the *backend* health, not just the API: red if Ollama is unreachable.
  const dot=document.getElementById('sc-dot');
  // v3.8.33: a reachable host with no usable model is NOT ok. The chip used to go green on
  // reachability alone, which is exactly the state where every mission fails.
  //
  // v0.3.8.41: and only when a role actually USES Ollama. With every ant routed to a CLI agent,
  // Ollama's health is no longer this colony's health, and a red chip for an idle local server
  // would be a false alarm about a component nothing is asking anything of.
  const usesOllama = routes.some(r => (r.provider || '').toLowerCase() === 'ollama');
  const modelBad = s.use_ollama && usesOllama && (s.model_resolved===false || s.ollama_model_present===false);
  const ok = connected && (!usesOllama || s.ollama_reachable!==false) && !modelBad;
  dot.className='sc-dot '+(ok?'ok':'err');
  document.getElementById('status-chip').title = ok?'Online — click for details'
    :(s.ollama_reachable===false?'Ollama unreachable — click for details'
    :modelBad?(s.model_choice_reason||'No usable model — click for details')
    :'Click for details');
}

function renderStatusPop(){
  const s=lastSystemSummary||{};
  setEl('sp-version','v'+(s.version||'—'));
  // v0.3.8.55 (field report): these were status DOTS once — an encoding mishap turned them into
  // literal question marks, so the popover read "? Online" as if unsure of its own answer.
  setEl('sp-api', connected?'● Online':'● Offline');
  document.getElementById('sp-api').style.color=connected?'var(--green)':'var(--red)';
  const or=s.ollama_reachable;
  // v3.8.33: reachability and MODEL are reported separately, because a reachable host with no
  // usable model was the state that read as healthy while nothing worked.
  const modelOk = s.model_resolved!==false && s.ollama_model_present!==false;
  const oTxt = !s.use_ollama?'not in use'
    :(or===true?(modelOk?'Reachable':'Reachable — no usable model')
    :or===false?'Unreachable':'—');
  setEl('sp-ollama',oTxt);
  document.getElementById('sp-ollama').style.color =
    or===true?(modelOk?'var(--green)':'var(--amber,#f59e0b)'):or===false?'var(--red)':'var(--dim)';
  setEl('sp-ollama-host',s.ollama_host||'—');
  // v0.3.8.42 (§11): "configured", because that is what the field measures. providers_configured
  // counts stored keys; it says nothing about reachability or auth, and calling it "connected"
  // reported health the colony had never checked.
  setEl('sp-mode',(s.routing_mode||'—')+(s.providers_configured?` · ${s.providers_configured} provider(s) configured`:''));
  // v0.3.8.40: deployment mode, with its reason on hover and whether it was detected or set.
  // "Desktop" and "Server" are what the operator sees; deployment_mode is the wire value.
  const depMode=(s.deployment_mode||'').toLowerCase();
  const depEl=document.getElementById('sp-deployment');
  if(depEl){
    depEl.textContent = depMode==='server' ? 'Server / container'
                      : depMode==='desktop' ? 'Desktop'
                      : '—';
    depEl.title = (s.deployment_reason||'')
                + (s.deployment_detected===false ? '' : ' (detected — set deployment_mode in configuration to override)');
  }
  setEl('sp-default',s.default_model||'—');
  // v3.8.34: roles routed to a model that cannot meet their contract. Reported here because the
  // only other surface is the Tools & Routing widget, which ships hidden — so on a default
  // dashboard an operator was told the model was reachable and resolved while seven of ten roles
  // would parse prose into an empty result. The row appears ONLY when something is wrong, so a
  // healthy colony gains no clutter; the per-role reasons stay one click away in Tools & Routing.
  const unfit = Number(s.unfit_role_count)||0;
  const fitRow = document.getElementById('sp-fitness-row');
  if(fitRow){
    fitRow.style.display = unfit>0 ? '' : 'none';
    const fitEl = document.getElementById('sp-fitness');
    if(fitEl){
      // v0.3.8.41 — worded for BOTH causes. The count now includes roles whose route cannot
      // resolve at all (no model chosen), and telling those operators their model "lacks a
      // capability" sent them looking for a better model when they had not picked one.
      fitEl.textContent = unfit+' role'+(unfit===1?'':'s')+' cannot run';
      fitEl.style.color = 'var(--amber,#f59e0b)';
      fitEl.title = 'These roles either have no model to run on, or are routed to one missing a '
                  + 'capability their contract needs — either way they return empty or unusable '
                  + 'results. Open Tools & Routing for the exact reason.';
    }
  }
  const routes=s.routes||[];
  document.getElementById('sp-routes').innerHTML=routes.map(rt=>{
    const local=(rt.provider||'ollama').toLowerCase()==='ollama';
    return `<div style="display:flex;align-items:center;justify-content:space-between;gap:8px;padding:3px 0;font-size:10px;">
      <span style="color:var(--dim);text-transform:capitalize;">${escapeHtml(rt.role)}</span>
      <span style="font-family:var(--mono);display:flex;align-items:center;gap:5px;">
        <span style="color:${local?'var(--green)':'var(--purple)'};font-size:8px;font-weight:700;text-transform:uppercase;">${local?'local':escapeHtml(rt.provider)}</span>
        ${escapeHtml(rt.model||'—')}</span></div>`;
  }).join('')||'<div style="color:var(--dim);font-size:10px;">No routes configured.</div>';
  renderUpdateBanner();
}

function renderUpdateBanner(){
  const u=lastUpdateInfo, banner=document.getElementById('sp-update-banner'), dot=document.getElementById('sc-update');
  if(!u){ banner.style.display='none'; if(dot) dot.style.display='none'; return; }
  if(u.update_available){
    banner.style.display='block';
    banner.innerHTML=`? Update available: <b>v${escapeHtml(u.latest)}</b> (you have v${escapeHtml(u.current)}). `+
      (u.release_url?`<a href="${escapeHtml(u.release_url)}" target="_blank" rel="noopener" style="color:var(--queen);text-decoration:underline;">Release notes</a> · `:'')+
      `On the LXC: <span style="font-family:var(--mono)">cd /opt/anthill/src && git pull && bash deploy/lxc/setup.sh</span>`;
    if(dot) dot.style.display='';
  } else {
    if(dot) dot.style.display='none';
    banner.style.display=u.status==='unknown'?'block':'none';
    if(u.status==='unknown') banner.innerHTML=`<span style="color:var(--dim)">Update check unavailable (${escapeHtml(u.reason||'offline')}). You have v${escapeHtml(u.current)}.</span>`;
  }
}

async function checkForUpdate(force){
  try{
    const r=await api('/update/check'+(force?'?force=1':'')); if(!r.success) return;
    lastUpdateInfo=r.data; renderUpdateBanner();
  }catch{}
}

function closeStatusPop(){ document.getElementById('status-pop').classList.remove('show'); }
document.getElementById('status-chip').addEventListener('click',e=>{
  e.stopPropagation();
  const pop=document.getElementById('status-pop');
  const show=!pop.classList.contains('show');
  pop.classList.toggle('show',show);
  if(show){ renderStatusPop(); checkForUpdate(false); }
});
document.addEventListener('click',e=>{
  const pop=document.getElementById('status-pop');
  if(pop.classList.contains('show') && !pop.contains(e.target) && !document.getElementById('status-chip').contains(e.target)) pop.classList.remove('show');
});
document.getElementById('sp-recheck').addEventListener('click',e=>{ e.stopPropagation(); setEl('sp-update-banner','Checking…'); document.getElementById('sp-update-banner').style.display='block'; checkForUpdate(true); });

// -- Job result ----------------------------------------------------------------
function selectJob(id){
  selectedJobId=id;
  document.querySelectorAll('.job-item').forEach(el=>el.classList.toggle('selected',el.dataset.id===id));
  showJobResult(id);
}

async function showJobResult(id){
  try{
    const r=await api(`/jobs/${id}`); if(!r.success) return;
    const j=r.data;
    document.getElementById('result-title').textContent=`${j.status.toUpperCase()} · ${(j.goal||'').substring(0,55)}`;
    const body=document.getElementById('result-body');
    if(j.status==='running'||j.status==='queued'){
      body.textContent=j.status==='running'
        ?'Mission is running — the colony is working...\n\nWatch Colony Events for live updates.'
        :'Job is queued, waiting for a worker...';
    } else if(j.mission_id){
      await renderMissionReport(j.mission_id, body);
    } else if(j.error){
      body.textContent='ERROR:\n'+j.error;
    } else if(j.result){
      body.textContent=j.result;
    } else {
      body.textContent='No result recorded.';
    }
    const statusBar=document.getElementById('result-status-bar');
    const start=j.started_at?fmtTime(j.started_at):'—',end=j.finished_at?fmtTime(j.finished_at):'—';
    statusBar.innerHTML=`<span>Job: <span style="font-family:var(--mono)">${id.substring(0,8)}…</span></span><span>Started: ${start}</span><span>Finished: ${end}</span>`;
    document.getElementById('result-overlay').classList.add('show');
  }catch{}
}

// -- Mission report: the readable "what actually happened" view ----------------
// v0.3.8.55 (field report): the same encoding mishap that hit the status popover — these were
// outcome glyphs, not questions.
const MR_STATUS={complete:['✓ Completed','var(--green)'],partial:['◐ Completed with gaps','var(--queen)'],
  failed:['✗ Failed','var(--red)'],running:['▶ Running','var(--blue)']};
const MR_TASK_ICON={complete:'?',failed:'?',skipped:'?',blocked:'?',running:'?',pending:'—',ready:'—'};
const MR_PATCH_STATE={proposed:['awaiting your approval','var(--queen)'],approved:['approved — not yet applied','var(--blue)'],
  applied:['applied to disk','var(--green)'],rejected:['rejected','var(--red)'],failed:['apply failed','var(--red)']};

/* ---------------------------------------------------------------------------------------------
 * v2.16.0 — Missions as a conversation.
 *
 * The colony has always stored two things: the answer (user_result, now rewritten into plain
 * English as final_result) and the full trace (debug_result). The old page led with the trace and
 * buried the answer. This inverts that: each exchange shows the directive and the answer, and
 * everything the colony did sits behind one disclosure.
 *
 * The detail view reuses renderMissionReport verbatim — one implementation of "what happened",
 * shared with the Results page, rather than a second one that can drift.
 * ------------------------------------------------------------------------------------------- */
let msThreadMissions=[];

/* v2.17.1 — the Missions conversation is updated INCREMENTALLY.
 *
 * v2.16.0 rebuilt the whole thread with innerHTML on every jobs poll (every 3s), which destroyed
 * open activity disclosures, already-loaded reports, focus, selection and scroll position — and
 * re-announced all forty exchanges through the live region each time. Worse, replacing innerHTML
 * clamps scrollTop to 0, so anyone reading history was thrown to the TOP of the thread every three
 * seconds.
 *
 * All the decision-making lives in mission-thread.js as pure functions (tested with node --test);
 * everything here is DOM application only.
 */
const MT = window.AnthillMissionThread;
let msPrints = new Map();          // mission id -> render fingerprint currently on screen
const msGate = MT.RequestGate();   // rejects stale /missions/json responses
const msActivity = MT.ActivityStore();
let msLastAnnounced = '';
let msLastSettledCount = -1;

const MS_CHIP={complete:['done','var(--green)'],partial:['partial','var(--amber)'],
               failed:['failed','var(--red)'],running:['working','var(--hud-cyan,#22d3ee)'],
               queued:['queued','var(--muted)']};

async function loadMissionThread(opts){
  const thread=document.getElementById('ms-thread');
  if(!thread) return;
  const token=msGate.next();
  try{
    // A finished mission must not be read from a stale cache — bust it so the answer appears now
    // rather than up to ten seconds later.
    if(opts&&opts.fresh) apiCacheBust('/missions/json');
    const r=await api('/missions/json?limit=40');
    if(!msGate.isCurrent(token)) return;            // a newer request already answered
    if(!r.success) throw new Error(r.message||'could not load missions');
    msThreadMissions=(r.data||[]).slice();
    renderMissionThread();
  }catch(e){
    if(!msGate.isCurrent(token)) return;
    // Never wipe a thread that is already showing content just because one refresh failed.
    if(!msPrints.size){
      thread.textContent='';
      const err=document.createElement('div');
      err.className='ms-empty'; err.style.color='var(--red)';
      err.textContent='Could not load missions: '+(e.message||'unknown error');
      thread.appendChild(err);
    }
    msSetStatus('Could not refresh missions: '+(e.message||'unknown error'));
  }
}

function msSetStatus(text){
  const el=document.getElementById('ms-announce');
  if(el && text && text!==msLastAnnounced){ el.textContent=text; msLastAnnounced=text; }
}

/** Build one exchange. Called only for missions that are genuinely new to the thread. */
function msBuildExchange(m){
  const row=document.createElement('div');
  row.className='ms-x'; row.dataset.mission=String(m.id);

  const ask=document.createElement('div'); ask.className='ms-ask';
  const say=document.createElement('div'); say.className='ms-say';
  const meta=document.createElement('div'); meta.className='ms-meta';

  const det=document.createElement('details');
  det.className='ms-act'; det.dataset.msact=String(m.id);
  const sum=document.createElement('summary');
  sum.textContent='▸ Show activity';
  const body=document.createElement('div'); body.className='ms-act-body';
  det.appendChild(sum); det.appendChild(body);

  row.appendChild(ask); row.appendChild(say); row.appendChild(meta); row.appendChild(det);
  msPatchExchange(row,m);
  return row;
}

/** Patch an existing exchange in place. Never touches the activity subtree. */
function msPatchExchange(row,m){
  const answer=MT.answerOf(m);
  const pending=!answer;
  const [chip,col]=MS_CHIP[m.status]||[m.status||'—','var(--muted)'];

  // textContent throughout: server text is never parsed as HTML, so nothing can be injected.
  row.querySelector('.ms-ask').textContent=m.goal||'';
  const say=row.querySelector('.ms-say');
  say.classList.toggle('pending',pending);
  say.textContent = pending ? 'Working — no answer recorded yet.' : answer;
  // v2.18.2: say so when the inline answer is clipped, rather than quietly ending mid-sentence.
  if(!pending && MT.answerIsTruncated(m)){
    const more=document.createElement('div');
    more.className='ms-clip';
    more.textContent='… answer truncated — open Show activity for the full output.';
    say.appendChild(more);
  }

  const meta=row.querySelector('.ms-meta');
  meta.textContent='';
  const chipEl=document.createElement('span');
  chipEl.className='ms-chip'; chipEl.style.color=col; chipEl.style.borderColor=col;
  chipEl.textContent=chip;
  meta.appendChild(chipEl);
  const when=document.createElement('span');
  when.textContent=fmtTime(m.saved_at||m.created_at);
  meta.appendChild(when);
  if(m.success_score!=null){
    const sc=document.createElement('span');
    sc.textContent='score '+Number(m.success_score).toFixed(2);
    meta.appendChild(sc);
  }
  row.querySelector('details.ms-act').setAttribute('aria-label','Colony activity for: '+(m.goal||'mission'));
}

function renderMissionThread(){
  const thread=document.getElementById('ms-thread');
  if(!thread) return;

  const plan=MT.reconcileThread(msPrints,msThreadMissions);
  const byId=new Map(msThreadMissions.map(m=>[MT.missionId(m),m]));

  if(!plan.changed && msPrints.size){
    return;   // identical data — the three-second poll now costs zero DOM work
  }

  if(!plan.order.length){
    thread.textContent='';
    const empty=document.createElement('div');
    empty.className='ms-empty';
    empty.textContent='No missions yet. Describe what you want done below.';
    thread.appendChild(empty);
    msPrints=plan.fingerprints;
    return;
  }

  // Measure BEFORE mutating, and remember what had focus so it can be restored.
  const follow=MT.shouldFollowBottom({scrollTop:thread.scrollTop,scrollHeight:thread.scrollHeight,clientHeight:thread.clientHeight});
  const prevTop=thread.scrollTop;
  const active=document.activeElement;
  const activeRow=active&&active.closest?active.closest('.ms-x'):null;
  const activeMission=activeRow?activeRow.dataset.mission:null;

  const empty=thread.querySelector('.ms-empty'); if(empty) empty.remove();

  plan.removed.forEach(id=>{
    const el=thread.querySelector('.ms-x[data-mission="'+CSS.escape(id)+'"]');
    if(el) el.remove();
    msActivity.forget(id);
  });

  // Walk the desired order, creating, patching and moving only what needs it.
  let cursor=null;
  plan.order.forEach(id=>{
    const m=byId.get(id);
    let row=thread.querySelector('.ms-x[data-mission="'+CSS.escape(id)+'"]');
    if(!row){
      row=msBuildExchange(m);
    } else if(plan.updated.indexOf(id)>=0){
      msPatchExchange(row,m);
    }
    const shouldFollow=cursor?cursor.nextElementSibling:thread.firstElementChild;
    if(shouldFollow!==row) thread.insertBefore(row,shouldFollow||null);
    cursor=row;
  });

  msPrints=plan.fingerprints;

  // Restore focus if the element survived; it does, because rows are patched rather than rebuilt.
  if(activeMission&&active&&!document.contains(active)){
    const back=thread.querySelector('.ms-x[data-mission="'+CSS.escape(activeMission)+'"] summary');
    if(back) back.focus();
  }

  if(follow) thread.scrollTop=thread.scrollHeight;
  else thread.scrollTop=prevTop;      // incremental updates keep this, but be explicit

  msSetStatus(MT.announcementFor(plan,byId));
}

/* ---- Activity disclosure -------------------------------------------------------------------
 * State lives in msActivity (outside the DOM), so it survives updates and a failed report can be
 * retried — v2.16.0 set dataset.loaded before the request resolved, permanently stranding any
 * report that timed out. */
async function msLoadActivity(missionId,det){
  const body=det.querySelector('.ms-act-body');
  if(!body) return;
  if(!msActivity.begin(missionId)) return;    // already loading, or already loaded

  body.textContent='';
  const wait=document.createElement('div');
  wait.style.cssText='color:var(--dim);font-size:11px;';
  wait.textContent='Loading activity…';
  body.appendChild(wait);

  let ok=false, message='';
  try{ ok=await renderMissionReport(missionId,body); }
  catch(e){ ok=false; message=(e&&e.message)||'unknown error'; }

  if(ok){ msActivity.succeed(missionId); return; }

  msActivity.fail(missionId,message||'The mission report could not be loaded.');
  body.textContent='';
  const err=document.createElement('div');
  err.className='ms-act-err';
  err.textContent=msActivity.errorOf(missionId)+' ';
  const retry=document.createElement('button');
  retry.type='button'; retry.className='ms-retry';
  retry.dataset.msretry=String(missionId);
  retry.textContent='Retry';
  err.appendChild(retry);
  body.appendChild(err);
}

document.addEventListener('click',e=>{
  const btn=e.target.closest&&e.target.closest('[data-msretry]');
  if(!btn) return;
  const id=btn.dataset.msretry;
  const det=btn.closest('details.ms-act');
  if(!det) return;
  msActivity.retry(id);
  msLoadActivity(id,det);
});

document.addEventListener('toggle',e=>{
  const det=e.target.closest&&e.target.closest('details[data-msact]');
  if(!det) return;
  const id=det.dataset.msact;
  msActivity.setOpen(id,det.open);
  const sum=det.querySelector('summary');
  if(sum) sum.textContent=det.open?'▾ Hide activity':'▸ Show activity';
  // Reopening after a failure retries; a loaded report is left alone.
  if(det.open){
    if(msActivity.stateOf(id)==='error') msActivity.retry(id);
    msLoadActivity(id,det);
  }
},true);

/**
 * v2.17.1: returns TRUE on success and FALSE on failure so callers can decide whether to mark the
 * report loaded. Previously it swallowed the failure into the body and returned undefined, which
 * is why the Missions thread could mark a timed-out report as permanently loaded.
 */
async function renderMissionReport(missionId, body){
  const r=await api(`/missions/${encodeURIComponent(missionId)}/report`);
  if(!r.success){ body.textContent='Could not load the mission report: '+(r.message||'unknown error'); return false; }
  const rep=r.data, tc=rep.task_counts||{};
  const [stLabel,stColor]=MR_STATUS[rep.status]||[rep.status,'var(--text)'];
  const sec=(title,inner)=>`<div style="margin-bottom:16px"><div style="font-size:10px;letter-spacing:.08em;text-transform:uppercase;color:var(--dim);margin-bottom:6px;">${title}</div>${inner}</div>`;
  let html='';

  // Mission-level outcome — separate from the tasks that produced it.
  html+=sec('Mission',
    `<div style="white-space:normal;line-height:1.6"><span style="color:${stColor};font-weight:600">${stLabel}</span>`+
    `<span style="color:var(--dim)"> · score ${rep.success_score!=null?Number(rep.success_score).toFixed(2):'—'}`+
    ` · ${tc.complete||0}/${tc.total||0} tasks completed${tc.failed?` · <span style="color:var(--red)">${tc.failed} failed</span>`:''}${tc.skipped?` · ${tc.skipped} skipped`:''}</span>`+
    `<div style="margin-top:6px;color:var(--text)">${escapeHtml(rep.goal||'')}</div></div>`);

  html+=sec('Final output (mission result)',
    `<div style="background:var(--panel2,rgba(255,255,255,.03));border:1px solid var(--border);border-radius:6px;padding:10px 12px;">${escapeHtml(rep.final_output||'No mission-level output was recorded.')}</div>`);

  // Tangible changes — the part that answers "did anything actually happen?"
  const patches=rep.patches||[];
  if(patches.length){
    const pcnt=rep.patch_counts||{};
    const countLine=`<div style="white-space:normal;font-size:11px;color:var(--dim);margin-bottom:6px">`+
      `${pcnt.total||patches.length} proposed · ${pcnt.pending||0} pending · ${pcnt.approved||0} approved · ${pcnt.applied||0} applied · ${pcnt.rejected||0} rejected${pcnt.failed?` · <span style="color:var(--red)">${pcnt.failed} failed</span>`:''}`+
      ` · <a href="#" data-onclick="openPatches({mission_id:'${rep.id}'});return false;" style="color:var(--blue)">Open in Changes →</a></div>`;
    html+=sec(`Tangible changes proposed (${patches.length})`,
      countLine+
      patches.map(p=>{
        const [pl,pc]=MR_PATCH_STATE[p.status]||[p.status,'var(--text)'];
        return `<div style="white-space:normal;margin-bottom:6px;line-height:1.5">`+
          `<a href="#" data-action="open-patches" data-mission-id="${escapeHtml(rep.id)}" data-file="${escapeHtml(p.file_path||'')}" style="font-family:var(--mono);color:var(--blue)">${escapeHtml(p.file_path||'?')}</a> <span style="color:var(--dim)">(${escapeHtml(p.change_type||'modify')})</span>`+
          ` — <span style="color:${pc}">${pl}</span>`+
          `<div style="color:var(--dim);font-size:11px">${escapeHtml(p.reason||'')}</div>`+
          (p.last_error?`<div style="color:var(--red);font-size:11px">Apply error: ${escapeHtml(p.last_error)}</div>`:'')+`</div>`;
      }).join('')+
      (rep.pending_approvals?`<div style="white-space:normal;color:var(--queen);font-size:11px;margin-top:4px">⚠ ${rep.pending_approvals} change(s) are waiting for review · open the <b>Changes</b> to inspect &amp; apply. Nothing touches disk until you approve &amp; apply.</div>`:''));
  } else {
    html+=sec('Tangible changes proposed',
      `<div style="white-space:normal;color:var(--dim)">None — this mission produced text output only. File changes only happen when the coder proposes patches <i>and</i> they are approved &amp; applied.</div>`);
  }
  if(rep.sources_saved) html+=sec('Research', `<div style="white-space:normal;color:var(--dim)">${rep.sources_saved} web source(s) saved.</div>`);

  // v1.8.23: gated auto-apply outcome · answers "did the autonomous change actually stick?"
  if(rep.auto_apply){
    const aa=rep.auto_apply, kept=!!aa.kept;
    const col=kept?'var(--green)':(aa.outcome==='reverted'?'var(--red)':'var(--queen)');
    const label={verified:'Kept (verified)',kept_unverified:'Kept (no verify)',reverted:'Reverted after failed verify',
      apply_failed:'Apply failed',git_failed:'Kept, git commit failed',skipped:'Skipped',started:'In progress'}[aa.outcome]||aa.outcome;
    html+=sec('Autonomous auto-apply',
      `<div style="white-space:normal;line-height:1.5"><span style="color:${col};font-weight:600">${label}</span>`+
      `<div style="color:var(--dim);font-size:11px;margin-top:3px">${escapeHtml(aa.message||'')}</div></div>`);
  }

  // Autonomy linkage — which objective drove this mission, and what it spawned.
  if(rep.autonomy_run){
    const ar=rep.autonomy_run;
    html+=sec('Autonomous run',
      `<div style="white-space:normal;line-height:1.6;color:var(--dim)">Driven by objective <b style="color:var(--text)">${escapeHtml(ar.objective_title||ar.objective_id||'?')}</b>`+
      `${ar.follow_ups_created?` · spawned ${ar.follow_ups_created} follow-up objective(s)`:''}</div>`);
  }
  const created=rep.created_objectives||[];
  if(created.length){
    html+=sec(`Objectives created by this mission (${created.length})`,
      created.map(o=>`<div style="white-space:normal;margin-bottom:5px;line-height:1.5">`+
        `<b>${escapeHtml(o.title||'')}</b> <span class="obj-status ${o.status}">${o.status}</span> <span style="color:var(--dim)">(priority ${o.priority})</span>`+
        `<div style="color:var(--dim);font-size:11px">${escapeHtml(o.charter||'')}</div></div>`).join('')+
      `<div style="white-space:normal;color:var(--dim);font-size:11px;margin-top:4px">These now live in the <b>Automation</b> backlog.</div>`);
  }

  // Problems — surfaced instead of buried (incl. unparseable coder proposals).
  const problems=rep.problems||[];
  if(problems.length){
    html+=sec(`Problems (${problems.length})`,
      problems.map(p=>{
        const friendly=p.type==='patch_proposal_parse_failed'
          ?'The coder produced a change proposal the parser could not read — that work never reached the approval queue.'
          :escapeHtml(p.message||p.type);
        return `<div style="white-space:normal;color:var(--red);font-size:11px;margin-bottom:4px">[${escapeHtml(p.type)}] ${friendly}</div>`;
      }).join(''));
  }

  // Per-task breakdown — each task's own readable output, expandable.
  const tasks=rep.tasks||[];
  html+=sec(`Tasks (${tasks.length})`,
    tasks.map((t,i)=>{
      const icon=MR_TASK_ICON[t.status]||'·';
      const color=t.status==='complete'?'var(--green)':t.status==='failed'?'var(--red)':t.status==='skipped'?'var(--dim)':'var(--queen)';
      const reason=t.failure_reason||t.skipped_reason||t.blocked_reason;
      const elapsed=t.elapsed_seconds!=null?` · ${Number(t.elapsed_seconds).toFixed(1)}s`:'';
      return `<details style="white-space:normal;margin-bottom:5px;border:1px solid var(--border);border-radius:6px;padding:6px 10px">`+
        `<summary style="cursor:pointer;line-height:1.5"><span style="color:${color}">${icon}</span> `+
        `<b>${i+1}. ${escapeHtml(t.title||'')}</b> <span style="color:var(--dim)">— ${escapeHtml(t.ant||'')} ant${elapsed}</span></summary>`+
        `<div style="margin-top:6px;white-space:pre-wrap;font-size:11px;color:var(--text)">${escapeHtml(t.readable_output||'(no output recorded)')}</div>`+
        (reason?`<div style="margin-top:4px;color:var(--red);font-size:11px;white-space:pre-wrap">Why: ${escapeHtml(reason)}</div>`:'')+
        `</details>`;
    }).join('')||'<div style="color:var(--dim)">No tasks recorded.</div>');

  // v1.8.23 (Phase 6): task DAG + timeline, lazy-loaded on demand.
  if(tasks.length){
    const fid='mrflow-'+missionId.replace(/[^a-zA-Z0-9]/g,'');
    html+=sec('Task Flow (DAG + timeline)',
      `<div id="${fid}"><button class="hud-act" data-onclick="loadTaskFlow('${missionId}','${fid}')">◈ Show task DAG &amp; timeline</button></div>`);
  }

  body.innerHTML=html;
  return true;
}

// -- Mission Timeline + Task DAG viewer (v1.8.23, UI Phase 6) ------------------
async function loadTaskFlow(missionId, containerId){
  const box=document.getElementById(containerId); if(!box) return;
  box.innerHTML='<div class="hud-state"><div class="hud-spinner"></div>Building flow…</div>';
  try{
    const r=await api('/missions/'+encodeURIComponent(missionId)+'/graph'); if(!r.success) throw new Error(r.message);
    const nodes=(r.data&&r.data.nodes)||[];
    if(!nodes.length){ box.innerHTML='<div class="hud-state">No task graph for this mission.</div>'; return; }
    TASKFLOW[containerId]={nodes, byId:Object.fromEntries(nodes.map(n=>[n.id,n]))};
    box.innerHTML=
      `<div class="mrflow-tabs">
        <button class="hud-act primary" data-v="dag" data-onclick="switchFlow('${containerId}','dag',this)">DAG</button>
        <button class="hud-act" data-v="timeline" data-onclick="switchFlow('${containerId}','timeline',this)">Timeline</button>
       </div>
       <div class="mrflow-view" id="${containerId}-view"></div>
       <div class="mrflow-drawer" id="${containerId}-drawer"></div>`;
    renderFlowView(containerId,'dag');
  }catch(e){ box.innerHTML=`<div class="hud-state err">Could not build flow: ${escapeHtml(e.message)}</div>`; }
}
const TASKFLOW={};
function switchFlow(cid,view,btn){
  if(btn){ btn.parentElement.querySelectorAll('.hud-act').forEach(b=>b.classList.remove('primary')); btn.classList.add('primary'); }
  renderFlowView(cid,view);
}
const FLOW_STATUS_COL={complete:'var(--green)',failed:'var(--red)',running:'var(--hud-cyan)',skipped:'var(--dim)',blocked:'var(--amber)',ready:'var(--amber)',pending:'var(--amber)'};
function renderFlowView(cid,view){
  const v=document.getElementById(cid+'-view'), data=TASKFLOW[cid]; if(!v||!data) return;
  v.innerHTML = view==='timeline' ? flowTimeline(cid,data) : flowDag(cid,data);
}
function flowDag(cid,data){
  const {nodes,byId}=data;
  // Longest-path level (column) per node from its dependencies (memoized, cycle-safe).
  const lvl={}, seen={};
  function level(id){
    if(lvl[id]!=null) return lvl[id];
    if(seen[id]) return 0; seen[id]=1;
    const n=byId[id]; const deps=(n&&(n.dependency_ids||n.depends_on))||[];
    let m=0; deps.forEach(d=>{ if(byId[d]) m=Math.max(m,level(d)+1); });
    return lvl[id]=m;
  }
  nodes.forEach(n=>level(n.id));
  const cols={}; nodes.forEach(n=>{ const L=lvl[n.id]||0; (cols[L]=cols[L]||[]).push(n); });
  const colW=150, rowH=42, padX=14, padY=14, boxW=124, boxH=28;
  const maxRow=Math.max(1,...Object.values(cols).map(c=>c.length));
  const width=padX*2+ (Math.max(1,Object.keys(cols).length))*colW;
  const height=padY*2+ maxRow*rowH;
  const pos={};
  Object.keys(cols).forEach(L=>{ cols[L].forEach((n,i)=>{ pos[n.id]={x:padX+L*colW, y:padY+i*rowH}; }); });
  let edges='';
  nodes.forEach(n=>{ ((n.dependency_ids||n.depends_on)||[]).forEach(d=>{
    if(!pos[d]||!pos[n.id]) return;
    const a=pos[d], b=pos[n.id];
    const x1=a.x+boxW, y1=a.y+boxH/2, x2=b.x, y2=b.y+boxH/2;
    const fail=(byId[d]&&byId[d].status==='failed')||n.status==='failed';
    edges+=`<path class="dag-edge ${fail?'fail':''}" d="M${x1},${y1} C${x1+30},${y1} ${x2-30},${y2} ${x2},${y2}"/>`;
  }); });
  const boxes=nodes.map(n=>{
    const p=pos[n.id], st=(n.status||'pending');
    const label=escapeHtml((n.title||n.assigned_ant||'task').slice(0,20));
    return `<g class="dag-node ${st}" data-onclick="flowSelect('${cid}','${n.id}')" style="color:${FLOW_STATUS_COL[st]||'var(--amber)'}">
      <rect x="${p.x}" y="${p.y}" width="${boxW}" height="${boxH}"/>
      <text x="${p.x+7}" y="${p.y+12}">${label}</text>
      <text x="${p.x+7}" y="${p.y+23}" style="fill:var(--dim);font-size:8px">${escapeHtml(n.assigned_ant||'')} · ${escapeHtml(st)}</text>
    </g>`;
  }).join('');
  return `<div class="mrflow-dag"><svg viewBox="0 0 ${width} ${height}" width="${width}" height="${height}">${edges}${boxes}</svg></div>`;
}
function flowTimeline(cid,data){
  const {nodes}=data;
  const withStart=nodes.map(n=>({n,ts:Date.parse(n.started_at||n.created_at||'')||0}));
  withStart.sort((a,b)=>a.ts-b.ts);
  const durs=nodes.map(n=>+n.elapsed_seconds||0); const maxDur=Math.max(1,...durs);
  const t0=Math.min(...withStart.map(x=>x.ts).filter(Boolean)); const span=Math.max(1,Math.max(...withStart.map(x=>x.ts).filter(Boolean))-t0);
  return withStart.map(({n,ts})=>{
    const st=(n.status||'pending'), col=FLOW_STATUS_COL[st]||'var(--amber)';
    const dur=+n.elapsed_seconds||0;
    const offPct = ts&&t0 ? Math.max(0,Math.min(96,(ts-t0)/span*96)) : 0;
    const wPct = Math.max(2,Math.min(100-offPct, dur/maxDur*100));
    return `<div class="tl-row" data-onclick="flowSelect('${cid}','${n.id}')">
      <span class="tl-dot" style="background:${col}"></span>
      <span class="tl-name" title="${escapeHtml(n.title||'')}">${escapeHtml(n.title||n.assigned_ant||'task')}</span>
      <span class="tl-track"><span class="tl-bar" style="left:${offPct}%;width:${wPct}%;background:${col}"></span></span>
      <span class="tl-dur">${dur?dur.toFixed(1)+'s':'—'}</span>
    </div>`;
  }).join('');
}
function flowSelect(cid,taskId){
  const data=TASKFLOW[cid]; if(!data) return;
  const n=data.byId[taskId]; if(!n) return;
  document.querySelectorAll('#'+cid+'-view .dag-node').forEach(g=>g.classList.remove('sel'));
  const drawer=document.getElementById(cid+'-drawer'); if(!drawer) return;
  const reason=n.status_message||n.failure_type||'';
  const row=(k,val)=>val?`<div style="display:flex;gap:8px;padding:1px 0"><span style="color:var(--dim);min-width:90px">${k}</span><span>${escapeHtml(String(val))}</span></div>`:'';
  drawer.innerHTML=
    `<div style="font-weight:600;margin-bottom:4px">${escapeHtml(n.title||'task')}</div>`+
    row('Ant', n.assigned_ant)+row('Worker', n.assigned_worker)+row('Type', n.task_type)+row('Status', n.status)+
    row('Elapsed', n.elapsed_seconds!=null?(+n.elapsed_seconds).toFixed(1)+'s':'')+
    row('Attempts', (n.attempt_count!=null?`${n.attempt_count}/${n.max_attempts??1}`:''))+
    (reason?`<div style="color:${n.status==='failed'?'var(--red)':'var(--muted)'};margin-top:4px">${escapeHtml(reason)}</div>`:'');
  drawer.classList.add('show');
}

// -- Results page: expandable mission reports ---------------------------------
let resultsMissions=[], resultsPendingExpand=null;

PAGE_ENTER['results']=()=>loadResults();
document.getElementById('results-refresh').addEventListener('click',()=>loadResults());
document.getElementById('results-filter').addEventListener('change',()=>renderResultsList());

async function loadResults(){
  try{
    const r=await api('/missions/json?limit=100'); if(!r.success) throw new Error(r.message);
    resultsMissions=r.data||[];
    renderResultsList();
  }catch(e){ document.getElementById('results-list').innerHTML=`<div style="color:var(--red);padding:20px;">Error loading missions: ${escapeHtml(e.message)}</div>`; }
}

function renderResultsList(){
  const filter=document.getElementById('results-filter').value;
  const list=document.getElementById('results-list');
  const rows=resultsMissions.filter(m=>!filter||m.status===filter);
  if(!rows.length){ list.innerHTML='<div style="color:var(--dim);padding:20px;text-align:center;">No missions match.</div>'; return; }
  list.innerHTML=rows.map(m=>{
    const [stLabel,stColor]=MR_STATUS[m.status]||[m.status,'var(--text)'];
    const score=m.success_score!=null?Number(m.success_score).toFixed(2):'—';
    return `<details class="card" style="margin-bottom:8px;padding:0;" data-mission="${m.id}" data-ontoggle="onResultToggle(this)">
      <summary style="cursor:pointer;list-style:none;display:flex;align-items:center;gap:12px;padding:12px 16px;user-select:none;">
        <span style="color:${stColor};font-weight:600;white-space:nowrap;font-size:11px;">${stLabel}</span>
        <span style="flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:12px;">${escapeHtml(m.goal||'')}</span>
        <span style="color:var(--dim);font-size:10px;font-family:var(--mono);white-space:nowrap;">score ${score} · ${fmtTime(m.saved_at||m.created_at)}</span>
        <span class="box-caret" style="color:var(--dim);font-size:11px;">▾</span>
      </summary>
      <div class="mr-body" style="padding:4px 16px 14px;border-top:1px solid var(--border);font-family:var(--mono);font-size:12px;line-height:1.7;white-space:pre-wrap;">
        <div style="color:var(--dim);padding:8px 0;">Loading report…</div>
      </div>
    </details>`;
  }).join('');
  if(resultsPendingExpand){
    const target=list.querySelector(`details[data-mission="${resultsPendingExpand}"]`);
    resultsPendingExpand=null;
    if(target){ target.open=true; target.scrollIntoView({behavior:'smooth',block:'start'}); }
  }
}

async function onResultToggle(det){
  const caret=det.querySelector('.box-caret'); if(caret) caret.textContent=det.open?'▾':'▸';
  if(!det.open||det.dataset.loaded||det.dataset.loading) return;
  // v2.17.1: mark loaded only AFTER the report succeeds, so a failed one can be retried by
  // closing and reopening — it used to latch on the first attempt whatever the outcome.
  det.dataset.loading='1';
  const ok=await renderMissionReport(det.dataset.mission, det.querySelector('.mr-body'));
  delete det.dataset.loading;
  if(ok) det.dataset.loaded='1';
}

// Deep link: open the Results page with one mission expanded (View Result buttons land here).
function openResults(missionId){
  resultsPendingExpand=missionId||null;
  showPage('results');
}

// -- Patch Center (v1.8.23) ---------------------------------------------------
function pcToast(msg,ok=true){
  let el=document.getElementById('pc-toast');
  if(!el){ el=document.createElement('div'); el.id='pc-toast';
    el.style.cssText='position:fixed;bottom:20px;right:20px;z-index:9999;padding:10px 16px;border-radius:8px;font-size:12px;box-shadow:0 4px 16px rgba(0,0,0,.4);max-width:340px;';
    document.body.appendChild(el); }
  el.style.background=ok?'rgba(16,185,129,.95)':'rgba(239,68,68,.95)'; el.style.color='#fff';
  el.textContent=msg; el.style.opacity='1';
  clearTimeout(el._t); el._t=setTimeout(()=>{ el.style.transition='opacity .4s'; el.style.opacity='0'; },3200);
}
let pcFilters={status:'',risk:'',file:'',mission_id:'',objective_id:''};
let pcPending=null; // filter to apply on next entry (deep-link from Results/Autonomy/Objectives)

// Compact "Patches: 2 applied, 1 pending" cell used in the Autonomy runs table and elsewhere.
// Clicking opens the Patch Center filtered to the given mission or objective.
function patchSummaryText(c){
  if(!c||!c.total) return 'Patches: 0';
  const parts=[];
  if(c.applied) parts.push(`${c.applied} applied`);
  if(c.pending||c.proposed) parts.push(`${c.pending||c.proposed} pending`);
  if(c.approved) parts.push(`${c.approved} approved`);
  if(c.rejected) parts.push(`${c.rejected} rejected`);
  if(c.failed) parts.push(`${c.failed} failed`);
  return 'Patches: '+(parts.join(', ')||c.total);
}
function patchCellHtml(counts,missionId,objectiveId){
  const c=counts||{}; const txt=patchSummaryText(c);
  if(!c.total) return `<span style="color:var(--dim)">${txt}</span>`;
  const filter=objectiveId?`{objective_id:'${objectiveId}'}`:(missionId?`{mission_id:'${missionId}'}`:'{}');
  const color=c.failed?'var(--red)':(c.applied?'var(--green)':'var(--queen)');
  return `<a href="#" data-onclick="openPatches(${filter});return false;" style="color:${color};text-decoration:none;" title="Open in Patch Center">${txt}</a>`;
}

PAGE_ENTER['patches']=()=>{ if(ROLE==='admin') loadPatches(); };

function pcBind(){
  const s=document.getElementById('pc-filter-status'), rk=document.getElementById('pc-filter-risk');
  const f=document.getElementById('pc-filter-file'), m=document.getElementById('pc-filter-mission'), o=document.getElementById('pc-filter-objective');
  if(!s) return;
  [s,rk].forEach(el=>el.addEventListener('change',()=>{ syncPcFilters(); loadPatches(); }));
  let t=null;
  [f,m,o].forEach(el=>el.addEventListener('input',()=>{ clearTimeout(t); t=setTimeout(()=>{ syncPcFilters(); loadPatches(); },350); }));
  document.getElementById('patches-refresh').addEventListener('click',loadPatches);
  document.getElementById('pc-filter-clear').addEventListener('click',()=>{
    pcFilters={status:'',risk:'',file:'',mission_id:'',objective_id:''}; applyPcFiltersToInputs(); loadPatches();
  });
  const grp=document.getElementById('pc-group');
  if(grp){
    grp.value=pcGroup;
    if(grp.value!==pcGroup){ pcGroup=''; localStorage.removeItem('pc-group'); } // stale stored value
    grp.addEventListener('change',()=>{ pcGroup=grp.value; localStorage.setItem('pc-group',pcGroup); loadPatches(); });
  }
}
function syncPcFilters(){
  pcFilters.status=document.getElementById('pc-filter-status').value;
  pcFilters.risk=document.getElementById('pc-filter-risk').value;
  pcFilters.file=document.getElementById('pc-filter-file').value.trim();
  pcFilters.mission_id=document.getElementById('pc-filter-mission').value.trim();
  pcFilters.objective_id=document.getElementById('pc-filter-objective').value.trim();
}
function applyPcFiltersToInputs(){
  document.getElementById('pc-filter-status').value=pcFilters.status||'';
  document.getElementById('pc-filter-risk').value=pcFilters.risk||'';
  document.getElementById('pc-filter-file').value=pcFilters.file||'';
  document.getElementById('pc-filter-mission').value=pcFilters.mission_id||'';
  document.getElementById('pc-filter-objective').value=pcFilters.objective_id||'';
}
// Deep-link entry: openPatches({mission_id:'…'}) or {objective_id} or {status}.
function openPatches(filter){
  pcFilters={status:'',risk:'',file:'',mission_id:'',objective_id:'',...(filter||{})};
  pcPending=true;
  showPage('patches');
}
async function loadPatches(){
  const list=document.getElementById('patches-list'); if(!list) return;
  if(pcPending){ applyPcFiltersToInputs(); pcPending=null; } else { syncPcFilters(); }
  const qs=new URLSearchParams();
  if(pcFilters.status) qs.set('status',pcFilters.status);
  if(pcFilters.risk) qs.set('risk',pcFilters.risk);
  if(pcFilters.file) qs.set('file',pcFilters.file);
  if(pcFilters.mission_id) qs.set('mission_id',pcFilters.mission_id);
  if(pcFilters.objective_id) qs.set('objective_id',pcFilters.objective_id);
  qs.set('limit','300');
  try{
    const r=await api('/patches?'+qs.toString()); if(!r.success) throw new Error(r.message);
    const rows=r.data||[];
    renderPcSummary(rows);
    renderPatchList(rows);
  }catch(e){ list.innerHTML=`<div style="color:var(--red);padding:20px;">Error loading patches: ${escapeHtml(e.message)}</div>`; }
}
function renderPcSummary(rows){
  const box=document.getElementById('pc-summary');
  const by={};(rows||[]).forEach(p=>{ by[p.status]=(by[p.status]||0)+1; });
  const chip=(label,key)=>`<span class="pc-chip">${label} <b>${by[key]||0}</b></span>`;
  box.innerHTML=`<span class="pc-chip">Total <b>${rows.length}</b></span>`+
    chip('Pending','proposed')+chip('Approved','approved')+chip('Applied','applied')+
    chip('Rejected','rejected')+chip('Failed','failed')+
    (by['superseded']?chip('Superseded','superseded'):'');
}
function renderPatchCard(p){
  const badge=`<span class="pc-badge ${p.status}">${escapeHtml(p.status_label||p.status)}</span>`;
  const risk=`<span class="pc-risk ${p.risk||'unknown'}">${escapeHtml(p.risk||'unknown')}</span>`;
  const src=p.objective_id?`obj ${escapeHtml(String(p.objective_id).slice(0,8))}`:(p.mission_id?`mission ${escapeHtml(String(p.mission_id).slice(0,8))}`:'manual');
  return `<details class="card" style="margin-bottom:8px;padding:0;" data-patch="${p.id}" data-ontoggle="onPatchToggle(this)">
    <summary style="cursor:pointer;list-style:none;display:flex;align-items:center;gap:10px;padding:11px 14px;user-select:none;">
      ${badge}${risk}
      <span style="flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-family:var(--mono);font-size:12px;">${escapeHtml(p.file_path||'?')}</span>
      <span style="color:var(--dim);font-size:10px;white-space:nowrap;">${escapeHtml(p.change_type||'modify')} · ${escapeHtml(src)} · ${fmtTime(p.created_at)||''}</span>
      <span class="box-caret" style="color:var(--dim);font-size:11px;">▾</span>
    </summary>
    <div class="pc-body" style="padding:6px 14px 14px;border-top:1px solid var(--border);">
      <div style="color:var(--muted);font-size:11px;padding:4px 0;">${escapeHtml(p.reason||'')}</div>
      <div class="pc-detail" style="color:var(--dim);font-size:11px;">Expand to load the diff…</div>
    </div>
  </details>`;
}
// UI Phase 7 (v1.8.24): optional grouping by status / risk / file / mission / objective.
// Grouping is a pure client-side re-render of the same fetched rows — filters, cards,
// diffs, and approve/reject/apply behavior are unchanged.
let pcGroup=localStorage.getItem('pc-group')||'';
const PC_GROUPERS={
  status:{ key:p=>p.status||'unknown', label:k=>k, order:['proposed','pending','approved','applied','reverted','rejected','failed','superseded','unknown'] },
  risk:{ key:p=>p.risk||'unknown', label:k=>`${k} risk`, order:['high','medium','low','unknown'] },
  file:{ key:p=>p.file_path||'(no file)', label:k=>k, order:null },
  mission:{ key:p=>p.mission_id||'(manual)', label:k=>k==='(manual)'?k:`mission ${k.slice(0,12)}`, order:null },
  objective:{ key:p=>p.objective_id||'(no objective)', label:k=>k==='(no objective)'?k:`objective ${String(k).slice(0,12)}`, order:null },
};
function renderPatchList(rows){
  const list=document.getElementById('patches-list');
  if(!rows.length){ list.innerHTML='<div style="color:var(--dim);padding:20px;text-align:center;">No patch proposals match these filters.</div>'; return; }
  const g=PC_GROUPERS[pcGroup];
  if(!g){ list.innerHTML=rows.map(renderPatchCard).join(''); return; }
  const groups=new Map();
  rows.forEach(p=>{ const k=g.key(p); if(!groups.has(k)) groups.set(k,[]); groups.get(k).push(p); });
  let keys=[...groups.keys()];
  if(g.order) keys.sort((a,b)=>{ const ia=g.order.indexOf(a), ib=g.order.indexOf(b); return (ia<0?99:ia)-(ib<0?99:ib); });
  else keys.sort((a,b)=>groups.get(b).length-groups.get(a).length || String(a).localeCompare(String(b)));
  list.innerHTML=keys.map(k=>{
    const items=groups.get(k);
    const by={}; items.forEach(p=>{ by[p.status]=(by[p.status]||0)+1; });
    const chips=Object.keys(by).map(s=>`<span class="pc-chip">${escapeHtml(s)} <b>${by[s]}</b></span>`).join('');
    return `<details class="pc-group-sec" open>
      <summary><span class="pc-group-name" title="${escapeHtml(String(k))}">${escapeHtml(g.label(String(k)))}</span><span class="pc-group-count">${items.length} patch${items.length===1?'':'es'}</span><span style="flex:1"></span>${chips}</summary>
      <div class="pc-group-body">${items.map(renderPatchCard).join('')}</div>
    </details>`;
  }).join('');
}
const pcDetailCache={}; // patchId -> detail (avoids inlining patch content into onclick handlers)
async function onPatchToggle(det){
  const caret=det.querySelector('summary .box-caret'); if(caret) caret.textContent=det.open?'▾':'▸';
  if(!det.open || det.dataset.loaded) return;
  det.dataset.loaded='1';
  const box=det.querySelector('.pc-detail');
  const pid=det.dataset.patch;
  try{
    const r=await api('/patches/'+encodeURIComponent(pid)+'/detail'); if(!r.success) throw new Error(r.message);
    const d=r.data||{};
    pcDetailCache[pid]=d;
    const meta=(k,v)=>v?`<div style="display:flex;gap:8px;padding:1px 0;"><span style="color:var(--dim);min-width:110px;">${k}</span><span style="word-break:break-all;">${escapeHtml(String(v))}</span></div>`:'';
    const canApprove=d.approval_id && d.approval_status==='pending';
    // v1.8.24: a pending patch with NO approval record can now be acted on — the backend
    // creates the missing approval record first, then runs the normal approve/reject path.
    const orphanPending=d.status==='proposed' && !d.approval_id;
    // Apply is available once the patch OR its approval record is approved (and not yet applied).
    // Honoring approval_status also covers patches approved before the backend started flipping the
    // patch status, so already-approved-but-still-"proposed" rows are appliable too.
    const canApply=(d.status==='approved'||d.approval_status==='approved') && d.approval_id && d.status!=='applied';
    const canVerify=d.status==='proposed';
    const canEdit=d.status==='proposed'||d.status==='approved';
    const canRevert=d.status==='applied'; // v2.7.0: an applied patch can be undone (add→delete, modify→restore backup)
    const actions=`<div class="pc-actions">
      ${d.mission_id?`<button class="pc-act" data-onclick="openResults('${d.mission_id}')">View Mission</button>`:''}
      ${canApprove?`<button class="pc-act approve" data-onclick="pcApprove('${d.approval_id}','${pid}')">✓ Approve</button>`:''}
      ${orphanPending?`<button class="pc-act approve" data-onclick="pcApproveDirect('${pid}')" title="Creates the missing approval record, then approves">✓ Approve</button>`:''}
      ${canApprove?`<button class="pc-act reject" data-onclick="pcReject('${d.approval_id}','${pid}')">✕ Reject</button>`:''}
      ${orphanPending?`<button class="pc-act reject" data-onclick="pcRejectDirect('${pid}')">✕ Reject</button>`:''}
      ${canVerify?`<button class="pc-act" id="pc-verify-${pid}" data-onclick="pcVerify('${pid}')" title="Applies the patch with a backup, runs the verify command (build+test or your configured check), ALWAYS restores the workspace, and auto-approves only if green. Never auto-applies.">${GLYPH.scale} Verify &amp; Auto-approve</button>`:''}
      ${canEdit?`<button class="pc-act" data-onclick="pcOpenEdit('${pid}')" title="Edit this patch's content and offer it as an alternative proposal (goes through the same approval gate)">✎ Edit as alternative</button>`:''}
      ${canApply?`<button class="pc-act apply" data-onclick="pcApply('${pid}')">▶ Apply</button>`:''}
      ${canRevert?`<button class="pc-act reject" data-onclick="pcRevert('${pid}')" title="Undo this applied patch: deletes the created file (add) or restores the pre-apply backup (modify), then marks it reverted. Stays in the sandboxed workspace.">↺ Revert</button>`:''}
    </div>
    <div class="pc-editbox" id="pc-edit-${pid}" style="display:none;"></div>
    <div class="pc-verifyout" id="pc-verifyout-${pid}" style="display:none;"></div>`;
    box.innerHTML=
      meta('Status', (d.status_label||d.status)+(d.approval_status?` (approval ${d.approval_status})`:''))+
      meta('Risk', (d.risk||'unknown')+(d.risk_raw&&d.risk_raw!==d.risk?` — "${d.risk_raw}"`:''))+
      meta('Objective', d.objective_title||d.objective_id)+
      meta('Mission goal', d.mission_goal)+
      meta('Applied at', d.applied_at)+
      (d.last_error?`<div style="color:var(--red);font-size:11px;margin:4px 0;">Apply error: ${escapeHtml(d.last_error)}</div>`:'')+
      renderDiff(d.old_content, d.new_content, d.change_type)+
      actions;
  }catch(e){ box.innerHTML=`<span style="color:var(--red)">Could not load patch: ${escapeHtml(e.message)}</span>`; }
}
// Minimal LCS-based unified diff — removed lines red, added lines green, context muted.
function renderDiff(oldC, newC, changeType){
  const a=(oldC==null?'':String(oldC)).split('\n');
  const b=(newC==null?'':String(newC)).split('\n');
  if(oldC==null && newC!=null){ // pure add
    return `<div class="pc-diff">${b.map((l,i)=>diffLine('add',i+1,l)).join('')}</div>`;
  }
  if(newC==null){ return `<div class="pc-diff"><div class="dl ctx" style="padding:6px 8px;">(no new content — ${escapeHtml(changeType||'delete')})</div></div>`; }
  // LCS table (bounded to keep it cheap on large files).
  const N=Math.min(a.length,1200), M=Math.min(b.length,1200);
  const dp=Array.from({length:N+1},()=>new Uint16Array(M+1));
  for(let i=N-1;i>=0;i--)for(let j=M-1;j>=0;j--)dp[i][j]=a[i]===b[j]?dp[i+1][j+1]+1:Math.max(dp[i+1][j],dp[i][j+1]);
  let i=0,j=0,out='',ln=0;
  while(i<N&&j<M){
    if(a[i]===b[j]){ out+=diffLine('ctx',++ln,a[i]); i++;j++; }
    else if(dp[i+1][j]>=dp[i][j+1]){ out+=diffLine('del','-',a[i]); i++; }
    else { out+=diffLine('add',++ln,b[j]); j++; }
  }
  while(i<N){ out+=diffLine('del','-',a[i++]); }
  while(j<M){ out+=diffLine('add',++ln,b[j++]); }
  if(a.length>1200||b.length>1200) out+=`<div class="dl ctx" style="padding:4px 8px;color:var(--queen)">…diff truncated (large file) — full content applies on Apply.</div>`;
  return `<div class="pc-diff">${out}</div>`;
}
function diffLine(cls,gutter,text){
  const sign=cls==='add'?'+':cls==='del'?'-':' ';
  return `<div class="dl ${cls}"><span class="gutter">${gutter}</span><span>${sign} ${escapeHtml(text)}</span></div>`;
}
/* v0.3.8.42 (§8): ONE double-submit rule for every patch mutation. pcVerify disabled its button;
 * the other five mutations disabled nothing, so a double-click on Approve posted twice and a slow
 * Apply invited a second Apply. The guard is keyed per patch, and the pending state lands on the
 * card's own controls — the place the operator is looking. */
const pcInFlight=new Set();
function pcSetBusy(pid,busy){
  document.querySelectorAll(`[data-patch="${(window.CSS&&CSS.escape)?CSS.escape(pid):pid}"] .pc-act`)
    .forEach(b=>{ b.disabled=busy; });
}
async function pcMutate(pid,fn){
  if(pcInFlight.has(pid)) return;
  pcInFlight.add(pid); pcSetBusy(pid,true);
  try{ await fn(); }
  finally{ pcInFlight.delete(pid); pcSetBusy(pid,false); }
}
async function pcApprove(approvalId,patchId){
  await pcMutate(patchId, async ()=>{
    try{ const res=await fetch(url('/approve/'+approvalId),{method:'POST',headers:{'Authorization':'Bearer '+TOKEN}});
      if(!res.ok) throw new Error('HTTP '+res.status);
      pcToast('Patch approved — apply it below to write it to disk.'); loadPatches(); pollApprovals();
    }catch(e){ pcToast('Approve failed: '+e.message,false); }
  });
}
async function pcReject(approvalId,patchId){
  if(!await uiConfirm('Reject this patch? It stays visible in history but will not be applied.')) return;
  await pcMutate(patchId, async ()=>{
    try{ const res=await fetch(url('/reject/'+approvalId),{method:'POST',headers:{'Authorization':'Bearer '+TOKEN,'Content-Type':'application/json'},body:JSON.stringify({reason:'Rejected from Patch Center'})});
      if(!res.ok) throw new Error('HTTP '+res.status);
      pcToast('Patch rejected.'); loadPatches(); pollApprovals();
    }catch(e){ pcToast('Reject failed: '+e.message,false); }
  });
}
async function pcApply(patchId){
  const d=pcDetailCache[patchId]; if(!d){ pcToast('Reopen the patch and try again.',false); return; }
  // Operator safety checks (v1.8.23): warn before writing to disk.
  const warns=[];
  if((d.risk==='medium'||d.risk==='high')) warns.push(`risk is ${d.risk.toUpperCase()}`);
  if(!d.old_content && d.change_type!=='add') warns.push('old content is missing/partial (context may be incomplete)');
  if(!d.has_backup) warns.push('no pre-apply backup recorded yet');
  const msg='Apply this patch to '+(d.file_path||'the file')+'?\n\n'+
    (warns.length?('⚠ '+warns.join('\n⚠ ')+'\n\n'):'')+
    'This writes to disk. A backup is taken and a failed build auto-rolls back.';
  if(!await uiConfirm(msg)) return;
  await pcMutate(patchId, async ()=>{
    try{ const res=await fetch(url('/apply/'+d.approval_id),{method:'POST',headers:{'Authorization':'Bearer '+TOKEN}});
      const txt=await res.text();
      if(!res.ok){
        // v1.10.0: surface the server's reason instead of a bare HTTP code, and give the operator
        // the actual next step when the apply capability gate is off.
        let msg2='HTTP '+res.status;
        try{ const j=JSON.parse(txt); if(j&&j.message) msg2=j.message; }catch(e2){}
        if(res.status===403&&/apply_patch|disabled/i.test(msg2)) msg2+=' → enable "Patch application" in Settings, then retry.';
        throw new Error(msg2);
      }
      pcToast('Apply requested — see result in the patch row.'); loadPatches();
    }catch(e){ pcToast('Apply failed: '+e.message,false); }
  });
}
// v2.7.0: undo an applied patch. Deletes the created file (add) or restores the pre-apply backup
// (modify), then the row flips to "reverted". Fulfills the Changes page's "roll back" promise.
async function pcRevert(patchId){
  const d=pcDetailCache[patchId]||{};
  const msg='Revert this applied patch to '+(d.file_path||'the file')+'?\n\n'+
    (d.change_type==='add'
      ? 'The file this patch created will be deleted.'
      : 'The file will be restored from its pre-apply backup (if one was recorded).')+
    '\n\nThis writes to the sandboxed workspace.';
  if(!await uiConfirm(msg)) return;
  await pcMutate(patchId, async ()=>{
    try{ const res=await fetch(url('/revert/'+patchId),{method:'POST',headers:{'Authorization':'Bearer '+TOKEN}});
      const txt=await res.text();
      if(!res.ok){ let m='HTTP '+res.status; try{ const j=JSON.parse(txt); if(j&&j.message) m=j.message; }catch(e2){} throw new Error(m); }
      pcToast('Patch reverted.'); apiCacheBust('/patches'); loadPatches();
    }catch(e){ pcToast('Revert failed: '+e.message,false); }
  });
}
// -- Patch Center 2.0 (v1.8.24): approve/reject by patch id, verify, edit-as-alternative --------
async function pcApproveDirect(patchId){
  await pcMutate(patchId, async ()=>{
    try{ const res=await fetch(url('/patches/'+encodeURIComponent(patchId)+'/approve'),{method:'POST',headers:{'Authorization':'Bearer '+TOKEN}});
      if(!res.ok) throw new Error('HTTP '+res.status);
      pcToast('Approval record created and patch approved — apply it to write to disk.'); loadPatches(); pollApprovals();
    }catch(e){ pcToast('Approve failed: '+e.message,false); }
  });
}
async function pcRejectDirect(patchId){
  if(!await uiConfirm('Reject this patch? It stays visible in history but will not be applied.')) return;
  await pcMutate(patchId, async ()=>{
    try{ const res=await fetch(url('/patches/'+encodeURIComponent(patchId)+'/reject'),{method:'POST',headers:{'Authorization':'Bearer '+TOKEN,'Content-Type':'application/json'},body:JSON.stringify({reason:'Rejected from Patch Center'})});
      if(!res.ok) throw new Error('HTTP '+res.status);
      pcToast('Patch rejected.'); loadPatches(); pollApprovals();
    }catch(e){ pcToast('Reject failed: '+e.message,false); }
  });
}
async function pcVerify(patchId){
  const btn=document.getElementById('pc-verify-'+patchId);
  const out=document.getElementById('pc-verifyout-'+patchId);
  // innerHTML, not textContent: the label carries the same inline scale icon as the button's
  // template — constant markup, nothing operator-authored rides in.
  if(btn){ btn.disabled=true; btn.innerHTML=`${GLYPH.scale} Verifying… (build+test)`; }
  if(out){ out.style.display='block'; out.innerHTML='<div style="color:var(--dim);font-size:11px;padding:6px 0;">Running unbiased verification — the patch is applied with a backup, the verify command runs, and the workspace is always restored. This can take a few minutes.</div>'; }
  try{
    const r=await api('/patches/'+encodeURIComponent(patchId)+'/verify','POST');
    if(!r.success) throw new Error(r.message||'verify endpoint error');
    const d=r.data||{};
    const tail=(d.output_tail||'').trim();
    if(d.verified){
      pcToast('Verification PASSED — patch auto-approved. Apply is still manual.');
      if(out) out.innerHTML=`<div style="color:var(--green);font-size:11px;padding:4px 0;">✓ Verify green (exit ${d.exit_code}, ${d.seconds}s) — auto-approved.</div>`+(tail?`<pre class="pc-verifytail">${escapeHtml(tail)}</pre>`:'');
    }else{
      pcToast('Verification FAILED — patch stays pending. Workspace restored.',false);
      if(out) out.innerHTML=`<div style="color:var(--red);font-size:11px;padding:4px 0;">✕ Verify failed (${d.timed_out?'timed out':'exit '+d.exit_code}${d.seconds?', '+d.seconds+'s':''})${d.error?' — '+escapeHtml(d.error):''}. Patch left pending; workspace restored.</div>`+(tail?`<pre class="pc-verifytail">${escapeHtml(tail)}</pre>`:'');
    }
    loadPatches._dirty=true;
  }catch(e){
    pcToast('Verify failed: '+e.message,false);
    if(out) out.innerHTML=`<div style="color:var(--red);font-size:11px;">Verify error: ${escapeHtml(e.message)}</div>`;
  }finally{
    if(btn){ btn.disabled=false; btn.innerHTML=`${GLYPH.scale} Verify &amp; Auto-approve`; }
  }
}
function pcOpenEdit(patchId){
  const box=document.getElementById('pc-edit-'+patchId); if(!box) return;
  if(box.style.display!=='none'){ box.style.display='none'; box.innerHTML=''; return; }
  const d=pcDetailCache[patchId]||{};
  box.style.display='block';
  box.innerHTML=`
    <div style="border-top:1px dashed var(--border);margin-top:8px;padding-top:8px;">
      <div style="font-size:11px;color:var(--muted);margin-bottom:4px;">Edit the proposed content and submit it as an <b>alternative patch</b>. It becomes a new proposal behind the normal approval gate — nothing is written to disk here. The original is marked superseded.</div>
      <textarea id="pc-edit-content-${patchId}" class="form-input" spellcheck="false" style="width:100%;min-height:220px;font-family:var(--mono);font-size:11px;white-space:pre;overflow:auto;"></textarea>
      <input id="pc-edit-reason-${patchId}" class="form-input" placeholder="Why this alternative? (optional)" style="width:100%;margin-top:6px;font-size:11px;">
      <div style="display:flex;gap:8px;margin-top:6px;align-items:center;">
        <button class="pc-act approve" data-onclick="pcSubmitAlternative('${patchId}')">Submit alternative</button>
        <label style="font-size:10px;color:var(--dim);display:flex;align-items:center;gap:4px;"><input type="checkbox" id="pc-edit-supersede-${patchId}" checked> supersede original</label>
        <button class="pc-act" data-onclick="pcOpenEdit('${patchId}')">Cancel</button>
      </div>
    </div>`;
  const ta=document.getElementById('pc-edit-content-'+patchId);
  ta.value=(d.new_content!=null?String(d.new_content):'');
  ta.focus();
}
async function pcSubmitAlternative(patchId){
  const ta=document.getElementById('pc-edit-content-'+patchId); if(!ta) return;
  const content=ta.value;
  if(!content.trim()){ pcToast('Alternative content is empty.',false); return; }
  const reason=(document.getElementById('pc-edit-reason-'+patchId)?.value||'').trim();
  const supersede=!!document.getElementById('pc-edit-supersede-'+patchId)?.checked;
  try{
    const r=await api('/patches/'+encodeURIComponent(patchId)+'/alternative','POST',{new_content:content,reason:reason,supersede_original:supersede});
    if(!r.success) throw new Error(r.message||'alternative endpoint error');
    pcToast('Alternative patch proposed — it appears in the list as a new pending patch.');
    loadPatches(); pollApprovals();
  }catch(e){ pcToast('Alternative failed: '+e.message,false); }
}
pcBind();

// -- Objective Command Board (v1.8.23, UI Phase 5) ----------------------------
// Lanes by lifecycle. Each objective lands in exactly one lane (precedence top?down).
const OB_LANES=[
  ['backlog','Backlog','var(--dim)'],
  ['active','Active','var(--hud-cyan)'],
  ['paused','Paused','var(--amber)'],
  ['completed','Completed','var(--green)'],
  ['stopped','Stopped','var(--blue)'],
  ['looping','Looping','var(--orange)'],
  ['failed','Failed','var(--red)'],
];
PAGE_ENTER['objboard']=()=>{ if(ROLE==='admin'){ objLoadProjectNames().then(loadObjBoard); } };
function objLane(o){
  const st=(o.status||'').toLowerCase();
  const end=o.end_reason||(o.retired_code==='looping_goals'?'retired_looping':null);
  if(end==='retired_looping'||o.retired_code==='looping_goals') return 'looping';
  if(end==='failed') return 'failed';
  if(st==='done'){
    if(end==='stopped_no_followup_required'||end==='manually_stopped') return 'stopped';
    return 'completed';
  }
  if(st==='paused') return 'paused';
  if(st==='active') return 'active';
  if(st==='pending') return 'backlog';
  return 'active';
}
/* v0.3.8.48 — objectives are project-owned. The board shows each objective's project (or the
 * word "unassigned" for legacy rows) and links back to the owning workspace. */
let objProjectNames={};
async function objLoadProjectNames(){
  const r=await api('/projects');
  objProjectNames=Object.fromEntries(((r&&r.data&&r.data.projects)||[]).map(p=>[p.id,p.name]));
}
let objFilterProject='', objFilterSearch='';
function objMatchesFilters(o){
  if(objFilterProject && (o.project_id||'')!==objFilterProject) return false;
  if(objFilterSearch){
    const q=objFilterSearch.toLowerCase();
    if(!((o.title||'').toLowerCase().includes(q)||(o.charter||'').toLowerCase().includes(q))) return false;
  }
  return true;
}
if(!window.__objFiltersWired){ window.__objFiltersWired=true;
  document.getElementById('obj-filter-project')?.addEventListener('change',e=>{ objFilterProject=e.target.value; loadObjBoard(); });
  document.getElementById('obj-filter-search')?.addEventListener('input',e=>{ clearTimeout(window.__objFT);
    window.__objFT=setTimeout(()=>{ objFilterSearch=e.target.value.trim(); loadObjBoard(); },250); });
}
async function loadObjBoard(){
  const board=document.getElementById('ob-board'); if(!board) return;
  board.innerHTML='<div class="hud-state"><div class="hud-spinner"></div>Loading objectives…</div>';
  try{
    const r=await api('/objectives'); if(!r.success) throw new Error(r.message);
    const objectives=Array.isArray(r.data)?r.data:[];
    setEl('ob-count',`${objectives.length} objective${objectives.length===1?'':'s'}`);
    // v0.3.8.48: project + search filters apply before laning; the dropdown carries live names.
    const pf=document.getElementById('obj-filter-project');
    if(pf&&pf.options.length<=1)
      for(const [pid,name] of Object.entries(objProjectNames))
        pf.insertAdjacentHTML('beforeend',`<option value="${escapeHtml(pid)}">${escapeHtml(name)}</option>`);
    const byLane={}; OB_LANES.forEach(([k])=>byLane[k]=[]);
    objectives.filter(objMatchesFilters).forEach(o=>{ (byLane[objLane(o)]||byLane.active).push(o); });
    board.innerHTML=OB_LANES.map(([key,name,col])=>{
      const items=byLane[key]||[];
      const cards=items.length ? items.map(o=>objCard(o)).join('') : '<div class="ob-lane-empty">—</div>';
      return `<div class="ob-lane ${key}">
        <div class="ob-lane-hd"><span class="ln-dot" style="background:${col}"></span><span class="ln-name">${name}</span><span class="ln-count">${items.length}</span></div>
        <div class="ob-lane-body">${cards}</div>
      </div>`;
    }).join('');
  }catch(e){ board.innerHTML=`<div class="hud-state err">Error loading objectives: ${escapeHtml(e.message)}</div>`; }
}
function objCard(o){
  const ema=o.success_ema==null?'—':Number(o.success_ema).toFixed(2);
  const reason=o.end_detail||o.retired_reason||'';
  return `<details class="ob-card" data-obj="${o.id}" data-ontoggle="onObjCardToggle(this)">
    <summary>
      <div class="oc-title">${escapeHtml(o.title||'(untitled)')}</div>
      <div class="oc-project" title="${o.project_id?'Owned by this project — click to open':'Legacy objective with no project owner'}">${
        o.project_id
          ? `<a href="#/projects/${escapeHtml(o.project_id)}">${escapeHtml(objProjectNames[o.project_id]||o.project_id)}</a>`
          : 'unassigned'}</div>
      <div class="oc-meta">
        <span class="ob-chip">runs ${o.run_count??0}${o.max_runs?'/'+o.max_runs:''}</span>
        <span class="ob-chip" title="Success EMA">ema ${ema}</span>
        <span class="ob-chip">prio ${o.priority??0}</span>
      </div>
      ${reason?`<div class="oc-reason">${escapeHtml(reason)}</div>`:''}
    </summary>
    <div class="oc-detail"><span style="color:var(--dim)">Expand to load runs, missions, tasks, and patches…</span></div>
  </details>`;
}
async function onObjCardToggle(det){
  if(!det.open || det.dataset.loaded) return;
  det.dataset.loaded='1';
  const box=det.querySelector('.oc-detail');
  try{
    const r=await api('/objectives/'+encodeURIComponent(det.dataset.obj)+'/detail'); if(!r.success) throw new Error(r.message);
    const d=r.data||{};
    const pc=d.patch_counts||{};
    const kv=(k,v)=>`<div class="ob-mini"><span style="color:var(--dim)">${k}:</span> ${escapeHtml(String(v??'—'))}</div>`;
    const runs=(d.runs||[]).slice(0,6).map(x=>`<div class="ob-mini">• ${escapeHtml((x.generated_goal||'').slice(0,70))} <span style="color:var(--dim)">(${escapeHtml(x.mission_status||'—')})</span></div>`).join('')||'<div class="ob-mini" style="color:var(--dim)">none</div>';
    const missions=(d.missions||[]).slice(0,6).map(x=>`<div class="ob-mini">• <a href="#" data-onclick="openResults('${x.id}');return false;" style="color:var(--blue);font-family:var(--mono)">${escapeHtml(String(x.id||'').slice(0,8))}</a> ${escapeHtml((x.goal||'').slice(0,55))} <span style="color:var(--dim)">(${escapeHtml(x.status||'—')})</span></div>`).join('')||'<div class="ob-mini" style="color:var(--dim)">none</div>';
    const patchLine = pc.total
      ? `${patchSummaryText(pc)} · <a href="#" data-onclick="openPatches({objective_id:'${d.id}'});return false;" style="color:var(--blue)">Changes →</a>`
      : 'No patches produced.';
    box.innerHTML=
      kv('End reason', d.end_reason_label||d.end_reason||'—')+
      (d.end_detail?kv('Detail', d.end_detail):'')+
      kv('Runs / Missions / Tasks', `${(d.runs||[]).length} / ${(d.missions||[]).length} / ${(d.tasks||[]).length}`)+
      kv('Ended at', d.ended_at||d.retired_at||'—')+
      `<div class="ob-mini" style="margin-top:6px;color:var(--muted);font-weight:600">Patches</div><div class="ob-mini">${patchLine}</div>`+
      `<div class="ob-mini" style="margin-top:6px;color:var(--muted);font-weight:600">Recent runs</div>${runs}`+
      `<div class="ob-mini" style="margin-top:4px;color:var(--muted);font-weight:600">Missions</div>${missions}`;
  }catch(e){ box.innerHTML=`<span style="color:var(--red)">Could not load detail: ${escapeHtml(e.message)}</span>`; }
}
document.getElementById('ob-refresh')?.addEventListener('click',loadObjBoard);
// v0.3.8.47: the self-improvement seed — fills the form, the OPERATOR presses Add. Deliberately
// not a silent create: a standing objective that starts missions against the colony's own repo
// is exactly the kind of thing that should be read before it exists.
document.getElementById('obj-seed-improve')?.addEventListener('click',()=>{
  const t=document.getElementById('obj-title'), c=document.getElementById('obj-charter'), m=document.getElementById('obj-maxruns');
  if(t) t.value='Improve ANTHILL';
  if(c) c.value='Review the ANTHILL codebase for technical debt, incomplete implementations, and missing tests. '
    +'Each run: pick ONE small, verifiable improvement, propose it as a patch with tests, and stop. '
    +'Never widen scope; every change goes through the normal review and approval pipeline.';
  if(m) m.value='10';
  setEl('obj-msg','Seeded — read it, adjust it, then press Add. Nothing was created yet.');
});

// -- Ant Inspector + Performance Observatory (v1.8.23, UI Phase 8) ------------
const ANTOBS_CASTES=[
  ['researcher','Researcher',['model']],
  ['web','Web',['web_search']],
  ['file','File',['file_tools']],
  ['coder','Coder',['file_writing','patch_application']],
  ['builder','Builder',['model']],
  ['verifier','Verifier',['model']],
];
const ANTOBS_GATELABEL={web_search:'web',file_tools:'file read',file_writing:'file write',patch_application:'apply',model:'model only'};
// v0.3.8.55: the merged page loads BOTH halves — the colony-wide/orchestration routing panel
// (openAntConfig, which now renders only into #antcfg-global) and the per-role inspector grid.
PAGE_ENTER['antobs']=()=>{ if(ROLE==='admin'){ if(typeof openAntConfig==='function') openAntConfig(); loadAntObs(); } };
async function loadAntObs(){
  const grid=document.getElementById('antobs-grid'); if(!grid) return;
  grid.innerHTML='<div class="hud-state"><div class="hud-spinner"></div>Loading ant telemetry…</div>';
  try{
    // v0.3.8.55 (field report, Models & Routing merged in): the same fetch that feeds the cards
    // also fetches what can actually RUN — /routes/json lists installed local models, configured
    // catalogs and installed agents — so each card's route selectors offer only real choices.
    const [r,rj]=await Promise.all([api('/ants/stats'),api('/routes/json')]);
    if(!r.success) throw new Error(r.message);
    obsProvModels=new Map(); obsRoutes={};
    if(rj&&rj.success&&rj.data){
      for(const m of (rj.data.available_models||[])){
        if(!obsProvModels.has(m.provider)) obsProvModels.set(m.provider,[]);
        obsProvModels.get(m.provider).push({model:m.model,label:m.label});
      }
      for(const x of (rj.data.roles||[])) obsRoutes[x.role]={provider:x.provider,model:x.model,available:x.available};
    }
    const d=r.data||{}, stats=d.ants||{}, routes=d.routes||{}, gates=d.gates||{};
    // v0.3.8.50 (field report §20): the operator's names and colors, applied wherever the ant is
    // drawn on this page. The registry id stays visible in the role line — an override is a face,
    // not a new identity.
    antProfiles=d.profiles||{};
    grid.innerHTML=ANTOBS_CASTES.map(([ant,label,gateKeys])=>{
      let am=(typeof ANT_MAP!=='undefined'&&ANT_MAP[ant])||{color:'var(--muted)',role:ant};
      const prof=antProfiles[ant]||{};
      if(prof.color) am={...am,color:prof.color};
      if(prof.display_name) label=prof.display_name;
      const s=stats[ant]||{total:0,complete:0,failed:0,skipped:0,running:0,avg_seconds:0};
      const total=s.total||0, ok=s.complete||0, fail=s.failed||0, skip=s.skipped||0;
      const rate=total?Math.round(ok/total*100):null;
      const rt=routes[ant]||{};
      const gateBadges=gateKeys.map(g=>{
        if(g==='model') return `<span class="ac-gate off">model only</span>`;
        const on=!!gates[g];
        return `<span class="ac-gate ${on?'on':'off'}">${ANTOBS_GATELABEL[g]||g}${on?'':' off'}</span>`;
      }).join('');
      const okPct=total?ok/total*100:0, failPct=total?fail/total*100:0, skipPct=total?skip/total*100:0;
      return `<div class="ant-card" style="border-left-color:${cssColor(am.color)}">
        <div class="ac-hd"><span class="ac-dot" style="color:${cssColor(am.color)};background:${cssColor(am.color)}"></span>
          <span class="ac-name">${escapeHtml(label)}</span><span class="ac-role">${escapeHtml(am.role||ant)}</span>
          <button class="chat-copy ant-edit" data-ant-edit="${escapeHtml(ant)}" title="Edit this ant's name and color" aria-label="Edit ant profile">✎</button></div>
        <div class="ant-edit-slot" data-ant-slot="${escapeHtml(ant)}" hidden></div>
        ${obsRoutes[ant]
          ? obsRouteControls(ant, obsRoutes[ant])
          : `<div class="ac-route">${escapeHtml(rt.provider||'ollama')} · ${escapeHtml(rt.model||'—')}</div>`}
        <div class="ac-gates">${gateBadges}</div>
        <div class="ac-stats">
          <div class="ac-stat"><div class="n" style="color:var(--text)">${total}</div><div class="l">Tasks</div></div>
          <div class="ac-stat"><div class="n" style="color:var(--green)">${ok}</div><div class="l">Done</div></div>
          <div class="ac-stat"><div class="n" style="color:${fail?'var(--red)':'var(--dim)'}">${fail}</div><div class="l">${ant==='verifier'?'Reject':'Failed'}</div></div>
          <div class="ac-stat"><div class="n" style="color:${rate==null?'var(--dim)':(rate>=70?'var(--green)':rate>=40?'var(--queen)':'var(--red)')}">${rate==null?'—':rate+'%'}</div><div class="l">Success</div></div>
        </div>
        <div class="ac-bar">
          <i style="width:${okPct}%;background:var(--green)"></i><i style="width:${failPct}%;background:var(--red)"></i><i style="width:${skipPct}%;background:var(--dim)"></i>
        </div>
        <div class="ac-sub">avg ${s.avg_seconds?Number(s.avg_seconds).toFixed(1)+'s':'—'}/task${skip?` · ${skip} skipped`:''}${s.running?` · ${s.running} running`:''}</div>
        <details data-ant="${ant}" data-ontoggle="onAntRecentToggle(this)"><summary>recent activity</summary><div class="ac-recent"><div style="color:var(--dim)">Expand to load…</div></div></details>
      </div>`;
    }).join('');
    // v0.3.8.56 (field report): planner, strategist, conversation and fallback are CARDS IN THIS
    // GRID now, not a separate panel above it — same shape, same immediate-save route selectors.
    // They carry no gates, telemetry or profile editor because they have none: they are
    // control-plane roles, and inventing empty stats for them would claim telemetry that does
    // not exist. ORCHESTRATION_ROLES (inspector-routing.js) supplies label and purpose.
    const antIds=new Set(ANTOBS_CASTES.map(x=>x[0]));
    const orch=(typeof ORCHESTRATION_ROLES!=='undefined'?ORCHESTRATION_ROLES:[])
      .filter(r=>!antIds.has(r.id) && obsRoutes[r.id]);
    grid.innerHTML += orch.map(r=>`<div class="ant-card" style="border-left-color:${cssColor('#8b93a8')}">
        <div class="ac-hd"><span class="ac-dot" style="color:#8b93a8;background:#8b93a8"></span>
          <span class="ac-name">${escapeHtml(r.label)}</span><span class="ac-role">orchestration · ${escapeHtml(r.id)}</span></div>
        ${obsRouteControls(r.id, obsRoutes[r.id])}
        <div class="ac-sub" style="margin-top:6px;line-height:1.5;">${escapeHtml(r.why)}</div>
      </div>`).join('');
    wireAntProfileEditors(grid);
    wireObsRouting(grid);
    loadAntObsDirectory(grid);
  }catch(e){ grid.innerHTML=`<div class="hud-state err">Error loading ant stats: ${escapeHtml(e.message)}</div>`; }
}

// v2.2.4: full colony directory — the six cards above are the legacy executable castes with task
// telemetry; this section lists EVERY registry ant (roles + workers) with its purpose so any ant
// can be inspected here, matching the colony map inspector.
async function loadAntObsDirectory(grid){
  try{
    // Stage F: truthful runtime state per role — never a bare 'inactive'.
    //
    // v3.8.34: this fetched `/colony/graph`, a route that does not exist in the API. The request
    // 404'd on every load, so `rt` stayed empty, `s` was always undefined, and the `${s?…:''}`
    // guard below omitted the runtime state line from every card — the comment above described
    // behaviour this function never had. `runtime_status` is returned by `/colony/registry`
    // (ApiHost.Routes.cs), which this function already fetches, so the second request was wrong
    // AND redundant. Line ~986 reads the same field off the same response correctly; of the two
    // call sites for one contract, this was the one that disagreed.
    const r=await api('/colony/registry');
    if(!(r&&r.success&&r.data))return;
    const roles=Array.isArray(r.data.roles)?r.data.roles:(Array.isArray(r.data.Roles)?r.data.Roles:[]);
    if(!roles.length)return;
    const rt={};
    ((r.data.runtime_status)||[]).forEach(s=>{ rt[String(s.role_id).toLowerCase()]=s; });
    const total=roles.reduce((n,x)=>n+1+antWorkers(x).length,0);
    const html=roles.map(role=>{
      const rid=antRoleId(role), rname=antRoleName(role);
      let label=String(rid).toLowerCase().includes('queen')?'Queen':getRoleLabel(rid);
      if(label==='Other')label=getRoleLabel(rname);
      const color=getRoleColor(label==='Other'?rid:label);
      const workers=antWorkers(role);
      const s=rt[String(rid).toLowerCase()];
      const stateColor=s? (s.runtime_available||s.runtime_kind!=='MissionAgent'?'var(--green)':(s.implemented?'var(--orange)':'var(--dim)')) : 'var(--dim)';
      return `<div class="ant-card" style="border-left-color:${color}">
        <div class="ac-hd"><span class="ac-dot" style="color:${color};background:${color}"></span>
          <span class="ac-name">${escapeHtml(rname)}</span><span class="ac-role">${escapeHtml(label)}</span></div>
        ${s?`<div style="font-size:9px;font-weight:700;color:${stateColor};margin:2px 0;" title="${escapeHtml(s.unavailability_reason||'')}">${escapeHtml(s.status_label||'')}</div>`:''}
        ${antPurpose(role)?`<div class="ac-sub" style="margin:4px 0 6px;">${escapeHtml(antPurpose(role))}</div>`:''}
        ${workers.length?`<div style="font-size:9px;color:var(--dim);margin-bottom:2px;">WORKERS · ${workers.length}</div>`+
          workers.map(w=>`<div style="padding:3px 0;border-bottom:1px solid rgba(30,51,84,.4);font-size:10px;">
            <b style="color:var(--text)">${escapeHtml(antWorkerName(w))}</b>
            ${antPurpose(w)?`<div style="color:var(--muted);font-size:9px;">${escapeHtml(antPurpose(w))}</div>`:''}
          </div>`).join(''):''}
      </div>`;
    }).join('');
    grid.insertAdjacentHTML('beforeend',
      `<div style="grid-column:1/-1;margin-top:10px;font-size:11px;color:var(--muted);letter-spacing:.08em;">COLONY DIRECTORY · ${total} ants — every registered role and worker with its duty</div>`+html);
  }catch(e){ /* directory is additive; telemetry cards above still render */ }
}
/* ── Chat-first surface (v3.8.39) ─────────────────────────────────────────────────────────────
 *
 * The conversation as the application, rather than a widget on a grid the operator has to learn
 * first. Same endpoints the Conversations panel already uses — /conversations, /{id},
 * /{id}/turns — because the escalation gate, the approval policy and the mission handoff are
 * already right there and a second client for them would be a second thing to keep true.
 *
 * The colony is available BESIDE the conversation rather than instead of it. Watching the ants is
 * the thing an operator wants occasionally and a newcomer wants never, so it is a toggle rather
 * than the landing view.
 */
let chatActiveId = null;
let chatComposingNew = false;   // v0.3.8.42 (§13): an explicit "New conversation" wins over auto-open
let chatPendingPolicy = null;   // v0.3.8.53: a gate chosen BEFORE any conversation exists — rides creation
let chatColonyOpen = false;
// v0.3.8.40: /conversations/{id} does NOT return a title — only the list does. Without this the
// header read "Conversation" for every thread, including the one whose title was right there in
// the rail beside it.
const chatTitles = {};

function chatSetState(text){ const el=document.getElementById('chat-state'); if(el) el.textContent=text||''; }

/* v0.3.8.51 (field report) — the colony narrates its own activity. "Colony is building…" while a
 * coder/builder task runs, "Colony is working…" for everything else in flight. One small report
 * fetch per poll while a mission runs; failure keeps the previous line rather than inventing one. */
async function chatActivityLine(missionId){
  if(!missionId) return;
  try{
    const r=await api('/missions/'+encodeURIComponent(missionId)+'/report');
    const running=(r&&r.success&&r.data&&r.data.tasks||[]).find(t=>t.status==='running');
    const building=running&&(running.ant==='coder'||running.ant==='builder');
    chatSetState(building?'Colony is building…':'Colony is working…');
  }catch{ /* keep whatever the line already says */ }
}

// v0.3.8.46: the rail search — server-side over titles AND transcript content (GET
// /conversations?q=), so the box finds what the store actually holds, not what happens to be
// rendered. Kept as module state so the poll's refresh respects an in-progress search.
let chatSearchQuery='';

async function loadChat(){
  const list=document.getElementById('chat-conv-list'); if(!list) return;
  try{
    const r=await api('/conversations'+(chatSearchQuery?'?q='+encodeURIComponent(chatSearchQuery):''));
    if(!r||!r.success){ list.innerHTML=`<div class="hud-state err">${escapeHtml((r&&r.message)||'Could not load conversations.')}</div>`; return; }
    const convs=(r.data&&r.data.conversations)||[];
    if(!convs.length){
      list.innerHTML=chatSearchQuery
        ? `<div class="hud-state">No conversations match “${escapeHtml(chatSearchQuery)}”.</div>`
        : '<div class="hud-state">Nothing yet — your first message starts one.</div>';
    }else{
      convs.forEach(c=>{ chatTitles[c.id]=c.title||'Conversation'; });
      // v0.3.8.52 (field report: "cannot tell what conversations go where") — every row carries
      // its project's name on a second line, dim and small, so the tracker reads as
      // what-it-is-about over where-it-lives without stealing width from the title.
      list.innerHTML=convs.map(c=>`<div class="chat-conv${c.id===chatActiveId?' active':''}" data-id="${escapeHtml(c.id)}">
        ${escapeHtml(c.title||'Conversation')}
        <button class="conv-pin${c.pinned?' pinned':''}" data-pin="${escapeHtml(c.id)}" data-pinned="${c.pinned?'1':'0'}"
          title="${c.pinned?'Unpin this conversation':'Pin this conversation to the top'}"
          aria-label="${c.pinned?'Unpin':'Pin'}">${c.pinned?GLYPH.starFill:GLYPH.star}</button>
        ${c.cancelled?`<span class="attn" style="color:var(--dim)">Stopped</span>`
          :(c.doing||'').startsWith('running mission')?`<span class="attn">Working…</span>`:''}
        ${c.project_name?`<span class="conv-proj" title="Project: ${escapeHtml(c.project_name)}">${GLYPH.folder} ${escapeHtml(c.project_name)}</span>`:''}
      </div>`).join('');
      list.querySelectorAll('.chat-conv').forEach(el=>
        el.addEventListener('click',()=>{ chatComposingNew=false; chatOpen(el.dataset.id); }));
      // The pin is inside the clickable row; stop propagation so pinning never ALSO opens.
      list.querySelectorAll('.conv-pin').forEach(el=>
        el.addEventListener('click', async e=>{
          e.stopPropagation();
          const id=el.dataset.pin, was=el.dataset.pinned==='1';
          const r2=await api('/conversations/'+encodeURIComponent(id)+(was?'/unpin':'/pin'),'POST');
          if(r2&&r2.success) loadChat();
        }));
      // v0.3.8.42 (§13): found live — "New conversation" cleared chatActiveId, then THIS auto-open
      // immediately re-selected the old thread, so the next message went to a conversation the
      // operator had deliberately left (in the found case, a cancelled one, which refused it).
      // The auto-open is for page ENTRY only; an explicit New wins until the first send.
      // A search never auto-opens either — results are candidates, not a selection.
      if(!chatActiveId && !chatComposingNew && !chatSearchQuery) chatOpen(convs[0].id);
    }
  }catch(e){ pollWarnOnce('loadChat', e); }
}

/* v0.3.8.42 (§13) — chat quality-of-life, all frontend, all on current APIs.
 *
 * Fenced code blocks render as code. Everything passes through escapeHtml FIRST and the only
 * structure added is <pre><code> around the fenced spans — no markdown engine, no third-party
 * renderer, no new sanitisation surface. The rest of the message keeps pre-wrap text semantics. */
function chatRenderContent(text){
  const parts=String(text||'').split('```');
  let html='';
  for(let i=0;i<parts.length;i++){
    if(i%2===0){
      // v0.3.8.52 — inline `code` and **bold** in the prose segments. Escape-first is preserved:
      // both replacements run ON the escaped text and only wrap it, never widen what can render.
      let seg=escapeHtml(parts[i]);
      seg=seg.replace(/`([^`\n]+)`/g,'<code class="chat-inline">$1</code>');
      seg=seg.replace(/\*\*([^*\n]+)\*\*/g,'<b>$1</b>');
      html+=seg; continue;
    }
    // Odd segments are fenced. The first line may be a language tag; it names the highlighter.
    const nl=parts[i].indexOf('\n');
    const lang=nl>=0?parts[i].slice(0,nl).trim().toLowerCase():'';
    const code=nl>=0?parts[i].slice(nl+1):parts[i];
    html+='<pre class="chat-code"><code>'+chatHighlight(code,lang)+'</code></pre>';
  }
  return html;
}

/* v0.3.8.46 — syntax highlighting with no third-party code (UI-ALIGNMENT-BRIEF §0: no framework,
 * no bundler; CSP script-src 'self'). A single-pass tokenizer: comments, strings, numbers and a
 * per-language keyword set. THE SAFETY PROPERTY IS STRUCTURAL — every character of output passes
 * through escapeHtml before any markup is added around it, so highlighting can only ever change
 * how text looks, never what is allowed to render. An unknown language falls back to a generic
 * set; a failure to tokenize falls back to the plain escaped text it always was. */
const HL_KEYWORDS={
  js:'const let var function return if else for while do switch case default break continue new class extends import export from async await try catch finally throw typeof instanceof this null undefined true false yield of in delete void static get set',
  py:'def return if elif else for while import from as class try except finally with lambda pass break continue global nonlocal yield raise assert del not and or is in None True False async await self print',
  cs:'using namespace class struct record interface enum public private protected internal static readonly const void int long double string bool char var new return if else for foreach while do switch case default break continue try catch finally throw null true false async await this base override virtual abstract sealed partial get set is as out ref where',
  sh:'if then else elif fi for while until do done case esac function echo exit return local export set unset cd source sudo',
  sql:'select from where insert into values update delete create drop alter table index view join left right inner outer full on group by order having limit offset as distinct and or not null is primary key foreign references union all exists between like',
};
const HL_ALIAS={ javascript:'js', ts:'js', typescript:'js', jsx:'js', tsx:'js', json:'js', node:'js',
  python:'py', python3:'py', csharp:'cs', 'c#':'cs', java:'cs', cpp:'cs', c:'cs', go:'cs', rust:'cs',
  bash:'sh', shell:'sh', zsh:'sh', console:'sh', powershell:'sh', ps1:'sh', yaml:'sh', yml:'sh' };
function chatHighlight(code, lang){
  try{
    const fam=HL_KEYWORDS[lang]?lang:(HL_ALIAS[lang]||null);
    const kw=new Set(((fam&&HL_KEYWORDS[fam])||'').split(' ').filter(Boolean));
    const hashComments=fam==='py'||fam==='sh'||fam===null;
    const slashComments=fam==='js'||fam==='cs'||fam===null;
    // One scanner, char by char: no regex backtracking surprises on model-generated code.
    let out='', i=0; const s=String(code);
    const push=(cls,text)=>{ out+=cls?'<span class="'+cls+'">'+escapeHtml(text)+'</span>':escapeHtml(text); };
    while(i<s.length){
      const c=s[i];
      if(slashComments&&c==='/'&&s[i+1]==='/'){ let j=s.indexOf('\n',i); if(j<0)j=s.length; push('hl-c',s.slice(i,j)); i=j; continue; }
      if(slashComments&&c==='/'&&s[i+1]==='*'){ let j=s.indexOf('*/',i+2); j=j<0?s.length:j+2; push('hl-c',s.slice(i,j)); i=j; continue; }
      if(hashComments&&c==='#'){ let j=s.indexOf('\n',i); if(j<0)j=s.length; push('hl-c',s.slice(i,j)); i=j; continue; }
      if(c==='"'||c==="'"||c==='`'){
        let j=i+1; while(j<s.length&&(s[j]!==c||s[j-1]==='\\')){ j++; }
        push('hl-s',s.slice(i,Math.min(j+1,s.length))); i=j+1; continue;
      }
      if(/[0-9]/.test(c)&&!/[A-Za-z0-9_]/.test(s[i-1]||'')){
        let j=i; while(j<s.length&&/[0-9._xA-Fa-f]/.test(s[j])) j++;
        push('hl-n',s.slice(i,j)); i=j; continue;
      }
      if(/[A-Za-z_]/.test(c)){
        let j=i; while(j<s.length&&/[A-Za-z0-9_]/.test(s[j])) j++;
        const word=s.slice(i,j);
        push(kw.has(fam==='sql'?word.toLowerCase():word)?'hl-k':null, word); i=j; continue;
      }
      push(null,c); i++;
    }
    return out;
  }catch(e){ return escapeHtml(code); }
}

/* v0.3.8.46: a turn's recorded time, briefly. Today shows the clock; older shows the date too.
 * Returns '' for anything unparseable — a wrong time is worse than no time. */
function chatTurnTime(iso){
  if(!iso) return '';
  const d=new Date(iso); if(isNaN(d.getTime())) return '';
  const now=new Date();
  const sameDay=d.getFullYear()===now.getFullYear()&&d.getMonth()===now.getMonth()&&d.getDate()===now.getDate();
  const hm=d.toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'});
  return sameDay?hm:d.toLocaleDateString([],{month:'short',day:'numeric'})+' '+hm;
}

let chatFingerprint='';        // what is currently on screen, so a poll that changes nothing costs nothing
let chatTurnContents=[];       // per-turn raw text for the copy action (kept out of the DOM)
async function chatOpen(id){
  if(!id) return;
  chatActiveId=id;
  const thread=document.getElementById('chat-thread'); if(!thread) return;
  try{
    const r=await api('/conversations/'+encodeURIComponent(id));
    if(!r||!r.success){ chatFingerprint=''; thread.innerHTML=`<div class="hud-state err">${escapeHtml((r&&r.message)||'Could not open that conversation.')}</div>`; return; }
    const d=r.data||{}, turns=d.turns||[];

    // Re-render only when something actually changed. The chat poll calls this every few seconds;
    // rebuilding an unchanged thread would destroy selection and scroll for nothing.
    const print=id+':'+turns.length+':'+(turns.length?String(turns[turns.length-1].content||'').length:0)
      +':'+String(d.doing||'')+':'+(d.needs_operator?1:0)+':'+(d.cancelled?1:0);
    const changed = print!==chatFingerprint;
    chatFingerprint=print;

    if(changed){
      // Preserve the reading position: follow new content only when the reader is already at the
      // bottom; someone reading history stays exactly where they were.
      const nearBottom = thread.scrollTop + thread.clientHeight >= thread.scrollHeight - 48;
      const keepTop = thread.scrollTop;

      chatTurnContents = turns.map(t=>String(t.content||''));
      // Only the operator's own words are theirs; everything else is the colony speaking,
      // whichever ant or provider produced it.
      thread.innerHTML = turns.length
        ? turns.map((t,i)=>{
            const mine=String(t.role||'').toLowerCase()==='user';
            // v0.3.8.46: each turn carries its recorded time — the stored created_at, rendered
            // local. Shown inline, small; the full ISO instant is in the title for auditing.
            const when=chatTurnTime(t.created_at);
            // v0.3.8.46: token usage, only when the provider reported it. No number is shown for
            // an unreported count — absence and zero are different facts.
            const tok=(t.completion_tokens!=null||t.prompt_tokens!=null)
              ? `<span class="chat-tok" title="prompt ${t.prompt_tokens??'?'} · completion ${t.completion_tokens??'?'} tokens">${(t.prompt_tokens??0)+(t.completion_tokens??0)} tok</span>`
              : '';
            // v0.3.8.47: ✎ on your own messages — puts the text back in the composer to revise
            // and RESEND as a new turn. The record is an audit trail; editing never rewrites it.
            //
            // BUILT WHITESPACE-TIGHT on purpose (found live, "the bubbles look like shit"): the
            // bubble renders with pre-wrap, so any newline or indentation inside this template
            // becomes literal blank space in every message. One string, no stray characters.
            // v0.3.8.49 (§3): a long colony turn is collapsed to a compact preview with a "Show full
            // response" toggle — Chat should read like an assistant, not dump raw mission state. The
            // operator's own messages are never collapsed. "Long" is measured on the raw text so
            // the decision is stable regardless of how it renders.
            const raw=String(t.content||'');
            const isLong=!mine && (raw.length>900 || (raw.match(/\n/g)||[]).length>16);
            const body=isLong
              ? `<div class="chat-body clamp" data-body="${i}">${chatRenderContent(t.content)}</div>`
                + `<button class="chat-expand" data-expand="${i}" aria-expanded="false">Show full response</button>`
              : chatRenderContent(t.content);
            return `<div class="chat-turn ${mine?'user':'colony'}">`
              + `<span class="who"><span class="who-name">${mine?'You':escapeHtml(t.model||t.provider||'Colony')}</span>`
              + (when?`<span class="chat-when" title="${escapeHtml(String(t.created_at||''))}">${escapeHtml(when)}</span>`:'')
              + tok
              + (mine?`<button class="chat-copy chat-edit" data-i="${i}" title="Edit and resend as a new message" aria-label="Edit and resend">✎</button>`:'')
              + `<button class="chat-copy" data-i="${i}" title="Copy message" aria-label="Copy message">⧉</button></span>`
              + body
              + ((t.attachments&&t.attachments.length)?`<div class="turn-attach">${t.attachments.map(a=>`${GLYPH.file} ${escapeHtml(a.filename)}`).join(' · ')}</div>`:'')
              + `</div>`;
          }).join('')
        : '<div class="hud-state">No messages yet.</div>';
      // CSP is script-src 'self' with no unsafe-inline, so handlers are bound, never inlined.
      thread.querySelectorAll('.chat-edit').forEach(b=>b.addEventListener('click',()=>{
        const inp=document.getElementById('chat-input');
        if(inp){ inp.value=chatTurnContents[+b.dataset.i]||''; inp.focus(); }
      }));
      thread.querySelectorAll('.chat-copy:not(.chat-edit)').forEach(b=>b.addEventListener('click',async ()=>{
        try{ await navigator.clipboard.writeText(chatTurnContents[+b.dataset.i]||''); b.textContent='✓'; setTimeout(()=>{ b.textContent='⧉'; },1200); }
        catch{ b.textContent='✕'; setTimeout(()=>{ b.textContent='⧉'; },1200); }
      }));
      // v0.3.8.49 (§3): expand/collapse a long colony response. CSP is script-src 'self', so the
      // handler is bound here, never inlined.
      thread.querySelectorAll('.chat-expand').forEach(b=>b.addEventListener('click',()=>{
        const body=thread.querySelector('.chat-body[data-body="'+b.dataset.expand+'"]');
        if(!body) return;
        const clamped=body.classList.toggle('clamp');
        b.setAttribute('aria-expanded',clamped?'false':'true');
        b.textContent=clamped?'Show full response':'Show less';
      }));
      if(nearBottom) thread.scrollTop = thread.scrollHeight;
      else thread.scrollTop = keepTop;
    }

    // v0.3.8.40 — the escalation gate, IN the thread.
    //
    // `needs_operator` means the colony has stopped and is waiting for a person: it wants to do
    // something its approval policy will not let it do unasked. That is the single most important
    // thing the chat surface can say, and until now it said nothing — the prompt appeared only in
    // the old Conversations widget, which is hidden on the default dashboard. An operator working
    // in Chat would have waited on a colony that was waiting on them.
    //
    // Rendered after the turns, where the next thing to happen belongs, and using the same
    // convApprove/convCancel calls the old panel uses so there is one approval path rather than two.
    // Appended only on a changed render — the unchanged path leaves the existing box alone.
    if(changed && d.needs_operator){
      const waiting = d.waiting_on || [];
      const box = document.createElement('div');
      box.className = 'chat-approve';
      // v0.3.8.46: when the colony is asking to start a mission, the operator can see the task
      // plan BEFORE saying yes. POST /missions/plan is a dry run — same planner, no mission
      // created — and it lost its only surface when the old Mission Composer retired. This is
      // where it belongs now: at the moment of the decision it informs.
      const canPreview = waiting.includes('start_mission');
      box.innerHTML =
        `<div class="chat-approve-hd">The colony is waiting for you</div>`
        + `<div class="chat-approve-what">It wants to ${escapeHtml(waiting.join(', ') || 'do real work')}. `
        + `Nothing happens until you say so.</div>`
        + `<div class="chat-plan-preview" hidden></div>`
        + `<div class="chat-approve-actions">`
        + waiting.map(a=>`<button class="btn btn-primary chat-approve-yes" data-act="${escapeHtml(a)}">Allow ${escapeHtml(a)}</button>`).join('')
        + (canPreview?`<button class="btn btn-ghost chat-plan-btn">${GLYPH.list} Preview the plan</button>`:'')
        + `<button class="btn chat-approve-no">Stop this</button></div>`;
      thread.appendChild(box);
      // Bound after render, never as an inline handler: the console's CSP is script-src 'self'
      // with no unsafe-inline, so an onclick attribute here is dropped silently by the browser.
      box.querySelectorAll('.chat-approve-yes').forEach(b=>
        b.addEventListener('click', async ()=>{ await convApprove(id, b.dataset.act); chatOpen(id); }));
      box.querySelector('.chat-approve-no')
        ?.addEventListener('click', async ()=>{ await convCancel(id); chatOpen(id); });
      box.querySelector('.chat-plan-btn')
        ?.addEventListener('click', ()=>chatPlanPreview(box, turns));
      thread.scrollTop = thread.scrollHeight;
    }

    setEl('chat-title', chatTitles[id] || d.title || 'Conversation');
    // v0.3.8.52 (field report): with the files pane open nothing said WHICH project the work
    // belongs to. The project wears the second line under the chat name, always visible while a
    // conversation is open, and clicks through to its project page.
    const projLine=document.getElementById('chat-title-proj');
    if(projLine){
      if(d.project_name){
        projLine.hidden=false;
        projLine.innerHTML=`${GLYPH.folder} ${escapeHtml(d.project_name)}`;
        projLine.title='Project: '+d.project_name+' — open its page';
        projLine.onclick=()=>go('/projects/'+encodeURIComponent(d.project_id||''));
      }else projLine.hidden=true;
    }
    // v0.3.8.48: the selector shows the conversation's EFFECTIVE policy, and says who set it.
    const polSel=document.getElementById('chat-policy');
    if(polSel){ polSel.value=d.policy||'ask'; polSel.dataset.current=d.policy||'ask'; }
    chatPendingPolicy=null;   // an OPEN conversation's own gate governs; the held choice expires
    // Inline change cards: this conversation's proposed patches, reviewed where the work happened.
    chatRenderPatches(d, thread);
    chatOpenAfterRender(d);
  }catch(e){ pollWarnOnce('chatOpen', e); }
}

/* v0.3.8.46 — the dry-run plan, shown where the yes/no is made.
 *
 * The goal previewed is the LAST OPERATOR MESSAGE, which is what the runner escalates with; the
 * card says so rather than implying the plan is a promise. Steps come back with the ant that
 * would take each one and — because the preview runs real admission — which steps dispatch would
 * refuse, so a plan that cannot run looks broken here instead of after approval. */
async function chatPlanPreview(box, turns){
  const host=box.querySelector('.chat-plan-preview'); if(!host) return;
  if(!host.hidden){ host.hidden=true; return; }   // second press folds it away
  const lastUser=[...(turns||[])].reverse().find(t=>t.role==='user');
  if(!lastUser){ host.hidden=false; host.innerHTML='<div class="hud-state">Nothing to plan — no operator message found.</div>'; return; }
  host.hidden=false;
  host.innerHTML='<div class="hud-state">Asking the planner… (a routed model does real planning here; this can take a minute)</div>';
  const r=await api('/missions/plan','POST',{goal:lastUser.content},120000);
  if(!r||!r.success){ host.innerHTML=`<div class="hud-state err">${escapeHtml((r&&r.message)||'The planner did not answer.')}</div>`; return; }
  const p=r.data||{}, tasks=p.tasks||[];
  const warns=(p.constraint_warnings||[]).map(w=>`<div class="chat-plan-warn">⚠ ${escapeHtml(w)}</div>`).join('');
  host.innerHTML =
    `<div class="chat-plan-hd">What would run — a dry run for your last message, not a promise</div>`
    + warns
    + tasks.map(t=>`<div class="chat-plan-task${t.blocked?' blocked':''}">`
        + `<span class="chat-plan-n">${t.index}</span> ${escapeHtml(t.title||'')}`
        + ` <span class="chat-plan-ant">${escapeHtml(t.display||t.ant||'')}</span>`
        + (t.depends_on&&t.depends_on.length?` <span class="chat-plan-dep">after ${t.depends_on.join(', ')}</span>`:'')
        + (t.blocked?`<div class="chat-plan-blocked">would be refused: ${escapeHtml(t.blocked_reason||'admission refused')}</div>`:'')
        + `</div>`).join('')
    + (tasks.length?'':'<div class="hud-state">The planner produced no steps for this goal.</div>');
}

function chatOpenAfterRender(d){
  const id=chatActiveId;
    // v0.3.8.42: `cancelled` is a field, and `doing` is now a truthful present-tense vocabulary.
    // v0.3.8.51 (field report): a running mission reads as the COLONY'S OWN WORDS — "Colony is
    // working…" (and "Colony is building…" while the coder/builder holds the running task),
    // resolved from the mission report rather than shown as a raw id.
    chatSetState(d.needs_operator ? 'Waiting on you'
               : d.cancelled ? 'Stopped — start a new conversation to continue'
               : (d.doing||''));
    if(!d.needs_operator && !d.cancelled && (d.doing||'').startsWith('running mission'))
      chatActivityLine((d.doing.match(/running mission (\S+)/)||[])[1]);
    // The Stop control exists exactly while there is something stoppable — a running mission.
    // Chat replies are synchronous inside the send; "unanswered" is a failure state, not work.
    const stopBtn=document.getElementById('chat-stop');
    if(stopBtn) stopBtn.hidden = !(d.doing||'').startsWith('running mission') || !!d.needs_operator || !!d.cancelled;
    if(chatColonyOpen) chatColonyMissionNote();   // keep the layer's mission line current
    document.querySelectorAll('.chat-conv').forEach(el=>
      el.classList.toggle('active', el.dataset.id===id));
}

let chatLastSent='';   // v0.3.8.42 (§13): Up-arrow in an empty composer recalls this
let chatStreamAbort=null;   // v0.3.8.44: the in-flight streamed turn, abortable from the composer

/* v0.3.8.44 — the streamed turn, consumed. The server answers text/event-stream: delta frames as
 * the provider produces them, the outcome as the terminal done event. Deltas render into a
 * provisional bubble through the SAME escape-first chatRenderContent every recorded turn uses —
 * streaming changes when text appears, never what is allowed to appear. On done, the provisional
 * bubble yields to the recorded turn (fingerprint reset + chatOpen), so what remains on screen is
 * exactly what the database holds. A provider that cannot stream simply produces no deltas and
 * the done frame carries the whole reply — one code path, no fake trickle. */
let chatStreamLiveEl=null;   // the provisional bubble, held as a REFERENCE — not an id lookup
async function chatConsumeStream(response){
  const thread=document.getElementById('chat-thread');
  let raw='';
  const ensureBubble=()=>{
    if(chatStreamLiveEl) return;
    chatStreamLiveEl=document.createElement('div');
    chatStreamLiveEl.className='chat-turn colony';
    chatStreamLiveEl.innerHTML='<span class="who">Colony</span><span class="chat-stream-text"></span>';
    thread?.appendChild(chatStreamLiveEl);
  };
  const reader=response.body.getReader();
  const decoder=new TextDecoder();
  let buffer='', outcome=null;
  for(;;){
    const {done,value}=await reader.read();
    if(done) break;
    buffer+=decoder.decode(value,{stream:true});
    let sep;
    while((sep=buffer.indexOf('\n\n'))>=0){
      const frame=buffer.slice(0,sep); buffer=buffer.slice(sep+2);
      const ev=/^event: (.+)$/m.exec(frame)?.[1];
      const data=/^data: (.+)$/m.exec(frame)?.[1];
      if(!ev||data===undefined) continue;
      if(ev==='delta'){
        raw+=JSON.parse(data);
        ensureBubble();
        const nearBottom=thread.scrollTop+thread.clientHeight>=thread.scrollHeight-48;
        chatStreamLiveEl.querySelector('.chat-stream-text').innerHTML=chatRenderContent(raw);
        if(nearBottom) thread.scrollTop=thread.scrollHeight;
      }else if(ev==='done'){
        outcome=JSON.parse(data);
      }
    }
  }
  return outcome;
}

/** @param mode 'chat' (default) or 'mission' — the same two modes the runtime has. A mission
 *  request goes through the escalation gate; under Ask the approval renders IN the thread. */
/* v0.3.8.47 — attachments. Files staged on the composer (📎 or drag-and-drop), sent WITH the
 * message, recorded against that turn. Text only and capped, mirroring the server's own rule —
 * refusing here just says it sooner and kinder. */
let chatStagedFiles=[];
function chatRenderStaged(){
  const host=document.getElementById('chat-attach-chips'); if(!host) return;
  host.innerHTML=chatStagedFiles.map((f,i)=>`<span class="attach-chip">${GLYPH.file} ${escapeHtml(f.filename)}`
    +` <span class="attach-size">${f.content.length>1024?Math.round(f.content.length/1024)+' KB':f.content.length+' B'}</span>`
    +`<button class="attach-x" data-x="${i}" title="Remove" aria-label="Remove attachment">✕</button></span>`).join('');
  host.querySelectorAll('.attach-x').forEach(b=>b.addEventListener('click',()=>{
    chatStagedFiles.splice(+b.dataset.x,1); chatRenderStaged();
  }));
}
async function chatStageFiles(fileList){
  for(const file of fileList||[]){
    if(chatStagedFiles.length>=8){ chatSetState('At most 8 attachments per message.'); break; }
    if(file.size>262144){ chatSetState(`"${file.name}" is too large — 256 KB of text max.`); continue; }
    const content=await file.text();
    if(content.includes('\0')){ chatSetState(`"${file.name}" looks binary — attach text files.`); continue; }
    chatStagedFiles.push({filename:file.name, content});
  }
  chatRenderStaged();
}
document.getElementById('chat-attach')?.addEventListener('click', ()=>document.getElementById('chat-attach-file')?.click());
document.getElementById('chat-attach-file')?.addEventListener('change', e=>{ chatStageFiles(e.target.files); e.target.value=''; });
// Drag-and-drop onto the composer area.
const _composer=document.querySelector('.chat-composer');
if(_composer){
  ['dragover','dragenter'].forEach(ev=>_composer.addEventListener(ev, e=>{ e.preventDefault(); _composer.classList.add('dragging'); }));
  ['dragleave','drop'].forEach(ev=>_composer.addEventListener(ev, e=>{ e.preventDefault(); _composer.classList.remove('dragging'); }));
  _composer.addEventListener('drop', e=>{ if(e.dataTransfer&&e.dataTransfer.files.length) chatStageFiles(e.dataTransfer.files); });
}

/* v0.3.8.48 — the project picker. Shown when a conversation is about to be born outside any
 * project: pick an active project or name a new one. Returns the project id, or null on cancel.
 * Built as a real modal (CSP-safe bound handlers, focus into the list, Escape cancels). */
let chatPendingProjectId=null;
function chatPickProject(){
  return new Promise(async resolve=>{
    const r=await api('/projects');
    const projects=((r&&r.data&&r.data.projects)||[]).filter(p=>!p.archived);
    const ov=document.createElement('div');
    ov.className='ui-modal-ov';
    ov.innerHTML=`<div class="ui-modal" role="dialog" aria-label="Choose a project" style="max-width:440px;width:92%;">
      <h3 style="margin:0 0 4px;font-size:13px;">Where does this conversation live?</h3>
      <div class="sub" style="margin-bottom:10px;">Every conversation belongs to a project — its purpose and working directory travel with your messages.</div>
      <div class="pick-list" style="max-height:220px;overflow-y:auto;margin-bottom:10px;">
        ${projects.map(p=>`<button class="pick-row" data-pick="${escapeHtml(p.id)}">
          <b>${escapeHtml(p.name||'Untitled')}</b>
          <span>${p.conversations} conversation(s)</span></button>`).join('')
          ||'<div class="hud-state">No projects yet — name your first one below.</div>'}
      </div>
      <div style="display:flex;gap:8px;">
        <input class="provider-input" data-newname placeholder="Or start a new project…" style="flex:1" maxlength="120">
        <button class="btn btn-primary" data-create>Create</button>
        <button class="btn btn-ghost" data-cancel>Cancel</button>
      </div></div>`;
    const done=v=>{ ov.remove(); document.removeEventListener('keydown',esc,true); resolve(v); };
    const esc=e=>{ if(e.key==='Escape'){ e.stopPropagation(); done(null); } };
    document.addEventListener('keydown',esc,true);
    ov.addEventListener('click',e=>{ if(e.target===ov) done(null); });
    ov.querySelectorAll('.pick-row').forEach(b=>b.addEventListener('click',()=>done(b.dataset.pick)));
    ov.querySelector('[data-cancel]').addEventListener('click',()=>done(null));
    ov.querySelector('[data-create]').addEventListener('click',async ()=>{
      const name=ov.querySelector('[data-newname]').value.trim();
      if(!name) return;
      const c=await api('/projects','POST',{name});
      if(c&&c.success&&c.data) done(c.data.id);
    });
    document.body.appendChild(ov);
    (ov.querySelector('.pick-row')||ov.querySelector('[data-newname]')).focus();
  });
}

async function chatSend(mode){
  mode = mode==='mission' ? 'mission' : 'chat';
  const el=document.getElementById('chat-input');
  const msg=(el&&el.value||'').trim();
  if(!msg) return;
  if(chatStreamAbort) return;   // one turn at a time; the composer shows ■ while one is in flight
  chatLastSent=msg;
  if(el){ el.value=''; el.style.height=''; }
  chatSetState(mode==='mission'?'Asking to start work…':'Sending…');
  // v0.3.8.42: the refusal summary must OUTLIVE the refresh. chatOpen() recomputes the state line
  // from the conversation detail, so setting the refusal before refreshing meant the one message
  // that explains what happened was overwritten milliseconds later.
  let note='';
  try{
    if(!chatActiveId){
      // v0.3.8.48: a conversation lives in a project — chosen, never invented. The picker
      // resolves to a project id (existing or newly named by the operator) before anything is
      // created; the message stays in the composer if they cancel.
      const pid=chatPendingProjectId||await chatPickProject();
      if(!pid){ if(el) el.value=msg; chatSetState('Pick a project to start this conversation in.'); return; }
      chatPendingProjectId=null;
      const c=await api('/conversations','POST',{ title: msg.slice(0,48), project_id: pid,
        // v0.3.8.53: the gate chosen before this conversation existed rides its creation.
        policy: chatPendingPolicy||undefined });
      if(!c||!c.success){ if(el) el.value=msg; chatSetState((c&&c.message)||'Could not start.'); return; }
      chatActiveId=c.data.id; chatPendingPolicy=null;
      chatComposingNew=false;   // the new conversation exists; auto-open may resume
    }
    if(mode==='chat'){
      // Streamed. Aborting the fetch aborts the model call server-side (RequestAborted is bound
      // into ModelCallScope) — cancellation reaches the provider, not merely the animation.
      chatStreamAbort=new AbortController();
      chatSetComposerStreaming(true);
      // v0.3.8.51 (field report): the 40-second silence that made the operator resend. The state
      // line now SAYS the colony is thinking from the first instant; the first streamed token
      // flips it, and mission activity has its own words (see chatActivityLine).
      chatSetState('Colony is thinking…');
      const response=await fetch(url('/conversations/'+encodeURIComponent(chatActiveId)+'/turns'),{
        method:'POST',
        headers:{'Authorization':'Bearer '+TOKEN,'Content-Type':'application/json'},
        body:JSON.stringify({message:msg, mode:mode, stream:true, attachments:chatStagedFiles}),
        signal:chatStreamAbort.signal,
      });
      if((response.headers.get('content-type')||'').includes('text/event-stream')){
        const outcome=await chatConsumeStream(response);
        if(outcome&&outcome.started===false) note=outcome.summary||'Refused';
      }else{
        const r=await response.json().catch(()=>null);
        // v0.3.8.52 (third field round): a refused turn keeps the operator's message and NAMES
        // the remedy. (v0.3.8.55: the working-directory refusal is gone — a pathless project
        // stands in the ANTHILL source by default now.)
        if(r&&r.success===false){
          note=r.message||'Refused';
          if(el) el.value=msg;
        }
        else if(r&&r.data&&r.data.started===false) note=r.data.summary||'Refused';
      }
    }else{
      const r=await api('/conversations/'+encodeURIComponent(chatActiveId)+'/turns','POST',{ message:msg, mode:mode, attachments:chatStagedFiles });
      if(r&&r.success===false){
        note=r.message||'Refused';
        if(el) el.value=msg;
      }
      else if(r&&r.data&&r.data.started===false) note=r.data.summary||'Refused';
    }
  }catch(e){
    note = (e&&e.name==='AbortError') ? 'Stopped.' : 'Could not send: '+(e.message||'');
  }
  finally{
    chatStreamAbort=null; chatSetComposerStreaming(false);
    chatStagedFiles=[]; chatRenderStaged();
    chatStreamLiveEl?.remove(); chatStreamLiveEl=null;
    chatFingerprint='';   // the provisional bubble yields to the recorded turn
    apiCacheBust('/conversations'); loadChat();
    await chatOpen(chatActiveId);
    if(note) chatSetState(note);
  }
}

/* While a streamed turn is in flight the send button is ■: pressing it aborts the fetch, which
 * aborts the model call. The same control, the two things it can truthfully do. */
function chatSetComposerStreaming(streaming){
  const b=document.getElementById('chat-send'); if(!b) return;
  if(streaming){ b.textContent='■'; b.title='Stop this answer'; b.classList.add('chat-streaming'); }
  else{ b.textContent='▶'; b.title='Send (Enter)'; b.classList.remove('chat-streaming'); }
}

/* v0.3.8.43 — Chat + Colony mode: the canonical topology as a full-page layer BEHIND the
 * conversation (SOW §4), the chat floating above it as a frosted panel. One renderer: the same
 * #colony-canvas-area the Colony page owns is re-parented into the layer (topologyMountTo), so
 * there is no second canvas, no second render loop, and no second subscription — toggling moves a
 * node, it does not build one. The camera (camX/camY/camZ) travels with the node, so the view an
 * operator framed on the Colony page is the view they get here. Closing hands the canvas back to
 * its home slot; refreshTopologyAwake() then measures 0×0 in the hidden layer and the loop stops
 * drawing it. Chat state (draft, scroll, selection) is untouched in both directions because
 * nothing in the conversation subtree is rebuilt. */
function chatToggleColony(open){
  const layer=document.getElementById('chat-colony-layer'); if(!layer) return;
  chatColonyOpen = open===undefined ? !chatColonyOpen : !!open;
  layer.hidden = !chatColonyOpen;
  // v0.3.8.51: the split stays while EITHER right-hand pane (colony or files) is open.
  document.getElementById('page-chat')?.classList.toggle('colony-open', chatColonyOpen||chatFilesOpen);
  const btn=document.getElementById('chat-colony-toggle');
  if(btn) btn.setAttribute('aria-pressed', chatColonyOpen?'true':'false');
  if(typeof topologyMountTo==='function') topologyMountTo(chatColonyOpen?'chat':'home');
  // Topology failure must not break Chat: if the canonical canvas is not in the document at all,
  // the layer says so plainly instead of presenting an empty black region as a working map.
  if(chatColonyOpen){
    const mount=document.getElementById('chat-colony-mount');
    if(mount&&!document.getElementById('colony-canvas-area'))
      mount.innerHTML='<div class="hud-state err" style="margin:24px auto;max-width:420px">The colony view could not load. The conversation is unaffected — try Open full Colony, or reload the console.</div>';
    chatColonyMissionNote();
  }
}

/* The truthful mission line in the layer's bar. The conversation detail carries mission_ids
 * (v3.7.0); the topology itself renders the colony's real current activity from /graph. This
 * line says which of three states is true rather than pretending the map is always "the
 * conversation's mission": linked-and-running (the activity IS this mission), linked-and-settled,
 * or not linked (the map shows the colony as it is — including a legitimate idle).
 *
 * Found by the live walkthrough: this read /jobs, and conversation-ESCALATED missions run through
 * the Queen directly — the jobs registry only holds POST /missions submissions — so the note said
 * "not in the recent runs list" about exactly the missions chat creates. The conversation's own
 * state is the first authority, and mission HISTORY is the settled record. */
async function chatColonyMissionNote(){
  const el=document.getElementById('chat-colony-mission'); if(el===null) return;
  if(!chatActiveId){ el.textContent='No mission linked — the map shows the colony as it is.'; return; }
  try{
    const r=await api('/conversations/'+encodeURIComponent(chatActiveId));
    const d=(r&&r.success&&r.data)||{};
    const ids=d.mission_ids||[];
    if(!ids.length){ el.textContent='No mission linked — the map shows the colony as it is.'; return; }
    const mid=String(ids[ids.length-1]);
    if((d.doing||'').startsWith('running mission')){
      el.textContent='This conversation’s mission is running — the activity below is it.';
      return;
    }
    const hist=(await api('/missions/json?limit=40'));
    const m=((hist&&hist.success&&hist.data)||[]).find(x=>String(x.id)===mid);
    el.textContent = m
      ? `This conversation’s last mission: ${m.status}${m.success_score!=null?' · score '+m.success_score:''}.`
      : 'Linked mission '+mid.substring(0,8)+'… — not in recent history.';
  }catch{ el.textContent=''; }
}

document.getElementById('chat-send')?.addEventListener('click', ()=>{
  if(chatStreamAbort){ chatStreamAbort.abort(); return; }   // ■ while streaming: abort is the action
  chatSend();
});
// v0.3.8.51: the ⚒ button retired — one send path; the colony proposes missions itself and the
// approval policy governs them. (Handler removed with the button, noted so its absence is a fact.)
// The selector wears a "Gates" LABEL (operator correction: the dropdown IS the gates — a sibling
// button also named Gates was one gates too many). Directory gates live in the project Settings.
let chatStopInFlight=false;
document.getElementById('chat-stop')?.addEventListener('click', async ()=>{
  if(chatStopInFlight||!chatActiveId) return;
  chatStopInFlight=true;
  const b=document.getElementById('chat-stop'); if(b){ b.disabled=true; b.textContent='Stopping…'; }
  try{ await convCancel(chatActiveId); }
  finally{
    chatStopInFlight=false;
    if(b){ b.disabled=false; b.textContent='■ Stop'; }
    chatOpen(chatActiveId);
  }
});
document.getElementById('chat-new')?.addEventListener('click', ()=>{
  chatActiveId=null;
  chatComposingNew=true;
  chatFingerprint='';   // the welcome screen replaced the thread outside chatOpen's knowledge
  setEl('chat-title','New conversation'); chatSetState('');
  // No conversation, no project line — it returns with the next chatOpen.
  const npl=document.getElementById('chat-title-proj'); if(npl) npl.hidden=true;
  const thread=document.getElementById('chat-thread');
  if(thread) thread.innerHTML='<div class="chat-welcome"><h2>What would you like done?</h2>'
    + '<p>Describe it in your own words. The colony plans the work, carries it out, and shows you '
    + 'every step — you approve anything that changes real files.</p></div>';
  document.getElementById('chat-input')?.focus();
  loadChat();
});
// v0.3.8.47: import — the inverse of export. Reads a JSON file, sends it whole, opens the result.
// The file is parsed HERE so a malformed one fails with a message instead of a 400 round-trip.
document.getElementById('chat-import')?.addEventListener('click', ()=>document.getElementById('chat-import-file')?.click());
document.getElementById('chat-import-file')?.addEventListener('change', async e=>{
  const file=e.target.files&&e.target.files[0]; e.target.value=''; if(!file) return;
  try{
    const parsed=JSON.parse(await file.text());
    const turns=Array.isArray(parsed.turns)?parsed.turns:Array.isArray(parsed)?parsed:null;
    if(!turns||!turns.length){ chatSetState('Import: no turns found in that file.'); return; }
    const r=await api('/conversations/import','POST',{title:parsed.title||file.name.replace(/\.json$/i,''), turns},30000);
    if(r&&r.success&&r.data){ chatSetState(r.message||'Imported.'); chatComposingNew=false; chatOpen(r.data.id); loadChat(); }
    else chatSetState((r&&r.message)||'Import failed.');
  }catch(err){ chatSetState('Import: that file is not valid JSON.'); }
});

// v0.3.8.46: the rail search box — debounced, and Escape clears it.
let chatSearchTimer=null;
document.getElementById('chat-search')?.addEventListener('input', e=>{
  clearTimeout(chatSearchTimer);
  chatSearchTimer=setTimeout(()=>{ chatSearchQuery=e.target.value.trim(); loadChat(); }, 250);
});
document.getElementById('chat-search')?.addEventListener('keydown', e=>{
  if(e.key==='Escape'&&e.target.value){ e.stopPropagation(); e.target.value=''; chatSearchQuery=''; loadChat(); }
});

// v0.3.8.46: export — GET /conversations/{id}/export needs the auth header, so the download goes
// through fetch + blob rather than a bare link. The filename comes from the server's own header.
document.getElementById('chat-export')?.addEventListener('click', async ()=>{
  if(!chatActiveId){ chatSetState('Open a conversation first.'); return; }
  try{
    const resp=await fetch(url('/conversations/'+encodeURIComponent(chatActiveId)+'/export'),
      {headers:{'Authorization':'Bearer '+TOKEN}});
    if(!resp.ok){ chatSetState('Export failed ('+resp.status+').'); return; }
    const blob=await resp.blob();
    const a=document.createElement('a');
    a.href=URL.createObjectURL(blob);
    a.download='conversation-'+chatActiveId+'.md';
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(()=>URL.revokeObjectURL(a.href), 5000);
  }catch(e){ chatSetState('Export failed: '+(e&&e.message||e)); }
});

// v0.3.8.48 — the approval gate on the conversation. Skip-all is a real decision: confirmed in
// words. The refusal path snaps the selector back; nothing changes silently.
// v0.3.8.53 (field report): the gate is settable BEFORE any conversation exists — the choice is
// HELD and becomes the policy of the conversation the next message creates, attributed at
// creation exactly as the server has always required. No more "open a conversation first".
document.getElementById('chat-policy')?.addEventListener('change', async e=>{
  const sel=e.target, want=sel.value, was=sel.dataset.current||'ask';
  if(want==='bypass' && !confirm('Skip all approvals for this conversation?\n\nThe colony will act '
      +'without asking you first. Authentication, workspace boundaries, capability gates and '
      +'verification requirements still apply — this skips prompts, not security.')){
    sel.value=was; return;
  }
  if(!chatActiveId){
    chatPendingPolicy=want; sel.dataset.current=want;
    chatSetState('Approval mode set — it will apply to the conversation your next message starts.');
    return;
  }
  sel.disabled=true;
  const r=await api('/conversations/'+encodeURIComponent(chatActiveId)+'/policy','POST',{policy:want});
  sel.disabled=false;
  if(r&&r.success){ sel.dataset.current=want; chatSetState(r.message||''); }
  else{ sel.value=was; chatSetState((r&&r.message)||'Could not change the approval mode.'); }
});

/* v0.3.8.48 — the change, reviewed where the work happened. Every proposed patch from this
 * conversation's missions renders as a card IN the thread: path, risk, verification, and the
 * actions. Approve & apply runs the two backend transitions in sequence, keeping both audit
 * records; nothing navigates away. */
let chatPatchFingerprint='';
async function chatRenderPatches(d, thread){
  const missions=d.mission_ids||[];
  if(!missions.length){ chatPatchFingerprint=''; return; }
  let rows=[];
  for(const mid of missions.slice(-3)){
    const r=await api('/patches?mission_id='+encodeURIComponent(mid)+'&limit=20');
    if(r&&r.success&&Array.isArray(r.data)) rows.push(...r.data);
  }
  // v0.3.8.56 (field report): under Skip-all, the chat asks NOTHING. Bypass is the operator
  // saying "don't prompt me" — verified patches auto-apply, and a proposal still pending is one
  // the pipeline has not verified yet (or refused), which is a status, not a question. Pending
  // cards collapse to one status line; applied/rejected/reverted cards still render as record.
  const policy=document.getElementById('chat-policy')?.dataset.current||'ask';
  let pendingHeldBack=0;
  if(policy==='bypass'){
    pendingHeldBack=rows.filter(p=>p.status==='proposed').length;
    rows=rows.filter(p=>p.status!=='proposed');
  }
  const print=policy+'|'+rows.map(p=>p.id+':'+p.status).join('|')+'|held:'+pendingHeldBack;
  if(print===chatPatchFingerprint) return;
  chatPatchFingerprint=print;
  thread.querySelectorAll('.patch-card').forEach(el=>el.remove());
  if(pendingHeldBack>0){
    const note=document.createElement('div');
    note.className='patch-card';
    note.innerHTML=`<div class="patch-hd"><b>${pendingHeldBack} proposal(s) awaiting colony verification</b></div>
      <div class="patch-path" style="color:var(--dim);">Skip-all applies changes once they verify — nothing to answer here. Full detail lives in Changes.</div>`;
    thread.appendChild(note);
  }
  for(const p of rows){
    const card=document.createElement('div');
    card.className='patch-card'+(p.status==='applied'?' applied':p.status==='rejected'?' rejected':'');
    card.innerHTML=`<div class="patch-hd"><b>Proposed change</b>
        <span class="sch-badge">${escapeHtml(p.status||'')}</span>
        ${p.risk?`<span class="sch-badge">risk: ${escapeHtml(p.risk)}</span>`:''}
        ${p.verified?`<span class="sch-badge">verified</span>`:''}
      </div>
      <div class="patch-path">${escapeHtml(p.file||p.path||'')}</div>
      <div class="patch-diff" hidden></div>
      <div class="patch-actions">
        <button class="btn btn-ghost" data-p-diff>View diff</button>
        ${p.status==='proposed'?`<button class="btn btn-primary" data-p-approve>Approve &amp; apply</button>
        <button class="btn btn-ghost" data-p-reject>Reject</button>`:''}
        ${p.status==='applied'?`<button class="btn btn-ghost" data-p-revert>Revert</button>`:''}
      </div>`;
    thread.appendChild(card);
    const pid=p.id;
    card.querySelector('[data-p-diff]')?.addEventListener('click', async ()=>{
      const box=card.querySelector('.patch-diff');
      if(!box.hidden){ box.hidden=true; return; }
      const det=await api('/patches/'+encodeURIComponent(pid)+'/detail');
      box.hidden=false;
      box.textContent=(det&&det.success&&det.data&&(det.data.diff||det.data.new_content))||
        (det&&det.message)||'No diff available.';
    });
    card.querySelector('[data-p-approve]')?.addEventListener('click', async e2=>{
      // v0.3.8.51 (field report): this ran approve-then-apply against two text/plain endpoints
      // through .json() — the parse threw, the card said "Approve failed" over an approval that
      // had LANDED, and the apply never ran. One JSON endpoint now does both steps and says
      // exactly what happened.
      const b=e2.target; b.disabled=true; b.textContent='Applying…';
      const r2=await api('/patches/'+encodeURIComponent(pid)+'/approve-apply','POST',{});
      chatSetState((r2&&r2.message)||'The apply did not answer.');
      if(!(r2&&r2.success)){ b.disabled=false; b.textContent='Approve & apply'; return; }
      apiCacheBust('/patches'); chatPatchFingerprint=''; chatFingerprint=''; chatOpen(chatActiveId);
    });
    card.querySelector('[data-p-reject]')?.addEventListener('click', async e2=>{
      const b=e2.target; b.disabled=true;
      const r2=await fetch(url('/patches/'+encodeURIComponent(pid)+'/reject'),
        {method:'POST',headers:{'Authorization':'Bearer '+TOKEN}}).then(x=>x.json()).catch(()=>null);
      chatSetState((r2&&r2.message)||'Rejected.');
      apiCacheBust('/patches'); chatPatchFingerprint=''; chatFingerprint=''; chatOpen(chatActiveId);
    });
    card.querySelector('[data-p-revert]')?.addEventListener('click', async e2=>{
      const b=e2.target; b.disabled=true;
      const r2=await fetch(url('/revert/'+encodeURIComponent(pid)),
        {method:'POST',headers:{'Authorization':'Bearer '+TOKEN}}).then(x=>x.json()).catch(()=>null);
      chatSetState((r2&&r2.message)||'Reverted.');
      apiCacheBust('/patches'); chatPatchFingerprint=''; chatFingerprint=''; chatOpen(chatActiveId);
    });
  }
}

/* v0.3.8.51 (field report) — the FILES pane: side-by-side cowork in the colony view's real
 * estate. Browse the project's working tree, open a file in the editor, save (an attributed
 * operator action through PUT /projects/{id}/file), or flip to CHANGES — what this conversation's
 * missions proposed/applied, diff on demand through the same endpoints the patch cards use.
 * Files and Colony are mutually exclusive panes: one split, one right-hand occupant. */
let chatFilesOpen=false, chatFilesProjectId=null, chatFilesPath='', chatFilesChangesMode=false;
let chatFilesRepo=null, chatEditorPath='', chatEditorDiffMode=false, chatFilesBranch=null;

function chatToggleFiles(open){
  const layer=document.getElementById('chat-files-layer'); if(!layer) return;
  chatFilesOpen = open===undefined ? !chatFilesOpen : !!open;
  if(chatFilesOpen && chatColonyOpen) chatToggleColony(false);
  layer.hidden=!chatFilesOpen;
  document.getElementById('page-chat')?.classList.toggle('colony-open', chatFilesOpen||chatColonyOpen);
  const btn=document.getElementById('chat-files-toggle');
  if(btn) btn.setAttribute('aria-pressed', chatFilesOpen?'true':'false');
  if(chatFilesOpen){ chatFilesProjectId=null; chatFilesLoad(); }   // re-resolve per open — conversations move
}

async function chatFilesProject(){
  if(!chatActiveId) return null;
  const r=await api('/conversations/'+encodeURIComponent(chatActiveId));
  return (r&&r.success&&r.data&&r.data.project_id)||null;
}

/* The WORKING TREE (field correction: "can't see a working tree"): directories expand in place,
 * children fetched lazily, expansion state kept across refreshes. A project with no working
 * directory gets an inline "set it here" form instead of a dead end. */
let chatFilesExpanded=new Set();

async function chatFilesLoad(){
  const body=document.getElementById('chat-files-body');
  const editor=document.getElementById('chat-files-editor');
  if(!body) return;
  editor.hidden=true; body.style.display='';
  chatFilesChangesMode=false;
  document.getElementById('chat-files-changes')?.setAttribute('aria-pressed','false');
  chatFilesProjectId=chatFilesProjectId||await chatFilesProject();
  if(!chatFilesProjectId){
    body.innerHTML=chatActiveId
      ? '<div class="hud-state">This conversation has no project link — reopen it from its project, or start a new conversation.</div>'
      : '<div class="hud-state">Open a conversation first — the files pane shows its project\'s directory.</div>';
    return;
  }
  body.innerHTML='<div class="hud-state"><div class="hud-spinner"></div>Loading…</div>';
  const r=await api('/projects/'+encodeURIComponent(chatFilesProjectId)+'/files?path=');
  if(!(r&&r.success)){
    // Field correction: "can't add a working tree." A project without one sets it right here —
    // the same form the toolbar's Dir… button opens (fourth field round: one form, two doors).
    body.innerHTML=`<div class="hud-state">${escapeHtml((r&&r.message)||'Could not list files.')}</div>`;
    await chatFilesShowRootForm();
    return;
  }
  // v0.3.8.55: the ANTHILL-source default is LABELLED as one — the crumb must never let the
  // colony's own checkout pass for a directory the operator picked.
  setEl('chat-files-crumb', (r.data.root||'') + (r.data.default_root?'  (ANTHILL source — default until you set one)':''));
  body.innerHTML='<div id="cf-tree"></div>';
  chatFilesRenderDir(document.getElementById('cf-tree'), '', r.data.entries||[], 0);
  chatFilesRepoLoad();
}

/* v0.3.8.52 (fourth field round) — ONE set-root form, two doors: first-time setup (the files
 * pane shows it when listing fails) and the toolbar's Dir… button (the working directory stays
 * changeable after it is set). Prefilled with the current path, else the project's suggested
 * tree; typing an absolute path from memory is the fallback, not the ask — Browse opens the
 * picker (OS dialog in the desktop shell, the server-backed browser everywhere else, because
 * the page cannot learn an absolute path from its own picker and in Docker/LXC the working
 * directory lives on the SERVER anyway). Setting it CREATES the directory when new — the colony
 * never creates one behind the operator's back. */
async function chatFilesShowRootForm(){
  const host=document.getElementById('chat-files-body'); if(!host||!chatFilesProjectId) return;
  const existing=document.getElementById('cf-root-form');
  if(existing){ existing.remove(); return; }   // the Dir… button toggles
  const det=await api('/projects/'+encodeURIComponent(chatFilesProjectId));
  const prefill=(det&&det.success&&det.data&&(det.data.path||det.data.suggested_path))||'';
  host.insertAdjacentHTML('afterbegin',`<div id="cf-root-form">
    <div style="display:flex;gap:8px;align-items:center;padding:8px 4px;">
      <input class="provider-input" id="cf-set-root" value="${escapeHtml(prefill)}" placeholder="/absolute/path/to/working/directory" style="flex:1;">
      <button class="btn btn-ghost" id="cf-set-root-browse">Browse…</button>
      <button class="btn btn-primary" id="cf-set-root-go">Set working directory</button>
    </div>
    <div id="cf-dir-browser" hidden style="margin:4px;border:1px solid var(--border);border-radius:6px;max-height:280px;display:flex;flex-direction:column;"></div>
  </div>`);
  const setRoot=async p=>{
    if(!p) return;
    const pr=await api('/projects/'+encodeURIComponent(chatFilesProjectId),'PATCH',{path:p});
    chatSetState((pr&&pr.message)||'');
    // Bust every cached GET under this project — tree, repo badge, detail — or the pane keeps
    // showing the OLD directory (and its git state) for up to a TTL after the change.
    if(pr&&pr.success){ apiCacheBust('/projects'); chatFilesLoad(); }
  };
  document.getElementById('cf-set-root-go')?.addEventListener('click',
    ()=>setRoot((document.getElementById('cf-set-root').value||'').trim()));
  document.getElementById('cf-set-root-browse')?.addEventListener('click',async ()=>{
    const native=await cfPickFolderNative();
    if(native===''){ return; }                       // the operator cancelled the OS dialog
    if(native){ document.getElementById('cf-set-root').value=native; setRoot(native); return; }
    cfDirBrowserOpen('', setRoot);                   // no native picker — the server-backed one
  });
}
document.getElementById('chat-files-chroot')?.addEventListener('click',()=>chatFilesShowRootForm());

/* v0.3.8.55 (field report) — refresh on demand. The tree and the git badge ride the TTL'd GET
 * cache, which is right for polling and wrong for "I just changed something on disk — did it
 * land?". One button: bust every cached read under /projects, reload the pane. Expansion state
 * survives (chatFilesExpanded is keyed by path, not by fetch). */
(function(){
  const b=document.getElementById('chat-files-refresh'); if(!b) return;
  b.innerHTML=GLYPH.refresh;   // inline SVG goes through innerHTML, never textContent
  b.addEventListener('click',()=>{ apiCacheBust('/projects'); chatFilesLoad(); });
})();

/* v0.3.8.52 — the working-directory picker's two lanes.
 *
 * Native lane: inside the desktop shell, window.chrome.webview exists and the HOST owns a real
 * FolderBrowserDialog (ShellForm's pick-folder bridge). Resolves to the absolute path, '' when
 * the operator cancels, or null when there is no native picker to ask.
 * Server lane: everywhere else, a small directory browser over /fs/dirs — the server's own tree,
 * which in Docker/LXC is the only tree a working directory can live on. */
function cfPickFolderNative(){
  return new Promise(resolve=>{
    const wv=window.chrome&&window.chrome.webview;
    if(!wv){ resolve(null); return; }
    const onMsg=ev=>{
      const d=ev.data;
      if(d&&d.type==='picked-folder'){ wv.removeEventListener('message',onMsg); resolve(d.path||''); }
    };
    wv.addEventListener('message',onMsg);
    wv.postMessage('pick-folder');
  });
}

async function cfDirBrowserOpen(startPath, onPick){
  const host=document.getElementById('cf-dir-browser'); if(!host) return;
  host.hidden=false;
  host.innerHTML='<div class="hud-state"><div class="hud-spinner"></div>Loading…</div>';
  const r=await api('/fs/dirs?path='+encodeURIComponent(startPath||''));
  if(!(r&&r.success)){
    host.innerHTML=`<div class="hud-state err" style="padding:8px;">${escapeHtml((r&&r.message)||'Could not browse this host.')}</div>`;
    return;
  }
  const d=r.data||{}, dirs=d.dirs||[];
  host.innerHTML=`
    <div style="display:flex;gap:6px;align-items:center;padding:6px 8px;border-bottom:1px solid var(--border);">
      <span style="font-family:var(--mono);font-size:10px;color:var(--text);flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;" title="${escapeHtml(d.path||'')}">${escapeHtml(d.path||'')}</span>
      <button class="btn btn-ghost" data-cfb-home style="padding:1px 8px;font-size:10px;">Home</button>
      ${(d.roots||[]).map(rt=>`<button class="btn btn-ghost" data-cfb-go="${escapeHtml(rt)}" style="padding:1px 8px;font-size:10px;">${escapeHtml(rt)}</button>`).join('')}
      <button class="btn btn-primary" data-cfb-pick style="padding:1px 8px;font-size:10px;">Use this folder</button>
      <button class="btn btn-ghost" data-cfb-close style="padding:1px 8px;font-size:10px;">✕</button>
    </div>
    <div style="overflow-y:auto;flex:1;">
      ${d.parent?`<div class="cf-row" data-cfb-go="${escapeHtml(d.parent)}" style="padding:3px 10px;font-size:11px;cursor:pointer;color:var(--dim);">↩ ..</div>`:''}
      ${dirs.length?dirs.map(e=>`<div class="cf-row" data-cfb-go="${escapeHtml(e.path)}" style="padding:3px 10px;font-size:11px;cursor:pointer;">${GLYPH.folder} ${escapeHtml(e.name)}</div>`).join('')
                   :'<div class="hud-state" style="padding:8px;">No subfolders.</div>'}
    </div>`;
  host.querySelectorAll('[data-cfb-go]').forEach(el=>
    el.addEventListener('click',()=>cfDirBrowserOpen(el.dataset.cfbGo, onPick)));
  host.querySelector('[data-cfb-home]')?.addEventListener('click',()=>cfDirBrowserOpen(d.home||'', onPick));
  host.querySelector('[data-cfb-pick]')?.addEventListener('click',()=>{
    const input=document.getElementById('cf-set-root'); if(input) input.value=d.path||'';
    host.hidden=true;
    onPick(d.path||'');
  });
  host.querySelector('[data-cfb-close]')?.addEventListener('click',()=>{ host.hidden=true; });
}

/* v0.3.8.51 third round — GIT AWARENESS (operator: "make the colony aware (and show) whether
 * the project it is currently working on is a git repo or just a regular folder"). The badge
 * states branch + dirty count, or "plain folder"; Commit appears only for a repo. */
async function chatFilesRepoLoad(){
  const badge=document.getElementById('chat-files-repo');
  const commitBtn=document.getElementById('chat-files-commit');
  if(!badge||!chatFilesProjectId) return;
  const r=await api('/projects/'+encodeURIComponent(chatFilesProjectId)+'/repo');
  chatFilesRepo=(r&&r.success&&r.data)||null;
  chatFilesApplyGitMarks();   // the tree usually rendered before this answer arrived
  const initBtn=document.getElementById('chat-files-gitinit');
  if(!chatFilesRepo){ badge.textContent=''; if(commitBtn) commitBtn.hidden=true; if(initBtn) initBtn.hidden=true; return; }
  const branchSel=document.getElementById('chat-files-branch');
  if(chatFilesRepo.is_repo){
    if(initBtn) initBtn.hidden=true;   // offered ONLY when it is not a repo already
    // v0.3.8.52 (fourth field round): one line, bounded — the server no longer sends git's
    // stderr as a branch name, and this guard means even a bad payload cannot eat the toolbar.
    const branchText=String(chatFilesRepo.branch||'no commits yet').split('\n')[0].slice(0,40);
    badge.textContent='⎇ '+branchText
      +(chatFilesRepo.dirty_count>0?' · '+chatFilesRepo.dirty_count+' dirty':' · clean');
    badge.style.color=chatFilesRepo.dirty_count>0?'var(--amber,#e5b567)':'var(--dim)';
    badge.title=(chatFilesRepo.last_commit?('last: '+chatFilesRepo.last_commit):'')||'git repository';
    if(commitBtn){ commitBtn.hidden=false; commitBtn.disabled=!chatFilesRepo.dirty_count; }
    // v0.3.8.52 — the branch selector, GitHub-style: history is read from the chosen branch,
    // nothing is ever checked out. Defaults to the branch the tree actually stands on.
    if(branchSel){
      const br=await api('/projects/'+encodeURIComponent(chatFilesProjectId)+'/repo/branches');
      const list=(br&&br.success&&br.data&&br.data.branches)||[];
      if(list.length){
        const cur=(br.data.current&&list.includes(br.data.current))?br.data.current:list[0];
        if(!chatFilesBranch||!list.includes(chatFilesBranch)) chatFilesBranch=cur;
        branchSel.innerHTML=list.map(b=>`<option value="${escapeHtml(b)}"${b===chatFilesBranch?' selected':''}>${escapeHtml(b)}</option>`).join('');
        branchSel.hidden=false;
      }else branchSel.hidden=true;
    }
  }else{
    // Field wording: "no git" — two words, the detail in the title, the remedy one button away.
    badge.textContent='no git';
    badge.style.color='var(--dim)';
    badge.title=chatFilesRepo.note||'This directory is not a git repository.';
    if(commitBtn) commitBtn.hidden=true;
    if(branchSel) branchSel.hidden=true;
    if(initBtn) initBtn.hidden=false;
  }
}

// v0.3.8.52 (fourth field round): make the working directory a repository — offered only while
// it is not one (chatFilesRepoLoad shows/hides the button), confirmed, then the badge goes live.
document.getElementById('chat-files-gitinit')?.addEventListener('click',async ()=>{
  if(!chatFilesProjectId) return;
  if(!await uiConfirm('Create a git repository in this project\'s working directory?\n\n'
    +'Runs git init there — no commits are made and nothing is sent anywhere.')) return;
  const btn=document.getElementById('chat-files-gitinit');
  if(btn) btn.disabled=true;
  try{
    const r=await api('/projects/'+encodeURIComponent(chatFilesProjectId)+'/repo/init','POST');
    chatSetState((r&&r.message)||'');
    // Bust the cached GETs (repo badge, tree, detail) — without this the badge re-read a stale
    // "not a repo" answer for up to a TTL and Init git appeared to ignore the click.
    if(r&&r.success){ apiCacheBust('/projects/'+encodeURIComponent(chatFilesProjectId)); chatFilesRepoLoad(); chatFilesLoad(); }
  }finally{ if(btn) btn.disabled=false; }
});

/* v0.3.8.52 (fifth field round) — the splits are SIZABLE. Chat ↔ pane drags horizontally, tree ↔
 * editor drags vertically; plain pointer math over the containing flex row/column, the chosen
 * proportion written onto flex-basis and persisted in localStorage so it survives a reload.
 * Pointer capture keeps the drag alive when the cursor outruns the 5px handle. */
function wireSplit(handleId, horizontal, storeKey, apply){
  const handle=document.getElementById(handleId); if(!handle) return;
  const saved=parseFloat(localStorage.getItem(storeKey)||'');
  if(!Number.isNaN(saved)) apply(saved);
  handle.addEventListener('pointerdown',e=>{
    e.preventDefault(); handle.classList.add('dragging');
    // Capture keeps the drag when the cursor outruns the 5px handle; a pointer that cannot be
    // captured (gone between down and here) must not kill the handler with it.
    try{ handle.setPointerCapture(e.pointerId); }catch{ /* drag still works while over the handle */ }
    const rect=handle.parentElement.getBoundingClientRect();
    const move=ev=>{
      const pct=horizontal ? (ev.clientX-rect.left)/rect.width*100
                           : (ev.clientY-rect.top)/rect.height*100;
      localStorage.setItem(storeKey, String(apply(pct)));
    };
    const up=()=>{ handle.classList.remove('dragging');
      handle.removeEventListener('pointermove',move); handle.removeEventListener('pointerup',up);
      handle.removeEventListener('pointercancel',up); };
    handle.addEventListener('pointermove',move); handle.addEventListener('pointerup',up);
    // v0.3.8.55 (field report: the bar stayed light): a CANCELLED pointer — touch cancel, window
    // blur mid-drag — never fired pointerup, so .dragging stuck and the handle kept its bright
    // drag colour until the next page load. Cancel ends the drag like up does.
    handle.addEventListener('pointercancel',up);
  });
}
wireSplit('chat-split-x', true, 'anthill_split_x', pct=>{
  pct=Math.min(75,Math.max(25,pct));
  const main=document.querySelector('#page-chat .chat-main');
  if(main) main.style.flex=`1 1 ${pct}%`;
  document.querySelectorAll('#page-chat .chat-colony-layer').forEach(l=>l.style.flex=`1 1 ${100-pct}%`);
  return pct;
});
wireSplit('chat-split-y', false, 'anthill_split_y', pct=>{
  pct=Math.min(80,Math.max(20,pct));
  const body=document.getElementById('chat-files-body');
  const editor=document.getElementById('chat-files-editor');
  if(body) body.style.flex=`1 1 ${pct}%`;
  if(editor) editor.style.flex=`1 1 ${100-pct}%`;
  return pct;
});

document.getElementById('chat-files-branch')?.addEventListener('change',e=>{
  chatFilesBranch=e.target.value;
  // Open trains show the OLD branch's history — close them so nothing on screen lies.
  document.querySelectorAll('.cf-train').forEach(el=>{ el.parentElement.hidden=true; el.remove(); });
});

document.getElementById('chat-files-commit')?.addEventListener('click',async ()=>{
  if(!chatFilesProjectId) return;
  const msg=prompt('Commit message (commits all uncommitted changes):','anthill: operator commit from the files pane');
  if(!msg) return;
  const r=await api('/projects/'+encodeURIComponent(chatFilesProjectId)+'/repo/commit','POST',{message:msg});
  chatSetState((r&&r.message)||'');
  chatFilesRepoLoad();
});

let chatFilesSelPath=null;

function chatFilesRenderDir(host, base, entries, depth){
  host.innerHTML=(entries||[]).map(e=>{
    const p=(base?base+'/':'')+e.name;
    const open=chatFilesExpanded.has(p);
    // v0.3.8.52 — the WHOLE row is the click target (the listener always was on the row; now the
    // hover/selected styling says so), the selected file stays lit, and a git-status letter sits
    // in the dead space before the size.
    return `<div class="cf-row${(!e.dir&&chatFilesSelPath===p)?' sel':''}" data-cf-${e.dir?'dir':'file'}="${escapeHtml(p)}" style="padding:3px 6px 3px ${8+depth*16}px;display:flex;gap:7px;align-items:center;font-size:11px;cursor:pointer;border-radius:4px;">
        <span style="width:12px;color:var(--dim);">${e.dir?(open?'▾':'▸'):''}</span>
        <span style="display:flex;color:var(--dim);">${e.dir?GLYPH.folder:GLYPH.file}</span>
        <span style="color:var(--text);flex:1;overflow:hidden;text-overflow:ellipsis;">${escapeHtml(e.name)}</span>
        ${e.dir?'':`<span class="cf-hist" data-cf-h="${escapeHtml(p)}" title="Commit history for this file">${GLYPH.clock}</span>`}
        <span class="cf-git" data-cf-g="${escapeHtml(p)}" data-cf-g-dir="${e.dir?'1':''}"></span>
        ${e.dir?'':`<span style="color:var(--dim);font-size:9px;">${e.size<1024?e.size+' B':Math.round(e.size/1024)+' KB'}</span>`}
      </div><div class="cf-kids" data-cf-kids="${escapeHtml(p)}" ${open?'':'hidden'}></div>`;
  }).join('')||`<div style="padding:4px 6px 4px ${8+depth*16}px;font-size:10px;color:var(--dim);">empty</div>`;
  host.querySelectorAll(':scope > [data-cf-dir]').forEach(el=>el.addEventListener('click',async ()=>{
    const p=el.dataset.cfDir;
    const kids=host.querySelector(`:scope > [data-cf-kids="${CSS.escape(p)}"]`);
    if(chatFilesExpanded.has(p)){ chatFilesExpanded.delete(p); kids.hidden=true; el.firstElementChild.textContent='▸'; return; }
    chatFilesExpanded.add(p); el.firstElementChild.textContent='▾'; kids.hidden=false;
    const r=await api('/projects/'+encodeURIComponent(chatFilesProjectId)+'/files?path='+encodeURIComponent(p));
    if(r&&r.success) chatFilesRenderDir(kids, p, r.data.entries||[], p.split('/').length);
    else kids.innerHTML=`<div class="hud-state err">${escapeHtml((r&&r.message)||'Could not open.')}</div>`;
  }));
  host.querySelectorAll(':scope > [data-cf-file]').forEach(el=>el.addEventListener('click',()=>chatFilesEdit(el.dataset.cfFile)));
  // v0.3.8.52 — the commit train, on demand: the clock toggles this file's recent commits into
  // the slot beneath its row. Its click must NOT also open the editor — the row owns that.
  host.querySelectorAll(':scope .cf-hist').forEach(el=>el.addEventListener('click',ev=>{
    ev.stopPropagation();
    const p=el.dataset.cfH;
    chatFilesTrain(p, host.querySelector(`:scope > [data-cf-kids="${CSS.escape(p)}"]`));
  }));
  chatFilesApplyGitMarks(host);
}

function cfAge(unixSeconds){
  const s=Math.max(0,(Date.now()/1000)-unixSeconds);
  if(s<3600) return Math.max(1,Math.round(s/60))+'m';
  if(s<86400) return Math.round(s/3600)+'h';
  if(s<86400*30) return Math.round(s/86400)+'d';
  if(s<86400*365) return Math.round(s/(86400*30))+'mo';
  return Math.round(s/(86400*365))+'y';
}

/* The train itself: hash · subject · author · age per stop, read from the SELECTED branch
 * (GitHub-style — nothing is checked out). Clicking a stop toggles that commit's diff for this
 * file, through the same colored renderer the uncommitted view uses. */
async function chatFilesTrain(path, slot){
  if(!slot) return;
  const open=slot.querySelector(':scope > .cf-train');
  if(open){ open.remove(); if(!slot.children.length) slot.hidden=true; return; }
  slot.hidden=false;
  const box=document.createElement('div');
  box.className='cf-train';
  box.innerHTML='<div style="padding:4px 10px;font-size:10px;color:var(--dim);">loading history…</div>';
  slot.prepend(box);
  const q='/projects/'+encodeURIComponent(chatFilesProjectId)+'/repo/log?path='+encodeURIComponent(path)
    +(chatFilesBranch?'&branch='+encodeURIComponent(chatFilesBranch):'')+'&limit=20';
  const r=await api(q);
  const commits=(r&&r.success&&r.data&&r.data.commits)||[];
  if(!commits.length){
    box.innerHTML='<div style="padding:4px 10px;font-size:10px;color:var(--dim);">No commits touch this file'
      +(chatFilesBranch?' on '+escapeHtml(chatFilesBranch):'')+'.</div>';
    return;
  }
  box.innerHTML=commits.map(c=>`<div class="cf-stop" data-cf-hash="${escapeHtml(c.hash)}"
      style="padding:2px 10px 2px 34px;display:flex;gap:8px;align-items:baseline;font-size:10.5px;cursor:pointer;border-radius:4px;">
      <span style="color:var(--dim);">○</span>
      <span style="font-family:var(--mono);color:#8ab4f8;">${escapeHtml(c.hash)}</span>
      <span style="color:var(--text);flex:1;overflow:hidden;text-overflow:ellipsis;">${escapeHtml(c.subject)}</span>
      <span style="color:var(--dim);font-size:9px;white-space:nowrap;">${escapeHtml(c.author)} · ${cfAge(c.time)}</span>
    </div><div data-cf-stop-diff="${escapeHtml(c.hash)}" hidden></div>`).join('');
  box.querySelectorAll('.cf-stop').forEach(el=>el.addEventListener('click',async ev=>{
    ev.stopPropagation();
    const h=el.dataset.cfHash;
    const d=box.querySelector(`[data-cf-stop-diff="${CSS.escape(h)}"]`);
    if(!d.hidden){ d.hidden=true; return; }
    d.hidden=false;
    if(!d.innerHTML){
      d.innerHTML='<div style="padding:4px 10px;font-size:10px;color:var(--dim);">loading…</div>';
      const s=await api('/projects/'+encodeURIComponent(chatFilesProjectId)+'/repo/show?hash='
        +encodeURIComponent(h)+'&path='+encodeURIComponent(path));
      d.innerHTML=(s&&s.success&&s.data)?cfGitDiffHtml(s.data.diff||''):
        `<div class="hud-state err">${escapeHtml((s&&s.message)||'Could not load the commit.')}</div>`;
    }
  }));
}

/* v0.3.8.52 — git status letters in the tree, from the dirty list the repo badge already fetched
 * (zero extra calls): M modified, A added, D deleted, ? untracked; a dot on a folder that holds
 * dirty files somewhere beneath it. Repo paths are repo-root-relative and tree paths are
 * project-root-relative — suffix agreement bridges the two when the roots differ. */
function chatFilesApplyGitMarks(scope){
  const dirty=(chatFilesRepo&&chatFilesRepo.is_repo&&chatFilesRepo.dirty)||[];
  (scope||document).querySelectorAll('.cf-git[data-cf-g]').forEach(el=>{
    const p=el.dataset.cfG; let st=null;
    if(el.dataset.cfGDir){
      if(dirty.some(d=>d.path.startsWith(p+'/')||d.path.includes('/'+p+'/'))) st='•';
    }else{
      const hit=dirty.find(d=>d.path===p||d.path.endsWith('/'+p)||p.endsWith('/'+d.path));
      if(hit) st=(hit.status||'M')[0];
    }
    if(!st){ el.textContent=''; el.className='cf-git'; return; }
    const cls=st==='•'?'m':st==='?'?'u':st.toLowerCase()==='a'?'a':st.toLowerCase()==='d'?'d':'m';
    el.textContent=st==='•'?'•':st.toUpperCase();
    el.className='cf-git '+cls;
    el.title=st==='•'?'contains uncommitted changes':'uncommitted: '+st;
  });
}

// Create file / folder (field correction: "can't add a new file, can't add a folder").
// v0.3.8.52 (second field round): + File and + Folder BROWSE like the Browse button does — a
// picker over the project's own jailed tree instead of a prompt() asking for a path from memory.
// Navigate to where the new entry belongs (+ Folder walks folders only; + File also SHOWS the
// files already at each stop, since seeing what exists is the point of browsing), name it,
// create. The POST underneath is the same attributed, jailed call it always was.
async function chatFilesCreate(isDir, at){
  if(!chatFilesProjectId) return;
  const body=document.getElementById('chat-files-body'); if(!body) return;
  if(!document.getElementById('cf-create-panel'))
    body.insertAdjacentHTML('afterbegin','<div id="cf-create-panel" style="margin:4px;border:1px solid var(--border);border-radius:6px;max-height:300px;display:flex;flex-direction:column;"></div>');
  const panel=document.getElementById('cf-create-panel');
  const rel=(at||'').replace(/^\/+/,'');
  const r=await api('/projects/'+encodeURIComponent(chatFilesProjectId)+'/files?path='+encodeURIComponent(rel));
  if(!(r&&r.success)){ panel.innerHTML=`<div class="hud-state err" style="padding:8px;">${escapeHtml((r&&r.message)||'Could not list that folder.')}</div>`; return; }
  const entries=r.data.entries||[];
  const dirs=entries.filter(e=>e.dir), files=entries.filter(e=>!e.dir);
  const parent=rel?rel.split('/').slice(0,-1).join('/'):null;
  panel.innerHTML=`
    <div style="display:flex;gap:6px;align-items:center;padding:6px 8px;border-bottom:1px solid var(--border);">
      <span style="font-size:10px;color:var(--muted);">New ${isDir?'folder':'file'} in</span>
      <span style="font-family:var(--mono);font-size:10px;color:var(--text);flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">/${escapeHtml(rel)}</span>
      <button class="btn btn-ghost" data-cfc-close style="padding:1px 8px;font-size:10px;">✕</button>
    </div>
    <div style="overflow-y:auto;flex:1;">
      ${rel!==null&&rel!==''?`<div class="cf-row" data-cfc-go="${escapeHtml(parent)}" style="padding:3px 10px;font-size:11px;cursor:pointer;color:var(--dim);">↩ ..</div>`:''}
      ${dirs.map(e=>`<div class="cf-row" data-cfc-go="${escapeHtml((rel?rel+'/':'')+e.name)}" style="padding:3px 10px;font-size:11px;cursor:pointer;">${GLYPH.folder} ${escapeHtml(e.name)}</div>`).join('')}
      ${isDir?'':files.map(e=>`<div style="padding:3px 10px;font-size:11px;color:var(--dim);">${GLYPH.file} ${escapeHtml(e.name)}</div>`).join('')}
      ${(dirs.length||(!isDir&&files.length))?'':'<div class="hud-state" style="padding:8px;">Empty folder.</div>'}
    </div>
    <div style="display:flex;gap:6px;align-items:center;padding:6px 8px;border-top:1px solid var(--border);">
      <input class="provider-input" data-cfc-name placeholder="${isDir?'folder-name':'file-name.ext'}" style="flex:1;font-size:11px;">
      <button class="btn btn-primary" data-cfc-create style="padding:2px 10px;font-size:10px;">Create here</button>
    </div>`;
  panel.querySelectorAll('[data-cfc-go]').forEach(el=>
    el.addEventListener('click',()=>chatFilesCreate(isDir, el.dataset.cfcGo)));
  panel.querySelector('[data-cfc-close]')?.addEventListener('click',()=>panel.remove());
  const nameInput=panel.querySelector('[data-cfc-name]');
  const create=async ()=>{
    const name=(nameInput.value||'').trim().replace(/^\/+|\/+$/g,'');
    if(!name) return;
    const full=(rel?rel+'/':'')+name;
    const cr=await api('/projects/'+encodeURIComponent(chatFilesProjectId)+'/files','POST',{path:full, dir:!!isDir});
    chatSetState((cr&&cr.message)||'');
    if(cr&&cr.success){
      panel.remove();
      full.split('/').slice(0,-1).reduce((acc,part)=>{ const p=(acc?acc+'/':'')+part; chatFilesExpanded.add(p); return p; },'');
      if(!isDir){ await chatFilesLoad(); chatFilesEdit(full); } else chatFilesLoad();
    }
  };
  panel.querySelector('[data-cfc-create]')?.addEventListener('click',create);
  nameInput?.addEventListener('keydown',e=>{ if(e.key==='Enter') create(); });
  nameInput?.focus();
}
document.getElementById('chat-files-newfile')?.addEventListener('click',()=>chatFilesCreate(false));
document.getElementById('chat-files-newdir')?.addEventListener('click',()=>chatFilesCreate(true));

/* v0.3.8.51 third round — the editor DOCKS BELOW the working tree (operator: "click on a file,
 * and it open below the working tree"). The tree stays visible and clickable above; the editor
 * fills the lower half; ✕ closes only the editor. The textarea is, and stays, EDITABLE — Save
 * is the same attributed PUT it always was. */
async function chatFilesEdit(path){
  const editor=document.getElementById('chat-files-editor');
  const r=await api('/projects/'+encodeURIComponent(chatFilesProjectId)+'/file?path='+encodeURIComponent(path));
  if(!(r&&r.success)){ chatSetState((r&&r.message)||'Could not open the file.'); return; }
  editor.hidden=false;
  chatEditorPath=r.data.path;
  // v0.3.8.52 — the elected file stays LIT in the tree above.
  chatFilesSelPath=path;
  document.querySelectorAll('.cf-row[data-cf-file]').forEach(el=>
    el.classList.toggle('sel', el.dataset.cfFile===path));
  chatEditorShowText();               // a fresh file always opens in EDIT mode, not a stale diff
  setEl('chat-editor-path',r.data.path);
  document.getElementById('chat-editor-text').value=r.data.content;
  document.getElementById('chat-editor-msg').textContent='';
  chatEditorHl();
}

function chatEditorShowText(){
  chatEditorDiffMode=false;
  const w=document.getElementById('chat-editor-wrap'), d=document.getElementById('chat-editor-diff');
  if(w) w.hidden=false; if(d) d.hidden=true;
  document.getElementById('chat-editor-changes')?.setAttribute('aria-pressed','false');
}

/* v0.3.8.52 — the editor's highlighted layer: the SAME escape-first chatHighlight the chat's
 * fenced blocks use, keyed by file extension, re-run (debounced) on every edit and scroll-synced
 * under the transparent textarea. Oversized files fall back to plain escaped text — visible,
 * editable, just uncolored. */
function chatEditorLang(p){
  const ext=(p||'').split('.').pop().toLowerCase();
  return ({js:'js',mjs:'js',cjs:'js',jsx:'js',ts:'js',tsx:'js',json:'js',
    py:'py',cs:'cs',sh:'sh',bash:'sh',ps1:'sh',yml:'sh',yaml:'sh',sql:'sql'})[ext]||'';
}
let chatEditorHlTimer=null;
function chatEditorHl(){
  const pre=document.getElementById('chat-editor-hl'), t=document.getElementById('chat-editor-text');
  if(!pre||!t) return;
  const v=t.value;
  // The trailing newline keeps the pre's scroll height matching the textarea's on the last line.
  pre.innerHTML=(v.length>200000?escapeHtml(v):chatHighlight(v,chatEditorLang(chatEditorPath)))+'\n';
  pre.scrollTop=t.scrollTop; pre.scrollLeft=t.scrollLeft;
}
document.getElementById('chat-editor-text')?.addEventListener('input',()=>{
  clearTimeout(chatEditorHlTimer); chatEditorHlTimer=setTimeout(chatEditorHl,90);
});
document.getElementById('chat-editor-text')?.addEventListener('scroll',()=>{
  const pre=document.getElementById('chat-editor-hl'), t=document.getElementById('chat-editor-text');
  if(pre&&t){ pre.scrollTop=t.scrollTop; pre.scrollLeft=t.scrollLeft; }
});

document.getElementById('chat-editor-save')?.addEventListener('click',async ()=>{
  const msg=document.getElementById('chat-editor-msg');
  const r=await api('/projects/'+encodeURIComponent(chatFilesProjectId)+'/file','PUT',{
    path:document.getElementById('chat-editor-path').textContent,
    content:document.getElementById('chat-editor-text').value,
  });
  msg.style.color=r&&r.success?'var(--green)':'var(--red)';
  msg.textContent=(r&&r.message)||'Save failed';
  setTimeout(()=>{ if(msg.textContent) msg.textContent=''; },4000);
  if(r&&r.success) chatFilesRepoLoad();          // a save changes the dirty count
});
document.getElementById('chat-editor-back')?.addEventListener('click',()=>{
  document.getElementById('chat-files-editor').hidden=true;   // the tree above is untouched
});

/* Recent edits to THE OPEN FILE (operator: "view recent edits by clicking change in the text
 * editor window"): the uncommitted git diff leads when the directory is a repo, then the
 * colony's patch proposals against this file, newest mission last. Toggling back returns to
 * the editable textarea with its content intact. */
function cfGitDiffHtml(text){
  if(!(text||'').trim()) return '<div style="padding:6px 10px;font-size:10px;color:var(--dim);">No uncommitted changes to this file.</div>';
  return '<pre style="margin:0;font-size:10.5px;line-height:1.45;overflow-x:auto;">'
    + text.split('\n').map(l=>{
        const s=escapeHtml(l);
        if(l.startsWith('+++')||l.startsWith('---')||l.startsWith('diff ')||l.startsWith('index '))
          return `<span style="display:block;padding:0 10px;color:var(--dim);">${s}</span>`;
        if(l.startsWith('@@')) return `<span style="display:block;padding:0 10px;color:#8ab4f8;">${s}</span>`;
        if(l.startsWith('+')) return `<span style="display:block;padding:0 10px;background:rgba(52,211,153,.09);color:#7ee2b8;">${s}</span>`;
        if(l.startsWith('-')) return `<span style="display:block;padding:0 10px;background:rgba(248,113,113,.09);color:#f3a0a0;">${s}</span>`;
        return `<span style="display:block;padding:0 10px;color:var(--muted);">${s}</span>`;
      }).join('')+'</pre>';
}

document.getElementById('chat-editor-changes')?.addEventListener('click',async ()=>{
  const t=document.getElementById('chat-editor-wrap'), d=document.getElementById('chat-editor-diff');
  if(chatEditorDiffMode){ chatEditorShowText(); return; }
  chatEditorDiffMode=true; t.hidden=true; d.hidden=false;
  document.getElementById('chat-editor-changes').setAttribute('aria-pressed','true');
  d.innerHTML='<div class="hud-state"><div class="hud-spinner"></div>Loading recent edits…</div>';
  const parts=[];

  if(chatFilesRepo&&chatFilesRepo.is_repo){
    const g=await api('/projects/'+encodeURIComponent(chatFilesProjectId)+'/repo/diff?path='+encodeURIComponent(chatEditorPath));
    parts.push('<div style="padding:6px 10px 2px;font-size:10px;color:var(--dim);letter-spacing:.05em;text-transform:uppercase;">Uncommitted (git)</div>'
      + cfGitDiffHtml((g&&g.success&&g.data&&g.data.diff)||''));
  }

  const conv=await api('/conversations/'+encodeURIComponent(chatActiveId));
  const missions=(conv&&conv.data&&conv.data.mission_ids)||[];
  const hist=await api('/missions/json?limit=60');
  const goals=Object.fromEntries(((hist&&hist.data)||[]).map(m=>[String(m.id),m.goal||'']));
  let any=false;
  for(const mid of missions.slice(-6)){
    const r=await api('/patches?mission_id='+encodeURIComponent(mid)+'&limit=30');
    // Patch paths may be workspace-relative while the editor's are project-root-relative; when
    // the two roots differ, suffix agreement is the honest match.
    const mine=((r&&r.success&&Array.isArray(r.data))?r.data:[]).filter(p=>{
      const f=p.file||p.path||'';
      return f===chatEditorPath||f.endsWith('/'+chatEditorPath)||chatEditorPath.endsWith('/'+f);
    });
    if(!mine.length) continue;
    any=true;
    const first=await api('/patches/'+encodeURIComponent(mine[0].id)+'/detail');
    const last=mine.length>1?await api('/patches/'+encodeURIComponent(mine[mine.length-1].id)+'/detail'):first;
    const d1=(first&&first.success&&first.data)||{}, d2=(last&&last.success&&last.data)||{};
    parts.push(`<div style="padding:8px 10px 2px;font-size:10px;color:var(--dim);">
        <span style="letter-spacing:.05em;text-transform:uppercase;">Colony</span> ·
        ${escapeHtml((goals[mid]||('mission '+mid.slice(0,8))).split('\n')[0].slice(0,90))}
        <span class="sch-badge" style="margin-left:6px;">${escapeHtml(mine[mine.length-1].status||'')}</span>
      </div>`
      + ((d1.old_content!==undefined||d2.new_content!==undefined)
          ? cfDiffHtml(cfLineDiff(d1.old_content||'',d2.new_content||''))
          : `<pre style="margin:0;padding:4px 10px;font-size:10.5px;white-space:pre-wrap;color:var(--muted);">${escapeHtml(d2.diff||'No diff recorded.')}</pre>`));
  }
  if(!any&&!(chatFilesRepo&&chatFilesRepo.is_repo))
    parts.push('<div class="hud-state">No recorded edits to this file.</div>');
  else if(!any)
    parts.push('<div style="padding:6px 10px;font-size:10px;color:var(--dim);">No colony proposals against this file in this conversation.</div>');
  d.innerHTML=parts.join('');
});

/* CHANGES, commit-style (field correction: "changes should be displayed like Github changes in
 * Commits"). Grouped by MISSION — the mission's goal line is the commit message, its status the
 * badge — with every file's colored diff rendered OPEN, +/- lines and context, no extra click. */
function cfLineDiff(oldText, newText){
  const a=(oldText||'').split('\n'), b=(newText||'').split('\n');
  let start=0; while(start<a.length&&start<b.length&&a[start]===b[start]) start++;
  let endA=a.length, endB=b.length;
  while(endA>start&&endB>start&&a[endA-1]===b[endB-1]){ endA--; endB--; }
  const rows=[];
  const CTX=3;
  for(let i=Math.max(0,start-CTX);i<start;i++) rows.push({t:' ',s:a[i]});
  for(let i=start;i<endA;i++) rows.push({t:'-',s:a[i]});
  for(let i=start;i<endB;i++) rows.push({t:'+',s:b[i]});
  for(let i=endA;i<Math.min(a.length,endA+CTX);i++) rows.push({t:' ',s:a[i]});
  return rows;
}
function cfDiffHtml(rows){
  if(!rows.length) return '<div style="padding:6px 10px;font-size:10px;color:var(--dim);">No line changes.</div>';
  return '<pre style="margin:0;font-size:10.5px;line-height:1.45;overflow-x:auto;">'
    + rows.map(r=>`<span style="display:block;padding:0 10px;${
        r.t==='+'?'background:rgba(52,211,153,.09);color:#7ee2b8;':
        r.t==='-'?'background:rgba(248,113,113,.09);color:#f3a0a0;':'color:var(--muted);'
      }">${escapeHtml(r.t+' '+r.s)}</span>`).join('')+'</pre>';
}

document.getElementById('chat-files-changes')?.addEventListener('click',async ()=>{
  const body=document.getElementById('chat-files-body');
  const editor=document.getElementById('chat-files-editor');
  if(chatFilesChangesMode){ chatFilesLoad(); return; }
  chatFilesChangesMode=true; editor.hidden=true; body.style.display='';
  document.getElementById('chat-files-changes').setAttribute('aria-pressed','true');
  body.innerHTML='<div class="hud-state"><div class="hud-spinner"></div>Loading changes…</div>';

  const conv=await api('/conversations/'+encodeURIComponent(chatActiveId));
  const missions=(conv&&conv.data&&conv.data.mission_ids)||[];
  const hist=await api('/missions/json?limit=60');
  const goals=Object.fromEntries(((hist&&hist.data)||[]).map(m=>[String(m.id),m.goal||'']));

  const groups=[];
  for(const mid of missions.slice(-6)){
    const r=await api('/patches?mission_id='+encodeURIComponent(mid)+'&limit=30');
    const rows=(r&&r.success&&Array.isArray(r.data))?r.data:[];
    if(rows.length) groups.push({mid, rows});
  }
  if(!groups.length){ body.innerHTML='<div class="hud-state">No changes yet — this conversation\'s missions have proposed nothing.</div>'; return; }

  // Field correction: PER FILE, not per approval. Several proposals against one file collapse
  // into ONE entry whose diff runs from the FIRST proposal's old content to the LAST one's new —
  // the cumulative change, the way a commit shows a file once however many edits produced it.
  for(const g of groups){
    const byFile=new Map();
    for(const p of g.rows){
      const key=p.file||p.path||'?';
      if(!byFile.has(key)) byFile.set(key,[]);
      byFile.get(key).push(p);
    }
    g.files=[...byFile.entries()].map(([file,patches])=>({file,patches}));
  }

  body.innerHTML=groups.map(g=>`<div class="card" style="margin-bottom:10px;">
      <div style="padding:9px 12px;border-bottom:1px solid var(--border);">
        <div style="font-size:11.5px;color:var(--text);font-weight:600;">${escapeHtml((goals[g.mid]||'mission '+g.mid.slice(0,8)).split('\n')[0].slice(0,110))}</div>
        <div style="font-size:9px;color:var(--dim);margin-top:2px;">mission ${escapeHtml(g.mid.slice(0,8))} · ${g.files.length} file(s), ${g.rows.length} proposal(s)</div>
      </div>
      ${g.files.map((f,i)=>`<div style="border-bottom:1px solid rgba(30,51,84,.4);">
        <div style="padding:6px 12px;display:flex;gap:8px;align-items:center;font-size:11px;">
          <span class="sch-badge">${escapeHtml(f.patches[f.patches.length-1].status||'')}</span>
          <span style="font-family:var(--mono);color:var(--text);flex:1;overflow:hidden;text-overflow:ellipsis;">${escapeHtml(f.file)}</span>
          ${f.patches.length>1?`<span style="font-size:9px;color:var(--dim);">${f.patches.length} proposals</span>`:''}
        </div>
        <div data-cf-diff="${escapeHtml(g.mid)}:${i}"><div style="padding:4px 12px;font-size:10px;color:var(--dim);">loading diff…</div></div>
      </div>`).join('')}
    </div>`).join('');

  // Diffs render OPEN, GitHub-style: first old → last new per file.
  for(const g of groups) for(let i=0;i<g.files.length;i++){
    const f=g.files[i];
    const box=body.querySelector(`[data-cf-diff="${CSS.escape(g.mid+':'+i)}"]`);
    if(!box) continue;
    const first=await api('/patches/'+encodeURIComponent(f.patches[0].id)+'/detail');
    const last=f.patches.length>1
      ? await api('/patches/'+encodeURIComponent(f.patches[f.patches.length-1].id)+'/detail') : first;
    const d1=(first&&first.success&&first.data)||{}, d2=(last&&last.success&&last.data)||{};
    box.innerHTML=(d1.old_content!==undefined||d2.new_content!==undefined)
      ? cfDiffHtml(cfLineDiff(d1.old_content||'', d2.new_content||''))
      : `<pre style="margin:0;padding:4px 12px;font-size:10.5px;white-space:pre-wrap;color:var(--muted);">${escapeHtml(d2.diff||'No diff available.')}</pre>`;
  }
});

document.getElementById('chat-files-toggle')?.addEventListener('click',()=>chatToggleFiles());
document.getElementById('chat-files-close')?.addEventListener('click',()=>chatToggleFiles(false));

document.getElementById('chat-colony-toggle')?.addEventListener('click', ()=>{
  if(chatFilesOpen) chatToggleFiles(false);   // one split, one right-hand occupant
  chatToggleColony();
});
document.getElementById('chat-colony-close')?.addEventListener('click', ()=>chatToggleColony(false));
// Secondary action for operators who want the dedicated page. Close first so the canvas is home
// before the Colony route claims it — the same hand-off order navigation itself performs.
document.getElementById('chat-colony-full')?.addEventListener('click', ()=>{
  chatToggleColony(false); go('/colony/topology');
});
// v0.3.8.43 (SOW §4): the obvious way back when panning has lost the colony — reset the camera
// targets and let the loop glide home. The same reset the Colony page's own controls use.
document.getElementById('chat-colony-fit')?.addEventListener('click', ()=>{
  if(typeof colonyResetView==='function') colonyResetView();
  if(typeof topologyRemeasure==='function') topologyRemeasure();
});
// Escape closes the layer — but never out from under a real modal, which owns the key first.
document.addEventListener('keydown', e=>{
  if(e.key!=='Escape'||!chatColonyOpen) return;
  if(!document.getElementById('page-chat')?.classList.contains('active')) return;
  if(document.querySelector('.ui-modal-ov')||document.getElementById('result-overlay')?.classList.contains('show')) return;
  chatToggleColony(false);
});
document.getElementById('chat-input')?.addEventListener('keydown', e=>{
  if(e.key==='Enter' && !e.shiftKey){ e.preventDefault(); chatSend(); return; }
  // v0.3.8.42 (§13): Up-arrow recalls the last submitted message — only when the composer is
  // EMPTY, so cursor movement inside a draft is never hijacked.
  if(e.key==='ArrowUp' && chatLastSent && !e.target.value){
    e.preventDefault(); e.target.value=chatLastSent;
    e.target.dispatchEvent(new Event('input'));   // reuse the auto-grow handler
    e.target.setSelectionRange(e.target.value.length, e.target.value.length);
  }
});
// v0.3.8.42 (§13): the open conversation stays current on its own. The fingerprint inside
// chatOpen makes an unchanged poll cost nothing and destroy nothing (selection, scroll), so this
// is safe at a chat-appropriate cadence. Only runs while the Chat page is actually on screen.
setInterval(()=>{
  if(!chatActiveId) return;
  if(!document.getElementById('page-chat')?.classList.contains('active')) return;
  chatOpen(chatActiveId);
}, 4000);
// Grow with the message rather than scrolling a one-line box — a multi-step request is normal here.
document.getElementById('chat-input')?.addEventListener('input', e=>{
  e.target.style.height='auto';
  e.target.style.height=Math.min(e.target.scrollHeight,180)+'px';
});

/* ── Projects & Tools as destinations (v0.3.8.40) ─────────────────────────────────────────────
 *
 * Both read the same endpoints their dashboard widgets read. The widgets stay; this is about
 * REACHABILITY, not duplication — DEFAULT_DASHBOARD_VIEW ships both hidden, so until now an
 * operator had to know they existed and enable them before they could see either.
 */
async function loadProjects(){
  const el=document.getElementById('projects-list'); if(!el) return;
  try{
    const r=await api('/workspaces');
    if(!r||!r.success){ el.innerHTML=`<div class="hud-state err">${escapeHtml((r&&r.message)||'Could not load workspaces.')}</div>`; return; }
    const ws=(r.data&&r.data.workspaces)||r.data||[];
    if(!ws.length){ el.innerHTML='<div class="hud-state">No workspaces yet — the colony creates one the first time a mission needs to change files.</div>'; return; }
    // v0.3.8.55 (field report): the MISSION GOAL is the checkout's human name; the GUID demotes
    // to the detail line. A workspace whose mission is gone keeps the id — better a true GUID
    // than an invented title.
    el.innerHTML=ws.map(w=>`<div class="card agentcli-card">
      <div class="agentcli-hd">
        <span class="health-dot ${w.usable?'ok':''}"></span>
        <span class="agentcli-name">${escapeHtml(w.mission_goal||w.mission_id||w.id||'workspace')}</span>
        <span class="hud-badge ${w.state==='live'?'active':''}">${escapeHtml(w.state||'')}</span>
        ${w.deletable?'<span class="hud-badge">can be cleaned up</span>':''}
      </div>
      <div class="agentcli-sub">${w.mission_goal?escapeHtml(String(w.mission_id||w.id||'').slice(0,8))+' · ':''}${escapeHtml(w.mode||'')}${w.branch?' · '+escapeHtml(w.branch):''}${w.base_revision?' · based on '+escapeHtml(String(w.base_revision).slice(0,10)):''}</div>
    </div>`).join('');
  }catch(e){ el.innerHTML=`<div class="hud-state err">${escapeHtml(e.message||'')}</div>`; }
}

/* v0.3.8.42 (§5): ONE Tools implementation. This page previously carried its own thin summary of
 * /tools while the dashboard widget carried the real one — fitness alarms, broken-tool reasons,
 * operator-tool registration. Two renderers of one endpoint is how they drift; the page now calls
 * the same renderToolsPanel the widget uses, plus the full inventory a destination page owes. */
async function loadToolsView(){
  const el=document.getElementById('toolsview-body'); if(!el) return;
  try{
    const r=await api('/tools');
    if(!r||!r.success){ el.innerHTML=`<div class="hud-state err">${escapeHtml((r&&r.message)||'Could not load tools.')}</div>`; return; }
    // Both branches fixed this page independently: theirs taught it the registered/planned/
    // rejected/blocked distinctions the widget always had; ours consolidated it ONTO the widget's
    // renderer so the two cannot drift again. The consolidation is the stronger form of the same
    // fix, and the one v0.3.8.41 insight it lacked — an unresolved route is not an unfit model —
    // now lives in renderToolsPanel, where both surfaces get it.
    renderToolsPanel(r.data||{}, 'toolsview-body', {inventory:true});
  }catch(e){ el.innerHTML=`<div class="hud-state err">${escapeHtml(e.message||'')}</div>`; }
}

PAGE_ENTER['projects']=()=>{
  loadProjectCards(); loadProjects();
  // v0.3.8.55: the Automation panel rides the right-hand column — admin only, same visibility
  // the standalone page had ('vis:admin' in the old IA entry).
  const auto=document.getElementById('projects-col-auto');
  if(auto) auto.hidden=(ROLE!=='admin');
  if(ROLE==='admin' && typeof openAutonomy==='function') openAutonomy();
};

/* v0.3.8.48 — the project workspace. One project, whole: Chat (its conversations), Schedules
 * (the real subsystem), History (conversations + their missions), Settings (name, purpose, path,
 * attributed default approval, archive). Deep-linked at #/projects/{id}; reload-safe. */
var projectViewId=null;
let pvProject=null;
PAGE_ENTER['projectview']=()=>{ if(projectViewId) loadProjectView(); };

/* v0.3.8.48 — Integrations: the real catalog, honestly ordered. Configured providers first with
 * their verify state, unconfigured second as "available", installed agents as integrations (they
 * are — see the directive), and the homelab as one card into its own deck. Nothing invented. */
PAGE_ENTER['integrations']=()=>loadIntegrations();
async function loadIntegrations(){
  const host=document.getElementById('int-body'); if(!host) return;
  // v0.3.8.56 (field report): the hidden Coding Agents page is GONE — its cards render HERE,
  // populating each agent integration's own row with the full story the page used to tell:
  // installed state, capability badge, the exact remedy when missing, Install, Docs.
  const [cat,conn,ag]=await Promise.all([api('/providers/catalog'),api('/providers'),api('/agents')]);
  const catalog=(cat&&cat.data)||[];
  const conns=(conn&&conn.data)||[];
  const byId=Object.fromEntries(conns.map(c=>[c.provider,c]));
  const agentsData=(ag&&ag.success&&ag.data)||{};
  const agentsById=Object.fromEntries((agentsData.agents||[]).map(a=>[a.id,a]));
  const row=(p)=>{
    const c=byId[p.provider]||{};
    const configured=!!(c.configured||p.agent&&p.installed);
    const status=p.agent
      ? (p.installed?'installed':'not installed')
      : c.configured
        ? (c.last_verify_ok===true?'verified':c.last_verify_ok===false?'failing':'configured — not yet tested')
        : 'not configured';
    return {p,c,configured,status};
  };
  const rows=catalog.map(row).sort((a,b)=>(b.configured?1:0)-(a.configured?1:0));
  const agentRow=({p})=>{
    const a=agentsById[p.provider];
    if(!a) return null;   // catalogued but unknown to /agents — fall through to the thin row
    const installed=!!a.installed, d=agentsData;
    return `<div class="card agentcli-card${installed?'':' int-avail'}" style="margin-bottom:6px;">
      <div class="agentcli-hd">
        <span class="health-dot ${installed?'ok':''}"></span>
        <span class="agentcli-name">${escapeHtml(a.name||a.id)}</span>
        <span class="agentcli-vendor">${escapeHtml(a.vendor||'')}</span>
        ${installed?`<span class="hud-badge completed">Installed${a.version?' · '+escapeHtml(a.version):''}</span>`
                   :`<span class="hud-badge">Not installed</span>`}
        ${a.writes?`<span class="hud-badge pending" title="This agent edits files and runs commands. The colony confines it to a mission workspace.">Can make changes</span>`:''}
      </div>
      ${installed
        ? `<div class="agentcli-sub">Sign in once in your own terminal if you have not already:
             <code>${escapeHtml(a.auth_command||'')}</code></div>`
        : `<div class="agentcli-sub">${escapeHtml(a.unavailable_reason||'Not found on this machine.')}</div>`}
      <div class="agentcli-actions">
        ${installed?'':(d.install_enabled
          ? `<button class="btn btn-primary agentcli-install" data-agent="${escapeHtml(a.id)}">Install</button>`
          : `<button class="btn" disabled title="${escapeHtml(d.install_disabled_reason||'')}">Install unavailable</button>`)}
        <a class="btn btn-ghost" href="${escapeHtml(a.docs_url||'#')}" target="_blank" rel="noopener noreferrer">Docs</a>
      </div>
    </div>`;
  };
  const localCard=(()=>{
    const lo=agentsData.local; if(!lo) return '';
    const loInstalled=!!lo.installed, d=agentsData;
    return `<div class="card agentcli-card${loInstalled?'':' int-avail'}" style="margin-bottom:6px;">
      <div class="agentcli-hd">
        <span class="health-dot ${loInstalled?'ok':''}"></span>
        <span class="agentcli-name">${escapeHtml(lo.name||'Local models')}</span>
        <span class="agentcli-vendor">${escapeHtml(lo.vendor||'')}</span>
        ${loInstalled?`<span class="hud-badge completed">Installed${lo.version?' · '+escapeHtml(lo.version):''}</span>`
                     :`<span class="hud-badge">Not installed</span>`}
      </div>
      ${loInstalled
        ? `<div class="agentcli-sub">Running locally — pull a model to route ants to it: <code>ollama pull llama3.1:8b</code></div>`
        : `<div class="agentcli-sub">${escapeHtml(lo.unavailable_reason||'Not found on this machine.')}${
             lo.install_supported?'':' Install it yourself: '}${
             lo.install_supported?'':`<code>${escapeHtml(lo.install_command||'')}</code>`}</div>`}
      <div class="agentcli-actions">
        ${loInstalled?'':(d.install_enabled&&lo.install_supported
          ? `<button class="btn btn-primary agentcli-install-local">Install</button>`
          : '')}
        <a class="btn btn-ghost" href="${escapeHtml(lo.docs_url||'#')}" target="_blank" rel="noopener noreferrer">Docs</a>
      </div>
    </div>`;
  })();
  host.innerHTML=(agentsData.install_dir
      ? `<div class="agentcli-where">Agents install into ${escapeHtml(agentsData.install_dir)} — no administrator rights needed, and removing that folder uninstalls them.</div>`:'')
    +localCard
    +rows.map(r=>{
    const {p,c,configured,status}=r;
    if(p.agent){ const card=agentRow(r); if(card) return card; }
    return `<div class="card${configured?'':' int-avail'}" style="margin-bottom:6px;">
    <div style="padding:11px 13px;display:flex;align-items:center;gap:10px;flex-wrap:wrap;">
      <b style="color:var(--text);font-size:12px;">${escapeHtml(p.name||p.provider)}</b>
      <span class="sch-badge">${escapeHtml(p.kind||'provider')}</span>
      <span class="sch-badge" style="${status==='verified'||status==='installed'?'color:var(--green,#34d399)':status==='failing'?'color:var(--red,#f87171)':''}">${escapeHtml(status)}</span>
      ${c.last_verified_at?`<span style="font-size:9px;color:var(--dim)">last test ${escapeHtml(chatTurnTime(c.last_verified_at)||'')}</span>`:''}
      ${c.last_verify_message&&c.last_verify_ok===false?`<span style="font-size:9px;color:var(--red,#f87171)">${escapeHtml(String(c.last_verify_message).slice(0,80))}</span>`:''}
      <span style="flex:1"></span>
      ${!p.agent?`<button class="btn btn-ghost" data-int-cfg="${escapeHtml(p.provider)}">Configure</button>`:''}
      ${!p.agent&&c.configured?`<button class="btn btn-ghost" data-int-test="${escapeHtml(p.provider)}">Test connection</button>`:''}
    </div>
    ${!p.agent?`<div class="int-cfg-slot" data-cfg-slot="${escapeHtml(p.provider)}" hidden style="padding:0 13px 13px;"></div>`:''}
    </div>`;}).join('')
    +`<div class="card" style="margin-top:10px;"><div style="padding:11px 13px;display:flex;align-items:center;gap:10px;">
       <b style="color:var(--text);font-size:12px;">Homelab</b>
       <span class="sch-badge">infrastructure</span>
       <span style="font-size:10px;color:var(--muted);flex:1;">Proxmox, containers, storage, networking and automation — managed in its own deck.</span>
       <button class="btn btn-ghost" data-int-hl>Open</button></div></div>`;
  // The agent install buttons, wired the way the retired page wired them.
  host.querySelectorAll('.agentcli-install').forEach(btn=>
    btn.addEventListener('click',()=>installAgentCli(btn.dataset.agent,btn)));
  host.querySelector('.agentcli-install-local')?.addEventListener('click',async function(){
    const btn=this;
    if(!await uiConfirm('Install Ollama on this machine?\n\nIt runs Windows’ own package manager '
      + '(winget) here. Models you pull afterwards run entirely on this machine.')) return;
    btn.disabled=true; btn.textContent='Installing…';
    agentCliMsg('Installing — a download of this size can take a few minutes.', true);
    try{
      const r=await api('/agents/local/install','POST');
      agentCliMsg((r&&r.message)||(r&&r.success?'Installed.':'Install failed.'), !!(r&&r.success));
    }catch(e){ agentCliMsg('Install failed: '+(e.message||''), false); }
    finally{ apiCacheBust('/agents'); loadIntegrations(); }
  });
  // v0.3.8.50 (field report): Configure was a link to the page it was already on. The provider
  // configuration CARD — the one thing the retired Settings→Providers tab existed for — now opens
  // inline, right under the integration it configures. Same card, same handler, one config path.
  host.querySelectorAll('[data-int-cfg]').forEach(b=>b.addEventListener('click',()=>{
    const provider=b.dataset.intCfg;
    const slot=host.querySelector(`[data-cfg-slot="${CSS.escape(provider)}"]`);
    if(!slot) return;
    if(!slot.hidden){ slot.hidden=true; slot.innerHTML=''; return; }
    const p=catalog.find(x=>x.provider===provider);
    if(!p) return;
    slot.innerHTML=renderProviderCard(p,byId[provider]||{});
    slot.hidden=false;
  }));
  // v0.3.8.56: no more hop to a hidden agents page — the cards above ARE that page's content.
  host.querySelector('[data-int-hl]')?.addEventListener('click',()=>showPage('homelab',{noHistory:false}));
  host.querySelectorAll('[data-int-test]').forEach(b=>b.addEventListener('click',async ()=>{
    b.disabled=true; b.textContent='Testing…';
    const r=await api('/providers/'+encodeURIComponent(b.dataset.intTest)+'/test','POST',{},30000);
    b.disabled=false; b.textContent='Test connection';
    chatSetState&&0; loadIntegrations();
    const note=document.createElement('div'); note.className='hud-state';
    note.textContent=(r&&r.message)||'Test finished.';
    host.prepend(note); setTimeout(()=>note.remove(),4000);
  }));
}
if(!window.__intReloadWired){ window.__intReloadWired=true;
  document.getElementById('int-reload')?.addEventListener('click',()=>loadIntegrations()); }

async function loadProjectView(){
  const r=await api('/projects/'+encodeURIComponent(projectViewId));
  if(!(r&&r.success&&r.data)){ setEl('pv-name','Project not found'); setEl('pv-sub',(r&&r.message)||''); return; }
  pvProject=r.data;
  setEl('pv-name', pvProject.name||'Untitled project');
  // innerHTML, not setEl: the folder GLYPH is inline SVG, and the E2E sweep caught setEl's
  // textContent printing it as literal "<svg …>" prose. Operator data is escaped; the icon is not.
  const pvSub=document.getElementById('pv-sub');
  if(pvSub) pvSub.innerHTML=[
    pvProject.path?`${GLYPH.folder} ${escapeHtml(pvProject.path)}`:null,
    escapeHtml((pvProject.conversations||[]).length+' conversation(s)'),
    escapeHtml(pvProject.schedule_count+' schedule(s)'),
    pvProject.archived?'archived':null,
  ].filter(Boolean).join(' · ');
  pvRenderChat(); pvRenderObjectives(); pvRenderSchedules(); pvRenderRules(); pvRenderHistory(); pvFillSettings();
}

function pvTab(name){
  document.querySelectorAll('.pv-tab').forEach(t=>t.classList.toggle('active',t.dataset.tab===name));
  document.querySelectorAll('.pv-body').forEach(b=>b.hidden=b.id!=='pv-body-'+name);
}
document.querySelectorAll('.pv-tab').forEach(t=>t.addEventListener('click',()=>pvTab(t.dataset.tab)));
document.getElementById('pv-back')?.addEventListener('click',()=>go('/projects'));
// v0.3.8.52 (field report): the button CREATES the conversation, immediately and in THIS
// project — it used to only flip the chat page into composing mode, which looked like a plain
// navigation ("just takes us back to the chat page"). The conversation is born untitled and
// appears in the tracker at once; the server names it from the first thing said in it.
document.getElementById('pv-new-conv')?.addEventListener('click',async ()=>{
  const btn=document.getElementById('pv-new-conv'); if(btn) btn.disabled=true;
  try{
    const c=await api('/conversations','POST',{ project_id: projectViewId });
    if(!(c&&c.success&&c.data)){ chatSetState((c&&c.message)||'Could not start a conversation.'); return; }
    chatPendingProjectId=null; chatComposingNew=false;
    go('/chat'); chatOpen(c.data.id); loadChat();
    document.getElementById('chat-input')?.focus();
  }finally{ if(btn) btn.disabled=false; }
});

function pvRenderChat(){
  const host=document.getElementById('pv-body-chat'); if(!host) return;
  const convs=pvProject.conversations||[];
  host.innerHTML = convs.length
    ? convs.map(c=>`<div class="card" style="margin-bottom:6px;cursor:pointer;" data-open="${escapeHtml(c.id)}">
        <div style="padding:10px 13px;display:flex;align-items:center;gap:9px;">
          ${c.pinned?`<span title="Pinned">${GLYPH.starFill}</span>`:''}
          <b style="color:var(--text);font-size:12px;flex:1;overflow:hidden;text-overflow:ellipsis;">${escapeHtml(c.title||'Conversation')}</b>
          ${(c.mission_ids||[]).length?`<span class="sch-badge">${c.mission_ids.length} mission(s)</span>`:''}
          ${c.cancelled?'<span class="sch-badge">stopped</span>':''}
          <span style="font-size:10px;color:var(--dim)">${escapeHtml(chatTurnTime(c.updated_at)||'')}</span>
        </div></div>`).join('')
    : '<div class="hud-state">No conversations yet — start the first one above.</div>';
  host.querySelectorAll('[data-open]').forEach(el=>el.addEventListener('click',()=>{
    chatComposingNew=false; go('/chat'); chatOpen(el.dataset.open);
  }));
}

/* v0.3.8.48 — the project's objectives: created HERE, in the owning project, exactly as the
 * directive orders. The global board is the aggregate view; this is where they are born. */
async function pvRenderObjectives(){
  const host=document.getElementById('pv-obj-list'); if(!host) return;
  const r=await api('/objectives');
  const mine=((r&&r.data)||[]).filter(o=>o.project_id===projectViewId);
  host.innerHTML=mine.length?mine.map(o=>`<div class="card" style="margin-bottom:6px;"><div style="padding:9px 13px;font-size:11px;">
      <b style="color:var(--text)">${escapeHtml(o.title||'')}</b>
      <span class="sch-badge" style="margin-left:7px;">${escapeHtml(o.status||'')}</span>
      <span class="sch-badge">runs ${escapeHtml(String(o.run_count||0))}${o.max_runs?'/'+escapeHtml(String(o.max_runs)):''}</span>
      ${o.last_run_at?`<span style="font-size:9px;color:var(--dim);margin-left:6px;">last ${escapeHtml(chatTurnTime(o.last_run_at)||'')}</span>`:''}
      <div style="font-size:10px;color:var(--muted);margin-top:3px;">${escapeHtml((o.charter||'').slice(0,140))}</div>
    </div></div>`).join('')
    :'<div class="hud-state">No objectives in this project yet.</div>';
}
if(!window.__pvObjWired){ window.__pvObjWired=true;
  document.getElementById('pv-obj-create')?.addEventListener('click', async ()=>{
    const btn=document.getElementById('pv-obj-create'); btn.disabled=true;
    const r=await api('/objectives','POST',{
      title:document.getElementById('pv-obj-title').value.trim(),
      charter:document.getElementById('pv-obj-charter').value.trim(),
      max_runs:+document.getElementById('pv-obj-maxruns').value||0,
      project_id:projectViewId,
    });
    btn.disabled=false;
    const msg=document.getElementById('pv-obj-msg');
    if(msg){ msg.textContent=(r&&r.message)||'Create failed.'; msg.className='save-msg '+(r&&r.success?'text-green':'text-red'); }
    if(r&&r.success){ ['pv-obj-title','pv-obj-charter'].forEach(id=>{const e=document.getElementById(id); if(e) e.value='';}); pvRenderObjectives(); }
  });
}

/* v0.3.8.48 — rules, quarantined honestly. The deterministic homelab rules predate project
 * ownership; the directive forbids silently attaching them to any project, so they appear here
 * as what they are: legacy triggers with no project owner, managed in the homelab deck until
 * each is claimed. No global Automation domain remains. */
async function pvRenderRules(){
  const host=document.getElementById('sch-rules'); if(!host) return;
  const r=await api('/homelab/automation/rules').catch(()=>null);
  const rules=(r&&r.success&&(r.data&&r.data.rules||r.data))||[];
  host.innerHTML=(Array.isArray(rules)&&rules.length)
    ? `<div class="sub" style="margin-bottom:6px;">Legacy rules with no project owner — quarantined, not attached. Manage them in the homelab deck until each is claimed by a project.</div>`
      + rules.slice(0,20).map(x=>`<div class="card" style="margin-bottom:5px;"><div style="padding:8px 12px;font-size:10px;color:var(--muted);">
          <b style="color:var(--text)">${escapeHtml(x.name||x.id||'rule')}</b>
          <span class="sch-badge" style="margin-left:6px;">${x.enabled?'enabled':'paused'}</span>
          <span class="sch-badge">no project owner</span>
        </div></div>`).join('')
    : '<div class="hud-state">No event-trigger rules exist yet.</div>';
}

async function pvRenderSchedules(){
  const host=document.getElementById('sch-list'); if(!host) return;
  const r=await api('/projects/'+encodeURIComponent(projectViewId)+'/schedules');
  const list=(r&&r.data&&r.data.schedules)||[];
  host.innerHTML = list.length
    ? list.map(sc=>`<div class="card" style="margin-bottom:6px;"><div style="padding:10px 13px;">
        <div class="sch-row">
          <b style="color:var(--text);font-size:12px;">${escapeHtml(sc.name)}</b>
          <span class="sch-badge">${escapeHtml(sc.trigger)}${sc.local_time?' '+escapeHtml(sc.local_time):''} ${escapeHtml(sc.timezone||'')}</span>
          <span class="sch-badge">${escapeHtml(sc.approval_mode==='ask'?'Manual approval':sc.approval_mode==='autoapprove'?'Automatically approve':'Skip all approvals')}</span>
          <span style="flex:1"></span>
          <span style="font-size:10px;color:var(--dim)">next: ${sc.next_run_at?escapeHtml(chatTurnTime(sc.next_run_at)):'—'} · last: ${sc.last_run_at?escapeHtml(chatTurnTime(sc.last_run_at)):'never'}</span>
        </div>
        <div style="display:flex;gap:7px;margin-top:7px;flex-wrap:wrap;">
          <button class="btn btn-ghost" data-sch-run="${escapeHtml(sc.id)}">Run now</button>
          <button class="btn btn-ghost" data-sch-toggle="${escapeHtml(sc.id)}" data-en="${sc.enabled?'1':'0'}">${sc.enabled?'Pause':'Resume'}</button>
          <button class="btn btn-ghost" data-sch-runs="${escapeHtml(sc.id)}">Past runs</button>
          <button class="btn btn-ghost" data-sch-del="${escapeHtml(sc.id)}">Delete</button>
        </div>
        <div data-sch-runlist="${escapeHtml(sc.id)}" hidden style="margin-top:7px;"></div>
      </div></div>`).join('')
    : '<div class="hud-state">No schedules yet.</div>';
  host.querySelectorAll('[data-sch-run]').forEach(b=>b.addEventListener('click',async ()=>{
    b.disabled=true; b.textContent='Running…';
    const r2=await api('/schedules/'+b.dataset.schRun+'/run','POST',{},180000);
    setEl('sch-msg',(r2&&r2.message)||'Run failed.');
    b.disabled=false; b.textContent='Run now'; pvRenderSchedules(); loadProjectView();
  }));
  host.querySelectorAll('[data-sch-toggle]').forEach(b=>b.addEventListener('click',async ()=>{
    b.disabled=true;
    await api('/schedules/'+b.dataset.schToggle,'PATCH',{enabled:b.dataset.en!=='1'});
    pvRenderSchedules();
  }));
  host.querySelectorAll('[data-sch-del]').forEach(b=>b.addEventListener('click',async ()=>{
    b.disabled=true;
    const r2=await api('/schedules/'+b.dataset.schDel,'DELETE');
    setEl('sch-msg',(r2&&r2.message)||''); pvRenderSchedules();
  }));
  host.querySelectorAll('[data-sch-runs]').forEach(b=>b.addEventListener('click',async ()=>{
    const box=host.querySelector(`[data-sch-runlist="${b.dataset.schRuns}"]`);
    if(!box.hidden){ box.hidden=true; return; }
    const r2=await api('/schedules/'+b.dataset.schRuns+'/runs');
    const runs=(r2&&r2.data&&r2.data.runs)||[];
    box.hidden=false;
    box.innerHTML=runs.length?runs.map(x=>`<div style="font-size:10px;color:var(--muted);padding:2px 0;">
      ${escapeHtml(chatTurnTime(x.started_at)||'')} · <b>${escapeHtml(x.status)}</b> (${escapeHtml(x.trigger)})
      ${x.conversation_id?`· <a href="#/chat" data-run-open="${escapeHtml(x.conversation_id)}">open conversation</a>`:''}
      ${x.summary?` — ${escapeHtml(String(x.summary).slice(0,90))}`:''}</div>`).join('')
      :'<div class="hud-state">No runs yet.</div>';
    box.querySelectorAll('[data-run-open]').forEach(a=>a.addEventListener('click',e=>{
      e.preventDefault(); chatComposingNew=false; go('/chat'); chatOpen(a.dataset.runOpen);
    }));
  }));
}

document.getElementById('sch-trigger')?.addEventListener('change',e=>{
  const v=e.target.value;
  document.getElementById('sch-cron').hidden=v!=='cron';
  document.getElementById('sch-once').hidden=v!=='once';
  document.getElementById('sch-time').hidden=!(v==='daily'||v==='weekdays'||v==='weekly'||v==='hourly');
});
document.getElementById('sch-create')?.addEventListener('click',async ()=>{
  const btn=document.getElementById('sch-create'); btn.disabled=true;
  const trig=document.getElementById('sch-trigger').value;
  const onceLocal=document.getElementById('sch-once').value;
  const r=await api('/projects/'+encodeURIComponent(projectViewId)+'/schedules','POST',{
    name:document.getElementById('sch-name').value.trim(),
    prompt:document.getElementById('sch-prompt').value.trim(),
    trigger:trig,
    local_time:document.getElementById('sch-time').value.trim()||null,
    timezone:document.getElementById('sch-tz').value.trim()||Intl.DateTimeFormat().resolvedOptions().timeZone,
    cron:document.getElementById('sch-cron').value.trim()||null,
    one_time_at:onceLocal?new Date(onceLocal).toISOString():null,
    approval_mode:document.getElementById('sch-approval').value,
  });
  btn.disabled=false;
  const msg=document.getElementById('sch-msg');
  if(msg){ msg.textContent=(r&&r.message)||'Create failed.'; msg.className='save-msg '+(r&&r.success?'text-green':'text-red'); }
  if(r&&r.success){ ['sch-name','sch-prompt'].forEach(id=>{const e2=document.getElementById(id); if(e2) e2.value='';}); pvRenderSchedules(); }
});

async function pvRenderHistory(){
  const host=document.getElementById('pv-body-history'); if(!host) return;
  const ids=(pvProject.conversations||[]).flatMap(c=>c.mission_ids||[]);
  const hist=await api('/missions/json?limit=100');
  const all=((hist&&hist.data)||[]).filter(m=>ids.includes(String(m.id)));
  host.innerHTML = (pvProject.conversations||[]).length
    ? `<div class="sub" style="margin:8px 0;">Everything this project has done — its conversations and the missions they started.</div>`
      + all.map(m=>`<div class="card" style="margin-bottom:6px;cursor:pointer;" data-mission="${escapeHtml(String(m.id))}"><div style="padding:9px 13px;font-size:11px;color:var(--muted);">
          <b style="color:var(--text)">${escapeHtml((m.goal||'').slice(0,90))}</b>
          <span class="sch-badge" style="margin-left:7px;">${escapeHtml(m.status||'')}</span>
          ${m.success_score!=null?`<span class="sch-badge">score ${escapeHtml(String(m.success_score))}</span>`:''}
          <div class="pv-mission-detail" hidden style="margin-top:7px;font-size:10px;white-space:pre-wrap;"></div>
        </div></div>`).join('')
      + (all.length?'':'<div class="hud-state">No missions yet.</div>')
    : '<div class="hud-state">Nothing yet.</div>';
  // v0.3.8.48: mission detail INLINE — the task trail and result open in place, no legacy page.
  host.querySelectorAll('[data-mission]').forEach(card=>card.addEventListener('click', async ()=>{
    const box=card.querySelector('.pv-mission-detail');
    if(!box.hidden){ box.hidden=true; return; }
    box.hidden=false; box.textContent='Loading…';
    // Found live: /missions/{id} is not a JSON surface — /missions/{id}/report is, and it is the
    // same report the mission thread reads. final_output is the answer; the tasks are the trail.
    const d=await api('/missions/'+encodeURIComponent(card.dataset.mission)+'/report').catch(()=>null);
    const det=(d&&d.success&&d.data)||{};
    box.textContent=[
      det.final_output||det.raw_output||'',
      (det.tasks||[]).map(t=>`• ${t.title||''} — ${t.status||''}`).join('\n'),
    ].filter(Boolean).join('\n\n')||'No detail recorded.';
  }));
}

function pvFillSettings(){
  const g=id=>document.getElementById(id);
  if(g('pv-edit-name')) g('pv-edit-name').value=pvProject.name||'';
  if(g('pv-edit-desc')) g('pv-edit-desc').value=pvProject.description_md||'';
  if(g('pv-edit-path')) g('pv-edit-path').value=pvProject.path||'';
  if(g('pv-edit-policy')) g('pv-edit-policy').value=pvProject.default_policy||'ask';
  if(g('pv-archive')) g('pv-archive').textContent=pvProject.archived?'Unarchive':'Archive';
  pvLoadGrants();
}

/* v0.3.8.51 (field report) — directory gates: list, open, close. Every row says who opened it
 * and when, because a standing grant without a name on it is the pattern this product refuses. */
async function pvLoadGrants(){
  const host=document.getElementById('pv-grants'); if(!host||!projectViewId) return;
  const r=await api('/projects/'+encodeURIComponent(projectViewId)+'/grants');
  const grants=(r&&r.success&&r.data&&r.data.grants)||[];
  host.innerHTML=grants.length
    ? grants.map(gr=>`<div class="card" style="margin-bottom:4px;"><div style="padding:8px 12px;display:flex;align-items:center;gap:9px;">
        <span style="font-family:var(--mono);font-size:11px;color:var(--text);flex:1;overflow:hidden;text-overflow:ellipsis;">${GLYPH.folder} ${escapeHtml(gr.path)}</span>
        <span style="font-size:9px;color:var(--dim);">opened by ${escapeHtml(gr.granted_by||'?')}</span>
        <button class="btn btn-ghost" data-close-gate="${escapeHtml(gr.id)}" style="font-size:10px;">Close gate</button>
      </div></div>`).join('')
    : '<div class="hud-state">No gates open — this project\'s colony reaches only its own workspace.</div>';
  host.querySelectorAll('[data-close-gate]').forEach(b=>b.addEventListener('click',async ()=>{
    b.disabled=true;
    await api('/projects/'+encodeURIComponent(projectViewId)+'/grants/'+encodeURIComponent(b.dataset.closeGate),'DELETE');
    pvLoadGrants();
  }));
}
document.getElementById('pv-grant-open')?.addEventListener('click',async ()=>{
  const inp=document.getElementById('pv-grant-path'), msg=document.getElementById('pv-grant-msg');
  const path=(inp.value||'').trim(); if(!path) return;
  const r=await api('/projects/'+encodeURIComponent(projectViewId)+'/grants','POST',{path});
  msg.style.color=r&&r.success?'var(--green)':'var(--red)';
  msg.textContent=(r&&r.message)||'Failed';
  setTimeout(()=>{ if(msg.textContent) msg.textContent=''; },5000);
  if(r&&r.success){ inp.value=''; pvLoadGrants(); }
});
document.getElementById('pv-save')?.addEventListener('click',async ()=>{
  const btn=document.getElementById('pv-save'); btn.disabled=true;
  const pol=document.getElementById('pv-edit-policy').value;
  // Skip all approvals is a real decision — confirmed in words, never a silent dropdown change.
  if(pol==='bypass' && pvProject.default_policy!=='bypass'
     && !confirm('Skip all approvals: the colony will act on this project without asking you first. '
       +'Security gates, workspace boundaries and verification still apply. Continue?')){
    btn.disabled=false; return;
  }
  const r=await api('/projects/'+encodeURIComponent(projectViewId),'PATCH',{
    name:document.getElementById('pv-edit-name').value.trim(),
    description_md:document.getElementById('pv-edit-desc').value,
    path:document.getElementById('pv-edit-path').value.trim(),
    default_policy:pol,
  });
  btn.disabled=false;
  const msg=document.getElementById('pv-save-msg');
  if(msg){ msg.textContent=(r&&r.message)||'Save failed.'; msg.className='save-msg '+(r&&r.success?'text-green':'text-red'); }
  if(r&&r.success) loadProjectView();
});
document.getElementById('pv-archive')?.addEventListener('click',async ()=>{
  const btn=document.getElementById('pv-archive'); btn.disabled=true;
  const r=await api('/projects/'+encodeURIComponent(projectViewId),'PATCH',{archived:!pvProject.archived});
  btn.disabled=false;
  if(r&&r.success) loadProjectView();
});

/* v0.3.8.47 — real Projects. One is created with every new conversation; here they are made by
 * hand: a name, a markdown purpose that travels with every message in the project, an optional
 * working-directory path. Claude-projects shaped, ANTHILL rules: the purpose is context the
 * model SEES, the path is recorded and shown to it — no surface claims wiring that isn't built. */
async function loadProjectCards(){
  const host=document.getElementById('project-cards'); if(!host) return;
  const r=await api('/projects');
  if(!(r&&r.success)){ host.innerHTML=`<div class="hud-state err">${escapeHtml((r&&r.message)||'Could not load projects.')}</div>`; return; }
  const list=(r.data&&r.data.projects)||[];
  if(!list.length){ host.innerHTML='<div class="hud-state">No projects yet — your first conversation creates one, or make one here.</div>'; return; }
  host.innerHTML=list.map(p=>`<div class="card project-card${p.archived?' archived':''}" data-project="${escapeHtml(p.id)}" style="margin-bottom:8px;cursor:pointer;">
    <div style="padding:11px 13px;">
      <div style="display:flex;align-items:center;gap:8px;">
        <b style="color:var(--text);font-size:13px;">${escapeHtml(p.name||'Untitled project')}</b>
        ${p.archived?'<span class="chat-tok">archived</span>':''}
        <span style="flex:1"></span>
        <span style="font-size:10px;color:var(--dim)">${p.conversations} conversation(s) · ${p.missions} mission(s)</span>
      </div>
      ${p.description_md?`<div style="font-size:11px;color:var(--muted);margin-top:5px;white-space:pre-wrap;">${escapeHtml(String(p.description_md).slice(0,240))}</div>`:''}
      ${p.path?`<div style="font-size:10px;color:var(--dim);margin-top:4px;">${GLYPH.folder} ${escapeHtml(p.path)}</div>`:''}
      <div style="display:flex;gap:8px;margin-top:8px;">
        <button class="btn btn-ghost project-chat">＋ New conversation here</button>
        <button class="btn btn-ghost project-archive">${p.archived?'Unarchive':'Archive'}</button>
      </div>
    </div>
  </div>`).join('');
  host.querySelectorAll('.project-card').forEach(card=>{
    const pid=card.dataset.project;
    // v0.3.8.48 (defect 1): the CARD opens the workspace; the buttons stop propagation below.
    card.addEventListener('click',()=>go('/projects/'+pid));
    card.querySelector('.project-chat')?.addEventListener('click', async e=>{
      e.stopPropagation();
      chatPendingProjectId=pid;   // the first message will create the conversation HERE
      chatActiveId=null; chatComposingNew=true; go('/chat');
      document.getElementById('chat-input')?.focus();
    });
    card.querySelector('.project-archive')?.addEventListener('click', async e=>{
      e.stopPropagation();
      const arch=card.classList.contains('archived');
      await api('/projects/'+encodeURIComponent(pid),'PATCH',{archived:!arch});
      loadProjectCards();
    });
  });
}
// v0.3.8.48 (defect 6): ONE refresh listener. PAGE_ENTER already loads both halves; the button
// calls the same pair once — the duplicate registration that double-fetched /workspaces is gone.
if(!window.__projectsReloadWired){
  window.__projectsReloadWired=true;
  document.getElementById('projects-reload')?.addEventListener('click', ()=>{ loadProjectCards(); loadProjects(); });
}
document.getElementById('project-new-btn')?.addEventListener('click', ()=>{
  const f=document.getElementById('project-new-form'); if(f){ f.hidden=!f.hidden; if(!f.hidden) document.getElementById('project-new-name')?.focus(); }
});
document.getElementById('project-new-cancel')?.addEventListener('click', ()=>{
  const f=document.getElementById('project-new-form'); if(f) f.hidden=true;
});
document.getElementById('project-new-create')?.addEventListener('click', async ()=>{
  const name=document.getElementById('project-new-name')?.value.trim();
  if(!name){ setEl('project-new-msg','A project needs a name.'); return; }
  const r=await api('/projects','POST',{
    name, description_md:document.getElementById('project-new-desc')?.value||'',
    path:document.getElementById('project-new-path')?.value.trim()||null,
  });
  const nm=document.getElementById('project-new-msg');
  if(nm){ nm.textContent=r&&r.success?'Created.':(r&&r.message)||'Create failed.';
          nm.className='save-msg '+(r&&r.success?'text-green':'text-red'); }   // defect 7: red errors
  if(r&&r.success){
    ['project-new-name','project-new-desc','project-new-path'].forEach(id=>{const e=document.getElementById(id); if(e) e.value='';});
    document.getElementById('project-new-form').hidden=true;
    loadProjectCards();
  }
});
PAGE_ENTER['toolsview']=()=>loadToolsView();
document.getElementById('projects-reload')?.addEventListener('click',()=>loadProjects());
document.getElementById('toolsview-reload')?.addEventListener('click',()=>loadToolsView());

/* ── Coding agents (v3.8.39) ──────────────────────────────────────────────────────────────────
 *
 * Installable CLI agents the colony can delegate a turn to. Two facts are shown separately for
 * every one, because they have different remedies and collapsing them prints the wrong
 * instruction: whether it is INSTALLED on this host, and whether the operator has SIGNED IN to it.
 *
 * Anthill never holds the credential. The sign-in command is printed for the operator to run in
 * their own terminal, under their own account, exactly as they would without Anthill — so there is
 * nothing here to store, refresh or leak.
 */
// v0.3.8.56: the agents page is gone; install progress reports as a note atop Integrations.
function agentCliMsg(text, ok){
  const host=document.getElementById('int-body'); if(!host||!text) return;
  const note=document.createElement('div');
  note.className='hud-state'+(ok?'':' err');
  note.textContent=text;
  host.prepend(note); setTimeout(()=>note.remove(),6000);
}

/**
 * Keep the provider catalogue for AGENT_LABEL, so the header chip can say "Claude Code" rather
 * than the wire id `agent:claude-code`. Refreshed alongside the agents page rather than fetched
 * separately: one more poll for a display name would not earn its request.
 */
async function refreshAgentCatalog(){
  try{
    const r=await api('/providers/catalog');
    if(r&&r.success&&Array.isArray(r.data)) lastAgentCatalog=r.data.filter(p=>p.agent);
  }catch(e){ /* the chip falls back to the id, which is still truthful */ }
}

// v0.3.8.56: loadAgentCli is gone with its page — the Integrations rows render the cards.

async function installAgentCli(id, btn){
  const agent=id||'';
  if(!await uiConfirm('Install this agent on this machine?\n\nIt runs the vendor\'s install command '
    + 'here and changes this host. You will still need to sign in to it yourself afterwards.')) return;
  if(btn){ btn.disabled=true; btn.textContent='Installing…'; }
  agentCliMsg('Installing — a package install can take a few minutes.', true);
  try{
    const r=await api('/agents/'+encodeURIComponent(agent)+'/install','POST');
    if(r&&r.success){ agentCliMsg((r.message||'Installed.')+' '+((r.data&&r.data.next_step)||''), true); }
    else{ agentCliMsg((r&&r.message)||'Install failed.', false); }
  }catch(e){ agentCliMsg('Install failed: '+(e.message||''), false); }
  finally{ if(btn){ btn.disabled=false; btn.textContent='Install'; } apiCacheBust('/agents'); if(typeof loadIntegrations==='function') loadIntegrations(); }
}


async function onAntRecentToggle(det){
  if(!det.open || det.dataset.loaded) return;
  det.dataset.loaded='1';
  const box=det.querySelector('.ac-recent');
  try{
    const r=await api('/events/json?limit=12&ant='+encodeURIComponent(det.dataset.ant)); if(!r.success) throw new Error(r.message);
    const evs=(r.data&&r.data.events)||[];
    box.innerHTML = evs.length ? evs.map(e=>{
      const t=(e.event_type||'').replace(/_/g,' ');
      return `<div style="padding:2px 0;border-bottom:1px solid rgba(30,51,84,.4)"><span style="color:var(--dim);font-size:9px">${fmtTime(e.created_at)||''}</span> ${escapeHtml(t)}<div style="color:var(--muted);font-size:9px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${escapeHtml((e.message||'').slice(0,90))}</div></div>`;
    }).join('') : '<div style="color:var(--dim)">No recent events for this caste.</div>';
  }catch(e){ box.innerHTML=`<div style="color:var(--red)">Error: ${escapeHtml(e.message)}</div>`; }
}
document.getElementById('antobs-refresh')?.addEventListener('click',loadAntObs);


// Route a job's "View Result" to the Results page when it has a mission; the overlay stays as
// the quick view for running/queued jobs and legacy jobs without a mission id.
function openJobResult(jobId, missionId){
  if(missionId) openResults(missionId);
  else selectJob(jobId);
}

// Opens the report modal directly for a mission id (used as a fallback quick view).
async function showMissionReport(missionId,label){
  document.getElementById('result-title').textContent=label||'Mission Report';
  const body=document.getElementById('result-body');
  body.textContent='Loading mission report...';
  document.getElementById('result-overlay').classList.add('show');
  const statusBar=document.getElementById('result-status-bar');
  statusBar.innerHTML=`<span>Mission: <span style="font-family:var(--mono)">${escapeHtml(String(missionId)).substring(0,8)}…</span></span>`;
  try{ await renderMissionReport(missionId, body); }
  catch(e){ body.textContent='Could not load the mission report: '+e.message; }
}

document.getElementById('result-close').addEventListener('click',()=>{
  document.getElementById('result-overlay').classList.remove('show');
  selectedJobId=null;
  document.querySelectorAll('.job-item').forEach(el=>el.classList.remove('selected'));
});
document.getElementById('result-refresh-btn').addEventListener('click',()=>{ if(selectedJobId) showJobResult(selectedJobId); });
document.getElementById('copy-btn').addEventListener('click',()=>{
  const text=document.getElementById('result-body').textContent;
  navigator.clipboard.writeText(text).then(()=>{
    const btn=document.getElementById('copy-btn');
    btn.textContent='Copied!';btn.classList.add('copied');
    setTimeout(()=>{btn.textContent='Copy';btn.classList.remove('copied');},2000);
  });
});

async function pollActiveJob(){
  if(!activeJobId) return;
  try{
    const r=await api(`/jobs/${activeJobId}`); if(!r.success) return;
    const j=r.data;
    // v3.8.34: 'escalated' is terminal — without it this poller never stops, the directive box
    // stays disabled, and the operator is locked out waiting for a run that already ended.
    // v0.3.8.42: 'cancelled' and 'timed_out' too — cancelling from the jobs list left this poller
    // running forever and the composer locked, the same defect 'escalated' fixed, one status over.
    // The set now lives in JOB_TERMINAL_STATUSES, keyed to ApiJobRegistry.IsTerminalStatus.
    if(jobIsTerminal(j.status)){
      clearInterval(jobPollTimer);jobPollTimer=null;
      showJobResult(activeJobId);
      activeJobId=null;
    }
  }catch{}
}

// -- Mission dispatch ----------------------------------------------------------
// v0.3.8.42 (§3): dispatch has ONE production caller left — Re-run on the jobs list. The four
// composers that used to feed it are retired; missions start in Chat.
/** v2.17.1: surface the failure instead of silently re-enabling the input. */
function msShowDispatchError(message){
  const box=document.getElementById('ms-error');
  if(box){ box.textContent=message; box.hidden=false; }
  if(typeof pcToast==='function') pcToast(message,false);
}
function msClearDispatchError(){
  const box=document.getElementById('ms-error');
  if(box){ box.textContent=''; box.hidden=true; }
}

/**
 * v2.17.1: returns whether the mission was accepted, so the composer knows whether to clear the
 * textarea or hand the directive back. Previously a rejected dispatch discarded what the operator
 * had typed AND told them nothing — `if(!r.success){enableInput(true);return;}`.
 */
let msDispatchInFlight=false;
async function submitMissionGoal(goal){
  if(!goal) return false;
  if(msDispatchInFlight) return false;   // double-submit guard, kept from the composer era
  msDispatchInFlight=true;
  msClearDispatchError();
  try{
    const r=await api('/missions','POST',{goal});
    if(!r.success){
      msShowDispatchError('Could not dispatch: '+(r.message||'the colony rejected the mission.'));
      return false;
    }
    activeJobId=r.data.id;
    selectedJobId=r.data.id;
    if(jobPollTimer) clearInterval(jobPollTimer);
    jobPollTimer=setInterval(pollActiveJob,2500);
    pollJobs();
    // Show the new mission immediately rather than up to ten seconds later on a cached read.
    if(document.getElementById('page-missions')?.classList.contains('active'))
      loadMissionThread({fresh:true});
    return true;
  }catch(e){
    msShowDispatchError('Could not dispatch: '+((e&&e.message)||'the colony is unreachable.'));
    return false;
  } finally { msDispatchInFlight=false; }
}

// v0.3.8.42 (§3): the plan-preview flow (previewPlan / renderPlan / approveDispatch) retired with
// the dashboard composer it belonged to. POST /missions/plan keeps working and is recorded as a
// UI GAP in ConsoleRouteCoverageTests until Chat grows a preview step — the capability was not
// removed, its only surface was.

// v2.16.0: the conversation loads when the page opens. Job polling keeps it current after that.
// v2.17.1: page entry forces a fresh read (not the 10s cache) so the newest answers are present
// immediately, and re-announces nothing that was already on screen.
PAGE_ENTER['missions']=()=>loadMissionThread({fresh:true});

// -- Helpers -------------------------------------------------------------------
function fmtTime(iso){
  if(!iso) return '';
  try{return new Date(iso).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'});}
  catch{return iso.substring(11,16);}
}

// -- Keyboard shortcuts (v1.8.25 UI Phase 10) -----------------------------------
// Ctrl+K now opens the command palette (it previously jumped to the mission input —
// "New mission" is the top palette action, one Enter away). Ctrl+L unchanged.
let gSeqAt=0; // "g then <key>" two-key page navigation
function typingInField(){
  const el=document.activeElement;
  return el && (el.tagName==='INPUT'||el.tagName==='TEXTAREA'||el.tagName==='SELECT'||el.isContentEditable);
}
document.addEventListener('keydown',e=>{
  if(e.key==='Escape'){
    document.getElementById('result-overlay').classList.remove('show');
    document.getElementById('rename-pop').classList.remove('show');
    closePalette(); closeShortcuts(); closeNotifPanel();
  }
  if((e.ctrlKey||e.metaKey)&&e.key==='k'){ e.preventDefault(); togglePalette(); return; }
  if((e.ctrlKey||e.metaKey)&&e.key==='l'){ e.preventDefault(); showPage('events'); reloadLogModal(); return; }
  if(typingInField()||e.ctrlKey||e.metaKey||e.altKey) return;
  if(e.key==='?'){ e.preventDefault(); openShortcuts(); return; }
  const now=Date.now();
  if(e.key==='g'){ gSeqAt=now; return; }
  if(gSeqAt && now-gSeqAt<900){
    const admin=ROLE==='admin';
    const map={o:'overview',c:'colony',m:'missions',r:'results',e:'events',h:'homelab',
      p:admin?'patches':null,b:admin?'objboard':null,s:admin?'settings':null,u:admin?'users':null,a:admin?'autonomy':null};
    const dest=map[e.key.toLowerCase()];
    if(dest){ e.preventDefault(); showPage(dest); }
    gSeqAt=0;
    return;
  }
  // v2.5.3 R3: while on Homelab, 1-9 / 0 / - jump between category sub-pages.
  if(document.getElementById('page-homelab')?.classList.contains('active')){
    const subMap={'1':'overview','2':'services','3':'virtualization','4':'containers','5':'storage',
      '6':'networking','7':'monitoring','8':'automation','9':'apps','0':'alerts','-':'activity'};
    if(subMap[e.key]){ e.preventDefault(); hlSubShow(subMap[e.key]); }
  }
});

// -- Polling loop --------------------------------------------------------------
function startPolling(){
  loadUiState();
  loadColonyRegistry();
  restoreLayout();          // v1.8.25 saved layout: reopen the last page you were on
  setTimeout(()=>startTour(false),600); // v1.8.25 onboarding tour on first login only
  pollStatus();pollJobs();pollEvents();pollModelInfo();pollGraph();pollApprovals();pollHealth();pollHud();checkForUpdate(false);
  // v0.3.8.52 — event-driven, with the timer demoted to a safety net. See `liveRefresh`.
  //
  // Every interval below used to be the ONLY thing that moved these panels. Each is now the
  // fallback for a stream that is doing the work, so the numbers went up while the console got
  // more responsive: a task event repaints the graph in ~250ms where the 2.5s timer averaged 1.25s.
  liveRefresh(pollStatus, { idleMs: 30000, on: EVT_MISSION });
  liveRefresh(pollJobs,   { idleMs: 20000, on: EVT_JOB });
  // No filter: the Event Log shows every event, so every event is a reason to refresh it.
  liveRefresh(pollEvents, { idleMs: 30000 });
  liveRefresh(pollGraph,  { idleMs: 20000, on: EVT_MISSION }); // the live activity view
  liveRefresh(pollApprovals, { idleMs: 30000, on: EVT_APPROVAL });
  liveRefresh(pollConversations, { idleMs: 30000, on: EVT_MISSION }); // only fetches while Overview is visible
  // Pheromone strengths are recomputed by the engine on its own cadence rather than emitted as
  // events, so this one stays a plain timer — there is no push to key off.
  setInterval(pollColonyPheromones, 15000); // Canvas 2.0 pheromone HUD (only fetches when Colony is visible)
  // v3.7.2: slower on purpose. Tool registration and workspace state change when an operator or a
  // mission acts, not continuously, so polling these at conversation speed would be traffic that
  // buys nothing. Both also return early unless the Overview is on screen.
  setInterval(pollToolsPanel,      20000);
  setInterval(pollWorkspacesPanel, 10000);
  setInterval(pollAttemptsPanel,   10000);
  setInterval(pollHealth,   8000); // Overview system-health panel (only fetches when Overview is visible)
  // The command dashboard summarises mission and autonomy state, so it keys off the same events.
  liveRefresh(pollHud, { idleMs: 30000, on: EVT_MISSION }); // only fetches when Overview is visible
  setInterval(pollModelInfo,15000);
  setInterval(()=>checkForUpdate(false), 6*60*60*1000); // update check is cached server-side (30m)

  // v2.7.0: a backgrounded tab serves cached data (see api()), so a mission that finished while you
  // were away could keep reading as "running" until a slow background tick caught up — looking stuck
  // when it wasn't. On refocus, drop the status-critical caches and repoll at once so what you see is
  // always current the instant you look. Registered here so it attaches exactly once, after login.
  document.addEventListener('visibilitychange',()=>{
    if(document.hidden||!pollingStarted) return;
    ['/status','/jobs','/missions','/providers/health','/autonomy'].forEach(apiCacheBust);
    pollStatus(); pollJobs(); pollHud();
    if(typeof pollActiveJob==='function' && typeof activeJobId!=='undefined' && activeJobId) pollActiveJob();
  });
}

// Bell ? navigate to colony and scroll to approvals card
// v0.3.8.49 (§1): approvals are decided IN Chat — that is the one authoritative approval surface.
// The bell no longer opens a separate Colony approvals card (a competing surface §1 forbids); it
// takes the operator to the conversation that is waiting on them and lets them approve/reject in
// the thread, where the request, the decision and what follows all stay in context.
document.getElementById('approval-bell').addEventListener('click',async ()=>{
  go('/chat');
  try{
    const r=await api('/conversations');
    const convs=(r&&r.data&&r.data.conversations)||[];
    // Prefer a conversation the runtime has flagged as waiting; fall back to the newest.
    let target=convs.find(c=>c.needs_operator||/await|waiting|approv/i.test(String(c.doing||'')));
    if(!target){
      for(const c of convs){
        const d=await api('/conversations/'+encodeURIComponent(c.id));
        if(d&&d.data&&d.data.needs_operator){ target=c; break; }
      }
    }
    if(target){ chatComposingNew=false; chatOpen(target.id); }
  }catch(e){ pollWarnOnce('approvalBell', e); }
});

// ============================================================================
// v1.8.25 — UI Phase 10: Full Command Center Polish
// Command palette + global memory search, notification center, saved layout
// restore, keyboard shortcuts help, onboarding tour. All additive; no page,
// route, or behavior removed (Ctrl+K reassigned to the palette, see keydown).
// ============================================================================

// ---- Command palette --------------------------------------------------------
const PALETTE_PAGES=[
  ['overview','Dashboard',false],['colony','Colony · Topology',false],['missions','Missions',false],
  ['results','Mission Results',false],['events','Activity · Events',false],
  ['patches','Changes & Approvals',true],['objboard','Automation · Objectives',true],
  ['pheromones','Colony · Signals',true],['antconfig','Agents · Configure',true],
  ['antobs','Agents · Inspect',true],['autonomy','Automation · Director',true],['security','Security',true],
  ['shell','Terminal',true],['settings','Settings',true],['users','Users',true],
];
const PALETTE_ACTIONS=[
  ['new-mission','New mission (in Chat)',()=>{ go('/chat'); setTimeout(()=>document.getElementById('chat-input')?.focus(),80); },false],
  ['toggle-nav','Toggle navigation sidebar',()=>document.getElementById('nav-collapse-btn')?.click(),false],
  ['shortcuts','Show keyboard shortcuts',()=>openShortcuts(),false],
  ['tour','Restart onboarding tour',()=>startTour(true),false],
  ['approvals','Open pending approvals',()=>document.getElementById('approval-bell')?.click(),true],
  ['refresh-events','Refresh event feed',()=>pollEvents(),false],
];
let palSel=0, palItems=[], palSearchTimer=null, palSearchRows=[], palLastQ='';
function paletteRecent(){ try{ return JSON.parse(localStorage.getItem('palette-recent')||'[]'); }catch{ return []; } }
function paletteRemember(key){
  try{
    const r=paletteRecent().filter(k=>k!==key); r.unshift(key);
    localStorage.setItem('palette-recent',JSON.stringify(r.slice(0,8)));
  }catch{}
}
function fuzzyScore(q,text){
  q=q.toLowerCase(); text=text.toLowerCase();
  if(!q) return 1;
  if(text.includes(q)) return 100-text.indexOf(q);
  let qi=0;
  for(let i=0;i<text.length&&qi<q.length;i++) if(text[i]===q[qi]) qi++;
  return qi===q.length?10:0;
}
function paletteBuild(q){
  const admin=ROLE==='admin';
  const recents=paletteRecent();
  const rank=k=>{ const i=recents.indexOf(k); return i<0?0:(50-i); };
  const items=[];
  PALETTE_PAGES.filter(([,,adm])=>!adm||admin).forEach(([id,title])=>{
    const s=fuzzyScore(q,'go to '+title)+rank('page:'+id);
    if(s>0||!q) items.push({kind:'page',key:'page:'+id,title,meta:'g '+({overview:'o',colony:'c',missions:'m',results:'r',events:'e',patches:'p',objboard:'b',settings:'s',users:'u',autonomy:'a'}[id]||''),score:s,run:()=>showPage(id)});
  });
  PALETTE_ACTIONS.filter(([,,,adm])=>!adm||admin).forEach(([key,title,run])=>{
    const s=fuzzyScore(q,title)+rank('act:'+key);
    if(s>0||!q) items.push({kind:'action',key:'act:'+key,title,meta:'',score:s,run});
  });
  items.sort((a,b)=>b.score-a.score);
  return items.slice(0,12);
}
function paletteRender(){
  const box=document.getElementById('palette-results');
  const rows=[];
  if(palItems.length){
    rows.push('<div class="palette-sect">Commands</div>');
    palItems.forEach((it,i)=>rows.push(
      `<div class="palette-item${i===palSel?' sel':''}" data-pi="${i}" data-onclick="paletteRun(${i})">`+
      `<span class="pi-kind">${it.kind}</span><span class="pi-title">${escapeHtml(it.title)}</span><span class="pi-meta">${escapeHtml(it.meta||'')}</span></div>`));
  }
  if(palSearchRows.length){
    rows.push('<div class="palette-sect">Mission memory</div>');
    palSearchRows.forEach((r,j)=>{
      const i=palItems.length+j;
      rows.push(`<div class="palette-item${i===palSel?' sel':''}" data-pi="${i}" data-onclick="paletteRun(${i})">`+
        `<span class="pi-kind">${r.kind}</span><span class="pi-title">${escapeHtml(r.title)}</span><span class="pi-meta">${escapeHtml(r.meta||'')}</span></div>`);
    });
  }
  box.innerHTML=rows.length?rows.join(''):'<div class="palette-empty">No matches — try fewer letters, or 2+ characters to search mission memory.</div>';
  const sel=box.querySelector('.palette-item.sel'); if(sel) sel.scrollIntoView({block:'nearest'});
}
function paletteAll(){ return palItems.concat(palSearchRows); }
function paletteRun(i){
  const it=paletteAll()[i]; if(!it) return;
  if(it.key) paletteRemember(it.key);
  closePalette();
  try{ it.run(); }catch(e){ console.error('palette action failed',e); }
}
async function paletteSearch(q){
  if(ROLE!=='admin'||q.length<2){ palSearchRows=[]; paletteRender(); return; }
  try{
    const r=await api('/memory/explorer?limit=20&q='+encodeURIComponent(q));
    if(!r.success||q!==palLastQ) return;
    const d=r.data||{}; const rows=[];
    (d.missions||[]).slice(0,5).forEach(m=>rows.push({kind:'mission',title:m.goal||'(mission)',meta:m.status||'',run:()=>openResults(m.id)}));
    (d.tasks||[]).slice(0,4).forEach(t=>rows.push({kind:'task',title:t.title||'(task)',meta:t.status||'',run:()=>{ if(t.mission_id) openResults(t.mission_id); }}));
    (d.patches||[]).slice(0,4).forEach(p=>rows.push({kind:'patch',title:p.file_path||'(patch)',meta:p.status||'',run:()=>openPatches(p.mission_id?{mission_id:p.mission_id}:{})}));
    (d.sources||[]).slice(0,3).forEach(s=>rows.push({kind:'source',title:s.title||s.url||'(source)',meta:s.domain||'',run:()=>{ if(s.mission_id) openResults(s.mission_id); }}));
    palSearchRows=rows;
    if(palSel>=paletteAll().length) palSel=0;
    paletteRender();
  }catch{ /* memory search is best-effort inside the palette */ }
}
function togglePalette(){ document.getElementById('palette-overlay').classList.contains('show')?closePalette():openPalette(); }
function openPalette(){
  closeNotifPanel();
  const ov=document.getElementById('palette-overlay'); ov.classList.add('show');
  const inp=document.getElementById('palette-input');
  inp.value=''; palSel=0; palLastQ=''; palSearchRows=[]; palItems=paletteBuild('');
  paletteRender(); setTimeout(()=>inp.focus(),30);
}
function closePalette(){ document.getElementById('palette-overlay')?.classList.remove('show'); }
document.getElementById('palette-overlay').addEventListener('click',e=>{ if(e.target.id==='palette-overlay') closePalette(); });
document.getElementById('palette-input').addEventListener('input',e=>{
  const q=e.target.value.trim(); palLastQ=q; palSel=0;
  palItems=paletteBuild(q); paletteRender();
  clearTimeout(palSearchTimer); palSearchTimer=setTimeout(()=>paletteSearch(q),300);
});
document.getElementById('palette-input').addEventListener('keydown',e=>{
  const total=paletteAll().length;
  if(e.key==='ArrowDown'){ e.preventDefault(); palSel=Math.min(total-1,palSel+1); paletteRender(); }
  else if(e.key==='ArrowUp'){ e.preventDefault(); palSel=Math.max(0,palSel-1); paletteRender(); }
  else if(e.key==='Enter'){ e.preventDefault(); paletteRun(palSel); }
  else if(e.key==='Escape'){ closePalette(); }
});

// ---- Notification center ----------------------------------------------------
// v0.3.8.90: `mission_complete` -> `mission_completed`. The colony emits `mission_completed`,
// `mission_partial` and `mission_failed` (Queen.cs); this pattern is anchored with ^...$, so the
// shorter spelling could never match and the notification centre has never once announced a
// SUCCESSFUL mission. Every other alternative here has a real producer, which is why it looked like
// a working feature: failures, patches and approvals all arrived. `mission_partial` added at the
// same time — a mission that half-worked is exactly as notable as one that did not.
// Guarded now by UiShellTests, against the event vocabulary rather than against this list.
const NOTIF_NOTABLE=/^(mission_completed|mission_partial|mission_failed|patch_applied|patch_apply_failed|patch_alternative_created|patch_verified_approved|patch_verify_failed|approval_request_created|approval_request_approved|approval_request_rejected|autonomy_autoapply_\w+|jobs_cancel_all)$/;
const NOTIF_BAD=/failed|reverted|rejected|cancel/;
const NOTIF_GOOD=/complete|applied|approved|verified|kept/;
let notifStore=[];
function notifSeen(){ return localStorage.getItem('notif-seen')||''; }
function notifIngest(blocks){
  const out=[];
  for(const block of blocks.slice(0,120)){
    const lines=block.trim().split('\n');
    const l0=lines[0]||'';
    const m=l0.match(/^\[([^\]]+)\]\s*(.*)$/); if(!m) continue;
    const type=m[1], ts=(m[2]||'').substring(0,19);
    if(!NOTIF_NOTABLE.test(type)) continue;
    const msg=lines.slice(2).join(' ').trim().substring(0,140)||lines.slice(1).join(' ').trim().substring(0,140);
    out.push({type,ts,msg});
    if(out.length>=30) break;
  }
  notifStore=out;
  notifRenderBadge();
}
function notifUnreadCount(){ const seen=notifSeen(); return notifStore.filter(n=>n.ts>seen).length; }
function notifRenderBadge(){
  const b=document.getElementById('notif-count'); if(!b) return;
  const n=notifUnreadCount();
  b.style.display=n>0?'block':'none';
  b.textContent=n>9?'9+':String(n);
}
function notifClass(t){ return NOTIF_BAD.test(t)?'bad':(NOTIF_GOOD.test(t)?'good':'warn'); }
function notifTarget(t){
  if(/^patch|^approval/.test(t)) return ()=>{ if(ROLE==='admin') showPage('patches'); else showPage('events'); };
  if(/^mission/.test(t)) return ()=>showPage('results');
  return ()=>showPage('events');
}
function notifRenderPanel(){
  const list=document.getElementById('notif-list');
  const seen=notifSeen();
  if(!notifStore.length){ list.innerHTML='<div class="notif-empty">No notable activity yet.</div>'; return; }
  list.innerHTML=notifStore.map((n,i)=>
    `<div class="notif-item ${notifClass(n.type)}${n.ts>seen?' unread':''}" data-onclick="notifOpen(${i})">`+
    `<span class="ni-dot"></span><span class="ni-msg"><b>${escapeHtml(n.type)}</b><br>${escapeHtml(n.msg)}</span>`+
    `<span class="ni-time">${escapeHtml((n.ts||'').replace('T',' ').substring(5))}</span></div>`).join('');
}
function notifOpen(i){ const n=notifStore[i]; closeNotifPanel(); if(n) notifTarget(n.type)(); }
function notifMarkAllRead(){
  if(notifStore.length) localStorage.setItem('notif-seen',notifStore[0].ts);
  notifRenderBadge(); notifRenderPanel();
}
function closeNotifPanel(){ document.getElementById('notif-panel')?.classList.remove('show'); }
// v0.3.8.49 (§17): the top-right glyph is the Colony activity / idle view — an ambient glance at the
// colony, NOT a notifications dropdown and NOT the old Dashboard. Clicking it opens the Colony view
// and nothing else. Recent-activity remains readable there (the Overview is the colony at a glance).
document.getElementById('notif-bell').addEventListener('click',e=>{
  e.stopPropagation();
  closeNotifPanel(); closePalette();
  notifMarkAllRead();      // opening the colony view acknowledges the ambient activity badge
  go('/colony');
});
document.getElementById('notif-clear').addEventListener('click',e=>{ e.stopPropagation(); notifMarkAllRead(); });
document.addEventListener('click',e=>{
  const p=document.getElementById('notif-panel');
  if(p?.classList.contains('show')&&!p.contains(e.target)&&e.target.id!=='notif-bell'&&!document.getElementById('notif-bell').contains(e.target)) closeNotifPanel();
});

// ---- Keyboard shortcuts help --------------------------------------------------
function openShortcuts(){ document.getElementById('shortcut-overlay').classList.add('show'); }
function closeShortcuts(){ document.getElementById('shortcut-overlay')?.classList.remove('show'); }
document.getElementById('shortcut-overlay').addEventListener('click',e=>{ if(e.target.id==='shortcut-overlay') closeShortcuts(); });

// ---- Onboarding tour ----------------------------------------------------------
const TOUR_STEPS=[
  ['Welcome to the colony','ANTHILL turns one request into a coordinated mission: the Queen plans, specialised ants execute, and everything they do is observable here. This 60-second tour shows you where things live.'],
  ['Dispatch missions','The Overview page is your command node — compose a goal, Preview Plan to see the task DAG the planner builds (with constraint warnings), then approve and dispatch. The Colony page shows the live canvas while it runs.'],
  ['Review every change','Nothing touches disk without you. Patch proposals land in Changes: filter, group, diff, edit an alternative, run an unbiased verify (build+test with auto-restore), approve, and only then apply.'],
  ['Memory that learns','The Pheromones page is the colony\'s memory explorer — successful paths strengthen, failures decay, and mission history is searchable. The same search is one Ctrl+K away from anywhere.'],
  ['Fast hands','Press Ctrl+K for the command palette, ? for shortcuts, and g then a letter to jump between pages (g o, g m, g p…). The bell tracks notable activity. That\'s it — the colony is yours.'],
];
let tourStep=0;
function tourRender(){
  const [t,b]=TOUR_STEPS[tourStep];
  setEl('tour-step-label',`${tourStep+1} / ${TOUR_STEPS.length}`);
  setEl('tour-title',t); setEl('tour-body',b);
  document.getElementById('tour-back').style.visibility=tourStep===0?'hidden':'visible';
  document.getElementById('tour-next').textContent=tourStep===TOUR_STEPS.length-1?'Finish':'Next';
}
function startTour(force){
  if(!force&&localStorage.getItem('tour-done')==='1') return;
  tourStep=0; tourRender();
  document.getElementById('tour-overlay').classList.add('show');
}
function endTour(){
  localStorage.setItem('tour-done','1');
  document.getElementById('tour-overlay').classList.remove('show');
}
document.getElementById('tour-skip').addEventListener('click',endTour);
document.getElementById('tour-back').addEventListener('click',()=>{ if(tourStep>0){ tourStep--; tourRender(); } });
document.getElementById('tour-next').addEventListener('click',()=>{
  if(tourStep<TOUR_STEPS.length-1){ tourStep++; tourRender(); } else endTour();
});

// ---- Saved layout restore ------------------------------------------------------
// nav-collapsed, per-card collapse, and Patch Center grouping already persist.
// This restores the last active page after login (admin pages only for admins).
function restoreLayout(){
  try{
    // 1) An explicit deep link in the URL wins (bookmarks / shared links / back-forward).
    if(router()) return;
    // 2) Otherwise reopen the last page, mapped to its canonical route (honoring role visibility).
    const last=localStorage.getItem('last-page');
    if(last && PAGE_HOME[last]){
      const r=ROUTE_TABLE[PAGE_HOME[last]];
      if(r && !canSee(r.vis)){ go('/dashboard',false); return; }
      go(PAGE_HOME[last],false); return;
    }
    // v3.8.39: a first-time operator lands in the conversation rather than on a grid of
    // widgets. Anyone with a remembered page keeps it — that branch is above and untouched.
    go('/chat',false);
  }catch{ try{ showPage('chat'); }catch{} }
}

// -- Card collapse --------------------------------------------------------------
function initCollapse(){
  document.querySelectorAll('.collapse-btn').forEach(btn=>{
    const cardId=btn.dataset.card;
    if(localStorage.getItem('collapsed:'+cardId)==='1') collapseCard(cardId,false);
    btn.addEventListener('click',e=>{
      e.stopPropagation();
      const card=document.getElementById(cardId);
      if(card.classList.contains('collapsed')) expandCard(cardId);
      else collapseCard(cardId,true);
    });
  });
}
function collapseCard(id,save){
  const card=document.getElementById(id); if(!card) return;
  card.classList.add('collapsed');
  if(save) localStorage.setItem('collapsed:'+id,'1');
}
function expandCard(id){
  const card=document.getElementById(id); if(!card) return;
  card.classList.remove('collapsed');
  localStorage.removeItem('collapsed:'+id);
}
initCollapse();

// -- Settings tabs --------------------------------------------------------------
document.querySelectorAll('.settings-tab').forEach(tab=>{
  tab.addEventListener('click',()=>{
    document.querySelectorAll('.settings-tab').forEach(t=>t.classList.remove('active'));
    document.querySelectorAll('.settings-pane').forEach(p=>p.classList.remove('active'));
    tab.classList.add('active');
    document.getElementById('tab-'+tab.dataset.tab)?.classList.add('active');
    if(tab.dataset.tab==='models') loadModelsTab();
    if(tab.dataset.tab==='info') loadInfoTab();
    if(tab.dataset.tab==='colony') loadColonyTab();
    if(tab.dataset.tab==='connection') loadSettingsInfo();
    if(tab.dataset.tab==='providers') loadProvidersTab();
  });
});

document.getElementById('models-refresh').addEventListener('click',loadModelsTab);
document.getElementById('diag-refresh').addEventListener('click',loadInfoTab);

// -- Maintenance / data hygiene ------------------------------------------------
function humanBytes(b){ if(b==null) return '—'; const u=['B','KB','MB','GB','TB']; let v=b,i=0; while(v>=1024&&i<u.length-1){v/=1024;i++;} return v.toFixed(v<10&&i>0?1:0)+' '+u[i]; }
function maintMsg(el,t,ok){ const m=document.getElementById(el); if(!m)return; m.style.color=ok?'var(--green)':'var(--red)'; m.textContent=t; setTimeout(()=>{ if(m.textContent===t) m.textContent=''; },5000); }

async function loadMaintStats(){
  try{
    const r=await api('/maintenance/stats'); if(!r.success) return; const d=r.data||{};
    setEl('maint-keep', d.max_db_backups);
    setEl('maint-disk', d.disk_total_bytes?`${humanBytes(d.disk_free_bytes)} free of ${humanBytes(d.disk_total_bytes)}`:'—');
    setEl('maint-db', humanBytes(d.db_bytes));
    setEl('maint-backups', `${d.backup_count||0} files · ${humanBytes(d.backup_bytes)}`);
  }catch{}
}
document.getElementById('maint-refresh').addEventListener('click',loadMaintStats);
document.getElementById('maint-flush').addEventListener('click',async()=>{
  if(!await uiConfirm('Flush cache? Prunes old DB backups (keeps the newest N) and compacts the database.')) return;
  maintMsg('maint-msg','Flushing…',true);
  try{ const r=await api('/maintenance/flush','POST',{});
    maintMsg('maint-msg', r.success?`Freed ${humanBytes(r.data?.bytes_freed)} (${r.data?.backups_deleted} backups).`:(r.message||'Failed'), r.success);
    loadMaintStats();
  }catch(e){ maintMsg('maint-msg','Failed: '+e.message,false); }
});
document.getElementById('maint-reset').addEventListener('click',async()=>{
  if(!await uiConfirm('Reset all settings to safe defaults? Connection settings (Ollama host/model/routes, API bind) are preserved.')) return;
  try{ const r=await api('/maintenance/reset-config','POST',{});
    maintMsg('maint-msg', r.success?'Config reset (connection preserved).':(r.message||'Failed'), r.success);
  }catch(e){ maintMsg('maint-msg','Failed: '+e.message,false); }
});

// Missions page: Cancel All + Clear Missions
document.getElementById('ms-cancel-all').addEventListener('click',async()=>{
  if(!await uiConfirm('Cancel all jobs? Queued work is dropped; a running mission finishes on its own.')) return;
  try{ const r=await api('/jobs/cancel-all','POST',{});
    maintMsg('ms-maint-msg', r.success?`Cancelled ${r.data?.cancelled} job(s).`:(r.message||'Failed'), r.success);
    pollJobs();
  }catch(e){ maintMsg('ms-maint-msg','Failed: '+e.message,false); }
});
document.getElementById('ms-clear').addEventListener('click',async()=>{
  if(!await uiConfirm('Clear ALL mission history (missions, tasks, events, patches, approvals)? Objectives, pheromones, users, and config are kept. This cannot be undone.')) return;
  try{ const r=await api('/maintenance/clear-missions','POST',{});
    maintMsg('ms-maint-msg', r.success?`Cleared ${r.data?.missions_deleted} mission(s); freed ${humanBytes(r.data?.bytes_freed)}.`:(r.message||'Failed'), r.success);
    pollJobs(); pollStatus();
  }catch(e){ maintMsg('ms-maint-msg','Failed: '+e.message,false); }
});

// Autonomy page: Dump Directives
document.getElementById('auto-dump').addEventListener('click',async()=>{
  if(!await uiConfirm('Dump the entire objective backlog and its run history? Missions and everything else are kept. This cannot be undone.')) return;
  try{ const r=await api('/objectives/clear','POST',{});
    autoMsg(r.success?`Dumped ${r.data?.objectives_deleted} objective(s).`:(r.message||'Failed'), r.success);
    if(typeof reloadAutonomy==='function') reloadAutonomy();
  }catch(e){ autoMsg('Failed: '+e.message,false); }
});

// -- Settings info -------------------------------------------------------------
async function loadSettingsInfo(){
  try{
    const text=await apiText('/models');
    const ollamaHost=(text.match(/Ollama Host:\s*([^\n]+)/)||[])[1]?.trim()||'—';
    const model=(text.match(/Active Route Targets:\s*([^\n]+)/)||[])[1]?.trim()||'—';
    setEl('si-ollama-host',ollamaHost);
    setEl('si-model',model);
    try{
      const mr=await fetch(url('/ollama/models'),{headers:{'Authorization':'Bearer '+TOKEN}});
      const md=await mr.json();
      const ok=Array.isArray(md.models);
      document.getElementById('si-ollama-dot').className='ollama-dot '+(ok?'ok':'err');
      setEl('si-ollama-status',ok?`${ollamaHost} (${md.models.length} models)`:'unreachable');
    }catch{
      document.getElementById('si-ollama-dot').className='ollama-dot err';
      setEl('si-ollama-status','unreachable');
    }
  }catch{
    document.getElementById('si-ollama-dot').className='ollama-dot err';
    setEl('si-ollama-status','unreachable');
  }
}

// -- Provider connections ------------------------------------------------------
let providerCatalog=[];

function providerKindLabel(kind){return kind==='free-local'?'Free · Local':'Paid API';}

async function loadProvidersTab(){
  const grid=document.getElementById('providers-grid');
  grid.innerHTML='<div style="font-size:10px;color:var(--dim);text-align:center;padding:20px;grid-column:1/-1;">Loading...</div>';
  try{
    const [catRes,connRes]=await Promise.all([api('/providers/catalog'),api('/providers')]);
    if(!catRes.success) throw new Error(catRes.message);
    providerCatalog=(catRes.data||[]).filter(p=>p.requires_key);
    const connections=connRes.success?(connRes.data||[]):[];
    const byProvider={};
    connections.forEach(c=>{byProvider[c.provider]=c;});
    grid.innerHTML=providerCatalog.map(p=>renderProviderCard(p,byProvider[p.provider]||{})).join('')
      ||'<div style="font-size:10px;color:var(--dim)">No providers available.</div>';
  }catch(e){
    grid.innerHTML=`<div style="font-size:10px;color:var(--red)">Error: ${escapeHtml(e.message)}</div>`;
  }
}

function renderProviderCard(p,conn){
  const configured=!!conn.configured;
  let statusHtml,statusColor;
  if(!configured){statusColor='var(--dim)';statusHtml='Not connected';}
  else if(conn.last_verify_ok===true){statusColor='var(--green)';statusHtml='Connected · verified '+(conn.last_verified_at?fmtTime(conn.last_verified_at):'');}
  else if(conn.last_verify_ok===false){statusColor='var(--red)';statusHtml='Connected · last test failed';}
  else{statusColor='var(--queen)';statusHtml='Connected · not yet tested';}
  return `<div class="provider-card" data-provider="${p.provider}">
    <div class="provider-top">
      <div class="provider-name">${escapeHtml(p.name)}</div>
      <span class="provider-kind ${p.kind}">${providerKindLabel(p.kind)}</span>
    </div>
    <div class="provider-desc">${escapeHtml(p.description||'')}</div>
    <div class="provider-status"><span class="ollama-dot ${configured?(conn.last_verify_ok===false?'err':'ok'):'err'}"></span>${statusHtml}</div>
    <div class="provider-field">
      <label>API Key</label>
      <input type="password" class="provider-input pv-key" placeholder="${configured?'•••••••••••••••• (leave blank to keep)':'Paste API key'}" autocomplete="off">
    </div>
    <div class="provider-field">
      <label>Base URL <span style="text-transform:none;font-weight:400;">(optional override)</span></label>
      <input type="text" class="provider-input pv-baseurl" placeholder="${escapeHtml(p.default_endpoint||'')}" value="${escapeHtml(conn.base_url||'')}">
    </div>
    <div class="provider-actions">
      <button class="btn btn-primary pv-save">Save</button>
      <button class="btn btn-ghost pv-test" ${configured?'':'disabled'}>Test Connection</button>
      <button class="btn btn-danger pv-remove" ${configured?'':'disabled'}>Remove</button>
    </div>
    <div class="save-msg pv-msg"></div>
    <div class="provider-help">Get a key: <a href="${p.key_help_url}" target="_blank" rel="noopener">${escapeHtml((p.key_help_url||'').replace(/^https?:\/\//,''))}</a></div>
  </div>`;
}

document.getElementById('providers-refresh')?.addEventListener('click',loadProvidersTab);

// v0.3.8.50: ONE handler for provider-card actions, wherever the card renders — the Settings grid
// or inline under an integration. Refresh follows the surface the card actually lives on.
async function providerCardAction(e){
  const card=e.target.closest('.provider-card'); if(!card) return;
  const provider=card.dataset.provider;
  const inIntegrations=!!e.target.closest('#int-body');
  const refresh=()=>inIntegrations?loadIntegrations():loadProvidersTab();
  const msg=card.querySelector('.pv-msg');
  const setMsg=(t,ok)=>{msg.style.color=ok?'var(--green)':'var(--red)';msg.textContent=t;setTimeout(()=>{if(msg.textContent===t)msg.textContent='';},4000);};

  if(e.target.classList.contains('pv-save')){
    const key=card.querySelector('.pv-key').value.trim();
    const baseUrl=card.querySelector('.pv-baseurl').value.trim();
    try{
      const r=await api('/providers','POST',{provider,api_key:key||undefined,base_url:baseUrl||undefined});
      if(r.success){setMsg('Saved ✓',true);card.querySelector('.pv-key').value='';await refresh();}
      else setMsg(r.message||'Failed',false);
    }catch(err){setMsg('Failed: '+err.message,false);}
  }

  if(e.target.classList.contains('pv-test')){
    setMsg('Testing…',true);
    try{
      const r=await api('/providers/'+encodeURIComponent(provider)+'/test','POST');
      setMsg(r.success?'Verified ✓':(r.message||'Test failed'),r.success);
      await refresh();
    }catch(err){setMsg('Failed: '+err.message,false);}
  }

  if(e.target.classList.contains('pv-remove')){
    if(!await uiConfirm(`Remove the ${provider} connection? Ants routed to it will fall back to Ollama.`)) return;
    try{
      const r=await api('/providers/'+encodeURIComponent(provider),'DELETE');
      if(r.success){setMsg('Removed',true);await refresh();}
      else setMsg(r.message||'Failed',false);
    }catch(err){setMsg('Failed: '+err.message,false);}
  }
}
document.getElementById('providers-grid')?.addEventListener('click',providerCardAction);
document.getElementById('int-body')?.addEventListener('click',providerCardAction);

// -- Model browser --------------------------------------------------------------
let activeRouteModels=new Set();

async function loadModelsTab(){
  await loadRoutes();
  await loadOllamaModels();
}

async function loadRoutes(){
  // v0.3.8.48 (defect 16): structured data. The old parser looked for '?' or '->' in prose the
  // endpoint stopped emitting, fell back to raw text, and left activeRouteModels empty.
  const grid=document.getElementById('route-grid');
  try{
    const r=await api('/routes/json');
    if(!(r&&r.success&&r.data)) throw new Error((r&&r.message)||'no data');
    activeRouteModels=new Set((r.data.roles||[]).map(x=>x.model).filter(Boolean));
    grid.innerHTML=(r.data.roles||[]).map(x=>
      `<div class="route-row"><span class="route-role">${escapeHtml(x.role)}</span>`
      +`<span class="route-model">${escapeHtml(x.model||'')}</span>`
      +`<span class="route-provider">${escapeHtml(x.provider||'')}${x.available?'':' ⚠ unavailable'}</span></div>`).join('');
  }catch(e){
    grid.innerHTML=`<div style="font-size:10px;color:var(--red)">Could not load routes: ${escapeHtml(e.message)}</div>`;
  }
}

/**
 * Make an installed model the colony's model. v3.8.33.
 *
 * Posts only `ollama_model`, deliberately — /settings is a partial update, and sending the whole
 * Colony tab from here would write back whatever happened to be in those inputs at the time,
 * including edits the operator had not saved. One field, one intent.
 */
async function selectOllamaModel(name){
  const grid=document.getElementById('model-grid');
  try{
    const r=await api('/settings','POST',{ollama_model:name});
    if(r.success){
      colonySettings=r.data?.settings||colonySettings;
      const box=document.getElementById('set-ollama-model'); if(box) box.value=name;
      await loadOllamaModels();
      pollModelInfo();
      if(grid) grid.insertAdjacentHTML('afterbegin',
        `<div style="font-size:10px;color:var(--green)">Model set to ${name}</div>`);
    } else if(grid){
      grid.insertAdjacentHTML('afterbegin',
        `<div style="font-size:10px;color:var(--red)">${r.message||'Could not set the model'}</div>`);
    }
  }catch(e){
    if(grid) grid.insertAdjacentHTML('afterbegin',
      `<div style="font-size:10px;color:var(--red)">Could not set the model: ${escapeHtml(e.message)}</div>`);
  }
}

async function loadOllamaModels(){
  const grid=document.getElementById('model-grid');
  grid.innerHTML='<div style="font-size:10px;color:var(--dim)">Loading from Ollama...</div>';
  try{
    const r=await fetch(url('/ollama/models'),{headers:{'Authorization':'Bearer '+TOKEN}});
    const data=await r.json();
    if(!data.models?.length&&data.error){
      grid.innerHTML=`<div style="font-size:10px;color:var(--red)">${data.message||'Ollama unreachable'}</div>`;
      return;
    }
    const models=(data.models||[]).sort((a,b)=>b.size-a.size);
    // v3.8.33 — clicking a model SELECTS it as the colony's model.
    //
    // It used to copy the name to the clipboard and leave you to paste it into Settings. With
    // `llama3.1:8b` hardcoded as the default that was merely inconvenient; now that there is no
    // built-in model, choosing one is the step that makes the colony work, and it should not require
    // retyping a tag by hand.
    grid.innerHTML=models.map(m=>{
      const name=m.name||m.model||'unknown';
      const sizeGb=m.size?(m.size/1e9).toFixed(1)+'GB':'?';
      const isActive=activeRouteModels.has(name)||[...activeRouteModels].some(r=>name.startsWith(r.split(':')[0]));
      return `<div class="model-item${isActive?' active-route':''}" title="Click to use this model"
        data-onclick="selectOllamaModel('${name.replace(/'/g,"\\'")}')">
        <span class="model-name">${name}</span>
        <span class="model-size">${sizeGb}</span>
        ${isActive?'<span class="model-badge-active">ACTIVE</span>':''}
      </div>`;
    }).join('');
    if(!models.length) grid.innerHTML='<div style="font-size:10px;color:var(--dim)">No models found. Pull one with <code>ollama pull &lt;model&gt;</code> — any model Ollama can run will work.</div>';
  }catch(e){
    grid.innerHTML=`<div style="font-size:10px;color:var(--red)">Error: ${escapeHtml(e.message)}</div>`;
  }
}

async function loadInfoTab(){
  loadMaintStats();
  try{
    const [sr,cfgText,diagText]=await Promise.all([api('/status'),apiText('/config'),apiText('/diagnostics')]);
    const d=sr.data||{};
    setEl('si-version',d.version||'—');
    setEl('si-kernel',d.native_kernel||'—');
    setEl('si-safety',cfgText.match(/Safety Profile:\s*([^\n]+)/)?.[1]?.trim()||'—');
    setEl('si-workers',cfgText.match(/Job Workers:\s*([^\n]+)/)?.[1]?.trim()||'—');
    setEl('si-web',cfgText.match(/Web Search:\s*([^\n]+)/)?.[1]?.trim()||'—');
    setEl('si-filewrite',cfgText.match(/File Writ\w+:\s*([^\n]+)/)?.[1]?.trim()||'—');
    setEl('si-patch',cfgText.match(/Patch Appli\w+:\s*([^\n]+)/)?.[1]?.trim()||'—');
    const diagEl=document.getElementById('si-diag');
    if(diagEl) diagEl.textContent=diagText.substring(0,2000);
  }catch(e){
    const diagEl=document.getElementById('si-diag');
    if(diagEl) diagEl.textContent='Error: '+e.message;
  }
}

// -- Event log (full page) -----------------------------------------------------
let allLogEvents=[];

async function openLogModal(){
  showPage('events');
  await reloadLogModal();
}

PAGE_ENTER['events']=()=>{reloadLogModal();};

document.getElementById('log-reload').addEventListener('click',reloadLogModal);

// v3.8.13: the apostrophe is encoded too. This is DEFENCE IN DEPTH, not the fix — an attribute
// value is decoded by getAttribute() before any parser sees it, so encoding alone never protected
// the pseudo-JavaScript in data-onclick. Untrusted values belong in plain data-* attributes read by
// a fixed action map (see the data-action dispatcher), never in an executable attribute.
function escapeHtml(s){return(s==null?'':String(s)).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));}

/**
 * v3.8.34: the correct escape for a value that lands INSIDE a quoted argument of a `data-onclick`
 * attribute — a nested context where `escapeHtml` alone is not enough.
 *
 * `data-onclick` is read back with getAttribute() and parsed by the micro-interpreter at the bottom
 * of this file. The HTML parser DECODES entities before that happens, so `escapeHtml`'s `&#39;`
 * becomes a real apostrophe by the time splitTop/coerce see it — closing the argument early and
 * letting a second statement be appended and called with the operator's session. Escaping for HTML
 * is correct for the outer layer and invisible to the inner one.
 *
 * v3.8.13 fixed the patch-path instance of this structurally, with the `data-action` map, and its
 * comment is still the right long-term answer for untrusted values. This closes the remaining sites
 * that still interpolate, without corrupting the values: the interpreter's own parser is
 * backslash-aware (`splitTop` honours `\'`, `coerce` unescapes `\(['"\])`), so a backslash escape
 * survives the round trip exactly, where stripping the character would silently change an id.
 *
 * Order matters. Escape for the JS layer FIRST, then for the HTML layer: the emitted `\&#39;`
 * decodes to `\'`, which the interpreter unescapes back to a literal apostrophe.
 */
function jsArg(s){
  return escapeHtml(String(s==null?'':s).replace(/\\/g,'\\\\').replace(/'/g,"\\'"));
}

/**
 * v2.14.13: sanitise a colour before it reaches a style="" attribute.
 *
 * Ant accent colours come from uiState.castes[caste].color, which the operator sets and which
 * UiStateStore round-trips verbatim by design ("the UI owns the shape"). That makes the client the
 * only place validation can happen. Anything that is not a plain hex literal or a CSS custom
 * property reference falls back to the neutral ant colour rather than being pasted into CSS.
 */
function cssColor(v,fallback){
  const s=String(v==null?'':v).trim();
  if(/^#[0-9a-fA-F]{3,8}$/.test(s)) return s;
  if(/^var\(--[a-zA-Z0-9_-]+\)$/.test(s)) return s;
  if(/^[a-zA-Z]{3,20}$/.test(s)) return s;          // named CSS colours: letters only, no separators
  return fallback||'#7fa0bc';
}

const SINCE_MS={'15m':9e5,'1h':36e5,'6h':216e5,'24h':864e5};

async function reloadLogModal(){
  try{
    const ant  =document.getElementById('log-filter-ant').value;
    const type =document.getElementById('log-filter-type').value;
    const level=document.getElementById('log-filter-level').value;
    const since=document.getElementById('log-filter-since').value;
    const p=new URLSearchParams();
    if(ant)   p.set('ant',ant);
    if(type)  p.set('type',type);
    if(level) p.set('level',level);
    if(since&&SINCE_MS[since]) p.set('since',new Date(Date.now()-SINCE_MS[since]).toISOString());
    p.set('limit','500');
    const r=await api('/events/json?'+p.toString());
    if(!r.success) throw new Error(r.message);
    allLogEvents=r.data.events||[];
    populateLogFilters(r.data.ants||[],r.data.types||[]);
    renderLogModal();
  }catch(e){
    document.getElementById('log-modal-body').innerHTML=`<div style="color:var(--red);padding:20px">Error: ${escapeHtml(e.message)}</div>`;
  }
}

function populateLogFilters(ants,types){
  const antSel=document.getElementById('log-filter-ant');
  const want=ants.length?ants:Object.keys(ANT_DEFAULTS);
  const cur=antSel.value;
  antSel.innerHTML='<option value="">All Ants</option>'+want.map(a=>{
    const label=ANT_DEFAULTS[a]?casteName(a):a;
    return `<option value="${escapeHtml(a)}"${a===cur?' selected':''}>${escapeHtml(label)}</option>`;
  }).join('');
  const typeSel=document.getElementById('log-filter-type');
  const tcur=typeSel.value;
  typeSel.innerHTML='<option value="">All Types</option>'+types.map(t=>`<option value="${escapeHtml(t)}"${t===tcur?' selected':''}>${escapeHtml(t)}</option>`).join('');
}

function renderLogModal(){
  const search=(document.getElementById('log-search').value||'').toLowerCase();
  const body=document.getElementById('log-modal-body');
  const filtered=allLogEvents.filter(ev=>{
    if(!search) return true;
    return `${ev.event_type} ${ev.ant_name} ${ev.message} ${ev.mission_id}`.toLowerCase().includes(search);
  });
  setEl('log-modal-count',`${filtered.length} / ${allLogEvents.length} events`);
  body.innerHTML=filtered.map(ev=>{
    const evType=ev.event_type||'event';
    const ant=ev.ant_name||'—';
    const ts=(ev.created_at||'').substring(0,19).replace('T',' ');
    const col=ANT_MAP[ant]?.color||'var(--dim)';
    const tc=evTypeClass(evType);
    return `<div class="log-ev">
      <div class="log-ev-head">
        <span class="log-ev-type ${tc}">${escapeHtml(evType)}</span>
        <span class="log-ev-ant" style="color:${col}">${escapeHtml(ant)}</span>
        <span class="log-ev-ts">${ts}</span>
      </div>
      <div class="log-ev-meta">mission:${escapeHtml((ev.mission_id||'—').substring(0,14))} · task:${escapeHtml((ev.task_id||'—').toString().substring(0,12))}</div>
      <div class="log-ev-msg">${escapeHtml((ev.message||'—').replace(/\.\.\.\[.+?\]/g,'').trim())}</div>
    </div>`;
  }).join('');
}

document.getElementById('log-search').addEventListener('input',renderLogModal);
document.getElementById('log-filter-ant').addEventListener('change',reloadLogModal);
document.getElementById('log-filter-type').addEventListener('change',reloadLogModal);
document.getElementById('log-filter-level').addEventListener('change',reloadLogModal);
document.getElementById('log-filter-since').addEventListener('change',reloadLogModal);

// ---- v2.6 Phase 3: unified Activity center ----------------------------------
// One filtered timeline over the same /events/json stream the Event Log uses. Category facets map
// event_type prefixes to the workflows an operator reasons about. Purely additive: the Event Log,
// Mission Results, and Changes pages are untouched and remain reachable as Activity sub-nav tabs.
const ACT_FACETS=[
  ['all','All',()=>true],
  ['missions','Missions',t=>/^(mission|task|plan)/.test(t)],
  ['changes','Changes',t=>/^(patch|approval)/.test(t)],
  ['autonomy','Autonomy',t=>/^(autonomy|director|objective|jobs|strateg)/.test(t)],
  ['infra','Infrastructure',t=>/^(homelab|proxmox|incident|health|action|network|integration|vm|container|storage|backup)/.test(t)],
  ['system','System',t=>!/^(mission|task|plan|patch|approval|autonomy|director|objective|jobs|strateg|homelab|proxmox|incident|health|action|network|integration|vm|container|storage|backup)/.test(t)],
];
let actEvents=[], actFacet='all';
async function loadActivity(){
  const body=document.getElementById('act-body'); if(!body) return;
  renderActFacets();
  try{
    const r=await api('/events/json?limit=500');
    if(!r.success) throw new Error(r.message||'load failed');
    actEvents=(r.data&&r.data.events)||[];
    renderActivity();
  }catch(e){ body.innerHTML='<div style="color:var(--red,#f87171);padding:20px">Error: '+escapeHtml((e&&e.message)||'?')+'</div>'; }
}
function renderActFacets(){
  const bar=document.getElementById('act-facets'); if(!bar) return;
  bar.innerHTML=ACT_FACETS.map(f=>'<button class="subnav-tab'+(f[0]===actFacet?' active':'')+'" data-onclick="actSetFacet(\''+f[0]+'\')">'+escapeHtml(f[1])+'</button>').join('');
}
function actSetFacet(k){ actFacet=k; renderActFacets(); renderActivity(); }
function renderActivity(){
  const body=document.getElementById('act-body'); if(!body) return;
  const search=((document.getElementById('act-search')||{}).value||'').toLowerCase();
  const facetDef=ACT_FACETS.find(f=>f[0]===actFacet)||ACT_FACETS[0];
  const match=facetDef[2];
  const rows=actEvents.filter(ev=>{
    const t=(ev.event_type||'').toLowerCase();
    if(!match(t)) return false;
    if(search && !((ev.event_type+' '+ev.ant_name+' '+ev.message+' '+ev.mission_id).toLowerCase().includes(search))) return false;
    return true;
  });
  if(typeof setEl==='function') setEl('act-count',rows.length+' / '+actEvents.length+' events · '+facetDef[1]);
  if(!rows.length){ body.innerHTML='<div style="font-size:11px;color:var(--dim);text-align:center;padding:24px 0;">No activity in this view yet.</div>'; return; }
  body.innerHTML=rows.map(ev=>{
    const evType=ev.event_type||'event';
    const ant=ev.ant_name||'—';
    const ts=(ev.created_at||'').substring(0,19).replace('T',' ');
    const col=(ANT_MAP[ant]&&ANT_MAP[ant].color)||'var(--dim)';
    const tc=(typeof evTypeClass==='function')?evTypeClass(evType):'';
    return '<div class="log-ev"><div class="log-ev-head">'+
      '<span class="log-ev-type '+tc+'">'+escapeHtml(evType)+'</span>'+
      '<span class="log-ev-ant" style="color:'+col+'">'+escapeHtml(ant)+'</span>'+
      '<span class="log-ev-ts">'+ts+'</span></div>'+
      '<div class="log-ev-meta">mission:'+escapeHtml((ev.mission_id||'—').substring(0,14))+'</div>'+
      '<div class="log-ev-msg">'+escapeHtml((ev.message||'—').replace(/\.\.\.\[.+?\]/g,'').trim())+'</div></div>';
  }).join('');
}
(function(){ const s=document.getElementById('act-search'); if(s) s.addEventListener('input',renderActivity); })();
PAGE_ENTER['activity']=loadActivity; // Event Log / Results / Changes keep their own PAGE_ENTER loaders.

// -- UI State ------------------------------------------------------------------
let uiState={version:1,castes:{},positions:{},widgets:{},chambers:{},chamberNames:{},overlays:{}}; // chambers: v2.14.7 dragged chamber offsets// widgets: v2.5.2 R2 layout registry (zone → ordered [{id,kind,integration_id}])
const ANT_CASTES=['researcher','web','file','coder','builder','verifier'];
const ANT_DEFAULTS=Object.assign({queen:{label:'Queen',color:'#fbbf24',role:'QUEEN'}},
  Object.fromEntries(Object.entries(ANT_MAP).map(([k,v])=>[k,{label:v.label,color:v.color,role:v.role}])));
uiReady=true;

async function loadUiState(){
  try{
    const r=await api('/ui/state');
    if(r.success&&r.data){
      uiState.castes=r.data.castes||{}; uiState.positions=r.data.positions||{}; uiState.widgets=r.data.widgets||{};
      uiState.chambers=r.data.chambers||{}; uiState.chamberNames=r.data.chamberNames||{};
      uiState.overlays=overlayStateFrom(r.data);
    }
  }catch{}
  applyUiState();
  applyOverlayState();
}

/* ---------------------------------------------------------------------------------------------
 * v2.15.0 Stage 8 — ONE writer for ui_state.json.
 *
 * Until now two independent debounced writers did read-modify-write against the same document:
 * saveUiState() here (350ms) and save() in dashboard-workspace.js (600ms). Each preserved the
 * other's keys as of v2.14.14, but they still raced — a panel drag landing inside an ant rename's
 * window meant one of the two reads was stale and the later PUT silently discarded the other's
 * change.
 *
 * Both now register a MUTATOR with this single writer. One debounce, one GET, every pending
 * mutator applied to that one freshly-read document, one PUT — and writes are chained so a second
 * flush cannot start before the first finishes. Two surfaces, one owner, no lost updates.
 * ------------------------------------------------------------------------------------------- */
const UiStateWriter = (function(){
  let timer=null, mutators=[], chain=Promise.resolve();

  function flush(){
    const batch=mutators; mutators=[];
    if(!batch.length) return chain;
    chain=chain.then(async()=>{
      let doc={};
      try{ const cur=await api('/ui/state'); if(cur&&cur.success&&cur.data) doc=cur.data; }catch{}
      batch.forEach(m=>{ try{ m(doc); }catch(e){ console.error('ui state mutator',e); } });
      try{ await api('/ui/state','PUT',doc); }
      catch(e){ console.error('ui state save',e); }
    });
    return chain;
  }

  return {
    /** Register a change. `mutate(doc)` is applied to the freshly-read document at flush time. */
    queue(mutate){
      if(typeof mutate!=='function') return;
      mutators.push(mutate);
      clearTimeout(timer);
      timer=setTimeout(flush,350);
    },
    /** Force a write now — used on pagehide so an in-flight arrangement is not lost. */
    flushNow(){ clearTimeout(timer); return flush(); },
  };
})();
// dashboard-workspace.js persists through this same writer rather than owning a second one.
window.AnthillUiState = UiStateWriter;
window.addEventListener('pagehide',()=>{ UiStateWriter.flushNow(); });

function saveUiState(){
  UiStateWriter.queue(doc=>{
    doc.version=1;
    doc.castes=uiState.castes;
    doc.positions=uiState.positions;
    doc.widgets=uiState.widgets;
    doc.chambers=uiState.chambers||{};
    doc.chamberNames=uiState.chamberNames||{};
    // Overlay visibility lives inside the workspace subtree so the C# sanitizer validates it.
    // Only this key is touched here; panel placements belong to the workspace module's mutator.
    doc.dashboard_workspace=doc.dashboard_workspace||{};
    doc.dashboard_workspace.topology_overlays=uiState.overlays||{};
  });
}

function registryRoleById(id){return (colonyRegistry?.roles||colonyRegistry?.Roles||[]).find(r=>roleId(r)===id);}
function workerTelemetryById(id){
  const t=colonyRegistry?.worker_telemetry||colonyRegistry?.workerTelemetry||colonyRegistry?.WorkerTelemetry||{};
  const workers=t.workers||t.Workers||[];
  const audits=t.permission_audits||t.permissionAudits||t.PermissionAudits||[];
  const metrics=t.message_metrics||t.messageMetrics||t.MessageMetrics||[];
  return {
    usage:workers.find(x=>(x.assigned_worker||x.assignedWorker||x.AssignedWorker)===id)||{},
    audit:audits.find(x=>(x.runtime_node||x.runtimeNode||x.RuntimeNode)===id)||{},
    metric:metrics.find(x=>(x.runtime_node||x.runtimeNode||x.RuntimeNode)===id)||{}
  };
}
function casteName(caste){return uiState.castes[caste]?.name||ANT_DEFAULTS[caste]?.label||roleName(registryRoleById(caste)||{RoleId:caste});}
function casteColor(caste){return uiState.castes[caste]?.color||ANT_DEFAULTS[caste]?.color||ROLE_COLORS[caste]||'#7fa0bc';}

function applyUiState(){
  if(!uiReady) return;
  ANT_CASTES.forEach(c=>{if(ANT_MAP[c]) ANT_MAP[c].color=casteColor(c);});
  nodes.forEach(n=>{
    const caste=n.ant||'queen';
    const base=casteName(caste);
    if(n.id===caste||n.id==='queen') n.label=base;
    else if(n.id.startsWith(caste+'_')){const idx=parseInt(n.id.split('_').pop(),10)||0;n.label=base+'-'+(idx+1);}
    n.color=casteColor(caste);
    const pos=uiState.positions[n.id];
    if(pos&&typeof pos.x==='number'&&typeof pos.y==='number'){n.x=pos.x;n.y=pos.y;}
  });
}

function persistNodePosition(n){uiState.positions[n.id]={x:Math.round(n.x),y:Math.round(n.y)};saveUiState();}

// -- Rename popover ------------------------------------------------------------
let renameCaste=null;
const renamePop=document.getElementById('rename-pop');

/** v2.14.10: operator-renamed chambers. The canonical key stays the built-in chamber name, so
 *  role membership, drag offsets, and stats never depend on the label the operator chose. */
function chamberLabel(name){
  const c=(uiState.chamberNames||{})[name];
  return (c&&String(c).trim())||name;
}
let renameChamber=null;
function openChamberRename(name,sx,sy){
  renameChamber=name; renameCaste=null;
  const inp=document.getElementById('rename-input');
  inp.value=chamberLabel(name);
  renamePop.style.left=Math.min(sx,window.innerWidth-220)+'px';
  renamePop.style.top=Math.min(sy,window.innerHeight-120)+'px';
  renamePop.classList.add('show');
  inp.focus();inp.select();
}

function openRename(node,sx,sy){
  renameChamber=null;
  renameCaste=node.ant||'queen';
  const inp=document.getElementById('rename-input');
  inp.value=casteName(renameCaste);
  renamePop.style.left=Math.min(sx,window.innerWidth-220)+'px';
  renamePop.style.top=Math.min(sy,window.innerHeight-120)+'px';
  renamePop.classList.add('show');
  inp.focus();inp.select();
}
function commitRename(){
  const v=document.getElementById('rename-input').value.trim();
  if(renameChamber){
    uiState.chamberNames=uiState.chamberNames||{};
    if(v&&v!==renameChamber) uiState.chamberNames[renameChamber]=v;
    else delete uiState.chamberNames[renameChamber];   // empty or unchanged = back to the default
    saveUiState();
    renamePop.classList.remove('show'); renameChamber=null; return;
  }
  if(renameCaste&&v){
    uiState.castes[renameCaste]=Object.assign({},uiState.castes[renameCaste],{name:v});
    applyUiState();saveUiState();
  }
  renamePop.classList.remove('show');renameCaste=null;renameChamber=null;
}
document.getElementById('rename-save').addEventListener('click',commitRename);
document.getElementById('rename-cancel').addEventListener('click',()=>{renamePop.classList.remove('show');renameCaste=null;});
document.getElementById('rename-input').addEventListener('keydown',e=>{if(e.key==='Enter')commitRename();if(e.key==='Escape'){renamePop.classList.remove('show');renameCaste=null;}});

// -- Ant Config page -----------------------------------------------------------
let availableModels=[];
let antcfgCatalog=[];       // provider catalog (ollama + keyed providers), from /providers/catalog
/**
 * v2.14.13: the live model-route map, shared by the Ant Config page and the Ant Inspector.
 *
 * This MUST be kept whole. `AnthillRuntime.ApplySettingsUpdate` does `dict[key] = value`, so
 * POST /settings {model_routes:{...}} REPLACES the entire route map rather than merging it.
 * Any caller that posts a partial map silently resets every route it omitted back to the
 * profile default. Both writers therefore merge into this cache and post all of it.
 */
let modelRoutes={};
let antRouteCatalogReady=false, antRouteCatalogLoading=null;

/** Load the model catalog + current routes once, for whichever surface asks first. */
async function ensureAntRouteCatalog(){
  if(antRouteCatalogReady) return;
  if(!antRouteCatalogLoading){
    antRouteCatalogLoading=(async()=>{
      await Promise.all([fetchModelNames(),fetchProviderCatalog()]);
      try{ const s=await api('/settings'); if(s.success) modelRoutes=s.data.model_routes||{}; }catch{}
      antRouteCatalogReady=true;
    })();
  }
  await antRouteCatalogLoading;
}

/** Merge one role's route into the full map and persist the WHOLE map. See modelRoutes above. */
async function saveModelRoute(updates){
  const merged=Object.assign({},modelRoutes);
  Object.keys(updates).forEach(k=>{ merged[k]=updates[k]; });
  const r=await api('/settings','POST',{model_routes:merged});
  if(!r||!r.success) throw new Error((r&&r.message)||'route update rejected');
  modelRoutes=merged;
  return merged;
}
let antcfgConfigured=new Set(); // provider ids that currently have a working key saved

async function fetchModelNames(){
  try{
    const r=await fetch(url('/ollama/models'),{headers:{'Authorization':'Bearer '+TOKEN}});
    const d=await r.json();
    availableModels=(d.models||[]).map(m=>m.name||m.model).filter(Boolean);
  }catch{availableModels=[];}
}

async function fetchProviderCatalog(){
  try{
    const [catRes,connRes]=await Promise.all([api('/providers/catalog'),api('/providers')]);
    antcfgCatalog=catRes.success?(catRes.data||[]):[];
    antcfgConfigured=new Set((connRes.success?(connRes.data||[]):[]).filter(c=>c.configured).map(c=>c.provider));
  }catch{antcfgCatalog=[];antcfgConfigured=new Set();}
}

// v0.3.8.55: the ant-config globals (options builders, the colony-wide priority panel,
// openAntConfig, save/reset) live in inspector-routing.js — the inspector/routing domain's own
// asset, under the app.js size guard.

// -- Autonomy page (Phase 1/2: Colony Director, objective backlog, Strategist runs) -------------
let autonomyObjectives=[];
let autoRefreshTimer=null;

async function openAutonomy(){
  // Data-loader only — do NOT call showPage from here. Invoked exclusively from
  // PAGE_ENTER['projects'] (v0.3.8.55: the Automation panel lives in the Projects page's right
  // column), which showPage() itself calls *after* switching to the page.
  // Calling showPage from in here used to re-fire the page-enter hook -> openAutonomy() ->
  // showPage() in an unbounded mutual-recursion loop: every single visit to the Autonomy page
  // (including clicking Start/Stop, which reloads this page's status) burned the call stack down
  // to a "RangeError: Maximum call stack size exceeded" within milliseconds. Recoverable on its
  // own (the thrown error just aborts that call chain), but it froze page interaction long enough
  // that clicks — like the kill switch's Stop button — appeared to hang or crash the app.
  await reloadAutonomy();
  if(autoRefreshTimer) clearInterval(autoRefreshTimer);
  // Keeps the status card (running/idle, budgets, kill switch) live while this page is open;
  // self-cancels the moment the user navigates elsewhere so it never polls in the background.
  autoRefreshTimer=setInterval(()=>{
    // v0.3.8.55: the panel lives inside page-projects now — poll only while that page is open.
    if(!document.getElementById('page-projects')?.classList.contains('active')){
      clearInterval(autoRefreshTimer); autoRefreshTimer=null; return;
    }
    reloadAutonomyStatus();
  },4000);
}
// v0.3.8.55: no PAGE_ENTER['autonomy'] — showPage remaps 'autonomy' to 'projects' before the
// hook lookup, and PAGE_ENTER['projects'] runs openAutonomy for admins.

async function reloadAutonomy(){
  await reloadObjectives();
  await Promise.all([reloadAutonomyStatus(), reloadAutonomyRuns()]);
}

document.getElementById('auto-refresh').addEventListener('click',reloadAutonomy);

// Collapsible table boxes (Autonomy page): header click folds the body; caret tracks state.
function toggleBoxBody(id,hdr){
  const box=document.getElementById(id); if(!box) return;
  const hidden=box.style.display==='none';
  box.style.display=hidden?'':'none';
  const caret=hdr.querySelector('.box-caret'); if(caret) caret.textContent=hidden?'▸':'▾';
}

function autoMsg(t,ok){
  const m=document.getElementById('auto-msg');
  m.style.color=ok?'var(--green)':'var(--red)'; m.textContent=t;
  setTimeout(()=>{ if(m.textContent===t) m.textContent=''; },3500);
}

async function reloadAutonomyStatus(){
  try{
    const r=await api('/autonomy/status'); if(!r.success) throw new Error(r.message);
    const s=r.data||{};
    document.getElementById('auto-disabled-note').style.display=s.enabled?'none':'block';
    document.getElementById('auto-start-btn').disabled=!s.enabled||!!s.running;
    document.getElementById('auto-stop-btn').disabled=!s.running;
    const runEl=document.getElementById('auto-running');
    runEl.textContent=s.running?'RUNNING':(s.enabled?'IDLE':'OFF');
    runEl.style.color=s.running?'var(--green)':(s.enabled?'var(--queen)':'var(--dim)');
    setEl('auto-poll-sub','poll every '+(s.poll_seconds??'—')+'s');
    setEl('auto-hour',`${s.missions_last_hour??0}/${s.max_missions_per_hour??'—'}`);
    setEl('auto-day',`${s.missions_last_day??0}/${s.max_missions_per_day??'—'}`);
    setEl('auto-backlog',`${s.backlog_pending??0}/${s.backlog_active??0}`);
    const killEngaged=!!s.kill_switch_engaged;
    document.getElementById('auto-kill-dot').className='kill-dot '+(killEngaged?'engaged':'ok');
    setEl('auto-kill-text',killEngaged?'Kill switch engaged':'Kill switch clear');
    setEl('auto-next',s.next_objective?`${s.next_objective.title} (priority ${s.next_objective.priority})`:'— backlog empty —');
    setEl('auto-decision',s.budget_decision||'—');
    setEl('auto-conc',`${s.concurrency_effective??s.concurrency_configured??1}/${s.concurrency_configured??1}`);
    setEl('auto-conc-sub',(s.governor_code&&s.governor_code!=='ok')?('governed: '+s.governor_code):'effective / configured');
    setEl('auto-governor',s.governor_reason||(s.running?'— first cycle pending —':'—'));
    const inflight=s.in_flight||[];
    setEl('auto-inflight',inflight.length
      ?inflight.map(f=>`${f.objective_title} (${f.job_status}${f.mission_id?', mission '+String(f.mission_id).slice(0,8):''})`).join(' · ')
      :'— none —');
    const badge=document.getElementById('nav-autonomy-badge');
    if(badge){
      if(s.running){ badge.style.display=''; badge.textContent='●'; badge.style.color='var(--green)'; }
      else badge.style.display='none';
    }
  }catch(e){ setEl('auto-decision','Error: '+e.message); }
}

document.getElementById('auto-start-btn').addEventListener('click',async()=>{
  try{
    const r=await api('/autonomy/start','POST');
    autoMsg(r.success?'Director started.':(r.message||'Failed'),r.success);
    await reloadAutonomyStatus();
  }catch(e){ autoMsg('Failed: '+e.message,false); }
});
document.getElementById('auto-stop-btn').addEventListener('click',async()=>{
  if(!await uiConfirm('Stop the Colony Director and engage the kill switch?')) return;
  try{
    const r=await api('/autonomy/stop','POST');
    autoMsg(r.success?'Director stopped; kill switch engaged.':(r.message||'Failed'),r.success);
    await reloadAutonomyStatus();
  }catch(e){ autoMsg('Failed: '+e.message,false); }
});

async function reloadObjectives(){
  const tb=document.getElementById('obj-tbody');
  try{
    const r=await api('/objectives'); if(!r.success) throw new Error(r.message);
    // Terminal objectives (loop-retired, or completed/stopped as 'done') move to the Completed
    // Objectives box below — hide them from the active backlog. Resumable pauses stay here.
    autonomyObjectives=(r.data||[]).filter(o=>o.retired_code!=='looping_goals' && o.status!=='done');
    setEl('obj-count',`Objectives (${autonomyObjectives.length})`);
    reloadCompletedObjectives();
    tb.innerHTML=autonomyObjectives.map(o=>{
      const paused=o.status==='paused';
      return `<tr>
        <td style="max-width:160px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${escapeHtml(o.title)}</td>
        <td class="obj-charter" title="${escapeHtml(o.charter||'')}">${escapeHtml(o.charter||'')}</td>
        <td style="white-space:nowrap;">
          <button class="obj-prio-btn" data-onclick="objAdjustPriority('${o.id}',${o.priority-1})" title="Lower priority">-</button>
          <span style="font-family:var(--mono);font-size:11px;margin:0 3px;">${o.priority}</span>
          <button class="obj-prio-btn" data-onclick="objAdjustPriority('${o.id}',${o.priority+1})" title="Raise priority">+</button>
        </td>
        <td><span class="obj-status ${o.status}">${o.status}</span></td>
        <td style="font-family:var(--mono);font-size:11px;">${o.run_count}${o.max_runs?'/'+o.max_runs:''}</td>
        <td style="font-family:var(--mono);font-size:11px;color:${o.success_ema==null?'var(--dim)':(o.success_ema>=0.5?'var(--green)':(o.success_ema>=0.25?'var(--queen)':'var(--red)'))}" title="Success score EMA — biases selection priority (Phase 4 learning)">${o.success_ema==null?'—':o.success_ema.toFixed(2)}</td>
        <td style="font-family:var(--mono);font-size:11px;color:${o.consecutive_failures>0?'var(--red)':'var(--dim)'}">${o.consecutive_failures}</td>
        <td>${o.last_run_at?fmtTime(o.last_run_at):'never'}</td>
        <td style="white-space:nowrap;">
          <button class="job-btn view" data-onclick="objToggleStatus('${o.id}','${paused?'active':'paused'}')">${paused?'Resume':'Pause'}</button>
          <button class="job-btn cancel" data-onclick="objDelete('${o.id}')">Delete</button>
        </td>
      </tr>`;
    }).join('')||'<tr><td colspan="9" style="color:var(--dim);text-align:center;padding:16px;">No objectives yet — add one above.</td></tr>';
  }catch(e){ tb.innerHTML=`<tr><td colspan="9" style="color:var(--red)">Error: ${escapeHtml(e.message)}</td></tr>`; }
}

// -- Completed Objectives (Director-ended: completed / stopped / retired / failed) --
// Badge styling per canonical end_reason (v1.8.23).
const OBJ_END_BADGE={
  completed_successfully:['Completed','rgba(16,185,129,.14)','var(--green)'],
  stopped_no_followup_required:['Stopped','rgba(59,130,246,.14)','var(--blue)'],
  retired_looping:['Retired · Looping','rgba(var(--queen-rgb),.14)','var(--queen)'],
  failed:['Failed','rgba(239,68,68,.14)','var(--red)'],
  manually_paused:['Paused','rgba(var(--queen-rgb),.14)','var(--queen)'],
  manually_stopped:['Stopped','rgba(255,255,255,.06)','var(--dim)'],
};
async function reloadCompletedObjectives(){
  const list=document.getElementById('completed-obj-list'); if(!list) return;
  try{
    const r=await api('/objectives/completed'); if(!r.success) throw new Error(r.message);
    const rows=r.data||[];
    setEl('completed-obj-count',`Completed Objectives (${rows.length})`);
    if(!rows.length){ list.innerHTML='<div style="color:var(--dim);font-size:11px;padding:8px 0;">None — objectives the Director has completed, stopped, or retired will appear here.</div>'; return; }
    list.innerHTML=rows.map(o=>{
      const [blabel,bbg,bcol]=OBJ_END_BADGE[o.end_reason]||['Ended','rgba(255,255,255,.06)','var(--dim)'];
      const pc=o.patch_counts||{};
      const patchTag=pc.total?`<span class="obj-status" style="background:rgba(59,130,246,.10);color:var(--blue)" title="Patches produced">${pc.total} patch${pc.total>1?'es':''}</span>`:'';
      return `
      <details class="card" style="margin-bottom:6px;padding:0;" data-obj="${o.id}" data-ontoggle="onCompletedToggle(this)">
        <summary style="cursor:pointer;list-style:none;display:flex;align-items:center;gap:10px;padding:9px 12px;user-select:none;">
          <span style="flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:12px;font-weight:600;">${escapeHtml(o.title||'')}</span>
          ${patchTag}
          <span class="obj-status" style="background:${bbg};color:${bcol};">${blabel}</span>
          <span class="box-caret" style="color:var(--dim);font-size:11px;">▾</span>
        </summary>
        <div style="padding:2px 14px 12px;border-top:1px solid var(--border);font-size:11px;color:var(--muted);">
          <div style="margin:6px 0;color:${bcol};">${escapeHtml(o.end_detail||o.end_reason_label||'Ended.')}</div>
          <div class="co-detail" style="color:var(--dim);">Expand to load related runs, missions, tasks, and patches…</div>
        </div>
      </details>`;}).join('');
  }catch(e){ list.innerHTML=`<div style="color:var(--red);font-size:11px;">Error: ${escapeHtml(e.message)}</div>`; }
}

async function onCompletedToggle(det){
  const caret=det.querySelector('summary .box-caret'); if(caret) caret.textContent=det.open?'▾':'▸';
  if(!det.open || det.dataset.loaded) return;
  det.dataset.loaded='1';
  const box=det.querySelector('.co-detail');
  try{
    const r=await api('/objectives/'+encodeURIComponent(det.dataset.obj)+'/detail'); if(!r.success) throw new Error(r.message);
    const d=r.data||{};
    const kv=(k,v)=>`<div style="display:flex;gap:8px;padding:2px 0;"><span style="color:var(--dim);min-width:130px;">${k}</span><span style="font-family:var(--mono);word-break:break-all;">${escapeHtml(String(v??'—'))}</span></div>`;
    const runs=(d.runs||[]).map(x=>`<div style="padding:2px 0;">• ${escapeHtml((x.generated_goal||'').slice(0,110))} <span style="color:var(--dim)">(${escapeHtml(x.mission_status||'—')})</span></div>`).join('')||'<div style="color:var(--dim)">none</div>';
    const missions=(d.missions||[]).map(x=>`<div style="padding:2px 0;">• <span style="font-family:var(--mono)">${escapeHtml(String(x.id||'').slice(0,8))}</span> ${escapeHtml((x.goal||'').slice(0,90))} <span style="color:var(--dim)">(${escapeHtml(x.status||'—')})</span></div>`).join('')||'<div style="color:var(--dim)">none</div>';
    const tasks=(d.tasks||[]).map(x=>`<div style="padding:1px 0;">• ${escapeHtml(x.ant||'')}: ${escapeHtml((x.title||'').slice(0,80))} <span style="color:var(--dim)">(${escapeHtml(x.status||'')})</span></div>`).join('')||'<div style="color:var(--dim)">none</div>';
    const pc=d.patch_counts||{};
    const patchBlock=pc.total
      ? `<div>${escapeHtml(patchSummaryText(pc))} · <a href="#" data-onclick="openPatches({objective_id:'${d.id}'});return false;" style="color:var(--blue)">Open in Changes →</a></div>`
      : '<div style="color:var(--dim)">No patches produced.</div>';
    box.innerHTML=
      kv('Objective ID', d.id)+kv('Original title', d.title)+
      kv('End reason', d.end_reason_label||d.end_reason)+kv('Detail', d.end_detail||d.retired_reason)+
      kv('Runs', (d.runs||[]).length)+kv('Missions', (d.missions||[]).length)+
      kv('Tasks', (d.tasks||[]).length)+kv('Ended at', d.ended_at||d.retired_at)+
      `<div style="margin-top:8px;color:var(--muted);font-weight:600;">Patch activity</div>${patchBlock}`+
      `<div style="margin-top:8px;color:var(--muted);font-weight:600;">Related runs</div>${runs}`+
      `<div style="margin-top:6px;color:var(--muted);font-weight:600;">Related missions</div>${missions}`+
      `<div style="margin-top:6px;color:var(--muted);font-weight:600;">Related tasks</div>${tasks}`;
  }catch(e){ box.innerHTML=`<span style="color:var(--red)">Could not load detail: ${escapeHtml(e.message)}</span>`; }
}

document.getElementById('obj-add').addEventListener('click',async()=>{
  const title=document.getElementById('obj-title').value.trim();
  const charter=document.getElementById('obj-charter').value.trim();
  const priority=parseInt(document.getElementById('obj-priority').value,10)||0;
  const maxRuns=parseInt(document.getElementById('obj-maxruns').value,10)||0;
  if(!title||!charter){ autoMsg('Title and charter are required.',false); return; }
  try{
    const r=await api('/objectives','POST',{title,charter,priority,max_runs:maxRuns});
    if(r.success){
      document.getElementById('obj-title').value='';
      document.getElementById('obj-charter').value='';
      document.getElementById('obj-priority').value='0';
      document.getElementById('obj-maxruns').value='0';
      await reloadObjectives();
      // v0.3.8.55 (operator's rule): an objective is a request for WORK — adding one starts the
      // Director if it is enabled and idle, so the backlog never sits behind a button on another
      // page. A refused start (kill switch, disabled) is REPORTED, never silent.
      apiCacheBust('/autonomy');
      const st=await api('/autonomy/status');
      const s=(st&&st.success&&st.data)||{};
      if(s.running){ autoMsg('Objective added — Director already running.',true); }
      else if(!s.enabled){ autoMsg('Objective added. Autonomy is disabled in config — enable it in Settings to run the backlog.',true); }
      else{
        const go=await api('/autonomy/start','POST');
        apiCacheBust('/autonomy');
        if(go&&go.success) autoMsg('Objective added — Director started.',true);
        else autoMsg('Objective added, but the Director did not start: '+((go&&go.message)||'refused')+'. Start it from the Colony Overview.',false);
      }
    } else autoMsg(r.message||'Failed',false);
  }catch(e){ autoMsg('Failed: '+e.message,false); }
});

// v0.3.8.55 (operator's rule): the Projects backlog keeps its own Start. Same endpoint as the
// overview card's button; the answer — started, already running, disabled, kill switch — is
// always said out loud in auto-msg.
document.getElementById('proj-auto-start')?.addEventListener('click',async()=>{
  const r=await api('/autonomy/start','POST');
  apiCacheBust('/autonomy');
  autoMsg((r&&r.success)?'Director started.':((r&&r.message)||'The Director did not start.'),!!(r&&r.success));
  if(typeof reloadAutonomyStatus==='function') reloadAutonomyStatus();
});

async function objToggleStatus(id,status){
  try{
    const r=await api('/objectives/'+encodeURIComponent(id),'PATCH',{status});
    if(r.success){ autoMsg(status==='paused'?'Objective paused.':'Objective resumed.',true); await reloadObjectives(); }
    else autoMsg(r.message||'Failed',false);
  }catch(e){ autoMsg('Failed: '+e.message,false); }
}
async function objAdjustPriority(id,priority){
  try{
    const r=await api('/objectives/'+encodeURIComponent(id),'PATCH',{priority});
    if(r.success) await reloadObjectives(); else autoMsg(r.message||'Failed',false);
  }catch(e){ autoMsg('Failed: '+e.message,false); }
}
async function objDelete(id){
  if(!await uiConfirm('Delete this objective? This cannot be undone.')) return;
  try{
    const r=await api('/objectives/'+encodeURIComponent(id),'DELETE');
    if(r.success){ autoMsg('Objective deleted.',true); await reloadObjectives(); }
    else autoMsg(r.message||'Failed',false);
  }catch(e){ autoMsg('Failed: '+e.message,false); }
}

async function reloadAutonomyRuns(){
  const tb=document.getElementById('auto-runs-tbody');
  try{
    const r=await api('/autonomy/runs'); if(!r.success) throw new Error(r.message);
    const runs=r.data||[];
    setEl('auto-runs-count',`Recent Autonomous Runs (${runs.length})`);
    const titleById={}; autonomyObjectives.forEach(o=>{ titleById[o.id]=o.title; });
    tb.innerHTML=runs.map(run=>{
      const status=run.mission_status||'—';
      const statusColor=status==='complete'?'var(--green)':status==='failed'?'var(--red)':status==='partial'?'var(--orange)':'var(--dim)';
      return `<tr>
        <td>${escapeHtml(titleById[run.objective_id]||run.objective_id||'—')}</td>
        <td class="obj-charter" title="${escapeHtml(run.generated_goal||'')}">${escapeHtml(run.generated_goal||'')}</td>
        <td style="color:${statusColor}">${status}</td>
        <td style="font-family:var(--mono);font-size:11px;">${run.success_score!=null?Number(run.success_score).toFixed(2):'—'}</td>
        <td style="font-family:var(--mono);font-size:11px;">${run.follow_ups_created||0}</td>
        <td style="font-size:11px;">${patchCellHtml(run.patch_counts,run.mission_id)}</td>
        <td>${run.started_at?fmtTime(run.started_at):'—'}</td>
        <td>${run.mission_id?`<button class="job-btn view" data-onclick="openResults('${run.mission_id}')">View</button>`:'<span style="color:var(--dim);font-size:10px">no mission</span>'}</td>
      </tr>`;
    }).join('')||'<tr><td colspan="8" style="color:var(--dim);text-align:center;padding:16px;">No autonomous runs yet.</td></tr>';
  }catch(e){ tb.innerHTML=`<tr><td colspan="8" style="color:var(--red)">Error: ${escapeHtml(e.message)}</td></tr>`; }
}

// -- Pheromone memory page -----------------------------------------------------
async function openPheromones(){
  showPage('pheromones');
  await reloadPheromones();
}

PAGE_ENTER['pheromones']=()=>{ reloadPheromones(); loadSourceQuality(); };

/* v0.3.8.46 — research source-quality trails, shown at last. The endpoint has served this text
 * since v2.x with no reader; it lives on the signals page because that is what it is. */
async function loadSourceQuality(){
  const el=document.getElementById('source-quality-body'); if(!el) return;
  try{ el.textContent=(await apiText('/source-quality'))||'No source-quality trails recorded yet.'; }
  catch(e){ el.textContent='Source-quality report unavailable: '+((e&&e.message)||e); }
}

/* v0.3.8.46 — the Readiness page: the qualification snapshot with attestation, the certification
 * download, the qualification-report action, and the colony's introspection. The one rule carried
 * over from the backend: unmeasured reads as NOT ready, and nothing here can be satisfied by
 * silence — the page renders failures first and never summarises them away. */
async function loadReadiness(){
  const stmt=document.getElementById('rd-statement'), checks=document.getElementById('rd-checks');
  const intro=document.getElementById('rd-introspection');
  if(!checks) return;
  try{
    const r=await api('/readiness/json');
    if(!(r&&r.success&&r.data)){ checks.innerHTML='<div class="hud-state err">Readiness snapshot unavailable.</div>'; }
    else{
      const d=r.data;
      if(stmt) stmt.textContent=(d.ready?'READY — ':'NOT READY — ')+(d.statement||'')+` (${d.satisfied}/${d.total})`;
      const attestable=new Set(d.attestable_ids||[]);
      const rows=[...(d.checks||[])].sort((a,b)=>(a.satisfied?1:0)-(b.satisfied?1:0));
      checks.innerHTML=rows.map(c=>`<div class="rd-check${c.satisfied?'':' fail'}" data-check="${escapeHtml(c.id)}">`
        + `<span class="rd-flag">${c.satisfied?'PASS':'FAIL'}</span> <b>${escapeHtml(c.title)}</b>`
        + ` <span class="rd-kind">${escapeHtml(c.kind||'')}</span>`
        + `<div class="rd-detail">${escapeHtml(c.detail||'')}</div>`
        + (attestable.has(c.id)?`<div class="rd-attest">`
            + `<input type="text" class="rd-note" placeholder="Attestation note (why you are satisfied it holds)">`
            + `<button class="btn btn-ghost rd-attest-yes">Attest: holds</button>`
            + `<button class="btn btn-ghost rd-attest-no">Attest: does not hold</button></div>`:'')
        + `</div>`).join('') || '<div class="hud-state">No thresholds defined.</div>';
      checks.querySelectorAll('.rd-check').forEach(card=>{
        const send=async satisfied=>{
          const note=card.querySelector('.rd-note')?.value.trim()||'';
          const r2=await api('/readiness/attest','POST',{threshold_id:card.dataset.check, satisfied, note});
          setEl('rd-msg', r2&&r2.success?'Attestation recorded.':(r2&&r2.message)||'Attestation failed.');
          if(r2&&r2.success) loadReadiness();
        };
        card.querySelector('.rd-attest-yes')?.addEventListener('click',()=>send(true));
        card.querySelector('.rd-attest-no')?.addEventListener('click',()=>send(false));
      });
    }
  }catch(e){ checks.innerHTML=`<div class="hud-state err">${escapeHtml(String(e&&e.message||e))}</div>`; }
  try{
    const r=await api('/colony/introspection');
    if(!(r&&r.success&&r.data)){ if(intro) intro.innerHTML='<div class="hud-state err">Introspection unavailable.</div>'; return; }
    const d=r.data;
    const chip=(label,val,tone)=>`<span class="rd-chip"><span>${escapeHtml(label)}</span> <b${tone?` style="color:${tone}"`:''}>${escapeHtml(String(val))}</b></span>`;
    const on=v=>v?'on':'off';
    const findings=(d.config_health||[]);
    if(intro) intro.innerHTML =
      `<div class="rd-chips">`
      + chip('Version', d.version)
      + chip('Activation tier', d.activation_tier)
      + chip('Autonomy', on(d.autonomy_enabled))
      + chip('Stop engaged', d.stop_engaged?'YES':'no', d.stop_engaged?'var(--red,#f87171)':'')
      + chip('Director', d.director_running?'running':'idle')
      + chip('File writing', on(d.can_write_files))
      + chip('Patch application', on(d.can_apply_patches))
      + chip('Auto-apply', on(d.auto_apply_enabled))
      + chip('Running jobs', d.running_jobs)
      + chip('V3 qualified', d.v3_qualified?'yes':'no', d.v3_qualified?'var(--green,#34d399)':'var(--dim)')
      + `</div>`
      + `<div class="rd-detail" style="margin-top:8px">Executable roles: ${escapeHtml((d.executable_roles||[]).join(', '))}</div>`
      + (findings.length
          ? findings.map(f=>`<div class="rd-check fail" style="margin-top:6px"><span class="rd-flag">${escapeHtml((f.severity||'').toUpperCase())}</span> `
              + `<b>${escapeHtml(f.combination||'')}</b><div class="rd-detail">${escapeHtml(f.detail||'')}</div></div>`).join('')
          : `<div class="rd-detail" style="margin-top:8px;color:var(--green,#34d399)">Configuration is healthy — no incompatible combinations.</div>`);
  }catch(e){ if(intro) intro.innerHTML=`<div class="hud-state err">${escapeHtml(String(e&&e.message||e))}</div>`; }
}
PAGE_ENTER['readiness']=()=>{ if(ROLE==='admin') loadReadiness(); };
document.getElementById('rd-reload')?.addEventListener('click', ()=>loadReadiness());
document.getElementById('rd-report')?.addEventListener('click', async ()=>{
  const r=await api('/readiness/qualification-report','POST',{});
  setEl('rd-msg', r&&r.success?('Written: '+((r.data&&r.data.markdown_path)||'report')):(r&&r.message)||'Report failed.');
});
document.getElementById('rd-cert')?.addEventListener('click', async ()=>{
  try{
    const resp=await fetch(url('/readiness/certification'),{headers:{'Authorization':'Bearer '+TOKEN}});
    if(!resp.ok){ setEl('rd-msg','Certification failed ('+resp.status+').'); return; }
    const blob=await resp.blob();
    const a=document.createElement('a');
    a.href=URL.createObjectURL(blob); a.download='anthill-readiness-certification.txt';
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(()=>URL.revokeObjectURL(a.href), 5000);
  }catch(e){ setEl('rd-msg','Certification failed: '+((e&&e.message)||e)); }
});

// -- Security page -------------------------------------------------------------
const SEC_GATES=[
  ['web_search_enabled','Web Search','Read-only external research (DuckDuckGo). No API key.'],
  ['file_tools_enabled','File Read Tools','Let ants list dirs and read files inside the workspace.'],
  ['file_writing_enabled','File Writing','Allow approved patches to write files (still needs approval).'],
  ['patch_application_enabled','Patch Application','Allow /apply to write approved patches to disk.'],
  ['shell_tool_enabled','AI Shell Tool','Allowlisted shell for the AI ants. High risk.'],
];
let secSettings={};

PAGE_ENTER['security']=()=>{ if(ROLE==='admin') loadSecurity(); };
document.getElementById('sec-refresh').addEventListener('click',loadSecurity);
document.getElementById('sec-save').addEventListener('click',saveSecurity);

async function loadSecurity(){
  try{ const r=await api('/settings'); if(!r.success) return; secSettings=r.data; }catch{ return; }
  const bind=secSettings.api_host==='0.0.0.0'?'All interfaces':secSettings.api_host;
  setEl('sec-auth', secSettings.api_auth_enabled===false?'DISABLED':'Password login');
  setEl('sec-profile', secSettings.safety_profile||'—');
  setEl('sec-bind', bind);
  setEl('sec-enc','AES-256-GCM');
  document.getElementById('sec-auth').style.color = secSettings.api_auth_enabled===false?'var(--red)':'var(--green)';
  document.getElementById('sec-bind').style.color = secSettings.api_host==='0.0.0.0'?'var(--queen)':'var(--green)';
  const tog=(k,label,desc)=>`<div class="toggle-row" style="align-items:flex-start"><div><div>${label}</div><div style="font-size:10px;color:var(--dim)">${desc}</div></div><div class="toggle-sw${secSettings[k]?' on':''}" data-key="${k}" data-onclick="this.classList.toggle('on')"></div></div>`;
  document.getElementById('sec-toggles').innerHTML=SEC_GATES.map(g=>tog(...g)).join('');
  document.getElementById('sec-shell-toggle').innerHTML=tog('operator_shell_enabled','Operator Shell (host terminal)','Admin-only interactive shell into this host. Remote code execution.');
  document.getElementById('sec-autoapply-toggle').innerHTML=tog('autonomy_autoapply_enabled','Enable auto-apply','Director applies allowlisted patches that verify green, without review.');
  (function(){var sw=document.querySelector('#sec-autoapply-toggle .toggle-sw');if(sw)sw.addEventListener('click',function(){var pa=document.getElementById('sec-autoapply-paths');if(sw.classList.contains('on')&&pa&&!pa.value.trim())pa.value='docs/**\nsrc/**';});})();
  document.getElementById('sec-autoapply-git').innerHTML=tog('autonomy_autoapply_git_commit','Git-commit verified changes','After a green verify, commit the change on the standalone branch (never main).');
  document.getElementById('sec-autoapply-git-push').innerHTML=tog('autonomy_autoapply_git_push','Push branch to origin','After commit, push the standalone branch via the SSH deploy key. Never pushes or merges main.');
  document.getElementById('sec-autoapply-git-user').value=secSettings.autonomy_autoapply_git_username||'';
  document.getElementById('sec-autoapply-git-remote').value=secSettings.autonomy_autoapply_git_remote||'origin';
  document.getElementById('sec-autoapply-git-key').value=secSettings.autonomy_autoapply_git_ssh_key_path||'';
  secSyncGitBranch();
  document.getElementById('sec-workspace').value=secSettings.agent_workspace_dir||'';
  document.getElementById('sec-shell-dir').value=secSettings.operator_shell_dir||'';
  document.getElementById('sec-autoapply-paths').value=(secSettings.autonomy_autoapply_paths||[]).join('\n');
  document.getElementById('sec-autoapply-maxlines').value=secSettings.autonomy_autoapply_max_lines??40;
  document.getElementById('sec-autoapply-timeout').value=secSettings.autonomy_autoapply_verify_timeout??900;
  document.getElementById('sec-autoapply-verify').value=secSettings.autonomy_autoapply_verify_cmd||'';
}

function secSyncGitBranch(){
  const u=(document.getElementById('sec-autoapply-git-user').value||'').trim();
  const el=document.getElementById('sec-autoapply-git-branch');
  if(el) el.value=u?`${u}-anthill`:'';
}
async function saveSecurity(){
  const payload={};
  // Include the Autonomous Auto-Apply toggles — they live in their own containers, so they were
  // previously never collected here and toggling "Enable auto-apply" / "Git-commit verified changes"
  // had no effect on save.
  document.querySelectorAll('#sec-toggles .toggle-sw,#sec-shell-toggle .toggle-sw,#sec-autoapply-toggle .toggle-sw,#sec-autoapply-git .toggle-sw,#sec-autoapply-git-push .toggle-sw').forEach(sw=>{ payload[sw.dataset.key]=sw.classList.contains('on'); });
  const ws=document.getElementById('sec-workspace').value.trim(); if(ws) payload.agent_workspace_dir=ws;
  payload.operator_shell_dir=document.getElementById('sec-shell-dir').value.trim();
  payload.autonomy_autoapply_paths=document.getElementById('sec-autoapply-paths').value.split('\n').map(s=>s.trim()).filter(Boolean);
  payload.autonomy_autoapply_max_lines=parseInt(document.getElementById('sec-autoapply-maxlines').value,10)||40;
  payload.autonomy_autoapply_verify_timeout=parseInt(document.getElementById('sec-autoapply-timeout').value,10)||900;
  payload.autonomy_autoapply_verify_cmd=document.getElementById('sec-autoapply-verify').value.trim();
  payload.autonomy_autoapply_git_username=document.getElementById('sec-autoapply-git-user').value.trim();
  payload.autonomy_autoapply_git_remote=document.getElementById('sec-autoapply-git-remote').value.trim()||'origin';
  payload.autonomy_autoapply_git_ssh_key_path=document.getElementById('sec-autoapply-git-key').value.trim();
  const msg=document.getElementById('sec-msg');
  try{
    const r=await api('/settings','POST',payload);
    msg.style.color=r.success?'var(--green)':'var(--red)'; msg.textContent=r.success?'Saved.':(r.message||'Failed');
    if(r.success) await loadSecurity();
  }catch(e){ msg.style.color='var(--red)'; msg.textContent='Failed: '+e.message; }
  setTimeout(()=>{ msg.textContent=''; },3500);
}

// -- Shell page (admin-only host terminal) -------------------------------------
let shHistory=[], shHistIdx=-1, shBusy=false;

PAGE_ENTER['shell']=()=>{ if(ROLE==='admin') initShell(); };
document.getElementById('sh-clear').addEventListener('click',()=>{ const o=document.getElementById('sh-output'); o.innerHTML=''; delete o.dataset.banner; showShellBanner(); });
document.getElementById('sh-run').addEventListener('click',runShell);

// v0.3.8.49 (§14): quick actions are rendered from the platform-aware list /shell/info returns, so the
// console never offers a Linux command on a Windows host. A "danger" action (restart/stop) confirms
// first; the rest run immediately. Rebuilt on each shell open in case the host changed.
function renderShellQuickActions(platform, actions){
  const host=document.getElementById('sh-quick');
  const label=document.getElementById('sh-quick-label');
  if(!host) return;
  // clear everything except the leading "Quick:" label
  [...host.querySelectorAll('.sh-qbtn')].forEach(b=>b.remove());
  const none=host.querySelector('.sh-quick-none'); if(none) none.remove();
  if(!actions||!actions.length){
    if(label) label.style.display='none';
    const n=document.createElement('span'); n.className='sh-quick-none';
    n.style.cssText='font-size:10px;color:var(--dim);align-self:center;';
    n.textContent='No quick actions for this environment ('+(platform||'unknown')+') — type commands directly.';
    host.appendChild(n); return;
  }
  if(label) label.style.display='';
  for(const a of actions){
    const b=document.createElement('button');
    b.className='btn btn-ghost sh-qbtn';
    b.style.cssText='font-size:10px;padding:3px 8px;'+(a.danger?'color:var(--queen);':'');
    b.textContent=a.label;
    b.title=a.command;
    b.addEventListener('click',async()=>{
      if(a.danger && !(await uiConfirm('Run "'+a.label+'" on this host?\n\n'+a.command+'\n\nThis may disrupt the running service.'))) return;
      const inp=document.getElementById('sh-input'); if(inp.disabled) return;
      inp.value=a.command; runShell();
    });
    host.appendChild(b);
  }
}
document.getElementById('sh-input').addEventListener('keydown',e=>{
  if(e.key==='Enter'){ e.preventDefault(); runShell(); }
  else if(e.key==='ArrowUp'){ e.preventDefault(); if(shHistory.length){ shHistIdx=Math.max(0,shHistIdx-1); e.target.value=shHistory[shHistIdx]||''; } }
  else if(e.key==='ArrowDown'){ e.preventDefault(); if(shHistory.length){ shHistIdx=Math.min(shHistory.length,shHistIdx+1); e.target.value=shHistory[shHistIdx]||''; } }
});

function showShellBanner(){
  const out=document.getElementById('sh-output'); if(!out || out.dataset.banner) return;
  out.dataset.banner='1';
  const src=document.getElementById('sh-banner-src');
  const art=src?src.textContent.replace(/\s+$/,''):'';
  const ver=(typeof lastSystemSummary==='object'&&lastSystemSummary&&lastSystemSummary.version)?('v'+lastSystemSummary.version):'';
  out.innerHTML=`<pre style="margin:0 0 6px;color:var(--cyan);line-height:1.05;font-size:10px;white-space:pre;overflow-x:auto">${escapeHtml(art)}</pre>`+
    `<div style="color:var(--dim);margin-bottom:10px;">ANTHILL operator shell ${ver} · type a command and press Enter.</div>`;
}
async function initShell(){
  showShellBanner();
  try{
    const r=await api('/shell/info'); if(!r.success) throw new Error(r.message);
    const info=r.data||{};
    const enabled=!!info.enabled;
    document.getElementById('sh-disabled-note').style.display=enabled?'none':'block';
    document.getElementById('sh-input').disabled=!enabled;
    document.getElementById('sh-run').disabled=!enabled;
    setEl('sh-host', info.host?`(${info.host})`:'');
    setEl('sh-host2', (info.host||'host')+' · '+(info.os||''));
    setEl('sh-platform', info.platform||'unknown');
    // v0.3.8.49 (§14): render only the quick actions this platform supports.
    renderShellQuickActions(info.platform, info.quick_actions||[]);
    const dirEl=document.getElementById('sh-dir');
    if(!dirEl.value) dirEl.value=info.default_dir||'';
  }catch(e){ document.getElementById('sh-disabled-note').style.display='block'; }
}

function shAppend(html){ const o=document.getElementById('sh-output'); o.innerHTML+=html; o.scrollTop=o.scrollHeight; }

async function runShell(){
  if(shBusy) return;
  const inp=document.getElementById('sh-input'); const cmd=inp.value.trim(); if(!cmd) return;
  const dir=document.getElementById('sh-dir').value.trim();
  shHistory.push(cmd); shHistIdx=shHistory.length; inp.value=''; shBusy=true;
  document.getElementById('sh-run').disabled=true;
  shAppend(`<div style="color:var(--dim)"><span style="color:var(--green)">$</span> ${escapeHtml(cmd)}</div>`);
  try{
    const r=await api('/shell/exec','POST',{command:cmd,dir});
    if(!r.success){ shAppend(`<div style="color:var(--red)">${escapeHtml(r.message||'command failed')}</div>`); }
    else{
      const d=r.data||{};
      if(d.stdout) shAppend(`<div>${escapeHtml(d.stdout)}</div>`);
      if(d.stderr) shAppend(`<div style="color:var(--queen)">${escapeHtml(d.stderr)}</div>`);
      const ec=d.exit_code, col=ec===0?'var(--dim)':'var(--red)';
      shAppend(`<div style="color:${col};font-size:10px">[exit ${ec}${d.timed_out?' · timed out':''} · ${d.elapsed_seconds}s · ${escapeHtml(d.dir||'')}]</div>`);
      if(d.dir) document.getElementById('sh-dir').value=d.dir;
    }
  }catch(e){ shAppend(`<div style="color:var(--red)">${escapeHtml(e.message)}</div>`); }
  shBusy=false; document.getElementById('sh-run').disabled=false; inp.focus();
}

async function reloadPheromones(){
  const body=document.getElementById('phero-body');
  const kpis=document.getElementById('memory-kpis');
  const signals=document.getElementById('memory-signals');
  const results=document.getElementById('memory-results');
  const q=(document.getElementById('memory-query')?.value||'').trim();
  try{
    const r=await api('/memory/explorer?limit=120'+(q?'&q='+encodeURIComponent(q):''));if(!r.success) throw new Error(r.message);
    const data=r.data||{},sum=data.summary||{},trails=data.trails||[];
    setEl('phero-count',`${sum.missions||0} missions indexed / ${trails.length} trails${q?' / filtered':''}`);
    renderMemorySummary(sum,kpis);
    renderMemorySignals(sum,signals);
    renderMemoryResults(data,results);
    renderPheromoneTable(trails,body);
  }catch(e){
    body.innerHTML=`<div style="color:var(--red)">Error: ${escapeHtml(e.message)}</div>`;
    if(results) results.innerHTML=`<div style="color:var(--red)">Explorer error: ${escapeHtml(e.message)}</div>`;
  }
}

function renderMemorySummary(sum,box){
  if(!box) return;
  const cells=[
    ['Missions',sum.missions||0],['Tasks',sum.tasks||0],['Sources',sum.sources||0],['Patches',sum.patches||0],
    ['Trails',sum.trails||0],['Strong',sum.strong_trails||0],['Failure-led',sum.failure_dominant_trails||0],['Loop patterns',sum.loop_pattern_trails||0],
  ];
  box.innerHTML=cells.map(([l,v])=>`<div class="memory-kpi"><div class="v">${escapeHtml(v)}</div><div class="l">${escapeHtml(l)}</div></div>`).join('');
}

function renderMemorySignals(sum,box){
  if(!box) return;
  const total=Math.max(1,+sum.trails||0);
  const mk=(label,val,color)=>{const pct=Math.min(100,Math.round((val/total)*100));return `<div class="memory-signal"><span>${escapeHtml(label)}</span><div class="memory-signal-track"><div class="memory-signal-fill" style="width:${pct}%;background:${color}"></div></div><span>${pct}%</span></div>`;};
  box.innerHTML=[
    mk('Strong success',+sum.strong_trails||0,'var(--green)'),
    mk('Failure-dominant',+sum.failure_dominant_trails||0,'var(--red)'),
    mk('Loop patterns',+sum.loop_pattern_trails||0,'var(--purple)'),
  ].join('')+`<div style="font-size:10px;color:var(--dim);line-height:1.5;margin-top:4px;">Signals are derived from stored mission memory and pheromone trails. Use prune to archive weak or failure-heavy trails.</div>`;
}

function memoryItem(kind,title,copy,meta,chips=[]){
  return `<div class="memory-result"><div class="memory-result-top"><div><div class="memory-kind">${escapeHtml(kind)}</div><div class="memory-title">${escapeHtml(title||'Untitled')}</div></div><div class="memory-meta">${escapeHtml(meta||'')}</div></div>`+
    `<div class="memory-copy">${escapeHtml((copy||'').slice(0,260))}</div>`+
    (chips.length?`<div class="memory-chiprow">${chips.filter(Boolean).map(c=>`<span class="memory-chip">${escapeHtml(c)}</span>`).join('')}</div>`:'')+`</div>`;
}

function renderMemoryResults(data,box){
  if(!box) return;
  const rows=[];
  (data.missions||[]).slice(0,8).forEach(m=>rows.push(memoryItem('mission',m.goal,m.final_result||m.user_result||m.debug_result,fmtTime(m.saved_at||m.created_at),[`score ${m.success_score??'?'}`,m.status])));
  (data.tasks||[]).slice(0,10).forEach(t=>rows.push(memoryItem('task',t.title,t.result_summary||t.description||t.failure_reason,t.assigned_worker||t.assigned_ant,[t.status,t.task_type,String(t.mission_id||'').slice(0,8)])));
  (data.sources||[]).slice(0,8).forEach(s=>rows.push(memoryItem('source',s.title||s.url,s.summary||s.notes||s.domain,s.domain,[`confidence ${s.confidence_score??'?'}`,String(s.mission_id||'').slice(0,8)])));
  (data.patches||[]).slice(0,8).forEach(p=>rows.push(memoryItem('patch',p.file_path,p.reason||p.patch_set_summary||p.last_error,p.status,[p.change_type,p.risk,String(p.mission_id||'').slice(0,8)])));
  (data.events||[]).slice(0,8).forEach(e=>rows.push(memoryItem('event',e.event_type,e.message,e.ant_name||fmtTime(e.created_at),[e.level,String(e.mission_id||'').slice(0,8)])));
  (data.trails||[]).slice(0,8).forEach(t=>rows.push(memoryItem('trail',t.trail_key,`${t.success_count||0} success / ${t.failure_count||0} failure`,t.trail_type,[`strength ${Number(t.strength||0).toFixed(3)}`,`net ${t.net_count??0}`])));
  box.innerHTML=rows.length?rows.join(''):'<div style="font-size:10px;color:var(--dim);padding:8px 0;">No memory matched that search.</div>';
}

function renderPheromoneTable(trails,body){
  if(!trails.length){body.innerHTML='<div style="color:var(--dim)">No pheromone trails recorded yet. Run a few missions to build colony memory.</div>';return;}
  body.innerHTML=`<table class="phero-table"><thead><tr>
      <th>Trail</th><th>Type</th><th>Strength</th><th>✓</th><th>✕</th><th>Net</th><th>Updated</th></tr></thead><tbody>${
      trails.map(t=>{
        const s=+t.strength||0,pct=Math.round(s*100),net=t.net_count||0;
        const col=s>=.6?'var(--green)':s>=.3?'var(--queen)':'var(--red)';
        return `<tr>
          <td class="phero-key" title="${escapeHtml(t.trail_key||'')}">${escapeHtml(t.trail_key||'?')}</td>
          <td>${escapeHtml(t.trail_type||'?')}</td>
          <td><div style="display:flex;align-items:center;gap:7px"><div class="phero-bar"><div class="phero-bar-fill" style="width:${pct}%;background:${col}"></div></div><span style="color:${col}">${s.toFixed(3)}</span></div></td>
          <td style="color:var(--green)">${t.success_count||0}</td>
          <td style="color:var(--red)">${t.failure_count||0}</td>
          <td class="phero-net ${net>=0?'pos':'neg'}">${net>=0?'+':''}${net}</td>
          <td>${fmtTime(t.last_updated)||'?'}</td>
        </tr>`;
      }).join('')}</tbody></table>`;
}

document.getElementById('phero-reload').addEventListener('click',reloadPheromones);
document.getElementById('memory-search').addEventListener('click',reloadPheromones);
document.getElementById('memory-query').addEventListener('keydown',e=>{if(e.key==='Enter') reloadPheromones();});
document.getElementById('phero-prune').addEventListener('click',async()=>{
  if(!await uiConfirm('Prune weak and failure-dominant pheromone trails? This keeps only useful colony memory.')) return;
  const msg=document.getElementById('phero-msg');
  try{const r=await api('/pheromones/prune','POST');msg.style.color='var(--green)';msg.textContent=r.message||'Pruned';await reloadPheromones();}
  catch(e){msg.style.color='var(--red)';msg.textContent='Failed: '+e.message;}
  setTimeout(()=>msg.textContent='',3000);
});

// -- Colony settings tab -------------------------------------------------------
const TOGGLE_KEYS=[
  ['web_search_enabled','Web Search'],['file_tools_enabled','File Tools'],
  ['file_writing_enabled','File Writing'],['patch_application_enabled','Patch Apply'],
  ['shell_tool_enabled','Shell Tool'],['parallel_execution_enabled','Parallel Exec'],
  ['spec_ingestion_enabled','Spec Ingestion'],['autonomy_enabled','Autonomy'],
  ['autonomy_learning_enabled','Autonomy Learning'],
];
const NUM_KEYS=[
  ['max_parallel_workers','Parallel Workers'],['max_web_searches_per_mission','Web Searches/Mission'],
  ['max_sources_per_mission','Sources/Mission'],['max_context_packet_chars','Context Chars'],
  ['long_input_threshold','Long-Input Threshold'],['max_section_chars','Section Chars'],
];
const AUTO_KEYS=[
  ['autonomy_poll_seconds','Poll Seconds'],['autonomy_max_missions_per_hour','Missions/Hour'],
  ['autonomy_max_missions_per_day','Missions/Day'],['autonomy_max_consecutive_failures','Max Fails'],
  ['autonomy_concurrency','Concurrency'],['autonomy_aging_minutes','Aging Minutes'],
  ['autonomy_priority_bias_max','Learning Bias Max'],['autonomy_retire_min_runs','Retire Min Runs'],
  ['autonomy_loop_window','Loop Window'],
];
let colonySettings={};

async function loadColonyTab(){
  try{const r=await api('/settings');if(!r.success)return;colonySettings=r.data;}catch{return;}
  document.getElementById('set-ollama-host').value=colonySettings.ollama_host||'';
  document.getElementById('set-ollama-model').value=colonySettings.ollama_model||'';
  document.getElementById('set-toggles').innerHTML=TOGGLE_KEYS.map(([k,label])=>
    `<div class="toggle-row"><span>${label}</span><div class="toggle-sw${colonySettings[k]?' on':''}" data-key="${k}" data-onclick="this.classList.toggle('on')"></div></div>`).join('');
  document.getElementById('set-nums').innerHTML=NUM_KEYS.map(([k,label])=>
    `<div class="num-row"><label>${label}</label><input type="number" data-key="${k}" value="${colonySettings[k]??0}"></div>`).join('');
  document.getElementById('set-autonomy').innerHTML=AUTO_KEYS.map(([k,label])=>
    `<div class="num-row"><label>${label}</label><input type="number" data-key="${k}" value="${colonySettings[k]??0}"></div>`).join('');
}

async function saveColonyTab(){
  const msg=document.getElementById('colony-save-msg'); msg.textContent='';
  const payload={
    ollama_host:document.getElementById('set-ollama-host').value.trim(),
    ollama_model:document.getElementById('set-ollama-model').value.trim(),
  };
  document.querySelectorAll('#set-toggles .toggle-sw').forEach(sw=>{payload[sw.dataset.key]=sw.classList.contains('on');});
  document.querySelectorAll('#set-nums input,#set-autonomy input').forEach(i=>{const v=parseInt(i.value,10);if(!isNaN(v))payload[i.dataset.key]=v;});
  try{
    const r=await api('/settings','POST',payload);
    if(r.success){msg.style.color='var(--green)';msg.textContent=r.message||'Saved';colonySettings=r.data.settings||colonySettings;pollModelInfo();}
    else{msg.style.color='var(--red)';msg.textContent=r.message||'Failed';}
  }catch(e){msg.style.color='var(--red)';msg.textContent='Failed: '+e.message;}
  setTimeout(()=>msg.textContent='',3000);
}

document.getElementById('colony-save').addEventListener('click',saveColonyTab);
document.getElementById('colony-reload').addEventListener('click',loadColonyTab);

// -- User management -----------------------------------------------------------
async function openUsers(){
  if(ROLE!=='admin') return;
  showPage('users');
  await reloadUsers();
}

PAGE_ENTER['users']=()=>{ if(ROLE==='admin') reloadUsers(); };

async function reloadUsers(){
  const tb=document.getElementById('users-tbody');
  try{
    const r=await api('/users');if(!r.success) throw new Error(r.message);
    const users=r.data||[];
    setEl('users-count',`${users.length} account${users.length===1?'':'s'}`);
    tb.innerHTML=users.map(u=>{
      const isAdmin=(u.role||'coordinator')==='admin';
      const status=u.active?'<span style="color:var(--green)">active</span>':'<span style="color:var(--red)">disabled</span>';
      const last=u.last_login_at?fmtTime(u.last_login_at):'never';
      const self=u.username===USERNAME;
      return `<tr>
        <td style="font-family:var(--mono);color:var(--text)">${escapeHtml(u.username)}${self?' <span style="color:var(--dim)">(you)</span>':''}</td>
        <td><span style="color:${isAdmin?'var(--queen)':'var(--blue)'}">${isAdmin?'Administrator':'Coordinator'}</span></td>
        <td>${status}</td>
        <td>${last}</td>
        <td style="white-space:nowrap">
          <button class="job-btn view" data-onclick="userResetPw('${jsArg(u.username)}')">Reset PW</button>
          <button class="job-btn view" data-onclick="userToggleRole('${jsArg(u.username)}','${isAdmin?'coordinator':'admin'}')">${isAdmin?'→ Coord':'→ Admin'}</button>
          <button class="job-btn ${u.active?'cancel':'view'}" data-onclick="userToggleActive('${jsArg(u.username)}',${u.active?'false':'true'})">${u.active?'Disable':'Enable'}</button>
          ${self?'':`<button class="job-btn cancel" data-onclick="userDelete('${jsArg(u.username)}')">Delete</button>`}
        </td></tr>`;
    }).join('')||'<tr><td colspan="5" style="color:var(--dim)">No accounts.</td></tr>';
  }catch(e){tb.innerHTML=`<tr><td colspan="5" style="color:var(--red)">Error: ${escapeHtml(e.message)}</td></tr>`;}
}

function usersMsg(t,ok){
  const m=document.getElementById('nu-msg');
  m.style.color=ok?'var(--green)':'var(--red)';
  m.textContent=t;
  setTimeout(()=>{if(m.textContent===t)m.textContent='';},3500);
}

document.getElementById('nu-add').addEventListener('click',async()=>{
  const u=document.getElementById('nu-user').value.trim();
  const p=document.getElementById('nu-pass').value;
  const role=document.getElementById('nu-role').value;
  if(!u||!p){usersMsg('Username and password are required.',false);return;}
  try{
    const r=await api('/users','POST',{username:u,password:p,role});
    if(r.success){usersMsg('Created '+u,true);document.getElementById('nu-user').value='';document.getElementById('nu-pass').value='';reloadUsers();}
    else usersMsg(r.message||'Failed',false);
  }catch(e){usersMsg('Failed: '+e.message,false);}
});

document.getElementById('users-reload').addEventListener('click',reloadUsers);

async function userPatch(username,body,okMsg){
  try{
    const r=await api('/users/'+encodeURIComponent(username),'PATCH',body);
    if(r.success){usersMsg(okMsg,true);reloadUsers();}else usersMsg(r.message||'Failed',false);
  }catch(e){usersMsg('Failed: '+e.message,false);}
}
async function userResetPw(u){const p=await uiPrompt(`New password for ${u} (min 8 characters):`,{password:true,title:'Reset password'});if(p)userPatch(u,{password:p},'Password reset for '+u);}
async function userToggleRole(u,role){if(await uiConfirm(`Change role of ${u} to ${role}?`))userPatch(u,{role},'Role changed for '+u);}
function userToggleActive(u,active){userPatch(u,{active:active},active?'Enabled '+u:'Disabled '+u);}
async function userDelete(u){
  if(!await uiConfirm(`Delete account ${u}? This cannot be undone.`)) return;
  try{
    const r=await api('/users/'+encodeURIComponent(u),'DELETE');
    if(r.success){usersMsg('Deleted '+u,true);reloadUsers();}else usersMsg(r.message||'Failed',false);
  }catch(e){usersMsg('Failed: '+e.message,false);}
}

// -- Homelab: moved to homelab.js in v0.3.8.52 (the app.js split). It loads AFTER this file,
//    because its PAGE_ENTER['homelab'] registration needs PAGE_ENTER to already exist.

// -- V2.2 Pass A: ANTHILL design-system helpers + TopTelemetryBar --------------------
// Role colors: THE single mapping for chambers, nodes, dots, badges, trails, legends.
function getRoleColor(role){
  const n=String(role||'').toLowerCase();
  if(n.includes('queen'))return 'var(--role-queen)';
  if(n.includes('mission')||n.includes('director')||n.includes('planner')||n.includes('constraint'))return 'var(--role-mission-control)';
  if(n.includes('research'))return 'var(--role-research)';
  if(n.includes('coder')||n.includes('code'))return 'var(--role-coder)';
  if(n.includes('builder')||n.includes('build'))return 'var(--role-builder)';
  if(n.includes('verifier')||n.includes('verify'))return 'var(--role-verifier)';
  if(n.includes('tester')||n.includes('test'))return 'var(--role-tester)';
  if(n.includes('scout')||n.includes('forager')||n.includes('web'))return 'var(--role-scout)';
  if(n.includes('archivist'))return 'var(--role-archivist)';
  if(n.includes('memory')||n.includes('pheromone')||n.includes('quartermaster'))return 'var(--role-memory)';
  if(n.includes('security')||n.includes('sentinel')||n.includes('guard')||n.includes('soldier')||n.includes('medic'))return 'var(--role-security)';
  if(n.includes('source')||n.includes('file'))return 'var(--role-source)';
  if(n.includes('ui')||n.includes('cartographer'))return 'var(--role-ui)';
  if(n.includes('operator')||n.includes('scribe'))return 'var(--role-operator)';
  return 'var(--role-other)';
}
function getRoleLabel(role){
  const n=String(role||'').toLowerCase();
  if(n.includes('queen'))return 'Queen';
  if(n.includes('mission')||n.includes('director')||n.includes('planner')||n.includes('constraint'))return 'Mission Control';
  if(n.includes('research'))return 'Researchers';
  if(n.includes('coder'))return 'Coders';
  if(n.includes('builder'))return 'Builders';
  if(n.includes('verifier'))return 'Verifiers';
  if(n.includes('tester'))return 'Testers';
  if(n.includes('scout')||n.includes('forager')||n.includes('web'))return 'Scouts';
  if(n.includes('archivist'))return 'Archivists';
  if(n.includes('memory')||n.includes('pheromone')||n.includes('quartermaster'))return 'Memory';
  if(n.includes('security')||n.includes('sentinel')||n.includes('soldier')||n.includes('medic'))return 'Security';
  if(n.includes('source')||n.includes('file'))return 'Sources';
  if(n.includes('ui')||n.includes('cartographer'))return 'UI';
  if(n.includes('operator')||n.includes('scribe'))return 'Operators';
  return 'Other';
}
function athPill(state,text){ return '<span class="ath-pill '+state+'">'+escapeHtml(text)+'</span>'; }

// Central telemetry store — every value is real or null (rendered as em dash, never invented).
const TB={online:null,tasks:null,tasksPerSec:null,successRate:null,activeAnts:null,approvals:null,
  health:null,healthLabel:'',search:'',_prevTasks:null,_prevAt:0};

async function pollTelemetry(){
  const ovActive=document.getElementById('page-overview')?.classList.contains('active');
  const coActive=document.getElementById('page-colony')?.classList.contains('active');
  if(!ovActive&&!coActive) return;
  try{
    // v2.2.6: always request the registry so the ant count tracks changes (api() TTL-caches it,
    // so this adds no network traffic between cache windows — the count just stops going stale).
    const [st,reg]=await Promise.all([api('/status'), api('/colony/registry')]);
    const s=(st&&st.success&&st.data)||null;
    TB.online=!!s;
    if(s){
      const now=Date.now();
      const tasks=(typeof s.tasks==='number')?s.tasks:null;
      if(tasks!==null&&TB._prevTasks!==null&&now>TB._prevAt)
        TB.tasksPerSec=Math.max(0,(tasks-TB._prevTasks)/((now-TB._prevAt)/1000));
      TB._prevTasks=tasks; TB._prevAt=now;
      TB.tasks=tasks;
      TB.approvals=(typeof s.pending_approvals==='number')?s.pending_approvals:null;
      // Success rate derived from the real event stream: 1 - failures/events (when events exist).
      TB.successRate=(typeof s.events==='number'&&s.events>0&&typeof s.failures==='number')
        ? Math.max(0,Math.min(100,(1-(s.failures/s.events))*100)) : null;
      // Health: simple real-signal score — start 100, -4 per failure share point, -5 if approvals pile up.
      if(TB.successRate!==null){
        let h=Math.round(TB.successRate);
        if((TB.approvals??0)>5)h-=5;
        TB.health=Math.max(0,Math.min(100,h));
        TB.healthLabel=TB.health>=90?'STABLE':(TB.health>=75?'WATCH':(TB.health>=50?'DEGRADED':'CRITICAL'));
      }
    }
    if(reg&&reg.success&&reg.data){
      const roles=Array.isArray(reg.data.roles)?reg.data.roles:(Array.isArray(reg.data)?reg.data:[]);
      let ants=0; roles.forEach(r=>{ ants+=1+((r.workers&&r.workers.length)||0); });
      if(ants>0)TB.activeAnts=ants;
    }
  }catch(e){ TB.online=false; }
  renderTelemetryBar('tb-overview'); renderTelemetryBar('tb-colony');
}

function renderTelemetryBar(containerId){
  const el=document.getElementById(containerId); if(!el) return;
  // Never re-render while the operator is typing in this bar's search box.
  const focused=el.querySelector('.tb-search');
  if(focused&&focused===document.activeElement) return;
  const v=(x,f)=>x===null||x===undefined?'—':(f?f(x):x);
  const hs=TB.health===null?'':(TB.health>=90?'ok':(TB.health>=75?'warn':'danger'));
  el.innerHTML='<div class="tb-bar">'+
    '<span class="tb-brand">ANTHILL</span>'+
    '<span class="tb-seg"><span class="tb-dot '+(TB.online?'on':'off')+'"></span>'+(TB.online===null?'CONNECTING':(TB.online?'COLONY ONLINE':'COLONY OFFLINE'))+'</span>'+
    '<span class="tb-seg">TASKS <b>'+v(TB.tasks)+'</b>'+(TB.tasksPerSec!==null?' <span class="up">'+TB.tasksPerSec.toFixed(2)+'/s</span>':'')+'</span>'+
    '<span class="tb-seg">SUCCESS <b>'+v(TB.successRate,x=>x.toFixed(1)+'%')+'</b></span>'+
    '<span class="tb-seg">ACTIVE ANTS <b>'+v(TB.activeAnts)+'</b></span>'+
    '<span class="tb-seg">APPROVALS <b'+((TB.approvals??0)>0?' style="color:var(--status-warning)"':'')+'>'+v(TB.approvals)+'</b></span>'+
    '<span class="tb-seg">HEALTH '+(TB.health===null?'<b>—</b>':athPill(hs,TB.health+'% '+TB.healthLabel))+'</span>'+
    '<input class="tb-search" placeholder="Search colony..." value="'+escapeHtml(TB.search)+'" '+
      'data-oninput="TB.search=this.value; if(typeof colonySearchHook===\'function\')colonySearchHook(TB.search);">'+
    '</div>';
}

// Wire into page lifecycle without touching existing enter handlers.
(function(){
  const prevOv=PAGE_ENTER['overview'];
  PAGE_ENTER['overview']=()=>{ if(prevOv)prevOv(); pollTelemetry(); pollOv2();
    if(!window.__wsBooted){ window.__wsBooted=true; initDashboardGrid(); } };
  const prevCo=PAGE_ENTER['colony'];
  PAGE_ENTER['colony']=()=>{ if(prevCo)prevCo(); pollTelemetry(); };
  setInterval(pollTelemetry,15000);
  // v0.3.8.52: the Overview cards read /events/json among other things, so they refresh on the
  // stream like the rest. The visibility check stays inside the callback — a background tab must
  // not repaint on every arriving event any more than it polled on every tick.
  liveRefresh(() => { if(document.getElementById('page-overview')?.classList.contains('active')) pollOv2(); },
    { idleMs: 30000, on: EVT_MISSION });
})();

// -- V2.2 Pass B: Overview command-center cards (real data + labeled empty states) ----
const OV2={healthTrend:[]};
// v0.3.8.42 (§3): Create Mission goes to the canonical composer — Chat.
function ov2FocusMission(){ go('/chat'); setTimeout(()=>document.getElementById('chat-input')?.focus(),80); }
function ov2Empty(id,msg){ const el=document.getElementById(id); if(el) el.innerHTML='<div class="ov2-empty">'+escapeHtml(msg)+'</div>'; }

async function pollOv2(){
  let jb,mn,ev,ap,reg;
  try{
    [jb,mn,ev,ap,reg]=await Promise.all([
      api('/jobs'), api('/missions/json?limit=6'), api('/events/json?limit=200'),
      api('/homelab/approvals/unified'), api('/colony/registry'),
    ]);
  }catch(e){ return; }
  const jobs=Array.isArray(jb&&jb.data)?jb.data:[];
  const missions=Array.isArray(mn&&mn.data)?mn.data:[];
  const events=Array.isArray(ev&&ev.data)?ev.data:[];
  const approvals=(ap&&ap.success&&ap.data&&Array.isArray(ap.data.items))?ap.data.items:[];
  const roles=(reg&&reg.success&&reg.data)?(Array.isArray(reg.data.roles)?reg.data.roles:(Array.isArray(reg.data)?reg.data:[])):[];
  renderOv2Health(); renderOv2Active(jobs,missions);
  renderOv2Approvals(approvals); renderOv2Core(roles,jobs,missions,approvals);
  renderOv2Resources(); renderOv2Events(events);
}


/* ─────────────────────────────────────────────────────────────────────────────
   v3.3.0 dashboard grid — widget registration.

   Same adoption model the workspace used and the reason a layout replacement is
   not a rewrite: every widget mounts an EXISTING element by id, so every
   renderer in this file keeps writing to the node it already wrote to.

   ORDER IS THE LAYOUT. There is no saved arrangement yet, so registration order
   is what the operator sees: situational awareness above the Colony, detail
   below it. The Colony sits mid-list on purpose — that is what makes the page
   read as an operations console with a mission display rather than a list of
   cards that happens to contain a map.

   SIZES TILE. At 12 columns a row is 4 small (3) / 3 medium (4) / 2 large (6),
   so each group below sums to 12 and no row ends short. See the tiling rule in
   dashboard-grid.css for why grid-auto-flow:dense is not used instead.
   ───────────────────────────────────────────────────────────────────────────── */
function registerGridWidgets(){
  if(!window.AnthillGrid) return;
  var G=window.AnthillGrid;

  // ---- above the Colony: is the colony healthy, and what needs me -----------
  var above=[
    // TWO rows above the Colony, not three. With three the map was pushed to the lower third of
    // the viewport and read as the bottom of the page rather than its centre. Vitals and alerts
    // answer "is the colony healthy"; the composer and mission list are what an operator reaches
    // for first — that is enough context to sit above the map, and everything else reads after it.
    {id:'colony-vitals',      title:'Colony Vitals',      icon:'\u25b2', size:'large',  body:'ov-vitals-body'},
    {id:'operator-attention', title:'Operator Attention', icon:'\u0021', size:'large',  body:'hud-attn-list'},
    {id:'mission-composer',   title:'Mission Command',    icon:'\u25b6', size:'large',  body:'ov-composer-body', cls:'mission-node'},
    {id:'missions',           title:'Active Missions',    icon:'\u26a1', size:'large',  body:'ov2-active-body'},
    // v3.7.1: conversations sit ABOVE the Colony because they are where an operator ACTS —
    // and because a conversation waiting for approval is the one thing on this page that stops
    // the colony until a human answers.
    {id:'conversations',      title:'Conversations',      icon:'\u2709', size:'large',  body:'ov-conversations-body'},
    // v0.3.8.55 (field report): the Director's status glance, on the dashboard BY DEFAULT \u2014 the
    // full Automation panel (objectives, runs) lives on the Projects page; this answers "is the
    // Director running and inside budget" without leaving the overview.
    {id:'director',           title:'Automation Director', icon:'\u23f5', size:'large', body:'ov-director-body'},
  ];

  // ---- below the Colony: detail, history and queues -------------------------
  var below=[
    // The four status smalls move BELOW the map so the Colony rises to the middle of the layout.
    // They are glanceable rather than read, which is what makes them the right ones to move.
    {id:'colony-health',      title:'Colony Health',      icon:'\u25c6', size:'small',  body:'ov2-health-body'},
    {id:'system-core',        title:'System Core',        icon:'\u2699', size:'small',  body:'ov2-core-body'},
    {id:'resource-usage',     title:'Resource Usage',     icon:'\u25a4', size:'small',  body:'ov2-resources-body'},
    // v3.8.34: "Job" was a third name for a thing the console already calls a mission.
    // ApiJobRegistry names the type ApiMissionJob, POST /missions answers "Mission queued.", and
    // every row carries a mission_id. The queue is named after what is in it.
    {id:'colony-jobs',        title:'Mission Runs',       icon:'\u2637', size:'small',  body:'jobs-list'},
    {id:'agent-inspector',    title:'Agent Inspector',    icon:'\u2b21', size:'medium', body:'agent-detail'},
    {id:'live-telemetry',     title:'Live Telemetry',     icon:'\u2261', size:'medium', body:'ov-feed-list'},
    {id:'recent-events',      title:'Recent Events',      icon:'\u25cf', size:'medium', body:'ov2-events-body'},
    {id:'recent-missions',    title:'Recent Missions',    icon:'\u25f4', size:'medium', body:'ov-sum-missions'},
    {id:'approvals',          title:'Pending Approvals',  icon:'\u2713', size:'medium', body:'ov2-approvals-body'},
    {id:'patch-activity',     title:'Patch Activity',     icon:'\u2726', size:'medium', body:'ov-sum-patches'},
    {id:'objectives',         title:'Objectives',         icon:'\u25ce', size:'large',  body:'ov-sum-objectives'},
    // Same endpoint and same renderer as 'colony-jobs' above, capped at 5 rows instead of 8 \u2014
    // pollJobs feeds both from one /jobs array. Renamed for consistency rather than merged: it is
    // registered, an operator may have it enabled in a saved layout, and removing a widget is a
    // product decision rather than a naming one.
    {id:'recent-jobs',        title:'Recent Mission Runs',icon:'\u231b', size:'large',  body:'ov-jobs-list'},
    // v3.7.2: the operator surface for v3.4.1, v3.4.2 and v3.5.0. Registered but off by default \u2014
    // both answer questions an operator asks occasionally rather than continuously, and a console
    // that opens on everything is a wall rather than a dashboard.
    {id:'tools',              title:'Tools & Routing',    icon:'\u2692', size:'large',  body:'ov-tools-body'},
    {id:'workspaces',         title:'Mission Workspaces', icon:'\u25a3', size:'medium', body:'ov-workspaces-body'},
    {id:'attempts',           title:'Task Attempts',      icon:'\u21bb', size:'large',  body:'ov-attempts-body'},
  ];

  function reg(d){
    G.register({ id:d.id, title:d.title, icon:d.icon, size:d.size,
      render:function(el){ gridMountTarget(d.body, el, d.cls); } });
  }

  above.forEach(reg);

  // The Colony: its own full-width row, taller than anything around it, and the
  // existing canvas MOVED in rather than duplicated — same node, same renderer,
  // same zoom/pan state. topologyRemeasure() re-runs the layout maths after the
  // element lands in its new box, which is what stops the canvas drawing at the
  // wrong size when the grid reflows.
  G.register({ id:'colony', title:'Colony', icon:'\u2726', size:'colony',
    render:function(el){
      var area=document.getElementById('colony-canvas-area');
      if(!area) return;
      if(typeof topologyCaptureHome==='function') topologyCaptureHome();
      el.appendChild(area);
      if(typeof topologyHost!=='undefined') topologyHost='grid';
      if(typeof topologyRemeasure==='function') topologyRemeasure();
    }});

  below.forEach(reg);
}

/**
 * Re-parent a renderer's own element into a widget body: same node, same id, same writer.
 *
 * `cls` restores a class the element's STYLES were scoped to via an ancestor. Adoption moves a
 * node out from under that ancestor, so any rule written as `.ancestor .child` silently stops
 * applying — the markup is intact, the ids are intact, and the element quietly loses its layout.
 * The Mission Command box is the case that exposed this: every one of its rules is written
 * `.mission-node ...`, so once adopted, its prompt row was no longer a flex row, the textarea fell
 * back to its intrinsic width (47% of the card) and the dispatch button collapsed to 8×15px.
 */
function gridMountTarget(bodyId, el, cls){
  var existing=document.getElementById(bodyId);
  if(existing){ if(cls) existing.classList.add(cls); el.appendChild(existing); return; }
  var made=document.createElement('div'); made.id=bodyId; if(cls) made.classList.add(cls); el.appendChild(made);
}

/**
 * The dashboard a freshly installed colony opens on.
 *
 * Seventeen widgets at once is a wall, not a console. This is the curated set: the composer to
 * give an order, the vitals to see whether the colony can take one, the Colony itself, and the
 * four status smalls beneath it. Everything else is registered, listed in the Widgets menu, and one
 * click from being shown — hidden here is "not on by default", never "unavailable".
 *
 * Only order and visibility are shipped. No spans or heights: a default that dictates pixel sizes
 * would fight the content-fit pass and would be wrong on the first screen that is not the one it
 * was captured on.
 */
var DEFAULT_DASHBOARD_VIEW = {
  locked: true,
  // v0.3.8.56: this is not a hand-guessed arrangement — it is the operators' own board, captured
  // from a live console after the free-placement engine settled and read back cell by cell. The
  // colony spans the top; command surfaces (composer, system, health, director) sit on the row
  // beneath it; vitals and the working set (tools, conversations, jobs, workspaces, attempts)
  // fill in below. Order is the board's reading order (top-left to bottom-right), which is also
  // what the narrow-viewport stack and the Widgets menu present.
  //
  // Everything hidden stays REGISTERED (one click away in the Widgets menu, and any saved layout
  // that pins it still works); a saved layout always wins over this view, and "Reset layout"
  // restores exactly this — positions included.
  order: [
    'colony', 'mission-composer', 'system-core', 'colony-health', 'director',
    'colony-vitals', 'tools', 'conversations', 'colony-jobs', 'workspaces', 'attempts',
    // registered but off by default, in the order they appear in the Widgets menu
    'operator-attention', 'missions', 'agent-inspector', 'live-telemetry', 'recent-events',
    'recent-missions', 'approvals', 'patch-activity', 'objectives', 'recent-jobs', 'resource-usage',
  ],
  hidden: {
    'operator-attention': true, 'missions': true, 'agent-inspector': true, 'live-telemetry': true,
    'recent-events': true, 'recent-missions': true, 'approvals': true, 'patch-activity': true,
    'objectives': true, 'recent-jobs': true, 'resource-usage': true,
  },
  // Widths are fractions of the six-cell row (sixths); heights are canonical px chosen so they
  // quantize to the intended CELL count at every wide breakpoint (250 -> 1 cell, 516 -> 2,
  // 1350 -> 5, for cell heights 236/250/280 with the 16px row gap).
  spans: {
    'mission-composer': 1 / 6, 'colony-vitals': 0.5, 'director': 0.5,
    'conversations': 0.5, 'workspaces': 1 / 3, 'tools': 0.5,
  },
  heights: {
    'mission-composer': 250, 'colony-vitals': 250, 'director': 516,
    'conversations': 1350, 'workspaces': 516, 'tools': 250,
  },
  pos: {
    'colony': { x: 0, y: 0 },
    'mission-composer': { x: 0, y: 2 }, 'system-core': { x: 1, y: 2 },
    'colony-health': { x: 2, y: 2 }, 'director': { x: 3, y: 2 },
    'colony-vitals': { x: 0, y: 3 },
    'tools': { x: 0, y: 4 }, 'conversations': { x: 3, y: 4 },
    'colony-jobs': { x: 0, y: 5 }, 'workspaces': { x: 1, y: 5 },
    'attempts': { x: 0, y: 7 },
  },
};

/** Mount the responsive grid as the dashboard. */
function initDashboardGrid(){
  if(!window.AnthillGrid) return;
  var page=document.getElementById('page-overview'); if(!page) return;

  var root=document.getElementById('dg-root');
  if(!root){ root=document.createElement('div'); root.id='dg-root'; page.insertBefore(root, page.firstChild); }

  // The classic grid steps aside; its card bodies are re-parented into widgets, so every
  // renderer keeps writing to the same ids and nothing is duplicated.
  var legacy=document.getElementById('ov2-grid'); if(legacy) legacy.style.display='none';
  page.classList.add('dg-active');

  var bar=document.getElementById('dg-toolbar');
  // dg-keep at creation, not just in renderToolbar: the element is in the page for a frame before
  // the grid fills it, and without the class that frame hides it.
  if(!bar){ bar=document.createElement('div'); bar.id='dg-toolbar'; bar.className='dg-keep'; page.insertBefore(bar, root); }

  registerGridWidgets();

  // Persist through the host's SINGLE ui_state writer. app.js and the old workspace once ran
  // independent debounced read-modify-write cycles against this document and silently discarded
  // each other's changes; there is one writer now and the grid uses it rather than adding a second.
  window.AnthillGrid.onLayoutChange=function(layout){
    if(!window.AnthillUiState) return;
    window.AnthillUiState.queue(function(doc){ doc.dashboard_grid=layout; });
  };

  window.AnthillGrid.mount(root);
  window.AnthillGrid.renderToolbar(bar);

  // The data pollers refill widget bodies on their own timers, long after the grid laid out. A
  // widget that was empty at mount and has rows a second later must stop being treated as quiet —
  // otherwise the first poll leaves real content squeezed into a collapsed card.
  var quietTimer=setInterval(function(){
    if(window.AnthillGrid && typeof window.AnthillGrid.remeasure==='function') window.AnthillGrid.remeasure();
  }, 4000);
  window.addEventListener('beforeunload', function(){ clearInterval(quietTimer); });

  // Load the saved arrangement after the first paint: the dashboard must render immediately even
  // if /ui/state is slow or unreachable, and an unreachable state document must never mean a
  // blank console.
  (async function(){
    try{
      const r=await api('/ui/state');
      // Also what "Reset layout" restores, so the button and the first-run view cannot disagree.
      window.AnthillGrid.defaults = DEFAULT_DASHBOARD_VIEW;
      const saved = r && r.success && r.data && r.data.dashboard_grid;
      // A first-run console gets the curated view rather than all seventeen widgets at once. A
      // SAVED layout always wins, including one that hides nothing — an operator who deliberately
      // turned everything on must not have the default reimposed on their next visit, which is why
      // this tests for the document's presence and not for whether it looks empty.
      //
      // v0.3.8.55 (operator's correction): the Director widget is a MOVE of the Projects page's
      // status card, default-visible BY PLACEMENT. effectiveOrder appends unknown new widgets at
      // the END, which buried the moved card below the fold in every pre-existing saved layout —
      // technically visible, practically invisible. A layout saved before the widget existed gets
      // it spliced in near the top, once; a layout that ALREADY names it (the operator moved or
      // hid it themselves) is left exactly alone.
      if(saved && Array.isArray(saved.order) && saved.order.indexOf('director')<0){
        const at=saved.order.indexOf('colony-vitals');
        saved.order.splice(at>=0?at+1:0, 0, 'director');
      }
      window.AnthillGrid.applyLayout(saved || DEFAULT_DASHBOARD_VIEW);
      window.AnthillGrid.renderToolbar(bar);
    }catch(e){ /* keep the registration defaults: an unreachable state doc is not a blank console */ }
  })();
}

/** Mount the workspace on the dashboard when the server says the flag is on. */

// v2.2.3: restored — the events feed anchors row 3 of the balanced grid (uses data already
// fetched by pollOv2; no extra polling).
function renderOv2Events(events){
  const el=document.getElementById('ov2-events-body'); if(!el) return;
  const recent=events.slice(0,6);
  if(!recent.length){ el.innerHTML='<div class="ov2-empty">No recent events yet.</div>'; return; }
  el.innerHTML=recent.map(e=>{
    const who=String(e.ant||e.ant_name||e.source||'');
    return '<div class="ov2-list-item"><span style="display:inline-block;width:7px;height:7px;border-radius:50%;background:'+getRoleColor(who||e.event_type)+';margin-right:6px;"></span>'+
      '<span style="color:var(--anthill-dim)">'+escapeHtml((e.created_at||'').substring(11,16))+'</span> '+
      (who?'<b style="color:var(--anthill-text)">'+escapeHtml(who)+'</b> ':'')+
      escapeHtml(String(e.message||e.event_type||'').substring(0,70))+'</div>';
  }).join('');
}

function renderOv2Health(){
  const el=document.getElementById('ov2-health-body'); if(!el) return;
  if(TB.health===null){ el.innerHTML='<div class="ov2-empty">No health signal yet — waiting for event data.</div>'; return; }
  OV2.healthTrend.push(TB.health); if(OV2.healthTrend.length>48)OV2.healthTrend.shift();
  const col=TB.health>=90?'var(--status-success)':(TB.health>=75?'var(--status-warning)':'var(--status-danger)');
  el.innerHTML='<div class="ov2-big" style="color:'+col+'">'+TB.health+'%</div>'+
    '<div style="margin:4px 0">'+athPill(TB.health>=90?'ok':(TB.health>=75?'warn':'danger'),TB.healthLabel)+'</div>'+
    '<div class="ov2-row"><span>Systems</span><b>'+(TB.online?'online':'offline')+'</b></div>'+
    '<div class="ov2-row"><span>Ant network</span><b>'+(TB.activeAnts??'—')+' ants</b></div>'+
    '<div class="ov2-row"><span>Approvals</span><b>'+(TB.approvals??'—')+' pending</b></div>'+
    '<div class="ov2-spark" title="session trend">'+OV2.healthTrend.map(h=>'<i style="height:'+Math.max(6,h*0.26)+'px;background:'+col+'"></i>').join('')+'</div>';
}

function renderOv2Active(jobs,missions){
  // v2.2.2: single Missions card — active mission plus the queue folded in (queue card removed).
  const el=document.getElementById('ov2-active-body'); if(!el) return;
  const running=jobs.find(j=>(j.status||'').toLowerCase()==='running');
  const queued=jobs.filter(j=>(j.status||'').toLowerCase()==='queued');
  if(!running&&!queued.length){ el.innerHTML='<div class="ov2-empty">Idle — no active mission.<br>Enter a mission directive to begin.</div>'; return; }
  let html='';
  if(running){
    const started=running.created_at||running.started_at||'';
    const mins=started?Math.max(0,Math.round((Date.now()-Date.parse(started))/60000)):null;
    html+='<div style="font-size:12px;color:var(--anthill-text);font-weight:700;margin-bottom:6px;">'+escapeHtml((running.goal||running.title||'Mission').substring(0,90))+'</div>'+
      '<div class="ov2-row"><span>Status</span><b style="color:var(--status-info)">running'+(mins!==null?' · '+mins+'m':'')+'</b></div>';
  }
  if(queued.length){
    html+='<div class="ov2-row"><span>Queued</span><b>'+queued.length+'</b></div>'+
      queued.slice(0,2).map(j=>'<div class="ov2-list-item">'+escapeHtml((j.goal||j.id||'Mission').substring(0,55))+'</div>').join('');
  }
  el.innerHTML=html;
}

// v2.2.2: Tasks Today, Chamber Activity, Recent Events, Mission Timeline, and Mission Queue cards
// removed — all of that lives in the Events / Colony / Missions tabs. Overview shows only
// cross-cutting status not visible elsewhere.

function renderOv2Approvals(items){
  const el=document.getElementById('ov2-approvals-body'); if(!el) return;
  const pending=items.filter(i=>i.state==='pending').slice(0,3);
  if(!pending.length){ el.innerHTML='<div class="ov2-empty">No approvals pending.</div>'; return; }
  el.innerHTML=pending.map(a=>{
    const rc=a.risk_level==='critical'||a.risk_level==='high'?'danger':(a.risk_level==='medium'?'warn':'info');
    const kindTag=a.kind==='homelab_action'?' '+athPill('warn','action'):'';
    return '<div class="ov2-list-item"><b style="color:var(--anthill-text)">'+escapeHtml((a.title||'Approval').substring(0,48))+'</b> '+athPill(rc,a.risk_level||'?')+kindTag+
      '<div style="margin-top:4px;display:flex;gap:6px;">'+
      '<button class="ov2-approve" data-onclick="doApproval(\''+jsArg(a.source_id)+'\',\'approve\',\''+jsArg(a.kind||'patch')+'\')">✓ Approve</button>'+
      '<button class="ov2-reject" data-onclick="doApproval(\''+jsArg(a.source_id)+'\',\'reject\',\''+jsArg(a.kind||'patch')+'\')">✕ Reject</button></div></div>';
  }).join('');
}

function renderOv2Core(roles,jobs,missions,approvals){
  // v2.2.6: prefer the authoritative state published by pollHud (it sees autonomy, objectives,
  // patches, and provider health — this card's local fallback only covers the basics and is used
  // solely until the first pollHud completes).
  const el=document.getElementById('ov2-core-body'); if(!el) return;
  roles=roles||[]; jobs=jobs||[]; missions=missions||[]; approvals=approvals||[];
  OV2._core={roles,jobs,missions,approvals};
  let state,text,sub;
  if(CORE_STATE&&Date.now()-CORE_STATE.at<30000){
    state=CORE_STATE.state; text=CORE_STATE.text; sub=CORE_STATE.sub;
  }else{
    const activeJobs=jobs.filter(j=>(j.status||'').toLowerCase()==='running').length;
    const pending=approvals.filter(a=>a.state==='pending');
    const highRisk=pending.filter(a=>a.risk_level==='high'||a.risk_level==='critical').length;
    const lastFailed=missions.length&&(missions[0].status||'')==='failed';
    const offline=TB.online===false;
    state='idle'; text='IDLE'; sub='awaiting directive';
    if(offline||lastFailed){ state='alert'; text='ALERT'; sub=offline?'backend offline':'last mission failed'; }
    else if(pending.length){ state='action'; text='OPERATOR ACTION'; sub=highRisk?highRisk+' high-risk patch':pending.length+' awaiting review'; }
    else if(activeJobs){ state='mission'; text='MISSION ACTIVE'; sub=activeJobs+' in flight'; }
  }
  const col=state==='alert'?'var(--status-danger)':state==='action'?'var(--status-warning)':state==='mission'?'var(--status-info)':state==='autonomy'?'var(--role-mission-control)':'var(--anthill-muted)';
  const workers=roles.reduce((n,r)=>n+1+antWorkers(r).length,0);
  el.innerHTML='<div style="display:flex;align-items:center;gap:14px;">'+
    '<div class="ov2-orb" data-state="'+state+'"></div>'+
    '<div style="flex:1;min-width:0;">'+
      '<div class="ov2-big" style="font-size:16px;color:'+col+'">'+text+'</div>'+
      '<div style="font-size:10px;color:var(--anthill-dim);margin-bottom:4px;">'+escapeHtml(sub)+'</div>'+
      '<div class="ov2-row"><span>Roles</span><b>'+roles.length+'</b></div>'+
      '<div class="ov2-row"><span>Ants (incl. workers)</span><b>'+(roles.length?workers:(TB.activeAnts??'—'))+'</b></div>'+
    '</div></div>';
}
// v2.2.6: pollHud calls this after publishing CORE_STATE so the orb updates without waiting for
// the next pollOv2 tick (re-renders with the last known card data; no extra API calls).
function renderOv2CoreOrb(){ if(OV2._core) renderOv2Core(OV2._core.roles,OV2._core.jobs,OV2._core.missions,OV2._core.approvals); }

function renderOv2Resources(){
  // v2.2.6 fix: this card read cpu_percent/memory_percent/disk_percent fields that /status never
  // provides, so it was permanently "Metrics unavailable" while real governor signals existed.
  // It now renders the same signals the retired hud-metrics row used (published by pollHud).
  const el=document.getElementById('ov2-resources-body'); if(!el) return;
  const g=GOV||{};
  const rows=[];
  const gauge=(pct)=>'<div class="ov2-gauge"><i style="width:'+Math.min(100,Math.max(0,pct))+'%;background:'+(pct>=90?'var(--status-danger)':(pct>=75?'var(--status-warning)':'var(--status-info)'))+'"></i></div>';
  if(g.load!=null){ const pct=Math.min(100,g.load*100);
    rows.push('<div class="ov2-row"><span>CPU / core</span><b>'+Number(g.load).toFixed(2)+'</b></div>'+gauge(pct)); }
  if(g.memFree!=null){ const used=(1-g.memFree)*100;
    rows.push('<div class="ov2-row"><span>Memory used</span><b>'+Math.round(used)+'%</b></div>'+gauge(used)); }
  if(g.backendOk===false) rows.push('<div class="ov2-row"><span>Backend</span><b style="color:var(--status-danger)">offline</b></div>');
  else if(g.lat!=null) rows.push('<div class="ov2-row"><span>Backend latency</span><b'+(g.lat>=1500?' style="color:var(--status-warning)"':'')+'>'+Math.round(g.lat)+' ms</b></div>');
  if(g.concEff!=null||g.concCfg!=null) rows.push('<div class="ov2-row"><span>Concurrency</span><b>'+(g.concEff??g.concCfg??'—')+(g.concCfg!=null?' / '+g.concCfg:'')+'</b></div>');
  el.innerHTML=rows.length?rows.join(''):'<div class="ov2-empty">No signals yet — governor metrics appear during autonomy or shortly after load.</div>';
}

// -- Registry accessors + attribute hygiene ------------------------------------
// v2.14.8: the chamber SVG map (CMAP state, renderer, inspector, pan/zoom, polling) is deleted —
// chambers are a layout of the live colony canvas now. These case-tolerant registry accessors
// survive because the Ant Inspector directory and other callers use them; attrSafe keeps ids
// safe when they are embedded in delegated handler attributes.
function antRoleId(r){ return prop(r,'roleId','RoleId','role_id','id')||''; }
function antRoleName(r){ return prop(r,'displayName','DisplayName','display_name')||antRoleId(r); }
function antWorkers(r){ return prop(r,'workers','Workers')||[]; }
function antWorkerId(w){ return prop(w,'workerId','WorkerId','worker_id','id')||''; }
function antWorkerName(w){ return prop(w,'displayName','DisplayName','display_name')||antWorkerId(w); }
function antPurpose(o){ return prop(o,'purpose','Purpose')||''; }
function attrSafe(s){ return String(s??'').replace(/[\\'"`<>\n\r]/g,''); }

/**
 * v2.14.8: colony search now targets the live canvas (the retired chamber SVG previously owned
 * this). Finds the ant by label or id, selects it, opens the inspector, and centres the camera on
 * it — the same outcome the old chamber search gave, on the surviving renderer.
 */
function colonySearchHook(q){
  if(!q) return;
  const needle=String(q).toLowerCase();
  const hit=nodes.find(n=>String(n.label||'').toLowerCase().includes(needle)
                       || String(n.id||'').toLowerCase().includes(needle)
                       || String(n.worker||'').toLowerCase().includes(needle));
  if(!hit) return;
  selectedNode=hit;
  // Centre the camera on the match without changing zoom.
  tX = -(hit.x-cx)*camZ; tY = -(hit.y-cy)*camZ;
  if(typeof showInspector==='function') showInspector(hit);
}

// -- Boot ----------------------------------------------------------------------
// v1.9.1.1: the header/title version comes from the API (single source of truth:
// AnthillRuntime.Version via the public /health endpoint) instead of hardcoded markup,
// which had silently drifted from the runtime version. Guarded by RegressionGuardTests.
async function bootVersion(){
  try{
    const h=await (await fetch(url('/health'))).json();
    const v=h?.data?.version;
    if(!v) return;
    document.title='ANTHILL v'+v;
    const a=document.getElementById('auth-ver'); if(a) a.textContent='v'+v;
    const n=document.getElementById('nav-ver'); if(n) n.textContent='v'+v;
  }catch(e){ /* offline/unreachable: header simply shows no version */ }
}
bootVersion();
bootAuth();



/* ===================================================================================
   v2.6.3 CSP: delegated handler dispatcher.  Inline on*= handlers were renamed to
   data-on*= so the page carries NO inline JS — enabling CSP `script-src 'self'`
   without 'unsafe-inline'.  Handler code is run via a small micro-parser (never eval).
   =================================================================================== */
(function(){
  function splitTop(s, sep){
    var out=[], d=0, q=null, cur='';
    for(var i=0;i<s.length;i++){ var c=s[i];
      if(q){ cur+=c; if(c===q && s[i-1]!=='\\') q=null; continue; }
      if(c==='"'||c==="'"){ q=c; cur+=c; continue; }
      if(c==='('||c==='['||c==='{') d++;
      else if(c===')'||c===']'||c==='}') d--;
      if(c===sep && d===0){ out.push(cur); cur=''; continue; }
      cur+=c;
    }
    if(cur.trim()!=='') out.push(cur);
    return out;
  }
  function coerce(a, el, ev){
    a=a.trim();
    if(a==='this') return el;
    if(a==='event') return ev;
    if(a==='true') return true;
    if(a==='false') return false;
    if(a==='null') return null;
    if(a==='undefined') return undefined;
    if(/^-?\d+(?:\.\d+)?$/.test(a)) return Number(a);
    if(a.length>=2 && (a[0]==="'"||a[0]==='"') && a[a.length-1]===a[0])
      return a.slice(1,-1).replace(/\\(['"\\])/g,'$1');
    if(a[0]==='{'){ try{ return JSON.parse(a.replace(/([{,]\s*)([A-Za-z_$][\w$]*)\s*:/g,'$1"$2":').replace(/'/g,'"')); }catch(e){ return undefined; } }
    if(a[0]==='['){ try{ return JSON.parse(a.replace(/'/g,'"')); }catch(e){ return undefined; } }
    return undefined;
  }
  function runStmt(stmt, el, ev){
    stmt=stmt.trim();
    if(!stmt || stmt==='return false' || stmt==='return true' || stmt==='return') return;
    if(stmt==='event.stopPropagation()'){ ev.stopPropagation(); return; }
    if(stmt==='event.preventDefault()'){ ev.preventDefault(); return; }
    if(/^void\s*\(/.test(stmt)) return;
    var m=stmt.match(/^([\w$]+(?:\.[\w$]+)*)\(([\s\S]*)\)$/);
    if(!m) return;
    var path=m[1].split('.'), rawArgs=m[2].trim();
    var args=(rawArgs==='')?[]:splitTop(rawArgs, ',').map(function(a){ return coerce(a, el, ev); });
    var fn, ctx=null, o;
    if(path.length===1){ fn=window[path[0]]; }
    else {
      o = (path[0]==='this') ? el : window[path[0]];
      for(var i=1;i<path.length-1 && o!=null;i++) o=o[path[i]];
      ctx=o; fn=(o!=null)?o[path[path.length-1]]:undefined;
    }
    if(typeof fn==='function') fn.apply(ctx, args);
  }
  function run(code, el, ev){
    if(el.tagName==='A' || /(^|;)\s*return\s+false\s*(;|$)/.test(code)) ev.preventDefault(); // <a>=JS-action link: no navigate, avoids javascript: CSP violation
    splitTop(code, ';').forEach(function(s){ try{ runStmt(s, el, ev); }catch(e){ console.error('handler failed:', s, e); } });
  }
  function make(evName){
    var attr='data-on'+evName;
    return function(ev){
      var el=ev.target && ev.target.closest ? ev.target.closest('['+attr+']') : null;
      if(el) run(el.getAttribute(attr), el, ev);
    };
  }
  document.addEventListener('click',  make('click'));
  document.addEventListener('change', make('change'));
  document.addEventListener('input',  make('input'));
  document.addEventListener('toggle', make('toggle'), true); // toggle doesn't bubble -> capture

  // v2.6.3 a11y: non-native clickables (div/span with data-onclick) get keyboard activation +
  // focusability, so the delegated handlers are usable without a mouse (WCAG 2.1 keyboard).
  var NATIVE={BUTTON:1,A:1,INPUT:1,SELECT:1,TEXTAREA:1};
  document.addEventListener('keydown', function(ev){
    if(ev.key!=='Enter' && ev.key!==' ' && ev.key!=='Spacebar') return;
    var el=ev.target && ev.target.closest ? ev.target.closest('[data-onclick]') : null;
    if(!el || NATIVE[el.tagName]) return;         // native controls: the browser already activates
    ev.preventDefault();
    run(el.getAttribute('data-onclick'), el, ev);
  });
  function tagOne(el){
    if(NATIVE[el.tagName]) return;
    if(!el.hasAttribute('tabindex')) el.setAttribute('tabindex','0');
    if(!el.hasAttribute('role')) el.setAttribute('role','button');
  }
  function tagTree(node){
    if(!node || node.nodeType!==1) return;
    if(node.hasAttribute && node.hasAttribute('data-onclick')) tagOne(node);
    if(node.querySelectorAll) node.querySelectorAll('[data-onclick]').forEach(tagOne);
  }
  tagTree(document.body);
  try{
    new MutationObserver(function(muts){
      for(var i=0;i<muts.length;i++){ var a=muts[i].addedNodes; for(var j=0;j<a.length;j++) tagTree(a[j]); }
    }).observe(document.body, {childList:true, subtree:true});
  }catch(e){}
})();

// -- v3.8.13: fixed action map for untrusted values ----------------------------------------------
//
// The data-onclick dispatcher above is a micro-interpreter: it splits an attribute on ';' and
// resolves whatever name it finds against `window`. That is safe for the fixed strings the console
// writes itself, and NOT safe for anything a model or an external system supplies, because such a
// value sits inside a quoted argument and an apostrophe ends that argument early.
//
// Patch file paths were reaching it. `ValidateSafePatchPath` rejects absolute paths, traversal,
// blocked directories and disallowed suffixes — it has no reason to care about quotes, and did not.
// So a path-valid filename could append a second statement, and the dispatcher would resolve and
// call it as the operator, with the operator's session, skipping whatever confirmation the real
// button would have shown.
//
// The fix is structural rather than more escaping: the untrusted value now travels in an ordinary
// data-* attribute that is never parsed as code, and the action name is looked up in a map that
// contains exactly the handlers listed here. hasOwnProperty keeps `constructor`, `toString` and the
// rest of the prototype chain from resolving to a function.
(function(){
  var ACTIONS = {
    'open-patches': function(el){
      openPatches({
        mission_id: el.getAttribute('data-mission-id') || '',
        file:       el.getAttribute('data-file') || ''
      });
    }
  };
  document.addEventListener('click', function(ev){
    var el = ev.target && ev.target.closest ? ev.target.closest('[data-action]') : null;
    if(!el) return;
    var name = el.getAttribute('data-action');
    if(!Object.prototype.hasOwnProperty.call(ACTIONS, name)) return;
    ev.preventDefault();
    try{ ACTIONS[name](el, ev); }catch(e){ console.error('action failed:', name, e); }
  });
})();


// -- v2.24.0 Phase E: shadow qualification panel -------------------------------------------------
// Shadow mode never executes. This panel reports what it WOULD have done and how that compared to
// what the operator actually did — the qualification evidence a V3 executor has to earn first.
//
// The one rule this view must never break: an empty scoreboard reads as "not qualified", never as
// a pass. A gate that looks satisfied because nothing was measured is the most dangerous possible
// failure for this subsystem, so zero samples renders as an explicit "no evidence yet".
function hlShadowPct(v){ return (v==null||isNaN(v))?'—':(Math.round(v*1000)/10).toFixed(1)+'%'; }

function hlShadowDuration(seconds){
  const s=Number(seconds)||0;
  if(s<=0) return '—';
  if(s<90) return Math.round(s)+'s';
  if(s<5400) return (s/60).toFixed(1)+'m';
  return (s/3600).toFixed(1)+'h';
}

async function hlLoadShadow(){
  const body=document.getElementById('hl-shadow-body');
  const kpis=document.getElementById('hl-shadow-kpis');
  const state=document.getElementById('hl-shadow-state');
  if(!body) return;
  try{
    const r=await api('/shadow/json');
    if(!(r&&r.success&&r.data)){
      body.innerHTML='<span style="color:var(--dim)">Shadow record unavailable.</span>';
      if(kpis) kpis.innerHTML=''; return;
    }
    const d=r.data, m=d.metrics||{}, t=d.timing||{}, pairs=d.pairs||[];
    const sample=Number(d.qualified_sample)||0;
    const pending=Number(d.awaiting_operator_judgment)||0;

    if(state) state.textContent=d.status||'';

    // Sample first, and stated as a count: every rate below is meaningless without it.
    const kpi=(label,value,tone)=>'<span style="padding:3px 8px;border:1px solid var(--border);border-radius:4px;">'+
      '<span style="color:var(--dim)">'+escapeHtml(label)+'</span> '+
      '<b'+(tone?' style="color:'+tone+'"':'')+'>'+escapeHtml(value)+'</b></span>';

    if(kpis) kpis.innerHTML = sample===0
      ? kpi('Scored incidents','0 — not qualified','var(--dim)')+kpi('Awaiting judgment',String(pending))
      : [ kpi('Scored incidents',String(sample)),
          kpi('Awaiting judgment',String(pending)),
          kpi('Diagnosis precision',hlShadowPct(m.diagnosis_precision)),
          kpi('Action accuracy',hlShadowPct(m.action_selection_accuracy)),
          kpi('Unnecessary actions',hlShadowPct(m.unnecessary_action_rate)),
          kpi('Median resolve',hlShadowDuration(t.median_resolution_seconds)),
          kpi('Invariants', d.invariants_hold?'hold':'VIOLATED',
              d.invariants_hold?'var(--green)':'var(--red)'),
        ].join('');

    // v0.3.8.46: the judgment queue, with the form beside each item. POST /shadow/judge existed
    // for a release while the console showed only a COUNT of what was waiting — an operator asked
    // to clear a queue they could not see. Four yes/no answers and an optional note per item; a
    // recorded judgment turns the recommendation into a scoreable pair above.
    const pendList=d.pending||[];
    const pendHost=document.getElementById('hl-shadow-pending');
    if(pendHost){
      if(!pendList.length){ pendHost.innerHTML=''; }
      else{
        pendHost.innerHTML='<div class="chat-plan-hd" style="margin-top:10px">Awaiting your judgment</div>'+
          pendList.map((p,i)=>`<div class="shadow-judge" data-incident="${escapeHtml(p.incident_id||'')}">`
            + `<div style="font-size:11px;color:var(--text)"><b>${escapeHtml(p.incident_id||'')}</b> — ${escapeHtml(p.diagnosis||'no diagnosis')}</div>`
            + `<div style="font-size:10px;color:var(--muted)">Proposed: ${escapeHtml(p.proposed_action||'—')} · predicted: ${escapeHtml(p.predicted_outcome||'—')} · risk: ${escapeHtml(p.risk_level||'—')}</div>`
            + ['diagnosis_correct|Diagnosis was right','action_was_needed|An action was needed',
               'action_matched|Its action matched what you did','would_have_succeeded|It would have worked']
              .map(f=>{const [k,label]=f.split('|');
                return `<label style="font-size:10px;color:var(--muted);margin-right:10px">`
                  + `<input type="checkbox" data-field="${k}"> ${label}</label>`;}).join('')
            + `<input type="text" data-field="note" placeholder="What actually happened (optional)" `
            + `style="width:100%;margin-top:5px;font-size:10px;padding:4px 7px;background:var(--bg);`
            + `border:1px solid var(--border);border-radius:4px;color:var(--text)">`
            + `<button class="btn btn-ghost shadow-judge-save" style="margin-top:5px">Record judgment</button>`
            + `</div>`).join('');
        pendHost.querySelectorAll('.shadow-judge-save').forEach(btn=>
          btn.addEventListener('click', async ()=>{
            const card=btn.closest('.shadow-judge');
            const get=k=>card.querySelector(`[data-field="${k}"]`);
            btn.disabled=true; btn.textContent='Recording…';
            const r2=await api('/shadow/judge','POST',{
              incident_id:card.dataset.incident,
              diagnosis_correct:get('diagnosis_correct').checked,
              action_was_needed:get('action_was_needed').checked,
              action_matched:get('action_matched').checked,
              would_have_succeeded:get('would_have_succeeded').checked,
              note:get('note').value.trim(),
            });
            if(r2&&r2.success) hlLoadShadow();
            else{ btn.disabled=false; btn.textContent='Record judgment'; }
          }));
      }
    }

    if(sample===0){
      body.innerHTML='<span style="color:var(--dim)">No scored incidents yet — shadow mode has not '+
        'qualified anything. It records a recommendation when an incident opens and stays unscored '+
        'until an operator says what actually happened'+
        (pending>0?('; '+pending+' recommendation(s) are waiting on that judgment.'):'. ')+
        ' Observation is off by default (shadow_observation_enabled).</span>';
      return;
    }

    const yn=(v,good)=>'<span style="color:'+((!!v===good)?'var(--green)':'var(--red)')+'">'+(v?'yes':'no')+'</span>';
    body.innerHTML='<table style="width:100%;border-collapse:collapse;font-size:10px;">'+
      '<tr style="color:var(--dim);text-align:left;"><th>Incident</th><th>Diagnosis</th>'+
      '<th>Proposed</th><th>Predicted</th><th>Risk</th><th>Rollback</th>'+
      '<th>Would advise</th><th>Dx right</th><th>Action matched</th></tr>'+
      pairs.map(p=>'<tr style="border-top:1px solid var(--border);">'+
        '<td>'+escapeHtml(p.incident_id)+'</td>'+
        '<td>'+escapeHtml(p.diagnosis)+'</td>'+
        '<td>'+escapeHtml(p.proposed_action)+'</td>'+
        '<td>'+escapeHtml(p.predicted_outcome)+'</td>'+
        '<td>'+escapeHtml(p.risk_level)+(Number(p.risk_requires_approval)?' <span style="color:var(--amber)">(approval)</span>':'')+'</td>'+
        '<td>'+escapeHtml(p.rollback_plan)+'</td>'+
        '<td>'+(Number(p.would_recommend_execution)?'yes':'no')+'</td>'+
        '<td>'+yn(Number(p.diagnosis_correct),true)+'</td>'+
        '<td>'+yn(Number(p.action_matched),true)+'</td>'+
      '</tr>').join('')+'</table>'+
      '<div style="margin-top:6px;font-size:9px;color:var(--dim);">Shadow mode has no action pathway — '+
      'nothing in this table was executed.</div>';
  }catch(e){
    body.innerHTML='<span style="color:var(--dim)">Shadow record unavailable.</span>';
  }
}

// v2.25.0: one plain-English sentence per automation run — the conversation's reply line. Pure
// (data in, {text,tone} out) so it can be tested by reading, like mission-thread's helpers. The
// vocabulary is honest about restraint: a skip is the engine WORKING (cooldowns and caps are
// safety features), so skips read as deliberate quiet, not as failures.
function hlAutoStory(run){
  const act={propose_restart:'proposed a restart (approval-gated — nothing runs until a human agrees)',
             open_incident:'opened an incident',
             alert:'sent an alert',
             warn_event:'recorded a warning event',
             flag_risk:'flagged the target as at-risk'};
  const did=(run.action_taken&&run.action_taken.length)?run.action_taken:null;
  switch(run.outcome){
    case 'fired':
      return {text:did||('it '+(act[run.action_kind]||'acted')+'.'),tone:'var(--orange)'};
    case 'skipped_cooldown':
      return {text:'it stayed quiet — it acted on this recently and is still in cooldown.',tone:'var(--dim)'};
    case 'skipped_cap':
      return {text:'it stayed quiet — it reached its daily cap for this rule.',tone:'var(--dim)'};
    case 'skipped_pending':
      return {text:'it held off — an earlier proposal it made is still waiting for a human decision.',tone:'var(--dim)'};
    case 'error':
      return {text:'it tried to act but failed: '+(did||run.trigger_detail||'unknown error'),tone:'var(--red)'};
    default:
      return {text:did||('outcome: '+(run.outcome||'unknown')),tone:'var(--muted)'};
  }
}


/* ===========================================================================
 * v3.7.1 — Conversations: the operator surface for the escalation boundary.
 *
 * This exists because the v3.7.0 backend shipped with no way for a human to
 * reach it. Endpoints nobody can call from the console are the same "no call
 * site, no feature" failure one layer up — the colony could gate escalation
 * perfectly and no operator would ever see a gate.
 *
 * The design turns on ONE state. `needs_operator` means the colony has stopped
 * and nothing moves until a human answers; every other state is informational.
 * So that state gets the colour, the position and the button, and the rest is
 * deliberately quiet. A panel where "waiting for you" looks like "running" is
 * a panel that trains people to ignore it.
 * ======================================================================== */

/**
 * Tell the operator something. Routed through the console's existing notifier rather than a new
 * one — and guarded, because a panel that throws when the toast helper is absent takes the whole
 * dashboard poll down with it.
 */
function convSay(msg, ok){ if(typeof pcToast==='function') pcToast(msg, ok!==false); }

var CONV_POLICY_LABEL = { ask:'asks first', autoapprove:'auto-approves', bypass:'NO APPROVALS' };

/** Poll + render. Cheap: one GET, and only while the Overview is on screen. */
async function pollConversations(){
  var body=document.getElementById('ov-conversations-body');
  // The same visibility test every other Overview poll uses — copied deliberately rather than
  // wrapped in a new helper, so this panel cannot drift from how the rest of the page behaves.
  if(!body || !document.getElementById('page-overview')?.classList.contains('active')) return;
  // Guarded like every other poll: an unhandled rejection here is an unhandled rejection on a
  // five-second timer, and the panel that reports refusals must not become a source of noise itself.
  try{
    const r=await api('/conversations');
    if(!r||!r.success) return;
    renderConversations((r.data&&r.data.conversations)||[]);
  }catch(e){ pollWarnOnce('pollConversations', e); }
}

function renderConversations(list){
  const body=document.getElementById('ov-conversations-body');
  if(!body) return;

  // v0.3.8.42 (§3): the widget's own composer and per-row message boxes retired — Chat is the one
  // conversation surface. What remains is what a dashboard widget owes: what is WAITING on the
  // operator, one-click answers through the same convApprove/convCancel path Chat uses (one
  // approval path, not two), and a way into the real conversation.
  if(!document.getElementById('conv-list')){
    body.innerHTML =
      `<div class="conv-new">
         <button class="conv-btn primary" data-onclick="go('/chat')">✎ Start a conversation in Chat</button>
       </div>
       <div id="conv-list"></div>`;
  }

  // The header counts what is WAITING, not what exists. "12 conversations" is a number nobody can
  // act on; "2 waiting for you" is the only count that changes what an operator does next.
  const waitingCount=list.filter(c=>c.needs_operator).length;
  const countEl=document.getElementById('conv-count');
  if(countEl){
    countEl.textContent = waitingCount ? waitingCount+' waiting for you'
                        : list.length  ? list.length+' active' : '';
    countEl.classList.toggle('conv-count-attn', waitingCount>0);
  }

  const listEl=document.getElementById('conv-list');
  if(!listEl) return;

  if(!list.length){
    listEl.innerHTML='<div class="conv-empty">No conversations yet. Start one in Chat — it stays chat until you approve real work.</div>';
    return;
  }

  // Conversations needing a human float to the TOP. An attention state buried under six healthy
  // rows is an attention state nobody acts on.
  const sorted=list.slice().sort((a,b)=>(b.needs_operator?1:0)-(a.needs_operator?1:0));

  listEl.innerHTML=sorted.slice(0,12).map(c=>{
    const pol=(c.policy||'ask').toLowerCase();
    const polCls=pol==='bypass'?'danger':pol==='autoapprove'?'warn':'';
    const waiting=(c.waiting_on||[]);
    const title=escapeHtml(c.title||'(untitled)');

    // The one loud row: what it is waiting for, and the button that answers it.
    const attention = c.needs_operator
      ? `<div class="conv-attn">
           <span class="conv-attn-label">Waiting for you:</span>
           <span class="conv-attn-what">${escapeHtml(waiting.join(', '))}</span>
           ${waiting.map(a=>`<button class="conv-btn approve" data-onclick="convApprove('${jsArg(c.id)}','${jsArg(a)}')">Approve ${escapeHtml(a)}</button>`).join('')}
           <button class="conv-btn" data-onclick="convCancel('${jsArg(c.id)}')">Cancel</button>
         </div>`
      : '';

    return `<div class="conv-item${c.needs_operator?' needs-op':''}${c.cancelled?' cancelled':''}">
      <div class="conv-head">
        <span class="conv-title">${title}</span>
        <span class="conv-policy ${polCls}" title="Approval policy${c.policy_set_by?' — set by '+escapeHtml(c.policy_set_by):''}">${escapeHtml(CONV_POLICY_LABEL[pol]||pol)}</span>
      </div>
      <div class="conv-doing" title="${escapeHtml(c.doing||'')}">${escapeHtml((c.doing||'').slice(0,240))}</div>
      ${(c.mission_ids&&c.mission_ids.length)?`<div class="conv-missions">${c.mission_ids.length} mission(s) started</div>`:''}
      ${attention}
      ${c.cancelled?'':`<div class="conv-say">
        <button class="conv-btn" data-onclick="openConversationInChat('${jsArg(c.id)}')">Open in Chat</button>
      </div>`}
    </div>`;
  }).join('');
}

/** v0.3.8.42 (§3): the widget's way into the real conversation surface. */
function openConversationInChat(id){ go('/chat'); chatOpen(id); }

// v0.3.8.42 (§3): convStart and convSend retired with the widget's composer and per-row message
// boxes — messages travel through the Chat page. convApprove and convCancel stay: they are the ONE
// approval path, shared by the chat escalation gate and the widget's attention rows.

/**
 * Answer a pending escalation.
 *
 * Re-sends the LAST message with the approval attached, which is what the operator means by
 * "approve" — the request that was refused is the request they are now permitting.
 */
async function convApprove(id, action){
  const detail=await api('/conversations/'+id);
  const turns=(detail&&detail.data&&detail.data.turns)||[];
  const last=turns.length?turns[turns.length-1].content:'';
  const answers={}; answers[action]='approve';
  // v0.3.8.46, found live: a MID-MISSION action (a tool gate the running mission raised) is
  // approved by re-sending the message — and the re-send meets the start_mission gate FIRST,
  // which ate the answer: the click approved nothing and both actions sat waiting. A secondary
  // gate can only exist because start_mission was already approved (or covered by a standing
  // policy) for this very message, so restating that recorded decision is not a new grant.
  if(action!=='start_mission') answers['start_mission']='approve';
  const r=await api('/conversations/'+id+'/turns','POST',{ message:last||'proceed', mode:'mission', answers:answers });
  if(r&&r.data) convSay(r.data.summary||'Approved', r.data.started!==false);
  apiCacheBust('/conversations'); pollConversations();
}

async function convCancel(id){
  const r=await api('/conversations/'+id+'/cancel','POST',{});
  if(r) convSay(r.message||'Cancelled', true);
  apiCacheBust('/conversations'); pollConversations();
}

/* ==========================================================================
 * v3.7.2 — TOOLS AND WORKSPACES: the rest of the missing operator surface
 *
 * An endpoint sweep found sixteen routes no client calls. Most are honestly
 * machine-facing (readiness, config health, runtime inventory). Four were not:
 * GET /tools, POST and DELETE /tools/user, and GET /workspaces. Those are
 * v3.4.1, v3.4.2 and v3.5.0 — three shipped subsystems an operator could not
 * see, let alone use. Same defect as v3.7.0's unreachable runtime, one layer
 * out: the feature exists, and nobody can get to it.
 *
 * Both panels are built around a single rule: SHOW WHAT IS WRONG FIRST. A
 * console that renders forty healthy rows and one broken one, all alike, has
 * technically displayed the problem and practically hidden it.
 * ========================================================================== */

var TOOL_STATUS_LABEL = {
  registered:'ready', gated_off:'switched off', planned:'not built',
  // "rejected" and "disabled" are deliberately worded to point at their DIFFERENT remedies: a
  // rejected definition has to be rewritten, a disabled one is re-enabled in a click.
  rejected:'rejected — see why', disabled:'switched off by you',
};

/* v0.3.8.42: the poll serves whichever Tools surface is on screen — the Overview widget or the
 * Tools page — so the CRUD actions' refresh reaches the surface the operator is looking at. */
function activeToolsTarget(){
  if(document.getElementById('page-toolsview')?.classList.contains('active')) return 'toolsview-body';
  if(document.getElementById('ov-tools-body') && document.getElementById('page-overview')?.classList.contains('active')) return 'ov-tools-body';
  return null;
}
async function pollToolsPanel(){
  const target=activeToolsTarget(); if(!target) return;
  try{
    const r=await api('/tools');
    if(!r||!r.success) return;
    renderToolsPanel(r.data||{}, target, target==='toolsview-body'?{inventory:true}:undefined);
  }catch(e){ pollWarnOnce('pollToolsPanel', e); }
}

function renderToolsPanel(d, targetId, opts){
  const body=document.getElementById(targetId||'ov-tools-body');
  if(!body) return;
  // One renderer, two possible surfaces, one DOM: the inactive surface must not keep a stale copy
  // of the registration form, or the document carries duplicate tp-* ids and getElementById feeds
  // the CRUD actions from whichever copy came first.
  const other=document.getElementById((targetId||'ov-tools-body')==='ov-tools-body'?'toolsview-body':'ov-tools-body');
  if(other&&other.querySelector('#tp-name')) other.innerHTML='<div class="hud-state">Loading…</div>';

  const tools=d.tools||[], userTools=d.user_tools||[], fitness=d.model_fitness||[];

  // ---- the loudest thing on the panel, because it fails SILENTLY -----------
  //
  // A role routed to a model that cannot call tools produces a confident answer that skipped every
  // tool. In a transcript that reads as a weak model rather than as a misconfiguration an operator
  // could fix in thirty seconds. Only the misfits are listed: a table of all-green is noise, and
  // noise is what taught everyone to skim past this kind of panel.
  // v0.3.8.41 — UNRESOLVED is separated from UNFIT, because they are different problems.
  //
  // "No model is chosen" is one fact about the colony with one remedy, and it made every role look
  // individually broken: seven rows naming capabilities the operator's model was never asked about,
  // on a host with a perfectly good model installed. Listing it per role also buries the remedy
  // under the symptom.
  const unresolved=fitness.filter(f=>f.unresolved);
  const unfit=fitness.filter(f=>!f.fit && !f.unresolved);

  const unresolvedHtml = unresolved.length
    ? `<div class="tp-alarm">
         <div class="tp-alarm-hd">${unresolved.length} role(s) have no model to run on</div>
         <div class="tp-alarm-row"><span class="tp-unmet">${escapeHtml(unresolved[0].unresolved)}</span></div>
         <div class="tp-alarm-row"><span class="tp-role">Affected</span>
           <span class="tp-model">${escapeHtml(unresolved.map(f=>f.role).join(', '))}</span></div>
       </div>`
    : '';

  const fitnessHtml = unresolvedHtml + (unfit.length
    ? `<div class="tp-alarm">
         <div class="tp-alarm-hd">${unfit.length} role(s) routed to a model that cannot do the job</div>
         ${unfit.map(f=>`<div class="tp-alarm-row">
            <span class="tp-role">${escapeHtml(f.role)}</span>
            <span class="tp-model">${escapeHtml((f.provider||'')+' / '+(f.model||'—'))}</span>
            <span class="tp-unmet">${escapeHtml((f.unmet||[]).join(', '))}</span>
          </div>`).join('')}
       </div>`
    : (fitness.length && !unresolved.length)
      ? `<div class="tp-quiet">All ${fitness.length} contracted roles are routed to a capable model.</div>`
      : '');

  // ---- tools that will not run, and why ------------------------------------
  const broken=tools.filter(t=>t.status!=='registered');
  const brokenHtml = broken.length
    ? `<div class="tp-section-hd">${broken.length} of ${tools.length} tools cannot be dispatched</div>
       ${broken.map(t=>`<div class="tp-item ${t.status==='planned'?'planned':'off'}">
          <span class="tp-name">${escapeHtml(t.name)}</span>
          <span class="tp-status">${escapeHtml(TOOL_STATUS_LABEL[t.status]||t.status)}</span>
          <span class="tp-why">${t.status==='planned'
            ? 'referenced by a contract, not implemented in this build'
            : 'implemented, but a configuration gate is off'}</span>
        </div>`).join('')}`
    : `<div class="tp-quiet">All ${tools.length} tools are registered and dispatchable.</div>`;

  // ---- roles blocked outright ---------------------------------------------
  const blocked=d.roles_blocked_by_missing_tools||[];
  const blockedHtml = blocked.length
    ? `<div class="tp-blocked">Roles authorised to dispatch nothing: ${escapeHtml(blocked.join(', '))}</div>`
    : '';

  // ---- operator-defined tools ---------------------------------------------
  //
  // Rejected definitions are listed WITH their problems. A stored definition that did not register
  // is the state most worth surfacing: it is present in the editor and not callable, which without
  // this reads exactly like the tool being broken at runtime.
  // The composer. Rebuilt only when absent, so a half-typed definition survives the poll — the
  // same rule the Conversations panel needed, and for the same reason: this form takes a URL and a
  // JSON schema, which is more than anyone wants to retype every twenty seconds.
  //
  // Only `http` is offered. The other three kinds parse and store, but nothing in this build
  // BUILDS them, so listing them would be offering an operator a tool that saves and never runs.
  const addHtml = `
    <div class="tp-add">
      <input id="tp-name" class="conv-input" placeholder="tool name (e.g. jira_issue)" />
      <input id="tp-desc" class="conv-input" placeholder="what it does — the model reads this" />
      <input id="tp-url"  class="conv-input" placeholder="https://host/path — host must be allow-listed" />
      <select id="tp-method" class="conv-select">
        <option value="GET">GET</option><option value="POST">POST</option>
      </select>
      <button class="conv-btn primary" data-onclick="toolAdd()">Add / Update</button>
      <!-- v0.3.8.56 (field report: "no way to edit, change, add, or remove"): the allow-list is
           EDITABLE here — a form whose every submission is rejected by an allow-list only
           config.json could change was an add button in name only. Same name = update: the Edit
           button on each row loads its stored definition into this form. -->
      <div class="tp-hosts" style="display:flex;gap:6px;align-items:center;flex-wrap:wrap;">
        <span>Allow-listed hosts:</span>
        <input id="tp-hosts-input" class="conv-input" style="flex:1;min-width:200px;"
               placeholder="api.example.com, hooks.internal — empty rejects every definition"
               value="${escapeHtml((d.user_tool_allowed_hosts||[]).join(', '))}">
        <button class="conv-btn" data-onclick="toolHostsSave()">Save hosts</button>
      </div>
    </div>`;

  const utHtml = !d.user_tools_enabled
    ? `<div class="tp-quiet">Operator-defined tools are switched off in this build.</div>`
    : addHtml + (userTools.length
        ? userTools.map(u=>{
            // The buttons say what they DO. "Remove" used to call the disable endpoint, which left
            // a row behind that looked broken — the label promised one thing and the API did
            // another, which is the worst possible pairing on a destructive control.
            const actions = u.status==='disabled'
              ? `<button class="conv-btn" data-onclick="toolEnable('${jsArg(u.name)}')">Enable</button>`
              : `<button class="conv-btn" data-onclick="toolDisable('${jsArg(u.name)}')">Disable</button>`;
            return `<div class="tp-item ${u.status==='registered'?'':'off'}">
             <span class="tp-name">${escapeHtml(u.name)}</span>
             <span class="tp-status">${escapeHtml(u.kind)} · ${escapeHtml(TOOL_STATUS_LABEL[u.status]||u.status)}</span>
             <span class="tp-why">${escapeHtml((u.problems||[]).join('; ')||u.description||'')}</span>
             <button class="conv-btn" data-onclick="toolEdit('${jsArg(u.name)}')">Edit</button>
             ${actions}
             <button class="conv-btn" data-onclick="toolDelete('${jsArg(u.name)}')">Delete</button>
           </div>`;
          }).join('')
        : `<div class="tp-quiet">No operator-defined tools yet.</div>`);

  // Like the Conversations header, this counts PROBLEMS rather than inventory. "31 tools" is a
  // number nobody acts on; "2 problems" is the one that decides whether to open the panel.
  const problems=unfit.length + broken.length + userTools.filter(u=>u.status==='rejected').length;
  const countEl=document.getElementById('tools-count');
  if(countEl){
    countEl.textContent = problems ? problems+' need attention' : 'all healthy';
    countEl.classList.toggle('conv-count-attn', problems>0);
  }

  // The whole panel re-renders, so anything half-typed is carried across explicitly. A twenty-second
  // poll that erases a URL mid-entry makes the form unusable in a way that looks like a browser bug.
  const keep=['tp-name','tp-desc','tp-url','tp-method','tp-hosts-input']
    .map(id=>[id, document.getElementById(id)?.value]);

  // The destination page owes the full inventory the widget deliberately omits (the widget's rule
  // is show-what-is-wrong-first). Built-ins are read-only by contract: the API offers no mutation
  // for them, so the UI offers none either.
  const inventoryHtml = opts&&opts.inventory
    ? `<div class="tp-group"><div class="tp-section-hd">All built-in tools (${tools.length}) — read-only</div>`
      + (tools.map(t=>`<div class="tp-item ${t.status==='registered'?'':'off'}">
           <span class="tp-name">${escapeHtml(t.name||t)}</span>
           <span class="tp-status">${escapeHtml(TOOL_STATUS_LABEL[t.status]||t.status||'')}</span>
           <span class="tp-why">${escapeHtml(t.description||'')}</span>
         </div>`).join('')||'<div class="tp-quiet">None.</div>')
      + `</div>`
    : '';

  body.innerHTML =
    fitnessHtml + blockedHtml +
    `<div class="tp-group">${brokenHtml}</div>` +
    `<div class="tp-group"><div class="tp-section-hd">Operator-defined tools</div>${utHtml}</div>` +
    inventoryHtml;

  for(const [id, value] of keep){
    if(value===undefined || value==='') continue;
    const el=document.getElementById(id); if(el) el.value=value;
  }
}

/**
 * Register an operator-defined HTTP tool.
 *
 * The server validates and REJECTS with a list of problems; those are shown verbatim rather than
 * summarised, because they name the exact reason — an unlisted host, a malformed schema — and a
 * generic "invalid definition" would send an operator back to guess which.
 */
async function toolAdd(){
  const v=id=>(document.getElementById(id)?.value||'').trim();
  const name=v('tp-name'), url=v('tp-url');
  if(!name || !url){ convSay('A tool needs a name and a URL.', false); return; }

  const r=await api('/tools/user','POST',{
    name: name,
    description: v('tp-desc'),
    kind: 'http',
    config: { url: url, method: v('tp-method')||'GET' },
  });

  if(r&&r.success){
    ['tp-name','tp-desc','tp-url'].forEach(id=>{ const el=document.getElementById(id); if(el) el.value=''; });
    convSay(r.message||('Tool '+name+' registered'), true);
  }else{
    const problems=(r&&r.data&&r.data.problems)||[];
    convSay(problems.length?problems.join('; '):((r&&r.message)||'Rejected'), false);
  }
  apiCacheBust('/tools'); pollToolsPanel();
}

/**
 * v0.3.8.56 — EDIT: load a stored definition back into the form. POST /tools/user upserts by
 * name, so Add / Update with the same name IS the save; nothing has to be retyped but the change.
 */
async function toolEdit(name){
  const listing=await api('/tools');
  const d=((listing&&listing.data&&listing.data.user_tools)||[])
    .find(u=>String(u.name).toLowerCase()===String(name).toLowerCase());
  if(!d){ convSay('No stored definition for '+name, false); return; }
  const set=(id,v)=>{ const el=document.getElementById(id); if(el) el.value=v||''; };
  set('tp-name', d.name); set('tp-desc', d.description);
  set('tp-url', (d.config&&d.config.url)||''); set('tp-method', (d.config&&d.config.method)||'GET');
  document.getElementById('tp-name')?.scrollIntoView({block:'center'});
  convSay('Definition loaded — change what you need and press Add / Update.', true);
}

/**
 * v0.3.8.56 — the allow-list, editable where it gates. user_tool_allowed_hosts is an editable
 * setting; only config.json could change it before, which made the add form a button that could
 * only ever be refused on a fresh install.
 */
async function toolHostsSave(){
  const raw=(document.getElementById('tp-hosts-input')?.value||'');
  const hosts=raw.split(',').map(s=>s.trim()).filter(Boolean);
  const r=await api('/settings','POST',{user_tool_allowed_hosts:hosts});
  convSay((r&&r.message)||(r&&r.success?'Allow-list saved.':'Save failed.'), !!(r&&r.success));
  apiCacheBust('/tools'); pollToolsPanel();
}

/**
 * Stop offering a tool, but KEEP its definition.
 *
 * Not confirmed, because it is reversible in one click — and a confirm on a reversible action is
 * what teaches people to dismiss the confirm on the irreversible one two buttons along.
 */
async function toolDisable(name){
  const r=await api('/tools/user/'+encodeURIComponent(name),'DELETE');
  convSay((r&&r.message)||'Disabled', !!(r&&r.success));
  apiCacheBust('/tools'); pollToolsPanel();
}

/**
 * Turn a disabled tool back on by re-submitting its STORED definition.
 *
 * The listing already carries config, description and roles, so nothing has to be retyped. Without
 * this, disabling was a one-way door dressed up as a toggle: the row stayed, the tool never came
 * back, and the only route to re-enabling it was to remember the URL and add it again.
 */
async function toolEnable(name){
  const listing=await api('/tools');
  const d=((listing&&listing.data&&listing.data.user_tools)||[])
    .find(u=>String(u.name).toLowerCase()===String(name).toLowerCase());
  if(!d){ convSay('No stored definition for '+name, false); return; }

  const r=await api('/tools/user','POST',{
    name:d.name, description:d.description, kind:d.kind,
    config:d.config||{}, allowed_roles:d.allowed_roles||[],
  });
  if(r&&r.success) convSay(r.message||('Tool '+name+' enabled'), true);
  else convSay(((r&&r.data&&r.data.problems)||[]).join('; ')||((r&&r.message)||'Rejected'), false);
  apiCacheBust('/tools'); pollToolsPanel();
}

/**
 * Delete the definition outright.
 *
 * Confirmed, and the confirm says what is lost. `purge=true` is the difference between this and
 * Disable: without it the server keeps the row deliberately, so an audit can still explain a
 * transcript in which a since-revoked tool was called.
 */
async function toolDelete(name){
  // v0.3.8.42: uiConfirm, not window.confirm — the native dialog blocks the renderer's main
  // thread, which is the exact defect the in-app modal exists to prevent (see uiConfirm).
  if(!await uiConfirm('Delete the definition for "'+name+'" permanently? Disable keeps it and stops offering it to agents. Delete cannot be undone from the console.',
      {title:'Delete tool', ok:'Delete', danger:true})) return;
  const r=await api('/tools/user/'+encodeURIComponent(name)+'?purge=true','DELETE');
  convSay((r&&r.message)||'Deleted', !!(r&&r.success));
  apiCacheBust('/tools'); pollToolsPanel();
}

/**
 * v3.8.0 — durable attempts, and the ones waiting on a person.
 *
 * Recovery reports abandoned work to stderr at startup, which is nobody's console. An attempt that
 * may have left effects outside the process is deliberately NOT redelivered automatically — it waits
 * for an operator who can look — and a decision waiting for a human it never reaches is a stall
 * wearing the costume of a policy.
 */
async function pollAttemptsPanel(){
  var body=document.getElementById('ov-attempts-body');
  if(!body || !document.getElementById('page-overview')?.classList.contains('active')) return;
  try{
    const r=await api('/attempts');
    if(!r||!r.success) return;
    renderAttemptsPanel(r.data||{});
  }catch(e){ pollWarnOnce('pollAttemptsPanel', e); }
}

var ATTEMPT_STATE_LABEL = {
  running:'running', succeeded:'succeeded', failed:'failed',
  // Worded as the fact rather than the verdict: nobody watched this end, which is a different
  // statement from "it did not work" and carries a different remedy.
  abandoned:'ended unobserved',
};

function renderAttemptsPanel(d){
  const body=document.getElementById('ov-attempts-body');
  if(!body) return;

  const review=d.needs_review||[], recent=d.recent||[];

  const countEl=document.getElementById('attempts-count');
  if(countEl){
    countEl.textContent = review.length ? review.length+' need review'
                        : recent.length ? recent.length+' recent' : '';
    countEl.classList.toggle('conv-count-attn', review.length>0);
  }

  // The one loud block. These cannot be resolved by policy: the attempt may have completed the
  // change it died during, and nothing observable says whether it did. Retrying is the operator's
  // call because only a person can go and look.
  const reviewHtml = review.length
    ? `<div class="tp-alarm">
         <div class="tp-alarm-hd">${review.length} attempt(s) ended unobserved after touching something</div>
         <div class="at-note">Each may have completed the change it died during. Nothing in the record can
           say whether it did — check the workspace or the target before retrying.</div>
         ${review.map(a=>`<div class="tp-alarm-row">
            <span class="tp-role">${escapeHtml(String(a.task_id).slice(0,8))}</span>
            <span class="tp-model">attempt ${escapeHtml(String(a.number))} · worker ${escapeHtml(a.worker_id||'—')}</span>
            <span class="tp-unmet">${escapeHtml(a.failure_reason||'no reason recorded')}</span>
          </div>`).join('')}
       </div>`
    : '';

  const recentHtml = recent.length
    ? recent.slice(0,12).map(a=>`<div class="tp-item ${a.state==='succeeded'?'':'off'}">
         <span class="tp-name">${escapeHtml(String(a.task_id).slice(0,8))} · #${escapeHtml(String(a.number))}</span>
         <span class="tp-status">${escapeHtml(ATTEMPT_STATE_LABEL[a.state]||a.state)}</span>
         <span class="tp-why">${escapeHtml(a.failure_reason||[a.provider,a.model].filter(Boolean).join(' / ')||'')}</span>
       </div>`).join('')
    : `<div class="conv-empty">No attempts recorded yet. One is written per task the moment a worker claims it.</div>`;

  body.innerHTML = reviewHtml +
    `<div class="tp-group"><div class="tp-section-hd">Recent attempts</div>${recentHtml}</div>`;
}

async function pollWorkspacesPanel(){
  var body=document.getElementById('ov-workspaces-body');
  if(!body || !document.getElementById('page-overview')?.classList.contains('active')) return;
  try{
    const r=await api('/workspaces');
    if(!r||!r.success) return;
    renderWorkspacesPanel((r.data&&r.data.workspaces)||[]);
  }catch(e){ pollWarnOnce('pollWorkspacesPanel', e); }
}

function renderWorkspacesPanel(list){
  const body=document.getElementById('ov-workspaces-body');
  if(!body) return;

  if(!list.length){
    body.innerHTML='<div class="conv-empty">No mission workspaces. One is prepared when a code mission starts, and its record outlives the directory.</div>';
    return;
  }

  // Live work first, then everything else newest-first. A cleaned workspace is a record, not a
  // thing to act on, so it must not sit above one an agent is currently writing into.
  const sorted=list.slice().sort((a,b)=>(b.usable?1:0)-(a.usable?1:0)||String(b.updated_at||'').localeCompare(String(a.updated_at||'')));

  body.innerHTML=sorted.slice(0,15).map(w=>`
    <div class="ws-item${w.usable?' live':''}">
      <div class="ws-head">
        <span class="ws-state">${escapeHtml(w.state||'')}</span>
        <span class="ws-mission">${escapeHtml(w.mission_id||'—')}</span>
        ${w.retained_by?`<span class="ws-retained" title="${escapeHtml(w.retain_reason||'')}">retained by ${escapeHtml(w.retained_by)}</span>`:''}
      </div>
      <div class="ws-meta" title="${escapeHtml(w.root||'')}">
        ${escapeHtml(w.mode||'')} · based on ${escapeHtml(String(w.base_revision||'').slice(0,10)||'—')}
        ${(w.project_types&&w.project_types.length)?' · '+escapeHtml(w.project_types.join(', ')):''}
      </div>
    </div>`).join('');
}
