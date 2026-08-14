/* v0.3.8.55 — THEMES (its own console asset, under the app.js size guard's split rule).
 *
 * The palette is a set of CSS variables and a theme re-states them on html[data-theme]; nothing
 * else changes, because components name variables, never raw colors. Saved per device
 * (localStorage), applied at PARSE time below — deferred scripts all run before DOMContentLoaded,
 * so the saved theme lands before first meaningful paint. Deliberately NOT an inline head script:
 * the console's CSP is script-src 'self' with no unsafe-inline, and an inline snippet would be
 * silently blocked in the served console while appearing to work from a source checkout.
 * 'default' is the website's own palette and sets no attribute at all. */
const THEME_IDS=['default','light','hermes','contrast'];
function applyTheme(t){
  if(THEME_IDS.indexOf(t)<0) t='default';
  if(t==='default') delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme=t;
}
(function(){
  let cur='default';
  try{ cur=localStorage.getItem('anthill-theme')||'default'; }catch{}
  if(THEME_IDS.indexOf(cur)<0) cur='default';
  applyTheme(cur);   // the load-time application — the head carries no script to do it
  const sel=document.getElementById('settings-theme'); if(!sel) return;
  sel.value=cur;
  sel.addEventListener('change',()=>{
    applyTheme(sel.value);
    try{ localStorage.setItem('anthill-theme',sel.value); }catch{}
  });
})();
