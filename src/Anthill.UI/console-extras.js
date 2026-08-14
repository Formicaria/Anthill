/* v0.3.8.55 — CONSOLE EXTRAS (its own asset, under the app.js size guard's split rule).
 *
 * Post-split additions that are not core shell live here: the Automation Director's dashboard
 * widget and the issue-report composer. Classic script, deferred, loaded after app.js; the
 * widget renderer is reached at page-enter/poll time and the report handler at click time —
 * nothing here runs against app.js globals at parse time except the interval registration. */

/* v0.3.8.55 — the Director widget's own renderer. Deliberately NOT the auto-* ids: those are the
 * Projects page's Automation panel, and two surfaces writing one set of singleton ids is how a
 * hidden panel eats a visible panel's update. Same endpoint, own markup, glance-sized. */
async function pollDirectorWidget(){
  const el=document.getElementById('ov-director-body'); if(!el) return;
  if(!document.getElementById('page-overview')?.classList.contains('active')) return;
  try{
    const r=await api('/autonomy/status'); if(!(r&&r.success)) return;
    const s=r.data||{};
    const state=s.running?'RUNNING':(s.enabled?'IDLE':'OFF');
    const col=s.running?'var(--green)':(s.enabled?'var(--queen)':'var(--dim)');
    const kill=!!s.kill_switch_engaged;
    el.innerHTML=`<div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap;padding:2px 0 8px;">
        <span style="font-size:16px;font-weight:800;color:${col};">${state}</span>
        <span style="font-size:10px;color:${kill?'var(--red)':'var(--dim)'};">${kill?'kill switch engaged':'kill switch clear'}</span>
        <span style="flex:1;"></span>
        <button class="btn btn-primary" data-onclick="dirWidgetStart()" ${(!s.enabled||s.running)?'disabled':''} style="font-size:10px;">▶ Start</button>
        <button class="btn btn-danger" data-onclick="dirWidgetStop()" ${s.running?'':'disabled'} style="font-size:10px;">■ Stop</button>
      </div>
      <div class="info-row"><span class="info-key">Missions / hour</span><span class="info-val">${s.missions_last_hour??0}/${s.max_missions_per_hour??'—'}</span></div>
      <div class="info-row"><span class="info-key">Missions / day</span><span class="info-val">${s.missions_last_day??0}/${s.max_missions_per_day??'—'}</span></div>
      <div class="info-row"><span class="info-key">Backlog</span><span class="info-val">${s.backlog_pending??0} pending · ${s.backlog_active??0} active</span></div>
      <div class="info-row"><span class="info-key">Next objective</span><span class="info-val">${escapeHtml(s.next_objective?s.next_objective.title:'— backlog empty —')}</span></div>
      <div style="margin-top:8px;"><button class="btn btn-ghost" data-onclick="go('/projects')" style="font-size:10px;">Open Automation →</button></div>`;
  }catch{}
}
function dirWidgetStart(){ api('/autonomy/start','POST').then(()=>{ apiCacheBust('/autonomy'); pollDirectorWidget(); }); }
function dirWidgetStop(){ api('/autonomy/stop','POST').then(()=>{ apiCacheBust('/autonomy'); pollDirectorWidget(); }); }
setInterval(pollDirectorWidget, 10000);

/* v0.3.8.55 (field report) — issue reports route to info@formicaria.us by mailto:. The version
 * and deployment ride along (they are the two questions every first reply asks); the description
 * is the operator's own words, URL-encoded and bounded by the mailto: length the OS will take. */
document.getElementById('report-send')?.addEventListener('click',()=>{
  const cat=document.getElementById('report-category')?.value||'Bug report';
  const desc=(document.getElementById('report-desc')?.value||'').trim();
  const ver=(lastSystemSummary&&lastSystemSummary.version)||'unknown';
  const subject=`[ANTHILL ${cat}] v${ver}`;
  const body=`${desc}\n\n—\nANTHILL v${ver}\nCategory: ${cat}`;
  location.href='mailto:info@formicaria.us?subject='+encodeURIComponent(subject)
    +'&body='+encodeURIComponent(body.slice(0,1800));
});
