import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { randomUUID } from 'node:crypto';

const source = fs.readFileSync(new URL('../scenes.js', import.meta.url), 'utf8');
function setup(surface = 'workspace') {
  const settings = { scenes: [], appEntries: [{ id: randomUUID(), name: '应用 A', target: 'C:\\A.exe' }, { id: randomUUID(), name: '应用 B', target: 'C:\\B.exe' }], launcherPins: [] };
  const sent = [], subscribers = new Map(); let response = {};
  const H = { state: { scenes: [] }, ui: { page: 'scenes' }, pages: {}, actions: {}, render() {}, navigate(page) { H.ui.page = page; },
    icon: () => '<svg></svg>', escape: value => String(value ?? '').replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char])),
    toast(message) { H.lastToast = message; }, closeModal() {}, showModal(title, body, footer) { H.modal = { title, body, footer }; }, setSurface() {} };
  const bridge = { async request(type, payload) { sent.push({ type, payload: structuredClone(payload) });
    if (type === 'scenes.save') { settings.scenes = [structuredClone(payload.scene)]; return { settings }; }
    if (type === 'scenes.run') return response;
    return { settings };
  }, on(type, handler) { subscribers.set(type, handler); } };
  const document = { querySelector: () => null, querySelectorAll: () => [], addEventListener() {} };
  const window = { Hub: H, crypto: { randomUUID }, DesktopHubNative: { bridge }, DesktopHubDesktop: { getState: () => ({ surface, hostState: { settings } }), applyHostState(result) { Object.assign(settings, result.settings); } } };
  vm.runInContext(source, vm.createContext({ window, document, console, Date }));
  return { H, window, settings, sent, emit: (type, payload) => subscribers.get(type)(payload), setResponse: next => { response = next; } };
}
const click = id => ({ dataset: { id } });
function create(env, count = 2) { env.H.actions['new-scene'](); for (let i = 0; i < count; i++) env.H.actions['native-add-type']({ dataset: { type: 'application' } }); return env.H.ui.sceneDraft; }
test('native editor selects AppEntry ids, creates explicit sequence edges, and reordering leaves dependencies unchanged', async () => {
  const env = setup(); const draft = create(env, 5);
  assert.equal(draft.actions.length, 5); assert.ok(draft.actions.every(action => action.appEntryId === env.settings.appEntries[0].id && !action.target));
  env.H.actions['native-sequence'](); const edges = JSON.stringify(draft.dependencies);
  assert.equal(draft.dependencies.length, 4);
  env.H.actions['native-action-move']({ dataset: { id: draft.actions[0].id, direction: '1' } }); assert.equal(JSON.stringify(draft.dependencies), edges);
  await env.H.actions['native-scene-save'](); assert.equal(env.sent[0].type, 'scenes.save'); assert.equal(env.sent[0].payload.scene.actions.length, 5); assert.equal(env.H.ui.sceneDraftDirty, false);
});
test('each of five action types receives real configuration with default ten-second waits and script repeat rule', () => {
  const env = setup(); create(env, 0);
  for (const type of ['application', 'web', 'file', 'folder', 'script']) env.H.actions['native-add-type']({ dataset: { type } });
  const actions = env.H.ui.sceneDraft.actions; assert.deepEqual(Array.from(actions, action => action.type), ['application', 'web', 'file', 'folder', 'script']);
  assert.ok(actions.every(action => action.timeoutMs === 10000 && action.repeatPolicy === 'default'));
  env.H.actions['native-script-arguments']({ value: 'two words\n--flag' }); assert.deepEqual(Array.from(actions.at(-1).argumentList), ['two words', '--flag']);
});
test('invalid app references have a recovery option and re-association removes copied paths', () => {
  const env = setup(); const draft = create(env, 1); draft.actions[0].appEntryId = randomUUID();
  assert.match(env.H.pages.editor(), /引用失效/);
  env.H.actions['native-action-app']({ value: env.settings.appEntries[1].id }); assert.equal(draft.actions[0].name, '应用 B'); assert.equal(draft.actions[0].target, ''); assert.equal(draft.actions[0].referenceInvalid, false);
});
test('Mini, Dock and Workspace request new scene execution by id and never launch individual apps', async () => {
  for (const surface of ['mini', 'dock', 'workspace']) {
    const env = setup(surface); const id = randomUUID(); env.setResponse({ execution: { id: 'execution', sceneId: id, actions: [] }, executions: [] });
    await env.H.actions['run-scene'](click(id)); await env.H.actions['run-scene'](click(id));
    assert.deepEqual(env.sent.map(item => item.type), ['scenes.run', 'scenes.run']); assert.deepEqual(env.sent[0].payload, { sceneId: id });
  }
});
test('all surfaces consume shared execution events and render truthful skipped/failed results with escaped errors', () => {
  const execution = { id: 'x', sceneId: 's', sceneName: '场景', status: 'PartialSuccess', summary: '部分成功', actions: [ { actionId: 'a', name: '应用', status: 'Skipped', code: 'already_running', message: '已运行，跳过启动', processId: 10 }, { actionId: 'b', name: '窗口', status: 'Failed', code: 'wait_window_timeout', message: '<error>10 秒' } ] };
  for (const surface of ['mini', 'dock', 'workspace']) {
    const env = setup(surface); env.emit('scene.executions', { executions: [execution] });
    assert.deepEqual(JSON.parse(JSON.stringify(env.window.DesktopHubScenes.getExecutions())), [execution]);
    const markup = env.window.DesktopHubScenes.executionMarkup(execution); assert.match(markup, /已跳过/); assert.match(markup, /wait_window_timeout/); assert.match(markup, /&lt;error&gt;10 秒/); assert.doesNotMatch(markup, /模拟/);
  }
});
test('disabled scenes, real graph relationships and custom symbols are visible in the scene center', () => {
  const env = setup(); const draft = create(env); draft.enabled = false; draft.icon = '☕'; env.settings.scenes = [draft];
  const markup = env.H.pages.scenes(); assert.match(markup, /已停用/); assert.match(markup, /动作彼此独立/); assert.match(markup, /☕/); assert.match(markup, /data-action="run-scene"[^>]+disabled/);
});
