/* Versioned, request/response-only WebView2 bridge. No shell or path execution API. */
(function (root) {
  'use strict';
  const version = 1;
  const allowedTypes = new Set([
    'app.getState', 'app.exit', 'settings.get', 'settings.save',
    'window.openWorkspace', 'window.closeWorkspace', 'window.openDock',
    'window.closeDock', 'window.drag', 'window.resizeSurface',
    'apps.add', 'apps.update', 'apps.replaceTarget', 'apps.remove',
    'apps.reorder', 'apps.launch', 'launcher.setPins',
    'scenes.save', 'scenes.remove', 'scenes.reorder', 'scenes.run', 'scenes.pickTarget',
    'resources.query', 'resources.discover', 'resources.preview', 'resources.cancel',
    'resources.confirm', 'resources.refresh', 'resources.removeSource', 'resources.favorite',
    'resources.beginRepair', 'resources.repair', 'resources.clean', 'resources.saveTag',
    'resources.deleteTag', 'resources.bindTag', 'resources.suggestion', 'resources.browse',
    'resources.addBrowsed', 'resources.open'
  ]);

  class NativeBridgeError extends Error {
    constructor(code, message) {
      super(message);
      this.name = 'NativeBridgeError';
      this.code = code;
    }
  }

  function createBridge(webview, options = {}) {
    let sequence = 0;
    const pending = new Map();
    const subscribers = new Map();
    const timeoutMs = options.timeoutMs ?? 12000;
    const setTimer = options.setTimeout || root.setTimeout.bind(root);
    const clearTimer = options.clearTimeout || root.clearTimeout.bind(root);
    const available = !!webview && typeof webview.postMessage === 'function' && typeof webview.addEventListener === 'function';
    let disposed = false;

    function receive(event) {
      let message = event.data;
      if (typeof message === 'string') {
        try { message = JSON.parse(message); } catch { return; }
      }
      if (!message || typeof message !== 'object' || Array.isArray(message) || message.version !== version) return;
      if (typeof message.id === 'string') {
        const request = pending.get(message.id);
        if (!request || typeof message.ok !== 'boolean') return;
        // An invalid envelope cannot resolve an operation as success.
        if (!message.ok && (!message.error || typeof message.error.code !== 'string' || typeof message.error.message !== 'string')) return;
        clearTimer(request.timer);
        pending.delete(message.id);
        if (message.ok) request.resolve(message.result);
        else request.reject(new NativeBridgeError(message.error.code, message.error.message));
        return;
      }
      if (!['state.changed', 'window.navigate', 'scene.executions', 'resources.changed', 'resources.progress'].includes(message.type)) return;
      if (!message.payload || typeof message.payload !== 'object' || Array.isArray(message.payload)) return;
      for (const callback of subscribers.get(message.type) || []) {
        try { callback(message.payload); } catch (error) { options.onEventError?.(error); }
      }
    }
    if (available) webview.addEventListener('message', receive);

    function request(type, payload = {}, requestOptions = {}) {
      if (!allowedTypes.has(type)) return Promise.reject(new NativeBridgeError('UNKNOWN_MESSAGE', '此操作尚未受支持。'));
      if (!payload || typeof payload !== 'object' || Array.isArray(payload)) return Promise.reject(new NativeBridgeError('INVALID_PAYLOAD', '请求参数格式不正确。'));
      if (!available || disposed) return Promise.reject(new NativeBridgeError('HOST_UNAVAILABLE', '原生宿主尚未连接，请通过 Desktop Hub 应用打开界面。'));
      const id = `ui-${Date.now().toString(36)}-${++sequence}`;
      return new Promise((resolve, reject) => {
        const delay = requestOptions.timeoutMs ?? timeoutMs;
        const timer = setTimer(() => {
          pending.delete(id);
          reject(new NativeBridgeError('TIMEOUT', '原生操作尚未返回结果，请重试。'));
        }, delay);
        pending.set(id, { resolve, reject, timer });
        try { webview.postMessage({ version, id, type, payload }); }
        catch {
          clearTimer(timer);
          pending.delete(id);
          reject(new NativeBridgeError('SEND_FAILED', '无法将请求发送给原生宿主。'));
        }
      });
    }

    function on(type, callback) {
      if (typeof callback !== 'function') throw new TypeError('Event callback must be a function.');
      if (!subscribers.has(type)) subscribers.set(type, new Set());
      subscribers.get(type).add(callback);
      return () => subscribers.get(type)?.delete(callback);
    }

    function dispose() {
      if (disposed) return;
      disposed = true;
      if (available && typeof webview.removeEventListener === 'function') webview.removeEventListener('message', receive);
      for (const entry of pending.values()) {
        clearTimer(entry.timer);
        entry.reject(new NativeBridgeError('HOST_UNAVAILABLE', '应用连接已关闭。'));
      }
      pending.clear();
      subscribers.clear();
    }

    return Object.freeze({ available, request, on, dispose });
  }

  root.DesktopHubNative = Object.freeze({
    createBridge,
    NativeBridgeError,
    bridge: createBridge(root.chrome?.webview)
  });
}(globalThis));
