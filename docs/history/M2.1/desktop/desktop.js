/* Desktop-only adapter: keep all accepted M0 assets and separate native operations. */
(function () {
  'use strict';
  const H = window.Hub;
  const bridge = window.DesktopHubNative.bridge;
  const surfaces = new Set(['mini', 'dock', 'workspace']);
  const requestedSurface = new URLSearchParams(location.search).get('surface');
  const surface = surfaces.has(requestedSurface) ? requestedSurface : 'workspace';
  const editableSettings = [
    'theme', 'reducedMotion', 'transparency', 'miniPosition', 'miniSize', 'miniMode',
    'dockPosition', 'dockSize', 'dockIconSize', 'hoverEnabled', 'hoverDelay', 'allowForeground'
  ];
  const pages = new Set(['home', 'scenes', 'library', 'tags', 'favorites', 'search', 'settings']);
  const prototypeStorageKey = 'desktop-hub-m0-v07';
  const originalRender = H.render;
  const originalNavigate = H.navigate;
  const originalSave = H.save;
  const originalApplyTheme = H.applyTheme;
  const originalHome = H.pages.home;
  let hostState = null;
  let lastSettings = '';
  let requestedPage = null;
  let saveTimer = null;
  let saveInFlight = false;
  let pendingVisualSettings = null;
  let activeSaveSettings = null;
  let measurementObserver = null;
  let measurementTimer = null;
  let lastMeasurement = '';
  let launch = { status: 'idle', message: '', code: '', appId: '' };
  const appLaunches = new Map();
  let adding = false;
  let mutationInFlight = false;
  let appQuery = '';
  let managingApps = false;
  let pinDraft = null;
  let warningShown = false;

  H.state.onboardingComplete = true;
  H.ui.surface = surface;
  document.documentElement.dataset.surface = surface;

  const e = value => H.escape(value);
  const icon = (name, size = 20) => H.icon(name, size);
  const button = (action, text, primary = false, disabled = false) => `<button class="button ${primary ? 'primary' : 'quiet'}" data-action="${action}" ${disabled ? 'disabled' : ''}>${text}</button>`;
  function visualSettings(settings) {
    return Object.fromEntries(editableSettings.filter(key => settings[key] !== undefined).map(key => [key, settings[key]]));
  }
  function apps() { return [...(hostState?.settings?.appEntries || [])].sort((a, b) => a.displayOrder - b.displayOrder); }
  function appById(id) { return apps().find(app => app.id === id); }
  function pins(where) { return [...(hostState?.settings?.launcherPins || [])].filter(pin => pin.surface === where && pin.enabled).sort((a, b) => a.displayOrder - b.displayOrder); }
  function pinItem(pin) { return pin.itemType === 'app' ? appById(pin.itemId) : H.state.scenes.find(scene => scene.id === pin.itemId); }
  function appIcon(app, size = 36) {
    const source = hostState?.appIcons?.[app.id];
    // Only the host's PNG data is admitted. Paths/URLs from app records are never loaded.
    return source && /^data:image\/png;base64,[A-Za-z0-9+/=]+$/.test(source)
      ? `<img class="launcher-real-icon" src="${e(source)}" width="${size}" height="${size}" alt="">`
      : icon('app', size);
  }
  function isPending(id) { return appLaunches.get(id)?.status === 'pending'; }
  function syncSceneMetadata(storedValue) {
    if (hostState?.settings?.scenes) return false;
    if (surface === 'workspace') return false;
    try {
      const cached = JSON.parse(storedValue === undefined ? window.localStorage?.getItem(prototypeStorageKey) : storedValue);
      if (cached?.version !== 7 || !Array.isArray(cached.scenes)) return false;
      const seen = new Set();
      // Scene demos share their identity/display metadata only. Native applications,
      // preferences, resource data and scene execution actions never come from this cache.
      H.state.scenes = cached.scenes.filter(scene => {
        if (!scene || typeof scene.id !== 'string' || !scene.id || typeof scene.name !== 'string' || seen.has(scene.id)) return false;
        seen.add(scene.id);
        return true;
      }).map(scene => ({ id: scene.id, name: scene.name, icon: typeof scene.icon === 'string' ? scene.icon : 'scenes', pinned: !!scene.pinned, actions: [] }));
      return true;
    } catch { return false; }
  }
  function applyHostState(next, render = true) {
    if (!next || typeof next !== 'object' || !next.settings || typeof next.settings !== 'object') return;
    hostState = { ...hostState, ...next };
    if (next.executions) window.DesktopHubScenes?.acceptExecutions(next.executions);
    if (Array.isArray(next.settings.scenes)) H.state.scenes = next.settings.scenes.map(scene => ({ ...scene,
      pinned: next.settings.launcherPins?.some(pin => pin.enabled && pin.itemType === 'scene' && pin.itemId === scene.id) }));
    syncSceneMetadata();
    Object.assign(H.state.settings, visualSettings(next.settings), pendingVisualSettings || activeSaveSettings || {});
    lastSettings = JSON.stringify(visualSettings(next.settings));
    H.state.onboardingComplete = true;
    H.applyTheme();
    if (render) H.render();
    if (hostState.settingsWarning && !warningShown) {
      warningShown = true;
      H.toast(hostState.settingsWarning, 'warning');
    }
  }
  H.applyTheme = function () {
    originalApplyTheme();
    if (H.state.settings.theme === 'system' && ['dark', 'light'].includes(hostState?.effectiveTheme)) {
      document.documentElement.dataset.theme = hostState.effectiveTheme;
    }
  };
  async function request(type, payload = {}, options = {}) {
    try { return await bridge.request(type, payload, options); }
    catch (error) {
      H.toast(error.message, 'error');
      throw error;
    }
  }
  function route(type, payload) {
    // Event handlers that have no result must still consume rejected promises.
    void request(type, payload).catch(() => {});
  }
  H.setSurface = function (next) {
    if (next === 'workspace') route('window.openWorkspace', { page: 'home' });
    else if (next === 'dock') route('window.openDock', {});
    else if (surface === 'workspace') route('window.closeWorkspace', {});
    else if (surface === 'dock') route('window.closeDock', {});
  };
  H.navigate = function (page) {
    if (surface !== 'workspace') {
      route('window.openWorkspace', { page: pages.has(page) ? page : 'scenes' });
      return;
    }
    originalNavigate(page);
  };
  H.openOnboarding = () => H.toast('在启动台添加常用应用，文件资源导入将在后续阶段接入。');

  async function flushSettings() {
    if (!hostState || saveInFlight) return;
    const settings = pendingVisualSettings || visualSettings(H.state.settings);
    pendingVisualSettings = null;
    if (JSON.stringify(settings) === lastSettings) return;
    saveInFlight = true;
    activeSaveSettings = settings;
    try {
      const result = await bridge.request('settings.save', { settings });
      if (result?.settings) applyHostState(result);
      else if (result?.theme) applyHostState({ settings: { ...hostState.settings, ...result } });
      else lastSettings = JSON.stringify(settings);
    } catch (error) {
      Object.assign(H.state.settings, visualSettings(hostState.settings), pendingVisualSettings || {});
      H.render();
      H.toast(`设置未保存：${error.message}`, 'error');
    } finally {
      saveInFlight = false;
      activeSaveSettings = null;
      if (pendingVisualSettings && JSON.stringify(pendingVisualSettings) !== lastSettings) {
        clearTimeout(saveTimer);
        saveTimer = setTimeout(flushSettings, 120);
      }
    }
  }
  H.save = function () {
    // Original prototype records remain isolated demo data. Native JSON is authoritative
    // for native preferences and the application library and overrides the cache on startup.
    // Compact surfaces only hold scene display metadata, so they must never write the
    // prototype cache back. Dock density uses its explicit native settings request.
    if (surface !== 'workspace') return;
    for (const key of ['launchTarget', 'appEntries', 'launcherPins']) delete H.state.settings[key];
    originalSave();
    if (hostState) pendingVisualSettings = visualSettings(H.state.settings);
    clearTimeout(saveTimer);
    saveTimer = setTimeout(flushSettings, 100);
  };

  function statusMarkup() {
    return `<p class="native-launch-status" data-status="${e(launch.status)}" role="status" aria-live="polite">${e(launch.message)}${launch.code ? `<span class="native-launch-code">${e(launch.code)}</span>` : ''}</p>`;
  }
  function appTile(app, index, count) {
    const state = appLaunches.get(app.id);
    return `<div class="launcher-tile" data-app-id="${e(app.id)}"><button class="launcher-app" data-action="app-launch" data-id="${e(app.id)}" data-status="${e(state?.status || 'idle')}" aria-label="启动 ${e(app.name)}" title="${e(state?.message || app.name)}" ${isPending(app.id) || !hostState ? 'disabled' : ''}><span class="launcher-icon">${appIcon(app)}</span><strong>${e(app.name)}</strong>${isPending(app.id) ? '<span class="launcher-app-state">启动中…</span>' : ''}</button>${managingApps ? `<div class="launcher-tile-tools"><button class="icon-button small" data-action="app-move" data-id="${e(app.id)}" data-direction="-1" aria-label="向前移动 ${e(app.name)}" ${index === 0 || appQuery || mutationInFlight ? 'disabled' : ''}>${icon('arrow-left', 14)}</button><button class="icon-button small" data-action="app-edit" data-id="${e(app.id)}" aria-label="编辑 ${e(app.name)}">${icon('edit', 15)}</button><button class="icon-button small" data-action="app-move" data-id="${e(app.id)}" data-direction="1" aria-label="向后移动 ${e(app.name)}" ${index === count - 1 || appQuery || mutationInFlight ? 'disabled' : ''}>${icon('arrow-right', 14)}</button></div>` : ''}</div>`;
  }
  function launcherMarkup() {
    const entries = apps();
    const query = appQuery.trim().toLocaleLowerCase();
    const visible = entries.filter(app => !query || app.name.toLocaleLowerCase().includes(query) || app.target.toLocaleLowerCase().includes(query));
    return `<section class="application-launcher" aria-labelledby="launcher-title"><div class="launcher-heading"><div><h2 id="launcher-title">我的应用 <span>${entries.length}</span></h2><p>常用软件，点击即启动。</p></div><div class="toolbar"><button class="button quiet" data-action="launcher-manage" data-surface="mini">${icon('pin', 16)} 固定入口</button><button class="button quiet" data-action="apps-manage" aria-pressed="${managingApps}">${icon('edit', 16)} ${managingApps ? '完成管理' : '管理应用'}</button>${button('app-add', `${icon('plus', 17)} ${adding ? '等待选择…' : '添加应用'}`, true, !hostState || adding || mutationInFlight)}</div></div>${entries.length ? `<label class="launcher-search">${icon('search', 17)}<input id="app-query" type="search" data-change="apps-search" value="${e(appQuery)}" placeholder="查找应用" aria-label="查找应用"><span>${visible.length} 个应用</span></label><div class="launcher-grid">${visible.map((app, index) => appTile(app, index, visible.length)).join('')}</div>${!visible.length ? `<div class="launcher-empty"><p>没有找到“${e(appQuery)}”</p><button class="text-button" data-action="apps-clear-search">清除搜索</button></div>` : ''}` : `<div class="launcher-empty"><span class="launcher-empty-icon">${icon('app', 32)}</span><h3>把常用软件放在这里</h3><p>选择电脑上的 EXE 或 Windows 快捷方式，每个应用会独立保存。</p>${button('app-add', `${icon('plus', 16)} 添加第一个应用`, false, !hostState || adding || mutationInFlight)}</div>`}${managingApps && entries.length ? `<p class="launcher-hint">${appQuery ? '清除搜索后可调整应用顺序。' : '用图标下方的箭头排序；编辑中可设置启动参数与固定位置。'}</p>` : ''}${statusMarkup()}</section>`;
  }
  function dockLaunch() {
    const entries = pins('dock').filter(pin => pin.itemType === 'app');
    const size = ['compact', 'normal', 'large'].includes(H.state.settings.dockIconSize) ? H.state.settings.dockIconSize : 'normal';
    return `<section class="native-dock-launch" aria-label="常用应用"><div class="dock-section-title">常用应用 <button class="text-button" data-action="native-open-launch" aria-label="在工作空间管理应用">管理应用 ${icon('edit', 13)}</button></div><div class="dock-app-grid icon-size-${size}">${entries.map(pin => {
      const app = pinItem(pin);
      return app ? `<button class="dock-app" data-action="app-launch" data-id="${e(app.id)}" data-status="${e(appLaunches.get(app.id)?.status || 'idle')}" title="${e(appLaunches.get(app.id)?.message || app.name)}" aria-label="启动 ${e(app.name)}" ${isPending(app.id) ? 'disabled' : ''}><span>${appIcon(app, 30)}</span><small>${e(app.name)}</small></button>` : `<button class="dock-app launcher-missing" data-action="launcher-manage" data-surface="dock" title="固定的应用已移除，请重新选择">${icon('alert', 25)}<small>入口失效</small></button>`;
    }).join('')}</div>${!entries.length ? '<button class="text-button dock-add-app" data-action="native-open-launch">打开应用库 ' + icon('plus', 15) + '</button>' : ''}${statusMarkup()}<div class="dock-density"><label for="dock-icon-size">图标大小</label><select id="dock-icon-size" data-change="dock-icon-size" aria-label="Dock 应用图标大小">${[['compact', '紧凑'], ['normal', '标准'], ['large', '较大']].map(([value, label]) => `<option value="${value}" ${size === value ? 'selected' : ''}>${label}</option>`).join('')}</select></div></section>`;
  }
  H.pages.home = function () {
    const previous = originalHome?.() || '<div class="launchpad-grid"></div>';
    return previous.replace('<div class="launchpad-grid">', `${launcherMarkup()}<div class="quick-heading launcher-modules-heading"><h2>工作空间</h2></div><div class="launchpad-grid">`);
  };

  function showAppEditor(app, draft = app) {
    if (surface !== 'workspace' || !app) return;
    const miniFull = pins('mini').filter(pin => !(pin.itemType === 'app' && pin.itemId === app.id)).length >= 4;
    H.showModal('编辑应用', `<div class="app-editor-identity"><span class="launcher-icon">${appIcon(app, 32)}</span><div><strong>${e(app.name)}</strong><p>每个应用单独保存与启动。</p></div></div><div class="stack"><label class="field"><span>显示名称</span><input id="app-name" type="text" value="${e(draft.name)}" maxlength="120" required autofocus></label><div class="field"><span>程序或快捷方式</span><div class="app-target-row"><p title="${e(app.target)}">${e(app.target)}</p><button class="button small" data-action="app-replace-target" data-id="${e(app.id)}">${icon('folder', 15)} 更换文件</button></div></div><label class="field"><span>启动参数（可选）</span><input id="app-arguments" type="text" value="${e(draft.arguments || '')}" maxlength="4096" placeholder="例如 --new-window"><small>参数传给所选程序，由 Desktop Hub 校验后启动。</small></label><label class="field"><span>工作目录（可选）</span><input id="app-working-directory" type="text" value="${e(draft.workingDirectory || '')}" maxlength="1024" placeholder="留空使用程序默认目录"></label><div class="app-pin-options"><label class="check-row"><input id="app-pin-dock" type="checkbox" ${draft.pinnedToDock ? 'checked' : ''}><span>固定到 Dock</span></label><label class="check-row"><input id="app-pin-mini" type="checkbox" ${draft.pinnedToMini ? 'checked' : ''} ${miniFull && !draft.pinnedToMini ? 'disabled' : ''}><span>固定到 Mini ${miniFull && !draft.pinnedToMini ? '（已有 4 个入口）' : ''}</span></label></div><p class="page-note">固定只添加启动入口。各位置的显示顺序可在“固定入口”中调整。</p><p id="app-edit-error" class="form-error" role="alert"></p></div>`, `<button class="button danger app-remove-button" data-action="app-remove" data-id="${e(app.id)}">${icon('trash', 16)} 从应用库移除</button><button class="button" data-action="close-modal">取消</button><button class="button primary" data-action="app-save" data-id="${e(app.id)}">保存应用</button>`, { wide: false });
  }
  function readAppDraft(app) {
    return { ...app, name: document.querySelector('#app-name')?.value?.trim() || '', arguments: document.querySelector('#app-arguments')?.value || '', workingDirectory: document.querySelector('#app-working-directory')?.value?.trim() || '', pinnedToDock: !!document.querySelector('#app-pin-dock')?.checked, pinnedToMini: !!document.querySelector('#app-pin-mini')?.checked };
  }
  function modalError(message) {
    const element = document.querySelector('#app-edit-error,#launcher-edit-error');
    if (element) element.textContent = message;
    else H.toast(message, 'error');
  }
  function setModalBusy(busy) {
    if (H.ui.modal) H.ui.modal.locked = busy;
    document.querySelectorAll('.modal button').forEach(control => { control.disabled = busy; });
  }
  async function mutate(type, payload, success) {
    if (mutationInFlight || !hostState) return false;
    mutationInFlight = true;
    setModalBusy(true);
    try {
      const result = await bridge.request(type, payload);
      if (!result?.settings) throw new window.DesktopHubNative.NativeBridgeError('INVALID_RESPONSE', '应用配置未返回，请重试。');
      applyHostState(result);
      success?.(result);
      return true;
    } catch (error) {
      modalError(error.message);
      return false;
    } finally {
      mutationInFlight = false;
      setModalBusy(false);
      H.render();
    }
  }
  function pinChoices() {
    return [ ...apps().map(app => ({ itemType: 'app', itemId: app.id, name: app.name, icon: appIcon(app, 22) })), ...H.state.scenes.map(scene => ({ itemType: 'scene', itemId: scene.id, name: `${scene.name} · 场景`, icon: icon(scene.icon, 22) })) ];
  }
  function showPinManager(where = 'mini', preserve = false) {
    if (surface !== 'workspace') { H.navigate('home'); return; }
    if (!preserve || !pinDraft || pinDraft.surface !== where) pinDraft = { surface: where, pins: pins(where).map(pin => ({ itemType: pin.itemType, itemId: pin.itemId, enabled: true })) };
    const choices = pinChoices();
    const selected = pinDraft.pins;
    const available = choices.filter(choice => !selected.some(pin => pin.itemType === choice.itemType && pin.itemId === choice.itemId));
    const full = where === 'mini' && selected.length >= 4;
    H.showModal('固定启动入口', `<div class="launcher-pin-tabs segmented"><button data-action="launcher-manage" data-surface="mini" class="${where === 'mini' ? 'active' : ''}">Mini</button><button data-action="launcher-manage" data-surface="dock" class="${where === 'dock' ? 'active' : ''}">Dock</button></div><p class="launcher-pin-description">${where === 'mini' ? '选择最多 4 个常用应用或场景，按你习惯的顺序排列。' : '常用应用和快捷场景各自显示，排序只影响 Dock。'}</p><h3 class="launcher-pin-group-heading">已固定 <span>${selected.length}${where === 'mini' ? ' / 4' : ''}</span></h3><div class="launcher-pin-list">${selected.map((pin, index) => {
      const choice = choices.find(item => item.itemType === pin.itemType && item.itemId === pin.itemId);
      return `<div class="launcher-pin-row"><span class="launcher-pin-symbol">${choice?.icon || icon('alert', 22)}</span><span class="grow">${e(choice?.name || '入口失效')}<small>${pin.itemType === 'app' ? '应用' : '原型场景'}</small></span><button class="icon-button small" data-action="launcher-pin-move" data-index="${index}" data-direction="-1" aria-label="向前移动 ${e(choice?.name || '入口')}" ${index === 0 ? 'disabled' : ''}>${icon('arrow-left', 15)}</button><button class="icon-button small" data-action="launcher-pin-move" data-index="${index}" data-direction="1" aria-label="向后移动 ${e(choice?.name || '入口')}" ${index === selected.length - 1 ? 'disabled' : ''}>${icon('arrow-right', 15)}</button><button class="icon-button small" data-action="launcher-pin-remove" data-index="${index}" aria-label="取消固定 ${e(choice?.name || '入口')}">${icon('close', 16)}</button></div>`;
    }).join('') || '<p class="launcher-pin-empty muted">还没有固定入口。</p>'}</div><h3 class="launcher-pin-group-heading">可添加</h3><div class="launcher-pin-list">${available.map(choice => `<button class="launcher-pin-choice" data-action="launcher-pin-add" data-item-type="${choice.itemType}" data-id="${e(choice.itemId)}" ${full ? 'disabled' : ''}><span class="launcher-pin-symbol">${choice.icon}</span><span class="grow">${e(choice.name)}</span>${icon('plus', 17)}</button>`).join('') || '<p class="launcher-pin-empty muted">已固定全部入口。可在启动台添加更多应用。</p>'}</div><p id="launcher-edit-error" class="form-error" role="alert"></p>`, '<button class="button" data-action="close-modal">取消</button><button class="button primary" data-action="launcher-pins-save">保存入口</button>');
  }
  function measureSurface() {
    if (surface === 'workspace' || !hostState) return;
    const panel = document.querySelector(surface === 'mini' ? '.mini-bar' : '.dock-panel');
    if (!panel) return;
    const bounds = panel.getBoundingClientRect();
    const width = Math.ceil(bounds.width) + 8;
    const height = Math.ceil(Math.max(bounds.height, panel.scrollHeight)) + 8;
    const key = `${width}:${height}`;
    if (key === lastMeasurement) return;
    lastMeasurement = key;
    void bridge.request('window.resizeSurface', { width, height }).catch(error => {
      lastMeasurement = '';
      console.warn('Surface resize:', error.code);
    });
  }
  function watchSurface() {
    measurementObserver?.disconnect();
    if (surface === 'workspace') return;
    const panel = document.querySelector(surface === 'mini' ? '.mini-bar' : '.dock-panel');
    if (!panel) return;
    if (typeof ResizeObserver === 'function') {
      measurementObserver = new ResizeObserver(() => {
        clearTimeout(measurementTimer);
        measurementTimer = setTimeout(measureSurface, 60);
      });
      measurementObserver.observe(panel);
    }
    clearTimeout(measurementTimer);
    measurementTimer = setTimeout(measureSurface, 60);
  }
  function annotatePrototype() {
    document.querySelectorAll('[data-action="run-scene"]').forEach(el => {
      el.title = '执行组合场景';
      if (el.classList.contains('mini-item')) {
        const name = H.state.scenes.find(item => item.id === el.dataset.id)?.name || '场景';
        el.setAttribute('aria-label', `${name} · 执行场景`);
        el.dataset.tooltip = `${name} · 执行场景`;
      }
      if (el.classList.contains('home-scene')) {
        const detail = el.querySelector('small');
        if (detail) detail.textContent = `${H.state.scenes.find(scene => scene.id === el.dataset.id)?.actions?.length || 0} 个动作`;
      }
    });
    document.querySelectorAll('.mini-item[data-tooltip]').forEach(el => { el.title = el.dataset.tooltip; });
    if (surface !== 'workspace') return;
    const footer = document.querySelector('.workspace-footer > span');
    if (footer) footer.innerHTML = '<i></i> 本地应用与场景 <span class="footer-separator">·</span> 资源页为原型演示';
    const tip = document.querySelector('.home-tip > span');
    if (tip) tip.innerHTML = 'Mini / Dock 打开工作空间 <span class="tip-divider">/</span> <kbd>Esc</kbd> 回到桌面';
    if (H.ui.page === 'scenes') {
      const note = document.querySelector('.page-note');
      if (note) note.textContent = '场景组合多个动作。常用软件仍可在启动台独立启动。';
      document.querySelectorAll('[data-action="scene-pin"]').forEach(control => {
        const pinned = pins('mini').some(pin => pin.itemType === 'scene' && pin.itemId === control.dataset.id);
        const label = pinned ? '取消固定到 Mini' : '固定到 Mini';
        control.title = label;
        control.setAttribute('aria-label', label);
        control.setAttribute('aria-pressed', String(pinned));
      });
      const count = document.querySelector('.scene-toolbar > .subtitle');
      if (count) count.textContent = `${H.state.scenes.length} 个场景 · Mini 已固定 ${pins('mini').length}/4 个入口`;
    } else if (H.ui.page !== 'settings' && H.ui.page !== 'home' && H.ui.page !== 'editor') {
      const heading = document.querySelector('.page-heading');
      if (heading && !heading.querySelector('.native-prototype-badge')) {
        heading.insertAdjacentHTML('beforeend', '<span class="badge native-prototype-badge">原型演示</span>');
      }
    }
    if (H.ui.page === 'settings') {
      const badge = document.querySelector('.page-heading > .badge');
      if (badge) badge.textContent = '外观与窗口偏好保存到本地';
      for (const control of document.querySelectorAll('[data-setting]')) {
        if (!editableSettings.includes(control.dataset.setting)) {
          control.disabled = true;
          control.title = '本轮未接入；保留原型展示';
        }
      }
      const unsupportedActions = [
        'settings-save-shortcuts', 'settings-default-shortcuts', 'settings-add-source',
        'settings-confirm-source', 'settings-remove-source', 'settings-add-exclusion',
        'settings-remove-exclusion', 'settings-restore-policy', 'settings-restore-icons',
        'settings-replay-onboarding', 'settings-reset'
      ];
      for (const action of unsupportedActions) {
        document.querySelectorAll(`[data-action="${action}"]`).forEach(el => {
          el.disabled = true;
          el.title = '本轮未接入；保留原型展示';
        });
      }
      document.querySelectorAll('[data-change="source-enabled"],[data-change="source-recursive"]').forEach(el => { el.disabled = true; });
      document.querySelectorAll('.settings-note').forEach(el => {
        if (el.textContent.includes('真实桌面层级')) el.innerHTML = `${icon('alert', 16)}<span>位置、尺寸与悬停偏好保存到本地。Mini 和 Dock 使用独立 Windows 窗口；壁纸嵌入与排除程序识别尚未接入。</span>`;
      });
      document.querySelectorAll('.setting-row p').forEach(el => {
        if (el.textContent.includes('模拟 Edge')) el.textContent = '允许在普通前台程序上方唤出 Dock。';
      });
    }
  }
  H.render = function () {
    H.ui.surface = surface;
    originalRender();
    document.querySelector('.desktop-stage')?.remove();
    document.querySelector('.demo-bar')?.remove();
    document.querySelector('.edge-trigger')?.remove();
    if (surface === 'workspace') {
      const actions = document.querySelector('.header-actions');
      actions?.insertAdjacentHTML('beforeend', `<button class="icon-button" data-action="native-exit" aria-label="退出 Desktop Hub" title="退出 Desktop Hub">${icon('logout', 19)}</button>`);
      const heading = document.querySelector('.home-content .quick-heading:not(.launcher-modules-heading) h2');
      if (heading) heading.textContent = '快捷场景';
    } else if (surface === 'dock') {
      const heading = document.querySelector('.dock-section-title');
      heading?.insertAdjacentHTML('beforebegin', dockLaunch());
      if (heading) heading.innerHTML = '快捷场景 <span>组合启动</span>';
      const sceneArea = document.querySelector('.dock-scenes');
      if (sceneArea) sceneArea.innerHTML = pins('dock').filter(pin => pin.itemType === 'scene').map(pin => {
        const scene = pinItem(pin);
        return scene ? `<button class="dock-scene" data-action="run-scene" data-id="${e(scene.id)}" ${scene.enabled === false ? 'disabled' : ''}><span class="scene-icon">${window.DesktopHubScenes?.sceneSymbol(scene, 23) || icon(scene.icon, 23)}</span><span><strong>${e(scene.name)}</strong><small>${scene.actions?.length || 0} 个动作${scene.enabled === false ? ' · 已停用' : ''}</small></span>${icon('play', 17)}</button>` : '<p class="muted launcher-missing">固定的场景已移除，请重新选择。</p>';
      }).join('') || '<p class="muted launcher-pin-empty">还没有固定场景。</p>';
      const shortcut = document.querySelector('.dock-workspace small');
      if (shortcut) shortcut.textContent = 'Launchpad';
    } else {
      const bar = document.querySelector('.mini-bar');
      if (bar) {
        const entries = pins('mini').slice(0, 4);
        bar.setAttribute('aria-label', 'Mini 固定应用与场景');
        bar.classList.toggle('has-launch-error', launch.status === 'error');
        bar.innerHTML = `<button class="mini-brand" data-action="open-workspace" aria-label="打开工作空间" title="打开工作空间">${icon('grid', 22)}</button><span class="mini-separator"></span>${entries.map(pin => {
          const item = pinItem(pin);
          if (!item) return `<button class="mini-item launcher-pin launcher-missing" data-action="native-open-launch" title="固定入口已移除，请重新选择" aria-label="修复失效入口">${icon('alert', 22)}</button>`;
          const app = pin.itemType === 'app';
          const label = app ? `启动 ${item.name}` : `${item.name} · 执行场景`;
          return `<button class="mini-item launcher-pin" data-action="${app ? 'app-launch' : 'run-scene'}" data-id="${e(item.id)}" data-item-type="${pin.itemType}" data-status="${e(appLaunches.get(item.id)?.status || 'idle')}" aria-label="${e(label)}" title="${e(app ? appLaunches.get(item.id)?.message || label : label)}" ${app && isPending(item.id) ? 'disabled' : ''}>${app ? appIcon(item, 22) : window.DesktopHubScenes?.sceneSymbol(item, 22) || icon(item.icon, 22)}${H.state.settings.miniMode === 'labels' ? `<span class="mini-item-label">${e(item.name)}</span>` : ''}</button>`;
        }).join('') || `<button class="mini-item launcher-pin" data-action="native-open-launch" aria-label="添加入口" title="添加入口">${icon('plus', 22)}${H.state.settings.miniMode === 'labels' ? '<span class="mini-item-label">添加入口</span>' : ''}</button>`}<span class="mini-separator"></span><button class="mini-item" data-action="open-dock" aria-label="展开 Dock" title="展开 Dock">${icon('dock', 21)}</button>${launch.status === 'error' ? `<div class="mini-launch-error" role="alert"><span>${e(launch.message)}</span><button class="icon-button small" data-action="launch-dismiss" aria-label="关闭启动反馈">${icon('close', 14)}</button></div>` : `<span class="sr-only" role="status" aria-live="polite">${e(launch.message)}</span>`}`;
      }
    }
    annotatePrototype();
    watchSurface();
  };

  Object.assign(H.actions, {
    'native-open-launch': () => H.navigate('home'),
    'native-exit': () => route('app.exit', {}),
    'apps-manage': () => { managingApps = !managingApps; H.render(); },
    'apps-search': el => { appQuery = el.value; H.render(); },
    'apps-clear-search': () => { appQuery = ''; H.render(); document.querySelector('#app-query')?.focus(); },
    'app-add': async () => {
      if (surface !== 'workspace') { H.navigate('home'); return; }
      if (!hostState || adding || mutationInFlight) return;
      adding = true;
      launch = { status: 'pending', message: '等待选择应用文件…', code: '', appId: '' };
      H.render();
      try {
        const result = await bridge.request('apps.add', {}, { timeoutMs: 120000 });
        if (result?.cancelled) {
          launch = { status: 'idle', message: '已取消添加，已有应用仍然保留。', code: '', appId: '' };
        } else if (result?.app?.id && result.settings) {
          applyHostState(result, false);
          appQuery = '';
          launch = { status: 'idle', message: `已添加 ${result.app.name}。`, code: '', appId: result.app.id };
        } else throw new window.DesktopHubNative.NativeBridgeError('INVALID_RESPONSE', '未收到有效应用信息，请重新选择。');
      } catch (error) {
        launch = { status: 'error', message: `添加失败：${error.message}`, code: error.code || 'ADD_FAILED', appId: '' };
      } finally {
        adding = false;
      }
      H.render();
    },
    'app-launch': async el => {
      const selected = appById(el.dataset.id);
      if (!selected || !bridge.available || !hostState || isPending(selected.id)) return;
      launch = { status: 'pending', message: `正在启动 ${selected.name}…`, code: '', appId: selected.id };
      appLaunches.set(selected.id, launch);
      H.render();
      let resultState;
      try {
        const result = await bridge.request('apps.launch', { appId: selected.id });
        if (result?.success !== true) {
          resultState = { status: 'error', message: result?.message || '程序启动失败，请检查所选文件。', code: result?.code || 'LAUNCH_FAILED', appId: selected.id };
        } else {
          resultState = { status: 'success', message: result.message || `已向 Windows 提交 ${selected.name} 的启动请求。`, code: '', appId: selected.id };
        }
      } catch (error) {
        resultState = { status: 'error', message: `启动失败：${error.message}`, code: error.code || 'LAUNCH_FAILED', appId: selected.id };
      }
      appLaunches.set(selected.id, resultState);
      launch = resultState;
      H.render();
    },
    'launch-dismiss': () => { launch = { status: 'idle', message: '', code: '', appId: '' }; H.render(); },
    'app-edit': el => showAppEditor(appById(el.dataset.id)),
    'app-save': async el => {
      const app = appById(el.dataset.id);
      if (!app) return;
      const draft = readAppDraft(app);
      if (!draft.name) { modalError('请输入应用显示名称。'); document.querySelector('#app-name')?.focus(); return; }
      const { name, arguments: argumentsValue, workingDirectory, pinnedToMini, pinnedToDock } = draft;
      await mutate('apps.update', { appId: app.id, name, arguments: argumentsValue, workingDirectory, pinnedToMini, pinnedToDock }, () => { H.closeModal(); H.toast(`已保存 ${name}。`); });
    },
    'app-replace-target': async el => {
      const app = appById(el.dataset.id);
      if (!app || mutationInFlight) return;
      const draft = readAppDraft(app);
      mutationInFlight = true;
      setModalBusy(true);
      try {
        const result = await bridge.request('apps.replaceTarget', { appId: app.id }, { timeoutMs: 120000 });
        if (!result?.cancelled) {
          if (!result?.app?.id || !result.settings) throw new window.DesktopHubNative.NativeBridgeError('INVALID_RESPONSE', '未收到有效应用文件，请重试。');
          applyHostState(result);
          showAppEditor(appById(app.id), draft);
          H.toast('应用文件已更换，其余编辑内容可继续保存。');
        }
      } catch (error) { modalError(error.message); }
      finally { mutationInFlight = false; setModalBusy(false); }
    },
    'app-remove': el => {
      const app = appById(el.dataset.id);
      if (!app) return;
      const affected = H.state.scenes.filter(scene => scene.actions?.some(action => action.appEntryId === app.id));
      H.showModal('从应用库移除？', `<p>移除“${e(app.name)}”及其 Mini / Dock 固定入口。</p>${affected.length ? `<p class="native-warning">受影响场景：${affected.map(scene => e(scene.name)).join('、')}。场景会保留，对应动作需要重新关联应用。</p>` : ''}<p class="muted">只删除 Desktop Hub 的记录，不会删除文件，也不会卸载电脑上的软件。</p><p id="app-edit-error" class="form-error" role="alert"></p>`, `<button class="button" data-action="app-edit" data-id="${e(app.id)}">返回编辑</button><button class="button danger" data-action="app-remove-confirm" data-id="${e(app.id)}">从应用库移除</button>`);
    },
    'app-remove-confirm': async el => {
      const app = appById(el.dataset.id);
      if (!app) return;
      const affected = H.state.scenes.some(scene => scene.actions?.some(action => action.appEntryId === app.id));
      await mutate('apps.remove', { appId: app.id, ...(affected ? { confirmReferences: true } : {}) }, () => { H.closeModal(); H.toast(`已从应用库移除 ${app.name}，原始软件保留。`); });
    },
    'app-move': async el => {
      const entries = apps();
      const from = entries.findIndex(app => app.id === el.dataset.id);
      const to = from + Number(el.dataset.direction);
      if (appQuery || from < 0 || to < 0 || to >= entries.length) return;
      [entries[from], entries[to]] = [entries[to], entries[from]];
      await mutate('apps.reorder', { appIds: entries.map(app => app.id) });
    },
    'dock-icon-size': async el => {
      if (!['compact', 'normal', 'large'].includes(el.value)) return;
      if (surface === 'dock') {
        await mutate('settings.save', { settings: { dockIconSize: el.value } });
        return;
      }
      H.state.settings.dockIconSize = el.value;
      H.save(); H.render();
    },
    'launcher-manage': el => showPinManager(el.dataset.surface === 'dock' ? 'dock' : 'mini'),
    'launcher-pin-add': el => {
      if (!pinDraft || pinDraft.surface === 'mini' && pinDraft.pins.length >= 4) return;
      const choice = pinChoices().find(item => item.itemType === el.dataset.itemType && item.itemId === el.dataset.id);
      if (!choice || pinDraft.pins.some(pin => pin.itemType === choice.itemType && pin.itemId === choice.itemId)) return;
      pinDraft.pins.push({ itemType: choice.itemType, itemId: choice.itemId, enabled: true });
      showPinManager(pinDraft.surface, true);
    },
    'launcher-pin-remove': el => { if (!pinDraft) return; pinDraft.pins.splice(Number(el.dataset.index), 1); showPinManager(pinDraft.surface, true); },
    'launcher-pin-move': el => {
      if (!pinDraft) return;
      const from = Number(el.dataset.index), to = from + Number(el.dataset.direction);
      if (from < 0 || to < 0 || from >= pinDraft.pins.length || to >= pinDraft.pins.length) return;
      [pinDraft.pins[from], pinDraft.pins[to]] = [pinDraft.pins[to], pinDraft.pins[from]];
      showPinManager(pinDraft.surface, true);
    },
    'launcher-pins-save': async () => {
      if (!pinDraft) return;
      await mutate('launcher.setPins', { surface: pinDraft.surface, pins: pinDraft.pins }, () => { H.closeModal(); H.toast('固定入口已保存。'); pinDraft = null; });
    },
    'scene-pin': async el => {
      const scene = H.state.scenes.find(item => item.id === el.dataset.id);
      if (!scene) return;
      const entries = pins('mini').map(pin => ({ itemType: pin.itemType, itemId: pin.itemId, enabled: true }));
      const selected = entries.findIndex(pin => pin.itemType === 'scene' && pin.itemId === scene.id);
      if (selected >= 0) entries.splice(selected, 1);
      else if (entries.length >= 4) { H.toast('Mini 最多显示 4 个入口，请先取消一个固定项。', 'warning'); return; }
      else entries.push({ itemType: 'scene', itemId: scene.id, enabled: true });
      await mutate('launcher.setPins', { surface: 'mini', pins: entries }, () => H.toast(selected >= 0 ? '已取消 Mini 固定。' : '已固定到 Mini。'));
    },
    'run-scene': () => {
      if (surface !== 'workspace') H.navigate('scenes');
      else {
        H.toast('此场景为原型演示，多动作执行将在后续阶段接入。');
        if (H.ui.page !== 'scenes') H.navigate('scenes');
      }
    }
  });

  function handleEscape() {
    if (H.ui.modal) {
      if (!H.ui.modal.locked) H.closeModal();
      return true;
    }
    return false;
  }
  window.DesktopHubDesktop = Object.freeze({ handleEscape, applyHostState, getState: () => ({ surface, hostState, launch }) });
  document.addEventListener('keydown', event => {
    if (event.key !== 'Escape') return;
    event.preventDefault();
    event.stopImmediatePropagation();
    if (handleEscape()) return;
    if (surface === 'workspace') route('window.closeWorkspace', {});
    else if (surface === 'dock') route('window.closeDock', {});
  }, true);
  document.addEventListener('pointerdown', event => {
    const mini = surface === 'mini' && event.button === 2 && event.target.closest('.mini-bar');
    const dock = surface === 'dock' && event.button === 0 && event.target.closest('.dock-heading > span');
    const workspace = surface === 'workspace' && event.button === 0 && event.target.closest('.workspace-header') && !event.target.closest('button,input,select,a');
    if (!mini && !dock && !workspace) return;
    event.preventDefault();
    route('window.drag', {});
  });
  document.addEventListener('contextmenu', event => {
    if (surface === 'mini') event.preventDefault();
  });
  window.addEventListener?.('storage', event => {
    if (event.key === prototypeStorageKey && syncSceneMetadata(event.newValue)) H.render();
  });
  bridge.on('state.changed', state => applyHostState(state));
  bridge.on('window.navigate', ({ page }) => {
    if (surface !== 'workspace' || !pages.has(page)) return;
    if (document.readyState === 'loading') requestedPage = page;
    else H.navigate(page);
  });
  document.addEventListener('DOMContentLoaded', async () => {
    if (!bridge.available) {
      document.body.insertAdjacentHTML('beforeend', '<p class="native-offline-warning" role="alert">原生宿主未连接。请运行 Desktop Hub Windows 应用。</p>');
      return;
    }
    try {
      const state = await bridge.request('app.getState', {});
      applyHostState(state);
      if (requestedPage) { H.navigate(requestedPage); requestedPage = null; }
    } catch (error) {
      launch = { status: 'error', message: `应用初始化失败：${error.message}`, code: error.code || 'INITIALIZE_FAILED' };
      H.render();
      H.toast(launch.message, 'error');
    }
  });
}());
