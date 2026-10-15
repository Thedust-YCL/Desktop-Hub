import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { randomUUID } from 'node:crypto';

const source = fs.readFileSync(new URL('../scenes.js', import.meta.url), 'utf8');
function setup(surface = 'workspace') {
  const settings = { scenes: [], appEntries: [{ id: randomUUID(), name: '应用 A', target: 'C:\\A.exe' }, { id: randomUUID(), name: '应用 B', target: 'C:\\B.exe' }], launcherPins: [] };
  const sent = [], subscribers = new Map(), inserts = []; let response = {}, handler;
  const H = { state: { scenes: [] }, ui: { page: 'scenes' }, pages: {}, actions: {}, render() { H.renderCount = (H.renderCount || 0) + 1; }, navigate(page) { H.ui.page = page; },
    icon: () => '<svg></svg>', escape: value => String(value ?? '').replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char])),
    toast(message) { H.lastToast = message; }, closeModal() { H.ui.modal = null; H.modal = null; }, showModal(title, body, footer) { H.ui.modal = {}; H.modal = { title, body, footer }; }, setSurface(value) { H.lastSurface = value; } };
  H.actions['app-edit'] = el => { H.repairedAppId = el.dataset.id; H.showModal('编辑应用', '', ''); };
  const bridge = { async request(type, payload) { sent.push({ type, payload: structuredClone(payload) });
    if (handler) { const answer = await handler(type, payload); if (answer !== undefined) return answer; }
    if (type === 'scenes.save') { settings.scenes = [structuredClone(payload.scene)]; return { settings }; }
    if (['scenes.run', 'scenes.retry', 'scenes.retryScripts'].includes(type)) return response;
    return { settings };
  }, on(type, handler) { subscribers.set(type, handler); } };
  const hosts = new Map(['.workspace-header', '.workspace', '.workspace-content', '.dock-panel', '.mini-bar'].map(selector => [selector, { insertAdjacentHTML(position, html) { inserts.push({ selector, position, html }); } }]));
  const focusables = [];
  const document = { querySelector: selector => hosts.get(selector) || null, querySelectorAll: selector => selector.includes('[data-scene-focus]') ? focusables : [], addEventListener() {}, activeElement: null };
  const window = { Hub: H, crypto: { randomUUID }, DesktopHubNative: { bridge }, DesktopHubDesktop: { getState: () => ({ surface, hostState: { settings } }), applyHostState(result) { Object.assign(settings, result.settings); } } };
  vm.runInContext(source, vm.createContext({ window, document, console, Date }));
  return { H, window, settings, sent, inserts, document, focusables, emit: (type, payload) => subscribers.get(type)(payload), setResponse: next => { response = next; }, setHandler: next => { handler = next; } };
}
const click = id => ({ dataset: { id } });
function create(env, count = 2) { env.H.actions['new-scene'](); for (let i = 0; i < count; i++) env.H.actions['native-add-type']({ dataset: { type: 'application' } }); return env.H.ui.sceneDraft; }
test('native editor selects AppEntry ids, creates explicit sequence edges, and reordering leaves dependencies unchanged', async () => {
  const env = setup(); const draft = create(env, 5);
  assert.equal(draft.actions.length, 5); assert.ok(draft.actions.every(action => action.appEntryId === env.settings.appEntries[0].id && !action.target));
  env.H.actions['native-scene-mode']({ value: 'advanced' }); env.H.actions['native-sequence'](); const edges = JSON.stringify(draft.dependencies);
  assert.equal(draft.dependencies.length, 4);
  env.H.actions['native-action-move']({ dataset: { id: draft.actions[0].id, direction: '1' } }); assert.equal(JSON.stringify(draft.dependencies), edges);
  await env.H.actions['native-scene-save'](); assert.equal(env.sent[0].type, 'scenes.save'); assert.equal(env.sent[0].payload.scene.actions.length, 5); assert.equal(env.H.ui.sceneDraftDirty, false);
});
test('each of five action types receives real configuration with default ten-second waits and script repeat rule', () => {
  const env = setup(); create(env, 0);
  env.H.actions['native-scene-mode']({ value: 'advanced' });
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

test('simple defaults derive enabled sequential edges after move, disable, delete and simultaneous choice', () => {
  const env = setup(), draft = create(env, 3);
  assert.equal(draft.editorMode, 'simple'); assert.equal(draft.executionMode, 'simultaneous');
  env.H.actions['native-execution-mode']({ value: 'sequential' }); const [a, b, c] = draft.actions;
  env.H.actions['native-select-action'](click(b.id)); env.H.actions['native-action-field']({ dataset: { field: 'enabled' }, type: 'checkbox', checked: false });
  assert.equal(draft.dependencies.length, 1); assert.equal(draft.dependencies[0].actionId, c.id); assert.equal(draft.dependencies[0].dependsOnActionId, a.id);
  env.H.actions['native-action-move']({ dataset: { id: c.id, direction: '-1' } }); env.H.actions['native-action-move']({ dataset: { id: c.id, direction: '-1' } });
  assert.equal(draft.dependencies[0].actionId, a.id); assert.equal(draft.dependencies[0].dependsOnActionId, c.id);
  env.H.actions['native-execution-mode']({ value: 'simultaneous' }); assert.equal(draft.dependencies.length, 0);
  assert.doesNotMatch(env.H.pages.editor(), /data-field="completionCondition"|data-change="native-dependency-toggle"/);
});

test('advanced to simple conversion confirms precise graph and scheduling changes; cancel preserves draft and scripts block', () => {
  const env = setup(), draft = create(env); env.H.actions['native-scene-mode']({ value: 'advanced' });
  const action = draft.actions[0]; Object.assign(action, { delayMs: 50, completionCondition: 'window', waitTargetPath: 'C:\\Wait.exe', windowTitle: 'ready', arguments: '--keep', workingDirectory: 'C:\\Work', repeatPolicy: 'always' });
  draft.dependencies = [{ actionId: action.id, dependsOnActionId: draft.actions[1].id, condition: 'window', timeoutMs: 200 }];
  const before = JSON.stringify(draft); env.H.actions['native-scene-mode']({ value: 'simple' });
  assert.equal(JSON.stringify(draft), before); assert.match(env.H.modal.body, /依赖|等待|延迟|提交/); env.H.closeModal(); assert.equal(JSON.stringify(draft), before);
  env.H.actions['native-scene-mode']({ value: 'simple' }); env.H.actions['native-simple-confirm']();
  assert.equal(draft.editorMode, 'simple'); assert.equal(action.delayMs, 0); assert.equal(action.completionCondition, 'submitted'); assert.equal(action.waitTargetPath, ''); assert.equal(action.windowTitle, '');
  assert.equal(action.arguments, '--keep'); assert.equal(action.workingDirectory, 'C:\\Work'); assert.equal(action.repeatPolicy, 'always'); assert.equal(draft.dependencies.length, 0);
  env.H.actions['native-scene-mode']({ value: 'advanced' }); env.H.actions['native-add-type']({ dataset: { type: 'script' } }); const scripts = JSON.stringify(draft);
  env.H.actions['native-scene-mode']({ value: 'simple' }); assert.equal(JSON.stringify(draft), scripts); assert.equal(draft.editorMode, 'advanced'); assert.match(env.H.lastToast, /脚本/);
});

test('simple to advanced preserves graph and all action parameters and simple refuses script creation', () => {
  const env = setup(), draft = create(env); env.H.actions['native-execution-mode']({ value: 'sequential' }); const edges = JSON.stringify(draft.dependencies), actions = JSON.stringify(draft.actions);
  env.H.actions['native-add-type']({ dataset: { type: 'script' } }); assert.equal(JSON.stringify(draft.actions), actions);
  env.H.actions['native-scene-mode']({ value: 'advanced' }); assert.equal(JSON.stringify(draft.dependencies), edges); assert.equal(JSON.stringify(draft.actions), actions);
});

test('bulk application selection commits exact unique registered IDs without copied launch paths', () => {
  const env = setup(), draft = create(env, 0); env.H.actions['native-add-apps']();
  assert.match(env.H.modal.body, /应用 A|应用 B/); const ids = env.settings.appEntries.map(app => app.id);
  for (const id of ids) env.H.actions['native-app-choice']({ dataset: { id }, checked: true });
  env.H.actions['native-app-choice']({ dataset: { id: ids[0] }, checked: true }); env.H.actions['native-apps-confirm']();
  assert.deepEqual(Array.from(draft.actions, action => action.appEntryId), ids); assert.ok(draft.actions.every(action => action.target === '' && action.arguments === '' && action.workingDirectory === ''));
  assert.equal(new Set(draft.actions.map(action => action.id)).size, 2); assert.equal(env.H.ui.modal, null);
});

const failedRun = (id = randomUUID()) => ({ id, sceneId: randomUUID(), sceneName: '原执行', status: 'Failed', summary: '执行失败', canRetry: true, finishedAt: new Date().toISOString(), actions: [{ actionId: randomUUID(), actionType: 'application', name: '未启动', status: 'Failed', code: 'target_missing', message: '文件已移动', diagnostic: '<raw>', processId: 20 }] });

test('Workspace result entrance and bounded panel attach to shell, survive navigation and do not render or overwrite modals on events', () => {
  const env = setup(), run = failedRun(); env.H.showModal('正在编辑', '<input value="保留">', ''); const modal = env.H.ui.modal; const renders = env.H.renderCount || 0;
  env.emit('scene.executions', { executions: [run] }); assert.equal(env.H.ui.modal, modal); assert.equal(env.H.renderCount || 0, renders);
  assert.ok(env.inserts.some(x => x.selector === '.workspace-header' && x.position === 'afterend' && /scene-result-entrance/.test(x.html)));
  assert.ok(!env.inserts.some(x => x.selector === '.workspace-content')); env.H.closeModal(); env.H.actions['scene-results']();
  assert.ok(env.inserts.some(x => x.selector === '.workspace' && /scene-results-panel/.test(x.html)));
  env.H.navigate('favorites'); env.H.render(); assert.equal(env.window.DesktopHubScenes.getState().resultOpen, true); assert.equal(env.window.DesktopHubScenes.getState().selectedExecution, run.id);
});

test('result diagnostics remain inside closed per-action details and dependency recovery points to frozen prerequisite', () => {
  const env = setup(), run = failedRun(); const parent = run.actions[0]; run.actions.push({ actionId: randomUUID(), name: '被阻塞', status: 'Skipped', code: 'dependency_failed', message: '前置失败', blockedByActionId: parent.actionId });
  const markup = env.window.DesktopHubScenes.executionMarkup(run); assert.match(markup, /<details[^>]*data-scene-result-detail[^>]*>[\s\S]*target_missing/); assert.match(markup, /&lt;raw&gt;/);
  assert.match(markup, /scene-inspect-prerequisite/); assert.doesNotMatch(markup, /<details[^>]*data-scene-result-detail[^>]* open/);
});

test('ordinary retry uses execution ID only and older response cannot select over a newer execution', async () => {
  const env = setup(), old = failedRun(), newer = failedRun(); env.emit('scene.executions', { executions: [old, newer] }); env.H.actions['scene-execution-select']({ value: old.id });
  let finish; env.setHandler(type => type === 'scenes.retry' ? new Promise(resolve => { finish = resolve; }) : undefined);
  const pending = env.H.actions['scene-retry'](); env.H.actions['scene-execution-select']({ value: newer.id }); finish({ execution: failedRun(), executions: [old] }); await pending;
  assert.deepEqual(env.sent[0], { type: 'scenes.retry', payload: { executionId: old.id } }); assert.equal(env.window.DesktopHubScenes.getState().selectedExecution, newer.id);
});

test('script retry requires Workspace confirmation for exact submitted unsuccessful session-only IDs and respects every existing modal', async () => {
  const env = setup(), run = failedRun(); Object.assign(run.actions[0], { actionType: 'script', launchSubmitted: true, requiresScriptRepeat: true }); env.emit('scene.executions', { executions: [run] });
  env.H.showModal('导入中', '', ''); const modal = env.H.ui.modal; env.H.actions['scene-repeat-scripts'](); assert.equal(env.H.ui.modal, modal); assert.equal(env.sent.length, 0); env.H.closeModal();
  env.H.actions['scene-repeat-scripts'](); assert.match(env.H.modal.body, /仍在运行|终端/); assert.equal(env.sent.length, 0);
  env.setResponse({ execution: failedRun() }); await env.H.actions['scene-repeat-confirm'](); assert.deepEqual(env.sent[0], { type: 'scenes.retryScripts', payload: { executionId: run.id, actionIds: [run.actions[0].actionId] } });
  const dock = setup('dock'); dock.emit('scene.executions', { executions: [run] }); dock.H.actions['scene-repeat-scripts'](); assert.equal(dock.sent.length, 0); assert.equal(dock.H.lastSurface, 'workspace');
});

test('stale repeat confirmations and delayed results preserve newer modal and selected run', async () => {
  const env = setup(), run = failedRun(); Object.assign(run.actions[0], { actionType: 'script', launchSubmitted: true, requiresScriptRepeat: true }); env.emit('scene.executions', { executions: [run] }); env.H.actions['scene-repeat-scripts']();
  env.H.showModal('更新的编辑器', '', ''); await env.H.actions['scene-repeat-confirm'](); assert.equal(env.sent.length, 0);
  env.H.closeModal(); env.H.actions['scene-repeat-scripts'](); let finish; env.setHandler(type => type === 'scenes.retryScripts' ? new Promise(resolve => { finish = resolve; }) : undefined);
  const pending = env.H.actions['scene-repeat-confirm'](); env.H.showModal('另一个导入', '', ''); const modal = env.H.ui.modal; finish({ execution: failedRun() }); await pending; assert.equal(env.H.ui.modal, modal); assert.equal(env.H.modal.title, '另一个导入');
});

test('repair uses frozen registered-app identity and dirty editor guard rather than edited live scene mapping', () => {
  const env = setup(), run = failedRun(); run.actions[0].appEntryId = env.settings.appEntries[0].id;
  env.settings.scenes = [{ id: run.sceneId, name: '后来修改', actions: [{ id: run.actions[0].actionId, appEntryId: env.settings.appEntries[1].id }], dependencies: [] }]; env.emit('scene.executions', { executions: [run] });
  env.H.actions['scene-repair-app']({ dataset: { id: run.actions[0].actionId } }); assert.equal(env.H.repairedAppId, env.settings.appEntries[0].id); env.H.closeModal(); env.H.repairedAppId = null;
  const draft = create(env); env.H.actions['scene-repair-app']({ dataset: { id: run.actions[0].actionId } }); assert.equal(env.H.repairedAppId, null); assert.match(env.H.modal.body, /未保存|草稿/); env.H.closeModal(); assert.equal(env.H.ui.sceneDraft, draft);
  env.H.showModal('其他编辑', '', ''); const modal = env.H.ui.modal; env.H.actions['scene-repair-app']({ dataset: { id: run.actions[0].actionId } }); assert.equal(env.H.ui.modal, modal);
});

test('first run opens results even when its host execution event arrives before the request response', async () => {
  const env = setup(), run = failedRun(); let finish;
  env.setHandler(type => type === 'scenes.run' ? new Promise(resolve => { finish = resolve; }) : undefined);
  const pending = env.H.actions['run-scene'](click(run.sceneId)); env.emit('scene.executions', { executions: [run] }); finish({ execution: run }); await pending;
  assert.equal(env.window.DesktopHubScenes.getState().resultOpen, true); assert.equal(env.window.DesktopHubScenes.getState().selectedExecution, run.id);
});

test('a delayed scene target picker cannot mutate a replacement draft or overwrite a newer modal', async () => {
  const env = setup(), original = create(env, 1); let finish;
  env.setHandler(type => type === 'scenes.pickTarget' ? new Promise(resolve => { finish = resolve; }) : undefined);
  const pending = env.H.actions['native-scene-pick']({ dataset: { kind: 'application' } });
  const replacement = create(env, 1); env.H.showModal('较新的导入', '', ''); const modal = env.H.ui.modal; finish({ target: 'C:\\late.exe' }); await pending;
  assert.equal(original.actions[0].target, ''); assert.equal(replacement.actions[0].target, ''); assert.equal(env.H.ui.modal, modal);
});

test('stale mode and bulk selection confirmations leave a newer modal and original draft unchanged', () => {
  const env = setup(), draft = create(env); env.H.actions['native-scene-mode']({ value: 'advanced' }); env.H.actions['native-scene-mode']({ value: 'simple' });
  env.H.showModal('较新的编辑', '', ''); const modal = env.H.ui.modal; env.H.actions['native-simple-confirm'](); assert.equal(draft.editorMode, 'advanced'); assert.equal(env.H.ui.modal, modal);
  env.H.closeModal(); env.H.actions['native-add-apps'](); env.H.actions['native-app-choice']({ dataset: { id: env.settings.appEntries[1].id }, checked: true }); env.H.showModal('另一个弹窗', '', ''); env.H.actions['native-apps-confirm'](); assert.equal(draft.actions.length, 2); assert.equal(env.H.modal.title, '另一个弹窗');
});

test('disabling a simple step immediately refreshes visible skipped rows and rebuilt order', () => {
  const env = setup(), draft = create(env); env.H.actions['native-execution-mode']({ value: 'sequential' }); const before = env.H.renderCount;
  env.H.actions['native-action-field']({ dataset: { field: 'enabled' }, type: 'checkbox', checked: false });
  assert.ok(env.H.renderCount > before); assert.equal(draft.dependencies.length, 0); assert.match(env.H.pages.editor(), /已停用/);
});

test('script confirmation limits one exact attempt to 100 listed scripts and explains remaining failures', async () => {
  const env = setup(), run = failedRun(); run.actions = Array.from({ length: 101 }, (_, index) => ({ actionId: randomUUID(), actionType: 'script', name: `脚本 ${index + 1}`, status: 'Failed', launchSubmitted: true, requiresScriptRepeat: true }));
  env.emit('scene.executions', { executions: [run] }); env.H.actions['scene-repeat-scripts'](); assert.match(env.H.modal.body, /100/); assert.match(env.H.modal.body, /其余|剩余/); assert.doesNotMatch(env.H.modal.body, /<li>脚本 101<\/li>/);
  env.setResponse({ execution: failedRun() }); await env.H.actions['scene-repeat-confirm'](); assert.deepEqual(env.sent[0].payload.actionIds, run.actions.slice(0, 100).map(action => action.actionId));
});

test('operational failure copy is human readable while original technical messages stay under details', () => {
  const env = setup(), run = failedRun(); run.actions[0].message = 'System.ComponentModel.Win32Exception raw'; const markup = env.window.DesktopHubScenes.executionMarkup(run);
  assert.match(markup, /目标.*(?:不存在|找不到|移动)|找不到.*目标/); assert.doesNotMatch(markup.split('<details')[0], /Win32Exception/); assert.match(markup.split('<details')[1], /Win32Exception/);
});

test('execution updates restore the focused result action but never move focus out of a modal', () => {
  const env = setup(), run = failedRun(); env.emit('scene.executions', { executions: [run] }); env.H.actions['scene-results']();
  let focused = 0; env.document.activeElement = { dataset: { sceneFocus: 'scene-retry' }, closest: () => ({}) };
  env.focusables.push({ dataset: { sceneFocus: 'scene-retry' }, focus: () => { focused++; } }); env.emit('scene.executions', { executions: [run] }); assert.equal(focused, 1);
  env.H.showModal('导入', '', ''); env.document.activeElement = { dataset: {}, closest: () => null }; env.emit('scene.executions', { executions: [run] }); assert.equal(focused, 1);
});

for (const scenario of ['saved', 'never-saved', 'deleted']) test(`confirmed application repair discards the ${scenario} scene draft before leaving the editor`, async () => {
  const env = setup(), draft = create(env), saved = structuredClone(draft); saved.name = '已保存名称';
  if (scenario !== 'never-saved') env.settings.scenes = [saved];
  const run = failedRun(); run.actions[0].appEntryId = env.settings.appEntries[0].id; env.emit('scene.executions', { executions: [run] });
  env.H.actions['native-scene-field']({ dataset: { field: 'name' }, value: 'SHOULD BE DISCARDED' });
  env.H.actions['scene-repair-app'](click(run.actions[0].actionId));
  if (scenario === 'deleted') env.settings.scenes = [];
  env.H.actions['scene-repair-discard'](); assert.equal(env.H.modal.title, '编辑应用'); env.H.closeModal();
  assert.equal(env.H.ui.sceneDraft, null); assert.equal(env.H.ui.sceneDraftDirty, false); assert.notEqual(env.H.ui.page, 'editor');
  await env.H.actions['native-scene-save'](); assert.equal(env.sent.length, 0);
  assert.doesNotMatch(env.H.pages.editor(), /SHOULD BE DISCARDED/);
  if (scenario === 'saved') { env.H.actions['edit-scene'](click(saved.id)); assert.equal(env.H.ui.sceneDraft.name, '已保存名称'); }
  else assert.equal(env.settings.scenes.length, 0);
});

test('rejected script repeat retains exact owned confirmation for another explicit click and does not retry automatically', async () => {
  const env = setup(), run = failedRun(); Object.assign(run.actions[0], { actionType: 'script', launchSubmitted: true, requiresScriptRepeat: true });
  env.emit('scene.executions', { executions: [run] }); env.H.actions['scene-repeat-scripts'](); const modal = env.H.ui.modal;
  env.setHandler(type => { if (type === 'scenes.retryScripts') throw Object.assign(new Error('请等待当前操作'), { code: 'BUSY' }); });
  await env.H.actions['scene-repeat-confirm'](); assert.equal(env.sent.length, 1); assert.equal(env.H.ui.modal, modal); assert.equal(env.window.DesktopHubScenes.getState().retryBusy, false);
  await Promise.resolve(); assert.equal(env.sent.length, 1); // No automatic second attempt.
  env.setHandler(null); env.setResponse({ execution: failedRun() }); await env.H.actions['scene-repeat-confirm']();
  assert.equal(env.sent.length, 2); assert.deepEqual(env.sent.map(request => request.payload), [0, 1].map(() => ({ executionId: run.id, actionIds: [run.actions[0].actionId] })));
  assert.equal(env.H.ui.modal, null);
});

test('a rejected script request cannot revive its stale confirmation over a newer modal', async () => {
  const env = setup(), run = failedRun(); Object.assign(run.actions[0], { actionType: 'script', launchSubmitted: true, requiresScriptRepeat: true });
  env.emit('scene.executions', { executions: [run] }); env.H.actions['scene-repeat-scripts'](); let reject;
  env.setHandler(type => type === 'scenes.retryScripts' ? new Promise((_, fail) => { reject = fail; }) : undefined);
  const pending = env.H.actions['scene-repeat-confirm'](); env.H.showModal('新的导入', '<input value="保留">', ''); const modal = env.H.ui.modal;
  reject(new Error('BUSY')); await pending; await env.H.actions['scene-repeat-confirm']();
  assert.equal(env.sent.length, 1); assert.equal(env.H.ui.modal, modal); assert.equal(env.H.modal.title, '新的导入'); assert.equal(env.window.DesktopHubScenes.getState().retryBusy, false);
});
