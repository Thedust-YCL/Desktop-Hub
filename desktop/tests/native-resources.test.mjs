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
  const env=setup();env.data.items[0].resource.status='Missing';env.H.navigate('missing');await env.window.DesktopHubResources.reload();env.H.actions['nr-select']({dataset:{id:'resource'},checked:true});env.H.actions['nr-clean']();assert.match(env.H.ui.modal.body,/不会删除实际文件/);assert.ok(!env.sent.some(x=>x.type==='resources.clean'));
  env.fail('磁盘不可用');await env.H.actions['nr-clean-confirm']();assert.match(env.toasts.at(-1),/磁盘不可用/);assert.ok(env.sent.find(x=>x.type==='resources.clean').payload.confirmed);assert.ok(env.H.ui.modal);
});

function batchFixture() {
  const env=setup(), rows=Array.from({length:501},(_,i)=>({resource:{...env.data.items[0].resource,id:randomUUID(),displayName:`资源${i}`},tags:[],suggestions:[]}));
  env.rows=rows;env.data.items=rows.slice(0,60);env.data.total=501;env.data.savedFilters=[];
  env.respond('resources.query',payload=>({...structuredClone(env.data),page:payload.page||0,items:structuredClone(rows.slice((payload.page||0)*60,(payload.page||0)*60+60)),
    selectedResources:rows.filter(x=>(payload.selectedIds||[]).includes(x.resource.id)).map(x=>({id:x.resource.id,status:x.resource.status})),
    cleanupResources:rows.filter(x=>(payload.cleanupIds||[]).includes(x.resource.id)).map(x=>({id:x.resource.id,status:x.resource.status})),commonSuggestions:env.commonSuggestions||[]}));
  return env;
}
const selection = env => Array.from(env.window.DesktopHubResources.getState().selectedIds||[]);

function savedFilterFixture() {
  const env=setup(), id=randomUUID();
  env.data.tags.push({id:'new-tag',name:'新标签',tagType:'theme'});
  env.filter={id,name:'保存的条件',query:'alpha',tagIds:['tag'],extension:'.txt',favoritesOnly:false,sort:'name'};
  env.resolveFilter=()=>env.filter ? {filter:structuredClone(env.filter),valid:env.filter.tagIds.every(id=>env.data.tags.some(t=>t.id===id)),missingTagIds:env.filter.tagIds.filter(id=>!env.data.tags.some(t=>t.id===id))} : null;
  env.respond('resources.query',payload=>{
    const current=env.resolveFilter(), active=payload.filterId ? current : null, invalid=!!payload.filterId && !active?.valid;
    return {...structuredClone(env.data),page:payload.page||0,savedFilters:current?[current]:[],filterStatus:active,filterInvalid:invalid,filterMissing:!!payload.filterId&&!active,
      items:invalid?[]:structuredClone(env.data.items),total:invalid?0:1,
      selectedResources:(payload.selectedIds||[]).map(id=>({id,status:'Active'})),cleanupResources:[]};
  });
  env.respond('resources.filterSave',({filter})=>{env.filter=structuredClone(filter);return {saved:true};});
  return env;
}
function assertFilterCriteria(env, expected) {
  for(const state of [env.window.DesktopHubResources.getState(),env.window.DesktopHubResources.navigationState()]) {
    for(const key of ['query','extension','favoritesOnly','sort'])assert.equal(state[key],expected[key],key);
    assert.deepEqual(Array.from(state.tagIds),expected.tagIds);
    assert.equal(state.filterId,expected.id);
  }
  const html=env.H.pages.library();
  assert.match(html,new RegExp(`data-change="nr-search" value="${expected.query}"`));
  assert.ok(html.includes(`value="${expected.extension}" selected`));
  assert.ok(html.includes(`value="${expected.sort}" selected`));
  const favorites=html.match(/<input type="checkbox" data-change="nr-favorites-only"[^>]*>/)[0];
  assert.equal(/checked/.test(favorites),expected.favoritesOnly);
}

for(const removed of ['query','tag','extension','favoritesOnly'])test(`saved-filter history accepts current criteria before individually removing ${removed}`,async()=>{
  const env=savedFilterFixture();await env.window.DesktopHubResources.reload();compactResourceUI(env);
  env.H.actions['nr-filter-use'](click(env.filter.id));await env.window.DesktopHubResources.reload();
  env.H.navigate('tags');await env.window.DesktopHubResources.reload();
  env.H.actions['nr-filter-edit'](click(env.filter.id));
  env.nodes.set('#nr-filter-query',{value:'beta'});env.nodes.set('#nr-filter-extension',{value:'.pdf'});
  env.nodes.set('#nr-filter-favorites',{checked:true});env.nodes.set('#nr-filter-sort',{value:'modifiedDesc'});
  env.H.actions['nr-filter-tag-choice']({value:'tag',checked:false});env.H.actions['nr-filter-tag-choice']({value:'new-tag',checked:true});
  await env.H.actions['nr-filter-save']();const current=structuredClone(env.filter);
  env.H.actions['history-back']();await env.window.DesktopHubResources.reload();
  assert.equal(env.H.ui.page,'library');assertFilterCriteria(env,current);
  assert.match(env.nodes.get('.usability-conditions').innerHTML,/名称：beta/);
  assert.match(env.nodes.get('.usability-conditions').innerHTML,/类型：\.pdf/);
  assert.match(env.nodes.get('.usability-conditions').innerHTML,/仅收藏/);
  assert.match(env.H.pages.library(),/nr-filter-tag" data-id="new-tag"[^>]*>新标签 ×/);
  env.H.actions['history-forward']();await env.window.DesktopHubResources.reload();assert.equal(env.H.ui.page,'tags');
  env.H.actions['history-back']();await env.window.DesktopHubResources.reload();assertFilterCriteria(env,current);
  if(removed==='tag')env.H.actions['nr-filter-tag'](click('new-tag'));
  else env.H.actions[`usability-clear-${removed==='favoritesOnly'?'favorites':removed}`]();
  await env.window.DesktopHubResources.reload();const payload=env.sent.at(-1).payload;
  assert.equal(payload.filterId,undefined);assert.equal(payload.query,removed==='query'?'':current.query);
  assert.deepEqual(payload.tagIds,removed==='tag'?[]:current.tagIds);
  assert.equal(payload.extension,removed==='extension'?undefined:current.extension);
  assert.equal(payload.favoritesOnly,removed==='favoritesOnly'?false:current.favoritesOnly);assert.equal(payload.sort,current.sort);
});

for(const field of ['query','tagIds','extension','favoritesOnly','sort'])test(`accepted active saved-filter ${field} edit updates controls and ${field==='sort'?'retains':'clears'} selection`,async()=>{
  const env=savedFilterFixture();await env.window.DesktopHubResources.reload();env.H.actions['nr-filter-use'](click(env.filter.id));await env.window.DesktopHubResources.reload();
  env.H.actions['nr-select']({dataset:{id:'resource'},checked:true});assert.deepEqual(selection(env),['resource']);
  env.filter[field]=({query:'beta',tagIds:['new-tag'],extension:'.pdf',favoritesOnly:true,sort:'modifiedDesc'})[field];
  await env.window.DesktopHubResources.reload();assertFilterCriteria(env,env.filter);
  assert.deepEqual(selection(env),field==='sort'?['resource']:[]);assert.equal(env.window.DesktopHubResources.getState().loading,false);
});

test('invalid current saved-filter tags stay visible after history and deletion requires explicit exit',async()=>{
  const env=savedFilterFixture();await env.window.DesktopHubResources.reload();compactResourceUI(env);env.H.actions['nr-filter-use'](click(env.filter.id));await env.window.DesktopHubResources.reload();
  env.H.navigate('tags');await env.window.DesktopHubResources.reload();env.filter={...env.filter,query:'beta',tagIds:['gone'],extension:'.pdf',favoritesOnly:true,sort:'modifiedDesc'};
  env.H.actions['history-back']();await env.window.DesktopHubResources.reload();assertFilterCriteria(env,env.filter);
  assert.match(env.H.pages.library(),/标签已删除 ×/);assert.match(env.H.pages.library(),/筛选中的标签已失效/);assert.doesNotMatch(env.H.pages.library(),/data-resource-id/);
  env.H.actions['nr-filter-edit'](click(env.filter.id));assert.match(env.H.ui.modal.body,/标签已删除/);env.H.closeModal();
  const id=env.filter.id;env.filter=null;await env.window.DesktopHubResources.reload();assert.equal(env.window.DesktopHubResources.getState().filterId,id);
  assert.match(env.H.pages.library(),/此保存筛选已删除/);assert.doesNotMatch(env.H.pages.library(),/data-resource-id/);assert.equal(env.sent.at(-1).payload.filterId,id);
  env.H.actions['nr-filter-exit']();await env.window.DesktopHubResources.reload();assert.equal(env.sent.at(-1).payload.filterId,undefined);assert.equal(env.window.DesktopHubResources.getState().filterId,'');
});

test('stale saved-filter responses respect newer input, modal draft and page intent without stuck loading',async()=>{
  const env=savedFilterFixture();await env.window.DesktopHubResources.reload();env.H.actions['nr-filter-use'](click(env.filter.id));await env.window.DesktopHubResources.reload();
  const host=()=>({...structuredClone(env.data),savedFilters:[env.resolveFilter()],filterStatus:env.resolveFilter()});
  let done;env.respond('resources.query',()=>new Promise(resolve=>done=resolve));const pending=env.window.DesktopHubResources.reload();
  env.filter={...env.filter,query:'host-changed'};env.H.actions['nr-search']({value:'new-input'});done(host());await pending;
  assert.equal(env.window.DesktopHubResources.getState().query,'new-input');assert.equal(env.window.DesktopHubResources.getState().loading,false);
  env.respond('resources.query',()=>host());await env.window.DesktopHubResources.reload();env.H.actions['nr-filter-use'](click(env.filter.id));await env.window.DesktopHubResources.reload();
  env.H.actions['nr-filter-edit'](click(env.filter.id));env.nodes.set('#nr-filter-query',{value:'my-draft'});const modal=env.H.ui.modal;
  env.filter={...env.filter,query:'new-host'};await env.window.DesktopHubResources.reload();assert.equal(env.H.ui.modal,modal);assert.equal(env.nodes.get('#nr-filter-query').value,'my-draft');
  assert.equal(env.window.DesktopHubResources.getState().query,'new-host');env.H.closeModal();
  env.respond('resources.query',()=>new Promise(resolve=>done=resolve));const obsolete=env.window.DesktopHubResources.reload();env.H.navigate('home');done(host());await obsolete;
  assert.equal(env.window.DesktopHubResources.getState().query,'');assert.equal(env.window.DesktopHubResources.getState().filterId,'');assert.equal(env.window.DesktopHubResources.getState().loading,false);
});

test('active saved-filter sort-only save retains selection and invalidates older in-flight criteria',async()=>{
  const env=savedFilterFixture();await env.window.DesktopHubResources.reload();env.H.actions['nr-filter-use'](click(env.filter.id));await env.window.DesktopHubResources.reload();
  env.H.actions['nr-select']({dataset:{id:'resource'},checked:true});env.H.actions['nr-filter-edit'](click(env.filter.id));env.nodes.set('#nr-filter-sort',{value:'modifiedDesc'});
  const old=env.resolveFilter();let done;env.respond('resources.query',()=>new Promise(resolve=>done=resolve));const pending=env.window.DesktopHubResources.reload();
  env.respond('resources.query',()=>({...structuredClone(env.data),savedFilters:[env.resolveFilter()],filterStatus:env.resolveFilter(),selectedResources:[{id:'resource',status:'Active'}]}));
  await env.H.actions['nr-filter-save']();done({...structuredClone(env.data),savedFilters:[old],filterStatus:old});await pending;
  assertFilterCriteria(env,env.filter);assert.deepEqual(selection(env),['resource']);assert.equal(env.window.DesktopHubResources.getState().loading,false);
});
test('normal selection survives page/view/sort and metadata refresh, clearing synchronously on conditions',async()=>{
  const env=batchFixture();await env.window.DesktopHubResources.reload();assert.match(env.H.pages.library(),/nr-select/);
  env.H.actions['nr-select']({dataset:{id:env.rows[0].resource.id},checked:true});env.H.actions['nr-page']({dataset:{page:'1'}});await env.window.DesktopHubResources.reload();
  env.H.actions['nr-select']({dataset:{id:env.rows[60].resource.id},checked:true});env.H.actions['nr-view']({dataset:{view:'grid'}});env.H.actions['nr-sort']({value:'modifiedDesc'});await env.window.DesktopHubResources.reload();
  assert.deepEqual(selection(env),[env.rows[0].resource.id,env.rows[60].resource.id]);assert.equal(env.sent.at(-1).payload.sort,'modifiedDesc');
  env.H.actions['nr-search']({value:'新条件'});assert.deepEqual(selection(env),[]);
});
test('500 selection limit excludes non-active IDs and missing cleanup stays independent',async()=>{
  const env=batchFixture();await env.window.DesktopHubResources.reload();
  for(let p=0;p<9;p++){env.H.actions['nr-page']({dataset:{page:String(p)}});await env.window.DesktopHubResources.reload();for(const row of env.window.DesktopHubResources.getState().result.items)env.H.actions['nr-select']({dataset:{id:row.resource.id},checked:true});}
  assert.equal(selection(env).length,500);assert.match(env.toasts.at(-1),/500/);
  env.H.navigate('missing');env.rows[0].resource.status='Missing';env.rows[1].resource.status='SourceUnavailable';await env.window.DesktopHubResources.reload();
  env.H.actions['nr-select']({dataset:{id:env.rows[0].resource.id},checked:true});env.H.actions['nr-select']({dataset:{id:env.rows[1].resource.id},checked:true});
  assert.deepEqual(selection(env),[]);assert.deepEqual(Array.from(env.window.DesktopHubResources.getState().cleanupIds),[env.rows[0].resource.id]);
  env.H.navigate('favorites');assert.deepEqual(Array.from(env.window.DesktopHubResources.getState().cleanupIds),[]);
});
test('refresh prunes disappeared and status-changed off-page selection while preserving scroll',async()=>{
  const env=batchFixture();env.nodes.set('.workspace-content',{scrollTop:780,scrollLeft:24});await env.window.DesktopHubResources.reload();
  const vanished=env.rows[0].resource.id,changed=env.rows[1].resource.id,kept=env.rows[2].resource.id;
  for(const id of [vanished,changed,kept])env.H.actions['nr-select']({dataset:{id},checked:true});env.H.actions['nr-page']({dataset:{page:'1'}});await env.window.DesktopHubResources.reload();
  env.rows.splice(0,1);env.rows[0].resource.status='Missing';await env.window.DesktopHubResources.reload();
  assert.deepEqual(selection(env),[kept]);assert.equal(env.nodes.get('.workspace-content').scrollTop,780);assert.equal(env.nodes.get('.workspace-content').scrollLeft,24);
});
test('old query cannot restore selection/results after a condition changes before debounce',async()=>{
  const env=batchFixture();await env.window.DesktopHubResources.reload();env.H.actions['nr-select']({dataset:{id:env.rows[0].resource.id},checked:true});
  let resolve;env.respond('resources.query',()=>new Promise(done=>resolve=done));const pending=env.window.DesktopHubResources.reload();const before=env.window.DesktopHubResources.getState().result;
  env.H.actions['nr-search']({value:'新'});resolve({...env.data,items:[],selectedResources:[],cleanupResources:[]});await pending;
  assert.equal(env.window.DesktopHubResources.getState().result,before);assert.deepEqual(selection(env),[]);
  env.respond('resources.query',()=>env.data);
});
test('batch tag confirmation sends one incremental atomic request and keeps selection on host failure',async()=>{
  const env=batchFixture();await env.window.DesktopHubResources.reload();const ids=env.rows.slice(0,2).map(x=>x.resource.id);for(const id of ids)env.H.actions['nr-select']({dataset:{id},checked:true});
  await env.H.actions['nr-batch-tags']({dataset:{operation:'addTags'}});assert.match(env.H.ui.modal.body,/原有标签/);assert.ok(!env.sent.some(x=>x.type==='resources.batch'));
  env.nodes.set('[data-batch-tag]:checked',[{value:'tag'}]);env.H.actions['nr-batch-tag']({value:'tag',checked:true});
  env.respond('resources.batch',()=>{throw new Error('写入失败');});await env.H.actions['nr-batch-confirm']();assert.deepEqual(selection(env),ids);assert.ok(env.H.ui.modal);assert.match(env.toasts.at(-1),/写入失败/);
  env.respond('resources.batch',()=>({saved:true}));await env.H.actions['nr-batch-confirm']();const requests=env.sent.filter(x=>x.type==='resources.batch');assert.equal(requests.length,2);assert.deepEqual(requests[1].payload,{resourceIds:ids,operation:'addTags',values:['tag']});
  assert.ok(!env.sent.some(x=>x.type==='resources.bindTag'));assert.deepEqual(selection(env),[]);
});
test('favorites and comparable suggestions use whole selection with name/type rather than current-page IDs',async()=>{
  const env=batchFixture();await env.window.DesktopHubResources.reload();const first=env.rows[0].resource.id;env.H.actions['nr-select']({dataset:{id:first},checked:true});env.H.actions['nr-page']({dataset:{page:'1'}});await env.window.DesktopHubResources.reload();const second=env.rows[60].resource.id;env.H.actions['nr-select']({dataset:{id:second},checked:true});env.commonSuggestions=[{name:'共享',type:'theme'}];
  await env.H.actions['nr-batch-suggestions']();assert.match(env.H.ui.modal.body,/共享/);env.H.actions['nr-batch-suggestion']({dataset:{index:'0'}});await env.H.actions['nr-batch-confirm']();
  assert.deepEqual(env.sent.find(x=>x.type==='resources.batchSuggestions').payload,{resourceIds:[first,second],suggestionName:'共享',suggestionType:'theme'});
  env.H.actions['nr-select']({dataset:{id:second},checked:true});await env.H.actions['nr-batch-favorite']({dataset:{operation:'unfavorite'}});assert.deepEqual(env.sent.find(x=>x.type==='resources.batch').payload,{resourceIds:[second],operation:'unfavorite',values:[]});
});
test('batch confirmation cannot silently retarget a pruned selection or close a newer modal',async()=>{
  const env=batchFixture();await env.window.DesktopHubResources.reload();env.H.actions['nr-select']({dataset:{id:env.rows[0].resource.id},checked:true});await env.H.actions['nr-batch-tags']({dataset:{operation:'removeTags'}});env.H.actions['nr-batch-tag']({value:'tag',checked:true});
  env.rows.splice(0,1);await env.window.DesktopHubResources.reload();await env.H.actions['nr-batch-confirm']();assert.ok(!env.sent.some(x=>x.type==='resources.batch'));assert.match(env.toasts.at(-1),/选择.*变化|已失效/);
  env.H.closeModal();env.H.actions['nr-select']({dataset:{id:env.rows[0].resource.id},checked:true});let done;env.respond('resources.batch',()=>new Promise(resolve=>done=resolve));const pending=env.H.actions['nr-batch-favorite']({dataset:{operation:'favorite'}});env.H.showModal('新草稿','保持','');const modal=env.H.ui.modal;done({saved:true});await pending;assert.equal(env.H.ui.modal,modal);
});
test('saved filter create/edit/delete preserves ID and invalid filters offer explicit recovery without broadening',async()=>{
  const env=batchFixture();const id=randomUUID();env.data.savedFilters=[{filter:{id,name:'旧筛选',query:'课程',tagIds:['gone'],extension:'.txt',favoritesOnly:true,sort:'name'},valid:false,missingTagIds:['gone']}];await env.window.DesktopHubResources.reload();assert.match(env.H.pages.library(),/旧筛选/);
  env.H.actions['nr-filter-use'](click(id));await env.window.DesktopHubResources.reload();assert.equal(env.sent.at(-1).payload.filterId,id);assert.ok(!('query' in env.sent.at(-1).payload));
  env.H.actions['nr-filter-edit'](click(id));assert.match(env.H.ui.modal.body,/已删除|失效/);env.nodes.set('#nr-filter-name',{value:'修复后'});env.H.actions['nr-filter-tag-remove'](click('gone'));await env.H.actions['nr-filter-save']();const saved=env.sent.find(x=>x.type==='resources.filterSave');assert.equal(saved.payload.filter.id,id);assert.deepEqual(saved.payload.filter.tagIds,[]);assert.equal(saved.payload.filter.sort,'name');
  env.H.actions['nr-filter-delete'](click(id));assert.ok(!env.sent.some(x=>x.type==='resources.filterDelete'));await env.H.actions['nr-filter-delete-confirm'](click(id));assert.deepEqual(env.sent.find(x=>x.type==='resources.filterDelete').payload,{id});
  env.H.actions['nr-filter-new']();env.nodes.set('#nr-filter-name',{value:'当前条件'});await env.H.actions['nr-filter-save']();assert.notEqual(env.sent.filter(x=>x.type==='resources.filterSave').at(-1).payload.filter.id,id);
});
test('deferred batch editor never replaces a newer dialog or reports stale selection feedback',async()=>{
  const env=batchFixture();await env.window.DesktopHubResources.reload();env.H.actions['nr-select']({dataset:{id:env.rows[0].resource.id},checked:true});let done;env.respond('resources.query',()=>new Promise(resolve=>done=resolve));const pending=env.H.actions['nr-batch-suggestions']();env.H.showModal('新编辑器','保留','');const modal=env.H.ui.modal;done({...env.data,selectedResources:[],cleanupResources:[],commonSuggestions:[]});await pending;
  assert.equal(env.H.ui.modal,modal);assert.equal(env.toasts.length,0);
});
test('completed saved-filter and single-resource mutation render usable selection controls',async()=>{
  const env=batchFixture();await env.window.DesktopHubResources.reload();const images=[];env.H.render=()=>images.push(env.H.pages.library());
  await env.H.actions['nr-favorite'](click(env.rows[0].resource.id));assert.equal(env.window.DesktopHubResources.getState().busy,false);assert.doesNotMatch(images.at(-1).match(/<input type="checkbox" data-change="nr-select"[^>]*>/)[0],/disabled/);
  env.H.actions['nr-filter-new']();env.nodes.set('#nr-filter-name',{value:'我的筛选'});await env.H.actions['nr-filter-save']();assert.doesNotMatch(images.at(-1).match(/<input type="checkbox" data-change="nr-select"[^>]*>/)[0],/disabled/);
  const id=env.sent.find(x=>x.type==='resources.filterSave').payload.filter.id;env.H.actions['nr-filter-delete'](click(id));await env.H.actions['nr-filter-delete-confirm'](click(id));assert.doesNotMatch(images.at(-1).match(/<input type="checkbox" data-change="nr-select"[^>]*>/)[0],/disabled/);
});
test('invalid saved filter remains explicit and hides resource results instead of running broad conditions',async()=>{
  const env=batchFixture();const id=randomUUID();env.data.savedFilters=[{filter:{id,name:'失效条件',query:'课程',tagIds:['gone'],favoritesOnly:false,sort:'type'},valid:false,missingTagIds:['gone']}];await env.window.DesktopHubResources.reload();
  env.H.actions['nr-filter-use'](click(id));env.data.filterInvalid=true;await env.window.DesktopHubResources.reload();assert.match(env.H.pages.library(),/筛选中的标签已失效/);assert.doesNotMatch(env.H.pages.library(),/data-resource-id/);
  assert.ok(env.sent.filter(x=>x.type==='resources.query'&&x.payload.filterId===id).every(x=>!('query' in x.payload)&&!('tagIds' in x.payload)));
});
