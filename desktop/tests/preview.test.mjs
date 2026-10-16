import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import {randomUUID} from 'node:crypto';
const source=()=>fs.readFileSync(new URL('../preview.js',import.meta.url),'utf8');
function setup(realTimeoutBridge=false){
 const events=new Map(),sent=[],nodes=[],pending=new Map(),thumbs=[]; let active=null,observer;
 const make=()=>({dataset:{},classList:{toggle(){}},innerHTML:'',isConnected:true,replaceChildren(...children){this.children=children;},querySelector(){return {focus(){active=this;},addEventListener(){}};},querySelectorAll(){return [];},addEventListener(){},remove(){this.isConnected=false;},focus(){active=this;}});
 const document={hidden:false,body:{appendChild(node){nodes.push(node);}},createElement:make,querySelectorAll(selector){return selector.includes('data-preview-visible')?thumbs.filter(n=>n.dataset.previewVisible==='true'):selector.includes('data-preview-thumb')?thumbs:[];},querySelector(){return null;},get activeElement(){return active;},addEventListener(type,fn){events.set(type,fn);}};
 let bridge={request(type,payload){sent.push({type,payload}); if(type==='resources.previewCancel')return Promise.resolve({cancelled:true}); return new Promise((resolve,reject)=>pending.set(payload.requestId,{resolve,reject}));}};
 const timers=[];let receiveNative;
 if(realTimeoutBridge){
  const nativeRoot={console,setTimeout(fn,delay){timers.push({fn,delay});return timers.length;},clearTimeout(){}};
  vm.runInContext(fs.readFileSync(new URL('../native.js',import.meta.url),'utf8'),vm.createContext(nativeRoot));
  bridge=nativeRoot.DesktopHubNative.createBridge({postMessage(message){sent.push(message);if(message.type==='resources.previewCancel')receiveNative({data:{version:1,id:message.id,ok:true,result:{cancelled:true}}});},addEventListener(type,callback){receiveNative=callback;}});
 }
 const H={ui:{page:'library',modal:null},actions:{'nr-open'(){}},escape:String,render(){},toast(){}};
 const window={Hub:H,crypto:{randomUUID},DesktopHubNative:{bridge},DesktopHubDesktop:{getState:()=>({surface:'workspace'})},DesktopHubResources:{getState:()=>({selectedIds:[]})}};
 window.IntersectionObserver=class{constructor(callback){this.callback=callback;observer=this;}observe(){}disconnect(){}};
 vm.runInContext(source(),vm.createContext({window,document,console,setTimeout,clearTimeout,queueMicrotask}));
 return {window,H,document,sent,nodes,pending,events,make,thumbs,timers,emitNative:message=>receiveNative({data:message}),get observer(){return observer;}};
}
test('Space opens selected stable resource; input and IME leave typing untouched',()=>{
 const env=setup(),p=env.window.DesktopHubPreview; p.select({id:'registered',displayName:'图.png'},env.make());
 let prevented=0;const event={key:' ',preventDefault(){prevented++;},target:{closest(){return null;}}};
 env.events.get('keydown')({...event,isComposing:true});env.events.get('keydown')({...event,target:{closest(){return {};}}});assert.equal(env.sent.length,0);
 env.events.get('keydown')(event);assert.equal(prevented,1);assert.equal(env.sent[0].type,'resources.previewContent');assert.equal(env.sent[0].payload.resourceId,'registered');assert.equal(env.sent[0].payload.thumbnail,false);
});
test('Esc cancels just the active request and restores original focus',()=>{
 const env=setup(),p=env.window.DesktopHubPreview,focus=env.make();p.select({id:'registered',displayName:'x'},focus);p.open();assert.equal(p.handleEscape(),true);assert.equal(env.document.activeElement,focus);assert.equal(env.sent.at(-1).type,'resources.previewCancel');assert.equal(p.handleEscape(),false);
});
test('hidden or superseded request never inserts its late PNG response',async()=>{
 const env=setup(),p=env.window.DesktopHubPreview;p.select({id:'a',displayName:'A'},env.make());p.open();const a=env.sent[0].payload.requestId;p.suspend();env.pending.get(a).resolve({png:'iVBORw0KGgoAAA',width:1,height:1,detail:'图片'});await Promise.resolve();await Promise.resolve();assert.ok(env.nodes.every(n=>!n.isConnected));assert.equal(env.sent.at(-1).type,'resources.previewCancel');
});
test('preview has recoverable error and external-open action',async()=>{
 const env=setup(),p=env.window.DesktopHubPreview;p.select({id:'a',displayName:'A'},env.make());const opened=p.open();env.pending.get(env.sent[0].payload.requestId).reject(new Error('文件已移动'));await opened;assert.match(env.nodes[0].innerHTML,/文件已移动/);assert.match(env.nodes[0].innerHTML,/外部打开/);
});
test('unrelated theme render preserves preview and its pending content response',async()=>{
 const env=setup(),p=env.window.DesktopHubPreview;p.select({id:'a',displayName:'A'},env.make());const opened=p.open();
 env.H.state={settings:{theme:'light'}};env.H.render();assert.equal(p.isOpen(),true);assert.equal(env.sent.filter(x=>x.type==='resources.previewCancel').length,0);
 env.pending.get(env.sent[0].payload.requestId).resolve({png:'iVBORw0KGgoAAA',width:1,height:1,detail:'图片'});await opened;assert.match(env.nodes[0].innerHTML,/rp-image/);assert.equal(p.handleEscape(),true);
});
test('page change cancels active content before a late result can replace navigation',async()=>{
 const env=setup(),p=env.window.DesktopHubPreview;p.select({id:'a',displayName:'A'},env.make());const opened=p.open();env.H.ui.page='home';env.H.render();assert.equal(p.isOpen(),false);assert.equal(env.sent.at(-1).type,'resources.previewCancel');env.pending.get(env.sent[0].payload.requestId).resolve({png:'iVBORw0KGgoAAA',width:1,height:1});await opened;assert.ok(env.nodes.every(n=>!n.isConnected));
});
test('only intersecting grid icons render, with two requests and no queued offscreen page',async()=>{
 const env=setup(),p=env.window.DesktopHubPreview;for(let i=0;i<5;i++){const node=env.make();node.dataset.previewThumb='r'+i;env.thumbs.push(node);}p.refresh();env.observer.callback(env.thumbs.map((target,i)=>({target,isIntersecting:i<3})));
 assert.equal(env.sent.length,2);assert.deepEqual(env.sent.map(x=>x.payload.resourceId),['r0','r1']);
 env.pending.get(env.sent[0].payload.requestId).resolve({png:'iVBORw0KGgoAAA',width:1,height:1});await new Promise(setImmediate);assert.equal(env.sent.length,3);assert.equal(env.sent[2].payload.resourceId,'r2');assert.ok(env.thumbs[0].children[0].src.startsWith('data:image/png;base64,'));
 p.suspend();assert.equal(env.sent.filter(x=>x.type==='resources.previewCancel').length,2);assert.ok(!env.sent.some(x=>['r3','r4'].includes(x.payload.resourceId)));
});


test('actual bridge content timeout cancels its native request before Esc or page change loses identity',async()=>{
 const env=setup(true),preview=env.window.DesktopHubPreview;preview.select({id:'registered',displayName:'slow.png'},env.make());const opened=preview.open();
 const request=env.sent[0];env.timers.find(timer=>timer.delay===120000).fn();await opened;
 assert.deepEqual(env.sent.filter(item=>item.type==='resources.previewCancel').map(item=>item.payload.requestId),[request.payload.requestId]);
 assert.match(env.nodes[0].innerHTML,/暂不能预览/);preview.handleEscape();env.H.ui.page='home';env.H.render();
 env.emitNative({version:1,id:request.id,ok:true,result:{png:'iVBORw0KGgoAAA',width:1,height:1,detail:'late'}});await Promise.resolve();
 assert.ok(env.nodes.every(node=>!node.isConnected));assert.equal(env.sent.filter(item=>item.type==='resources.previewCancel').length,1);
});
test('actual bridge thumbnail timeout cancels only that native request before freeing the frontend slot',async()=>{
 const env=setup(true),preview=env.window.DesktopHubPreview;for(let i=0;i<3;i++){const node=env.make();node.dataset.previewThumb='r'+i;env.thumbs.push(node);}preview.refresh();env.observer.callback(env.thumbs.map(target=>({target,isIntersecting:true})));
 const first=env.sent[0],second=env.sent[1];env.timers.find(timer=>timer.delay===120000).fn();await new Promise(setImmediate);
 const cancellation=env.sent.find(item=>item.type==='resources.previewCancel');assert.ok(cancellation,'native timeout must be explicitly cancelled');assert.equal(cancellation.payload.requestId,first.payload.requestId);
 assert.ok(!env.sent.some(item=>item.type==='resources.previewCancel'&&item.payload.requestId===second.payload.requestId));
 const next=env.sent.find(item=>item.type==='resources.previewContent'&&item.payload.resourceId==='r2');assert.ok(next,'visible queued icon may use the freed frontend slot');assert.ok(env.sent.indexOf(cancellation)<env.sent.indexOf(next));
 env.emitNative({version:1,id:first.id,ok:true,result:{png:'iVBORw0KGgoAAA',width:1,height:1}});await Promise.resolve();assert.equal(env.thumbs[0].children,undefined);
});
