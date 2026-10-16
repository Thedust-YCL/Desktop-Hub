/* M2.3: native system settings. Reuses the accepted settings layout and controls. */
(function () {
  'use strict';
  const H = window.Hub, desktop = window.DesktopHubDesktop, bridge = window.DesktopHubNative.bridge;
  if (desktop.getState().surface !== 'workspace') return;
  const e = value => H.escape(String(value ?? '')), icon = (name, size = 18) => H.icon(name, size);
  const recoveryPath = '桌面空白处右键 → 查看 → 显示桌面图标（菜单文字随 Windows 语言变化）';
  const originalSettings = H.pages.settings, previousSection = H.actions['settings-section'];
  let section = 'appearance', busy = false, hidePolicy = '', policyOnly = false, feedback = '', feedbackError = false;
  let shortcutDraft = null, blocklistDraft = null;
  const state = () => desktop.getState().hostState;
  const settings = () => state()?.settings || {};
  const integration = () => state()?.systemIntegration || {};
  const btn = (action, label, attrs = '', kind = 'quiet') => `<button class="button ${kind}" data-action="${action}" ${busy ? 'disabled' : ''} ${attrs}>${label}</button>`;
  const row = (title, description, control) => `<div class="setting-row"><div><strong>${title}</strong><p>${description}</p></div><div class="setting-control">${control}</div></div>`;
  const note = text => `<div class="settings-note">${icon('alert', 16)}<span>${text}</span></div>`;
  const panel = (title, body) => `<section class="panel settings-section"><h2>${title}</h2>${body}</section>`;
  const opacitySlider = (key, label, value, action) => `<label class="opacity-control"><input type="range" min="0" max="100" step="1" value="${Math.round(value*100)}" aria-label="${label}" aria-valuetext="${Math.round(value*100)}%" data-setting="${key}" data-change="${action}" ${busy || !state() ? 'disabled' : ''}><output>${Math.round(value*100)}%</output></label>`;
  const workspaceOpacityRow = () => row('Workspace 不透明度', '调整整个工作空间。设为 0% 后，可从托盘打开设置恢复。', opacitySlider('workspaceOpacity', 'Workspace 不透明度', settings().workspaceOpacity ?? 1, 'sys-workspace-opacity-preview'));
  const visualValue = key => H.state.settings[key] ?? settings()[key];
  const toggle = (key, label) => {
    const explicit = key === 'miniEnabled', value = explicit ? settings()[key] : visualValue(key);
    return `<button type="button" class="switch ${value ? 'is-on' : ''}" role="switch" aria-checked="${!!value}" aria-label="${label}" data-action="${explicit ? 'sys-toggle-setting' : 'setting-toggle'}" data-setting="${key}" ${busy || !state() ? 'disabled' : ''}><span></span></button>`;
  };
  const select = (key, label, values) => `<select aria-label="${label}" data-change="setting-value" data-setting="${key}">${values.map(([value, labelText]) => `<option value="${value}" ${visualValue(key) === value ? 'selected' : ''}>${labelText}</option>`).join('')}</select>`;
  const message = () => feedback ? `<p class="${feedbackError ? 'form-error' : 'subtitle'}" role="${feedbackError ? 'alert' : 'status'}" aria-live="polite">${e(feedback)}</p>` : '';
  function systemPanel() {
    const icons = integration().desktopIcons, startup = integration().startup;
    const iconKnown = icons?.supported === true && typeof icons.visible === 'boolean';
    const iconStatus = iconKnown ? icons.visible ? '当前显示' : '当前隐藏' : icons?.supported === false ? '操作不受支持 / 当前状态未知' : '当前状态未知';
    const startupKnown = !!startup && typeof startup.enabled === 'boolean' &&
      !['registry_unavailable', 'startup_entry_unsupported'].includes(startup.code);
    const exitPolicy = icons?.exitPolicy === 'restore' ? '完整退出时恢复隐藏前的图标状态' : icons?.exitPolicy === 'keepHidden' ? '完整退出后保持隐藏' : '首次隐藏时必须选择';
    return panel('系统常驻',
      row('性能模式', '快速唤出保留界面；轻量常驻在隐藏 60 秒且没有未处理草稿或操作时释放界面，再次打开时恢复浏览位置。',
        `<select aria-label="性能模式" data-change="sys-performance-mode" ${busy ? 'disabled' : ''}><option value="quick" ${(settings().performanceMode || 'quick') === 'quick' ? 'selected' : ''}>快速唤出</option><option value="lightweight" ${settings().performanceMode === 'lightweight' ? 'selected' : ''}>轻量常驻</option></select>`) +
      row('当前用户开机自启', startupKnown ? `${startup.enabled ? '已启用' : '已关闭'} · 登录后安静常驻，不自动打开 Workspace 或执行场景。` : startup ? '当前状态未知，请重新核对 Windows 启动项。' : '正在读取真实 Windows 启动项。',
        startupKnown ? btn('sys-startup', startup.enabled ? '关闭开机自启' : '开启开机自启', `data-enabled="${!startup.enabled}"`) : '<span class="badge">状态未知</span>') +
      (startup?.needsRepair ? note(`${e(startup.message || '启动项与当前位置不一致。')} ${btn('sys-startup', '按当前位置修复', 'data-enabled="true"')}`) : '') +
      (startup && !startup.success && !startup.needsRepair ? note(e(startup.message)) : '') +
      row('托盘与完整退出', '关闭 Workspace 后继续常驻。托盘可打开设置、显示 Mini、恢复桌面图标或完整退出。', btn('native-exit', '完整退出'))) +
      panel('使用帮助', row('首次使用引导', '重新了解导入、日常使用、桌面图标与开机自启。已有内容和设置会保留。', btn('settings-replay-onboarding', '重新引导'))) +
      panel('桌面图标与恢复',
      row('桌面图标', `${iconStatus}。仅改变图标整体可见性，不会删除、移动或修改桌面文件。`,
        `<div class="toolbar">${btn('sys-icons', '显示桌面图标', `data-visible="true" ${!iconKnown ? 'disabled' : ''}`)}${btn('sys-icons', '隐藏桌面图标', `data-visible="false" ${!iconKnown ? 'disabled' : ''}`)}</div>`) +
      (icons?.conflict ? note(`${e(icons.message || '检测到软件外更改，已保留当前 Windows 状态。')} 自动恢复已暂停，请核对后选择目标状态。`) : icons?.message && !icons.success ? note(e(icons.message)) : '') +
      row('完整退出后的策略', exitPolicy, btn('sys-policy', '更改退出策略', `${!icons?.firstHideAuthorized ? 'disabled' : ''}`)) +
      note(`随时可以用 Windows 恢复，即使 Desktop Hub 未运行：<strong>${recoveryPath}</strong>。`) +
      note('意外退出或 Explorer 重启后会重新核对状态；不能保证所有情况下自动恢复成功。关闭 Workspace 和 Windows 关机 / 注销不等同于完整退出。') +
      `<div class="toolbar">${btn('sys-refresh', '重新核对状态')}</div>`) + message();
  }
  function shortcutsPanel() {
    if (!shortcutDraft) shortcutDraft = { dock: settings().dockHotkey || 'Ctrl+Alt+D', workspace: settings().workspaceHotkey || 'Ctrl+Alt+W' };
    const hotkeys = integration().hotkeys;
    return panel('独立快捷键',
      `<div class="form-grid"><label class="field"><span>展开 Dock</span><input id="sys-dock-hotkey" autocomplete="off" spellcheck="false" maxlength="64" value="${e(shortcutDraft.dock)}" data-change="sys-shortcut" data-key="dock"><small>当前保存：${e(settings().dockHotkey || 'Ctrl+Alt+D')}</small></label><label class="field"><span>唤出 Workspace</span><input id="sys-workspace-hotkey" autocomplete="off" spellcheck="false" maxlength="64" value="${e(shortcutDraft.workspace)}" data-change="sys-shortcut" data-key="workspace"><small>当前保存：${e(settings().workspaceHotkey || 'Ctrl+Alt+W')}</small></label></div>` +
      `<p class="subtitle">Ctrl、Alt、Shift 中至少两个修饰键，加字母、数字或 F1–F24（F12 除外），例如 Ctrl+Alt+W。两个入口必须不同。</p>` +
      `<div class="toolbar">${btn('sys-save-hotkeys', '保存快捷键', '', 'primary')}${btn('sys-default-hotkeys', '恢复默认')}</div>` +
      (hotkeys?.success === false ? note(e(hotkeys.error || '快捷键未注册，请选择其他组合。')) : '') +
      note('Windows 占用或注册失败时保留之前有效的快捷键。屏蔽程序和全屏保护同样适用于快捷键唤出。')) + message();
  }
  function protectionPanel() {
    if (blocklistDraft === null) blocklistDraft = (settings().foregroundBlocklist || []).join('\n');
    return panel('全屏与游戏保护',
      note('全屏程序前台时阻止自动唤出。游戏识别可能误判或漏判，可用屏蔽名单明确保护目标程序。Desktop Hub 自身的 Workspace 不会被当作游戏。') +
      `<label class="field"><span>前台程序屏蔽名单</span><textarea id="sys-blocklist" rows="6" maxlength="12100" data-change="sys-blocklist" spellcheck="false" placeholder="game.exe&#10;another-game.exe">${e(blocklistDraft)}</textarea><small>每行一个 EXE 文件名；无需完整路径。名单只阻止面板唤出，不会关闭或修改这些程序。</small></label><div class="toolbar">${btn('sys-save-blocklist', '保存屏蔽名单', '', 'primary')}</div>`) + message();
  }
  function surfacesPanel() {
    const opacity = settings().miniOpacity ?? 0.9;
    return panel('Mini',
      row('Mini 常驻入口', '显示 2–4 个已固定的应用或场景；Dock / Workspace 打开时暂时收起。', toggle('miniEnabled', '显示 Mini')) +
      row('固定入口', '应用与场景沿用同一份真实启动配置。', btn('launcher-manage', '管理固定入口', 'data-surface="mini"')) +
      row('位置', '纵向靠左或靠右，顶部为横向工具栏。', select('miniPosition', 'Mini 位置', [['left','左侧'],['right','右侧'],['top','顶部']])) +
      row('尺寸', '调整工具栏和点击区域。', select('miniSize', 'Mini 尺寸', [['small','紧凑'],['normal','标准'],['large','宽松']])) +
      row('显示模式', '仅显示图标，或同时显示入口名称。', select('miniMode', 'Mini 显示模式', [['icons','仅图标'],['labels','图标与名称']])) +
      row('不透明度', '从 0% 到 100% 调整 Mini；可用快捷键再次打开工作空间。', opacitySlider('miniOpacity', 'Mini 不透明度', opacity, 'sys-opacity-preview')) +
      `<div class="toolbar">${btn('settings-preview', `${icon('mini')} 查看 Mini`, 'data-surface="mini"')}</div>`) +
      panel('Dock',
      row('位置', '在所选屏幕边缘停留，可展开 Dock。', select('dockPosition', 'Dock 位置', [['left','左侧'],['right','右侧'],['bottom','底部']])) +
      row('尺寸', '快速入口的显示密度。', select('dockSize', 'Dock 尺寸', [['small','紧凑'],['normal','标准'],['large','宽松']])) +
      row('图标密度', '常用应用与快捷场景保留各自区域。', select('dockIconSize', 'Dock 图标密度', [['compact','紧凑'],['normal','标准'],['large','较大']])) +
      row('启动后收起', '独立应用启动请求成功后自动收起 Dock；场景保留进度入口。', `<label class="switch"><input type="checkbox" data-change="sys-auto-close-dock" aria-label="独立应用启动请求成功后自动收起 Dock" ${settings().autoCloseDock ? 'checked' : ''}><span></span></label>`) +
      row('边缘悬停展开', '全屏及屏蔽程序前台时暂停边缘触发。', toggle('hoverEnabled', '边缘悬停展开')) +
      row('悬停延迟', '默认约 500 ms，避免经过边缘时误触。', `<select aria-label="Dock 悬停延迟" data-change="setting-value" data-setting="hoverDelay">${[300,500,800,1000].map(value => `<option value="${value}" ${visualValue('hoverDelay') === value ? 'selected' : ''}>${value} ms</option>`).join('')}</select>`) +
      row('允许在普通软件前台展开', '允许在普通应用上方临时唤出 Dock。', toggle('allowForeground', '允许在普通软件前台展开')) +
      `<div class="toolbar">${btn('settings-preview', `${icon('dock')} 查看 Dock`, 'data-surface="dock"')}</div>`) + message();
  }
  H.pages.settings = function () {
    const old = originalSettings();
    const active = old.match(/data-section="([^\"]+)" aria-current="page"/)?.[1] || section;
    const content = { system: systemPanel, shortcuts: shortcutsPanel, protection: protectionPanel, surfaces: surfacesPanel }[active];
    let markup = old.replace('M0 · 仅模拟设置', '本地设置').replace('默认跟随系统，也可以切换主题评审两套界面。', '选择浅色、深色，或与 Windows 保持一致。');
    if (content) markup = markup.slice(0, markup.indexOf('<div class="settings-content">')) + `<div class="settings-content">${content()}</div></div>`;
    else if (active === 'appearance') {
      const anchor = '<div class="setting-row"><div><strong>减少动画</strong>';
      if (markup.includes(anchor)) markup = markup.replace(anchor, workspaceOpacityRow() + anchor);
      else markup = markup.replace(/<\/div><\/div>$/, `${panel('Workspace', workspaceOpacityRow())}</div></div>`);
      markup = markup.replace(/<\/div><\/div>$/, `${message()}</div></div>`);
    }
    return markup;
  };
  function accept(result, type) {
    const next = result?.state || result;
    if (!next?.settings) throw new Error('未收到真实系统状态，请重新核对。');
    desktop.applyHostState(next, false);
    const operation = result?.operation;
    if (type?.startsWith('system.set') && typeof operation?.success !== 'boolean') throw new Error('未收到系统操作结果，请重新核对状态。');
    if (operation?.success === false) throw new Error(operation.message || operation.error || '系统操作失败，原有设置保留。');
    return operation;
  }
  async function mutate(type, payload, success, close = false) {
    if (busy || !state()) return false;
    busy = true; feedback = ''; feedbackError = false;
    if (H.ui.modal) H.ui.modal.locked = true;
    document.querySelectorAll?.('.modal button,.modal input').forEach(control => { control.disabled = true; });
    try {
      accept(await bridge.request(type, payload), type);
      feedback = success || '已保存。';
      if (close) H.closeModal();
      return true;
    } catch (error) {
      feedback = error.message; feedbackError = true;
      const modalError = document.querySelector('#sys-modal-error');
      if (modalError) modalError.textContent = feedback;
      return false;
    } finally {
      busy = false;
      if (H.ui.modal) H.ui.modal.locked = false;
      document.querySelectorAll?.('.modal button,.modal input').forEach(control => { control.disabled = false; });
      H.render();
    }
  }
  async function refresh() {
    if (busy || !state()) return;
    try { accept(await bridge.request('system.getState', {})); }
    catch (error) { feedback = error.message; feedbackError = true; }
    H.render();
  }
  function showPolicy(editOnly = false) {
    hidePolicy = ''; policyOnly = editOnly;
    H.showModal(editOnly ? '完整退出后的桌面图标策略' : '隐藏桌面图标前，请选择退出策略',
      `<p>仅隐藏 Windows 桌面图标，不会删除、移动或修改任何桌面文件。</p><p>Desktop Hub 不运行时仍可恢复：<strong>${recoveryPath}</strong>。</p><div class="option-grid"><label class="option-card"><input type="radio" name="sys-icon-policy" value="restore" data-change="sys-policy-choice"><strong>退出后恢复图标</strong><p>完整退出时恢复隐藏前已知的状态；软件外更改会停止自动恢复。</p></label><label class="option-card"><input type="radio" name="sys-icon-policy" value="keepHidden" data-change="sys-policy-choice"><strong>退出后保持隐藏</strong><p>之后用设置或 Windows 原生菜单显示图标。</p></label></div><p id="sys-modal-error" class="form-error" role="alert"></p>`,
      btn('sys-cancel-policy', '取消') + btn('sys-confirm-hide', editOnly ? '保存退出策略' : '确认并隐藏', 'disabled', 'primary'));
  }
  Object.assign(H.actions, {
    'settings-section': el => { section = el.dataset.section; feedback = ''; feedbackError = false; previousSection(el); if (['system','shortcuts'].includes(section)) void refresh(); },
    'sys-refresh': refresh,
    'sys-auto-close-dock': el => mutate('settings.save', { settings: { autoCloseDock: !!el.checked } }, 'Dock 启动后收起偏好已保存。'),
    'sys-performance-mode': el => {
      if (!['quick', 'lightweight'].includes(el.value)) return;
      return mutate('settings.save', { settings: { performanceMode: el.value } }, '性能模式已保存。');
    },
    'sys-startup': el => mutate('system.setStartup', { enabled: el.dataset.enabled === 'true' }, '启动项已按真实系统状态更新。'),
    'sys-icons': el => {
      if (busy) return;
      if (el.dataset.visible === 'true') return mutate('system.setDesktopIcons', { visible: true }, '桌面图标已显示。');
      if (!integration().desktopIcons?.firstHideAuthorized) { showPolicy(); return; }
      return mutate('system.setDesktopIcons', { visible: false }, '桌面图标已隐藏。');
    },
    'sys-policy': () => { if (!busy) showPolicy(true); },
    'sys-policy-choice': el => { if (busy) return; hidePolicy = ['restore','keepHidden'].includes(el.value) ? el.value : ''; const control = document.querySelector('[data-action="sys-confirm-hide"]'); if (control) control.disabled = !hidePolicy; },
    'sys-cancel-policy': () => { if (busy) return; hidePolicy = ''; H.closeModal(); },
    'sys-confirm-hide': () => {
      if (!['restore','keepHidden'].includes(hidePolicy)) { const target = document.querySelector('#sys-modal-error'); if (target) target.textContent = '请先选择退出后的策略。'; return; }
      return policyOnly ? mutate('system.setIconExitPolicy', { policy: hidePolicy }, '退出策略已保存。', true) : mutate('system.setDesktopIcons', { visible: false, firstHidePolicy: hidePolicy }, '桌面图标已隐藏。', true);
    },
    'sys-shortcut': el => { if (!shortcutDraft) shortcutsPanel(); if (['dock','workspace'].includes(el.dataset.key)) shortcutDraft[el.dataset.key] = el.value; },
    'sys-default-hotkeys': () => { shortcutDraft = { dock: 'Ctrl+Alt+D', workspace: 'Ctrl+Alt+W' }; H.render(); },
    'sys-save-hotkeys': async () => {
      if (!shortcutDraft) shortcutsPanel();
      const draft = { dock: document.querySelector('#sys-dock-hotkey')?.value ?? shortcutDraft.dock, workspace: document.querySelector('#sys-workspace-hotkey')?.value ?? shortcutDraft.workspace };
      shortcutDraft = draft;
      if (await mutate('system.setHotkeys', draft, '两套全局快捷键已保存并生效。')) shortcutDraft = null;
      H.render();
    },
    'sys-blocklist': el => { blocklistDraft = el.value; },
    'sys-save-blocklist': async () => {
      const raw = document.querySelector('#sys-blocklist')?.value ?? blocklistDraft ?? '';
      blocklistDraft = raw;
      const list = [...new Set(raw.split(/\r?\n/).map(value => value.trim()).filter(Boolean))];
      if (list.length > 100 || list.some(value => value.length > 120 || !/^[^\\/:*?"<>|\x00-\x1f]+\.exe$/i.test(value))) { feedback = '每行输入一个 EXE 文件名，例如 game.exe；不要填写路径或命令。'; feedbackError = true; H.render(); return; }
      if (await mutate('settings.save', { settings: { foregroundBlocklist: list } }, '前台程序屏蔽名单已保存。')) blocklistDraft = null;
      H.render();
    },
    'sys-toggle-setting': el => { if (el.dataset.setting === 'miniEnabled') return mutate('settings.save', { settings: { miniEnabled: !settings().miniEnabled } }, '窗口偏好已保存。'); },
    'sys-opacity-preview': (el, event) => {
      const value = Number(el.value);
      if (!Number.isFinite(value) || value < 0 || value > 100) return;
      const output = el.parentElement?.querySelector('output');
      if (output) output.textContent = `${value}%`;
      el.setAttribute?.('aria-valuetext', `${value}%`);
      if (event?.type === 'input') return;
      return H.actions['sys-opacity']({ value: value / 100 });
    },
    'sys-opacity': el => { const value = Number(el.value); if (Number.isFinite(value) && value >= 0 && value <= 1) return mutate('settings.save', { settings: { miniOpacity: value } }, 'Mini 不透明度已保存。'); },
    'sys-workspace-opacity-preview': (el, event) => {
      const value = Number(el.value);
      if (!Number.isFinite(value) || value < 0 || value > 100) return;
      const output = el.parentElement?.querySelector('output');
      if (output) output.textContent = `${value}%`;
      el.setAttribute?.('aria-valuetext', `${value}%`);
      if (event?.type === 'input') return;
      return mutate('settings.save', { settings: { workspaceOpacity: value / 100 } }, 'Workspace 不透明度已保存。');
    }
  });
  // The prototype's local keyboard handler reads these display fields. Native
  // registration remains authoritative and every open request is checked by host.
  const previousRender = H.render;
  H.render = function () {
    H.state.settings.dockShortcut = settings().dockHotkey || 'Ctrl+Alt+D';
    H.state.settings.workspaceShortcut = settings().workspaceHotkey || 'Ctrl+Alt+W';
    previousRender();
    const version = document.querySelector('.workspace-header .brand small');
    if (version) version.textContent = state()?.version ? `工作空间 · ${state().version}` : '工作空间';
  };
  window.DesktopHubSystem = Object.freeze({ recoveryPath, getState: () => ({ section, busy, hidePolicy, shortcutDraft, blocklistDraft, feedback, feedbackError }) });
}());
