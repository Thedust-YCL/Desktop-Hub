import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import test from 'node:test';

const source = fs.readFileSync(new URL('../onboarding.js', import.meta.url), 'utf8');
function setup({ surface = 'workspace', completed = false, required = true, confirmFails = false, responses = {} } = {}) {
  const sent = [], nodes = new Map(), listeners = new Map();
  let navigated = '', renders = 0, previews = 0;
  let state = { onboardingRequired: required, settings: { onboardingCompleted: completed, workspaceHotkey: 'Ctrl+Shift+W', dockHotkey: 'Ctrl+Shift+D', appEntries: [{ id: 'existing', name: '已有应用' }] },
    systemIntegration: { desktopIcons: { supported: true, visible: true, firstHideAuthorized: false, success: true }, startup: { enabled: false, success: true } } };
  const resources = { scan: null, preview: null, result: { sources: [{ id: 'existing-source' }] } };
  const H = { ui: { modal: null, sceneDraftDirty: false }, actions: {},
    escape: value => String(value).replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char])),
    icon: () => '<svg></svg>', toast() {}, render() { renders++; },
    navigate(page) { navigated = page; H.closeModal(); },
    showModal(title, body, footer, options) { H.ui.modal = { title, body, footer, ...options }; }, closeModal() { H.ui.modal = null; } };
  H.actions['nr-cancel'] = async () => { resources.scan = null; resources.preview = null; H.closeModal(); };
  H.actions['nr-confirm'] = async () => { if (confirmFails) return; resources.scan = null; resources.preview = null; H.closeModal(); };
  H.actions['app-add'] = async () => { state.settings.appEntries.push({ id: 'new', name: '新应用' }); };
  const desktop = { getState: () => ({ surface, hostState: state, launch: { status: 'idle', message: '已添加新应用。' } }), applyHostState(next) { state = { ...state, ...structuredClone(next) }; H.render(); } };
  const bridge = { async request(type, payload = {}) {
    sent.push({ type, payload: structuredClone(payload) });
    if (responses[type]) return responses[type](payload, state);
    if (type === 'settings.save') { Object.assign(state.settings, payload.settings); return structuredClone(state); }
    if (type === 'system.getState') return structuredClone(state);
    if (type === 'system.setDesktopIcons') Object.assign(state.systemIntegration.desktopIcons, { visible: payload.visible, firstHideAuthorized: true, exitPolicy: payload.firstHidePolicy || state.systemIntegration.desktopIcons.exitPolicy });
    if (type === 'system.setStartup') state.systemIntegration.startup.enabled = payload.enabled;
    return { state: structuredClone(state), operation: { success: true } };
  } };
  const window = { Hub: H, DesktopHubDesktop: desktop, DesktopHubNative: { bridge }, DesktopHubResources: { getState: () => resources, async previewSource(kind) {
    previews++; resources.scan = 'owned-preview'; resources.preview = { token: 'owned-preview', kind }; H.showModal('确认索引来源', '', '');
  } } };
  H.actions['nr-rescan'] = async el => { await H.actions['nr-cancel'](); await window.DesktopHubResources.previewSource(el.dataset.kind); };
  vm.runInNewContext(source, { window, document: { querySelector: selector => nodes.get(selector), addEventListener(name, handler) { listeners.set(name, handler); } } });
  return { H, window, sent, resources, nodes, state: () => state, guide: () => window.DesktopHubOnboarding?.getState(), navigate: () => navigated,
    start() { H.render(); }, next() { H.actions['guide-next'](); }, toStep(index) { while (window.DesktopHubOnboarding.getState().step < index) H.actions['guide-next'](); }, previews: () => previews, renders: () => renders };
}

test('first manual Workspace initialization opens one locked guide without changing files or system preferences', () => {
  const env = setup(); env.start();
  assert.equal(env.guide().active, true);
  assert.equal(env.H.ui.modal.locked, true);
  assert.match(env.H.ui.modal.body, /aria-current="step"/);
  env.next(); env.H.render();
  assert.equal(env.guide().step, 1);
  assert.equal(env.sent.length, 0);
  env.H.navigate('settings');
  assert.equal(env.H.ui.modal.title, '先导入一个文件来源');
  assert.equal(env.navigate(), '');
});

test('completed users and diagnostic hosts stay quiet; compact surfaces never install the guide', () => {
  for (const options of [{ completed: true }, { required: false }]) {
    const env = setup(options); env.start(); assert.equal(env.guide().active, false); assert.equal(env.H.ui.modal, null);
  }
  for (const surface of ['mini', 'dock']) {
    const env = setup({ surface }); assert.equal(env.window.DesktopHubOnboarding, undefined); assert.equal(env.H.actions['guide-next'], undefined);
  }
});

test('automatic guide waits for existing dialogs and unsaved edits', () => {
  const env = setup(); env.H.ui.modal = { title: '已有对话框' }; env.start(); assert.equal(env.guide().active, false);
  env.H.closeModal(); env.H.ui.sceneDraftDirty = true; env.start(); assert.equal(env.guide().active, false);
  env.H.ui.sceneDraftDirty = false; env.start(); assert.equal(env.guide().active, true);
});

test('skipping saves only completion; no import, desktop icon or startup writes occur', async () => {
  const env = setup(); env.start(); await env.H.actions['guide-skip']();
  assert.equal(env.state().settings.onboardingCompleted, true); assert.equal(env.guide().active, false);
  assert.equal(env.H.ui.modal, null); assert.equal(env.navigate(), 'home');
  assert.deepEqual(env.sent, [{ type: 'settings.save', payload: { settings: { onboardingCompleted: true } } }]);
  assert.equal(env.state().settings.appEntries.length, 1);
  env.H.render(); assert.equal(env.H.ui.modal, null);
});

test('save failures keep guide open and allow retry; malformed completion is never accepted', async () => {
  for (const response of [async () => { throw new Error('磁盘无法写入'); }, async (_, state) => structuredClone(state), async () => ({})]) {
    const env = setup({ responses: { 'settings.save': response } }); env.start(); await env.H.actions['guide-skip']();
    assert.equal(env.guide().active, true); assert.equal(env.guide().feedbackError, true);
    assert.equal(env.guide().busy, false); assert.equal(env.state().settings.onboardingCompleted, false);
    assert.equal(env.H.ui.modal.locked, true);
  }
});

test('replay retains completion, apps and sources; back/next and finishing never reset data', async () => {
  const env = setup({ completed: true }); env.start(); env.H.actions['settings-replay-onboarding']();
  assert.equal(env.guide().step, 0); assert.equal(env.state().settings.onboardingCompleted, true);
  env.toStep(5); env.H.actions['guide-back'](); assert.equal(env.guide().step, 4); env.next();
  await env.H.actions['guide-finish']();
  assert.equal(env.state().settings.appEntries[0].id, 'existing'); assert.equal(env.resources.result.sources[0].id, 'existing-source');
  assert.equal(env.sent.length, 1);
  env.H.openOnboarding(); assert.equal(env.guide().step, 0);
});

test('import is opt-in and uses native preview; cancel and confirm return to the same step', async () => {
  const env = setup(); env.start(); env.next(); assert.equal(env.previews(), 0);
  await env.H.actions['guide-import']({ dataset: { kind: 'custom' } });
  assert.equal(env.guide().suspended, true); assert.equal(env.H.ui.modal.title, '确认索引来源');
  env.next(); assert.equal(env.guide().step, 1);
  await env.H.actions['nr-cancel'](); assert.equal(env.guide().suspended, false); assert.equal(env.guide().step, 1);
  await env.H.actions['guide-import']({ dataset: { kind: 'desktop' } });
  await env.H.actions['nr-confirm'](); assert.equal(env.guide().suspended, false); assert.equal(env.H.ui.modal.title, '先导入一个文件来源');
  assert.equal(env.previews(), 2); assert.equal(env.sent.length, 0);
});

test('failed import confirmation preserves the preview for correction', async () => {
  const env = setup({ confirmFails: true }); env.start(); env.next(); await env.H.actions['guide-import']({ dataset: { kind: 'custom' } });
  await env.H.actions['nr-confirm'](); assert.equal(env.guide().suspended, true); assert.equal(env.H.ui.modal.title, '确认索引来源');
});

test('changing recursive preview keeps guide suspended until final cancellation or confirmation', async () => {
  const env = setup(); env.start(); env.next(); await env.H.actions['guide-import']({ dataset: { kind: 'desktop' } });
  await env.H.actions['nr-rescan']({ dataset: { kind: 'desktop' } });
  assert.equal(env.guide().suspended, true); assert.equal(env.H.ui.modal.title, '确认索引来源');
  await env.H.actions['nr-confirm'](); assert.equal(env.guide().suspended, false); assert.equal(env.guide().step, 1);
});

test('usage teaches saved shortcuts and supports real application selection', async () => {
  const env = setup(); env.start(); env.toStep(2);
  assert.match(env.H.ui.modal.body, /Ctrl\+Shift\+W/); assert.match(env.H.ui.modal.body, /Ctrl\+Shift\+D/);
  await env.H.actions['guide-add-app'](); assert.equal(env.state().settings.appEntries.length, 2);
  assert.equal(env.guide().step, 2); assert.match(env.H.ui.modal.body, /已添加新应用/);
});

test('first hide requires explicit policy; subsequent hide and show use actual current state', async () => {
  const env = setup(); env.start(); env.toStep(3);
  assert.doesNotMatch(env.H.ui.modal.body, / checked/);
  assert.match(env.H.ui.modal.body, /data-visible="false" disabled/);
  await env.H.actions['guide-icons']({ dataset: { visible: 'false' } }); assert.equal(env.sent.length, 0);
  env.H.actions['guide-policy']({ value: 'restore' });
  await env.H.actions['guide-icons']({ dataset: { visible: 'false' } });
  assert.deepEqual(env.sent[0], { type: 'system.setDesktopIcons', payload: { visible: false, firstHidePolicy: 'restore' } });
  assert.match(env.H.ui.modal.body, /桌面图标当前隐藏/);
  await env.H.actions['guide-icons']({ dataset: { visible: 'true' } });
  assert.deepEqual(env.sent[1].payload, { visible: true }); assert.match(env.H.ui.modal.body, /桌面空白处右键/);
});

test('startup remains opt-in and both enabling and disabling send dedicated native operations', async () => {
  const env = setup(); env.start(); env.toStep(4); assert.equal(env.sent.length, 0);
  await env.H.actions['guide-startup']({ dataset: { enabled: 'true' } }); assert.equal(env.state().systemIntegration.startup.enabled, true);
  await env.H.actions['guide-startup']({ dataset: { enabled: 'false' } }); assert.equal(env.state().systemIntegration.startup.enabled, false);
  assert.deepEqual(env.sent.map(value => value.type), ['system.setStartup', 'system.setStartup']);
});

test('unknown Windows states disable writes; failed operations show native reason and retain actual state', async () => {
  const env = setup({ responses: { 'system.setStartup': async (_, state) => ({ state: structuredClone(state), operation: { success: false, message: 'Windows 拒绝写入' } }) } });
  env.start(); env.toStep(3); env.state().systemIntegration.desktopIcons.supported = false;
  await env.H.actions['guide-icons']({ dataset: { visible: 'true' } }); assert.equal(env.sent.length, 0);
  env.next(); await env.H.actions['guide-startup']({ dataset: { enabled: 'true' } });
  assert.equal(env.guide().feedbackError, true); assert.match(env.H.ui.modal.body, /Windows 拒绝写入/);
  assert.equal(env.state().systemIntegration.startup.enabled, false);
  env.state().systemIntegration.startup.code = 'registry_unavailable';
  await env.H.actions['guide-startup']({ dataset: { enabled: 'true' } }); assert.equal(env.sent.length, 1);
});
