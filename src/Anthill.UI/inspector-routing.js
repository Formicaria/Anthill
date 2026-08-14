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
