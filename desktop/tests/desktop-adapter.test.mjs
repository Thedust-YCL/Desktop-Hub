import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const source = fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'desktop.js'), 'utf8');
const baseSettings = {
  theme: 'system', reducedMotion: false, transparency: true,
  miniPosition: 'left', miniSize: 'normal', miniMode: 'icons',
  dockPosition: 'left', dockSize: 'normal', dockIconSize: 'normal', hoverEnabled: true,
  hoverDelay: 500, allowForeground: true, schemaVersion: 2, appEntries: [], launcherPins: [],
  workspaceBounds: { left: 40, top: 40, width: 1200, height: 800 }
};
function setup(surface = 'workspace', responses = {}) {
  const sent = [], eventListeners = new Map(), domListeners = new Map(), windowListeners = new Map(), timers = new Map(), toasts = [], nodes = new Map(), storage = new Map();
  let timerId = 0;
  const settings = structuredClone(baseSettings);
  const H = {
    state: { onboardingComplete: false, scenes: [], settings: { ...settings, autoStart: false } },
    ui: { surface: 'workspace', page: 'home', modal: null }, actions: {}, pages: { home: () => '<div class="home-intro"></div><div class="launchpad-grid"></div><div class="home-scenes"></div>' },
    save() { storage.set('desktop-hub-m0-v07', JSON.stringify(H.state)); }, applyTheme() {}, render() {},
    navigate(page) { H.ui.page = page; H.ui.surface = 'workspace'; H.render(); },
    toast(message, type) { toasts.push({ message, type }); },
    closeModal() { H.ui.modal = null; },
    showModal(title, body, footer) { H.ui.modal = { title, body, footer, locked: false }; },
    escape(value) { return String(value ?? '').replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char])); },
    icon() { return '<svg></svg>'; }
  };
  const state = { version: '0.1.5', surface, settings, appIcons: {}, effectiveTheme: 'dark', settingsWarning: null, runtimeVersion: 'test' };
  const bridge = {
    available: true,
    async request(type, payload = {}) {
      sent.push({ type, payload: structuredClone(payload) });
      if (responses[type]) return responses[type](payload, state);
      if (type === 'app.getState') return structuredClone(state);
      if (type === 'settings.save') {
        Object.assign(state.settings, structuredClone(payload.settings));
        return { settings: structuredClone(state.settings), effectiveTheme: state.effectiveTheme };
      }
      return {};
    },
    on(type, handler) { eventListeners.set(type, handler); }
  };
  class NativeBridgeError extends Error { constructor(code, message) { super(message); this.code = code; } }
  const document = {
    readyState: 'complete', documentElement: { dataset: {} },
    querySelector(selector) { return nodes.get(selector) || null; }, querySelectorAll() { return []; },
    addEventListener(type, handler) { if (!domListeners.has(type)) domListeners.set(type, []); domListeners.get(type).push(handler); },
    body: { insertAdjacentHTML() {} }
  };
  const window = { Hub: H, DesktopHubNative: { bridge, NativeBridgeError }, localStorage: { getItem(key) { return storage.get(key) ?? null; } }, addEventListener(type, handler) { windowListeners.set(type, handler); } };
  const context = vm.createContext({ window, document, location: { search: `?surface=${surface}` }, URLSearchParams, console,
    setTimeout(fn) { const id = ++timerId; timers.set(id, fn); return id; }, clearTimeout(id) { timers.delete(id); }
  });
  vm.runInContext(source, context, { filename: 'desktop.js' });
  return {
    H, sent, toasts, state, document, window, nodes, storage,
    async initialize() { for (const handler of domListeners.get('DOMContentLoaded') || []) await handler(); },
    emit(type, payload) { eventListeners.get(type)?.(payload); },
    emitStorage(key, value) { storage.set(key, value); windowListeners.get('storage')?.({ key, newValue: value }); },
    async flushTimers() { const tasks = [...timers.values()]; timers.clear(); for (const fn of tasks) await fn(); },
    key(event) { for (const handler of domListeners.get('keydown') || []) handler(event); }
  };
}

test('each HTML surface stays assigned to its native window', async () => {
  const env = setup('mini');
  await env.initialize();
  env.H.setSurface('dock');
  env.H.navigate('scenes');
  assert.equal(env.H.ui.surface, 'mini');
  assert.equal(env.document.documentElement.dataset.surface, 'mini');
  assert.ok(env.sent.some(item => item.type === 'window.openDock'));
  assert.ok(env.sent.some(item => item.type === 'window.openWorkspace' && item.payload.page === 'scenes'));
  assert.equal(env.H.state.onboardingComplete, true);
});

test('Windows effective theme and native saved settings override prototype cache', async () => {
  const env = setup();
  env.H.state.settings.theme = 'light';
  await env.initialize();
  assert.equal(env.H.state.settings.theme, 'system');
  assert.equal(env.document.documentElement.dataset.theme, 'dark');
  env.emit('state.changed', { settings: { ...baseSettings, theme: 'system' }, effectiveTheme: 'light' });
  assert.equal(env.document.documentElement.dataset.theme, 'light');
});

test('settings writes include only editable fields and never application records or bounds', async () => {
  const env = setup();
  await env.initialize();
  env.H.state.settings.theme = 'dark';
  env.H.state.settings.autoStart = true;
  env.H.save();
  await env.flushTimers();
  const saved = env.sent.find(item => item.type === 'settings.save').payload.settings;
  assert.equal(saved.theme, 'dark');
  assert.equal('autoStart' in saved, false);
  assert.equal('appEntries' in saved, false);
  assert.equal('launcherPins' in saved, false);
  assert.equal('workspaceBounds' in saved, false);
});

const appEntry = (id, displayOrder = 0) => ({ id, name: `应用 ${id}`, target: `C:\\Tools\\${id}.exe`, arguments: '', workingDirectory: '', icon: null, displayOrder, pinnedToMini: false, pinnedToDock: true });
const click = (id, more = {}) => ({ dataset: { id, ...more } });

test('five consecutive additions stay independent and each native launch carries only its id', async () => {
  let nextId = 0;
  const env = setup('workspace', {
    'apps.add': async (_, state) => {
      const app = appEntry(`native-${++nextId}`, nextId - 1);
      state.settings.appEntries.push(app);
      return { cancelled: false, app, settings: structuredClone(state.settings) };
    },
    'apps.launch': async ({ appId }) => ({ success: true, processId: 124, message: `已提交 ${appId} 的启动请求。` })
  });
  await env.initialize();
  for (let index = 0; index < 5; index++) await env.H.actions['app-add']();
  const saved = env.window.DesktopHubDesktop.getState().hostState.settings.appEntries;
  assert.equal(saved.length, 5);
  assert.deepEqual(saved.map(app => app.id), ['native-1', 'native-2', 'native-3', 'native-4', 'native-5']);
  for (const app of saved) await env.H.actions['app-launch'](click(app.id));
  assert.deepEqual(env.sent.filter(item => item.type === 'apps.launch').map(item => item.payload), saved.map(app => ({ appId: app.id })));
  assert.equal(env.sent.some(item => item.type.startsWith('launch.')), false);
  assert.equal(env.window.DesktopHubDesktop.getState().launch.status, 'success');
  assert.equal(env.window.DesktopHubDesktop.getState().launch.appId, 'native-5');
  assert.equal((env.H.pages.home().match(/data-action="app-launch"/g) || []).length, 5);
  assert.match(env.H.pages.home(), /class="launcher-grid"/);
});

test('real native failures and malformed success keep UI in error state', async () => {
  let result = { success: false, code: 'TARGET_NOT_FOUND', message: '程序文件已不存在。' };
  const env = setup('workspace', {
    'apps.launch': async () => result
  });
  env.state.settings.appEntries = [appEntry('selected')];
  await env.initialize();
  await env.H.actions['app-launch'](click('selected'));
  assert.equal(env.window.DesktopHubDesktop.getState().launch.status, 'error');
  assert.equal(env.window.DesktopHubDesktop.getState().launch.code, 'TARGET_NOT_FOUND');
  assert.equal(env.window.DesktopHubDesktop.getState().launch.message, '程序文件已不存在。');
  assert.match(env.H.pages.home(), /程序文件已不存在/);
  result = {};
  await env.H.actions['app-launch'](click('selected'));
  assert.equal(env.window.DesktopHubDesktop.getState().launch.code, 'LAUNCH_FAILED');
  await env.H.actions['app-launch'](click('unregistered-id'));
  assert.equal(env.sent.filter(item => item.type === 'apps.launch').length, 2);
});

test('Esc dismisses modal first then sends native close without prototype toast', async () => {
  const env = setup();
  await env.initialize();
  env.H.ui.modal = { locked: false };
  const event = { key: 'Escape', preventDefault() {}, stopImmediatePropagation() {} };
  env.key(event);
  assert.equal(env.H.ui.modal, null);
  assert.equal(env.sent.some(item => item.type === 'window.closeWorkspace'), false);
  env.key(event);
  assert.equal(env.sent.some(item => item.type === 'window.closeWorkspace'), true);
  assert.equal(env.toasts.some(item => /模拟.*返回/.test(item.message)), false);
});

test('original scene click does not claim a process launch', async () => {
  const env = setup('dock');
  await env.initialize();
  env.H.actions['run-scene']({ dataset: { id: 'creation' } });
  assert.equal(env.sent.some(item => item.type === 'apps.launch'), false);
  assert.ok(env.sent.some(item => item.type === 'window.openWorkspace' && item.payload.page === 'scenes'));
});

test('settings save failure rolls back native preferences with the actual error', async () => {
  const env = setup('workspace', { 'settings.save': async () => { const error = new Error('无法写入本地配置。'); error.code = 'SAVE_FAILED'; throw error; } });
  await env.initialize();
  env.H.state.settings.theme = 'dark';
  env.H.save();
  await env.flushTimers();
  assert.equal(env.H.state.settings.theme, 'system');
  assert.ok(env.toasts.some(item => item.message.includes('无法写入本地配置')));
});

test('a newer user edit survives a delayed save reply and is subsequently persisted', async () => {
  const writes = [];
  const env = setup('workspace', {
    'settings.save': payload => new Promise(resolve => writes.push({ settings: structuredClone(payload.settings), resolve }))
  });
  await env.initialize();
  env.H.state.settings.theme = 'dark';
  env.H.save();
  const firstBatch = env.flushTimers();
  assert.equal(writes.length, 1);
  assert.equal(writes[0].settings.theme, 'dark');

  // The user changes theme while write A is still pending. A separate native event
  // also delivers a new application; adopting it must not discard edit B.
  env.H.state.settings.theme = 'light';
  env.H.save();
  const nativeApp = appEntry('chosen-after-save');
  const savedA = { ...baseSettings, theme: 'dark', appEntries: [nativeApp] };
  env.emit('state.changed', { settings: savedA, appIcons: { [nativeApp.id]: 'data:image/png;base64,AAAA' }, effectiveTheme: 'dark' });
  assert.equal(env.H.state.settings.theme, 'light');
  assert.equal(env.window.DesktopHubDesktop.getState().hostState.settings.appEntries[0].id, nativeApp.id);

  writes[0].resolve({ settings: savedA, effectiveTheme: 'dark' });
  await firstBatch;
  assert.equal(env.H.state.settings.theme, 'light');
  const secondBatch = env.flushTimers();
  assert.equal(writes.length, 2);
  assert.equal(writes[1].settings.theme, 'light');
  writes[1].resolve({ settings: { ...savedA, theme: 'light' }, effectiveTheme: 'light' });
  await secondBatch;
  assert.equal(env.window.DesktopHubDesktop.getState().hostState.settings.theme, 'light');
  assert.equal(env.H.state.settings.theme, 'light');
  assert.equal(env.window.DesktopHubDesktop.getState().hostState.appIcons[nativeApp.id], 'data:image/png;base64,AAAA');
});

test('debounced appearance saves cannot overwrite explicitly saved native system preferences', async () => {
  const env = setup();
  Object.assign(env.state.settings, { miniEnabled: true, miniOpacity: .92, foregroundBlocklist: ['game.exe'], dockHotkey: 'Ctrl+Alt+D', workspaceHotkey: 'Ctrl+Alt+W' });
  await env.initialize();
  env.H.state.settings.theme = 'dark';
  Object.assign(env.H.state.settings, { miniEnabled: false, miniOpacity: .5, foregroundBlocklist: [], dockHotkey: 'unregistered' });
  env.H.save();
  await env.flushTimers();
  const patch = env.sent.find(item => item.type === 'settings.save').payload.settings;
  assert.equal(patch.theme, 'dark');
  for (const key of ['miniEnabled', 'miniOpacity', 'foregroundBlocklist', 'dockHotkey', 'workspaceHotkey']) assert.equal(key in patch, false);
  assert.equal(env.state.settings.miniEnabled, true);
  assert.equal(env.state.settings.miniOpacity, .92);
  assert.deepEqual(env.state.settings.foregroundBlocklist, ['game.exe']);
});

test('editing and removing one application preserve its neighbours and do not send any path execution', async () => {
  const entries = Array.from({ length: 5 }, (_, index) => appEntry(`app-${index}`, index));
  const env = setup('workspace', {
    'apps.update': async (payload, state) => {
      const { appId, ...patch } = payload;
      Object.assign(state.settings.appEntries.find(app => app.id === appId), patch);
      return { settings: structuredClone(state.settings) };
    },
    'apps.remove': async ({ appId }, state) => {
      state.settings.appEntries = state.settings.appEntries.filter(app => app.id !== appId);
      return { settings: structuredClone(state.settings) };
    }
  });
  env.state.settings.appEntries = structuredClone(entries);
  await env.initialize();
  const edited = entries[2];
  env.H.actions['app-edit'](click(edited.id));
  assert.match(env.H.ui.modal.body, /app-working-directory/);
  env.nodes.set('#app-name', { value: '编辑器的新名称' });
  env.nodes.set('#app-arguments', { value: '--new-window "C:\\My Files"' });
  env.nodes.set('#app-working-directory', { value: 'C:\\Tools' });
  env.nodes.set('#app-pin-mini', { checked: true });
  env.nodes.set('#app-pin-dock', { checked: false });
  await env.H.actions['app-save'](click(edited.id));
  const request = env.sent.find(item => item.type === 'apps.update');
  assert.deepEqual(request.payload, { appId: edited.id, name: '编辑器的新名称', arguments: '--new-window "C:\\My Files"', workingDirectory: 'C:\\Tools', pinnedToMini: true, pinnedToDock: false });
  const saved = env.window.DesktopHubDesktop.getState().hostState.settings.appEntries;
  assert.deepEqual(saved.filter(app => app.id !== edited.id), entries.filter(app => app.id !== edited.id));
  env.H.actions['app-remove'](click(edited.id));
  assert.match(env.H.ui.modal.body, /不会删除文件.*不会卸载/);
  assert.equal(env.sent.some(item => item.type === 'apps.remove'), false, 'removal waits for the record-only confirmation');
  await env.H.actions['app-remove-confirm'](click(edited.id));
  assert.deepEqual(env.window.DesktopHubDesktop.getState().hostState.settings.appEntries, entries.filter(app => app.id !== edited.id));
  assert.deepEqual(env.sent.find(item => item.type === 'apps.remove').payload, { appId: edited.id });
  assert.equal(env.sent.some(item => /delete|uninstall|shell|launch\./.test(item.type)), false);
});

test('cancelled picker adds nothing; replacing a target keeps the stable id and does not send a frontend path', async () => {
  const env = setup('workspace', {
    'apps.add': async () => ({ cancelled: true }),
    'apps.replaceTarget': async ({ appId }, state) => {
      const app = state.settings.appEntries.find(app => app.id === appId);
      app.target = 'C:\\Tools\\Replacement.lnk';
      return { cancelled: false, app: structuredClone(app), settings: structuredClone(state.settings) };
    }
  });
  env.state.settings.appEntries = [appEntry('keep-id'), appEntry('untouched', 1)];
  await env.initialize();
  await env.H.actions['app-add']();
  assert.equal(env.window.DesktopHubDesktop.getState().hostState.settings.appEntries.length, 2);
  env.nodes.set('#app-name', { value: '我的编辑草稿' });
  env.H.actions['app-edit'](click('keep-id'));
  await env.H.actions['app-replace-target'](click('keep-id'));
  assert.deepEqual(env.sent.find(item => item.type === 'apps.replaceTarget').payload, { appId: 'keep-id' });
  const saved = env.window.DesktopHubDesktop.getState().hostState.settings.appEntries;
  assert.equal(saved[0].id, 'keep-id');
  assert.equal(saved[1].target, 'C:\\Tools\\untouched.exe');
  assert.match(env.H.ui.modal.body, /Replacement.lnk/);
  assert.match(env.H.ui.modal.body, /我的编辑草稿/);
});

test('Workspace reorder stays independent from the Mini and Dock layouts and search keeps record identity', async () => {
  const env = setup('workspace', {
    'apps.reorder': async ({ appIds }, state) => {
      state.settings.appEntries.forEach(app => { app.displayOrder = appIds.indexOf(app.id); });
      return { settings: structuredClone(state.settings) };
    }
  });
  const entries = [appEntry('alpha'), appEntry('beta', 1), appEntry('gamma', 2)];
  const pinned = [{ surface: 'mini', itemType: 'app', itemId: 'alpha', enabled: true, displayOrder: 0 }, { surface: 'dock', itemType: 'app', itemId: 'beta', enabled: true, displayOrder: 0 }];
  env.state.settings.appEntries = entries;
  env.state.settings.launcherPins = structuredClone(pinned);
  await env.initialize();
  await env.H.actions['app-move'](click('gamma', { direction: '-1' }));
  assert.deepEqual(env.sent.find(item => item.type === 'apps.reorder').payload, { appIds: ['alpha', 'gamma', 'beta'] });
  assert.deepEqual(env.window.DesktopHubDesktop.getState().hostState.settings.launcherPins, pinned);
  env.H.actions['apps-search']({ value: 'beta' });
  const markup = env.H.pages.home();
  assert.match(markup, /data-action="app-launch" data-id="beta"/);
  assert.doesNotMatch(markup, /data-action="app-launch" data-id="alpha"/);
  await env.H.actions['app-move'](click('gamma', { direction: '-1' }));
  assert.equal(env.sent.filter(item => item.type === 'apps.reorder').length, 1);
});

test('Mini pin management mixes app/scene ids, caps four and preserves independent order', async () => {
  const env = setup('workspace', {
    'launcher.setPins': async ({ surface, pins }, state) => {
      state.settings.launcherPins = pins.map((pin, displayOrder) => ({ ...pin, id: `pin-${displayOrder}`, surface, displayOrder }));
      return { settings: structuredClone(state.settings) };
    }
  });
  env.state.settings.appEntries = Array.from({ length: 5 }, (_, index) => appEntry(`item-${index}`, index));
  env.H.state.scenes = [{ id: 'scene-demo', name: '创作模式', icon: 'pen' }];
  await env.initialize();
  env.H.actions['launcher-manage']({ dataset: { surface: 'mini' } });
  for (const id of ['item-0', 'item-1', 'item-2']) env.H.actions['launcher-pin-add'](click(id, { itemType: 'app' }));
  env.H.actions['launcher-pin-add'](click('scene-demo', { itemType: 'scene' }));
  env.H.actions['launcher-pin-add'](click('item-3', { itemType: 'app' }));
  assert.match(env.H.ui.modal.body, /4 \/ 4/);
  env.H.actions['launcher-pin-move']({ dataset: { index: '3', direction: '-1' } });
  await env.H.actions['launcher-pins-save']();
  assert.deepEqual(env.sent.find(item => item.type === 'launcher.setPins').payload, { surface: 'mini', pins: [ { itemType: 'app', itemId: 'item-0', enabled: true }, { itemType: 'app', itemId: 'item-1', enabled: true }, { itemType: 'scene', itemId: 'scene-demo', enabled: true }, { itemType: 'app', itemId: 'item-2', enabled: true } ] });
  assert.equal(env.window.DesktopHubDesktop.getState().hostState.settings.appEntries.length, 5);
  await env.H.actions['scene-pin'](click('scene-demo'));
  const nextPins = env.sent.filter(item => item.type === 'launcher.setPins').at(-1).payload.pins;
  assert.equal(nextPins.length, 3);
  assert.equal(nextPins.some(pin => pin.itemType === 'scene'), false);
  assert.equal('pinned' in env.H.state.scenes[0], false, 'native layout pinning does not turn scene demos into native execution state');
});

test('Dock renders app icons separately from scene demos and saves only permitted density preference', async () => {
  const env = setup('dock');
  env.state.settings.appEntries = [appEntry('editor')];
  env.state.settings.launcherPins = [{ surface: 'dock', itemType: 'app', itemId: 'editor', enabled: true, displayOrder: 0 }, { surface: 'dock', itemType: 'scene', itemId: 'demo', enabled: true, displayOrder: 1 }];
  env.H.state.scenes = [{ id: 'demo', name: '组合', icon: 'pen' }];
  let appArea = '';
  env.nodes.set('.dock-section-title', { innerHTML: '', insertAdjacentHTML(_, markup) { appArea = markup; } });
  const sceneArea = { innerHTML: '' };
  env.nodes.set('.dock-scenes', sceneArea);
  await env.initialize();
  assert.match(appArea, /dock-app-grid/);
  assert.match(appArea, /data-action="app-launch" data-id="editor"/);
  assert.match(sceneArea.innerHTML, /data-action="run-scene" data-id="demo"/);
  assert.match(sceneArea.innerHTML, /0 个动作/);
  await env.H.actions['dock-icon-size']({ value: 'compact' });
  assert.deepEqual(env.sent.find(item => item.type === 'settings.save').payload, { settings: { dockIconSize: 'compact' } });
  assert.match(appArea, /icon-size-compact/);
});

test('Mini renders empty add entry then mixed pins and visible real error with dismissal', async () => {
  const env = setup('mini', { 'apps.launch': async () => ({ success: false, code: 'TARGET_NOT_FOUND', message: '应用文件已不存在，请更换文件。' }) });
  const classes = new Set();
  const bar = { innerHTML: '', setAttribute() {}, classList: { toggle(key, enabled) { enabled ? classes.add(key) : classes.delete(key); } } };
  env.nodes.set('.mini-bar', bar);
  await env.initialize();
  assert.match(bar.innerHTML, /aria-label="添加入口"/);
  env.H.state.scenes = [{ id: 'scene', name: '创作', icon: 'pen' }];
  const settings = { ...baseSettings, appEntries: [appEntry('app')], launcherPins: [{ surface: 'mini', itemType: 'app', itemId: 'app', enabled: true, displayOrder: 0 }, { surface: 'mini', itemType: 'scene', itemId: 'scene', enabled: true, displayOrder: 1 }] };
  env.emit('state.changed', { settings });
  assert.match(bar.innerHTML, /launcher-pin.*data-action="app-launch" data-id="app"/);
  assert.match(bar.innerHTML, /launcher-pin.*data-action="run-scene" data-id="scene"/);
  await env.H.actions['app-launch'](click('app'));
  assert.match(bar.innerHTML, /class="mini-launch-error" role="alert"/);
  assert.match(bar.innerHTML, /应用文件已不存在，请更换文件/);
  assert.equal(classes.has('has-launch-error'), true);
  env.H.actions['launch-dismiss']();
  assert.equal(classes.has('has-launch-error'), false);
});

test('native icons are preferred, missing icons share fallback, and stored paths or remote URLs never become image sources', async () => {
  const env = setup();
  env.state.settings.appEntries = [{ ...appEntry('native'), name: '<script>注入</script>' }, { ...appEntry('fallback', 1), icon: 'https://invalid.example/icon.png' }];
  env.state.appIcons = { native: 'data:image/png;base64,AAAA', fallback: 'https://invalid.example/icon.png' };
  await env.initialize();
  const markup = env.H.pages.home();
  assert.equal((markup.match(/class="launcher-real-icon"/g) || []).length, 1);
  assert.match(markup, /&lt;script&gt;注入&lt;\/script&gt;/);
  assert.doesNotMatch(markup, /<script>|https:\/\/invalid/);
});

test('duplicate clicks are pending-guarded per app while another saved app can launch independently', async () => {
  const calls = [];
  const env = setup('workspace', { 'apps.launch': ({ appId }) => new Promise(resolve => calls.push({ appId, resolve })) });
  env.state.settings.appEntries = [appEntry('first'), appEntry('second', 1)];
  await env.initialize();
  const first = env.H.actions['app-launch'](click('first'));
  await env.H.actions['app-launch'](click('first'));
  const second = env.H.actions['app-launch'](click('second'));
  assert.deepEqual(calls.map(call => call.appId), ['first', 'second']);
  calls[1].resolve({ success: true, message: '第二个已提交。' });
  await second;
  calls[0].resolve({ success: false, code: 'ACCESS_DENIED', message: '操作系统拒绝启动。' });
  await first;
  assert.equal(env.window.DesktopHubDesktop.getState().launch.appId, 'first');
  assert.equal(env.window.DesktopHubDesktop.getState().launch.code, 'ACCESS_DENIED');
  assert.match(env.H.pages.home(), /data-id="second" data-status="success"/);
});

test('loaded Mini and Dock refresh custom scene metadata without importing cached applications, preferences or execution actions', async () => {
  const storageKey = 'desktop-hub-m0-v07';
  for (const surface of ['mini', 'dock']) {
    const env = setup(surface);
    env.state.settings.launcherPins = [{ surface, itemType: 'scene', itemId: 'custom-scene', enabled: true, displayOrder: 0 }];
    const bar = { innerHTML: '', setAttribute() {}, classList: { toggle() {} } };
    const sceneArea = { innerHTML: '' };
    env.nodes.set('.mini-bar', bar);
    env.nodes.set('.dock-scenes', sceneArea);
    await env.initialize();
    const cached = { version: 7, scenes: [{ id: 'custom-scene', name: '我的新场景', icon: 'pen', actions: [{ target: 'unvalidated' }] }], settings: { theme: 'light', appEntries: [appEntry('forged')] }, files: ['not-synchronized'] };
    env.emitStorage(storageKey, JSON.stringify(cached));
    assert.equal(env.H.state.scenes[0].name, '我的新场景');
    assert.equal(env.H.state.scenes[0].actions.length, 0);
    assert.equal(env.H.state.settings.theme, 'system');
    assert.equal(env.window.DesktopHubDesktop.getState().hostState.settings.appEntries.length, 0);
    assert.match(surface === 'mini' ? bar.innerHTML : sceneArea.innerHTML, /我的新场景/);
    cached.scenes[0].name = '重命名后的场景';
    env.storage.set(storageKey, JSON.stringify(cached));
    // A suspended view may miss storage events; host state sync catches up when shown.
    env.emit('state.changed', { settings: env.state.settings });
    assert.match(surface === 'mini' ? bar.innerHTML : sceneArea.innerHTML, /重命名后的场景/);
    env.emitStorage(storageKey, '{malformed');
    assert.equal(env.H.state.scenes[0].name, '重命名后的场景');
    const originalCache = JSON.stringify(cached);
    env.storage.set(storageKey, originalCache);
    env.H.save();
    assert.equal(env.storage.get(storageKey), originalCache, 'compact metadata saves cannot overwrite the original scene execution action sets');
    assert.equal(JSON.parse(env.storage.get(storageKey)).scenes[0].actions.length, 1);
    assert.equal(env.sent.some(item => item.type === 'settings.save'), false, 'compact generic saves cannot send unsupported full preferences');
    assert.equal(env.sent.some(item => item.type === 'apps.launch'), false);
  }
  const workspace = setup('workspace');
  workspace.H.state.scenes = [{ id: 'editing', name: '未保存的当前草稿' }];
  await workspace.initialize();
  workspace.emitStorage(storageKey, JSON.stringify({ version: 7, scenes: [{ id: 'other', name: '外部缓存' }] }));
  assert.equal(workspace.H.state.scenes[0].id, 'editing', 'Workspace remains the scene demo editing source');
});
