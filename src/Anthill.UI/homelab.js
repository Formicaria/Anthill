/**
 * The homelab domain, lifted out of app.js. v0.3.8.52 — AUTONOMY-10, the app.js split.
 *
 * WHY THIS FILE EXISTS: app.js was one 10,000-line unit with no boundaries. This is the first
 * domain out, chosen because it is the largest CONTIGUOUS one — inventory, automation rules, backup
 * intelligence, virtualization, the command center, incidents, approval-gated actions, the category
 * sub-pages, the widget runtime, the collection manager, network/risk and health checks were an
 * unbroken run of ~1,600 lines, so moving them is a relocation rather than an untangling.
 *
 * HOW IT LOADS, and why it is not `type="module"`. The console's `data-on*` handler interpreter
 * resolves callbacks with `window[name]` (the dispatcher at the bottom of app.js) — that lookup is
 * what lets the page stay CSP-clean with no inline script. Module scope would put every handler
 * name out of its reach at once, and the failure mode is a silently dead button, not a build error.
 * So this follows the pattern mission-thread.js and dashboard-grid.js already set here: a plain
 * deferred script, pinned by LogicalName in Anthill.Api.csproj and served same-origin.
 *
 * LOAD ORDER IS LOAD-BEARING: this must come AFTER app.js. The one statement here that runs at load
 * time is the PAGE_ENTER['homelab'] registration below, and PAGE_ENTER is defined in app.js.
 * ConsoleAssetSplitTests asserts that ordering so a later edit cannot quietly reverse it.
 *
 * The code below is what was in app.js, moved and not rewritten. Tightening these globals into an
 * explicit namespace is a separate change with separate risk; doing both at once would make any
 * regression impossible to attribute to one of them.
 */

// -- Homelab Inventory (v1.10.0, NORTH_STAR Phase 6) ----------------------------
let HL_HOSTS=[], HL_SVCS=[], HL_DEPS=[];
PAGE_ENTER['homelab']=()=>loadHomelab();

function hlMsg(m,ok){ const el=document.getElementById('hl-msg'); if(!el) return;
  el.textContent=m; el.style.color=ok?'var(--green)':'var(--red)';
  setTimeout(()=>{ if(el.textContent===m) el.textContent=''; },6000); }

// -- v2.5.0 Phase 14: automation rules -----------------------------------------------------------
async function hlLoadAutomation(){
  const body=document.getElementById('hl-auto-body'); if(!body) return;
  try{
    const [r,runs]=await Promise.all([api('/homelab/automation/rules'),api('/homelab/automation/runs')]);
    if(!(r&&r.success&&r.data)){ body.innerHTML='<span style="color:var(--dim)">Automation unavailable.</span>'; return; }
    const d=r.data, rules=d.rules||[];
    const st=document.getElementById('hl-auto-state');
    if(st) st.textContent=d.enabled?'engine ON · rules fire only when individually enabled':'engine OFF (homelab_automation_enabled) — rules are stored but nothing evaluates';
    body.innerHTML=!rules.length
      ? '<span style="color:var(--dim)">No rules yet. POST /homelab/automation/rules — e.g. disk_above_percent → warn_event, service_down → propose_restart (restart is approval-gated, never direct).</span>'
      : '<table style="width:100%;border-collapse:collapse;font-size:10px;">'+
        '<tr style="color:var(--dim);text-align:left;"><th>Rule</th><th>Trigger</th><th>Action</th><th>Cooldown</th><th>Cap/day</th><th>State</th><th></th></tr>'+
        rules.map(x=>'<tr style="border-top:1px solid var(--border);">'+
          '<td><b style="color:var(--text)">'+escapeHtml(x.name)+'</b></td>'+
          '<td>'+escapeHtml(x.trigger_kind)+(x.target?' · '+escapeHtml(x.target):'')+(x.trigger_kind==='disk_above_percent'||x.trigger_kind==='repeated_health_failure'?' ≥'+x.threshold:'')+'</td>'+
          '<td>'+escapeHtml(x.action_kind)+'</td><td>'+x.cooldown_minutes+'m</td><td>'+x.max_runs_per_day+'</td>'+
          '<td style="color:'+(x.enabled?'var(--green)':'var(--dim)')+';font-weight:700;">'+(x.enabled?'ENABLED':'off')+'</td>'+
          '<td><button class="btn btn-ghost" style="font-size:9px;" data-onclick="hlAutoToggle(\''+attrSafe(x.id)+'\','+(x.enabled?'0':'1')+')">'+(x.enabled?'Disable':'Enable')+'</button></td>'+
        '</tr>').join('')+'</table>';
    const runBox=document.getElementById('hl-auto-runs');
    const rr=(runs&&runs.success&&runs.data)||[];
    // v2.25.0 — Automation as a conversation (the NORTH_STAR v2.16.0 "Next:" item, same inversion
    // as Missions): lead with what happened and what the colony did about it in plain English;
    // the raw outcome token and full trigger detail sit behind a hover/title, not in front.
    if(runBox) runBox.innerHTML=rr.length
      ? '<div style="color:var(--dim);margin-bottom:2px;">WHAT AUTOMATION HAS BEEN DOING</div>'+rr.slice(0,8).map(x=>{
          const st=hlAutoStory(x);
          return '<div style="padding:4px 0;border-top:1px dotted var(--border);" title="'+escapeHtml(x.outcome+' — '+(x.trigger_detail||''))+'">'+
            '<div><span style="color:var(--dim)">'+escapeHtml((x.fired_at||'').substring(0,16).replace('T',' '))+'</span> '+
            '<b>'+escapeHtml(x.rule_name||'unnamed rule')+'</b> <span style="color:var(--dim)">noticed:</span> '+escapeHtml((x.trigger_detail||'its trigger condition').substring(0,110))+'</div>'+
            '<div style="padding-left:14px;color:'+st.tone+';">↳ '+escapeHtml(st.text)+'</div>'+
          '</div>';
        }).join('')
      : '';
  }catch(e){ body.innerHTML='<span style="color:var(--red)">Error: '+escapeHtml(e.message||'')+'</span>'; }
}
// v3.8.34: both of these were dead, and silently.
//
// `api(path, method='GET', body=null)` takes the method POSITIONALLY. These two passed
// `{method:'POST'}` as that second argument — the only two call sites in the console that did — so
// `method` was an object, `fetch` stringified it to "[object Object]", and the request threw on an
// invalid method token before it ever left the browser. `api` catches that and RETURNS
// `{success:false}` rather than throwing, so the `catch(e){}` never even ran; the result was simply
// discarded and `hlLoadAutomation()` re-rendered the unchanged state.
//
// The operator's experience: the toggle flips, snaps back on the reload, and nothing says why.
// Two defects stacked — a wrong call shape, and a swallowed result that hid it — which is why a
// rule that no endpoint ever received still looked like a rule that refused to change.
async function hlAutoToggle(id,on){
  const r=await window.api('/homelab/automation/rules/'+encodeURIComponent(id)+'/'+(on?'enable':'disable'),'POST');
  hlMsg(r&&r.success ? ('Rule '+(on?'enabled':'disabled')+'.') : ((r&&r.message)||'Could not change the rule.'), !!(r&&r.success));
  hlLoadAutomation();
}
async function hlAutoEvaluate(){
  const r=await window.api('/homelab/automation/evaluate','POST');
  hlMsg(r&&r.success ? 'Rules evaluated.' : ((r&&r.message)||'Could not evaluate the rules.'), !!(r&&r.success));
  hlLoadAutomation();
}

// -- v2.4.0 Phase 13: backup + restore intelligence ---------------------------------------------
async function hlLoadBackup(){
  const body=document.getElementById('hl-bk-body'); if(!body) return;
  try{
    const r=await api('/homelab/backup/coverage');
    if(!(r&&r.success&&r.data)){ body.innerHTML='<span style="color:var(--dim)">Coverage unavailable.</span>'; return; }
    const d=r.data, entries=d.entries||[], t=d.totals||{};
    const tot=document.getElementById('hl-bk-totals');
    if(tot) tot.textContent=(t.ok||0)+' ok · '+(t.stale||0)+' stale · '+(t.failed||0)+' failed · '+(t.none||0)+' unprotected · stale after '+d.stale_after_days+'d';
    if(!entries.length){ body.innerHTML='<span style="color:var(--dim)">No VMs or containers in inventory yet — sync an integration first.</span>'; return; }
    const cov=c=>c==='ok'?'var(--green)':c==='stale'?'var(--orange)':'var(--red)';
    body.innerHTML='<table style="width:100%;border-collapse:collapse;font-size:10px;">'+
      '<tr style="color:var(--dim);text-align:left;"><th>#</th><th>Target</th><th>Node</th><th>Coverage</th><th>Last success</th><th>Confidence</th><th></th></tr>'+
      entries.map(e=>'<tr style="border-top:1px solid var(--border);">'+
        '<td>'+e.restore_priority+'</td>'+
        '<td><b style="color:var(--text)">'+escapeHtml(e.name||e.target_id)+'</b> <span style="color:var(--dim)">'+escapeHtml(e.target_kind)+' '+escapeHtml(e.target_id)+'</span></td>'+
        '<td>'+escapeHtml(e.node_id||'—')+'</td>'+
        '<td style="color:'+cov(e.coverage)+';font-weight:700;" title="'+escapeHtml(e.detail)+'">'+e.coverage.toUpperCase()+'</td>'+
        '<td>'+escapeHtml((e.last_success||'—').substring(0,16))+'</td>'+
        '<td>'+e.restore_confidence+'/100</td>'+
        '<td><button class="btn btn-ghost" style="font-size:9px;" data-onclick="hlRunbook(\''+attrSafe(e.target_kind)+'\',\''+attrSafe(e.target_id)+'\')">Runbook</button></td>'+
      '</tr>').join('')+'</table>';
  }catch(e){ body.innerHTML='<span style="color:var(--red)">Error loading coverage: '+escapeHtml(e.message||'')+'</span>'; }
}
async function hlRunbook(kind,id){
  const box=document.getElementById('hl-bk-runbook'); if(!box) return;
  box.style.display='block'; box.innerHTML='Generating runbook…';
  try{
    const r=await api('/homelab/backup/runbook/'+encodeURIComponent(kind)+'/'+encodeURIComponent(id));
    const steps=(r&&r.success&&r.data&&r.data.steps)||[];
    box.innerHTML='<div class="section-head" style="margin-top:0;">Restore Runbook — '+escapeHtml(kind)+' '+escapeHtml(id)+
      '<button class="btn btn-ghost" style="float:right;" data-onclick="document.getElementById(\'hl-bk-runbook\').style.display=\'none\'">✕</button></div>'+
      steps.map(s=>'<div style="padding:3px 0;'+(s.includes('STOP')||s.includes('WARNING')?'color:var(--orange);':'')+'">'+escapeHtml(s)+'</div>').join('');
  }catch(e){ box.innerHTML='<span style="color:var(--red)">Runbook error: '+escapeHtml(e.message||'')+'</span>'; }
}

async function loadHomelab(){
  hlSubRestore(); // v2.5.3 R3: apply the operator's saved sub-page before any data lands
  try{
    const [sum,hosts,svcs,deps,chg]=await Promise.all([
      api('/homelab/summary'),api('/homelab/hosts'),api('/homelab/services'),
      api('/homelab/dependencies'),api('/homelab/changes')]);
    HL_HOSTS=(hosts.data||[]); HL_SVCS=(svcs.data||[]); HL_DEPS=(deps.data||[]);
    renderHlSummary(sum.success?(sum.data||{}):{});
    renderHlHosts(); renderHlServices(); renderHlPorts(); renderHlDeps();
    renderHlChanges(chg.success?(chg.data||[]):[]);
    loadHlHealth();
    loadHlVirt();
    loadHlRisks();
    loadHlIncidents();
    loadHlActions();    // v2.3.0: approval-gated action proposals + kill-switch state
    hlLoadBackup();     // v2.4.0: backup + restore intelligence (Phase 13)
    hlLoadAutomation(); // v2.5.0: automation rules (Phase 14)
    renderHlDeck();     // v2.3.2/3: the Service Deck — hosts/VMs/CTs/services as live tiles + metrics
    renderHl3Apps();    // v2.4.1: *arr-stack apps (Homarr-style)
    renderHl3Widgets(); // v2.5.2: Console Refit R2 — the widget zone (runtime + persisted layout)
    renderHlTargets();  // v2.5.4: Console Refit R4 — target allow/blocklist collection manager
    hl3LoaderFallbacks(); // v2.3.2: no infinite "Loading..." — labeled fallback after 7s
    loadHlDashboard();  // V2.0: command summary, next checks, dependency graph
    hlDelegateRows();   // V2.0: host/service row click → entity detail drawer
    decorateHlSections(); // V2.0 pass 2: subsystem theming + connection cues
    const c=document.getElementById('hl-count');
    if(c) c.textContent=HL_HOSTS.length+' host(s) · '+HL_SVCS.length+' service(s) · '+HL_DEPS.length+' dependency(ies)';
  }catch(e){ hlMsg('Load failed: '+e.message,false); }
}

function hlNodeName(id){ const n=HL_HOSTS.find(x=>x.id===id); return n?n.name:(id?id.slice(0,8)+'…':'—'); }
function hlRefName(kind,id){
  if(kind==='service'){ const s=HL_SVCS.find(x=>x.id===id); if(s) return s.name; }
  if(kind==='host'){ return hlNodeName(id); }
  const any=HL_SVCS.find(x=>x.id===id)||HL_HOSTS.find(x=>x.id===id);
  return any?any.name:(id?id.slice(0,8)+'…':'—');
}
function hlDate(s){ return s?String(s).slice(0,16).replace('T',' '):'—'; }

function renderHlSummary(d){
  const el=document.getElementById('hl-summary'); if(!el) return;
  const provs=(d.providers||[]).map(p=>escapeHtml(p.name)+': '+escapeHtml(p.state||'idle')).join(' · ')||'none registered';
  el.innerHTML=
    'Subsystem: <b>'+(d.enabled?'enabled':'disabled')+'</b> · Scheduler: <b>'+(d.scheduler_enabled?'enabled':'disabled')+
    (d.scheduler_running?' (running)':'')+'</b> · Allowlist entries: <b>'+(d.allowlist_entries??0)+'</b>'+
    '<br>Providers: '+provs+
    '<br><span style="color:var(--dim)">Read-only foundation - actions arrive approval-gated in V2.1. Toggles live in config (homelab_enabled, homelab_scheduler_enabled, homelab_mock_providers_enabled).</span>';
}

function renderHlHosts(){
  const tb=document.getElementById('hl-hosts-tbody'); if(!tb) return;
  if(!HL_HOSTS.length){ tb.innerHTML='<tr><td colspan="7" style="color:var(--dim);text-align:center;padding:16px;">No hosts registered yet.</td></tr>'; return; }
  tb.innerHTML=HL_HOSTS.map(n=>{
    const svcCount=HL_SVCS.filter(s=>s.node_id===n.id).length;
    return '<tr><td>'+escapeHtml(n.name)+'</td><td>'+escapeHtml(n.kind||'')+'</td><td>'+escapeHtml(n.address||'—')+'</td><td>'+escapeHtml(n.os||'—')+
      '</td><td>'+escapeHtml((n.role_tags||[]).join(', ')||'—')+'</td><td>'+svcCount+'</td><td>'+hlDate(n.updated_at)+'</td></tr>';
  }).join('');
  // Keep the service + dependency selectors in sync with registered hosts/services.
  const sel=document.getElementById('hl-s-node');
  if(sel) sel.innerHTML='<option value="">— unassigned —</option>'+HL_HOSTS.map(n=>'<option value="'+escapeHtml(n.id)+'">'+escapeHtml(n.name)+'</option>').join('');
  const from=document.getElementById('hl-d-from'), to=document.getElementById('hl-d-to');
  if(from) from.innerHTML=HL_SVCS.map(s=>'<option value="service:'+escapeHtml(s.id)+'">service: '+escapeHtml(s.name)+'</option>').join('')
    +HL_HOSTS.map(n=>'<option value="host:'+escapeHtml(n.id)+'">host: '+escapeHtml(n.name)+'</option>').join('');
  if(to) to.innerHTML=HL_HOSTS.map(n=>'<option value="host:'+escapeHtml(n.id)+'">host: '+escapeHtml(n.name)+'</option>').join('')
    +HL_SVCS.map(s=>'<option value="service:'+escapeHtml(s.id)+'">service: '+escapeHtml(s.name)+'</option>').join('');
}

function renderHlServices(){
  const tb=document.getElementById('hl-svcs-tbody'); if(!tb) return;
  if(!HL_SVCS.length){ tb.innerHTML='<tr><td colspan="8" style="color:var(--dim);text-align:center;padding:16px;">No services registered yet.</td></tr>'; return; }
  tb.innerHTML=HL_SVCS.map(s=>'<tr><td>'+escapeHtml(s.name)+'</td><td>'+escapeHtml(hlNodeName(s.node_id))+'</td><td>'+escapeHtml(s.url||'—')+
    '</td><td>'+escapeHtml((s.ports||[]).join(', ')||'—')+'</td><td>'+escapeHtml(s.owner||'—')+'</td><td>'+escapeHtml(s.criticality||'normal')+
    '</td><td>'+(s.internet_exposed?'<span style="color:var(--red)">yes</span>':'no')+'</td><td>'+hlDate(s.updated_at)+'</td></tr>').join('');
}

function renderHlPorts(){
  const tb=document.getElementById('hl-ports-tbody'); if(!tb) return;
  const rows=[];
  HL_SVCS.forEach(s=>(s.ports||[]).forEach(p=>rows.push({port:p,svc:s})));
  rows.sort((a,b)=>a.port-b.port);
  tb.innerHTML=rows.length?rows.map(r=>'<tr><td>'+r.port+'</td><td>'+escapeHtml(r.svc.name)+'</td><td>'+escapeHtml(hlNodeName(r.svc.node_id))+
    '</td><td>'+escapeHtml(r.svc.protocol||'—')+'</td><td>'+(r.svc.internet_exposed?'<span style="color:var(--red)">yes</span>':'no')+'</td></tr>').join('')
    :'<tr><td colspan="5" style="color:var(--dim);text-align:center;padding:16px;">No ports (register services with ports).</td></tr>';
}

function renderHlDeps(){
  const tb=document.getElementById('hl-deps-tbody'); if(!tb) return;
  const admin=ROLE==='admin';
  if(!HL_DEPS.length){ tb.innerHTML='<tr><td colspan="5" style="color:var(--dim);text-align:center;padding:16px;">No dependencies mapped yet.</td></tr>'; return; }
  tb.innerHTML=HL_DEPS.map(d=>'<tr><td>'+escapeHtml(d.from_kind)+': '+escapeHtml(hlRefName(d.from_kind,d.from_id))+'</td><td>'+escapeHtml(d.dependency_kind)+
    '</td><td>'+escapeHtml(d.to_kind)+': '+escapeHtml(hlRefName(d.to_kind,d.to_id))+'</td><td>'+escapeHtml(d.notes||'—')+
    '</td><td>'+(admin?('<button class="btn btn-ghost" data-onclick="hlDelDep(\''+jsArg(d.id)+'\')">✕</button>'):'—')+'</td></tr>').join('');
}

function renderHlChanges(list){
  const el=document.getElementById('hl-changes'); if(!el) return;
  el.innerHTML=list.length?list.map(c=>'<div style="padding:2px 0;">'+hlDate(c.created_at)+' — <b>'+escapeHtml(c.change_kind)+'</b> '+escapeHtml(c.subject_kind)+
    ' · '+escapeHtml(c.summary||'')+(c.changed_by?(' <span style="color:var(--dim)">by '+escapeHtml(c.changed_by)+'</span>'):'')+'</div>').join('')
    :'No changes recorded yet.';
}

// -- Virtualization: Proxmox read-only (v1.12.0) ----------------------------------
function hlBytes(n){ if(!n) return '0'; const u=['B','KB','MB','GB','TB','PB']; let i=0; let v=n;
  while(v>=1024&&i<u.length-1){ v/=1024; i++; } return v.toFixed(v>=100?0:1)+' '+u[i]; }
function hlUptime(s){ if(!s) return '—'; const d=Math.floor(s/86400), h=Math.floor(s%86400/3600);
  return d>0?(d+'d '+h+'h'):(h+'h '+Math.floor(s%3600/60)+'m'); }

// v2.1.0: unified read-only virtualization connections (Proxmox / ESXi / Docker / Hyper-V).
const HL_VIRT_META={
  proxmox:{title:'Proxmox VE',   defPort:8006,secretHint:'user@realm!tokenid=SECRET (PVEAuditor role)'},
  esxi:   {title:'VMware ESXi / vCenter',defPort:443, secretHint:'user:password (built-in Read-only role)'},
  docker: {title:'Docker Engine',defPort:2376,secretHint:'optional bearer — leave blank for none'},
  hyperv: {title:'Microsoft Hyper-V',defPort:5986,secretHint:'DOMAIN\\user:password (read-only account)'},
};
async function loadHlVirt(){
  try{
    const [st,vms,cts,pools]=await Promise.all([
      api('/homelab/virtualization/status'),api('/homelab/vms'),api('/homelab/containers'),api('/homelab/storage')]);
    renderHlVirtConns(st.success?(st.data||{}):{});
    renderHlVms(vms.success?(vms.data||[]):[]);
    renderHlCts(cts.success?(cts.data||[]):[]);
    renderHlStorage(pools.success?(pools.data||[]):[]);
  }catch(e){ hlMsg('Virtualization load failed: '+e.message,false); }
}

function renderHlVirtConns(data){
  const wrap=document.getElementById('hl-virt-conns'); if(!wrap) return;
  const admin=ROLE==='admin';
  const list=(data&&data.integrations)||[];
  // Master-gate bar. Read-only inventory still syncs manually with the subsystem off, but scheduled
  // sync + the health/risk/incident jobs need it enabled (and a restart to (de)register the jobs).
  let bar='<div style="grid-column:1/-1;display:flex;align-items:center;gap:10px;flex-wrap:wrap;padding:8px 10px;border:1px solid var(--border);border-radius:6px;font-size:11px;">'+
    '<span>Infrastructure subsystem: <b style="color:'+(data.homelab_enabled?'var(--green)':'var(--red)')+'">'+(data.homelab_enabled?'enabled':'disabled')+'</b></span>'+
    '<span>· Scheduler: <b style="color:'+(data.scheduler_enabled?'var(--green)':'var(--dim)')+'">'+(data.scheduler_enabled?'enabled':'disabled')+'</b></span>'+
    (admin?('<span style="flex:1"></span>'+
      '<button class="btn btn-ghost" data-onclick="hlSetHomelabGate(\'homelab_enabled\','+(!data.homelab_enabled)+')">'+(data.homelab_enabled?'Disable subsystem':'Enable subsystem')+'</button>'+
      '<button class="btn btn-ghost" data-onclick="hlSetHomelabGate(\'homelab_scheduler_enabled\','+(!data.scheduler_enabled)+')">'+(data.scheduler_enabled?'Disable scheduler':'Enable scheduler')+'</button>'+
      '<span style="color:var(--dim)">(restart to (de)register scheduled syncs)</span>'):'')+'</div>';
  if(!list.length){ wrap.innerHTML=bar+'<div style="color:var(--dim);font-size:11px;">No integrations available.</div>'; return; }
  wrap.innerHTML=bar+list.map(d=>{
    const k=d.kind, m=HL_VIRT_META[k]||{title:k,defPort:0,secretHint:'secret'};
    const dis=admin?'':' disabled';
    return '<div class="card" style="padding:10px 12px;border:1px solid var(--border);">'+
      '<div style="display:flex;justify-content:space-between;align-items:center;">'+
        '<b>'+escapeHtml(m.title)+'</b>'+
        '<span class="hud-risk low" title="No write methods exist in the client — read-only by construction">read-only</span></div>'+
      '<label style="display:flex;align-items:center;gap:6px;font-size:11px;margin:7px 0;">'+
        '<input type="checkbox" id="hl-vc-'+k+'-enabled" aria-label="'+escapeHtml(m.title)+' connection enabled"'+(d.enabled?' checked':'')+dis+'> Enabled'+
        '<span style="flex:1"></span>'+
        '<input type="checkbox" id="hl-vc-'+k+'-insecure" aria-label="'+escapeHtml(m.title)+': skip TLS verification"'+(d.insecure_tls?' checked':'')+dis+'> Skip TLS verify</label>'+
      '<div style="display:flex;gap:6px;margin-bottom:6px;">'+
        '<input class="form-input" id="hl-vc-'+k+'-host" value="'+escapeHtml(d.host||'')+'" placeholder="host / ip" style="flex:2;font-size:11px;" autocomplete="off"'+dis+'>'+
        '<input class="form-input" id="hl-vc-'+k+'-port" value="'+(d.port||m.defPort||'')+'" type="number" placeholder="port" style="width:66px;font-size:11px;"'+dis+'></div>'+
      '<input class="form-input" id="hl-vc-'+k+'-credid" value="'+escapeHtml(d.credential_id||'')+'" placeholder="credential id" style="width:100%;font-size:11px;margin-bottom:6px;" autocomplete="off"'+dis+'>'+
      (admin?('<div style="display:flex;gap:6px;margin-bottom:6px;">'+
        '<input class="form-input" id="hl-vc-'+k+'-secret" type="password" placeholder="'+escapeHtml(m.secretHint)+'" style="flex:1;font-size:11px;" autocomplete="new-password">'+
        '<button class="btn btn-ghost" data-onclick="hlSaveVirtCred(\''+k+'\')" title="Store the secret in the write-only credential vault">Save cred</button></div>'+
        '<div style="display:flex;gap:6px;">'+
        '<button class="btn btn-primary" data-onclick="hlSaveVirtConn(\''+k+'\')">Save</button>'+
        '<button class="btn btn-ghost" data-onclick="hlSyncVirt(\''+k+'\')">⟳ Sync now</button></div>'):'')+
      '<div style="font-size:10px;color:var(--muted);margin-top:6px;">credential <b>'+escapeHtml(d.credential_id||'—')+'</b>: '+
        (d.credential_configured?'<span style="color:var(--green)">configured</span>':'<span style="color:var(--red)">missing</span>')+
        ' · '+(d.active?'<span style="color:var(--green)">active</span>':'inactive')+
        (d.host?(' · host: '+(d.host_allowlisted
          ?'<span style="color:var(--green)">allowlisted</span>'
          :'<span style="color:var(--red)">NOT allowlisted — requests are blocked</span>'+(admin?' <button class="btn btn-ghost" style="padding:1px 6px;font-size:9px;" data-onclick="hlAllowVirtHost(\''+k+'\')">Allow this host</button>':''))):'')+
        '</div></div>';
  }).join('');
}

async function hlAllowVirtHost(k){
  const h=document.getElementById('hl-vc-'+k+'-host');
  const host=h?h.value.trim():'';
  if(!host){ hlMsg('Set the host first.',false); return; }
  const r=await api('/homelab/allowlist','POST',{target:host,note:k+' virtualization'});
  hlMsg(r.success?('Host '+host+' allowlisted — now Sync now to pull inventory.'):(r.message||'Failed'),r.success);
  if(r.success) loadHlVirt();
}

async function hlSetHomelabGate(key,val){
  const payload={}; payload[key]=val;
  const r=await api('/settings','POST',payload);
  hlMsg(r.success?(key+' → '+val+'. Restart the service so scheduled syncs (de)register.'):(r.message||'Failed'),r.success);
  if(r.success) loadHlVirt();
}

async function hlSaveVirtConn(k){
  const g=id=>document.getElementById('hl-vc-'+k+'-'+id);
  if(!g('host')) return;
  const payload={};
  payload['homelab_'+k+'_enabled']=g('enabled').checked;
  payload['homelab_'+k+'_host']=g('host').value.trim();
  const port=parseInt(g('port').value,10); if(port>0) payload['homelab_'+k+'_port']=port;
  payload['homelab_'+k+'_credential_id']=g('credid').value.trim();
  payload['homelab_'+k+'_insecure_tls']=g('insecure').checked;
  const r=await api('/settings','POST',payload);
  hlMsg(r.success?(k+' connection saved — Sync now to pull inventory (scheduled sync starts after next restart).'):(r.message||'Save failed'),r.success);
  if(r.success) loadHlVirt();
}

async function hlSaveVirtCred(k){
  const g=id=>document.getElementById('hl-vc-'+k+'-'+id);
  const secret=(g('secret')?g('secret').value:'').trim();
  const id=(g('credid')?g('credid').value:'').trim();
  if(!id){ hlMsg('Set a credential id first.',false); return; }
  if(!secret){ hlMsg('Enter the secret to store.',false); return; }
  const r=await api('/homelab/credentials','POST',{id:id,kind:k,target_host:(g('host')?g('host').value.trim():''),secret:secret});
  hlMsg(r.success?('Credential "'+id+'" stored (write-only).'):(r.message||'Save failed'),r.success);
  if(g('secret')) g('secret').value='';
  if(r.success) loadHlVirt();
}

async function hlSyncVirt(k){
  hlMsg('Syncing '+k+'...',true);
  const r=await api('/homelab/virtualization/'+encodeURIComponent(k)+'/sync','POST',{});
  if(!r.success){ hlMsg(r.message||'Failed',false); return; }
  loadHomelab(); // full refresh: node graph + inventory tables (loadHomelab calls loadHlVirt)
  // Diagnostic nudge for the #1 read-only Proxmox gotcha: a privilege-separated (privsep=1)
  // API token's effective permissions are the INTERSECTION of the backing user's perms and the
  // token's ACL. Proxmox then returns HTTP 200 + EMPTY lists (never 403) for resources the token
  // can't audit — so a sync "succeeds" and finds the nodes, yet pulls 0 VMs/containers/storage.
  // Surface the cause here instead of a silent "ok" over empty tables.
  const items=(r.data&&r.data.items)||0;
  try{
    const st=await api('/homelab/virtualization/status');
    const d=st.success?(st.data||{}):{};
    const emptyInventory=((d.vms||0)+(d.containers||0)+(d.storage_pools||0))===0;
    if(k==='proxmox'&&items>0&&emptyInventory){
      hlMsg(r.message+' — nodes only, no VMs/containers/storage. If this is a privilege-separated API token, grant the backing USER the PVEAuditor role too (effective perms = user ∩ token).',false);
      return;
    }
  }catch(e){}
  hlMsg(r.message||'Synced',true);
}

function renderHlVms(vms){
  const tb=document.getElementById('hl-vms-tbody'); if(!tb) return;
  if(!vms.length){ tb.innerHTML='<tr><td colspan="8" style="color:var(--dim);text-align:center;padding:16px;">No VMs synced yet.</td></tr>'; return; }
  tb.innerHTML=vms.map(v=>'<tr><td>'+escapeHtml(v.vm_id||'—')+'</td><td>'+escapeHtml(v.name)+'</td><td>'+escapeHtml(hlNodeName(v.node_id))+
    '</td><td style="color:'+(v.status==='running'?'var(--green)':'var(--dim)')+'">'+escapeHtml(v.status||'—')+'</td>'+
    '<td>'+(v.cpu_cores||0)+'</td><td>'+hlBytes((v.memory_mb||0)*1048576)+'</td><td>'+hlUptime(v.uptime_seconds)+'</td><td>'+hlDate(v.updated_at)+'</td></tr>').join('');
}

function renderHlCts(cts){
  const tb=document.getElementById('hl-cts-tbody'); if(!tb) return;
  if(!cts.length){ tb.innerHTML='<tr><td colspan="6" style="color:var(--dim);text-align:center;padding:16px;">No containers synced yet.</td></tr>'; return; }
  tb.innerHTML=cts.map(c=>'<tr><td>'+escapeHtml(c.container_id||'—')+'</td><td>'+escapeHtml(c.name)+'</td><td>'+escapeHtml(c.kind||'lxc')+
    '</td><td>'+escapeHtml(hlNodeName(c.node_id))+'</td><td style="color:'+(c.status==='running'?'var(--green)':'var(--dim)')+'">'+escapeHtml(c.status||'—')+'</td><td>'+hlDate(c.updated_at)+'</td></tr>').join('');
}

function renderHlStorage(pools){
  const tb=document.getElementById('hl-storage-tbody'); if(!tb) return;
  if(!pools.length){ tb.innerHTML='<tr><td colspan="7" style="color:var(--dim);text-align:center;padding:16px;">No storage synced yet.</td></tr>'; return; }
  tb.innerHTML=pools.map(p=>{
    const pct=p.total_bytes>0?Math.round(p.used_bytes*100/p.total_bytes):0;
    const col=pct>=90?'var(--red)':(pct>=75?'var(--yellow, orange)':'var(--green)');
    return '<tr><td>'+escapeHtml(p.name)+'</td><td>'+escapeHtml(hlNodeName(p.node_id))+'</td><td>'+escapeHtml(p.kind||'—')+
      '</td><td>'+hlBytes(p.used_bytes)+'</td><td>'+hlBytes(p.total_bytes)+'</td><td style="color:'+col+'">'+pct+'%</td><td>'+hlDate(p.updated_at)+'</td></tr>';
  }).join('');
}

async function hlPveSync(){
  hlMsg('Syncing Proxmox...',true);
  const r=await api('/homelab/proxmox/sync','POST',{});
  hlMsg(r.message||(r.success?'Synced':'Failed'),r.success);
  if(r.success){ loadHomelab(); }
}

// -- V2.0 Command Center: dashboard, graph, entity detail (Pass 1: functional) -------
let HL_DASH=null, HL_GRAPH_SEL=null;

function hlStatusColor(st){ return st==='healthy'?'var(--green)':(st==='degraded'?'var(--yellow, orange)':(st==='failed'?'var(--red)':'var(--dim)')); }

async function loadHlDashboard(){
  try{
    const r=await api('/homelab/dashboard');
    if(!r.success){ hlMsg(r.message||'Dashboard load failed',false); return; }
    HL_DASH=r.data||{};
    renderHlCmdStrip(HL_DASH);
    renderHlNext(HL_DASH);
    renderHlGraph(HL_DASH);
  }catch(e){ hlMsg('Dashboard load failed: '+e.message,false); }
}

function hlKpi(label,value,color){
  return '<span class="hl-kpi" style="border:1px solid var(--line, #223);border-radius:4px;padding:3px 8px;">'+
    '<b style="color:'+(color||'var(--muted)')+'">'+value+'</b> <span style="color:var(--dim)">'+escapeHtml(label)+'</span></span>';
}

function renderHlCmdStrip(d){
  const el=document.getElementById('hl-cmd-kpis'); if(!el) return;
  const h=d.health||{};
  const parts=[
    hlKpi('hosts', d.hosts??0),
    hlKpi('services', d.services??0),
    hlKpi('healthy', h.healthy??0, 'var(--green)'),
    hlKpi('degraded', h.degraded??0, (h.degraded>0?'var(--yellow, orange)':undefined)),
    hlKpi('failed', h.failed??0, (h.failed>0?'var(--red)':undefined)),
    hlKpi('active incidents', (d.active_incidents||[]).length, ((d.active_incidents||[]).length>0?'var(--red)':undefined)),
    hlKpi('risk errors', d.open_risk_errors??0, (d.open_risk_errors>0?'var(--red)':undefined)),
    hlKpi('risk warnings', d.open_risk_warnings??0),
    hlKpi('VMs/CTs', (d.vms??0)+'/'+(d.containers??0)),
  ];
  if((d.storage_total_bytes??0)>0)
    parts.push(hlKpi('storage', hlBytes(d.storage_used_bytes)+' / '+hlBytes(d.storage_total_bytes)+(d.backup_capable_pools>0?(' · '+d.backup_capable_pools+' backup pool(s)'):' · no backup pools')));
  else parts.push(hlKpi('storage', 'no data yet'));
  if((d.pending_approvals??-1)>=0) parts.push(hlKpi('pending approvals', d.pending_approvals, (d.pending_approvals>0?'var(--yellow, orange)':undefined)));
  el.innerHTML=parts.join('');
  const st=document.getElementById('hl-cmd-stamps');
  if(st){
    // Colony-link dot: derived strictly from real job stamps — green+pulse if any scheduler job
    // ran in the last 15 minutes, amber if ever, gray if never. Never fabricated.
    const stamps=[d.last_health_run,d.last_proxmox_sync,d.last_risk_analysis].filter(x=>x);
    const fresh=stamps.some(x=>{ const t=Date.parse(x); return !isNaN(t)&&(Date.now()-t)<15*60*1000; });
    const dot='<span class="hl-live-dot'+(fresh?' hl-pulse':'')+'" style="background:'+(fresh?'var(--hl-health)':(stamps.length?'var(--hl-memory)':'var(--dim)'))+'" title="'+(fresh?'colony link: scheduler active in the last 15m':(stamps.length?'colony link: idle (no recent job runs)':'colony link: no jobs have run yet'))+'"></span>';
    st.innerHTML=dot+
      'last health run: '+(d.last_health_run?hlDate(d.last_health_run):'no data yet')+
      ' · last proxmox sync: '+(d.last_proxmox_sync?hlDate(d.last_proxmox_sync):'not configured')+
      ' · last risk analysis: '+(d.last_risk_analysis?hlDate(d.last_risk_analysis):'no data yet')+
      ' · generated '+hlDate(d.generated_at);
  }
}

function renderHlNext(d){
  const el=document.getElementById('hl-next'); if(!el) return;
  const items=d.next_checks||[];
  el.innerHTML=items.length?('<ol style="margin:0 0 0 16px;padding:0;">'+items.map(n=>'<li style="padding:2px 0;">'+escapeHtml(n)+'</li>').join('')+'</ol>')
    :'<span style="color:var(--green)">Nothing urgent — no failing checks, error incidents, or error findings right now.</span>';
}

function renderHlGraph(d){
  const svg=document.getElementById('hl-graph'); if(!svg) return;
  const nodes=d.graph_nodes||[], edges=d.graph_edges||[];
  if(!nodes.length){
    svg.innerHTML='<text x="20" y="40" fill="var(--dim)" font-size="11">No graph yet — register hosts and services (with "runs on") to map dependencies.</text>';
    const info=document.getElementById('hl-graph-info'); if(info) info.textContent='';
    return;
  }
  const hosts=nodes.filter(n=>n.kind==='host'), svcs=nodes.filter(n=>n.kind!=='host');
  const W=Math.max(600,(Math.max(hosts.length,svcs.length))*130+60);
  svg.setAttribute('viewBox','0 0 '+W+' 230'); svg.style.minWidth=W+'px';
  const pos={};
  svcs.forEach((n,i)=>pos[n.id]={x:60+i*130+((svcs.length<hosts.length)?65:0), y:55});
  hosts.forEach((n,i)=>pos[n.id]={x:60+i*130+((hosts.length<svcs.length)?65:0), y:175});
  const sel=HL_GRAPH_SEL;
  const connected=new Set();
  if(sel){ connected.add(sel); edges.forEach(e=>{ if(e.from===sel||e.to===sel){ connected.add(e.from); connected.add(e.to); } }); }
  let out='';
  edges.forEach(e=>{
    const a=pos[e.from], b=pos[e.to]; if(!a||!b) return;
    const hot=sel&&(e.from===sel||e.to===sel);
    out+='<line class="hl-edge'+(e.impacted?' hl-edge-bad':'')+(hot?' hl-edge-hot':'')+'" x1="'+a.x+'" y1="'+(a.y+14)+'" x2="'+b.x+'" y2="'+(b.y-16)+'"'+
      ' stroke="'+(e.impacted?'var(--red)':(hot?'var(--acc, #6cf)':'var(--line, #334)'))+'" stroke-width="'+(hot||e.impacted?2:1)+'"'+(e.impacted?' stroke-dasharray="4 3"':'')+'/>';
  });
  nodes.forEach(n=>{
    const p=pos[n.id]; if(!p) return;
    const dim=sel&&!connected.has(n.id);
    const sc=hlStatusColor(n.status), isHost=n.kind==='host';
    const kindFill=isHost?'var(--hl-compute,#56b6f5)':'var(--hl-service,#5fd7a7)';
    const pulse=(n.status==='failed'||n.open_incident)?' class="hl-pulse"':'';
    // Host = square box; Service = rounded pill. Fill by KIND, border by STATUS — so shape+colour make
    // "is this a host or a service?" obvious at a glance, while the ring still shows health.
    const shape=isHost
      ? '<rect x="'+(p.x-17)+'" y="'+(p.y-13)+'" width="34" height="26" rx="4" fill="'+kindFill+'" fill-opacity="0.18" stroke="'+sc+'" stroke-width="2"'+pulse+'/>'
      : '<rect x="'+(p.x-16)+'" y="'+(p.y-9)+'" width="32" height="18" rx="9" fill="'+kindFill+'" fill-opacity="0.22" stroke="'+sc+'" stroke-width="2"'+pulse+'/>';
    const glyph='<text x="'+p.x+'" y="'+(p.y+4)+'" font-size="11" fill="'+kindFill+'" text-anchor="middle" style="font-weight:700;">'+(isHost?'▣':'●')+'</text>';
    out+='<g class="hl-node" style="cursor:pointer;'+(dim?'opacity:.3;':'')+'" data-onclick="hlGraphSelect(\''+jsArg(n.id)+'\')">'+
      shape+glyph+
      (n.internet_exposed?'<text x="'+(p.x+19)+'" y="'+(p.y-14)+'" font-size="9" fill="var(--red)">◉ exposed</text>':'')+
      (n.open_incident?'<text x="'+(p.x-17)+'" y="'+(p.y-18)+'" font-size="9" fill="var(--red)">! incident</text>':'')+
      '<text x="'+p.x+'" y="'+(p.y+(isHost?33:-16))+'" font-size="10" fill="var(--muted)" text-anchor="middle">'+escapeHtml(n.label)+'</text></g>';
  });
  svg.innerHTML=out;
  const info=document.getElementById('hl-graph-info');
  if(info){
    if(sel){
      const me=nodes.find(n=>n.id===sel);
      const dependents=hlDependents(sel,edges).map(id=>{const n=nodes.find(x=>x.id===id);return n?n.label:id;});
      info.innerHTML='<b>'+escapeHtml(me?me.label:sel)+'</b> — status <span style="color:'+hlStatusColor(me?me.status:'unknown')+'">'+escapeHtml(me?me.status:'?')+'</span>'+
        ' · depends-on-this: '+(dependents.length?escapeHtml(dependents.join(', ')):'nothing recorded')+
        ' · <a href="javascript:void(0)" data-onclick="hlGraphSelect(null)" style="color:var(--dim)">clear selection</a>';
    } else info.innerHTML='<span style="color:var(--hl-compute,#56b6f5);font-weight:700">▣ host</span> &nbsp; <span style="color:var(--hl-service,#5fd7a7);font-weight:700">● service</span> &nbsp; <span style="color:var(--red)">--- impacted path</span> &nbsp;·&nbsp; '+nodes.length+' node(s), '+edges.length+' edge(s), '+edges.filter(e=>e.impacted).length+' impacted.';
  }
}

function hlDependents(id,edges){
  const incoming={}; edges.forEach(e=>{ (incoming[e.to]=incoming[e.to]||[]).push(e.from); });
  const seen=new Set([id]), out=[], q=[id];
  while(q.length){ (incoming[q.shift()]||[]).forEach(p=>{ if(!seen.has(p)){ seen.add(p); out.push(p); q.push(p); } }); }
  return out;
}

function hlGraphSelect(id){ HL_GRAPH_SEL=(HL_GRAPH_SEL===id)?null:id; if(HL_DASH) renderHlGraph(HL_DASH); }

// Entity detail drawer (host/service) — click a name row in the Hosts/Services tables.
function hlEntity(kind,id){
  const box=document.getElementById('hl-entity-detail'); if(!box) return;
  const ent=(kind==='host'?HL_HOSTS:HL_SVCS).find(x=>x.id===id); if(!ent) return;
  box.style.display='';
  const t=document.getElementById('hl-ent-title'); if(t) t.textContent=(kind==='host'?'Host: ':'Service: ')+(ent.name||id);
  const node=(HL_DASH&&HL_DASH.graph_nodes||[]).find(n=>n.id===id);
  const facts=document.getElementById('hl-ent-facts');
  if(facts) facts.innerHTML=(kind==='host'
    ? 'Kind: '+escapeHtml(ent.kind||'host')+'<br>Address: '+escapeHtml(ent.address||'—')+'<br>OS: '+escapeHtml(ent.os||'—')+'<br>Tags: '+escapeHtml((ent.role_tags||[]).join(', ')||'—')
    : 'Runs on: '+escapeHtml(hlNodeName(ent.node_id))+'<br>URL: '+escapeHtml(ent.url||'—')+'<br>Ports: '+escapeHtml((ent.ports||[]).join(', ')||'—')+'<br>Owner: '+escapeHtml(ent.owner||'—')+'<br>Criticality: '+escapeHtml(ent.criticality||'normal')+'<br>Exposed: '+(ent.internet_exposed?'<span style="color:var(--red)">yes</span>':'no'))
    +'<br>Status: <span style="color:'+hlStatusColor(node?node.status:'unknown')+'">'+escapeHtml(node?node.status:'unknown (no checks)')+'</span>';
  const depsEl=document.getElementById('hl-ent-deps');
  if(depsEl){
    const edges=(HL_DASH&&HL_DASH.graph_edges)||[];
    const uses=edges.filter(e=>e.from===id).map(e=>hlRefName('any',e.to)+' ('+e.kind+')');
    const dependents=hlDependents(id,edges).map(x=>hlRefName('any',x));
    depsEl.innerHTML='Uses: '+(uses.length?escapeHtml(uses.join(', ')):'nothing recorded')+
      '<br>Depended on by: '+(dependents.length?escapeHtml(dependents.join(', ')):'nothing recorded');
  }
  const rel=document.getElementById('hl-ent-related');
  if(rel){
    const needle=(ent.name||'')+'|'+(ent.address||ent.url||'')+'|'+id;
    const toks=needle.split('|').filter(x=>x&&x.length>2);
    const match=s=>toks.some(t=>String(s||'').toLowerCase().includes(t.toLowerCase()));
    const incs=(HL_DASH&&HL_DASH.active_incidents||[]).filter(i=>match(i.subject_id)||match(i.title));
    const chgs=(HL_DASH&&HL_DASH.recent_changes||[]).filter(c=>match(c.summary)||c.subject_id===id);
    rel.innerHTML=(incs.length?incs.map(i=>'⚠ <b>'+escapeHtml(i.title)+'</b> ('+escapeHtml(i.status)+')').join('<br>'):'No active incidents')+
      '<br><span style="color:var(--dim)">Recent changes:</span><br>'+
      (chgs.length?chgs.slice(0,5).map(c=>hlDate(c.created_at)+' — '+escapeHtml(c.summary)).join('<br>'):'none recorded');
  }
  box.scrollIntoView({behavior:'smooth',block:'nearest'});
}
function hlCloseEntity(){ const box=document.getElementById('hl-entity-detail'); if(box) box.style.display='none'; }

// Row-click delegation for Hosts/Services tables (additive: no renderer changes needed).
let HL_DELEGATED=false;
function hlDelegateRows(){
  if(HL_DELEGATED) return; HL_DELEGATED=true;
  const wire=(tbodyId,kind,arr)=>{
    const tb=document.getElementById(tbodyId); if(!tb) return;
    tb.addEventListener('click',ev=>{
      if(ev.target.closest('button')) return; // let action buttons win
      const tr=ev.target.closest('tr'); if(!tr||!tr.parentNode) return;
      const idx=Array.prototype.indexOf.call(tr.parentNode.children,tr);
      const list=arr(); if(idx>=0&&idx<list.length) hlEntity(kind,list[idx].id);
    });
  };
  wire('hl-hosts-tbody','host',()=>HL_HOSTS);
  wire('hl-svcs-tbody','service',()=>HL_SVCS);
}

// -- V2.0 Pass 2: identity layer (subsystem theming + connection cues) ----------------
// Tags each card with its subsystem class so the centralized #hl-theme tokens color it.
// Pure decoration: reads the DOM, changes no data or behavior.
let HL_DECORATED=false;
function decorateHlSections(){
  if(HL_DECORATED) return; HL_DECORATED=true;
  const map={
    'hl-checks-tbody':'hl-sec-health','hl-check-form':'hl-sec-health',
    'hl-pve-status':'hl-sec-compute','hl-vms-tbody':'hl-sec-compute','hl-cts-tbody':'hl-sec-compute',
    'hl-storage-tbody':'hl-sec-storage',
    'hl-dev-form':'hl-sec-security','hl-devs-tbody':'hl-sec-security','hl-risks-tbody':'hl-sec-security',
    'hl-inc-tbody':'hl-sec-incident','hl-inc-detail':'hl-sec-incident',
    'hl-changes':'hl-sec-memory','hl-hosts-tbody':'hl-sec-memory','hl-svcs-tbody':'hl-sec-memory',
    'hl-ports-tbody':'hl-sec-memory','hl-deps-tbody':'hl-sec-memory',
    'hl-graph':'hl-sec-compute','hl-summary':'hl-sec-memory','hl-host-form':'hl-sec-memory','hl-svc-form':'hl-sec-memory','hl-dep-form':'hl-sec-memory'
  };
  Object.keys(map).forEach(id=>{
    const el=document.getElementById(id); if(!el) return;
    const card=el.closest('.card'); if(card) card.classList.add(map[id]);
  });
  // Connection cues: clicking a failed check flashes its related incidents; clicking a risk
  // finding flashes the services it points at. Subtle, purposeful, no diagrams.
  const cue=(tbodyId,getNeedle,targetTbodies)=>{
    const tb=document.getElementById(tbodyId); if(!tb) return;
    tb.addEventListener('click',ev=>{
      if(ev.target.closest('button')) return;
      const tr=ev.target.closest('tr'); if(!tr) return;
      const needle=getNeedle(tr); if(!needle) return;
      hlFlashRows(targetTbodies,needle);
    });
  };
  cue('hl-checks-tbody',tr=>(tr.cells[1]?tr.cells[1].textContent:'').trim(),['hl-inc-tbody']);
  cue('hl-risks-tbody',tr=>{
    const m=(tr.cells[2]?tr.cells[2].textContent:'').match(/'([^']+)'/); return m?m[1]:'';
  },['hl-svcs-tbody','hl-hosts-tbody','hl-devs-tbody']);
}

function hlFlashRows(tbodyIds,needle){
  const n=String(needle).toLowerCase(); let hits=0;
  tbodyIds.forEach(id=>{
    const tb=document.getElementById(id); if(!tb) return;
    Array.prototype.forEach.call(tb.rows,row=>{
      if(row.textContent.toLowerCase().includes(n)){
        row.classList.remove('hl-flash'); void row.offsetWidth; // restart animation
        row.classList.add('hl-flash'); hits++;
        if(hits===1) row.scrollIntoView({behavior:'smooth',block:'nearest'});
      }
    });
  });
  if(!hits) hlMsg('No related rows found for "'+needle+'"',true);
}

// -- Incidents + change memory (v1.14.0) --------------------------------------------
let HL_INC_OPEN=null; // id of the incident open in the detail drawer

async function loadHlIncidents(){
  try{
    const r=await api('/homelab/incidents');
    renderHlIncidents(r.success?(r.data||[]):[]);
  }catch(e){ hlMsg('Incident load failed: '+e.message,false); }
}

function renderHlIncidents(list){
  const tb=document.getElementById('hl-inc-tbody'); if(!tb) return;
  const open=list.filter(i=>i.status!=='resolved');
  const kpi=document.getElementById('hl-inc-kpi');
  if(kpi) kpi.textContent='· '+open.length+' active · '+(list.length-open.length)+' resolved';
  if(!list.length){ tb.innerHTML='<tr><td colspan="7" style="color:var(--dim);text-align:center;padding:16px;">No incidents — health-failure streaks open them automatically.</td></tr>'; return; }
  tb.innerHTML=list.slice(0,50).map(i=>{
    const col=i.severity==='error'?'var(--red)':(i.severity==='warning'?'var(--yellow, orange)':'var(--dim)');
    const stCol=i.status==='resolved'?'var(--green)':(i.status==='investigating'?'var(--yellow, orange)':'var(--red)');
    return '<tr><td style="color:'+col+'">'+escapeHtml(i.severity)+'</td><td>'+escapeHtml(i.title)+'</td><td>'+escapeHtml(i.subject_id||'—')+
      '</td><td style="color:'+stCol+'">'+escapeHtml(i.status)+'</td><td>'+hlDate(i.opened_at)+'</td><td>'+escapeHtml(i.root_cause||'—')+
      '</td><td><button class="btn btn-ghost" data-onclick="hlIncDetail(\''+jsArg(i.id)+'\',\''+jsArg((i.title||'').replace(/'/g,''))+'\')">🔎 Detail</button></td></tr>';
  }).join('');
}

async function hlIncDetail(id,title){
  HL_INC_OPEN=id;
  const box=document.getElementById('hl-inc-detail'); if(box) box.style.display='';
  const t=document.getElementById('hl-inc-detail-title'); if(t) t.textContent=title;
  try{
    const [tl,sim]=await Promise.all([
      api('/homelab/incidents/'+encodeURIComponent(id)+'/timeline'),
      api('/homelab/incidents/'+encodeURIComponent(id)+'/similar')]);
    const tlEl=document.getElementById('hl-inc-timeline');
    const entries=tl.success?(tl.data||[]):[];
    if(tlEl) tlEl.innerHTML=entries.length?entries.map(e=>{
      const col=e.severity==='error'?'var(--red)':(e.severity==='warning'?'var(--yellow, orange)':'var(--muted)');
      return '<div style="padding:2px 0;border-left:2px solid '+(e.suspect?'var(--red)':'var(--line, #223)')+';padding-left:8px;margin:2px 0;">'+
        '<span style="color:var(--dim)">'+hlDate(e.at)+'</span> <b style="color:'+col+'">'+escapeHtml(e.kind)+'</b>'+
        (e.suspect?' <span style="color:var(--red);font-weight:700">SUSPECT</span>':'')+' — '+escapeHtml(e.summary)+'</div>';
    }).join(''):'<div style="color:var(--dim)">No correlated activity found.</div>';
    const simEl=document.getElementById('hl-inc-similar');
    const matches=sim.success?(sim.data||[]):[];
    if(simEl) simEl.innerHTML=matches.length?matches.map(m=>
      '<div style="padding:4px 0;border-bottom:1px dotted var(--line, #223);">'+
      '<b>'+escapeHtml(m.incident.title)+'</b> <span style="color:var(--dim)">(score '+m.score+', '+escapeHtml(m.incident.status)+')</span>'+
      (m.fixed_by?('<br><span style="color:var(--green)">Fixed last time by:</span> '+escapeHtml(m.fixed_by)):'')+'</div>').join('')
      :'<div style="color:var(--dim)">No similar incidents in memory yet.</div>';
  }catch(e){ hlMsg('Detail load failed: '+e.message,false); }
}

function hlCloseIncDetail(){ HL_INC_OPEN=null; const box=document.getElementById('hl-inc-detail'); if(box) box.style.display='none'; }

async function hlOpenIncident(){
  const title=document.getElementById('hl-i-title').value.trim();
  if(!title){ hlMsg('Incident title required',false); return; }
  const r=await api('/homelab/incidents','POST',{title, subjectKind:'manual', subjectId:title, severity:'warning'});
  hlMsg(r.message||(r.success?'Opened':'Failed'),r.success);
  if(r.success){ document.getElementById('hl-i-title').value=''; loadHlIncidents(); }
}

async function hlIncStatus(status){
  if(!HL_INC_OPEN){ hlMsg('Open an incident detail first',false); return; }
  const root=document.getElementById('hl-inc-root').value.trim();
  if(status==='resolved'&&!root){ hlMsg('Record the root cause so future incidents can suggest the fix',false); return; }
  const r=await api('/homelab/incidents/'+encodeURIComponent(HL_INC_OPEN)+'/status','POST',{status, rootCause:root});
  hlMsg(r.message||(r.success?'Updated':'Failed'),r.success);
  if(r.success){ document.getElementById('hl-inc-root').value=''; hlCloseIncDetail(); loadHlIncidents(); }
}

// -- Approval-gated actions (v2.3.0, NORTH_STAR Phase 12) ---------------------------
let HL_ACT_STOPPED=false;
async function loadHlActions(){
  try{
    const r=await api('/homelab/actions');
    if(!r.success){ return; }
    const d=r.data||{};
    HL_ACT_STOPPED=!!d.stopped;
    const sel=document.getElementById('hl-a-type');
    if(sel&&!sel.options.length&&Array.isArray(d.allowed_actions))
      sel.innerHTML=d.allowed_actions.map(a=>'<option value="'+escapeHtml(a)+'">'+escapeHtml(a)+'</option>').join('');
    const st=document.getElementById('hl-act-stop-state');
    if(st) st.innerHTML=HL_ACT_STOPPED?athPill('danger','HOMELAB_STOP ENGAGED'):athPill('ok','actions armed');
    const btn=document.getElementById('hl-act-stop-btn');
    if(btn) btn.textContent=HL_ACT_STOPPED?'▶ Resume':'■ STOP';
    renderHlActions(Array.isArray(d.items)?d.items:[]);
  }catch(e){ /* panel shows its last state; load errors surface via hl-msg elsewhere */ }
}
function renderHlActions(items){
  const tb=document.getElementById('hl-act-tbody'); if(!tb) return;
  const kpi=document.getElementById('hl-act-kpi');
  const pending=items.filter(a=>a.state==='pending').length;
  if(kpi) kpi.textContent=items.length?(pending+' pending · '+items.length+' total'):'';
  if(!items.length){ tb.innerHTML='<tr><td colspan="8" style="color:var(--dim);text-align:center;padding:16px;">No action proposals yet — propose one above. Nothing executes without approval.</td></tr>'; return; }
  tb.innerHTML=items.slice(0,25).map(a=>{
    const rc=a.risk_level==='critical'||a.risk_level==='high'?'danger':(a.risk_level==='medium'?'warn':'ok');
    const sc=a.state==='executed'?'ok':(a.state==='rejected'||a.state==='superseded'?'danger':(a.state==='approved'?'warn':'info'));
    let ops='';
    if(a.state==='pending')
      ops='<button class="btn btn-ghost" data-onclick="hlActOp(\''+jsArg(a.approvable_id)+'\',\'approve\')">✓ Approve</button>'+
          '<button class="btn btn-ghost" data-onclick="hlActOp(\''+jsArg(a.approvable_id)+'\',\'reject\')">✕ Reject</button>';
    if(a.state==='pending'||a.state==='approved')
      ops+='<button class="btn btn-ghost" data-onclick="hlActOp(\''+jsArg(a.approvable_id)+'\',\'dryrun\')" title="describe what would happen — never executes">Dry run</button>';
    if(a.state==='approved')
      ops+='<button class="btn btn-primary" data-onclick="hlActExecute(\''+jsArg(a.approvable_id)+'\')"'+(HL_ACT_STOPPED?' disabled title="HOMELAB_STOP is engaged"':'')+'>▶ Execute</button>';
    return '<tr><td>'+athPill(rc,(a.risk_level||'?')+' · '+(a.blast_radius_score??'?'))+'</td>'+
      '<td><b>'+escapeHtml(a.action_type||'')+'</b></td>'+
      '<td title="'+escapeHtml(a.target_kind||'')+'">'+escapeHtml((a.target_id||'').substring(0,24))+'</td>'+
      '<td>'+athPill(sc,a.state||'?')+'</td>'+
      '<td style="font-size:10px;max-width:220px;" title="'+escapeHtml(a.blast_radius_explanation||'')+'">'+escapeHtml((a.blast_radius_explanation||'—').substring(0,60))+'</td>'+
      '<td style="font-size:10px;">'+(a.rollback_note?escapeHtml(a.rollback_note.substring(0,40)):'<span style="color:var(--status-danger)">required</span>')+'</td>'+
      '<td style="font-size:10px;max-width:200px;" title="'+escapeHtml(a.execution_result||'')+'">'+escapeHtml((a.execution_result||'—').substring(0,50))+'</td>'+
      '<td style="display:flex;gap:4px;flex-wrap:wrap;">'+(ops||'—')+'</td></tr>';
  }).join('');
}
async function hlActPropose(){
  const type=document.getElementById('hl-a-type').value;
  const targetId=document.getElementById('hl-a-target').value.trim();
  if(!targetId){ hlMsg('A target id is required',false); return; }
  const r=await api('/homelab/actions/propose','POST',{
    actionType:type, targetKind:document.getElementById('hl-a-tkind').value,
    targetId, rollbackNote:document.getElementById('hl-a-rollback').value.trim()});
  hlMsg(r.message||(r.success?'Proposed':'Refused'),r.success);
  if(r.success){ document.getElementById('hl-a-target').value=''; document.getElementById('hl-a-rollback').value=''; }
  loadHlActions();
}
async function hlActOp(id,verb){
  const r=await api('/homelab/actions/'+encodeURIComponent(id)+'/'+verb,'POST',{});
  if(verb==='dryrun'&&r.success&&r.data&&r.data.dry_run){ await uiConfirm('DRY RUN — nothing was executed:\n\n'+r.data.dry_run); }
  else hlMsg(r.message||(r.success?'Done':'Refused'),r.success);
  loadHlActions();
}
async function hlActExecute(id){
  if(!await uiConfirm('Execute this approved action now? It will be verified and audited.')) return;
  const r=await api('/homelab/actions/'+encodeURIComponent(id)+'/execute','POST',{});
  hlMsg(r.message||(r.success?'Executed':'Refused'),r.success);
  loadHlActions();
}
async function hlActKillSwitch(){
  if(HL_ACT_STOPPED){
    if(!await uiConfirm('Clear HOMELAB_STOP? Approved actions will be executable again.')) return;
    const r=await api('/homelab/actions/resume','POST',{});
    hlMsg(r.message||(r.success?'Resumed':'Refused'),r.success);
  }else{
    const r=await api('/homelab/actions/stop','POST',{reason:'console kill switch'});
    hlMsg(r.message||(r.success?'Stopped':'Refused'),r.success);
  }
  loadHlActions();
}

// -- v2.3.2 Service Deck --------------------------------------------------------------
// (hl3Toggle removed in v2.5.3 R3 — the collapsible sections it drove are now sub-page cards.)
// v2.5.3 R3: the collapsible secondary sections became category sub-pages (see hlSubShow);
// hl3Restore now restores the operator's last sub-page instead of collapse states.
function hl3Restore(){ hlSubRestore(); }

// ---- v2.5.3 Console Refit R3: homelab category sub-pages ---------------------------------------
// Every card on the Homelab page declares exactly ONE home via data-hlsub; the sub-nav filters
// visibility. Cards without data-hlsub (entity detail, incident detail, + Add / Manage drawer)
// are on-demand overlays and stay available from every sub-page. Keyboard: g h opens Homelab,
// then 1-9 / 0 / - switch sub-pages (see the ? shortcuts help).
const HL_SUBPAGES=['overview','services','virtualization','containers','storage','networking','monitoring','automation','apps','alerts','activity'];
function hlSubShow(name,fromRoute){
  if(!HL_SUBPAGES.includes(name)) name='overview';
  try{ localStorage.setItem('hl3.subpage',name); }catch(e){}
  document.querySelectorAll('#page-homelab [data-hlsub]').forEach(c=>{ c.style.display=(c.dataset.hlsub===name)?'':'none'; });
  document.querySelectorAll('#hl-subnav .hl-sub-btn').forEach(b=>b.classList.toggle('active',b.dataset.sub===name));
  // v2.24.0: the shadow panel loads with its sub-page rather than on every homelab open — it joins
  // two tables and nothing else on the page needs it.
  if(name==='automation' && typeof hlLoadShadow==='function') hlLoadShadow();
  // v2.6 Phase 2: when the in-page sub-nav or a keyboard shortcut drives the change, sync the router
  // chrome (breadcrumb, sidebar highlight, URL). fromRoute=true means go()/showPage() already did.
  if(!fromRoute && typeof HLSUB_ROUTE!=='undefined' && document.getElementById('page-homelab')?.classList.contains('active')){
    const r=HLSUB_ROUTE[name];
    if(r && typeof updateChrome==='function'){ updateChrome(r,'homelab'); try{ history.replaceState(null,'','#'+r); }catch(e){} }
  }
}
function hlSubRestore(){
  let saved=null; try{ saved=localStorage.getItem('hl3.subpage'); }catch(e){}
  hlSubShow(saved||'overview');
}
function hl3ToggleConfig(show){
  const el=document.getElementById('hl3-config'); if(!el) return;
  el.style.display=show?'':'none';
  if(show) el.scrollIntoView({behavior:'smooth',block:'start'});
}
function hl3Dot(status){
  const s=String(status||'').toLowerCase();
  const cls=s==='running'||s==='healthy'||s==='ok'||s==='online'?'ok'
    :(s==='stopped'||s==='failed'||s==='error'||s==='offline'?'bad'
    :(s==='degraded'||s==='warning'?'warn':''));
  return '<span class="hl3-dot '+cls+'" title="'+escapeHtml(s||'unknown')+'"></span>';
}
async function renderHlDeck(){
  const deck=document.getElementById('hl3-deck'); if(!deck) return;
  let vms=[],cts=[],health=[],metrics=[];
  try{
    const [v,c,h,m]=await Promise.all([api('/homelab/vms'),api('/homelab/containers'),api('/homelab/health/results'),api('/homelab/metrics/nodes')]);
    vms=Array.isArray(v&&v.data)?v.data:[]; cts=Array.isArray(c&&c.data)?c.data:[];
    health=Array.isArray(h&&h.data)?h.data:[]; metrics=Array.isArray(m&&m.data)?m.data:[];
  }catch(e){ /* deck renders what it has */ }
  HL3.metrics={}; for(const m of metrics) HL3.metrics[m.node_id]=m;
  HL3.vms=vms; HL3.cts=cts;
  const latest={};
  for(const r of health){ const t=String(r.target||''); if(t&&!(t in latest)) latest[t]=String(r.status||''); }
  const healthFor=(needle)=>{
    if(!needle) return '';
    if(latest[needle]!==undefined) return latest[needle];
    const hit=Object.keys(latest).find(t=>t.includes(needle));
    return hit?latest[hit]:'';
  };
  const hidden=hl3Hidden();
  // Group by HOST RECORD ID: registered hosts + provider-synced nodes both live in HL_HOSTS,
  // and vm.node_id / ct.node_id reference homelab_nodes.id (e.g. "pve-node:host:pve1").
  const groups=new Map();
  for(const hst of (HL_HOSTS||[])) groups.set(hst.id,{host:hst,vms:[],cts:[],svcs:[]});
  const misc={host:{id:'',name:'(unassigned)',kind:'',address:''},vms:[],cts:[],svcs:[]};
  const bucket=(nodeId)=>groups.get(nodeId)||misc;
  for(const vm of vms) bucket(vm.node_id).vms.push(vm);
  for(const ct of cts) bucket(ct.node_id).cts.push(ct);
  for(const svc of (HL_SVCS||[])) bucket(svc.node_id).svcs.push(svc);
  if(misc.vms.length||misc.cts.length||misc.svcs.length) groups.set('(unassigned)',misc);
  if(!groups.size){
    deck.innerHTML='<div class="hl3-empty">Nothing registered yet. Use <b>+ Add / Manage</b> to register a host or connect Proxmox/ESXi/Docker/Hyper-V — synced VMs and containers appear here automatically.</div>';
    hl3RenderHiddenTray(0); return;
  }
  let html=''; let hiddenCount=0;
  for(const [gid,g] of groups){
    if(hidden.has('host:'+gid)){ hiddenCount++; continue; }
    const total=g.vms.length+g.cts.length+g.svcs.length;
    const met=HL3.metrics[gid];
    const hostDot=hl3Dot(healthFor(g.host.address)||((g.vms.some(v=>String(v.status).toLowerCase()==='running')||g.cts.some(c=>String(c.status).toLowerCase()==='running'))?'running':''));
    html+='<div class="hl3-host"><div class="hl3-host-hd" data-onclick="'+(g.host.id?('hl3HostPage(\''+jsArg(g.host.id)+'\')'):'void(0)')+'">'
      +hostDot+escapeHtml(g.host.name||'?')
      +' <span style="font-weight:400;color:var(--dim);font-size:9px;">'+escapeHtml(g.host.kind||'')+(g.host.address?' · '+escapeHtml(g.host.address):'')+'</span>'
      +'<span class="sub">'+total+' item(s)'
      +(g.host.id?('<span class="hl3-x" title="Hide this node from the deck (re-add from the hidden tray)" data-onclick="event.stopPropagation();hl3Hide(\'host:'+jsArg(gid)+'\')">✕</span>'):'')
      +'</span></div>';
    if(met) html+=hl3Bars(met);
    html+='<div class="hl3-tiles">';
    for(const vm of g.vms){
      const key='vm:'+vm.id; if(hidden.has(key)){ hiddenCount++; continue; }
      html+='<span class="hl3-tile" title="VM '+escapeHtml(vm.vm_id||'')+' — '+escapeHtml(vm.status||'?')+' (click for detail page)" data-onclick="hl3GuestPage(\'vm\',\''+jsArg(vm.id)+'\')">'+hl3Dot(vm.status)
        +'<span class="k">vm</span>'+escapeHtml(vm.name||vm.vm_id||'?')
        +'<span class="hl3-x" title="Hide" data-onclick="event.stopPropagation();hl3Hide(\''+key+'\')">✕</span></span>';
    }
    for(const ct of g.cts){
      const key='ct:'+ct.id; if(hidden.has(key)){ hiddenCount++; continue; }
      html+='<span class="hl3-tile" title="'+escapeHtml(ct.kind||'ct')+' '+escapeHtml(ct.container_id||'')+' — '+escapeHtml(ct.status||'?')+' (click for detail page)" data-onclick="hl3GuestPage(\'ct\',\''+jsArg(ct.id)+'\')">'+hl3Dot(ct.status)
        +'<span class="k">'+escapeHtml(ct.kind||'ct')+'</span>'+escapeHtml(ct.name||ct.container_id||'?')
        +'<span class="hl3-x" title="Hide" data-onclick="event.stopPropagation();hl3Hide(\''+key+'\')">✕</span></span>';
    }
    for(const svc of g.svcs){
      const key='svc:'+svc.id; if(hidden.has(key)){ hiddenCount++; continue; }
      const st=healthFor(svc.url)||healthFor((svc.ports&&svc.ports.length&&g.host.address)?(g.host.address+':'+svc.ports[0]):'');
      const open=svc.url?('window.open(\''+escapeHtml(svc.url)+'\',\'_blank\')'):('hlEntity(\'service\',\''+escapeHtml(svc.id)+'\')');
      html+='<span class="hl3-tile" title="'+escapeHtml(svc.name)+(svc.url?' — '+escapeHtml(svc.url):'')+' (click to open)" data-onclick="'+open+'">'
        +hl3Dot(st||(svc.criticality==='critical'?'warning':''))
        +'<span class="k">svc</span>'+escapeHtml(svc.name||'?')
        +(svc.internet_exposed?'<span title="internet exposed" style="color:var(--status-warning)">🌐</span>':'')
        +'<span class="zap" title="Propose service restart (approval-gated)" data-onclick="event.stopPropagation();hl3DeckPropose(\'restart_service\',\'service\',\''+jsArg(svc.id)+'\')">⚡</span>'
        +'<span class="hl3-x" title="Hide" data-onclick="event.stopPropagation();hl3Hide(\''+key+'\')">✕</span></span>';
    }
    if(!total) html+='<span class="hl3-empty" style="padding:4px;">no services or guests yet</span>';
    html+='</div></div>';
  }
  deck.innerHTML=html||'<div class="hl3-empty">Everything is hidden — restore items from the tray below.</div>';
  hl3RenderHiddenTray(hiddenCount);
  const gc=document.getElementById('hl-graph-card');
  if(gc) gc.style.display=((HL_DEPS||[]).length||(HL_SVCS||[]).some(s=>s.node_id))?'':'none';
}
const HL3={metrics:{},vms:[],cts:[],apps:[]};
function hl3Bars(m){
  const pct=(u,t)=>u>=0&&t>0?Math.min(100,Math.round(u/t*100)):-1;
  const cls=(p)=>p>=90?'bad':(p>=75?'warn':'');
  const gb=(b)=>b<0?'—':(b/1073741824).toFixed(1)+' GB';
  let html='<div class="hl3-bars">';
  if(m.cpu_percent>=0){ const p=Math.round(m.cpu_percent);
    html+='<div class="hl3-bar"><b>CPU</b><span class="tr"><span class="fl '+cls(p)+'" style="width:'+p+'%"></span></span><span class="v">'+p+'% of '+(m.cpu_cores||'?')+'c</span></div>'; }
  const mp=pct(m.mem_used_bytes,m.mem_total_bytes);
  if(mp>=0) html+='<div class="hl3-bar"><b>RAM</b><span class="tr"><span class="fl '+cls(mp)+'" style="width:'+mp+'%"></span></span><span class="v">'+gb(m.mem_used_bytes)+' / '+gb(m.mem_total_bytes)+'</span></div>';
  const dp=pct(m.disk_used_bytes,m.disk_total_bytes);
  if(dp>=0) html+='<div class="hl3-bar"><b>DISK</b><span class="tr"><span class="fl '+cls(dp)+'" style="width:'+dp+'%"></span></span><span class="v">'+gb(m.disk_used_bytes)+' / '+gb(m.disk_total_bytes)+'</span></div>';
  return html+'</div>';
}
function hl3Hidden(){
  try{ return new Set(JSON.parse(localStorage.getItem('hl3.hiddenTiles')||'[]')); }catch(e){ return new Set(); }
}
function hl3SaveHidden(set){ try{ localStorage.setItem('hl3.hiddenTiles',JSON.stringify([...set])); }catch(e){} }
function hl3Hide(key){ const h=hl3Hidden(); h.add(key); hl3SaveHidden(h); renderHlDeck(); }
function hl3Unhide(key){ const h=hl3Hidden(); h.delete(key); hl3SaveHidden(h); renderHlDeck(); }
function hl3RenderHiddenTray(count){
  const tray=document.getElementById('hl3-hidden-tray'); if(!tray) return;
  const h=[...hl3Hidden()];
  if(!h.length){ tray.innerHTML=''; return; }
  const label=(k)=>{
    const [kind,id]=[k.slice(0,k.indexOf(':')),k.slice(k.indexOf(':')+1)];
    if(kind==='host'){ const x=(HL_HOSTS||[]).find(o=>o.id===id); return 'node '+(x?x.name:id.slice(0,14)); }
    if(kind==='vm'){ const x=(HL3.vms||[]).find(o=>o.id===id); return 'vm '+(x?x.name||x.vm_id:id.slice(0,14)); }
    if(kind==='ct'){ const x=(HL3.cts||[]).find(o=>o.id===id); return 'ct '+(x?x.name||x.container_id:id.slice(0,14)); }
    const x=(HL_SVCS||[]).find(o=>o.id===id); return 'svc '+(x?x.name:id.slice(0,14));
  };
  tray.innerHTML='Hidden ('+h.length+'): '+h.map(k=>'<span class="hl3-tile" style="display:inline-flex;" title="Click to restore" data-onclick="hl3Unhide(\''+jsArg(k)+'\')">'+escapeHtml(label(k))+' ↩</span>').join(' ')
    +' <span style="cursor:pointer;text-decoration:underline;" data-onclick="hl3SaveHidden(new Set());renderHlDeck()">restore all</span>';
}
// ---- v2.3.3 sub-pages: nothing nested — nested content opens a full page with ✕ Close on top --
let HL3_PAGE_HOME=null;
function hl3PageOpen(title,node){
  const pg=document.getElementById('hl3-page'); if(!pg) return;
  document.getElementById('hl3-page-title').textContent=title;
  const body=document.getElementById('hl3-page-body');
  body.innerHTML='';
  if(typeof node==='string'){ HL3_PAGE_HOME=null; body.innerHTML=node; }
  else{ HL3_PAGE_HOME={node,parent:node.parentNode}; body.appendChild(node); node.style.display=''; }
  pg.style.display='block'; pg.scrollTop=0;
}
function hl3PageClose(){
  const pg=document.getElementById('hl3-page'); if(!pg) return;
  if(HL3_PAGE_HOME){ HL3_PAGE_HOME.node.style.display='none'; HL3_PAGE_HOME.parent.appendChild(HL3_PAGE_HOME.node); HL3_PAGE_HOME=null; }
  pg.style.display='none';
}
// (hl3PageFromSection removed in v2.5.3 R3 — its "open section as full page" role is replaced by sub-pages.)
function hl3HostPage(hostId){
  const h=(HL_HOSTS||[]).find(x=>x.id===hostId); if(!h) return;
  const m=HL3.metrics[hostId];
  const vms=(HL3.vms||[]).filter(v=>v.node_id===hostId), cts=(HL3.cts||[]).filter(c=>c.node_id===hostId);
  const up=(s)=>s>0?(s>=86400?Math.floor(s/86400)+'d ':'')+Math.floor(s%86400/3600)+'h '+Math.floor(s%3600/60)+'m':'—';
  let html='<div class="card" style="padding:14px 16px;"><div class="section-head" style="margin-top:0;">Facts</div>'
    +'<div style="font-size:11px;color:var(--muted);">Kind: <b>'+escapeHtml(h.kind||'—')+'</b> · Address: <b>'+escapeHtml(h.address||'—')+'</b> · OS: <b>'+escapeHtml(h.os||'—')+'</b>'
    +(m?' · Uptime: <b>'+up(m.uptime_seconds)+'</b> · Metrics via <b>'+escapeHtml(m.source)+'</b> at '+escapeHtml((m.updated_at||'').slice(0,19).replace('T',' ')):'')+'</div>'
    +(m?hl3Bars(m):'<div style="font-size:10px;color:var(--dim);margin-top:6px;">No resource metrics for this node yet — they arrive with the next provider sync.</div>')+'</div>';
  html+='<div class="card" style="padding:14px 16px;"><div class="section-head" style="margin-top:0;">Guests ('+(vms.length+cts.length)+')</div><div class="hl3-tiles">'
    +vms.map(v=>'<span class="hl3-tile" data-onclick="hl3GuestPage(\'vm\',\''+jsArg(v.id)+'\')">'+hl3Dot(v.status)+'<span class="k">vm</span>'+escapeHtml(v.name||v.vm_id)+'</span>').join('')
    +cts.map(c=>'<span class="hl3-tile" data-onclick="hl3GuestPage(\'ct\',\''+jsArg(c.id)+'\')">'+hl3Dot(c.status)+'<span class="k">'+escapeHtml(c.kind||'ct')+'</span>'+escapeHtml(c.name||c.container_id)+'</span>').join('')
    +((vms.length+cts.length)?'':'<span class="hl3-empty">no guests</span>')+'</div></div>';
  hl3PageOpen('Node — '+(h.name||'?'),html);
}
function hl3GuestTarget(g,kind){
  // ActionExecutor targets use node/vmid; node_id is "pve-node:host:NODENAME".
  const nodeName=String(g.node_id||'').split(':').pop();
  return nodeName+'/'+(kind==='vm'?(g.vm_id||''):(g.container_id||''));
}
async function hl3GuestPage(kind,id){
  const list=kind==='vm'?(HL3.vms||[]):(HL3.cts||[]);
  const g=list.find(x=>x.id===id); if(!g) return;
  const host=(HL_HOSTS||[]).find(h=>h.id===g.node_id);
  const tgt=hl3GuestTarget(g,kind);
  const up=(s)=>s>0?(s>=86400?Math.floor(s/86400)+'d ':'')+Math.floor(s%86400/3600)+'h '+Math.floor(s%3600/60)+'m':'—';
  const act=(type,label)=>'<button class="btn btn-ghost" data-onclick="hl3PageClose();hl3DeckPropose(\''+type+'\',\''+(kind==='vm'?'vm':'container')+'\',\''+jsArg(tgt)+'\')">'+label+'</button>';
  let html='<div class="card" style="padding:14px 16px;"><div class="section-head" style="margin-top:0;">Status</div>'
    +'<div style="font-size:12px;">'+hl3Dot(g.status)+' <b>'+escapeHtml((g.status||'unknown').toUpperCase())+'</b>'
    +' <span style="color:var(--dim);font-size:10px;">on '+escapeHtml(host?host.name:String(g.node_id||'').split(':').pop())+' · id '+escapeHtml(kind==='vm'?g.vm_id:g.container_id)+'</span></div>'
    +'<div style="font-size:11px;color:var(--muted);margin-top:8px;">'
    +(kind==='vm'?('vCPU: <b>'+(g.cpu_cores||'—')+'</b> · RAM: <b>'+(g.memory_mb?(g.memory_mb/1024).toFixed(1)+' GB':'—')+'</b> · Uptime: <b>'+up(g.uptime_seconds)+'</b>'):('Kind: <b>'+escapeHtml(g.kind||'lxc')+'</b>'))
    +' · Last synced: '+escapeHtml((g.updated_at||'—').slice(0,19).replace('T',' '))+'</div></div>';
  html+='<div class="card" style="padding:14px 16px;"><div class="section-head" style="margin-top:0;">Actions <span style="font-weight:400;color:var(--dim)">(all approval-gated; nothing runs from this page directly)</span></div>'
    +'<div style="display:flex;gap:8px;flex-wrap:wrap;">'
    +act(kind==='vm'?'start_vm':'start_container','▶ Start')+act(kind==='vm'?'stop_vm':'stop_container','■ Stop (clean)')
    +act(kind==='vm'?'restart_vm':'restart_container','↻ Restart')+act('create_snapshot','📷 Snapshot')+act('run_backup','💾 Backup')+'</div></div>';
  html+='<div class="card" style="padding:14px 16px;"><div class="section-head" style="margin-top:0;">Recent related events</div><div id="hl3-guest-events" style="font-size:10px;color:var(--muted);">Loading…</div></div>';
  hl3PageOpen((kind==='vm'?'VM — ':'Container — ')+(g.name||tgt),html);
  try{
    const ev=await api('/homelab/events');
    const rows=(Array.isArray(ev&&ev.data)?ev.data:[]).filter(e=>
      String(e.subject_id||'').includes(kind==='vm'?g.vm_id:g.container_id)||String(e.message||'').includes(tgt)).slice(0,12);
    const el=document.getElementById('hl3-guest-events');
    if(el) el.innerHTML=rows.length?rows.map(e=>'<div>'+escapeHtml((e.created_at||'').slice(0,16).replace('T',' '))+' <b>'+escapeHtml(e.event_type||'')+'</b> '+escapeHtml((e.message||'').substring(0,110))+'</div>').join(''):'No events reference this guest yet.';
  }catch(e){}
}
// ---- v2.3.3 *arr apps (Homarr-style) -----------------------------------------------
const HL3_ARR_COLORS={sonarr:'#35c5f4',radarr:'#ffc230',lidarr:'#4dd865',readarr:'#8e4239',whisparr:'#c74f9e',prowlarr:'#e66000',bazarr:'#8c9eff'};
async function renderHl3Apps(){
  const el=document.getElementById('hl3-apps'); if(!el) return;
  let items=[];
  try{ const r=await api('/homelab/arr'); items=(r&&r.success&&r.data&&Array.isArray(r.data.items))?r.data.items:[]; }catch(e){}
  HL3.apps=items;
  const kpi=document.getElementById('hl3-apps-kpi');
  if(kpi) kpi.textContent=items.length?('· '+items.filter(a=>a.status==='ok').length+'/'+items.length+' healthy'):'';
  if(!items.length){ el.innerHTML='<div class="hl3-empty">No *arr apps connected yet. <b>+ App</b> supports sonarr, radarr, lidarr, readarr, whisparr, prowlarr, and bazarr — status, health, and queue at a glance, Homarr-style.</div>'; return; }
  el.innerHTML=items.map(a=>{
    const col=HL3_ARR_COLORS[a.kind]||'#4aa3ff';
    return '<div class="hl3-app" data-onclick="hl3ArrPage(\''+jsArg(a.id)+'\')">'
      +'<span class="av" style="background:'+col+'">'+escapeHtml((a.kind||'?')[0].toUpperCase())+'</span>'
      +'<span style="min-width:0;"><div class="nm">'+hl3Dot(a.status==='ok'?'ok':(a.status==='error'?'failed':''))+' '+escapeHtml(a.name)+'</div>'
      +'<div class="mt">'+escapeHtml(a.kind)+(a.version?' '+escapeHtml(a.version):'')
      +(a.queue_count>=0?' · queue '+a.queue_count:'')+(a.health_warnings?' · ⚠ '+a.health_warnings:'')+'</div></span></div>';
  }).join('');
}
async function hl3ArrAdd(){
  const kind=document.getElementById('hl3-ar-kind').value;
  const url=document.getElementById('hl3-ar-url').value.trim();
  const key=document.getElementById('hl3-ar-key').value.trim();
  if(!url||!key){ hlMsg('URL and API key are required',false); return; }
  const r=await api('/homelab/arr','POST',{kind,name:document.getElementById('hl3-ar-name').value.trim()||kind,url,apiKey:key});
  hlMsg(r.message||(r.success?'Saved':'Failed'),r.success);
  if(r.success){ document.getElementById('hl3-ar-url').value=''; document.getElementById('hl3-ar-key').value=''; hl3ArrSync(); }
}
async function hl3ArrSync(){
  const r=await api('/homelab/arr/sync','POST',{});
  hlMsg(r.message||(r.success?'Synced':'Sync failed'),r.success);
  renderHl3Apps();
}
function hl3ArrPage(id){
  const a=(HL3.apps||[]).find(x=>x.id===id); if(!a) return;
  const col=HL3_ARR_COLORS[a.kind]||'#4aa3ff';
  let html='<div class="card" style="padding:14px 16px;"><div class="section-head" style="margin-top:0;">'
    +'<span class="av" style="display:inline-flex;width:26px;height:26px;border-radius:6px;background:'+col+';color:#0a0f16;font-weight:800;align-items:center;justify-content:center;margin-right:8px;">'+escapeHtml((a.kind||'?')[0].toUpperCase())+'</span>'
    +escapeHtml(a.name)+' <span style="font-weight:400;color:var(--dim)">('+escapeHtml(a.kind)+')</span></div>'
    +'<div style="font-size:11px;color:var(--muted);">Status: '+hl3Dot(a.status==='ok'?'ok':'failed')+' <b>'+escapeHtml(a.status)+'</b>'
    +(a.version?' · Version <b>'+escapeHtml(a.version)+'</b>':'')
    +(a.queue_count>=0?' · Queue <b>'+a.queue_count+'</b>':'')
    +' · Health warnings <b>'+(a.health_warnings||0)+'</b>'
    +' · Checked '+escapeHtml((a.last_checked||'never').slice(0,19).replace('T',' '))+'</div>'
    +(a.last_message?'<div style="font-size:10px;color:var(--status-danger);margin-top:6px;">'+escapeHtml(a.last_message)+'</div>':'')
    +'<div style="display:flex;gap:8px;margin-top:10px;">'
    +'<button class="btn btn-primary" data-onclick="window.open(\''+jsArg(a.url)+'\',\'_blank\')">↗ Open '+escapeHtml(a.name)+'</button>'
    +'<button class="btn btn-ghost" data-onclick="hl3ArrSync();hl3PageClose()">⟳ Sync now</button>'
    +'<button class="btn btn-ghost" data-onclick="hl3ArrRemove(\''+jsArg(a.id)+'\')"><svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="vertical-align:-1px"><polyline points="3 6 5 6 21 6"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/></svg> Remove</button></div>'
    +'<div style="font-size:9px;color:var(--dim);margin-top:8px;">API key is stored write-only in the credential store (id '+escapeHtml(a.credential_id)+') and is never displayed. All requests are GET-only and allowlist-gated.</div></div>';
  hl3PageOpen('App — '+a.name,html);
}
async function hl3ArrRemove(id){
  if(!await uiConfirm('Remove this app and delete its stored API key?')) return;
  const r=await api('/homelab/arr/'+encodeURIComponent(id),'DELETE');
  hlMsg(r.message||(r.success?'Removed':'Failed'),r.success);
  hl3PageClose(); renderHl3Apps();
}
// ---- v2.5.2 Console Refit R2: THE widget runtime -----------------------------------------------
// One runtime for every dashboard tile: widget(kind, integrationId, el). Widgets are page-agnostic
// — they know their integration and their kind, never where they render. Full lifecycle
// (loading → success | empty | error, all labeled), per-kind TTL polling that stops when the
// element leaves the DOM, manual refresh, responsive sizing via the .wgt-grid zone, and a layout
// registry persisted per operator in /ui/state (ordered arrays — drag-and-drop ready).
// Data source: GET /homelab/integrations/{id}/widgets/{kind} (integration_state + freshness).
const WIDGET_KINDS={
  'health':          {title:'Health',          icon:'♥', ttl:30000,  render:wgtRenderHealth},
  'queue':           {title:'Queue',           icon:'⇣', ttl:15000,  render:wgtRenderQueue},
  'statistics':      {title:'Statistics',      icon:'∑', ttl:60000,  render:wgtRenderKv},
  'disk-usage':      {title:'Disk Usage',      icon:'◔', ttl:60000,  render:wgtRenderBars},
  'resource-usage':  {title:'Resources',       icon:'▦', ttl:20000,  render:wgtRenderBars},
  'recent-activity': {title:'Recent Activity', icon:'≡', ttl:30000,  wide:true, render:wgtRenderList},
  'calendar':        {title:'Upcoming',        icon:'▤', ttl:120000, wide:true, render:wgtRenderList},
  'failed-imports':  {title:'Failed Imports',  icon:'✕', ttl:60000,  wide:true, render:wgtRenderList},
  'logs':            {title:'Logs',            icon:'☰', ttl:20000,  wide:true, render:wgtRenderList},
  'alerts':          {title:'Alerts',          icon:'⚠', ttl:20000,  wide:true, render:wgtRenderList},
  // v3.0.1 Homarr parity: native integration widgets (Overseerr/Plex/Uptime-Kuma).
  'requests':        {title:'Requests',        icon:'▤', ttl:60000,  render:wgtRenderRequests},
  'mediaServer':     {title:'Media Server',    icon:'▶', ttl:30000,  render:wgtRenderMediaServer},
  'status':          {title:'Status',          icon:'◉', ttl:30000,  render:wgtRenderStatus},
};
const _widgets=new Map(); // el → widget state (timer bookkeeping; cleared on unmount)

function widget(kind,integrationId,el,opts){
  opts=opts||{};
  const def=WIDGET_KINDS[kind]||{title:kind,icon:'▣',ttl:30000,render:wgtRenderKv}; // unknown kinds render generically — never break
  const st={kind,integrationId,el,def,hasData:false,timer:null};
  el.classList.add('wgt'); if(def.wide) el.classList.add('wide');
  el.innerHTML='<div class="wgt-hd">'+def.icon+' '+escapeHtml(def.title)
    +' <span class="src">'+escapeHtml(opts.label||'')+'</span>'
    +'<span class="ctl">'+(opts.controls||'')+'<button title="Refresh now" data-onclick="wgtRefreshEl(this)">⟳</button></span></div>'
    +'<div class="wgt-bd"><div class="wgt-skel"></div><div class="wgt-skel" style="width:70%"></div></div>'
    +'<div class="wgt-ft"></div>';
  _widgets.set(el,st); el._wgt=st;
  wgtLoad(st,false);
  return { refresh:()=>wgtLoad(st,true), unmount:()=>wgtUnmount(el) };
}
function wgtUnmount(el){ const st=_widgets.get(el); if(st){ clearTimeout(st.timer); _widgets.delete(el); } }
function wgtRefreshEl(btn){ const el=btn.closest('.wgt'); if(el&&el._wgt) wgtLoad(el._wgt,true); }
function wgtPath(st){ return '/homelab/integrations/'+encodeURIComponent(st.integrationId)+'/widgets/'+encodeURIComponent(st.kind); }

async function wgtLoad(st,force){
  if(!st.el.isConnected){ wgtUnmount(st.el); return; } // page moved on — stop polling
  clearTimeout(st.timer);
  if(force) apiCacheBust(wgtPath(st));
  const bd=st.el.querySelector('.wgt-bd'), ft=st.el.querySelector('.wgt-ft');
  let r=null;
  try{ r=await api(wgtPath(st)); }catch(e){ r={success:false,message:(e&&e.message)||'load failed'}; }
  if(!st.el.isConnected){ wgtUnmount(st.el); return; }
  if(r&&r.success&&r.data){
    st.hasData=true;
    try{ bd.innerHTML=st.def.render(r.data.payload||{},st); }
    catch(e){ bd.innerHTML='<div class="wgt-state err">Render failed: '+escapeHtml((e&&e.message)||'?')+'</div>'; }
    wgtFreshness(ft,r.data.updated_at,st.def.ttl);
  }else if(r&&r.error==='not_found'){
    if(!st.hasData) bd.innerHTML='<div class="wgt-state">No data yet — this integration hasn’t published “'+escapeHtml(st.kind)+'”. It appears after the next sync.</div>';
    if(ft) ft.textContent='';
  }else{
    if(!st.hasData) bd.innerHTML='<div class="wgt-state err">'+escapeHtml((r&&r.message)||'Load failed.')+'<span class="retry" data-onclick="wgtRefreshEl(this)">retry</span></div>';
    else if(ft){ ft.classList.add('stale'); ft.textContent='refresh failed — showing last data'; }
  }
  st.timer=setTimeout(()=>wgtLoad(st,false),st.def.ttl); // TTL poll; api() cache still dedupes
}
function wgtFreshness(ft,iso,ttl){
  if(!ft) return;
  if(!iso){ ft.textContent=''; return; }
  const age=Date.now()-Date.parse(iso);
  const stale=isFinite(age)&&age>Math.max(3*ttl,180000);
  ft.className='wgt-ft'+(stale?' stale':'');
  ft.textContent=(stale?'stale — ':'')+'updated '+String(iso).slice(0,19).replace('T',' ');
}

// ---- Kind renderers (typed payloads from integration_state; tolerant of missing fields) --------
function wgtRenderHealth(p){
  const ok=(p.status||'unknown')==='ok';
  return '<div style="display:flex;align-items:center;gap:10px;">'
    +'<span style="font-size:20px;">'+hl3Dot(ok?'ok':(p.status==='error'?'failed':''))+'</span>'
    +'<div><div style="font-weight:700;color:var(--anthill-text)">'+escapeHtml(p.status||'unknown')+(p.version?' · v'+escapeHtml(String(p.version)):'')+'</div>'
    +'<div style="font-size:10px;color:'+((p.health_warnings|0)>0?'var(--status-warning)':'var(--dim)')+'">'
    +((p.health_warnings|0)>0?('⚠ '+p.health_warnings+' health warning'+(p.health_warnings>1?'s':'')):'no health warnings')+'</div></div></div>';
}
function wgtRenderQueue(p){
  const t=(typeof p.total==='number')?p.total:-1;
  return '<div class="wgt-big">'+(t<0?'—':t)+'</div><div style="font-size:10px;color:var(--dim);">'+(t<0?'queue not reported yet':('item'+(t===1?'':'s')+' in queue'))+'</div>';
}
function wgtRenderKv(p){
  const rows=Object.entries(p||{}).filter(([k,v])=>v===null||['string','number','boolean'].includes(typeof v)).slice(0,12);
  if(!rows.length) return '<div class="wgt-state">Nothing to show yet.</div>';
  return '<div class="wgt-kv">'+rows.map(([k,v])=>'<b>'+escapeHtml(k.replace(/_/g,' '))+'</b><span>'+escapeHtml(String(v??'—'))+'</span>').join('')+'</div>';
}
function wgtRenderBars(p){
  // Accepts {items:[{label,used_bytes,total_bytes}]} or flat {cpu_percent,mem_used_bytes,mem_total_bytes,...}
  const bars=[];
  if(Array.isArray(p.items)) for(const it of p.items.slice(0,6)) bars.push([it.label||it.path||it.name||'?',it.used_bytes,it.total_bytes]);
  else{
    if(typeof p.cpu_percent==='number'&&p.cpu_percent>=0) bars.push(['cpu',p.cpu_percent,100]);
    if(p.mem_total_bytes>0) bars.push(['mem',p.mem_used_bytes,p.mem_total_bytes]);
    if(p.disk_total_bytes>0) bars.push(['disk',p.disk_used_bytes,p.disk_total_bytes]);
  }
  if(!bars.length) return '<div class="wgt-state">No usage data yet.</div>';
  return '<div class="hl3-bars">'+bars.map(([l,u,t])=>{
    const pct=(t>0&&u>=0)?Math.min(100,Math.round(u*100/t)):0;
    return '<div class="hl3-bar"><b>'+escapeHtml(String(l)).slice(0,10)+'</b><div class="tr"><div class="fl'+(pct>90?' bad':(pct>75?' warn':''))+'" style="width:'+pct+'%"></div></div><span>'+pct+'%</span></div>';
  }).join('')+'</div>';
}
function wgtRenderList(p){
  const items=Array.isArray(p.items)?p.items:[];
  if(!items.length) return '<div class="wgt-state">Nothing here yet.</div>';
  return items.slice(0,10).map(it=>{
    const at=it.at||it.time||it.date||it.created_at||'';
    const tx=it.title||it.text||it.message||it.line||it.name||JSON.stringify(it).slice(0,80);
    return '<div class="wgt-li"><span class="at">'+escapeHtml(String(at).slice(0,16).replace('T',' '))+'</span><span class="tx" title="'+escapeHtml(String(tx))+'">'+escapeHtml(String(tx))+'</span></div>';
  }).join('');
}

// v3.0.1 Homarr parity — renderers for the native media/monitoring widgets. Tolerant of missing
// fields (an integer < 0 means "not reported yet"), same discipline as the built-in renderers.
function wgtRenderRequests(p){
  const t=(typeof p.total==='number')?p.total:-1;
  const parts=[];
  if((p.pending|0)>0) parts.push((p.pending|0)+' pending');
  if((p.processing|0)>0) parts.push((p.processing|0)+' processing');
  if((p.available|0)>=0 && (p.available|0)>0) parts.push((p.available|0)+' available');
  return '<div class="wgt-big">'+(t<0?'—':t)+'</div>'
    +'<div style="font-size:10px;color:var(--dim);">'
    +(t<0?'requests not reported yet':escapeHtml(parts.join(' · ')||('total request'+(t===1?'':'s'))))+'</div>';
}
function wgtRenderMediaServer(p){
  const s=(typeof p.active_streams==='number')?p.active_streams:-1;
  return '<div class="wgt-big">'+(s<0?'—':s)+'</div>'
    +'<div style="font-size:10px;color:var(--dim);">'
    +(s<0?'stream count not reported':('active stream'+(s===1?'':'s')))
    +(p.version?' · v'+escapeHtml(String(p.version)):'')+'</div>';
}
function wgtRenderStatus(p){
  const up=(p.up|0), down=(p.down|0);
  const total=(typeof p.total==='number')?p.total:(up+down);
  const ok=down===0;
  return '<div style="display:flex;align-items:center;gap:10px;">'
    +'<span style="font-size:20px;">'+hl3Dot(ok?'ok':(up===0?'failed':''))+'</span>'
    +'<div><div style="font-weight:700;color:var(--anthill-text)">'+up+' up · '+down+' down</div>'
    +'<div style="font-size:10px;color:'+(down>0?'var(--status-warning)':'var(--dim)')+'">'
    +total+' monitor'+(total===1?'':'s')+' watched</div></div></div>';
}

// ---- Layout registry (per-operator, persisted via /ui/state; ordered = drag-and-drop ready) ----
function wgtZone(zone){ const w=uiState.widgets||{}; return Array.isArray(w[zone])?w[zone]:[]; }
function wgtZoneSave(zone,list){ uiState.widgets=uiState.widgets||{}; uiState.widgets[zone]=list; saveUiState(); }

// ---- Homelab page glue: the "homelab" widget zone ----------------------------------------------
let HL3W={instances:[],kinds:[]};
async function renderHl3Widgets(){
  const box=document.getElementById('hl3-widgets'); if(!box) return;
  try{
    const r=await api('/homelab/integrations');
    if(r&&r.success&&r.data){ HL3W.instances=r.data.items||[]; HL3W.kinds=r.data.kinds||[]; }
  }catch(e){}
  for(const child of Array.from(box.children)) if(child._wgt) wgtUnmount(child);
  const layout=wgtZone('homelab');
  const kpi=document.getElementById('hl3-widgets-kpi');
  if(kpi) kpi.textContent=layout.length?('· '+layout.length):'';
  if(!layout.length){
    box.innerHTML='<div class="hl3-empty">No widgets yet. <b>+ Widget</b> pins live data from any connected integration — health, queue, and more as integrations publish them. Your layout is saved per operator.</div>';
    return;
  }
  box.innerHTML='';
  layout.forEach((entry,ix)=>{
    const inst=HL3W.instances.find(i=>i.id===entry.integration_id);
    const div=document.createElement('div');
    div.dataset.wid=entry.id;
    const controls='<button title="Move left" data-onclick="hl3WidgetMove(this,-1)">◀</button>'
      +'<button title="Move right" data-onclick="hl3WidgetMove(this,1)">▶</button>'
      +'<button title="Remove widget (data is untouched)" data-onclick="hl3WidgetRemove(this)">✕</button>';
    box.appendChild(div);
    widget(entry.kind,entry.integration_id,div,{label:inst?inst.name:'(integration removed)',controls:controls});
  });
}
function hl3WidgetMove(btn,dir){
  const el=btn.closest('.wgt'); if(!el) return;
  const list=wgtZone('homelab').slice();
  const ix=list.findIndex(e=>e.id===el.dataset.wid);
  const to=ix+dir;
  if(ix<0||to<0||to>=list.length) return;
  const [e]=list.splice(ix,1); list.splice(to,0,e);
  wgtZoneSave('homelab',list); renderHl3Widgets();
}
function hl3WidgetRemove(btn){
  const el=btn.closest('.wgt'); if(!el) return;
  wgtZoneSave('homelab',wgtZone('homelab').filter(e=>e.id!==el.dataset.wid));
  renderHl3Widgets();
}
function hl3WidgetsRefresh(){
  for(const [el,st] of _widgets) if(el.closest('#hl3-widgets')) wgtLoad(st,true);
}
async function hl3WidgetPickerToggle(){
  const pk=document.getElementById('hl3-widget-picker'); if(!pk) return;
  if(pk.classList.contains('open')){ pk.classList.remove('open'); return; }
  const r=await api('/homelab/integrations');
  if(r&&r.success&&r.data){ HL3W.instances=r.data.items||[]; HL3W.kinds=r.data.kinds||[]; }
  const sel=document.getElementById('hl3-wp-integration');
  const enabled=HL3W.instances.filter(i=>i.enabled);
  if(!enabled.length){ hlMsg('Connect an integration first (Apps → + App).',false); return; }
  sel.innerHTML=enabled.map(i=>'<option value="'+escapeHtml(i.id)+'">'+escapeHtml(i.name)+' ('+escapeHtml(i.kind)+')</option>').join('');
  hl3WidgetPickerKinds();
  pk.classList.add('open');
}
function hl3WidgetPickerKinds(){
  const sel=document.getElementById('hl3-wp-integration'), ks=document.getElementById('hl3-wp-kind');
  if(!sel||!ks) return;
  const inst=HL3W.instances.find(i=>i.id===sel.value);
  const kinds=(HL3W.kinds.find(k=>k.kind===(inst&&inst.kind))||{}).widget_kinds||[];
  ks.innerHTML=kinds.length?kinds.map(k=>'<option value="'+escapeHtml(k)+'">'+escapeHtml((WIDGET_KINDS[k]||{}).title||k)+'</option>').join(''):'<option value="">(no widgets offered)</option>';
}
function hl3WidgetAdd(){
  const id=document.getElementById('hl3-wp-integration').value;
  const kind=document.getElementById('hl3-wp-kind').value;
  if(!id||!kind){ hlMsg('Pick an integration and a widget kind.',false); return; }
  const list=wgtZone('homelab').slice();
  list.push({id:'w'+Date.now().toString(36)+Math.floor(Math.random()*1e4).toString(36),kind:kind,integration_id:id});
  wgtZoneSave('homelab',list);
  document.getElementById('hl3-widget-picker').classList.remove('open');
  renderHl3Widgets();
}

// ---- v2.5.4 Console Refit R4: THE generic collection manager -----------------------------------
// One reusable component for any CRUD collection (targets today; integration collections in R5+):
// search, filter, sortable columns, row selection with bulk actions, per-row actions, count
// footer. Vanilla + string-built like the rest of the console; toolbar renders once (search
// keeps focus), only the table body re-renders. cfg:
//   { el, load:async()=>rows, idOf(row), columns:[{key,label,render?(row)}], searchKeys:[...],
//     filters:[{label,fn?}], bulk:[{label,confirm?,run:async(ids)}], rowActions:[{icon,title,run:async(row)}],
//     emptyText, defaultSort }
function collectionManager(cfg){
  const st={rows:[],q:'',sortKey:cfg.defaultSort||null,sortDir:1,sel:new Set(),filter:0};
  const el=cfg.el;
  el.innerHTML='<div class="cm-bar" style="display:flex;gap:8px;flex-wrap:wrap;align-items:center;margin:8px 0;">'
    +'<input class="form-input cm-q" type="text" placeholder="search…" style="width:180px;" autocomplete="off">'
    +((cfg.filters&&cfg.filters.length)?'<select class="form-select cm-f" aria-label="Filter list" style="width:130px;">'+cfg.filters.map((f,i)=>'<option value="'+i+'">'+escapeHtml(f.label)+'</option>').join('')+'</select>':'')
    +'<span class="cm-bulk" style="display:flex;gap:6px;"></span>'
    +'<span class="cm-count" style="margin-left:auto;font-size:10px;color:var(--dim);"></span></div>'
    +'<div class="cm-body" style="overflow-x:auto;"></div>';
  const bulkBox=el.querySelector('.cm-bulk');
  (cfg.bulk||[]).forEach((b,i)=>{
    const btn=document.createElement('button');
    btn.className='btn btn-ghost'; btn.textContent=b.label; btn.disabled=true; btn.dataset.cmb=i;
    btn.addEventListener('click',async()=>{
      const ids=[...st.sel]; if(!ids.length) return;
      if(b.confirm&&!(await uiConfirm(b.confirm.replace('{n}',ids.length)))) return;
      await b.run(ids); reload();
    });
    bulkBox.appendChild(btn);
  });
  el.querySelector('.cm-q').addEventListener('input',e=>{ st.q=e.target.value.trim().toLowerCase(); renderBody(); });
  const fsel=el.querySelector('.cm-f');
  if(fsel) fsel.addEventListener('change',e=>{ st.filter=parseInt(e.target.value,10)||0; renderBody(); });
  function visibleRows(){
    let rows=st.rows.slice();
    const f=(cfg.filters||[])[st.filter];
    if(f&&typeof f.fn==='function') rows=rows.filter(f.fn);
    if(st.q) rows=rows.filter(r=>(cfg.searchKeys||[]).some(k=>String(r[k]??'').toLowerCase().includes(st.q)));
    if(st.sortKey) rows.sort((a,b)=>{ const x=String(a[st.sortKey]??''),y=String(b[st.sortKey]??'');
      return st.sortDir*x.localeCompare(y,undefined,{numeric:true}); });
    return rows;
  }
  function syncBulk(){ el.querySelectorAll('.cm-bulk button').forEach(b=>b.disabled=!st.sel.size); }
  function renderBody(){
    const rows=visibleRows();
    const body=el.querySelector('.cm-body');
    el.querySelector('.cm-count').textContent=rows.length+' of '+st.rows.length+(st.sel.size?(' · '+st.sel.size+' selected'):'');
    if(!rows.length){ body.innerHTML='<div class="hl3-empty">'+escapeHtml(st.rows.length?'Nothing matches the current search/filter.':(cfg.emptyText||'Nothing here yet.'))+'</div>'; syncBulk(); return; }
    let h='<table class="users-table"><thead><tr><th style="width:24px;"><input type="checkbox" class="cm-all"'+(rows.length&&rows.every(r=>st.sel.has(cfg.idOf(r)))?' checked':'')+'></th>';
    for(const c of cfg.columns) h+='<th class="cm-th" data-k="'+escapeHtml(c.key)+'" style="cursor:pointer;">'+escapeHtml(c.label)+(st.sortKey===c.key?(st.sortDir>0?' ▲':' ▼'):'')+'</th>';
    if(cfg.rowActions&&cfg.rowActions.length) h+='<th>Actions</th>';
    h+='</tr></thead><tbody>';
    for(const r of rows){
      const id=cfg.idOf(r);
      h+='<tr><td><input type="checkbox" class="cm-sel" data-row="'+escapeHtml(id)+'"'+(st.sel.has(id)?' checked':'')+'></td>';
      for(const c of cfg.columns) h+='<td>'+(c.render?c.render(r):escapeHtml(String(r[c.key]??'')))+'</td>';
      if(cfg.rowActions&&cfg.rowActions.length){
        h+='<td style="white-space:nowrap;">'+cfg.rowActions.map((a,i)=>'<button class="btn btn-ghost cm-act" data-row="'+escapeHtml(id)+'" data-a="'+i+'" title="'+escapeHtml(a.title)+'" style="padding:1px 6px;font-size:10px;">'+(typeof a.icon==='function'?a.icon(r):a.icon)+'</button>').join(' ')+'</td>';
      }
      h+='</tr>';
    }
    body.innerHTML=h+'</tbody></table>';
    body.querySelector('.cm-all').addEventListener('change',e=>{
      rows.forEach(r=>{ const id=cfg.idOf(r); if(e.target.checked) st.sel.add(id); else st.sel.delete(id); });
      renderBody();
    });
    body.querySelectorAll('.cm-sel').forEach(cb=>cb.addEventListener('change',e=>{
      if(e.target.checked) st.sel.add(e.target.dataset.row); else st.sel.delete(e.target.dataset.row);
      el.querySelector('.cm-count').textContent=visibleRows().length+' of '+st.rows.length+(st.sel.size?(' · '+st.sel.size+' selected'):'');
      syncBulk();
    }));
    body.querySelectorAll('.cm-th').forEach(th=>th.addEventListener('click',()=>{
      const k=th.dataset.k;
      if(st.sortKey===k) st.sortDir=-st.sortDir; else { st.sortKey=k; st.sortDir=1; }
      renderBody();
    }));
    body.querySelectorAll('.cm-act').forEach(btn=>btn.addEventListener('click',async()=>{
      const row=st.rows.find(r=>cfg.idOf(r)===btn.dataset.row); if(!row) return;
      await cfg.rowActions[parseInt(btn.dataset.a,10)].run(row); reload();
    }));
    syncBulk();
  }
  async function reload(){ st.rows=(await cfg.load())||[]; for(const id of [...st.sel]) if(!st.rows.some(r=>cfg.idOf(r)===id)) st.sel.delete(id); renderBody(); }
  el._cm={reload};
  reload();
  return el._cm;
}

// ---- Targets (allow/blocklist) — the first collection-manager surface --------------------------
function hlTargetChip(e){
  const deny=e.kind==='deny';
  return '<span style="font-size:9px;font-weight:700;padding:1px 7px;border-radius:8px;border:1px solid '
    +(deny?'var(--status-danger,#f05252)':'var(--status-success,#31c48d)')+';color:'
    +(deny?'var(--status-danger,#f05252)':'var(--status-success,#31c48d)')+';">'+(deny?'DENY':'ALLOW')+'</span>';
}
function renderHlTargets(){
  const el=document.getElementById('hl-targets'); if(!el) return;
  if(el._cm){ el._cm.reload(); return; }
  collectionManager({
    el,
    load:async()=>{
      const r=await api('/homelab/allowlist');
      const rows=(r&&r.success&&Array.isArray(r.data))?r.data:[];
      const kpi=document.getElementById('hl-targets-kpi');
      if(kpi) kpi.textContent=rows.length?('· '+rows.filter(e=>e.kind!=='deny'&&e.enabled).length+' allow / '+rows.filter(e=>e.kind==='deny'&&e.enabled).length+' deny'):'';
      return rows;
    },
    idOf:e=>e.id,
    searchKeys:['target','note','added_by','kind'],
    defaultSort:'created_at',
    emptyText:'No targets yet. Allow entries open hosts to the deterministic providers; deny entries slam the door (deny beats allow).',
    filters:[
      {label:'All'},
      {label:'Allow',fn:e=>e.kind!=='deny'},
      {label:'Deny',fn:e=>e.kind==='deny'},
      {label:'Disabled',fn:e=>!e.enabled},
    ],
    columns:[
      {key:'kind',label:'Kind',render:hlTargetChip},
      {key:'target',label:'Target',render:e=>'<b>'+escapeHtml(e.target)+'</b>'},
      {key:'note',label:'Note'},
      {key:'enabled',label:'Enabled',render:e=>e.enabled?'<span style="color:var(--status-success)">yes</span>':'<span style="color:var(--dim)">no</span>'},
      {key:'added_by',label:'Origin'},
      {key:'created_at',label:'Added',render:e=>escapeHtml(hlDate(e.created_at))},
    ],
    rowActions:[
      {icon:'✎',title:'Edit note',run:async e=>{
        const note=await uiPrompt('Note for '+e.target,{value:e.note||''});
        if(note!==null&&note!==undefined) await api('/homelab/allowlist/'+encodeURIComponent(e.id),'PUT',{note:note});
      }},
      {icon:e=>e.kind==='deny'?'✔':'⛔',title:'Flip allow/deny',run:async e=>{
        await api('/homelab/allowlist/'+encodeURIComponent(e.id),'PUT',{kind:e.kind==='deny'?'allow':'deny'});
      }},
      {icon:e=>e.enabled?'⏸':'▶',title:'Enable/disable',run:async e=>{
        await api('/homelab/allowlist/'+encodeURIComponent(e.id),'PUT',{enabled:!e.enabled});
      }},
      // v0.3.8.53: an inline stroke SVG, not 🗑 — the one COLOR emoji in a deliberately
      // monochrome glyph row (its siblings ✎ ✔ ⛔ ⏸ ▶ are text-presentation and stay).
      {icon:'<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="vertical-align:-1px"><polyline points="3 6 5 6 21 6"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/></svg>',title:'Remove entry',run:async e=>{
        if(await uiConfirm('Remove target \''+e.target+'\'?')) await api('/homelab/allowlist/'+encodeURIComponent(e.id),'DELETE');
      }},
    ],
    bulk:[
      {label:'Enable',run:ids=>api('/homelab/allowlist/bulk','POST',{action:'enable',ids})},
      {label:'Disable',run:ids=>api('/homelab/allowlist/bulk','POST',{action:'disable',ids})},
      {label:'Remove',confirm:'Remove {n} target entr(y/ies)?',run:ids=>api('/homelab/allowlist/bulk','POST',{action:'remove',ids})},
    ],
  });
}
async function hlTargetAdd(){
  const target=document.getElementById('hl-tg-target').value.trim();
  const kind=document.getElementById('hl-tg-kind').value;
  const note=document.getElementById('hl-tg-note').value.trim();
  if(!target){ hlMsg('Target (hostname, IP, or CIDR) is required',false); return; }
  const r=await api('/homelab/allowlist','POST',{target,kind,note});
  hlMsg(r.message||(r.success?'Saved':'Failed'),r.success);
  if(r.success){ document.getElementById('hl-tg-target').value=''; document.getElementById('hl-tg-note').value=''; renderHlTargets(); }
}

async function hl3DeckPropose(type,kind,target){
  const card=document.getElementById('hl-actions-card');
  const sel=document.getElementById('hl-a-type');
  if(sel&&!sel.options.length) await loadHlActions();
  if(sel) sel.value=type;
  const tk=document.getElementById('hl-a-tkind'); if(tk) tk.value=kind;
  const ti=document.getElementById('hl-a-target'); if(ti) ti.value=target;
  if(card) card.scrollIntoView({behavior:'smooth',block:'center'});
  const ri=document.getElementById('hl-a-rollback'); if(ri) ri.focus();
}
function hl3LoaderFallbacks(){
  setTimeout(()=>{
    const kpis=document.getElementById('hl-cmd-kpis');
    if(kpis&&/^Loading/.test(kpis.textContent)) kpis.textContent='Command summary unavailable right now — retrying on the next poll.';
    const nxt=document.getElementById('hl-next');
    if(nxt&&/^Loading/.test(nxt.textContent)) nxt.textContent='Recommendations unavailable right now — retrying on the next poll.';
  },7000);
}

// -- Network & risk awareness (v1.13.0) --------------------------------------------
async function loadHlRisks(){
  try{
    const [devs,risks]=await Promise.all([api('/homelab/devices'),api('/homelab/risks')]);
    renderHlDevices(devs.success?(devs.data||[]):[]);
    renderHlRisks(risks.success?(risks.data||[]):[]);
  }catch(e){ hlMsg('Risk load failed: '+e.message,false); }
}

function renderHlDevices(devs){
  const tb=document.getElementById('hl-devs-tbody'); if(!tb) return;
  const admin=ROLE==='admin';
  if(!devs.length){ tb.innerHTML='<tr><td colspan="8" style="color:var(--dim);text-align:center;padding:16px;">No devices registered yet.</td></tr>'; return; }
  tb.innerHTML=devs.map(d=>'<tr><td>'+escapeHtml(d.name||'—')+'</td><td>'+escapeHtml(d.kind||'unknown')+'</td><td>'+escapeHtml(d.mac||'—')+
    '</td><td>'+escapeHtml(d.ip||'—')+'</td><td>'+escapeHtml(d.vlan||'—')+
    '</td><td>'+(d.known?'yes':'<span style="color:var(--red)">NO</span>')+'</td><td>'+hlDate(d.last_seen)+'</td>'+
    '<td>'+(admin?('<button class="btn btn-ghost" data-onclick="hlDelDevice(\''+jsArg(d.id)+'\')">✕</button>'):'—')+'</td></tr>').join('');
}

function renderHlRisks(risks){
  const tb=document.getElementById('hl-risks-tbody'); if(!tb) return;
  const admin=ROLE==='admin';
  const open=risks.filter(r=>r.status==='open');
  const kpi=document.getElementById('hl-risk-kpi');
  if(kpi) kpi.textContent='· '+open.filter(r=>r.severity==='error').length+' error · '+open.filter(r=>r.severity==='warning').length+' warning · '+open.filter(r=>r.severity==='info').length+' info open';
  const visible=risks.filter(r=>r.status!=='resolved');
  if(!visible.length){ tb.innerHTML='<tr><td colspan="6" style="color:var(--dim);text-align:center;padding:16px;">No open findings — register inventory and hit Analyze Now.</td></tr>'; return; }
  tb.innerHTML=visible.map(r=>{
    const col=r.severity==='error'?'var(--red)':(r.severity==='warning'?'var(--yellow, orange)':'var(--dim)');
    return '<tr><td style="color:'+col+'">'+escapeHtml(r.severity)+'</td><td>'+escapeHtml(r.finding_kind)+'</td><td>'+escapeHtml(r.summary||'')+
      '</td><td>'+escapeHtml(r.status)+'</td><td>'+hlDate(r.updated_at)+'</td>'+
      '<td>'+((admin&&r.status==='open')?('<button class="btn btn-ghost" data-onclick="hlAckRisk(\''+jsArg(r.id)+'\')" title="Acknowledge: keep visible, stop counting as open">✓ Ack</button>'):'—')+'</td></tr>';
  }).join('');
}

async function hlAddDevice(){
  const name=document.getElementById('hl-n-name').value.trim(), mac=document.getElementById('hl-n-mac').value.trim();
  if(!name&&!mac){ hlMsg('Device needs a name or MAC',false); return; }
  const r=await api('/homelab/devices','POST',{name, kind:document.getElementById('hl-n-kind').value, mac,
    ip:document.getElementById('hl-n-ip').value.trim(), vlan:document.getElementById('hl-n-vlan').value.trim(),
    known:document.getElementById('hl-n-known').value==='yes'});
  hlMsg(r.message||(r.success?'Saved':'Failed'),r.success);
  if(r.success){ ['hl-n-name','hl-n-mac','hl-n-ip','hl-n-vlan'].forEach(id=>document.getElementById(id).value=''); loadHlRisks(); }
}

async function hlDelDevice(id){
  const r=await api('/homelab/devices/'+encodeURIComponent(id),'DELETE');
  hlMsg(r.message||(r.success?'Removed':'Failed'),r.success);
  if(r.success) loadHlRisks();
}

async function hlAnalyzeRisks(){
  hlMsg('Analyzing risks...',true);
  const r=await api('/homelab/risks/analyze','POST',{});
  hlMsg(r.message||(r.success?'Done':'Failed'),r.success);
  if(r.success) loadHlRisks();
}

async function hlAckRisk(id){
  const r=await api('/homelab/risks/'+encodeURIComponent(id)+'/ack','POST',{});
  hlMsg(r.message||(r.success?'Acknowledged':'Failed'),r.success);
  if(r.success) loadHlRisks();
}

// -- Health checks (v1.11.0) -----------------------------------------------------
async function loadHlHealth(){
  try{
    const [sum,sch,res]=await Promise.all([
      api('/homelab/health/summary'),api('/homelab/health/schedules'),api('/homelab/health/results')]);
    const s=sum.success?(sum.data||{}):{};
    const kpi=document.getElementById('hl-health-kpi');
    if(kpi) kpi.textContent='· '+(s.healthy||0)+' healthy · '+(s.degraded||0)+' degraded · '+(s.failed||0)+' failed · '+(s.unknown||0)+' unknown';
    renderHlChecks(sch.success?(sch.data||[]):[], res.success?(res.data||[]):[]);
  }catch(e){ hlMsg('Health load failed: '+e.message,false); }
}

function renderHlChecks(schedules,results){
  const tb=document.getElementById('hl-checks-tbody'); if(!tb) return;
  const admin=ROLE==='admin';
  if(!schedules.length){ tb.innerHTML='<tr><td colspan="7" style="color:var(--dim);text-align:center;padding:16px;">No health checks scheduled yet.</td></tr>'; return; }
  tb.innerHTML=schedules.map(sc=>{
    const last=results.find(r=>r.target===sc.target&&r.check_kind===sc.check_kind);
    const st=last?last.status:'—';
    const col=st==='healthy'?'var(--green)':(st==='degraded'?'var(--yellow, orange)':(st==='failed'?'var(--red)':'var(--dim)'));
    return '<tr><td>'+escapeHtml(sc.check_kind)+'</td><td>'+escapeHtml(sc.target)+'</td>'+
      '<td style="color:'+col+'">'+escapeHtml(st)+(sc.enabled?'':' (disabled)')+'</td>'+
      '<td>'+(last?last.latency_ms+'ms':'—')+'</td><td>'+escapeHtml(last?(last.detail||''):'not run yet')+'</td>'+
      '<td>'+(last?hlDate(last.checked_at):'—')+'</td>'+
      '<td>'+(admin?('<button class="btn btn-ghost" data-onclick="hlDelCheck(\''+jsArg(sc.id)+'\')">✕</button>'):'—')+'</td></tr>';
  }).join('');
}

async function hlAddCheck(){
  const target=document.getElementById('hl-c-target').value.trim();
  if(!target){ hlMsg('Check target is required',false); return; }
  const r=await api('/homelab/health/schedules','POST',{checkKind:document.getElementById('hl-c-kind').value,
    target, timeoutMs:parseInt(document.getElementById('hl-c-timeout').value,10)||0});
  hlMsg(r.message||(r.success?'Saved':'Failed'),r.success);
  if(r.success){ document.getElementById('hl-c-target').value=''; loadHlHealth(); }
}

async function hlDelCheck(id){
  const r=await api('/homelab/health/schedules/'+encodeURIComponent(id),'DELETE');
  hlMsg(r.message||(r.success?'Removed':'Failed'),r.success);
  if(r.success) loadHlHealth();
}

async function hlRunChecks(){
  hlMsg('Running health checks...',true);
  const r=await api('/homelab/health/run','POST',{});
  hlMsg(r.message||(r.success?'Done':'Failed'),r.success);
  loadHlHealth();
}

async function hlTestNotify(){
  const r=await api('/homelab/notifications/test','POST',{});
  hlMsg(r.message||(r.success?'Sent':'Failed'),r.success);
}

async function hlAddHost(){
  const name=document.getElementById('hl-h-name').value.trim();
  if(!name){ hlMsg('Host name is required',false); return; }
  const tags=document.getElementById('hl-h-tags').value.split(',').map(t=>t.trim()).filter(t=>t);
  const r=await api('/homelab/hosts','POST',{name, kind:document.getElementById('hl-h-kind').value,
    address:document.getElementById('hl-h-addr').value.trim(), os:document.getElementById('hl-h-os').value.trim(), roleTags:tags});
  hlMsg(r.message||(r.success?'Saved':'Failed'),r.success);
  if(r.success){ ['hl-h-name','hl-h-addr','hl-h-os','hl-h-tags'].forEach(id=>document.getElementById(id).value=''); loadHomelab(); }
}

async function hlAddService(){
  const name=document.getElementById('hl-s-name').value.trim();
  if(!name){ hlMsg('Service name is required',false); return; }
  const ports=document.getElementById('hl-s-ports').value.split(',').map(p=>parseInt(p.trim(),10)).filter(p=>!isNaN(p)&&p>0);
  const r=await api('/homelab/services','POST',{name, nodeId:document.getElementById('hl-s-node').value,
    url:document.getElementById('hl-s-url').value.trim(), ports, owner:document.getElementById('hl-s-owner').value.trim(),
    criticality:document.getElementById('hl-s-crit').value, internetExposed:document.getElementById('hl-s-exposed').value==='yes'});
  hlMsg(r.message||(r.success?'Saved':'Failed'),r.success);
  if(r.success){ ['hl-s-name','hl-s-url','hl-s-ports','hl-s-owner'].forEach(id=>document.getElementById(id).value=''); loadHomelab(); }
}

async function hlAddDep(){
  const from=document.getElementById('hl-d-from').value, to=document.getElementById('hl-d-to').value;
  if(!from||!to){ hlMsg('Register a host and a service first',false); return; }
  const [fk,fid]=from.split(':'), [tk,tid]=to.split(':');
  const r=await api('/homelab/dependencies','POST',{fromKind:fk, fromId:fid, toKind:tk, toId:tid,
    dependencyKind:document.getElementById('hl-d-kind').value, notes:document.getElementById('hl-d-notes').value.trim()});
  hlMsg(r.message||(r.success?'Saved':'Failed'),r.success);
  if(r.success){ document.getElementById('hl-d-notes').value=''; loadHomelab(); }
}

async function hlDelDep(id){
  const r=await api('/homelab/dependencies/'+encodeURIComponent(id),'DELETE');
  hlMsg(r.message||(r.success?'Removed':'Failed'),r.success);
  if(r.success) loadHomelab();
}

async function hlExport(){
  const r=await api('/homelab/export');
  if(!r.success){ hlMsg(r.message||'Export failed',false); return; }
  const blob=new Blob([JSON.stringify(r.data,null,2)],{type:'application/json'});
  const a=document.createElement('a'); a.href=URL.createObjectURL(blob);
  a.download='anthill-inventory-'+new Date().toISOString().slice(0,10)+'.json';
  a.click(); URL.revokeObjectURL(a.href);
  hlMsg('Inventory exported',true);
}

function hlImportPick(){ const f=document.getElementById('hl-import-file'); if(f) f.click(); }
async function hlImportFile(ev){
  const f=ev.target.files&&ev.target.files[0]; if(!f) return;
  try{
    const bundle=JSON.parse(await f.text());
    const r=await api('/homelab/import','POST',bundle);
    hlMsg(r.message||(r.success?'Imported':'Import failed'),r.success);
    if(r.success) loadHomelab();
  }catch(e){ hlMsg('Import failed: '+e.message,false); }
  ev.target.value='';
}
