/* Non-authorizing in-memory navigation state; native owns the final release decision. */
(function () {
  'use strict';
  const H = window.Hub, desktop = window.DesktopHubDesktop, bridge = window.DesktopHubNative.bridge;
  let epoch = 0, restoring = false;
  const pages = new Set('home scenes editor library tags favorites search settings activity projects project folder missing sources suggestions cleanup'.split(' '));
  const object = (v, keys) => v && typeof v === 'object' && !Array.isArray(v) && Object.keys(v).every(key => keys.split(' ').includes(key));
  const text = (v, max = 96) => typeof v === 'string' && v.length <= max && !/[\u0000-\u001f]/.test(v);
  const number = v => Number.isFinite(v) && v >= 0 && v <= 10000000;
  const strings = (v, max) => Array.isArray(v) && v.length <= max && v.every(x => text(x));
  const optional = (v, key, check) => !(key in v) || check(v[key]);
  const resources = v => object(v,'query tagIds view page status category extension sort filterId favoritesOnly folderResourceId') && Object.entries(v).every(([key,value]) => key === 'query' ? text(value,512) : key === 'tagIds' ? strings(value,100) : key === 'page' ? Number.isInteger(value) && value >= 0 && value <= 1000000 : key === 'favoritesOnly' ? typeof value === 'boolean' : key === 'view' ? ['list','grid','tags'].includes(value) : key === 'sort' ? ['name','type','modifiedAsc','modifiedDesc'].includes(value) : text(value));
  const projects = v => object(v,'projectId') && optional(v,'projectId',x=>x===null||text(x));
  const scenes = v => object(v,'sceneId actionId selectedExecutionId expandedResultIds resultOpen') && Object.entries(v).every(([key,value])=>key==='expandedResultIds'?strings(value,32):key==='resultOpen'?typeof value==='boolean':text(value));
  const scroll = v => object(v,'top left') && Object.values(v).every(number);
  const history = v => object(v,'entries index') && Array.isArray(v.entries) && v.entries.length <= 32 && Number.isInteger(v.index) && v.index >= -1 && v.index < v.entries.length && v.entries.every(e=>object(e,'page resources projects scroll')&&pages.has(e.page)&&optional(e,'resources',resources)&&optional(e,'projects',projects)&&optional(e,'scroll',number));
  function valid(v) {
    if (!object(v,'version page resources history scenes projects scroll') || v.version !== 1 || !pages.has(v.page) || !optional(v,'resources',resources)||!optional(v,'projects',projects)||!optional(v,'scenes',scenes)||!optional(v,'scroll',scroll)||!optional(v,'history',history)) return false;
    return new TextEncoder().encode(JSON.stringify(v)).length <= 65536;
  }
  function busyReasons() {
    const reasons=[];
    if (H.ui.modal) reasons.push('modal');
    if (H.ui.sceneDraftDirty) reasons.push('scene-draft');
    if (typeof bridge.pendingCount === 'function' ? bridge.pendingCount() > 0 : bridge.pendingCount > 0) reasons.push('native-request');
    if (desktop.isImporting?.()) reasons.push('app-import');
    if (window.DesktopHubOnboarding?.getState?.()?.active) reasons.push('onboarding');
    for (const module of [desktop,window.DesktopHubResources,window.DesktopHubProjects,window.DesktopHubScenes]) {
      if (module?.lifecycleBusyReasons) reasons.push(...module.lifecycleBusyReasons());
    }
    // Change-bound text fields may still contain values absent from their saved model.
    const active = document.activeElement;
    if (active?.matches?.('input,textarea,[contenteditable="true"]') && active.dataset?.change && active.value !== active.defaultValue) reasons.push('uncommitted-input');
    return [...new Set(reasons)].slice(0,32);
  }
  function capture() {
    const content = document.querySelector('.workspace-content');
    const snapshot={version:1,page:H.ui.page,scroll:{top:content?.scrollTop||0,left:content?.scrollLeft||0}};
    for(const [key,module] of [['resources',window.DesktopHubResources],['projects',window.DesktopHubProjects],['scenes',window.DesktopHubScenes],['history',window.DesktopHubInteractions]]) {
      const value=module?.captureLifecycle?.();if(value!==undefined)snapshot[key]=value;
    }
    return valid(snapshot)?snapshot:null;
  }
  function prepareHide(generation) {
    ++epoch;
    window.DesktopHubResources?.quiesceLifecycle?.(generation);
    const reasons=busyReasons(), snapshot=capture();
    if(!snapshot)reasons.push('unknown-snapshot');
    return {version:1,generation,quiesced:true,busyReasons:reasons,snapshot};
  }
  const frame = () => new Promise(resolve => (window.requestAnimationFrame || (fn=>setTimeout(fn,0)))(resolve));
  async function restore(snapshot,pageOverride) {
    if(snapshot!==null&&snapshot!==undefined&&!valid(snapshot))return {ready:false};
    const current=++epoch;restoring=true;
    try {
      const state=await bridge.request('app.getState',{});if(current!==epoch)return {ready:false};
      desktop.applyHostState(state);
      const workspace=desktop.getState().surface==='workspace';
      if(workspace&&snapshot) {
        window.DesktopHubScenes?.restoreLifecycle?.(snapshot.scenes||{});
        const projectRestoration = snapshot.projects ? window.DesktopHubProjects?.restoreNavigation?.({...snapshot.projects,scroll:snapshot.scroll?.top}) : null;
        const page=pages.has(pageOverride)?pageOverride:snapshot.page;
        H.navigate(page==='editor'&&!H.ui.sceneDraft?'scenes':page);
        if(snapshot.resources)await window.DesktopHubResources?.restoreLifecycle?.(snapshot.resources,{generation:current});
        await projectRestoration;
        if(current!==epoch)return {ready:false};
        if(snapshot.history)window.DesktopHubInteractions?.restoreLifecycle?.(snapshot.history);
      }else if(workspace) {
        if(pages.has(pageOverride))H.navigate(pageOverride);
        const resourceState=window.DesktopHubResources?.captureLifecycle?.();
        if(resourceState)await window.DesktopHubResources?.restoreLifecycle?.(resourceState,{generation:current});
        if(['home','projects','project'].includes(H.ui.page))await window.DesktopHubProjects?.refresh?.();
      }
      if(workspace&&['home','activity'].includes(H.ui.page))await window.DesktopHubActivity?.refresh?.();
      if(current!==epoch)return {ready:false};
      H.render();await frame();await frame();if(current!==epoch)return {ready:false};
      const content=workspace?document.querySelector('.workspace-content'):null;if(content&&snapshot?.scroll){content.scrollTop=snapshot.scroll.top||0;content.scrollLeft=snapshot.scroll.left||0;}
      if(workspace)window.DesktopHubInteractions?.checkpoint?.();return {ready:true};
    }finally{if(current===epoch)restoring=false;}
  }
  function resume(generation) {
    ++epoch;restoring=false;
    const resources=window.DesktopHubResources;
    if(resources?.resumeLifecycle)void resources.resumeLifecycle(generation);
    else void resources?.resumeQueries?.();
    return true;
  }
  window.DesktopHubLifecycle=Object.freeze({prepareHide,restore,resume,ready:()=>restore(null),busyReasons,isRestoring:()=>restoring,validateSnapshot:valid});
}());

