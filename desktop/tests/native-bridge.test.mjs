import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const source = fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'native.js'), 'utf8');
const context = vm.createContext({ setTimeout, clearTimeout });
vm.runInContext(source, context, { filename: 'native.js' });
const { createBridge } = context.DesktopHubNative;

function fakeWebView() {
  const listeners = new Set();
  const sent = [];
  return {
    sent,
    postMessage(message) { sent.push(JSON.parse(JSON.stringify(message))); },
    addEventListener(type, fn) { if (type === 'message') listeners.add(fn); },
    removeEventListener(type, fn) { if (type === 'message') listeners.delete(fn); },
    receive(data) { for (const fn of listeners) fn({ data }); }
  };
}

test('request/response correlation and only selected native launch id crosses boundary', async () => {
  const view = fakeWebView();
  const bridge = createBridge(view);
  const pending = bridge.request('apps.launch', { appId: 'chosen-native-id' });
  assert.equal(view.sent.length, 1);
  const { id, ...message } = view.sent[0];
  assert.deepEqual(message, { version: 1, type: 'apps.launch', payload: { appId: 'chosen-native-id' } });
  assert.match(id, /^ui-/);
  view.receive({ version: 1, id, ok: true, result: { success: true, processId: 123 } });
  assert.deepEqual(await pending, { success: true, processId: 123 });
  bridge.dispose();
});

test('out-of-order responses resolve their original requests', async () => {
  const view = fakeWebView();
  const bridge = createBridge(view);
  const first = bridge.request('app.getState');
  const second = bridge.request('settings.get');
  view.receive({ version: 1, id: view.sent[1].id, ok: true, result: 'settings' });
  view.receive(JSON.stringify({ version: 1, id: view.sent[0].id, ok: true, result: 'app' }));
  assert.equal(await first, 'app');
  assert.equal(await second, 'settings');
  bridge.dispose();
});

test('system API admits only explicit target-state operations and preserves structured native failures', async () => {
  const view = fakeWebView();
  const bridge = createBridge(view);
  for (const [type, payload] of [
    ['system.getState', {}], ['system.setDesktopIcons', { visible: false, firstHidePolicy: 'restore' }],
    ['system.setIconExitPolicy', { policy: 'keepHidden' }], ['system.setStartup', { enabled: false }],
    ['system.setHotkeys', { dock: 'Alt+D', workspace: 'Ctrl+Alt+W' }]
  ]) {
    const pending = bridge.request(type, payload);
    const sent = view.sent.at(-1);
    assert.deepEqual(sent.payload, payload);
    view.receive({ version: 1, id: sent.id, ok: true, result: { operation: { success: false, message: '实际系统失败' } } });
    assert.equal((await pending).operation.success, false);
  }
  await assert.rejects(bridge.request('system.execute', { command: 'reg.exe' }), error => error.code === 'UNKNOWN_MESSAGE');
  await assert.rejects(bridge.request('system.toggleDesktopIcons', {}), error => error.code === 'UNKNOWN_MESSAGE');
  assert.equal(view.sent.length, 5);
  bridge.dispose();
});

test('native failures preserve the actual host code and message', async () => {
  const view = fakeWebView();
  const bridge = createBridge(view);
  const pending = bridge.request('apps.launch', { appId: 'missing' });
  view.receive({ version: 1, id: view.sent[0].id, ok: false, error: { code: 'TARGET_NOT_FOUND', message: '所选程序已不存在。' } });
  await assert.rejects(pending, error => error.code === 'TARGET_NOT_FOUND' && error.message === '所选程序已不存在。');
  bridge.dispose();
});

test('unknown types and invalid payload never reach host', async () => {
  const view = fakeWebView();
  const bridge = createBridge(view);
  await assert.rejects(bridge.request('shell.execute', { command: 'anything' }), error => error.code === 'UNKNOWN_MESSAGE');
  await assert.rejects(bridge.request('launch.execute', { targetId: 'legacy' }), error => error.code === 'UNKNOWN_MESSAGE');
  await assert.rejects(bridge.request('app.getState', 'invalid'), error => error.code === 'INVALID_PAYLOAD');
  await assert.rejects(bridge.request('app.getState', []), error => error.code === 'INVALID_PAYLOAD');
  assert.equal(view.sent.length, 0);
  bridge.dispose();
});

test('malformed or wrong-version responses cannot simulate success', async () => {
  const view = fakeWebView();
  const bridge = createBridge(view, { timeoutMs: 15 });
  const pending = bridge.request('app.getState');
  const id = view.sent[0].id;
  view.receive('{not json');
  view.receive({ version: 2, id, ok: true, result: 'wrong version' });
  view.receive({ version: 1, id: 'unrecognized', ok: true });
  view.receive({ version: 1, id, ok: 'true' });
  view.receive({ version: 1, id, ok: false, error: { message: 'missing code' } });
  await assert.rejects(pending, error => error.code === 'TIMEOUT');
  bridge.dispose();
});

test('events are restricted and subscriptions can be removed', () => {
  const view = fakeWebView();
  const bridge = createBridge(view);
  const received = [];
  const unsubscribe = bridge.on('state.changed', value => received.push(value));
  view.receive({ version: 1, type: 'state.changed', payload: { settings: { theme: 'dark' } } });
  view.receive({ version: 2, type: 'state.changed', payload: { settings: { theme: 'light' } } });
  unsubscribe();
  view.receive({ version: 1, type: 'state.changed', payload: { settings: { theme: 'light' } } });
  assert.deepEqual(received, [{ settings: { theme: 'dark' } }]);
  bridge.dispose();
});

test('unavailable WebView and postMessage failure return honest errors', async () => {
  const unavailable = createBridge(null);
  assert.equal(unavailable.available, false);
  await assert.rejects(unavailable.request('app.getState'), error => error.code === 'HOST_UNAVAILABLE');
  const view = fakeWebView();
  view.postMessage = () => { throw new Error('closed view'); };
  const bridge = createBridge(view);
  await assert.rejects(bridge.request('app.getState'), error => error.code === 'SEND_FAILED');
  bridge.dispose();
});

test('dispose rejects pending requests and late messages cannot revive them', async () => {
  const view = fakeWebView();
  const bridge = createBridge(view);
  const pending = bridge.request('app.getState');
  bridge.dispose();
  await assert.rejects(pending, error => error.code === 'HOST_UNAVAILABLE');
  view.receive({ version: 1, id: view.sent[0].id, ok: true, result: 'too late' });
  await assert.rejects(bridge.request('app.getState'), error => error.code === 'HOST_UNAVAILABLE');
});
