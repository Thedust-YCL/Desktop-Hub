/* Desktop Hub M0 — local, simulated settings and first-run onboarding. */
(function () {
  'use strict';
  const H = window.Hub;
  let activeSection = 'appearance';
  let shortcutDraft = null;
  let restoreDraft = '';
  let onboarding = null;
  const sections = [
    ['appearance', 'sun', '外观与主题'], ['surfaces', 'dock', 'Mini 与 Dock'],
    ['shortcuts', 'keyboard', '快捷键'], ['protection', 'shield', '全屏保护'],
    ['sources', 'folder', '文件来源'], ['system', 'settings', '系统与恢复']
  ];
  const e = value => H.escape(String(value == null ? '' : value));
  const ico = (name, size) => H.icon(name, size || 20);
  const button = (label, action, className = 'button', attrs = '') => `<button class="${className}" data-action="${action}" ${attrs}>${label}</button>`;
  const note = text => `<div class="settings-note">${ico('alert', 16)}<span>${text}</span></div>`;
  const setting = (title, description, control) => `<div class="setting-row"><div><strong>${title}</strong><p>${description}</p></div><div class="setting-control">${control}</div></div>`;
  const toggle = (key, label) => `<button type="button" class="switch ${H.state.settings[key] ? 'is-on' : ''}" role="switch" aria-checked="${!!H.state.settings[key]}" aria-label="${e(label)}" data-action="setting-toggle" data-setting="${key}"><span></span></button>`;
  const select = (key, label, values) => `<select aria-label="${e(label)}" data-change="setting-value" data-setting="${key}">${values.map(([value, text]) => `<option value="${value}" ${H.state.settings[key] === value ? 'selected' : ''}>${text}</option>`).join('')}</select>`;
  const panel = (title, body, description = '') => `<section class="panel settings-section"><h2>${title}</h2>${description ? `<p class="subtitle">${description}</p>` : ''}${body}</section>`;
  function applySettings() {
    H.save();
    H.applyTheme();
    H.render();
  }
  function appearance() {
    const s = H.state.settings;
    return panel('外观与主题',
      setting('主题', '默认跟随系统，也可以切换主题评审两套界面。', select('theme', '主题', [['system', '跟随系统'], ['dark', '深色'], ['light', '浅色']])) +
      setting('减少动画', '关闭面板与卡片的过渡动效。', toggle('reducedMotion', '减少动画')) +
      setting('透明效果', '为 Mini 与 Dock 提供克制的半透明表面。', toggle('transparency', '透明效果')) +
      `<div class="option-grid"><div class="option-card"><strong>${ico('sun')} 浅色</strong><p>明亮、清晰的内容表面</p></div><div class="option-card"><strong>${ico('moon')} 深色</strong><p>低饱和、安静的桌面入口</p></div></div>` +
      note(`当前主题设置：${s.theme === 'system' ? '跟随系统' : s.theme === 'dark' ? '深色' : '浅色'}。系统的减少动态效果偏好也会受到尊重。`));
  }
  function surfaces() {
    const count = H.state.scenes.filter(scene => scene.pinned).length;
    const pinned = H.state.scenes.map(scene => `<button class="check-row ${scene.pinned ? 'selected' : ''}" data-action="settings-pin" data-id="${e(scene.id)}" role="checkbox" aria-checked="${!!scene.pinned}"><span class="selection-mark">${scene.pinned ? ico('check', 16) : ''}</span>${ico(scene.icon || 'app')}<span>${e(scene.name)}</span></button>`).join('');
    return panel('Mini',
      `<div class="setting-row"><div><strong>固定场景</strong><p>选择 2–4 个场景，直接显示在 Mini 上。</p></div><span class="badge">${count} / 4 已固定</span></div><div class="option-grid">${pinned || '<div class="empty-state">尚无场景，请先到场景中心创建。</div>'}</div>` +
      setting('位置', '纵向靠左或靠右，顶部为横向工具栏。', select('miniPosition', 'Mini 位置', [['left', '左侧'], ['right', '右侧'], ['top', '顶部']])) +
      setting('尺寸', '调整工具栏和点击区域。', select('miniSize', 'Mini 尺寸', [['small', '紧凑'], ['normal', '标准'], ['large', '宽松']])) +
      setting('显示模式', '默认仅显示图标，悬停时展示场景名称。', select('miniMode', 'Mini 显示模式', [['icons', '仅图标'], ['labels', '图标与名称']])) +
      `<div class="toolbar">${button(`${ico('mini')} 查看 Mini`, 'settings-preview', 'button quiet', 'data-surface="mini"')}</div>`) +
      panel('Dock',
      setting('位置', '在所选屏幕边缘停留，可展开 Dock。', select('dockPosition', 'Dock 位置', [['left', '左侧'], ['right', '右侧'], ['bottom', '底部']])) +
      setting('尺寸', '快速入口的显示密度。', select('dockSize', 'Dock 尺寸', [['small', '紧凑'], ['normal', '标准'], ['large', '宽松']])) +
      setting('边缘悬停展开', '进入展开区域后保持打开，场景运行后也不会自动收起。', toggle('hoverEnabled', '边缘悬停展开')) +
      setting('悬停延迟', '默认约 500 ms，避免经过边缘时误触。', `<label class="field"><span class="sr-only">Dock 悬停延迟</span><select aria-label="Dock 悬停延迟" data-change="setting-value" data-setting="hoverDelay">${[300, 500, 800, 1000].map(value => `<option value="${value}" ${Number(H.state.settings.hoverDelay) === value ? 'selected' : ''}>${value} ms</option>`).join('')}</select></label>`) +
      setting('允许在普通软件前台展开', '可在模拟 Edge 等普通应用上方唤出。', toggle('allowForeground', '允许在普通软件前台展开')) +
      `<div class="toolbar">${button(`${ico('dock')} 查看 Dock`, 'settings-preview', 'button quiet', 'data-surface="dock"')}</div>` +
      note('这里调整原型内的模拟入口。真实桌面层级、全屏识别及前台焦点恢复留待后续技术验证。'));
  }
  function shortcuts() {
    if (!shortcutDraft) shortcutDraft = { dockShortcut: H.state.settings.dockShortcut, workspaceShortcut: H.state.settings.workspaceShortcut };
    return panel('独立快捷键',
      `<div class="form-grid"><label class="field" for="dock-shortcut"><span>展开 Dock</span><input id="dock-shortcut" autocomplete="off" spellcheck="false" value="${e(shortcutDraft.dockShortcut)}" data-change="shortcut-value" data-setting="dockShortcut"><small>默认 Alt+D</small></label><label class="field" for="workspace-shortcut"><span>唤出 Workspace</span><input id="workspace-shortcut" autocomplete="off" spellcheck="false" value="${e(shortcutDraft.workspaceShortcut)}" data-change="shortcut-value" data-setting="workspaceShortcut"><small>默认 Alt+W</small></label></div>` +
      `<p class="subtitle">使用 Ctrl、Alt、Shift 或 Meta 加字母、数字或功能键，例如 Alt+W。两个入口需要不同的快捷键。</p><p id="shortcut-conflict" class="validation-message" role="status"></p>` +
      `<div class="toolbar">${button('保存快捷键', 'settings-save-shortcuts', 'button primary')}${button('恢复默认', 'settings-default-shortcuts', 'button quiet')}</div>` +
      note('快捷键仅在当前原型页面聚焦时演示，不注册 Windows 全局快捷键；浏览器或系统占用的组合可使用演示控制按钮替代。'));
  }
  function protection() {
    const exclusions = H.state.exclusions || [];
    return panel('全屏与游戏保护',
      setting('全屏游戏保护', '模拟全屏游戏或排除程序运行时，阻止边缘悬停展开 Dock。', toggle('gameProtection', '全屏游戏保护')) +
      `<div class="setting-row"><div><strong>排除程序</strong><p>列表内的程序受到额外保护；此列表只参与演示状态。</p></div>${button(`${ico('plus')} 添加`, 'settings-add-exclusion', 'button quiet')}</div>` +
      `<div class="source-list">${exclusions.map((name, index) => `<div class="list-row">${ico('app')}<span>${e(name)}</span>${button(ico('trash', 18), 'settings-remove-exclusion', 'icon-button', `data-index="${index}" aria-label="移除 ${e(name)}"`)}</div>`).join('') || '<div class="empty-state">暂无排除程序</div>'}</div>` +
      note('M0 不检测真实进程或全屏状态。可在演示控制栏选择模拟普通应用 / 全屏游戏，验证入口行为。'));
  }
  function sources() {
    return panel('文件来源',
      `<div class="setting-row"><div><strong>授权目录</strong><p>个人桌面为默认来源；公共桌面与其他目录由用户自愿选择。</p></div>${button(`${ico('plus')} 添加目录`, 'settings-add-source', 'button quiet')}</div>` +
      `<div class="source-list">${H.state.sources.map(source => `<div class="source-row"><div class="list-row"><label class="check-row"><input type="checkbox" data-change="source-enabled" data-id="${e(source.id)}" ${source.enabled ? 'checked' : ''}><span>${ico('folder')} ${e(source.name)}</span></label>${source.id !== 'desktop' && !/个人桌面|用户桌面/.test(source.name) && !/公共桌面/.test(source.name) ? button(ico('trash', 18), 'settings-remove-source', 'icon-button', `data-id="${e(source.id)}" aria-label="移除来源 ${e(source.name)}"`) : '<span class="badge">模拟来源</span>'}</div><p class="source-path">${e(source.path)}</p><label class="check-row"><input type="checkbox" data-change="source-recursive" data-id="${e(source.id)}" ${source.recursive ? 'checked' : ''}><span>递归索引子文件夹</span></label></div>`).join('')}</div>` +
      note('默认不递归。这里只保存模拟授权与索引选项；不会扫描、监控或操作真实目录。未索引文件夹可在资源库临时浏览。') +
      `<div class="toolbar">${button(`${ico('scan')} 重播扫描预览`, 'settings-replay-onboarding', 'button quiet')}</div>`);
  }
  function system() {
    const s = H.state.settings;
    const restore = s.restorePolicy === 'on-exit' ? '退出时恢复' : s.restorePolicy === 'manual' ? '由用户手动恢复' : '尚未选择';
    return panel('系统',
      setting('开机自启', '记录模拟偏好，不创建系统启动项。', toggle('autoStart', '开机自启（模拟）')) +
      setting('托盘入口', '记录模拟托盘策略。', toggle('tray', '托盘入口（模拟）'))) +
      panel('桌面图标与恢复',
      setting('隐藏桌面图标 · 模拟', '首次开启前，需要明确选择退出后的恢复策略。', toggle('hideDesktopIcons', '隐藏桌面图标（模拟）')) +
      setting('退出后的恢复策略', restore, button('设置恢复策略', 'settings-restore-policy', 'button quiet')) +
      (s.hideDesktopIcons ? `<div class="toolbar">${button('恢复桌面图标（模拟）', 'settings-restore-icons', 'button')}</div>` : '') +
      note('演示只改变背景中的模拟图标。Windows 桌面图标及系统设置始终不会被修改。')) +
      panel('演示数据',
      setting('首次使用引导', '重看扫描确认、Mini 位置选择和 Launchpad 欢迎流程。', button('重播引导', 'settings-replay-onboarding', 'button quiet')) +
      setting('重置演示数据', '清除当前原型中的本地修改，并重新开始首次使用引导。', button('重置', 'settings-reset', 'button quiet')));
  }
  H.pages.settings = function () {
    const content = { appearance, surfaces, shortcuts, protection, sources, system }[activeSection]();
    return `<div class="page-heading"><div><p class="eyebrow">PREFERENCES</p><h1 class="page-title" tabindex="-1">设置</h1><p class="subtitle">让桌面入口适合你的使用习惯。</p></div><span class="badge">M0 · 仅模拟设置</span></div><div class="settings-layout"><nav class="settings-nav" aria-label="设置分类">${sections.map(([id, icon, title]) => button(`${ico(icon)}<span>${title}</span>`, 'settings-section', `button quiet ${activeSection === id ? 'active' : ''}`, `data-section="${id}" ${activeSection === id ? 'aria-current="page"' : ''}`)).join('')}</nav><div class="settings-content">${content}</div></div>`;
  };
  function showRestorePolicy(enable) {
    restoreDraft = '';
    H.showModal('选择桌面图标恢复策略',
      `<p class="subtitle">隐藏只影响本原型里的模拟桌面。请选择退出 Desktop Hub 后如何恢复，再确认${enable ? '启用' : ''}。</p><div class="option-grid"><label class="option-card"><input type="radio" name="restore-policy" value="on-exit" data-change="restore-policy"><strong>退出时恢复</strong><p>正常退出时恢复模拟桌面图标。</p></label><label class="option-card"><input type="radio" name="restore-policy" value="manual" data-change="restore-policy"><strong>由用户手动恢复</strong><p>保留演示隐藏状态，可随时在设置中恢复。</p></label></div><p class="settings-note">真实退出、崩溃及异常恢复机制留待原生阶段验证。</p>`,
      button('取消', 'settings-cancel-modal', 'button quiet') + button(enable ? '确认并启用（模拟）' : '保存恢复策略', 'settings-confirm-restore', 'button primary', `data-enable="${enable ? 'true' : 'false'}"`));
  }
  function normalizedShortcut(raw) {
    const parts = String(raw || '').trim().replace(/\s+/g, '').split('+');
    const key = parts.pop();
    const modifiers = parts.map(value => ({ ctrl: 'Ctrl', control: 'Ctrl', alt: 'Alt', shift: 'Shift', meta: 'Meta', win: 'Meta' }[value.toLowerCase()]));
    if (!key || !modifiers.length || modifiers.some(value => !value) || new Set(modifiers).size !== modifiers.length || !/^([a-z0-9]|F(?:[1-9]|1[0-2])|Space)$/i.test(key)) return '';
    return ['Ctrl', 'Alt', 'Shift', 'Meta'].filter(value => modifiers.includes(value)).concat(key.length === 1 ? key.toUpperCase() : key.toLowerCase() === 'space' ? 'Space' : key.toUpperCase()).join('+');
  }
  function shortcutMessage() {
    const dock = normalizedShortcut(shortcutDraft.dockShortcut), workspace = normalizedShortcut(shortcutDraft.workspaceShortcut);
    return !dock || !workspace ? '请输入有效的组合快捷键，例如 Alt+D。' : dock === workspace ? '快捷键冲突：Dock 与 Workspace 不能使用相同的组合。' : '';
  }
  function updateShortcutMessage() {
    const target = document.getElementById('shortcut-conflict');
    if (target) target.textContent = shortcutMessage();
  }
  function renderOnboarding() {
    const step = onboarding.step;
    const progress = `<div class="step-indicator" aria-label="首次设置，第 ${Math.min(step + 1, 4)} 步，共 4 步">${['欢迎', '扫描确认', 'Mini 位置', '准备完成'].map((title, index) => `<span class="${index <= (step === 2 ? 1 : step > 2 ? step - 1 : step) ? 'active' : ''}">${index + 1}<small>${title}</small></span>`).join('')}</div>`;
    let title, body, footer;
    if (step === 0) {
      title = '欢迎使用 Desktop Hub';
      body = `${progress}<div class="onboarding-intro"><div class="onboarding-mark">${ico('grid', 36)}</div><h2>让桌面安静一点</h2><p class="subtitle">一个极简入口，连接场景与文件。所有流程都使用本地模拟数据。</p></div><div class="option-grid"><div class="option-card">${ico('mini')}<strong>Mini</strong><p>常用场景，一点即达</p></div><div class="option-card">${ico('dock')}<strong>Dock</strong><p>边缘停留，轻巧展开</p></div><div class="option-card">${ico('grid')}<strong>Workspace</strong><p>一个清晰的启动台</p></div></div>${note('M0 不启动程序、不访问真实桌面文件，也不修改 Windows 设置。')}`;
      footer = button('开始设置', 'onboarding-next', 'button primary');
    } else if (step === 1) {
      title = '确认模拟扫描结果';
      body = `${progress}<p class="subtitle">个人桌面 · 模拟扫描预览。勾选并确认后，才写入本原型的长期索引。</p><div class="onboarding-preview">${onboarding.files.map(file => `<label class="scan-row check-row"><input type="checkbox" data-change="onboarding-file" data-id="${e(file.id)}" ${onboarding.selected.includes(file.id) ? 'checked' : ''}>${ico(file.type === 'folder' ? 'folder' : 'file')}<span><strong>${e(file.name)}</strong><small>${e(file.path || '个人桌面 · 模拟文件')}</small></span><span class="badge">模拟</span></label>`).join('')}</div><p class="subtitle" id="onboarding-scan-count">已选择 ${onboarding.selected.length} 项；原文件位置与内容不会改变。</p>`;
      footer = button('返回', 'onboarding-back', 'button quiet') + button('确认写入模拟索引', 'onboarding-confirm-scan', 'button primary');
    } else if (step === 2) {
      title = '模拟索引已准备好';
      body = `${progress}<div class="empty-state">${ico('check', 38)}<h2>${onboarding.selected.length} 项已加入模拟索引</h2><p>后续可添加标签和收藏，原文件始终留在原处。</p></div>${note('索引只存储于本原型的浏览器本地状态，没有读取或修改磁盘文件。')}`;
      footer = button('选择 Mini 位置', 'onboarding-next', 'button primary');
    } else if (step === 3) {
      title = '把 Mini 放在顺手的位置';
      body = `${progress}<p class="subtitle">固定场景：创作、游戏、休闲。位置与固定项之后都可以在设置中调整。</p><div class="option-grid position-options">${[['left', '左侧', '纵向工具栏'], ['right', '右侧', '纵向工具栏'], ['top', '顶部', '横向工具栏']].map(([value, label, description]) => `<button class="option-card ${onboarding.position === value ? 'selected' : ''}" data-action="onboarding-position" data-position="${value}" role="radio" aria-checked="${onboarding.position === value}">${ico('mini', 28)}<strong>${label}</strong><p>${description}</p>${onboarding.position === value ? ico('check', 18) : ''}</button>`).join('')}</div>`;
      footer = button('进入 Launchpad', 'onboarding-finish', 'button primary');
    } else {
      title = '你的 Workspace，准备就绪';
      body = `<div class="onboarding-intro">${ico('grid', 36)}<h2>从一个场景开始</h2><p class="subtitle">已准备创作、游戏和休闲三个模拟示例。创建自己的第一个场景，再固定到 Mini。</p></div><div class="list-row">${ico('app')}<span>创作 · 加速器 → Clash → ChatGPT → Edge → DSH</span></div>${note('所有启动和文件操作都有“模拟”标记，供界面与流程评审。')}`;
      footer = button('先看看 Launchpad', 'onboarding-dismiss', 'button quiet') + button('创建第一个场景', 'onboarding-create-scene', 'button primary');
    }
    H.showModal(title, `<div class="onboarding">${body}</div>`, footer, { wide: true, locked: step < 4 });
  }
  H.openOnboarding = function () {
    const files = typeof H.seedFiles === 'function' ? H.seedFiles() : H.state.files.map(file => ({ ...file }));
    onboarding = { step: 0, files, selected: files.map(file => file.id), position: H.state.settings.miniPosition || 'left' };
    renderOnboarding();
  };
  Object.assign(H.actions, {
    'settings-section': el => { activeSection = el.dataset.section; H.render(); },
    'setting-toggle': el => {
      const key = el.dataset.setting;
      if (key === 'hideDesktopIcons' && !H.state.settings[key] && !H.state.settings.restorePolicy) { showRestorePolicy(true); return; }
      H.state.settings[key] = !H.state.settings[key];
      applySettings();
      if (key === 'hideDesktopIcons' || key === 'autoStart' || key === 'tray') H.toast('已保存模拟设置；未修改 Windows 系统。');
    },
    'setting-value': el => { const key = el.dataset.setting; H.state.settings[key] = key === 'hoverDelay' ? Number(el.value) : el.value; applySettings(); },
    'settings-pin': el => {
      const scene = H.state.scenes.find(item => item.id === el.dataset.id);
      if (!scene) return;
      const count = H.state.scenes.filter(item => item.pinned).length;
      if (scene.pinned && count <= 2) { H.toast('Mini 至少需要固定 2 个场景。'); return; }
      if (!scene.pinned && count >= 4) { H.toast('Mini 最多固定 4 个场景。'); return; }
      scene.pinned = !scene.pinned; H.save(); H.render();
    },
    'settings-preview': el => H.setSurface(el.dataset.surface),
    'shortcut-value': el => { shortcutDraft[el.dataset.setting] = el.value; updateShortcutMessage(); },
    'settings-save-shortcuts': () => {
      const error = shortcutMessage();
      if (error) { updateShortcutMessage(); H.toast(error); return; }
      H.state.settings.dockShortcut = normalizedShortcut(shortcutDraft.dockShortcut);
      H.state.settings.workspaceShortcut = normalizedShortcut(shortcutDraft.workspaceShortcut);
      shortcutDraft = null; H.save(); H.render(); H.toast('已保存页面内的演示快捷键。');
    },
    'settings-default-shortcuts': () => { shortcutDraft = { dockShortcut: 'Alt+D', workspaceShortcut: 'Alt+W' }; H.render(); },
    'settings-add-exclusion': () => H.showModal('添加排除程序（模拟）', `<label class="field" for="exclusion-name"><span>程序名称</span><input id="exclusion-name" placeholder="例如：模拟游戏客户端" maxlength="40" autocomplete="off"></label><p class="subtitle">仅添加演示名称，不读取或检查真实进程。</p>`, button('取消', 'settings-cancel-modal', 'button quiet') + button('添加', 'settings-confirm-exclusion', 'button primary')),
    'settings-confirm-exclusion': () => {
      const value = document.getElementById('exclusion-name').value.trim();
      if (!value) { H.toast('请填写一个模拟程序名称。'); return; }
      if (H.state.exclusions.some(name => name.toLowerCase() === value.toLowerCase())) { H.toast('此程序已在排除列表中。'); return; }
      H.state.exclusions.push(value); H.save(); H.closeModal(); H.render(); H.toast('已添加模拟排除程序。');
    },
    'settings-remove-exclusion': el => { H.state.exclusions.splice(Number(el.dataset.index), 1); H.save(); H.render(); },
    'source-enabled': el => { const source = H.state.sources.find(item => item.id === el.dataset.id); if (source) { source.enabled = el.checked; H.save(); H.toast('已保存模拟目录授权；未访问磁盘。'); } },
    'source-recursive': el => { const source = H.state.sources.find(item => item.id === el.dataset.id); if (source) { source.recursive = el.checked; H.save(); } },
    'settings-add-source': () => H.showModal('添加目录（模拟）', `<div class="form-grid"><label class="field" for="source-name"><span>来源名称</span><input id="source-name" maxlength="32" placeholder="例如：课程资料" autocomplete="off"></label><label class="field" for="source-path"><span>示例路径</span><input id="source-path" maxlength="160" placeholder="D:\\课程资料" autocomplete="off"></label></div><label class="check-row"><input id="source-recursive" type="checkbox"><span>递归索引子文件夹（默认关闭）</span></label>${note('填写路径仅用于界面演示，不请求系统权限，也不会访问该位置。')}`, button('取消', 'settings-cancel-modal', 'button quiet') + button('添加模拟来源', 'settings-confirm-source', 'button primary')),
    'settings-confirm-source': () => {
      const name = document.getElementById('source-name').value.trim(), path = document.getElementById('source-path').value.trim();
      if (!name || !path) { H.toast('请填写来源名称与示例路径。'); return; }
      H.state.sources.push({ id: `source-${Date.now()}`, name, path, recursive: document.getElementById('source-recursive').checked, enabled: true });
      H.save(); H.closeModal(); H.render(); H.toast('已添加模拟来源；未扫描真实目录。');
    },
    'settings-remove-source': el => {
      const source = H.state.sources.find(item => item.id === el.dataset.id);
      if (!source) return;
      H.showModal('移除模拟来源？', `<p>将移除来源“${e(source.name)}”的模拟授权。已有资源索引、标签与收藏仍会保留。</p><p class="subtitle">此操作不会删除或改动真实文件。</p>`, button('取消', 'settings-cancel-modal', 'button quiet') + button('移除来源', 'settings-confirm-remove-source', 'button primary', `data-id="${e(source.id)}"`));
    },
    'settings-confirm-remove-source': el => { H.state.sources = H.state.sources.filter(item => item.id !== el.dataset.id); H.save(); H.closeModal(); H.render(); H.toast('模拟来源已移除，已有索引仍保留。'); },
    'settings-restore-policy': () => showRestorePolicy(false),
    'restore-policy': el => { restoreDraft = el.value; },
    'settings-confirm-restore': el => {
      if (!restoreDraft) { H.toast('请先选择退出后的恢复策略。'); return; }
      H.state.settings.restorePolicy = restoreDraft;
      if (el.dataset.enable === 'true') H.state.settings.hideDesktopIcons = true;
      H.save(); H.closeModal(); H.render(); H.toast('模拟恢复策略已保存；Windows 桌面未改变。');
    },
    'settings-restore-icons': () => { H.state.settings.hideDesktopIcons = false; H.save(); H.render(); H.toast('模拟桌面图标已恢复。'); },
    'settings-cancel-modal': () => H.closeModal(),
    'settings-replay-onboarding': () => H.openOnboarding(),
    'settings-reset': () => H.showModal('重置演示数据？', '<p>当前原型中的场景编辑、标签、收藏和设置将恢复默认，并重新打开首次引导。</p><p class="subtitle">仅清除本原型的浏览器本地状态，不影响真实文件与软件。</p>', button('取消', 'settings-cancel-modal', 'button quiet') + button('重置演示数据', 'settings-confirm-reset', 'button primary')),
    'settings-confirm-reset': () => { shortcutDraft = null; activeSection = 'appearance'; H.closeModal(); H.resetDemo(); },
    'onboarding-next': () => { onboarding.step += 1; renderOnboarding(); },
    'onboarding-back': () => { onboarding.step = Math.max(0, onboarding.step - 1); renderOnboarding(); },
    'onboarding-file': el => {
      if (el.checked && !onboarding.selected.includes(el.dataset.id)) onboarding.selected.push(el.dataset.id);
      if (!el.checked) onboarding.selected = onboarding.selected.filter(id => id !== el.dataset.id);
      const count = document.getElementById('onboarding-scan-count');
      if (count) count.textContent = `已选择 ${onboarding.selected.length} 项；原文件位置与内容不会改变。`;
    },
    'onboarding-confirm-scan': () => {
      if (!onboarding.selected.length) { H.toast('请至少选择一个模拟文件，确认后再写入索引。'); return; }
      const existing = new Map(H.state.files.map(file => [file.id, file]));
      onboarding.files.filter(file => onboarding.selected.includes(file.id)).forEach(file => { if (!existing.has(file.id)) existing.set(file.id, file); });
      H.state.files = Array.from(existing.values()); H.state.indexConfirmed = true; H.save();
      onboarding.step = 2; renderOnboarding();
    },
    'onboarding-position': el => { onboarding.position = el.dataset.position; renderOnboarding(); },
    'onboarding-finish': () => {
      H.state.settings.miniPosition = onboarding.position;
      H.state.onboardingComplete = true; H.save(); H.closeModal(); H.setSurface('workspace'); H.navigate('home');
      onboarding.step = 4; renderOnboarding();
    },
    'onboarding-dismiss': () => { onboarding = null; H.closeModal(); },
    'onboarding-create-scene': () => {
      onboarding = null; H.closeModal(); H.navigate('scenes');
      if (typeof H.actions['new-scene'] === 'function') H.actions['new-scene']();
      else if (typeof H.actions['scene-new'] === 'function') H.actions['scene-new']();
      else H.toast('在场景中心点击“新建场景”，保存后可固定到 Mini。');
    }
  });
}());
