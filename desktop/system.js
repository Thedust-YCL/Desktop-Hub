/* M2.3: native system settings. Reuses the accepted settings layout and controls. */
(function () {
  'use strict';
  const t = window.DesktopHubI18n?.text || ((value, ...args) => Array.isArray(value) ? value.reduce((out, part, i) => out + part + (i < args.length ? args[i] : ''), '') : value);
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
  const workspaceOpacityRow = () => row(t('Workspace 不透明度'), t('调整整个工作空间。设为 0% 后，可从托盘打开设置恢复。'), opacitySlider('workspaceOpacity', t('Workspace 不透明度'), settings().workspaceOpacity ?? 1, 'sys-workspace-opacity-preview'));
  const visualValue = key => H.state.settings[key] ?? settings()[key];
  const toggle = (key, label) => {
    const explicit = key === 'miniEnabled', value = explicit ? settings()[key] : visualValue(key);
    return `<button type="button" class="switch ${value ? 'is-on' : ''}" role="switch" aria-checked="${!!value}" aria-label="${label}" data-action="${explicit ? 'sys-toggle-setting' : 'setting-toggle'}" data-setting="${key}" ${busy || !state() ? 'disabled' : ''}><span></span></button>`;
  };
  const select = (key, label, values) => `<select aria-label="${label}" data-change="setting-value" data-setting="${key}">${values.map(([value, labelText]) => `<option value="${value}" ${visualValue(key) === value ? 'selected' : ''}>${labelText}</option>`).join('')}</select>`;
  const message = () => feedback ? `<p class="${feedbackError ? 'form-error' : 'subtitle'}" role="${feedbackError ? 'alert' : 'status'}" aria-live="polite">${e(feedback)}</p>` : '';
  function updatesPanel() {
    const update = state()?.updates || { stage: 'idle', currentVersion: state()?.version || '' };
    const labels = { idle: '启动后会自动检查更新。', unavailable: '当前为便携版或开发版本，请安装正式安装版以接收更新。', checking: '正在检查更新…', current: '当前已是最新版本。', available: '发现新版本，可下载更新。', downloading: '正在下载更新…', ready: '更新已下载，重启后安装。', installing: '正在准备重启更新…', error: '更新未完成，请重试。' };
    const active = ['checking', 'downloading', 'installing', 'unavailable'].includes(update.stage);
    const version = `${e(update.currentVersion)}${update.availableVersion ? ` → ${e(update.availableVersion)}` : ''}`;
    return panel(t('软件更新'), row(t('当前版本'), version,
      btn('sys-update-check', t('检查更新'), active ? 'disabled' : '') +
      (update.stage === 'available' || (update.stage === 'error' && update.availableVersion) ? btn('sys-update-download', t(update.stage === 'error' ? '重试下载' : '下载更新'), '', 'primary') : '') +
      (update.stage === 'ready' ? btn('sys-update-install', t('重启更新'), '', 'primary') : '')) +
      `<p role="status" aria-live="polite">${e(t(labels[update.stage] || labels.idle))}</p>` +
      (update.stage === 'downloading' ? `<div role="progressbar" aria-label="${t('下载进度')}" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${Math.max(0, Math.min(100, Number(update.progress) || 0))}">${Math.max(0, Math.min(100, Number(update.progress) || 0))}%</div>` : '') +
      (update.error ? `<p class="form-error" role="alert">${e(t(update.error))}</p>` : ''));
  }
  async function updateRequest(type) {
    try {
      const update = await bridge.request(type);
      // Host emits progress events; response makes the initial state visible immediately.
      if (update?.stage) desktop.applyHostState({ ...state(), updates: update });
      H.render();
    } catch (error) { feedback = error.message; feedbackError = true; H.render(); }
  }
  H.actions['sys-update-check'] = () => updateRequest('update.check');
  H.actions['sys-update-download'] = () => updateRequest('update.download');
  H.actions['sys-update-install'] = () => H.showModal(t('重启更新'), `<p>${t('将退出并重启 Desktop Hub 以安装更新，请先保存当前操作。你的本机数据会保留。')}</p>`, btn('sys-update-cancel', t('取消')) + btn('sys-update-confirm', t('重启更新'), '', 'primary'));
  H.actions['sys-update-cancel'] = () => H.closeModal();
  H.actions['sys-update-confirm'] = async () => { H.closeModal(); await updateRequest('update.install'); };
  bridge.on?.('update.changed', update => {
    desktop.applyHostState({ ...state(), updates: update }, false);
    if (H.ui.page === 'settings' && section === 'system') H.render();
    else if (update.stage === 'available') H.toast?.(t('发现新版本，可下载更新。'), 'info');
  });
  function systemPanel() {
    const icons = integration().desktopIcons, startup = integration().startup;
    const iconKnown = icons?.supported === true && typeof icons.visible === 'boolean';
    const iconStatus = iconKnown ? icons.visible ? t('当前显示') : t('当前隐藏') : icons?.supported === false ? t('操作不受支持 / 当前状态未知') : t('当前状态未知');
    const startupKnown = !!startup && typeof startup.enabled === 'boolean' &&
      !['registry_unavailable', 'startup_entry_unsupported'].includes(startup.code);
    const exitPolicy = icons?.exitPolicy === 'restore' ? t('完整退出时恢复隐藏前的图标状态') : icons?.exitPolicy === 'keepHidden' ? t('完整退出后保持隐藏') : t('首次隐藏时必须选择');
    return updatesPanel() + panel(t('系统常驻'),
      row(t('性能模式'), t('快速唤出保留界面；轻量常驻在隐藏 60 秒且没有未处理草稿或操作时释放界面，再次打开时恢复浏览位置。'),
        t`<select aria-label="性能模式" data-change="sys-performance-mode" ${busy ? 'disabled' : ''}><option value="quick" ${(settings().performanceMode || 'quick') === 'quick' ? 'selected' : ''}>快速唤出</option><option value="lightweight" ${settings().performanceMode === 'lightweight' ? 'selected' : ''}>轻量常驻</option></select>`) +
      row(t('当前用户开机自启'), startupKnown ? t`${startup.enabled ? t('已启用') : t('已关闭')} · 登录后安静常驻，不自动打开 Workspace 或执行场景。` : startup ? t('当前状态未知，请重新核对 Windows 启动项。') : t('正在读取真实 Windows 启动项。'),
        startupKnown ? btn('sys-startup', startup.enabled ? t('关闭开机自启') : t('开启开机自启'), `data-enabled="${!startup.enabled}"`) : t('<span class="badge">状态未知</span>')) +
      (startup?.needsRepair ? note(`${e(startup.message || t('启动项与当前位置不一致。'))} ${btn('sys-startup', t('按当前位置修复'), 'data-enabled="true"')}`) : '') +
      (startup && !startup.success && !startup.needsRepair ? note(e(startup.message)) : '') +
      row(t('托盘与完整退出'), t('关闭 Workspace 后继续常驻。托盘可打开设置、显示 Mini、恢复桌面图标或完整退出。'), btn('native-exit', t('完整退出')))) +
      panel(t('使用帮助'), row(t('首次使用引导'), t('重新了解导入、日常使用、桌面图标与开机自启。已有内容和设置会保留。'), btn('settings-replay-onboarding', t('重新引导')))) +
      panel(t('桌面图标与恢复'),
      row(t('桌面图标'), t`${iconStatus}。仅改变图标整体可见性，不会删除、移动或修改桌面文件。`,
        `<div class="toolbar">${btn('sys-icons', t('显示桌面图标'), `data-visible="true" ${!iconKnown ? 'disabled' : ''}`)}${btn('sys-icons', t('隐藏桌面图标'), `data-visible="false" ${!iconKnown ? 'disabled' : ''}`)}</div>`) +
      (icons?.conflict ? note(t`${e(icons.message || t('检测到软件外更改，已保留当前 Windows 状态。'))} 自动恢复已暂停，请核对后选择目标状态。`) : icons?.message && !icons.success ? note(e(icons.message)) : '') +
      row(t('完整退出后的策略'), exitPolicy, btn('sys-policy', t('更改退出策略'), `${!icons?.firstHideAuthorized ? 'disabled' : ''}`)) +
      note(t`随时可以用 Windows 恢复，即使 Desktop Hub 未运行：<strong>${t(recoveryPath)}</strong>。`) +
      note(t('意外退出或 Explorer 重启后会重新核对状态；不能保证所有情况下自动恢复成功。关闭 Workspace 和 Windows 关机 / 注销不等同于完整退出。')) +
      `<div class="toolbar">${btn('sys-refresh', t('重新核对状态'))}</div>`) + message();
  }
  function shortcutsPanel() {
    if (!shortcutDraft) shortcutDraft = { dock: settings().dockHotkey || 'Ctrl+Alt+D', workspace: settings().workspaceHotkey || 'Ctrl+Alt+W' };
    const hotkeys = integration().hotkeys;
    return panel(t('独立快捷键'),
      t`<div class="form-grid"><label class="field"><span>展开 Dock</span><input id="sys-dock-hotkey" autocomplete="off" spellcheck="false" maxlength="64" value="${e(shortcutDraft.dock)}" data-change="sys-shortcut" data-key="dock"><small>当前保存：${e(settings().dockHotkey || 'Ctrl+Alt+D')}</small></label><label class="field"><span>唤出 Workspace</span><input id="sys-workspace-hotkey" autocomplete="off" spellcheck="false" maxlength="64" value="${e(shortcutDraft.workspace)}" data-change="sys-shortcut" data-key="workspace"><small>当前保存：${e(settings().workspaceHotkey || 'Ctrl+Alt+W')}</small></label></div>` +
      t`<p class="subtitle">Ctrl、Alt、Shift 中至少两个修饰键，加字母、数字或 F1–F24（F12 除外），例如 Ctrl+Alt+W。两个入口必须不同。</p>` +
      `<div class="toolbar">${btn('sys-save-hotkeys', t('保存快捷键'), '', 'primary')}${btn('sys-default-hotkeys', t('恢复默认'))}</div>` +
      (hotkeys?.success === false ? note(e(hotkeys.error || t('快捷键未注册，请选择其他组合。'))) : '') +
      note(t('Windows 占用或注册失败时保留之前有效的快捷键。屏蔽程序和全屏保护同样适用于快捷键唤出。'))) + message();
  }
  function protectionPanel() {
    if (blocklistDraft === null) blocklistDraft = (settings().foregroundBlocklist || []).join('\n');
    return panel(t('全屏与游戏保护'),
      note(t('全屏程序前台时阻止自动唤出。游戏识别可能误判或漏判，可用屏蔽名单明确保护目标程序。Desktop Hub 自身的 Workspace 不会被当作游戏。')) +
      t`<label class="field"><span>前台程序屏蔽名单</span><textarea id="sys-blocklist" rows="6" maxlength="12100" data-change="sys-blocklist" spellcheck="false" placeholder="game.exe&#10;another-game.exe">${e(blocklistDraft)}</textarea><small>每行一个 EXE 文件名；无需完整路径。名单只阻止面板唤出，不会关闭或修改这些程序。</small></label><div class="toolbar">${btn('sys-save-blocklist', t('保存屏蔽名单'), '', 'primary')}</div>`) + message();
  }
  function surfacesPanel() {
    const opacity = settings().miniOpacity ?? 0.9;
    return panel('Mini',
      row(t('Mini 常驻入口'), t('显示 2–4 个已固定的应用或场景；Dock / Workspace 打开时暂时收起。'), toggle('miniEnabled', t('显示 Mini'))) +
      row(t('固定入口'), t('应用与场景沿用同一份真实启动配置。'), btn('launcher-manage', t('管理固定入口'), 'data-surface="mini"')) +
      row(t('位置'), t('纵向靠左或靠右，顶部为横向工具栏。'), select('miniPosition', t('Mini 位置'), [['left',t('左侧')],['right',t('右侧')],['top',t('顶部')]])) +
      row(t('尺寸'), t('调整工具栏和点击区域。'), select('miniSize', t('Mini 尺寸'), [['small',t('紧凑')],['normal',t('标准')],['large',t('宽松')]])) +
      row(t('显示模式'), t('仅显示图标，或同时显示入口名称。'), select('miniMode', t('Mini 显示模式'), [['icons',t('仅图标')],['labels',t('图标与名称')]])) +
      row(t('不透明度'), t('从 0% 到 100% 调整 Mini；可用快捷键再次打开工作空间。'), opacitySlider('miniOpacity', t('Mini 不透明度'), opacity, 'sys-opacity-preview')) +
      `<div class="toolbar">${btn('settings-preview', t`${icon('mini')} 查看 Mini`, 'data-surface="mini"')}</div>`) +
      panel('Dock',
      row(t('位置'), t('在所选屏幕边缘停留，可展开 Dock。'), select('dockPosition', t('Dock 位置'), [['left',t('左侧')],['right',t('右侧')],['bottom',t('底部')]])) +
      row(t('尺寸'), t('快速入口的显示密度。'), select('dockSize', t('Dock 尺寸'), [['small',t('紧凑')],['normal',t('标准')],['large',t('宽松')]])) +
      row(t('图标密度'), t('常用应用与快捷场景保留各自区域。'), select('dockIconSize', t('Dock 图标密度'), [['compact',t('紧凑')],['normal',t('标准')],['large',t('较大')]])) +
      row(t('启动后收起'), t('独立应用启动请求成功后自动收起 Dock；场景保留进度入口。'), t`<label class="switch"><input type="checkbox" data-change="sys-auto-close-dock" aria-label="独立应用启动请求成功后自动收起 Dock" ${settings().autoCloseDock ? 'checked' : ''}><span></span></label>`) +
      row(t('边缘悬停展开'), t('全屏及屏蔽程序前台时暂停边缘触发。'), toggle('hoverEnabled', t('边缘悬停展开'))) +
      row(t('悬停延迟'), t('默认约 500 ms，避免经过边缘时误触。'), t`<select aria-label="Dock 悬停延迟" data-change="setting-value" data-setting="hoverDelay">${[300,500,800,1000].map(value => `<option value="${value}" ${visualValue('hoverDelay') === value ? 'selected' : ''}>${value} ms</option>`).join('')}</select>`) +
      row(t('允许在普通软件前台展开'), t('允许在普通应用上方临时唤出 Dock。'), toggle('allowForeground', t('允许在普通软件前台展开'))) +
      `<div class="toolbar">${btn('settings-preview', t`${icon('dock')} 查看 Dock`, 'data-surface="dock"')}</div>`) + message();
  }
  H.pages.settings = function () {
    const old = originalSettings();
    const active = old.match(/data-section="([^\"]+)" aria-current="page"/)?.[1] || section;
    const content = { system: systemPanel, shortcuts: shortcutsPanel, protection: protectionPanel, surfaces: surfacesPanel }[active];
    let markup = old.replace(t('M0 · 仅模拟设置'), t('本地设置')).replace(t('默认跟随系统，也可以切换主题评审两套界面。'), t('选择浅色、深色，或与 Windows 保持一致。'));
    if (content) markup = markup.slice(0, markup.indexOf('<div class="settings-content">')) + `<div class="settings-content">${content()}</div></div>`;
    else if (active === 'appearance') {
      const languageRow = row(t('语言 / Language'), t('选择界面语言，立即生效并自动保存。'), t`<select aria-label="语言 / Language" data-change="sys-language" ${busy || !state() ? 'disabled' : ''}><option value="zh-CN" ${(settings().language || 'zh-CN') === 'zh-CN' ? 'selected' : ''}>中文</option><option value="en-US" ${settings().language === 'en-US' ? 'selected' : ''}>English</option></select>`);
      markup = markup.replace('<div class="settings-content">', `<div class="settings-content">${panel(t('语言 / Language'), languageRow)}`);
      const anchor = t('<div class="setting-row"><div><strong>减少动画</strong>');
      if (markup.includes(anchor)) markup = markup.replace(anchor, workspaceOpacityRow() + anchor);
      else markup = markup.replace(/<\/div><\/div>$/, `${panel('Workspace', workspaceOpacityRow())}</div></div>`);
      markup = markup.replace(/<\/div><\/div>$/, `${message()}</div></div>`);
    }
    return markup;
  };
  function accept(result, type) {
    const next = result?.state || result;
    if (!next?.settings) throw new Error(t('未收到真实系统状态，请重新核对。'));
    desktop.applyHostState(next, false);
    const operation = result?.operation;
    if (type?.startsWith('system.set') && typeof operation?.success !== 'boolean') throw new Error(t('未收到系统操作结果，请重新核对状态。'));
    if (operation?.success === false) throw new Error(operation.message || operation.error || t('系统操作失败，原有设置保留。'));
    return operation;
  }
  async function mutate(type, payload, success, close = false) {
    if (busy || !state()) return false;
    busy = true; feedback = ''; feedbackError = false;
    if (H.ui.modal) H.ui.modal.locked = true;
    document.querySelectorAll?.('.modal button,.modal input').forEach(control => { control.disabled = true; });
    try {
      accept(await bridge.request(type, payload), type);
      feedback = t(success || '已保存。');
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
    H.showModal(editOnly ? t('完整退出后的桌面图标策略') : t('隐藏桌面图标前，请选择退出策略'),
      t`<p>仅隐藏 Windows 桌面图标，不会删除、移动或修改任何桌面文件。</p><p>Desktop Hub 不运行时仍可恢复：<strong>${t(recoveryPath)}</strong>。</p><div class="option-grid"><label class="option-card"><input type="radio" name="sys-icon-policy" value="restore" data-change="sys-policy-choice"><strong>退出后恢复图标</strong><p>完整退出时恢复隐藏前已知的状态；软件外更改会停止自动恢复。</p></label><label class="option-card"><input type="radio" name="sys-icon-policy" value="keepHidden" data-change="sys-policy-choice"><strong>退出后保持隐藏</strong><p>之后用设置或 Windows 原生菜单显示图标。</p></label></div><p id="sys-modal-error" class="form-error" role="alert"></p>`,
      btn('sys-cancel-policy', t('取消')) + btn('sys-confirm-hide', editOnly ? t('保存退出策略') : t('确认并隐藏'), 'disabled', 'primary'));
  }
  Object.assign(H.actions, {
    'sys-language': el => {
      if (!['zh-CN', 'en-US'].includes(el.value)) return;
      return mutate('settings.save', { settings: { language: el.value } }, '语言已保存。');
    },
    'settings-section': el => { section = el.dataset.section; feedback = ''; feedbackError = false; previousSection(el); if (['system','shortcuts'].includes(section)) void refresh(); },
    'sys-refresh': refresh,
    'sys-auto-close-dock': el => mutate('settings.save', { settings: { autoCloseDock: !!el.checked } }, t('Dock 启动后收起偏好已保存。')),
    'sys-performance-mode': el => {
      if (!['quick', 'lightweight'].includes(el.value)) return;
      return mutate('settings.save', { settings: { performanceMode: el.value } }, t('性能模式已保存。'));
    },
    'sys-startup': el => mutate('system.setStartup', { enabled: el.dataset.enabled === 'true' }, t('启动项已按真实系统状态更新。')),
    'sys-icons': el => {
      if (busy) return;
      if (el.dataset.visible === 'true') return mutate('system.setDesktopIcons', { visible: true }, t('桌面图标已显示。'));
      if (!integration().desktopIcons?.firstHideAuthorized) { showPolicy(); return; }
      return mutate('system.setDesktopIcons', { visible: false }, t('桌面图标已隐藏。'));
    },
    'sys-policy': () => { if (!busy) showPolicy(true); },
    'sys-policy-choice': el => { if (busy) return; hidePolicy = ['restore','keepHidden'].includes(el.value) ? el.value : ''; const control = document.querySelector('[data-action="sys-confirm-hide"]'); if (control) control.disabled = !hidePolicy; },
    'sys-cancel-policy': () => { if (busy) return; hidePolicy = ''; H.closeModal(); },
    'sys-confirm-hide': () => {
      if (!['restore','keepHidden'].includes(hidePolicy)) { const target = document.querySelector('#sys-modal-error'); if (target) target.textContent = t('请先选择退出后的策略。'); return; }
      return policyOnly ? mutate('system.setIconExitPolicy', { policy: hidePolicy }, t('退出策略已保存。'), true) : mutate('system.setDesktopIcons', { visible: false, firstHidePolicy: hidePolicy }, t('桌面图标已隐藏。'), true);
    },
    'sys-shortcut': el => { if (!shortcutDraft) shortcutsPanel(); if (['dock','workspace'].includes(el.dataset.key)) shortcutDraft[el.dataset.key] = el.value; },
    'sys-default-hotkeys': () => { shortcutDraft = { dock: 'Ctrl+Alt+D', workspace: 'Ctrl+Alt+W' }; H.render(); },
    'sys-save-hotkeys': async () => {
      if (!shortcutDraft) shortcutsPanel();
      const draft = { dock: document.querySelector('#sys-dock-hotkey')?.value ?? shortcutDraft.dock, workspace: document.querySelector('#sys-workspace-hotkey')?.value ?? shortcutDraft.workspace };
      shortcutDraft = draft;
      if (await mutate('system.setHotkeys', draft, t('两套全局快捷键已保存并生效。'))) shortcutDraft = null;
      H.render();
    },
    'sys-blocklist': el => { blocklistDraft = el.value; },
    'sys-save-blocklist': async () => {
      const raw = document.querySelector('#sys-blocklist')?.value ?? blocklistDraft ?? '';
      blocklistDraft = raw;
      const list = [...new Set(raw.split(/\r?\n/).map(value => value.trim()).filter(Boolean))];
      if (list.length > 100 || list.some(value => value.length > 120 || !/^[^\\/:*?"<>|\x00-\x1f]+\.exe$/i.test(value))) { feedback = t('每行输入一个 EXE 文件名，例如 game.exe；不要填写路径或命令。'); feedbackError = true; H.render(); return; }
      if (await mutate('settings.save', { settings: { foregroundBlocklist: list } }, t('前台程序屏蔽名单已保存。'))) blocklistDraft = null;
      H.render();
    },
    'sys-toggle-setting': el => { if (el.dataset.setting === 'miniEnabled') return mutate('settings.save', { settings: { miniEnabled: !settings().miniEnabled } }, t('窗口偏好已保存。')); },
    'sys-opacity-preview': (el, event) => {
      const value = Number(el.value);
      if (!Number.isFinite(value) || value < 0 || value > 100) return;
      const output = el.parentElement?.querySelector('output');
      if (output) output.textContent = `${value}%`;
      el.setAttribute?.('aria-valuetext', `${value}%`);
      if (event?.type === 'input') return;
      return H.actions['sys-opacity']({ value: value / 100 });
    },
    'sys-opacity': el => { const value = Number(el.value); if (Number.isFinite(value) && value >= 0 && value <= 1) return mutate('settings.save', { settings: { miniOpacity: value } }, t('Mini 不透明度已保存。')); },
    'sys-workspace-opacity-preview': (el, event) => {
      const value = Number(el.value);
      if (!Number.isFinite(value) || value < 0 || value > 100) return;
      const output = el.parentElement?.querySelector('output');
      if (output) output.textContent = `${value}%`;
      el.setAttribute?.('aria-valuetext', `${value}%`);
      if (event?.type === 'input') return;
      return mutate('settings.save', { settings: { workspaceOpacity: value / 100 } }, t('Workspace 不透明度已保存。'));
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
    if (version) version.textContent = state()?.version ? t`工作空间 · ${state().version}` : t('工作空间');
  };
  window.DesktopHubSystem = Object.freeze({ recoveryPath, getState: () => ({ section, busy, hidePolicy, shortcutDraft, blocklistDraft, feedback, feedbackError }) });
}());
