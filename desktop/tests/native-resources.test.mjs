import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { randomUUID } from 'node:crypto';
const source = fs.readFileSync(new URL('../resources.js', import.meta.url),'utf8');
function setup(surface='workspace') {
  const sent=[], events=new Map(), toasts=[], nodes=new Map(), responses=new Map();
  const tag={id:'tag',name:'主题',tagType:'theme'};
  const data={items:[{resource:{id:'resource',canonicalPath:'C:\\Owned\\课程.txt',displayName:'课程.txt',resourceType:'file',extension:'.txt',status:'Active',favorite:false,creationTime:new Date().toISOString(),modificationTime:new Date().toISOString()},tags:[tag],suggestions:[{id:'suggestion',suggestedName:'2026年10月',suggestionBasis:'创建时间',status:'Pending'}]}],total:1,page:0,pageSize:60,sources:[],tags:[tag]};
  let failure=null;
  const H={state:{files:[{id:'demo'}],tags:[{id:'demo'}],sources:[{path:'demo'}]},ui:{page:'library'},pages:{},actions:{},
    escape:value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])),icon:()=>'<svg></svg>',render(){nodes.delete('.usability-conditions');},navigate(next){H.ui.page=next;H.closeModal();H.render();},toast(message){toasts.push(message);},showModal(title,body,footer){H.ui.modal={title,body,footer};},closeModal(){H.ui.modal=null;}};
  const document={activeElement:null,documentElement:{dataset:{}},querySelector:selector=>nodes.get(selector)||null,querySelectorAll(){return [];},createElement(){return {innerHTML:'',className:''};},addEventListener(){}};
  const bridge={on(type,callback){events.set(type,callback);},async request(type,payload){sent.push({type,payload:structuredClone(payload)});if(responses.has(type))return responses.get(type)(payload);if(failure)throw new Error(failure);if(type==='resources.query')return structuredClone(data);if(type==='resources.preview')return {token:payload.operationId,path:'C:\\Owned',files:1,folders:0,recursive:payload.recursive,warnings:[],cloudPath:false};if(type==='resources.browse')return {token:'browse',path:'C:\\Owned\\sub',items:[],total:0,page:0,warnings:[]};return {saved:true};}};
  const window={Hub:H,crypto:{randomUUID},DesktopHubNative:{bridge},DesktopHubDesktop:{getState:()=>({surface})}};
  const context=vm.createContext({window,document,setTimeout,clearTimeout,queueMicrotask,Date,console});vm.runInContext(source,context);
  return {H,sent,data,window,toasts,events,nodes,respond(type,handler){responses.set(type,handler);},load(name){vm.runInContext(fs.readFileSync(new URL('../'+name,import.meta.url),'utf8'),context);},fail(message){failure=message;}};
}
const click=id=>({dataset:{id}});

for(const operation of ['tag','folder']) for(const intent of ['editor','import','search','page','closed-modal','page-back']) {
  test(`deferred ${operation} navigation respects newer ${intent} intent`,async()=>{
    const env=setup();await env.window.DesktopHubResources.reload();env.H.navigate('home');let resolve;const waiting=new Promise(done=>{resolve=done;});env.respond(operation==='tag'?'resources.query':'resources.browse',()=>waiting);
    const pending=operation==='tag'?env.window.DesktopHubResources.filterTag('tag'):env.window.DesktopHubResources.browse('off-page');
    if(intent==='page')env.H.navigate('editor');else if(intent==='page-back'){env.H.navigate('settings');env.H.navigate('home');}else if(intent==='search'){env.load('search.js');env.window.DesktopHubSearch.open();}else {env.H.showModal(intent,'新内容','');env.H.ui.modal.locked=intent==='import';if(intent==='closed-modal')env.H.closeModal();}
    const page=env.H.ui.page,modal=env.H.ui.modal,before=structuredClone(env.window.DesktopHubResources.navigationState());
    resolve(operation==='tag'?structuredClone(env.data):{token:'old',path:'C:/Owned/old',items:[],total:0,page:0,warnings:[]});await pending;
    assert.equal(env.H.ui.page,page);assert.equal(env.H.ui.modal,modal);assert.deepEqual(structuredClone(env.window.DesktopHubResources.navigationState()),before);
  });
}
function compactResourceUI(env) {
  env.H.state.settings={uiDensity:'compact'};env.H.pages.settings=()=>'';
  env.nodes.set('.workspace-content',{scrollTop:0,querySelector(){return null;}});env.nodes.set('#usability-filters',{open:false});
  env.nodes.set('.nr-toolbar',{after(element){env.nodes.set('.usability-conditions',element);},querySelectorAll(){return [];}});
  env.load('interactions.js');env.load('usability.js');env.H.render();
}
test('collapsed invalid-resource conditions expose selected status and clear it individually',async()=>{
  const env=setup();await env.window.DesktopHubResources.reload();env.H.navigate('missing');compactResourceUI(env);env.H.actions['nr-status']({value:'PendingRepair'});await env.window.DesktopHubResources.reload();
  assert.equal(env.nodes.get('#usability-filters').open,false);assert.match(env.nodes.get('.usability-conditions')?.innerHTML||'',/状态：需修复/);assert.match(env.nodes.get('.usability-conditions').innerHTML,/usability-clear-status/);
  env.H.actions['usability-clear-status']();await env.window.DesktopHubResources.reload();assert.equal(env.window.DesktopHubResources.navigationState().status,'');assert.ok(!env.nodes.has('.usability-conditions'));
});
test('clear all removes status together with tags, query and extension before pagination',async()=>{
  const env=setup();await env.window.DesktopHubResources.reload();env.H.navigate('missing');env.H.actions['nr-status']({value:'Missing'});env.H.actions['nr-filter-tag'](click('tag'));env.H.actions['nr-search']({value:'课程'});env.H.actions['nr-extension']({value:'.txt'});env.H.actions['nr-page']({dataset:{page:'3'}});env.H.actions['nr-clear']();await env.window.DesktopHubResources.reload();
  const state=env.window.DesktopHubResources.navigationState();assert.equal(state.status,'');assert.equal(state.query,'');assert.equal(state.extension,'');assert.deepEqual(Array.from(state.tagIds),[]);assert.equal(state.page,0);assert.equal('status' in env.sent.at(-1).payload,false);
});
test('back and forward restore the invalid status and visible chip with filters collapsed',async()=>{
  const env=setup();await env.window.DesktopHubResources.reload();env.H.navigate('missing');compactResourceUI(env);env.H.actions['nr-status']({value:'SourceUnavailable'});await env.window.DesktopHubResources.reload();env.H.navigate('library');await env.window.DesktopHubResources.reload();env.H.actions['history-back']();await env.window.DesktopHubResources.reload();
  assert.equal(env.H.ui.page,'missing');assert.equal(env.window.DesktopHubResources.navigationState().status,'SourceUnavailable');assert.match(env.nodes.get('.usability-conditions')?.innerHTML||'',/状态：来源不可用/);
  env.H.actions['history-forward']();await env.window.DesktopHubResources.reload();assert.equal(env.H.ui.page,'library');assert.equal(env.window.DesktopHubResources.navigationState().status,'');assert.ok(!env.nodes.has('.usability-conditions'));
});

test('public resource navigation uses off-page stable IDs and validates tag existence before filtering',async()=>{
  const env=setup(); await env.window.DesktopHubResources.reload();
  assert.equal(typeof env.window.DesktopHubResources.browse,'function');
  await env.window.DesktopHubResources.browse('off-page'); assert.ok(env.sent.some(x=>x.type==='resources.browse'&&x.payload.resourceId==='off-page'));
  await env.window.DesktopHubResources.repair('off-page'); assert.ok(env.sent.some(x=>x.type==='resources.repair'&&x.payload.resourceId==='off-page'));
  await env.window.DesktopHubResources.filterTag('tag'); assert.equal(env.H.ui.page,'library');assert.deepEqual(Array.from(env.window.DesktopHubResources.navigationState().tagIds),['tag']);assert.equal(env.window.DesktopHubResources.navigationState().page,0);
  await env.window.DesktopHubResources.filterTag('removed');assert.deepEqual(Array.from(env.window.DesktopHubResources.navigationState().tagIds),['tag']);assert.match(env.toasts.at(-1),/已移除/);
});
test('M0 resources are cleared, compact surfaces do not initialize or request native file library',()=>{
  const env=setup('mini');assert.deepEqual(Array.from(env.H.state.files),[]);assert.equal(env.sent.length,0);assert.equal(env.window.DesktopHubResources,undefined);
});
test('list, type-icon grid and tag views share native paged data without content image URLs',async()=>{
  const env=setup();await env.window.DesktopHubResources.reload();const list=env.H.pages.library();assert.match(list,/课程.txt/);assert.match(list,/nr-list/);
  env.H.actions['nr-view']({dataset:{view:'grid'}});const grid=env.H.pages.library();assert.match(grid,/nr-grid/);assert.doesNotMatch(grid,/<img|file:\/\/|Demo/);
  env.H.actions['nr-view']({dataset:{view:'tags'}});assert.match(env.H.pages.library(),/nr-category-filter/);assert.equal(env.window.DesktopHubResources.getState().result.items.length,1);
});
test('filename/tag search, conjunctive tag filters and favorites send the same query contract',async()=>{
  const env=setup();await env.window.DesktopHubResources.reload();env.H.actions['nr-filter-tag'](click('tag'));await env.window.DesktopHubResources.reload();assert.deepEqual(env.sent.at(-1).payload.tagIds,['tag']);
  env.H.navigate('favorites');await env.window.DesktopHubResources.reload();assert.equal(env.sent.at(-1).payload.favoritesOnly,true);
  await env.H.actions['nr-favorite'](click('resource'));assert.ok(env.sent.some(x=>x.type==='resources.favorite'&&x.payload.resourceId==='resource'&&x.payload.favorite));
});
test('source preview displays real count/path, requires confirmation and sends token rather than arbitrary path',async()=>{
  const env=setup();await env.window.DesktopHubResources.previewSource('desktop');assert.match(env.H.ui.modal.body,/C:\\Owned/);assert.match(env.H.ui.modal.body,/1 个文件/);assert.ok(!env.sent.some(x=>x.type==='resources.confirm'));
  await env.H.actions['nr-confirm']();assert.ok(env.sent.some(x=>x.type==='resources.confirm'&&x.payload.token));assert.ok(env.sent.every(x=>!('path' in x.payload)));
});
test('cancelled preview never confirms and sends explicit operation cancellation',async()=>{
  const env=setup();env.H.actions['nr-source-add']({dataset:{kind:'publicDesktop'}});assert.match(env.H.ui.modal.body,/type="checkbox"/);assert.ok(!env.sent.some(x=>x.type==='resources.preview'));await env.window.DesktopHubResources.previewSource('publicDesktop',null,false,null,true);await env.H.actions['nr-cancel']();assert.ok(env.sent.some(x=>x.type==='resources.preview'&&x.payload.authorizePublic));assert.ok(env.sent.some(x=>x.type==='resources.cancel'));assert.ok(!env.sent.some(x=>x.type==='resources.confirm'));assert.equal(env.window.DesktopHubResources.getState().preview,null);
});
test('first-import prompt sends explicit opt-in or refusal and respects the persisted choice',async()=>{
  for (const [value, expected] of [['yes', true], ['no', false]]) {
    const env=setup();await env.window.DesktopHubResources.reload();
    await env.window.DesktopHubResources.previewSource('desktop');
    assert.match(env.H.ui.modal.body,/是否按修改时间自动添加所有标签/);
    env.nodes.set('input[name="nr-time-tags"]:checked',{value});
    await env.H.actions['nr-confirm']();
    assert.equal(env.sent.find(x=>x.type==='resources.confirm').payload.autoTagByModificationTime,expected);
    env.data.autoTagByModificationTime=expected;await env.window.DesktopHubResources.reload();
    await env.window.DesktopHubResources.previewSource('desktop');
    assert.doesNotMatch(env.H.ui.modal.body,/nr-time-tags/);
    await env.H.actions['nr-cancel']();
  }
});
test('suggestion acceptance/rejection, open and browse use stable ids only and escape filename/tag injection',async()=>{
  const env=setup();env.data.items[0].resource.displayName='<script>unsafe</script>';await env.window.DesktopHubResources.reload();assert.match(env.H.pages.library(),/&lt;script&gt;/);assert.doesNotMatch(env.H.pages.library(),/<script>/);
  await env.H.actions['nr-accept'](click('suggestion'));await env.H.actions['nr-reject'](click('suggestion'));await env.H.actions['nr-open'](click('resource'));await env.H.actions['nr-browse'](click('resource'));
  assert.ok(env.sent.some(x=>x.type==='resources.suggestion'&&x.payload.accept===false));assert.ok(env.sent.some(x=>x.type==='resources.open'&&x.payload.resourceId==='resource'));assert.ok(env.sent.every(x=>!('target' in x.payload)));
});
test('cleanup needs a second confirmation; actual host errors are visible and never become fake success',async()=>{
  const env=setup();await env.window.DesktopHubResources.reload();env.H.actions['nr-select']({dataset:{id:'resource'},checked:true});env.H.actions['nr-clean']();assert.match(env.H.ui.modal.body,/不会删除实际文件/);assert.ok(!env.sent.some(x=>x.type==='resources.clean'));
  env.fail('磁盘不可用');await env.H.actions['nr-clean-confirm']();assert.match(env.toasts.at(-1),/磁盘不可用/);assert.ok(env.sent.find(x=>x.type==='resources.clean').payload.confirmed);assert.ok(env.H.ui.modal);
});
