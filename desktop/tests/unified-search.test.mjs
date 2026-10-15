import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

function setup(surface='workspace') {
  const sent=[], events=new Map(), dom=new Map(), timers=new Map(), requests=[], dispatched=[], nodes=new Map(); let tick=0;
  const node=(id)=>({id,value:'',innerHTML:'',textContent:'',dataset:{},isConnected:true,querySelectorAll(){return [];},focus(){document.activeElement=this;},setAttribute(k,v){this[k]=v;},removeAttribute(k){delete this[k];},selectionStart:0,selectionEnd:0});
  for(const id of ['unified-query','unified-results','unified-feedback','unified-types','origin']) nodes.set('#'+id,node(id));
  const origin=nodes.get('#origin'); origin.dataset={action:'nr-search'};
  const document={activeElement:origin,documentElement:{dataset:{}},querySelector:s=>nodes.get(s)||null,querySelectorAll:()=>[],addEventListener(t,fn){if(!dom.has(t))dom.set(t,[]);dom.get(t).push(fn);}};
  const settings={appEntries:[{id:'a',name:'应用'}],scenes:[{id:'s',name:'场景',enabled:true,actions:[]}],uiDensity:'compact'};
  const H={ui:{page:'library',modal:null},state:{settings:{}},actions:{},pages:{},escape:s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])),icon:()=>'<svg/>',render(){},navigate(p){H.ui.page=p;},toast(m){dispatched.push(['toast',m]);},showModal(title,body,footer){H.ui.modal={title,body,footer,opener:document.activeElement};},closeModal(){const opener=H.ui.modal?.opener;H.ui.modal=null;opener?.focus();}};
  for(const action of ['app-launch','run-scene','nr-open','nr-browse','nr-repair','edit-scene'])H.actions[action]=async el=>dispatched.push([action,structuredClone(el.dataset)]);
  const bridge={on(t,f){events.set(t,f);},request(type,payload){sent.push({type,payload});return new Promise((resolve,reject)=>requests.push({resolve,reject}));}};
  const window={Hub:H,DesktopHubNative:{bridge},DesktopHubDesktop:{getState:()=>({surface,hostState:{settings}})},DesktopHubResources:{filterTag:id=>dispatched.push(['tag',id]),browse:id=>dispatched.push(['folder',id]),repair:id=>dispatched.push(['repair',id])}};
  const context=vm.createContext({window,document,console,setTimeout(fn,ms){const id=++tick;timers.set(id,{fn,ms});return id;},clearTimeout(id){timers.delete(id);}});
  const path=new URL('../search.js',import.meta.url); if(fs.existsSync(path))vm.runInContext(fs.readFileSync(path,'utf8'),context);
  function emit(t,extra={}){const event={target:nodes.get('#unified-query'),preventDefault(){this.prevented=true;},stopImmediatePropagation(){this.stopped=true;},...extra};for(const fn of dom.get(t)||[]) {fn(event);if(event.stopped)break;}return event;}
  return {window,H,settings,sent,requests,dispatched,nodes,document,origin,events,emit,async flush(){const tasks=[...timers.values()];timers.clear();for(const {fn} of tasks)void fn();await Promise.resolve();},timers};
}
const row=(type,id,status='Active',name='名称')=>({type,id,status,name,detail:'C:/Owned/资料',favorite:false});
async function results(env,rows){await env.flush();env.requests.at(-1).resolve(rows);await Promise.resolve();await Promise.resolve();}

test('search opens in place, focuses input, closes to opener and refuses any existing modal',()=>{
  const env=setup();assert.equal(typeof env.window.DesktopHubSearch?.open,'function');assert.equal(env.window.DesktopHubSearch.open(),true);assert.equal(env.H.ui.page,'library');assert.equal(env.document.activeElement.id,'unified-query');env.H.closeModal();assert.equal(env.document.activeElement,env.origin);
  for(const locked of [false,true]){env.H.ui.modal={locked,title:'编辑器'};assert.equal(env.window.DesktopHubSearch.open(),false);assert.equal(env.H.ui.modal.title,'编辑器');}
});
test('Ctrl K consumes prototype navigation and compact windows never initialize search',()=>{
  const env=setup();const event=env.emit('keydown',{ctrlKey:true,key:'k'});assert.equal(event.stopped,true);assert.equal(env.H.ui.page,'library');assert.ok(env.H.ui.modal);assert.equal(setup('mini').window.DesktopHubSearch,undefined);
});
test('input immediately invalidates an in-flight response before its 150ms replacement starts',async()=>{
  const env=setup();env.window.DesktopHubSearch.open();await env.flush();const old=env.requests[0];env.nodes.get('#unified-query').value='微信';env.emit('input');assert.equal(env.sent.length,1);assert.ok([...env.timers.values()].some(t=>t.ms===150));old.resolve([row('file','old')]);await Promise.resolve();await Promise.resolve();assert.doesNotMatch(env.nodes.get('#unified-results').innerHTML,/data-id="old"/);await results(env,[row('file','new')]);assert.match(env.nodes.get('#unified-results').innerHTML,/data-id="new"/);
});
test('old failure and closed responses cannot overwrite current results',async()=>{
  const env=setup();env.window.DesktopHubSearch.open();await env.flush();const old=env.requests[0];env.nodes.get('#unified-query').value='new';env.emit('input');old.reject(new Error('old failure'));await Promise.resolve();await Promise.resolve();assert.doesNotMatch(env.nodes.get('#unified-feedback').textContent,/old failure/);await env.flush();env.H.closeModal();env.requests[1].resolve([row('file','closed')]);await Promise.resolve();assert.equal(env.H.ui.modal,null);
});
test('IME Enter and arrows do not execute or select, composition end searches final text',async()=>{
  const env=setup();env.window.DesktopHubSearch.open();await results(env,[row('file','first'),row('file','second')]);env.emit('compositionstart');env.emit('keydown',{key:'ArrowDown',isComposing:true});env.emit('keydown',{key:'Enter',keyCode:229});assert.equal(env.dispatched.length,0);env.nodes.get('#unified-query').value='中文';env.emit('input',{isComposing:true});env.emit('compositionend');await env.flush();assert.equal(env.sent.at(-1).payload.query,'中文');
});
test('off-page file, folder, missing resource and formal tag execute by stable IDs',async()=>{
  for(const [entity,expected] of [[row('file','outside'),'nr-open'],[row('folder','outside'),'folder'],[row('file','outside','Missing'),'repair'],[row('tag','outside'),'tag']]){const env=setup();env.window.DesktopHubSearch.open();await results(env,[entity]);env.emit('keydown',{key:'Enter'});await Promise.resolve();assert.equal(env.dispatched[0][0],expected);assert.equal(typeof env.dispatched[0][1]==='string'?env.dispatched[0][1]:env.dispatched[0][1].id,'outside');}
});
test('disabled scenes offer edit recovery, removed apps never silently launch',async()=>{
  const env=setup();env.window.DesktopHubSearch.open();await results(env,[row('scene','s','Disabled')]);env.emit('keydown',{key:'Enter'});await Promise.resolve();assert.equal(env.dispatched[0][0],'edit-scene');assert.ok(!env.dispatched.some(x=>x[0]==='run-scene'));
  const gone=setup();gone.window.DesktopHubSearch.open();await results(gone,[row('app','a')]);gone.settings.appEntries=[];gone.emit('keydown',{key:'Enter'});await Promise.resolve();assert.ok(gone.dispatched.some(x=>x[0]==='toast'));assert.ok(!gone.dispatched.some(x=>x[0]==='app-launch'));
});
test('type change invalidates immediately, failure exposes retry and unsafe names escape',async()=>{
  const env=setup();env.window.DesktopHubSearch.open();await env.flush();env.H.actions['unified-type']({dataset:{type:'file'}});env.requests[0].resolve([row('app','stale')]);await Promise.resolve();await env.flush();assert.equal(env.sent.at(-1).payload.type,'file');env.requests.at(-1).reject(new Error('磁盘忙'));await Promise.resolve();await Promise.resolve();assert.match(env.nodes.get('#unified-feedback').textContent,/磁盘忙/);env.H.actions['unified-retry']();await results(env,[row('file','safe','Active','<script>')]);assert.match(env.nodes.get('#unified-results').innerHTML,/&lt;script&gt;/);assert.doesNotMatch(env.nodes.get('#unified-results').innerHTML,/<script>/);
});

test('changing search type preserves the keyboard-focused filter button while results load',()=>{
  const env=setup();env.window.DesktopHubSearch.open();const filters=env.nodes.get('#unified-types');let html=filters.innerHTML;
  const focused={dataset:{type:'file'}};env.document.activeElement=focused;
  Object.defineProperty(filters,'innerHTML',{get:()=>html,set(value){html=value;env.document.activeElement=null;}});
  env.H.actions['unified-type']({dataset:{type:'file'}});assert.equal(env.document.activeElement,focused);
});

test('Task6 project Enter closes search before opening current project detail without launch actions', async()=>{
  const env=setup();let opened=null;
  env.window.DesktopHubProjects={open(id){assert.equal(env.H.ui.modal,null);opened=id;return true;}};
  env.window.DesktopHubSearch.open();await results(env,[row('project','project-a')]);
  env.emit('keydown',{key:'Enter'});await Promise.resolve();assert.equal(opened,'project-a');assert.equal(env.dispatched.length,0);
});

test('Task6 project metadata event invalidates an open search response',async()=>{
  const env=setup();env.window.DesktopHubSearch.open();await env.flush();const old=env.requests[0];
  env.events.get('projects.changed')?.({changed:true});old.resolve([row('project','removed')]);await Promise.resolve();await Promise.resolve();
  assert.doesNotMatch(env.nodes.get('#unified-results').innerHTML,/data-id="removed"/);await env.flush();assert.equal(env.sent.length,2);
});

test('Task4 favorite filter is a stable control, invalidates old requests and survives IME composition',async()=>{
  const env=setup();env.window.DesktopHubSearch.open();assert.match(env.H.ui.modal.body,/仅收藏的应用与文件/);
  const modal=env.H.ui.modal, checkbox={id:'unified-favorites',checked:true};env.document.activeElement=checkbox;
  await env.flush();const previous=env.requests[0];env.H.actions['unified-favorites'](checkbox);
  assert.equal(env.H.ui.modal,modal);assert.equal(env.document.activeElement,checkbox);
  previous.resolve([row('app','old')]);await Promise.resolve();await Promise.resolve();assert.doesNotMatch(env.nodes.get('#unified-results').innerHTML,/data-id="old"/);
  env.emit('compositionstart');await env.flush();assert.equal(env.sent.length,1);
  env.nodes.get('#unified-query').value='应用';env.emit('compositionend');await env.flush();assert.equal(env.sent.at(-1).payload.favoritesOnly,true);
  env.H.actions['unified-favorites']({checked:false});await env.flush();assert.equal(env.sent.at(-1).payload.favoritesOnly,false);
});
