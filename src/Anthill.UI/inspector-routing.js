/* v0.3.8.55 — INSPECTOR ROUTING (its own console asset).
 * Split out of app.js with the Models & Routing merge: the split guard holds app.js under the
 * 10,000 lines the v0.3.8.52 homelab extraction bought, and this is a coherent unit — the
 * per-role route selectors the Ant Inspector cards render. Classic script, deferred, loaded
 * after app.js; everything here is reached at call time (loadAntObs), never at parse time. */

/* v0.3.8.55 — the Models & Routing page folded into these cards: one box per role.
 *
 * Provider is ONE dropdown. A second dropdown appears only when the chosen provider offers more
 * than one usable model — which in practice means Ollama, whose installed models /routes/json
 * queries live; the agent CLIs each present a single entry and need no second choice. A current
 * route pointing at something unavailable is shown flagged, never silently offered to others.
 * Every change saves immediately through the merge-safe /routes/{role} endpoint — nothing else
 * in model_routes moves. */
let obsProvModels=new Map(), obsRoutes={};
function obsProviderLabel(p){
  if(p==='ollama') return 'Ollama (local)';
  const a=AGENT_LABEL(p); if(a) return a+' (agent)';
  return p;
}
function obsRouteControls(role, rr){
  const provs=[...obsProvModels.keys()];
  const curP=rr.provider||'ollama';
  const provList=provs.includes(curP)?provs:[curP,...provs];
  const provOpts=provList.map(p=>
    `<option value="${escapeHtml(p)}"${p===curP?' selected':''}>${escapeHtml(obsProviderLabel(p))}${provs.includes(p)?'':' ⚠'}</option>`).join('');
  const models=obsProvModels.get(curP)||[];
  const multi=models.length>1;
  const curM=rr.model||'';
  const known=models.some(m=>m.model===curM);
  const modelOpts=(known||!curM?[]:[`<option value="${escapeHtml(curM)}" selected>⚠ ${escapeHtml(curM)} (unavailable)</option>`])
    .concat(models.map(m=>`<option value="${escapeHtml(m.model)}"${m.model===curM?' selected':''}>${escapeHtml(m.model)}</option>`)).join('');
  return `<div class="ac-route" style="display:flex;gap:6px;align-items:center;flex-wrap:wrap;">
    <select class="provider-input obs-provider" data-role="${escapeHtml(role)}" aria-label="Provider for ${escapeHtml(role)}" style="font-size:10px;max-width:150px;">${provOpts}</select>
    <select class="provider-input obs-model" data-role="${escapeHtml(role)}" aria-label="Model for ${escapeHtml(role)}" style="font-size:10px;max-width:170px;" ${multi?'':'hidden'}>${modelOpts}</select>
    <span class="save-msg" data-obs-msg="${escapeHtml(role)}"></span>
  </div>`;
}
function wireObsRouting(grid){
  const save=async(role,provider,model,msgEl)=>{
    const res=await api('/routes/'+encodeURIComponent(role),'POST',{provider,model});
    if(msgEl){ msgEl.textContent=(res&&res.success)?'Saved':((res&&res.message)||'Save failed.');
               msgEl.className='save-msg '+(res&&res.success?'text-green':'text-red'); }
    // The next stats read must see the new route, not a TTL'd copy of the old one.
    apiCacheBust('/ants/stats'); apiCacheBust('/routes');
  };
  grid.querySelectorAll('.obs-provider').forEach(sel=>sel.addEventListener('change',()=>{
    const role=sel.dataset.role, p=sel.value;
    const models=obsProvModels.get(p)||[];
    const mSel=grid.querySelector(`.obs-model[data-role="${CSS.escape(role)}"]`);
    if(mSel){
      mSel.hidden=models.length<=1;
      mSel.innerHTML=models.map((m,i)=>`<option value="${escapeHtml(m.model)}"${i===0?' selected':''}>${escapeHtml(m.model)}</option>`).join('');
    }
    save(role,p,models.length?models[0].model:'',grid.querySelector(`[data-obs-msg="${CSS.escape(role)}"]`));
  }));
  grid.querySelectorAll('.obs-model').forEach(sel=>sel.addEventListener('change',()=>{
    const role=sel.dataset.role;
    const p=grid.querySelector(`.obs-provider[data-role="${CSS.escape(role)}"]`)?.value||'ollama';
    save(role,p,sel.value,grid.querySelector(`[data-obs-msg="${CSS.escape(role)}"]`));
  }));
}

// v0.3.8.50 (field report §20): the profile editor — a name and a color, saved to the real
// /ants/{id}/profile endpoint, applied on the next render of every surface that draws the ant.
// "Reset" clears the override; the registry identity was never touched.
let antProfiles={};
function wireAntProfileEditors(scope){
  scope.querySelectorAll('[data-ant-edit]').forEach(b=>b.addEventListener('click',()=>{
    const ant=b.dataset.antEdit;
    const slot=scope.querySelector(`[data-ant-slot="${CSS.escape(ant)}"]`);
    if(!slot) return;
    if(!slot.hidden){ slot.hidden=true; slot.innerHTML=''; return; }
    const prof=antProfiles[ant]||{};
    slot.innerHTML=`<div style="display:flex;gap:6px;align-items:center;padding:6px 0;flex-wrap:wrap;">
      <input class="provider-input ap-name" maxlength="40" placeholder="Display name" value="${escapeHtml(prof.display_name||'')}" style="width:150px;font-size:10px;">
      <input class="ap-color" type="color" value="${/^#[0-9a-fA-F]{6}$/.test(prof.color||'')?prof.color:'#7fa0bc'}" title="Ant color" style="width:34px;height:26px;padding:0;border:1px solid var(--border);background:var(--inner);border-radius:4px;">
      <button class="btn btn-primary ap-save" style="font-size:10px;">Save</button>
      <button class="btn btn-ghost ap-reset" style="font-size:10px;" title="Back to the registry name and color">Reset</button>
      <span class="save-msg ap-msg" style="font-size:10px;"></span></div>`;
    slot.hidden=false;
    const msg=slot.querySelector('.ap-msg');
    const done=async(r)=>{ if(r&&r.success){ await loadAntObs(); } else { msg.textContent=(r&&r.message)||'Failed'; msg.style.color='var(--red)'; } };
    slot.querySelector('.ap-save').addEventListener('click',async ()=>{
      done(await api('/ants/'+encodeURIComponent(ant)+'/profile','POST',{
        display_name:slot.querySelector('.ap-name').value.trim(),
        color:slot.querySelector('.ap-color').value,
      }));
    });
    slot.querySelector('.ap-reset').addEventListener('click',async ()=>{
      done(await api('/ants/'+encodeURIComponent(ant)+'/profile','POST',{display_name:'',color:''}));
    });
  }));
}

/* ---- the ant-config globals: options builders, the colony-wide priority + orchestration
 * panel (renderAntConfigGlobals/openAntConfig), and the save/reset handlers. Moved from app.js
 * with the Models & Routing merge — this file IS the inspector/routing domain. All state they
 * touch (uiState, modelRoutes, availableModels, antcfgCatalog…) stays declared in app.js and is
 * reached at call time through the shared global scope. ---- */

function antcfgModelOptions(provider,curModel){
  const models=provider==='ollama'?availableModels:((antcfgCatalog.find(p=>p.provider===provider)||{}).models||[]);
  // v0.3.8.52: escaped, in a quoted attribute and in text.
  //
  // Two sources feed `models`, and only one of them is ours. `antcfgCatalog` is ProviderCatalog.All,
  // a hardcoded list in the SDK — safe today, and safe only for as long as it stays hardcoded.
  // `availableModels` is the tag list read back from a LOCAL OLLAMA over HTTP, which is a separate
  // process serving whatever names its models were pulled under. A `"` in one of those ends the
  // attribute value and everything after it parses as markup.
  //
  // Escaping both rather than only the Ollama branch: which array a value came from is not visible
  // at this line, and a rule that depends on remembering that distinction is one edit from being
  // wrong. `curModel` is escaped for the same reason — it is a stored config value.
  const opts=models.map(m=>`<option value="${escapeHtml(m)}"${m===curModel?' selected':''}>${escapeHtml(m)}</option>`).join('');
  const extra=curModel&&!models.includes(curModel)?`<option value="${escapeHtml(curModel)}" selected>${escapeHtml(curModel)} (current)</option>`:'';
  return `<option value="">— default —</option>${opts}${extra}`;
}

function antcfgProviderOptions(curProvider, opts){
  opts=opts||{};
  let providers=[{provider:'ollama',name:'Ollama (local)'},...antcfgCatalog];
  // v0.3.8.49 (§4): Ollama is NOT a user-facing Chat provider. It stays fully available to ants — every
  // other role below still lists it — but the `conversation` role that speaks in Chat must route to
  // a real provider (a keyed API or an installed agent), so it is dropped from that one dropdown.
  // Chat provider configuration and ant execution infrastructure are deliberately separated here.
  if(opts.excludeOllama) providers=providers.filter(p=>p.provider!=='ollama');
  const isOllamaNow=(curProvider||'ollama')==='ollama';
  let html='';
  // If the stored chat route is still Ollama (or unset), show a selected placeholder so the operator
  // SEES they must pick a chat provider, rather than the select quietly showing something as active.
  if(opts.excludeOllama && isOllamaNow)
    html+=`<option value="" selected disabled>— choose a chat provider —</option>`;
  html+=providers.map(p=>{
    const connected=p.provider==='ollama'||antcfgConfigured.has(p.provider);
    const label=connected?p.name:`${p.name} (not connected)`;
    const selected=(!(opts.excludeOllama&&isOllamaNow) && p.provider===(curProvider||'ollama'))?' selected':'';
    return `<option value="${p.provider}"${selected}>${label}</option>`;
  }).join('');
  return html;
}

/**
 * v3.8.1 — the colony-wide priority model, and the roles that are not ants.
 *
 * Two gaps, one section. The caste grid below is built from the ant ROSTER, so `planner` and
 * `strategist` — which make model calls but are not ants — had no control anywhere in the console.
 * A colony whose planner model had gone missing fell back to a static task plan and there was
 * nowhere to repoint it. `fallback` had the same problem while being the route every unrouted role
 * silently used.
 *
 * The priority is stated as what it DOES rather than as a toggle: "every ant tries this first". A
 * checkbox labelled "priority" would leave an operator guessing whether it outranks the per-ant
 * routes below it, which is the only question that matters here.
 */
var ORCHESTRATION_ROLES = [
  { id:'planner',    label:'Planner',    why:'Turns a goal into the task plan. If this model is missing the colony silently falls back to a static plan.' },
  { id:'strategist', label:'Strategist', why:'Adaptive mission control — decides whether to replan mid-mission.' },
  // v0.3.8.49 (§4): who speaks for the colony in Chat. A real provider only — a keyed API or an
  // installed agent — NOT Ollama, which stays an ant-side backend rather than a chat voice.
  { id:'conversation', label:'Conversation', why:'Answers chat turns. Route it to a keyed API or an installed agent — the colony’s voice in Chat. (Ollama stays available to ants, not here.)' },
  { id:'fallback',   label:'Fallback',   why:'Used by any role with no route of its own, and when a preferred route is unhealthy.' },
];

function renderAntConfigGlobals(routes, priorityProvider, priorityModel){
  const host=document.getElementById('antcfg-global');
  if(!host) return;

  const active = !!(priorityProvider && priorityModel);

  host.innerHTML = `
    <div class="antcfg-card" style="margin-bottom:14px">
      <div style="font-size:13px;font-weight:700;margin-bottom:2px">Colony-wide priority model</div>
      <div style="font-size:10px;color:var(--muted);line-height:1.45;margin-bottom:8px">
        When set, <strong>every ant tries this model first</strong>, whatever its own route says below.
        Each ant's own route is kept and is what the colony falls back to if this model is unhealthy —
        clearing this restores every per-ant choice exactly as it was.
      </div>
      <div class="antcfg-field">
        <label>Provider</label>
        <select id="antcfg-prio-provider" class="antcfg-model">${antcfgProviderOptions(priorityProvider||'ollama')}</select>
      </div>
      <div class="antcfg-field">
        <label>Model</label>
        <select id="antcfg-prio-model" class="antcfg-model" data-provider="${escapeHtml(priorityProvider||'ollama')}">
          ${antcfgModelOptions(priorityProvider||'ollama', priorityModel||'')}
        </select>
      </div>
      <div style="font-size:10px;margin-top:4px;color:${active?'var(--queen)':'var(--dim)'}">
        ${active
          ? 'Active — every ant tries '+escapeHtml(priorityModel)+' first.'
          : 'Not set — each ant uses its own route below. Choose a model and Save to promote it.'}
      </div>
    </div>

    <div class="antcfg-grid" style="margin-bottom:14px">
      ${ORCHESTRATION_ROLES.map(r=>{
        const p=routes[r.id]?.provider||'ollama', m=routes[r.id]?.model||'';
        // v0.3.8.49 (§4): the conversation (chat) role hides Ollama; every other orchestration role
        // keeps it, because Ollama is legitimate ant-side infrastructure.
        const chatRole=r.id==='conversation';
        return `<div class="antcfg-card">
          <div style="font-size:13px;font-weight:700">${escapeHtml(r.label)}</div>
          <div class="antcfg-role">orchestration · ${escapeHtml(r.id)}</div>
          <div style="font-size:10px;color:var(--muted);line-height:1.45;margin:6px 0 8px">${escapeHtml(r.why)}</div>
          <div class="antcfg-field">
            <label>Provider</label>
            <select data-caste="${r.id}" class="antcfg-model antcfg-provider" aria-label="Model provider for ${escapeHtml(r.label)}">${antcfgProviderOptions(p,{excludeOllama:chatRole})}</select>
          </div>
          <div class="antcfg-field">
            <label>Model (route)</label>
            <select data-caste="${r.id}" class="antcfg-model antcfg-modelname" data-provider="${escapeHtml(p)}" aria-label="Model route for ${escapeHtml(r.label)}">
              ${antcfgModelOptions(p, m)}
            </select>
          </div>
        </div>`;
      }).join('')}
    </div>`;

  // The priority model list follows its provider, the same way the per-caste pair does. Without
  // this, switching provider leaves a model list from the previous one and the operator picks a
  // model that provider has never heard of.
  const provEl=document.getElementById('antcfg-prio-provider');
  const modelEl=document.getElementById('antcfg-prio-model');
  if(provEl && modelEl) provEl.addEventListener('change',()=>{
    modelEl.dataset.provider=provEl.value;
    modelEl.innerHTML=antcfgModelOptions(provEl.value,'');
  });
}

async function openAntConfig(){
  // Data-loader only — do NOT call showPage from here. Invoked from PAGE_ENTER['antobs']
  // (v0.3.8.55: the merged inspector page; showPage() calls it *after* switching) and from the
  // "Reset" button below (where the page is already active). Calling showPage from in here used
  // to re-fire the page-enter hook -> openAntConfig() -> showPage() in an unbounded
  // mutual-recursion loop, blowing the call stack every single time this page was opened.
  await Promise.all([fetchModelNames(),fetchProviderCatalog()]);
  await ensureAntRouteCatalog();
  const routes=modelRoutes;

  // v3.8.1: the priority model and the non-ant roles, above the caste grid.
  let prioProvider='', prioModel='';
  try{
    const s=await api('/settings');
    if(s&&s.success){ prioProvider=s.data.model_priority_provider||''; prioModel=s.data.model_priority_model||''; }
  }catch{}
  renderAntConfigGlobals(routes, prioProvider, prioModel);

  // v0.3.8.55: the per-caste configuration grid is GONE — its name/colour lives in the
  // inspector's ✎ profile editor and its provider/model pair in each inspector card's route
  // selectors (inspector-routing.js). This renders only what sits ABOVE the inspector grid.
}

// v0.3.8.55: loadRoleRouting (the Models & Routing page's flat per-role selector list) is gone —
// its merge-safe /routes/{role} saves now live in each Ant Inspector card's own route selectors
// (wireObsRouting), and there is no PAGE_ENTER['antconfig']: showPage remaps 'antconfig' to
// 'antobs' before the hook lookup.

document.getElementById('antcfg-save').addEventListener('click',async()=>{
  const msg=document.getElementById('antcfg-msg'); msg.textContent='';
  document.querySelectorAll('.antcfg-name').forEach(i=>{const c=i.dataset.caste,v=i.value.trim();if(v)uiState.castes[c]=Object.assign({},uiState.castes[c],{name:v});});
  document.querySelectorAll('.antcfg-color').forEach(i=>{const c=i.dataset.caste;uiState.castes[c]=Object.assign({},uiState.castes[c],{color:i.value});});
  const routeUpdate={};
  // v3.8.1: orchestration roles share this control but are NOT castes — they have no node, colour
  // or display name, and writing them into uiState.castes would invent visual state for something
  // that never appears on the colony map.
  const orchestration=new Set(ORCHESTRATION_ROLES.map(r=>r.id));
  document.querySelectorAll('.antcfg-modelname').forEach(s=>{
    const c=s.dataset.caste,v=s.value,provider=s.dataset.provider||'ollama';
    if(!orchestration.has(c))
      uiState.castes[c]=Object.assign({},uiState.castes[c],{model:v||undefined,provider:provider!=='ollama'?provider:undefined});
    if(v) routeUpdate[c]={provider,model:v};
  });
  applyUiState();saveUiState();

  // v3.8.1: the priority model. Posted even when EMPTY, because clearing it is a real operator
  // decision — "stop promoting that model" has to be expressible, and a save that only ever wrote
  // non-empty values would make the priority impossible to turn off from the console.
  try{
    const p=document.getElementById('antcfg-prio-provider'), m=document.getElementById('antcfg-prio-model');
    if(p&&m){
      const r=await api('/settings','POST',{
        model_priority_provider: m.value ? p.value : '',
        model_priority_model: m.value || '',
      });
      if(!r||!r.success) throw new Error((r&&r.message)||'priority update rejected');
    }
  }catch(e){ msg.style.color='var(--red)'; msg.textContent='Priority failed: '+e.message; return; }

  if(Object.keys(routeUpdate).length){
    // v2.14.13: merge, never replace. This used to post ONLY the castes rendered on this page,
    // which reset every route it omitted (strategist, fallback, and any caste with no model
    // selected) back to the profile default.
    try{await saveModelRoute(routeUpdate);}
    catch(e){msg.style.color='var(--red)';msg.textContent='Routes failed: '+e.message;return;}
  }
  msg.style.color='var(--green)';msg.textContent='Saved ✓';setTimeout(()=>msg.textContent='',2500);
});

document.getElementById('antcfg-reset').addEventListener('click',async()=>{
  if(!await uiConfirm('Reset all ant names, colours and positions to defaults?')) return;
  uiState.castes={};uiState.positions={};applyUiState();buildNodes();saveUiState();openAntConfig();
});
