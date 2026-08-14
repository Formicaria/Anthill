/* v0.3.8.55 — CONSOLE EXTRAS (its own asset, under the app.js size guard's split rule).
 *
 * Post-split additions that are not core shell live here: the Automation Director's dashboard
 * widget and the issue-report composer. Classic script, deferred, loaded after app.js; the
 * widget renderer is reached at page-enter/poll time and the report handler at click time —
 * nothing here runs against app.js globals at parse time except the interval registration. */

/* v0.3.8.55 (operator's correction) — the Director widget hosts the REAL status card. The first
 * pass rendered a glance-sized duplicate with its own ids while the full card stayed on
 * Projects; the ask was a MOVE. The card's markup — auto-* ids, Start/Stop, kpi grid, kill
 * switch — now lives inside #ov-director-body on the overview (authored in index.html), the
 * existing reloadAutonomyStatus fills it, and the existing button handlers drive it. This
 * poller just keeps it fresh while the overview is open. */
async function pollDirectorWidget(){
  if(!document.getElementById('ov-director-body')) return;
  if(!document.getElementById('page-overview')?.classList.contains('active')) return;
  if(typeof reloadAutonomyStatus==='function'){ try{ await reloadAutonomyStatus(); }catch{} }
}
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
