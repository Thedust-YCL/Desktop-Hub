import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
function setup() {
  const calls=[], content={scrollTop:12,scrollLeft:3}; let pending=0;
  const H={ui:{page:'library'},render(){calls.push('render');},navigate(page){this.ui.page=page;},toast(){}};
  const bridge={get pendingCount(){return pending;},async request(type){calls.push(type);return {settings:{scenes:[]}};}};
  const window={Hub:H,DesktopHubNative:{bridge},DesktopHubDesktop:{applyHostState(){calls.push('fresh');},getState(){return {surface:'workspace',hostState:{settings:{}}};},lifecycleBusyReasons:()=>[]},
  DesktopHubResources:{captureLifecycle:()=>({query:'notes',tagIds:['tag-a'],sort:'name'}),lifecycleBusyReasons:()=>[],quiesceLifecycle(){calls.push('quiesce');},async restoreLifecycle(value){calls.push('query:'+value.query);content.scrollTop=0;}},
  requestAnimationFrame:fn=>fn()};
  const document={activeElement:null,querySelector:()=>content};const path=new URL('../lifecycle.js',import.meta.url);
  if(fs.existsSync(path))vm.runInNewContext(fs.readFileSync(path,'utf8'),{window,document,TextEncoder,JSON,Number,Promise,setTimeout,clearTimeout});
  return {window,H,calls,content,pending(value){pending=value;},api:window.DesktopHubLifecycle};
}
test('Task8 hide captures safe navigation and reports every unsaved/modal/pending state',async()=>{
  const env=setup();assert.equal(typeof env.api?.prepareHide,'function','lifecycle handshake exists');
  let result=await env.api.prepareHide(4);assert.equal(result.generation,4);assert.deepEqual(Array.from(result.busyReasons),[]);assert.equal(result.snapshot.resources.query,'notes');assert.equal(result.snapshot.scroll.top,12);
  env.H.ui.modal={locked:false};env.H.ui.sceneDraftDirty=true;env.pending(1);result=await env.api.prepareHide(5);assert.ok(result.busyReasons.includes('modal'));assert.ok(result.busyReasons.includes('scene-draft'));assert.ok(result.busyReasons.includes('native-request'));
});
test('Task8 restore fetches fresh state and awaits resource query before restoring scroll; explicit page wins',async()=>{
  const env=setup();assert.equal(typeof env.api?.restore,'function');const snap={version:1,page:'library',resources:{query:'notes'},scroll:{top:99,left:7}};
  const result=await env.api.restore(snap,'settings');assert.equal(result.ready,true);assert.equal(env.H.ui.page,'settings');assert.equal(env.content.scrollTop,99);assert.equal(env.content.scrollLeft,7);assert.ok(env.calls.indexOf('fresh')<env.calls.indexOf('query:notes'));
});
test('Task8 newer resume invalidates an in-flight restore and never overwrites its scroll',async()=>{
  const env=setup();assert.equal(typeof env.api?.resume,'function');let release;env.window.DesktopHubResources.restoreLifecycle=()=>new Promise(r=>release=r);
  const task=env.api.restore({version:1,page:'library',resources:{query:'old'},scroll:{top:900,left:0}});for(let i=0;i<5;i++)await Promise.resolve();env.api.resume(9);release();const result=await task;assert.equal(result.ready,false);assert.notEqual(env.content.scrollTop,900);
});
test('Task8 invalid or action-bearing snapshots are refused without requests',async()=>{
  const env=setup();assert.equal(typeof env.api?.restore,'function');for(const snap of [{version:2,page:'home'},{version:1,page:'home',launch:{id:'x'}},{version:1,page:'library',resources:{folder:{path:'C:/private',token:'old'}}}])assert.equal((await env.api.restore(snap)).ready,false);assert.equal(env.calls.length,0);
});
