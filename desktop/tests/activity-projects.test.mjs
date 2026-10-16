import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { randomUUID } from 'node:crypto';

function setup(surface='workspace',history=false,composeScenes=false) {
  const requests=[],sent=[],events=new Map(),dom=new Map(),timers=new Map(),calls=[],nodes=new Map();let uid=0;
  const element=(id,value='')=>({id,value,checked:false,innerHTML:'',textContent:'',dataset:{},isConnected:true,disabled:false,focus(){document.activeElement=this;},querySelector(){return null;},querySelectorAll(){return [];},insertAdjacentHTML(position,html){this.innerHTML=position==='afterbegin'?html+this.innerHTML:this.innerHTML+html;},setAttribute(k,v){this[k]=v;},remove(){nodes.delete('#'+id);}});
  for(const id of ['ap-project-name','ap-project-description','ap-project-pin','ap-error','ap-error-diagnostic','ap-error-details','ap-candidates','ap-select-query','ap-web-url','ap-web-label'])nodes.set('#'+id,element(id));
  const content=element('page-content');content.scrollTop=0;content.scrollLeft=0;
  const home=element('home');home.innerHTML='<section class="home-scenes">scenes</section><h2 class="launcher-modules-heading">modules</h2><div class="launchpad-grid"></div>';
  const scenes={insertAdjacentHTML(position,html){assert.equal(position,'afterend');home.innerHTML=home.innerHTML.replace('</section>','</section>'+html);}};
  home.querySelector=s=>s==='.home-scenes'?scenes:null;
  const document={activeElement:null,readyState:'complete',documentElement:{dataset:{}},querySelector(s){if(s==='.workspace-content')return content;if(s==='.home-content')return home;if(s==='.home-scenes')return scenes;if(s==='.projects-home')return home.innerHTML.includes('projects-home')?{}:null;if(s==='.activity-home')return home.innerHTML.includes('activity-home')?{}:null;return nodes.get(s)||null;},querySelectorAll(){return [];},addEventListener(type,fn){if(!dom.has(type))dom.set(type,[]);dom.get(type).push(fn);}};
  const settings={appEntries:[{id:'app-a',name:'App'}],scenes:[{id:'scene-a',name:'Scene',enabled:true,actions:[],dependencies:[]}],launcherPins:[]};
  const H={ui:{page:'home',modal:null},state:{settings:{}},actions:{},pages:{home:()=>home.innerHTML},escape:s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])),icon:()=>'<svg/>',render(){content.innerHTML=(H.pages[H.ui.page]||H.pages.home)();},navigate(page){if(H.ui.page==='editor'&&H.ui.sceneDraftDirty&&page!=='editor'){H.showModal('离开场景编辑器？','','');return;}H.ui.page=page;H.closeModal();H.render();},showModal(title,body,footer){H.ui.modal={title,body,footer};},closeModal(){H.ui.modal=null;},toast(message){calls.push(['toast',message]);},restoreViewport(){calls.push(['viewport']);}};
  for(const action of ['app-launch','run-scene','edit-scene','nr-open'])H.actions[action]=async el=>{calls.push([action,structuredClone(el.dataset)]);return {success:true};};
  const bridge={on(type,fn){if(!events.has(type))events.set(type,[]);events.get(type).push(fn);},request(type,payload){sent.push({type,payload:structuredClone(payload)});return new Promise((resolve,reject)=>requests.push({type,payload,resolve,reject}));}};
  let resources={query:'',tagIds:[],folder:null};
  const window={Hub:H,crypto:{randomUUID},DesktopHubNative:{bridge},DesktopHubDesktop:{getState:()=>({surface,hostState:{settings}}),applyHostState(result){Object.assign(settings,result.settings);}},DesktopHubResources:{repair:id=>calls.push(['repair',id]),browse:id=>calls.push(['browse',id]),getState:()=>({busy:false}),navigationState:()=>structuredClone(resources),restoreNavigation(state){resources=structuredClone(state);}}};
  const context=vm.createContext({window,document,console,Date,URL,setTimeout(fn,ms){const id=++uid;timers.set(id,{fn,ms});return id;},clearTimeout(id){timers.delete(id);}});
  if(composeScenes)for(const path of ['../../prototype/scenes.js','../scenes.js'])vm.runInContext(fs.readFileSync(new URL(path,import.meta.url),'utf8'),context);
  if(history)vm.runInContext(fs.readFileSync(new URL('../interactions.js',import.meta.url),'utf8'),context);
  const path=new URL('../activity-projects.js',import.meta.url);if(fs.existsSync(path))vm.runInContext(fs.readFileSync(path,'utf8'),context);
  return {window,H,settings,requests,sent,events,nodes,content,home,calls,document,emit(type,target,extra={}){for(const fn of dom.get(type)||[])fn({target,preventDefault(){},...extra});},event(type,data={}){for(const fn of events.get(type)||[])fn(data);},async timers(){const list=[...timers.values()];timers.clear();for(const {fn}of list)fn();await settle();}};
}
const project=(id='project-a',name='Project')=>({id,name,description:'Description',displayOrder:0,pinnedToHome:true,items:[]});
const summary=p=>({...p,itemCount:p.items.length});
const ref=(type,id,status='Active',name='Current name')=>({item:{id:'item-'+id,type,entityId:type==='web'?null:id,url:type==='web'?'https://example.com/':null,name:null},name,detail:'current detail',status,canExecute:status==='Active'});
const recent=(type,id,status='Active')=>({record:{type,id,at:'2026-10-14T12:00:00Z',result:type==='app'?'started':type==='scene'?'execution_established':'submitted'},name:'Current '+id,detail:'current detail',status,canExecute:status==='Active'});
async function settle(){for(let i=0;i<12;i++)await Promise.resolve();}
async function reply(env,type,value){const req=env.requests.findLast(r=>r.type===type&&!r.done);assert.ok(req,'pending '+type);req.done=true;req.resolve(value);await settle();}
async function open(env,p=project(),items=[]){assert.equal(env.window.DesktopHubProjects?.open(p.id),true);await reply(env,'projects.query',{projects:[summary(p)],project:p,items,warning:null});}

test('Task6 module stays Workspace-only and refuses every existing modal',()=>{
  const e=setup();assert.equal(typeof e.window.DesktopHubProjects?.open,'function');for(const locked of [false,true]){e.H.ui.modal={locked};assert.equal(e.window.DesktopHubProjects.open('project-a'),false);}assert.equal(setup('mini').window.DesktopHubProjects,undefined);assert.equal(setup('dock').sent.length,0);
});
test('Task6 opening a project renders current details without launching associated content',async()=>{
  const e=setup();await open(e,project(),[ref('app','app-a'),ref('web','web-a')]);assert.equal(e.H.ui.page,'project');assert.match(e.content.innerHTML,/Current name/);assert.ok(!e.calls.some(c=>['app-launch','run-scene','nr-open','browse'].includes(c[0])));assert.equal(e.window.DesktopHubProjects.navigationState().projectId,'project-a');
});
test('Task6 newer modal and open-close intent invalidate pending project navigation',async()=>{
  for(const close of [false,true]){const e=setup();e.window.DesktopHubProjects.open('project-a');e.H.showModal('new editor','','');const modal=e.H.ui.modal;if(close)e.H.closeModal();await reply(e,'projects.query',{projects:[],project:project(),items:[],warning:null});assert.equal(e.H.ui.page,'home');assert.equal(e.H.ui.modal,close?null:modal);}
});
test('Task6 newer project and navigation supersede old detail queries',async()=>{
  const e=setup();e.window.DesktopHubProjects.open('project-a');e.window.DesktopHubProjects.open('project-b');const first=e.requests.find(r=>r.type==='projects.query');first.done=true;first.resolve({projects:[],project:project('project-a'),items:[]});await settle();assert.notEqual(e.H.ui.page,'project');await reply(e,'projects.query',{projects:[],project:project('project-b'),items:[],warning:null});assert.equal(e.window.DesktopHubProjects.navigationState().projectId,'project-b');
  e.window.DesktopHubProjects.open('project-c');e.H.navigate('library');await reply(e,'projects.query',{projects:[],project:project('project-c'),items:[]});assert.equal(e.H.ui.page,'library');
});
test('Task6 home shows six recent items after fixed projects and never duplicates sections',async()=>{
  const e=setup();e.H.render();await reply(e,'activity.query',{items:Array.from({length:9},(_,i)=>recent('file','f'+i)),warning:'history warning'});await reply(e,'projects.query',{projects:[summary(project())],project:null,items:[],warning:null});e.H.render();e.H.render();const html=e.home.innerHTML;assert.equal((html.match(/class="projects-home/g)||[]).length,1);assert.equal((html.match(/class="activity-home/g)||[]).length,1);assert.ok(html.indexOf('home-scenes')<html.indexOf('projects-home'));assert.ok(html.indexOf('projects-home')<html.indexOf('activity-home'));assert.ok(html.indexOf('activity-home')<html.indexOf('launcher-modules-heading'));assert.equal((html.match(/data-recent-index=/g)||[]).length,6);assert.match(html,/history warning/);
});
test('Task6 recent actions use exact stable IDs and shared application feedback without a second banner',async()=>{
  const e=setup();e.window.DesktopHubActivity.refresh();await reply(e,'activity.query',{items:[recent('app','app-a')],warning:null});await e.H.actions['ap-recent-open']({dataset:{recentIndex:'0'}});assert.deepEqual(e.calls.find(c=>c[0]==='app-launch'),['app-launch',{id:'app-a'}]);assert.ok(e.sent.filter(r=>r.type==='activity.query').length>=2);assert.doesNotMatch(e.content.innerHTML,/unified-launch-failure/);
});
test('Task6 deleted entities cannot launch and missing resources use existing repair',async()=>{
  const e=setup();await open(e,project(),[ref('app','gone','Removed'),ref('file','missing','Missing'),ref('scene','scene-a','Disabled')]);await e.H.actions['ap-item-open']({dataset:{id:'item-gone'}});assert.ok(!e.calls.some(c=>c[0]==='app-launch'));await e.H.actions['ap-item-open']({dataset:{id:'item-missing'}});assert.ok(e.calls.some(c=>c[0]==='repair'&&c[1]==='missing'));await e.H.actions['ap-item-open']({dataset:{id:'item-scene-a'}});assert.ok(e.calls.some(c=>c[0]==='edit-scene'));assert.match(e.content.innerHTML,/关联失效|已移除/);
});
test('Task6 project web opening sends current project/item IDs and no URL payload',async()=>{
  const e=setup();await open(e,project(),[ref('web','web-a')]);const action=e.H.actions['ap-item-open']({dataset:{id:'item-web-a'}});assert.deepEqual(e.sent.at(-1),{type:'projects.openWeb',payload:{projectId:'project-a',itemId:'item-web-a'}});await reply(e,'projects.openWeb',{submitted:true,message:'已提交'});await action;assert.ok(!e.sent.some(r=>r.type==='activity.record'));
});
test('Task6 clear confirms, preserves rows on failure and invalidates earlier history replies',async()=>{
  const e=setup();e.window.DesktopHubActivity.refresh();await reply(e,'activity.query',{items:[recent('file','f')],warning:null});e.H.actions['ap-clear']();assert.match(e.H.ui.modal.title,/清空/);const promise=e.H.actions['ap-clear-confirm']();e.requests.findLast(r=>r.type==='activity.clear').reject(new Error('write blocked'));await promise;assert.ok(e.H.ui.modal);assert.match(e.nodes.get('#ap-error').textContent,/write blocked/);assert.match(e.H.pages.activity(),/Current f/);
  const success=e.H.actions['ap-clear-confirm']();await reply(e,'activity.clear',{cleared:true});await success;assert.equal(e.H.ui.modal,null);assert.doesNotMatch(e.H.pages.activity(),/Current f/);
});
test('Task6 project editing preserves typed draft on save failure and events never replace its modal',async()=>{
  const e=setup();await open(e);e.H.actions['ap-edit']({dataset:{id:'project-a'}});const modal=e.H.ui.modal;e.nodes.get('#ap-project-name').value='Edited';e.nodes.get('#ap-project-description').value='Notes';const save=e.H.actions['ap-save']();assert.equal(e.sent.at(-1).payload.project.name,'Edited');e.event('projects.changed');e.requests.findLast(r=>r.type==='projects.save').reject(new Error('disk locked'));await save;assert.equal(e.H.ui.modal,modal);assert.equal(e.nodes.get('#ap-project-name').value,'Edited');assert.match(e.nodes.get('#ap-error').textContent,/disk locked/);
});
test('Task6 within-type arrows only exchange that section occupied positions',async()=>{
  const e=setup();const p=project();p.items=[ref('app','a').item,ref('web','w').item,ref('app','b').item,ref('scene','s').item];await open(e,p,p.items.map(i=>({...ref(i.type,i.entityId||'w'),item:i})));e.H.actions['ap-edit']({dataset:{id:p.id}});e.nodes.get('#ap-project-name').value=p.name;e.nodes.get('#ap-project-description').value=p.description;e.H.actions['ap-draft-move']({dataset:{id:'item-b',direction:'-1'}});const save=e.H.actions['ap-save']();assert.deepEqual(e.sent.at(-1).payload.project.items.map(i=>i.id),['item-b','item-w','item-a','item-s']);await reply(e,'projects.save',{project:{...p,items:[p.items[2],p.items[1],p.items[0],p.items[3]]}});await save;
});
test('Task6 project remove confirmation explains association scope and failed remove retains detail',async()=>{
  const e=setup();await open(e);e.H.actions['ap-remove']({dataset:{id:'project-a'}});assert.match(e.H.ui.modal.body,/关联/);assert.match(e.H.ui.modal.body,/文件|应用/);const remove=e.H.actions['ap-remove-confirm']({dataset:{id:'project-a'}});e.requests.findLast(r=>r.type==='projects.remove').reject(new Error('blocked'));await remove;assert.equal(e.H.ui.page,'project');assert.ok(e.H.ui.modal);
});
test('Task6 selection search is type-specific, debounced and cannot replace a newer editor',async()=>{
  const e=setup();await open(e);e.H.actions['ap-edit']({dataset:{id:'project-a'}});e.H.actions['ap-add-type']({dataset:{type:'file'}});e.nodes.get('#ap-select-query').value='资料';e.emit('input',e.nodes.get('#ap-select-query'));await e.timers();assert.deepEqual(e.sent.at(-1),{type:'search.query',payload:{query:'资料',type:'file'}});e.H.closeModal();e.H.showModal('another','','');const modal=e.H.ui.modal;await reply(e,'search.query',[{type:'file',id:'f',name:'资料',status:'Active'}]);assert.equal(e.H.ui.modal,modal);assert.doesNotMatch(e.nodes.get('#ap-candidates').innerHTML,/data-id="f"/);
});
test('Task6 unsafe project text is escaped, deleted project fallback goes to list with a notice',async()=>{
  const e=setup();await open(e,project('project-a','<img src=x onerror=1>'));assert.match(e.content.innerHTML,/&lt;img/);assert.doesNotMatch(e.content.innerHTML,/<img/);e.window.DesktopHubProjects.open('deleted');await reply(e,'projects.query',{projects:[],project:null,items:[],warning:null});assert.equal(e.H.ui.page,'projects');assert.ok(e.calls.some(c=>c[0]==='toast'&&/删除|移除/.test(c[1])));
});

test('Task6 mixed file and folder arrows share the files section without moving web positions',async()=>{
  const e=setup(),p=project();p.items=[ref('file','f').item,ref('web','w').item,ref('folder','d').item];
  await open(e,p,p.items.map(i=>({...ref(i.type,i.entityId||'w'),item:i})));e.H.actions['ap-edit']({dataset:{id:p.id}});
  e.H.actions['ap-draft-move']({dataset:{id:'item-d',direction:'-1'}});const save=e.H.actions['ap-save']();
  assert.deepEqual(e.sent.at(-1).payload.project.items.map(i=>i.id),['item-d','item-w','item-f']);await reply(e,'projects.save',{project:p});await save;
});

test('Task6 creation flow retains the draft and returns to type selection after existing importer closes',async()=>{
  const e=setup();await open(e);e.H.actions['app-add']=()=>e.calls.push(['import-app']);e.H.actions['ap-edit']({dataset:{id:'project-a'}});
  e.nodes.get('#ap-project-name').value='Retained';e.H.actions['ap-add-type']({dataset:{type:'app'}});e.H.actions['ap-import-existing']();
  assert.ok(e.calls.some(c=>c[0]==='import-app'));assert.equal(e.H.ui.page,'home');
  e.H.actions['ap-resume-draft']();assert.match(e.H.ui.modal.title,/应用/);e.H.actions['ap-editor-back']();assert.match(e.H.ui.modal.body,/Retained/);
});

test('Task6 project scene creation uses the native constructor and returns a usable scene to the retained project draft',async()=>{
  // Keep both real constructors in index.html order: the legacy action still exists.
  const e=setup('workspace',false,true);
  e.H.actions['ap-new']();
  e.nodes.get('#ap-project-name').value='Retained project';
  e.nodes.get('#ap-project-description').value='Keep these notes';
  e.nodes.get('#ap-project-pin').checked=true;
  e.H.actions['ap-add-type']({dataset:{type:'web'}});
  e.nodes.get('#ap-web-url').value='https://example.com/project';e.nodes.get('#ap-web-label').value='Project notes';
  e.H.actions['ap-web-add']();
  e.H.actions['ap-add-type']({dataset:{type:'app'}});
  e.emit('input',e.nodes.get('#ap-select-query'));await e.timers();
  await reply(e,'search.query',[{type:'app',id:'app-a',name:'App',status:'Active'}]);
  e.H.actions['ap-select']({dataset:{id:'app-a'}});
  e.H.actions['ap-add-type']({dataset:{type:'scene'}});e.H.actions['ap-import-existing']();

  const scene=e.H.ui.sceneDraft;
  assert.equal(e.H.ui.page,'editor');assert.equal(e.H.ui.modal,null);
  assert.match(scene.id,/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);
  assert.notEqual(scene.id,'00000000-0000-0000-0000-000000000000');
  assert.equal(scene.editorMode,'simple');assert.equal(scene.executionMode,'simultaneous');
  assert.deepEqual(Array.from(scene.actions),[]);assert.deepEqual(Array.from(scene.dependencies),[]);
  assert.match(e.content.innerHTML,/场景编辑器/);
  e.H.actions['native-add-type']({dataset:{type:'application'}});
  assert.equal(scene.actions.length,1);assert.equal(scene.actions[0].appEntryId,'app-a');
  assert.equal(scene.actions[0].type,'application');assert.equal(scene.actions[0].target,'');
  assert.match(e.H.pages.editor(),/native-action-config/);assert.match(e.content.innerHTML,/App/);
  const saveScene=e.H.actions['native-scene-save']();
  const nativeSave=e.sent.findLast(r=>r.type==='scenes.save');assert.ok(nativeSave);
  assert.equal(nativeSave.payload.scene.id,scene.id);assert.equal(nativeSave.payload.scene.editorMode,'simple');
  assert.equal(nativeSave.payload.scene.executionMode,'simultaneous');assert.deepEqual(nativeSave.payload.scene.dependencies,[]);
  assert.equal(nativeSave.payload.scene.actions[0].appEntryId,'app-a');
  await reply(e,'scenes.save',{settings:{...e.settings,scenes:[...e.settings.scenes,nativeSave.payload.scene]}});await saveScene;
  assert.equal(e.H.ui.page,'scenes');assert.equal(e.H.ui.sceneDraftDirty,false);

  e.H.actions['ap-resume-draft']();assert.match(e.H.ui.modal.title,/场景/);
  e.emit('input',e.nodes.get('#ap-select-query'));await e.timers();
  await reply(e,'search.query',[{type:'scene',id:scene.id,name:'New native scene',status:'Active'}]);
  e.H.actions['ap-select']({dataset:{id:scene.id}});
  assert.match(e.H.ui.modal.body,/Retained project/);assert.match(e.H.ui.modal.body,/Keep these notes/);
  assert.match(e.H.ui.modal.body,/id="ap-project-pin"[^>]*checked/);
  assert.match(e.H.ui.modal.body,/Project notes/);assert.match(e.H.ui.modal.body,/App/);
  const saveProject=e.H.actions['ap-save']();
  const candidate=e.sent.findLast(r=>r.type==='projects.save').payload.project;
  assert.equal(candidate.name,'Retained project');assert.equal(candidate.description,'Keep these notes');assert.equal(candidate.pinnedToHome,true);
  assert.deepEqual(candidate.items.map(({type,entityId,url,name})=>({type,entityId,url,name})),[
    {type:'web',entityId:null,url:'https://example.com/project',name:'Project notes'},
    {type:'app',entityId:'app-a',url:null,name:null},
    {type:'scene',entityId:scene.id,url:null,name:null}
  ]);
  await reply(e,'projects.save',{project:candidate});await saveProject;
  assert.equal(e.H.ui.page,'project');assert.equal(e.H.ui.modal,null);
  assert.ok(!e.sent.some(r=>['scenes.run','apps.launch','resources.open'].includes(r.type)));
});

test('Task6 stale activity query cannot resurrect cleared rows and old project queries cannot undo saved names',async()=>{
  const e=setup();e.window.DesktopHubActivity.refresh();const old=e.requests.at(-1);e.H.actions['ap-clear']();const clear=e.H.actions['ap-clear-confirm']();await reply(e,'activity.clear',{cleared:true});await clear;
  old.resolve({items:[recent('file','old')],warning:null});await settle();assert.doesNotMatch(e.H.pages.activity(),/Current old/);
  await open(e);e.window.DesktopHubProjects.refresh();const oldProject=e.requests.at(-1);e.H.actions['ap-edit']({dataset:{id:'project-a'}});e.nodes.get('#ap-project-name').value='Saved';
  const save=e.H.actions['ap-save']();await reply(e,'projects.save',{project:project('project-a','Saved')});await save;
  oldProject.resolve({projects:[],project:project('project-a','Old'),items:[]});await settle();assert.match(e.H.pages.project(),/Saved/);
});

test('Task6 real history wrapper traverses project A B and folders with selected IDs and scroll intact',async()=>{
  const e=setup('workspace',true);e.H.render();await open(e,project('project-a'));e.content.scrollTop=123;
  await open(e,project('project-b'));e.content.scrollTop=245;
  e.H.navigate('folder');e.window.DesktopHubInteractions.navigateHistory(-1);
  assert.equal(e.H.ui.page,'project');assert.equal(e.window.DesktopHubProjects.navigationState().projectId,'project-b');
  await reply(e,'projects.query',{projects:[],project:project('project-b'),items:[]});assert.equal(e.content.scrollTop,245);
  e.window.DesktopHubInteractions.navigateHistory(-1);await reply(e,'projects.query',{projects:[],project:project('project-a'),items:[]});assert.equal(e.content.scrollTop,123);
  e.window.DesktopHubInteractions.navigateHistory(1);await reply(e,'projects.query',{projects:[],project:project('project-b'),items:[]});assert.equal(e.content.scrollTop,245);
});

test('Task6 scene execution events do not requery metadata or rebuild project drafts',async()=>{
  const e=setup();await open(e);e.H.actions['ap-edit']({dataset:{id:'project-a'}});const modal=e.H.ui.modal,count=e.sent.length;
  e.event('scene.executions',{executions:[{id:'run'}]});e.event('state.changed',{settings:e.settings,executions:[{id:'run'}]});
  assert.equal(e.sent.length,count);assert.equal(e.H.ui.modal,modal);
});

test('Task6 native write failures keep technical paths under diagnostics with a human recovery message',async()=>{
  const e=setup();await open(e);e.H.actions['ap-edit']({dataset:{id:'project-a'}});const save=e.H.actions['ap-save']();
  const err=new Error('System.IO.IOException: E:/private/projects.json');err.code='NATIVE_ERROR';e.requests.findLast(r=>r.type==='projects.save').reject(err);await save;
  assert.match(e.nodes.get('#ap-error').textContent,/重试|权限/);assert.doesNotMatch(e.nodes.get('#ap-error').textContent,/System\.IO|private/);assert.match(e.nodes.get('#ap-error-diagnostic').textContent,/System\.IO/);
});

test('Task6 dismissed save reconciles its committed cache after an event query starts before the reply',async()=>{
  const e=setup();await open(e);e.H.actions['ap-edit']({dataset:{id:'project-a'}});e.nodes.get('#ap-project-name').value='Saved after close';
  const save=e.H.actions['ap-save']();e.H.closeModal();e.event('projects.changed');const eventQuery=e.requests.at(-1);
  const committed=project('project-a','Saved after close');committed.items=[ref('app','app-a').item];
  await reply(e,'projects.save',{project:committed});await save;
  assert.match(e.H.pages.projects(),/Saved after close/);assert.match(e.H.pages.projects(),/1 个关联/);
  const reconciliation=e.requests.at(-1);assert.equal(reconciliation.type,'projects.query');assert.notEqual(reconciliation,eventQuery);
  eventQuery.done=true;eventQuery.resolve({projects:[summary(project())],project:project(),items:[]});await settle();
  e.H.actions['ap-edit']({dataset:{id:'project-a'}});assert.match(e.H.ui.modal.body,/Saved after close/);assert.match(e.H.ui.modal.body,/1 \/ 100/);
  e.H.closeModal();reconciliation.done=true;reconciliation.resolve({projects:[],project:null,items:[]});await settle();
  assert.equal(e.H.ui.page,'projects');assert.doesNotMatch(e.H.pages.projects(),/正在读取/);assert.match(e.H.pages.projects(),/创建项目/);
});

test('Task6 committed save refresh preserves a newer project modal and its unsaved draft',async()=>{
  const e=setup();await open(e);e.H.actions['ap-edit']({dataset:{id:'project-a'}});e.nodes.get('#ap-project-name').value='Committed';
  const save=e.H.actions['ap-save']();e.H.closeModal();e.H.actions['ap-new']();e.nodes.get('#ap-project-name').value='New unsaved draft';
  const modal=e.H.ui.modal,page=e.H.ui.page;e.event('projects.changed');const eventQuery=e.requests.at(-1);
  const committed=project('project-a','Committed');await reply(e,'projects.save',{project:committed});await save;
  assert.equal(e.H.ui.modal,modal);assert.equal(e.H.ui.page,page);assert.equal(e.nodes.get('#ap-project-name').value,'New unsaved draft');
  assert.match(e.H.pages.projects(),/Committed/);assert.notEqual(e.requests.at(-1),eventQuery);
  await reply(e,'projects.query',{projects:[summary(committed)],project:committed,items:[]});
  assert.equal(e.H.ui.modal,modal);assert.equal(e.nodes.get('#ap-project-name').value,'New unsaved draft');
  const secondSave=e.H.actions['ap-save']();assert.equal(e.sent.at(-1).payload.project.name,'New unsaved draft');
  assert.notEqual(e.sent.at(-1).payload.project.id,committed.id);
  e.requests.findLast(r=>r.type==='projects.save').reject(new Error('keep draft'));await secondSave;
});

test('Task6 late project history response cannot change a newer activity scroll or its checkpoint',async()=>{
  const e=setup('workspace',true);e.H.render();await open(e,project('project-a'));e.content.scrollTop=123;
  await open(e,project('project-b'));e.content.scrollTop=245;e.H.navigate('folder');e.window.DesktopHubInteractions.navigateHistory(-1);
  const restoring=e.requests.at(-1);e.H.navigate('activity');e.content.scrollTop=17;e.window.DesktopHubInteractions.checkpoint();
  const before=structuredClone(e.window.DesktopHubInteractions.getState());
  restoring.done=true;restoring.resolve({projects:[summary(project('project-b','Background update'))],project:project('project-b','Background update'),items:[]});await settle();
  assert.equal(e.H.ui.page,'activity');assert.equal(e.content.scrollTop,17);
  const after=e.window.DesktopHubInteractions.getState();assert.equal(after.index,before.index);assert.equal(after.entries[after.index].scroll,17);assert.equal(after.entries[after.index].key,'activity');
  assert.match(e.H.pages.projects(),/Background update/);
});

test('Task6 ordinary reentry to the same project supersedes an earlier history restoration token',async()=>{
  const e=setup('workspace',true);e.H.render();await open(e,project('project-a'));e.content.scrollTop=123;
  await open(e,project('project-b'));e.content.scrollTop=245;e.H.navigate('folder');e.window.DesktopHubInteractions.navigateHistory(-1);
  const restoring=e.requests.at(-1);e.H.navigate('activity');e.H.navigate('project');e.content.scrollTop=31;e.window.DesktopHubInteractions.checkpoint();
  restoring.done=true;restoring.resolve({projects:[],project:project('project-b'),items:[]});await settle();
  assert.equal(e.H.ui.page,'project');assert.equal(e.content.scrollTop,31);
  const history=e.window.DesktopHubInteractions.getState();assert.equal(history.entries[history.index].key,'project:project-b');assert.equal(history.entries[history.index].scroll,31);
});
test('Task8 return-to-project draft remains busy outside its modal and snapshot contains only saved identity',async()=>{
  const e=setup();e.H.actions['ap-new']();e.H.actions['ap-add-type']({dataset:{type:'scene'}});e.H.actions['ap-import-existing']();
  assert.equal(e.H.ui.modal,null);assert.ok(e.window.DesktopHubProjects.lifecycleBusyReasons().includes('project-draft'));
  assert.deepEqual(structuredClone(e.window.DesktopHubProjects.captureLifecycle()),{projectId:null});
});
