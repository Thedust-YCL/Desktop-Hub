import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
const source=fs.readFileSync(new URL('../window-layout.js',import.meta.url),'utf8');
function setup(surface='workspace',layoutResults=[]){
 const sent=[];let answer={captureToken:'capture',candidates:[{candidateToken:'candidate',title:'<Document>',appIds:['app'],bounds:{left:10,top:20,width:400,height:300},state:'normal',dpi:192,skipReason:''}],truncated:false},handler;
 const H={ui:{},actions:{},escape:v=>String(v??'').replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('"','&quot;'),render(){},toast(m){H.toastText=m;},showModal(title,body,footer){H.ui.modal={};H.modal={title,body,footer};},closeModal(){H.ui.modal=null;H.modal=null;}};
 const desktop={getState:()=>({surface,hostState:{layoutResults,settings:{appEntries:[{id:'app',name:'App'}],scenes:[{id:'scene',windowLayout:{restoreAfterRun:false,waitTimeoutMs:15000}}]}}}),applyHostState(v){desktop.applied=v;}};
 const bridge={async request(type,payload){sent.push(structuredClone({type,payload}));return handler?handler(type,payload):answer;},on(){}};
 const window={Hub:H,DesktopHubDesktop:desktop,DesktopHubNative:{bridge}};vm.runInContext(source,vm.createContext({window,document:{},console}));
 return{H,sent,api:window.DesktopHubWindowLayout,setAnswer:a=>answer=a,setHandler:h=>handler=h};
}
test('capture saves checked candidate IDs and title but no coordinates or executable authority',async()=>{
 const e=setup();await e.api.capture('scene');assert.match(e.H.modal.body,/&lt;Document>/);assert.match(e.H.modal.body,/400/);
 e.H.actions['layout-pick']({checked:true,dataset:{token:'candidate'}});e.H.actions['layout-title']({value:'document',dataset:{token:'candidate'}});
 e.setAnswer({settings:{}});await e.H.actions['layout-save']();assert.deepEqual(e.sent.at(-1),{type:'scenes.layoutSave',payload:{sceneId:'scene',captureToken:'capture',selections:[{candidateToken:'candidate',appEntryId:'app',titleContains:'document'}],restoreAfterRun:false,waitTimeoutMs:15000}});assert.equal(e.H.modal,null);
});
test('capture refuses candidates associated with multiple apps until user selects app',async()=>{
 const e=setup();e.setAnswer({captureToken:'capture',candidates:[{candidateToken:'candidate',title:'A',appIds:['app','other'],bounds:{},state:'normal',skipReason:''}]});await e.api.capture('scene');e.H.actions['layout-pick']({checked:true,dataset:{token:'candidate'}});await e.H.actions['layout-save']();assert.equal(e.sent.length,1);assert.match(e.H.toastText,/应用/);
});
test('late capture response never replaces a newer modal',async()=>{
 const e=setup();let finish;e.setHandler(()=>new Promise(r=>finish=r));const pending=e.api.capture('scene');e.H.showModal('new editor','draft','');finish({captureToken:'c',candidates:[]});await pending;assert.equal(e.H.modal.title,'new editor');
});
test('automatic result updates stay quiet and explicit result choice sends tokens only',async()=>{
 const e=setup();const result={sceneId:'scene',restoreToken:'restore',entries:[{entryToken:'entry',appEntryId:'app',status:'needsChoice',code:'multipleWindows',message:'choose',candidates:[{candidateToken:'one',title:'one'},{candidateToken:'two',title:'two'}]}]};e.api.receiveResults([result]);assert.equal(e.H.modal,undefined);e.api.openResult(result);assert.match(e.H.modal.body,/选择/);e.H.actions['layout-choice']({value:'two',dataset:{entry:'entry'}});e.setAnswer({sceneId:'scene',entries:[]});await e.H.actions['layout-choose']();assert.deepEqual(e.sent.at(-1),{type:'scenes.layoutChoose',payload:{restoreToken:'restore',choices:[{entryToken:'entry',candidateToken:'two'}]}});
});
test('manual restore is blocked while an editor modal is open and non-workspace has no controls',async()=>{
 const e=setup();e.H.showModal('editor','','');await e.api.restore('scene');assert.equal(e.sent.length,0);assert.equal(setup('dock').api.controls('scene'),'');
});
test('empty capture explains no matching windows and offers recapture',async()=>{
 const e=setup();e.setAnswer({captureToken:'c',candidates:[],truncated:false});await e.api.capture('scene');assert.match(e.H.modal.body,/没有/);assert.match(e.H.modal.footer,/重新捕获/);
});

test('manual result remains latest after automatic-only or empty retained host refresh',async()=>{
 const e=setup();const automatic={resultId:'automatic',order:1,kind:'automatic',executionId:'real',result:{sceneId:'scene',entries:[{status:'failed',message:'older automatic',candidates:[]}]}};
 e.api.receiveResults([automatic]);e.setAnswer({sceneId:'scene',resultId:'manual',order:2,kind:'manual',entries:[{status:'restored',message:'latest manual',candidates:[]}]});await e.api.restore('scene');e.H.closeModal();e.api.receiveResults([automatic]);e.api.receiveResults([]);e.H.actions['layout-results']({dataset:{id:'scene'}});assert.match(e.H.modal.body,/latest manual/);
});
test('host manual history survives lightweight module rebuilding and exact manual choice remains latest',async()=>{
 const automatic={resultId:'automatic',order:1,kind:'automatic',executionId:'real',result:{sceneId:'scene',entries:[{status:'failed',message:'frozen automatic',candidates:[]}]}};
 const manual={resultId:'manual',order:4,kind:'manual',executionId:null,result:{sceneId:'scene',entries:[{status:'restored',message:'completed manual choice',candidates:[]}]}};
 const e=setup('workspace',[manual,automatic]);e.H.actions['layout-results']({dataset:{id:'scene'}});assert.match(e.H.modal.body,/completed manual choice/);assert.equal(e.api.getState().resultCount,2);
});


test('legacy automatic wrapper refresh keeps its original position behind later manual outcome',async()=>{
 const e=setup();const automatic={executionId:'real',result:{sceneId:'scene',entries:[{entryToken:'auto-entry',status:'failed',message:'legacy automatic',candidates:[]}]}};
 e.api.receiveResults([automatic]);e.setAnswer({sceneId:'scene',entries:[{entryToken:'manual-entry',status:'restored',message:'later manual',candidates:[]}]});await e.api.restore('scene');e.H.closeModal();e.api.receiveResults([automatic]);e.H.actions['layout-results']({dataset:{id:'scene'}});assert.match(e.H.modal.body,/later manual/);
});
