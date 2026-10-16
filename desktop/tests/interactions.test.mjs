import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
const source = fs.readFileSync(new URL('../interactions.js', import.meta.url), 'utf8');
function setup() {
  const events = new Map(), nativeEvents = new Map(), sent = [];
  const content = { scrollTop: 0 };
  let resources = { query: '', tagIds: [], folder: null, extension: '', category: '' };
  let project = { projectId: null };
  const H = { ui: { page: 'home' }, state: { settings: {} }, actions: {}, render() {},
    navigate(page) { H.ui.page = page; resources.query = ''; content.scrollTop = 0; H.render(); },
    escape: String, icon: () => '', toast() {}, closeModal() { H.ui.modal = null; },
    showModal(title, body, footer) { H.ui.modal = { title, body, footer }; } };
  const document = { activeElement: null, querySelector: selector => selector === '.workspace-content' ? content : null,
    querySelectorAll: () => [], addEventListener(type, callback) { events.set(type, callback); } };
  const bridge = { on(type, callback) { nativeEvents.set(type, callback); }, async request(type, payload) { sent.push({ type, payload }); } };
  const window = { Hub: H, DesktopHubNative: { bridge }, DesktopHubDesktop: { getState: () => ({ surface: 'workspace' }) },
    DesktopHubResources: { navigationState: () => structuredClone(resources), restoreNavigation(value) { resources = structuredClone(value); H.render(); } },
    DesktopHubProjects: { navigationState: () => structuredClone(project), restoreNavigation(value) { project = { projectId: value.projectId }; } } };
  vm.runInNewContext(source, { window, document, Date }); H.render();
  return { H, content, window, sent, nativeEvents, resources: () => resources, project: () => project,
    emit(type, event) { events.get(type)?.({ preventDefault() {}, stopImmediatePropagation() {}, target: { closest: () => null }, ...event }); } };
}
test('history restores resource filters and scroll; a new destination removes the forward branch', () => {
  const env = setup(); env.H.navigate('library');
  Object.assign(env.resources(), { query: 'notes', tagIds: ['project'], extension: '.txt', category: 'custom' });
  env.content.scrollTop = 240; env.H.navigate('missing'); env.H.actions['history-back']();
  assert.equal(env.H.ui.page, 'library'); assert.equal(env.resources().query, 'notes');
  assert.deepEqual(env.resources().tagIds, ['project']); assert.equal(env.resources().extension, '.txt');
  assert.equal(env.content.scrollTop, 240); env.H.navigate('settings');
  env.H.actions['history-forward'](); assert.equal(env.H.ui.page, 'settings');
});

test('Task6 history distinguishes project details and restores project identity before page rendering', () => {
  const env = setup(); env.project().projectId = 'project-a'; env.H.navigate('project');
  env.content.scrollTop = 115; env.window.DesktopHubInteractions.checkpoint();
  env.project().projectId = 'project-b'; env.H.navigate('project'); env.content.scrollTop = 235;
  env.resources().folder = { path: 'C:/Owned', token: 'folder' }; env.H.navigate('folder');
  env.H.actions['history-back'](); assert.equal(env.H.ui.page, 'project'); assert.equal(env.project().projectId, 'project-b'); assert.equal(env.content.scrollTop, 235);
  env.H.actions['history-back'](); assert.equal(env.project().projectId, 'project-a'); assert.equal(env.content.scrollTop, 115);
  env.H.actions['history-forward'](); assert.equal(env.project().projectId, 'project-b');
  const keys = env.window.DesktopHubInteractions.getState().entries.map(entry => entry.key);
  assert.ok(keys.includes('project:project-a')); assert.ok(keys.includes('project:project-b'));
});
test('mouse side buttons and native browser commands traverse pages; duplicate delivery is ignored', () => {
  const env = setup(); env.H.navigate('library'); env.H.navigate('missing');
  env.emit('pointerup', { button: 3 }); assert.equal(env.H.ui.page, 'library');
  env.nativeEvents.get('window.historyNavigate')({ direction: -1 }); assert.equal(env.H.ui.page, 'library');
  env.H.actions['history-forward'](); assert.equal(env.H.ui.page, 'missing');
  env.emit('keydown', { altKey: true, key: 'ArrowLeft' }); assert.equal(env.H.ui.page, 'library');
});
test('nested folder history restores the previous browse token and path', () => {
  const env = setup(); env.H.navigate('library');
  env.resources().folder = { path: 'C:/Owned', token: 'parent' }; env.H.navigate('folder');
  env.window.DesktopHubInteractions.checkpoint();
  env.resources().folder = { path: 'C:/Owned/sub', token: 'child' }; env.H.navigate('folder');
  env.H.actions['history-back'](); assert.equal(env.resources().folder.token, 'parent');
  env.H.actions['history-forward'](); assert.equal(env.resources().folder.token, 'child');
});
test('history protects unsaved scene edits and locked dialogs', () => {
  const env = setup(); env.H.navigate('editor'); env.H.ui.sceneDraftDirty = true;
  env.H.actions['history-back'](); assert.equal(env.H.ui.page, 'editor');
  assert.match(env.H.ui.modal.footer, /history-discard/);
  env.H.actions['history-discard'](); assert.equal(env.H.ui.page, 'home');
  env.H.ui.modal = { locked: true }; env.H.actions['history-forward'](); assert.equal(env.H.ui.page, 'home');
});
test('resize handles request an enumerated native edge', async () => {
  const env = setup(); env.emit('pointerdown', { button: 0, target: { closest: () => ({ dataset: { resizeEdge: 'bottom-right' } }) } });
  await Promise.resolve(); assert.deepEqual(structuredClone(env.sent[0]), { type: 'window.beginResize', payload: { edge: 'bottom-right' } });
});
test('Task8 lifecycle history is bounded and never exports session folder authorization or editor actions', () => {
  const env=setup();env.H.navigate('library');env.resources().folder={path:'C:/private',token:'lease'};env.H.navigate('folder');env.H.navigate('editor');
  for(let i=0;i<40;i++)env.H.navigate(i%2?'settings':'projects');
  const state=env.window.DesktopHubInteractions.captureLifecycle();assert.equal(state.entries.length,32);assert.equal(state.index,31);
  assert.doesNotMatch(JSON.stringify(state),/private|lease|folder|editor/);
  const rebuilt=setup();assert.equal(rebuilt.window.DesktopHubInteractions.restoreLifecycle(state),true);rebuilt.window.DesktopHubInteractions.navigateHistory(-1);assert.equal(rebuilt.H.ui.page,'projects');
});
test('Task8 bounded lifecycle history keeps the current route when it is far behind the newest entry',()=>{
 const env=setup();for(let i=0;i<40;i++){env.resources().query='q'+i;env.H.navigate(i%2?'settings':'library');}
 for(let i=0;i<35;i++)env.window.DesktopHubInteractions.navigateHistory(-1);
 const current=env.resources().query;const state=env.window.DesktopHubInteractions.captureLifecycle();assert.equal(state.entries[state.index].resources.query,current);
});
