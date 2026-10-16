import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import test from 'node:test';

const source = fs.readFileSync(new URL('../system.js', import.meta.url), 'utf8');
const clone = value => structuredClone(value);
function setup(responses = {}, surface = 'workspace') {
  let activeSection = 'appearance';
  const nodes = new Map(), sent = [];
  let hostState = {
    settings: { dockHotkey: 'Alt+D', workspaceHotkey: 'Alt+W', miniEnabled: true, miniOpacity: .92,
      foregroundBlocklist: [], miniPosition: 'left', miniSize: 'normal', miniMode: 'icons', dockPosition: 'left', dockSize: 'normal', dockIconSize: 'normal', hoverEnabled: true, hoverDelay: 500, allowForeground: true },
    systemIntegration: { desktopIcons: { success: true, visible: true, supported: true, firstHideAuthorized: false, exitPolicy: null, conflict: false, message: '' },
      startup: { success: true, enabled: false, needsRepair: false }, hotkeys: { success: true, dockHotkey: 'Alt+D', workspaceHotkey: 'Alt+W' } }
  };
  const H = {
    state: { settings: {} }, ui: { modal: null }, pages: {}, actions: {}, render() {},
    escape(value) { return String(value ?? '').replace(/[&<>"]/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[char])); },
    icon() { return '<svg></svg>'; },
    showModal(title, body, footer) { H.ui.modal = { title, body, footer, locked: false }; }, closeModal() { H.ui.modal = null; }
  };
  H.pages.settings = () => `<div class="page-heading"><span class="badge">M0 · 仅模拟设置</span></div><div class="settings-layout"><nav>${['appearance','surfaces','shortcuts','protection','sources','system'].map(id => `<button data-section="${id}" ${activeSection === id ? 'aria-current="page"' : ''}>${id}</button>`).join('')}</nav><div class="settings-content">existing ${activeSection}</div></div>`;
  H.actions['settings-section'] = el => { activeSection = el.dataset.section; H.render(); };
  const desktop = { getState: () => ({ surface, hostState }), applyHostState(next) { hostState = { ...hostState, ...clone(next) }; } };
  const bridge = { async request(type, payload = {}) {
    sent.push({ type, payload: clone(payload) });
    if (responses[type]) return responses[type](payload, hostState);
    if (type === 'system.getState') return clone(hostState);
    if (type === 'settings.save') { Object.assign(hostState.settings, payload.settings); return clone(hostState); }
    if (type === 'system.setDesktopIcons') {
      Object.assign(hostState.systemIntegration.desktopIcons, { visible: payload.visible, firstHideAuthorized: true, exitPolicy: payload.firstHidePolicy || hostState.systemIntegration.desktopIcons.exitPolicy });
    }
    if (type === 'system.setStartup') hostState.systemIntegration.startup.enabled = payload.enabled;
    if (type === 'system.setHotkeys') Object.assign(hostState.settings, { dockHotkey: payload.dock, workspaceHotkey: payload.workspace });
    return { state: clone(hostState), operation: { success: true } };
  } };
  const document = { querySelector: selector => nodes.get(selector) || null };
  const window = { Hub: H, DesktopHubDesktop: desktop, DesktopHubNative: { bridge } };
  vm.runInNewContext(source, { window, document }, { filename: 'system.js' });
  return { H, sent, nodes, desktop, window, state: () => hostState,
    section(id) { H.actions['settings-section']({ dataset: { section: id } }); return H.pages.settings(); } };
}

test('first hide requires explicit unselected policy; cancelling sends no system mutation', async () => {
  const env = setup();
  env.section('system');
  env.H.actions['sys-icons']({ dataset: { visible: 'false' } });
  assert.match(env.H.ui.modal.body, /不会删除、移动或修改任何桌面文件/);
  assert.match(env.H.ui.modal.body, /桌面空白处右键 → 查看 → 显示桌面图标/);
  assert.doesNotMatch(env.H.ui.modal.body, /checked/);
  assert.match(env.H.ui.modal.footer, /data-action="sys-confirm-hide"[^>]*disabled/);
  await env.H.actions['sys-confirm-hide']();
  assert.equal(env.sent.some(item => item.type === 'system.setDesktopIcons'), false);
  env.H.actions['sys-cancel-policy']();
  assert.equal(env.H.ui.modal, null);
  assert.equal(env.sent.some(item => item.type.startsWith('system.set')), false);
  assert.equal(env.state().systemIntegration.desktopIcons.visible, true);
});

test('Task4 Dock launch auto-close is opt-in and saves only its dedicated setting',async()=>{
  const env=setup();assert.equal(env.state().settings.autoCloseDock,undefined);
  const markup=env.section('surfaces');assert.match(markup,/独立应用启动请求成功后自动收起 Dock/);assert.doesNotMatch(markup,/aria-label="独立应用启动请求成功后自动收起 Dock" checked/);
  await env.H.actions['sys-auto-close-dock']({checked:true});assert.deepEqual(env.sent.filter(x=>x.type==='settings.save').at(-1).payload,{settings:{autoCloseDock:true}});
  assert.equal(env.state().settings.autoCloseDock,true);
});

test('performance mode choice saves the explicit preference and explains the hidden release delay', async () => {
  const env = setup();
  const markup = env.section('system');
  assert.match(markup, /性能模式/);
  assert.match(markup, /快速唤出/);
  assert.match(markup, /轻量常驻/);
  assert.match(markup, /60 秒/);
  await env.H.actions['sys-performance-mode']({ value: 'lightweight' });
  assert.deepEqual(env.sent.filter(x => x.type === 'settings.save').at(-1).payload, { settings: { performanceMode: 'lightweight' } });
  assert.equal(env.state().settings.performanceMode, 'lightweight');
});

test('Workspace brand displays the connected host version after every state update', () => {
  const env = setup(), label = { textContent: '' };
  env.nodes.set('.workspace-header .brand small', label);
  env.state().version = '0.3.0'; env.H.render();
  assert.equal(label.textContent, '工作空间 · 0.3.0');
  env.state().version = '0.3.1'; env.H.render();
  assert.equal(label.textContent, '工作空间 · 0.3.1');
});

test('first hide transmits chosen exit policy; subsequent hide commands remain target-state writes', async () => {
  const env = setup();
  env.H.actions['sys-icons']({ dataset: { visible: 'false' } });
  env.H.actions['sys-policy-choice']({ value: 'keepHidden' });
  await env.H.actions['sys-confirm-hide']();
  assert.deepEqual(env.sent[0], { type: 'system.setDesktopIcons', payload: { visible: false, firstHidePolicy: 'keepHidden' } });
  assert.equal(env.H.ui.modal, null);
  await env.H.actions['sys-icons']({ dataset: { visible: 'false' } });
  await env.H.actions['sys-icons']({ dataset: { visible: 'false' } });
  assert.deepEqual(env.sent.slice(1).map(item => item.payload), [{ visible: false }, { visible: false }]);
  assert.equal(env.state().systemIntegration.desktopIcons.visible, false);
  await env.H.actions['sys-icons']({ dataset: { visible: 'true' } });
  assert.deepEqual(env.sent.at(-1).payload, { visible: true });
});

test('unknown or unsupported visibility never appears successful; native recovery path stays available', () => {
  const env = setup();
  Object.assign(env.state().systemIntegration.desktopIcons, { visible: null, supported: false, success: false, message: 'Explorer 无响应。' });
  const markup = env.section('system');
  assert.match(markup, /当前状态未知/);
  assert.match(markup, /显示桌面图标[^>]*|显示桌面图标/);
  assert.match(markup, /data-action="sys-icons"[^>]*disabled/);
  assert.match(markup, /Explorer 无响应/);
  assert.match(markup, /桌面空白处右键 → 查看 → 显示桌面图标/);
  assert.doesNotMatch(markup, /模拟|演示数据/);
});

test('external-change conflicts show preserved Windows state and paused automatic restoration', () => {
  const env = setup();
  Object.assign(env.state().systemIntegration.desktopIcons, { conflict: true, visible: true, message: 'Windows 中已手动显示图标。' });
  const markup = env.section('system');
  assert.match(markup, /当前显示/);
  assert.match(markup, /Windows 中已手动显示图标/);
  assert.match(markup, /自动恢复已暂停/);
});

test('startup registration failure keeps actual disabled state and returns native reason', async () => {
  const env = setup({ 'system.setStartup': async (_, state) => ({ state: clone(state), operation: { success: false, message: 'Windows 拒绝写入启动项。' } }) });
  await env.H.actions['sys-startup']({ dataset: { enabled: 'true' } });
  assert.equal(env.state().systemIntegration.startup.enabled, false);
  assert.equal(env.window.DesktopHubSystem.getState().feedbackError, true);
  assert.match(env.window.DesktopHubSystem.getState().feedback, /Windows 拒绝写入/);
  assert.doesNotMatch(env.window.DesktopHubSystem.getState().feedback, /已.*更新/);
});

test('unreadable or unsupported startup registrations show unknown instead of falsely disabled', () => {
  for (const code of ['registry_unavailable', 'startup_entry_unsupported']) {
    const env = setup();
    Object.assign(env.state().systemIntegration.startup, { success: false, code, enabled: false, message: 'Windows 启动项无法可靠读取。' });
    const markup = env.section('system');
    assert.match(markup, /当前状态未知，请重新核对 Windows 启动项/);
    assert.match(markup, /Windows 启动项无法可靠读取/);
    assert.doesNotMatch(markup, /已关闭|data-action="sys-startup"/);
    assert.match(markup, /data-action="sys-refresh"/);
  }
});

test('a failed startup disable retains known registered state and offers the same target retry', async () => {
  const env = setup({ 'system.setStartup': async (_, state) => ({ state: clone(state), operation: { success: false, code: 'startup_entry_changed', message: '启动项在写入前发生变化，操作已取消。' } }) });
  env.state().systemIntegration.startup.enabled = true;
  env.section('system');
  await env.H.actions['sys-startup']({ dataset: { enabled: 'false' } });
  const markup = env.H.pages.settings();
  assert.match(markup, /已启用/);
  assert.match(markup, /data-action="sys-startup"[^>]*data-enabled="false"/);
  assert.match(markup, /启动项在写入前发生变化/);
  assert.doesNotMatch(markup, /已关闭/);
  assert.equal(env.state().systemIntegration.startup.enabled, true);
});

test('malformed mutation reply cannot produce a successful system change message', async () => {
  const env = setup({ 'system.setDesktopIcons': async (_, state) => ({ state: clone(state) }) });
  await env.H.actions['sys-icons']({ dataset: { visible: 'true' } });
  assert.equal(env.window.DesktopHubSystem.getState().feedbackError, true);
  assert.match(env.window.DesktopHubSystem.getState().feedback, /未收到系统操作结果/);
});

test('hotkey conflict retains saved combinations and editable draft, using dedicated atomic operation', async () => {
  const env = setup({ 'system.setHotkeys': async (_, state) => ({ state: clone(state), operation: { success: false, error: 'Ctrl+Alt+W 已被其他程序占用。' } }) });
  env.section('shortcuts');
  env.H.actions['sys-shortcut']({ dataset: { key: 'dock' }, value: 'Ctrl+Alt+D' });
  env.H.actions['sys-shortcut']({ dataset: { key: 'workspace' }, value: 'Ctrl+Alt+W' });
  await env.H.actions['sys-save-hotkeys']();
  const request = env.sent.find(item => item.type === 'system.setHotkeys');
  assert.deepEqual(request.payload, { dock: 'Ctrl+Alt+D', workspace: 'Ctrl+Alt+W' });
  assert.equal(env.state().settings.dockHotkey, 'Alt+D');
  assert.equal(env.state().settings.workspaceHotkey, 'Alt+W');
  const markup = env.H.pages.settings();
  assert.match(markup, /Ctrl\+Alt\+W 已被其他程序占用/);
  assert.match(markup, /value="Ctrl\+Alt\+D"/);
  assert.equal(env.sent.some(item => item.type === 'settings.save'), false);
});

test('successful hotkeys update native display values; native Mini opacity reflects stored non-preset value', async () => {
  const env = setup();
  env.section('shortcuts');
  env.H.actions['sys-shortcut']({ dataset: { key: 'dock' }, value: 'Ctrl+Alt+D' });
  env.H.actions['sys-shortcut']({ dataset: { key: 'workspace' }, value: 'Ctrl+Alt+W' });
  await env.H.actions['sys-save-hotkeys']();
  assert.equal(env.H.state.settings.workspaceShortcut, 'Ctrl+Alt+W');
  assert.equal(env.H.state.settings.dockShortcut, 'Ctrl+Alt+D');
  const markup = env.section('surfaces');
  assert.match(markup, /type="range" min="0" max="100" step="1" value="92"/);
  assert.match(markup, /data-action="sys-toggle-setting" data-setting="miniEnabled"/);
});

test('blocklist validates filenames before native write and escapes user content in settings', async () => {
  const env = setup();
  env.section('protection');
  env.H.actions['sys-blocklist']({ value: 'C:\\Games\\game.exe\n<script>.exe' });
  await env.H.actions['sys-save-blocklist']();
  assert.equal(env.sent.some(item => item.type === 'settings.save'), false);
  assert.match(env.H.pages.settings(), /&lt;script&gt;/);
  env.H.actions['sys-blocklist']({ value: 'game.exe\n another.exe \n\ngame.exe' });
  await env.H.actions['sys-save-blocklist']();
  assert.deepEqual(env.sent.find(item => item.type === 'settings.save').payload, { settings: { foregroundBlocklist: ['game.exe', 'another.exe'] } });
  assert.deepEqual(env.state().settings.foregroundBlocklist, ['game.exe', 'another.exe']);
});

test('slow system operation ignores repeated clicks and failure keeps first-hide modal recoverable', async () => {
  let resolve;
  const env = setup({ 'system.setDesktopIcons': (_, state) => new Promise(done => { resolve = () => done({ state: clone(state), operation: { success: false, message: 'Explorer 状态核对失败。' } }); }) });
  env.H.actions['sys-icons']({ dataset: { visible: 'false' } });
  env.H.actions['sys-policy-choice']({ value: 'restore' });
  const first = env.H.actions['sys-confirm-hide']();
  assert.equal(env.H.ui.modal.locked, true);
  env.H.actions['sys-cancel-policy']();
  assert.notEqual(env.H.ui.modal, null, 'an issued native write cannot be cancelled by dismissing its modal');
  await env.H.actions['sys-confirm-hide']();
  assert.equal(env.sent.length, 1);
  resolve(); await first;
  assert.equal(env.H.ui.modal.locked, false);
  assert.equal(env.state().systemIntegration.desktopIcons.visible, true);
  assert.equal(env.window.DesktopHubSystem.getState().feedbackError, true);
});

test('compact surfaces cannot install native system setting controls', () => {
  for (const surface of ['mini','dock']) {
    const env = setup({}, surface);
    assert.equal(env.H.actions['sys-icons'], undefined);
    assert.equal(env.window.DesktopHubSystem, undefined);
    assert.equal(env.sent.length, 0);
  }
});

test('Workspace opacity lives in appearance, previews percent and saves independently', async () => {
  const env = setup();
  const markup = env.section('appearance');
  assert.match(markup, /existing appearance/);
  assert.match(markup, /value="100" aria-label="Workspace 不透明度"/);
  const output = { textContent: '' }, attributes = {};
  const slider = { value: '65', parentElement: { querySelector: () => output }, setAttribute: (key, value) => { attributes[key] = value; } };
  env.H.actions['sys-workspace-opacity-preview'](slider, { type: 'input' });
  assert.equal(output.textContent, '65%');
  assert.equal(attributes['aria-valuetext'], '65%');
  assert.equal(env.sent.length, 0);
  await env.H.actions['sys-workspace-opacity-preview'](slider, { type: 'change' });
  assert.deepEqual(env.sent[0], { type: 'settings.save', payload: { settings: { workspaceOpacity: .65 } } });
  assert.equal(env.state().settings.miniOpacity, .92);
  assert.match(env.H.pages.settings(), /value="65" aria-label="Workspace 不透明度"/);
  for (const value of ['0', '100']) await env.H.actions['sys-workspace-opacity-preview']({ value }, { type: 'change' });
  assert.deepEqual(env.sent.slice(1).map(item => item.payload.settings.workspaceOpacity), [0, 1]);
  for (const value of ['NaN', '-1', '101']) await env.H.actions['sys-workspace-opacity-preview']({ value }, { type: 'change' });
  assert.equal(env.sent.length, 3);
});

test('failed Workspace opacity save shows error and keeps stored percent', async () => {
  const env = setup({ 'settings.save': async () => { throw new Error('设置写入失败'); } });
  env.section('appearance');
  await env.H.actions['sys-workspace-opacity-preview']({ value: '45' }, { type: 'change' });
  assert.match(env.H.pages.settings(), /设置写入失败/);
  assert.match(env.H.pages.settings(), /value="100" aria-label="Workspace 不透明度"/);
});
