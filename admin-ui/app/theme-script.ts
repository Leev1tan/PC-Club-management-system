// Runs in <head> before the first paint (app/layout.tsx): sets <html data-theme data-mode> and the owner accent from the
// copy that app/theme.tsx keeps in localStorage, so a page load never shows another theme first. The admin UI uses the
// staff member's own mode (ccms.mode); the public booking page /book always shows the default mode of the club theme.
// Keep it small and without dependencies: it is inlined as text.
export const themeBootScript = `(function(){try{
var d=document.documentElement,T=['cyber','pro-dark','light','arcade'],D={cyber:'dark','pro-dark':'dark',light:'light',arcade:'dark'};
var v=JSON.parse(localStorage.getItem('ccms.theme')||'null')||{};
var t=T.indexOf(v.theme)>=0?v.theme:'cyber';
var m=/^\\/book(\\/|\\.html)?$/.test(location.pathname)?null:localStorage.getItem('ccms.mode');
if(m!=='dark'&&m!=='light')m=D[t];
d.setAttribute('data-theme',t);d.setAttribute('data-mode',m);
var x=v.vars&&v.vars[m];
if(x&&/^#[0-9A-Fa-f]{6}$/.test(v.accent||''))for(var k in x)if(/^--accent(-contrast|-text)?$/.test(k)&&/^#[0-9A-Fa-f]{6}$/.test(x[k]))d.style.setProperty(k,x[k]);
}catch(e){}})();`;
