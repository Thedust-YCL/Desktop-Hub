/* Native first-use guide. Preferences stay in settings.json; real actions use the existing bridges. */
(function () {
  'use strict';
  const H = window.Hub, desktop = window.DesktopHubDesktop, bridge = window.DesktopHubNative.bridge;
  if (desktop.getState().surface !== 'workspace') return;
  const e = value => H.escape(String(value ?? '')), icon = (name, size = 20) => H.icon(name, size);
  const steps = ['欢迎', '导入', '使用', '桌面图标', '开机自启', '完成'];
  let active = false, step = 0, busy = false, suspended = false, autoStarted = false, rescanning = false;
  let hidePolicy = '', feedback = '', feedbackError = false;
  const host = () => desktop.getState().hostState;
  const settings = () => host()?.settings || {};
  const integration = () => host()?.systemIntegration || {};
  const button = (action, label, kind = 'quiet', attrs = '') => `<button type="button" class="button ${kind}" data-action="${action}" ${busy ? 'disabled' : ''} ${attrs}>${label}</button>`;
  const hint = text => `<p class="guide-hint">${text}</p>`;
  const lesson = (name, title, text, extra = '') => `<div class="guide-lesson"><span class="guide-symbol">${icon(name, 22)}</span><div><h3>${title}</h3><p>${text}</p>${extra}</div></div>`;

  function renderGuide() {
    if (!active || suspended) return;
    const icons = integration().desktopIcons, startup = integration().startup;
    const iconsKnown = icons?.supported === true && typeof icons.visible === 'boolean';
    const startupKnown = typeof startup?.enabled === 'boolean' && !['registry_unavailable', 'startup_entry_unsupported'].includes(startup.code);
    let title, body;
    switch (step) {
      case 0:
        title = '欢迎使用 Desktop Hub';
        body = `<div class="guide-welcome"><span class="guide-mark">${icon('grid', 36)}</span><h3>把常用内容，放在顺手的位置</h3><p>用几步了解导入与使用，再按你的习惯设置桌面。</p></div><div class="guide-overview"><span>${icon('folder', 18)} 导入文件</span><span>${icon('dock', 18)} 快速打开</span><span>${icon('settings', 18)} 常驻设置</span></div>${hint('随时可以跳过。之后在“设置 → 系统与恢复 → 使用帮助”重新引导。')}`;
        break;
      case 1:
        title = '先导入一个文件来源';
        body = lesson('folder', '选择桌面或常用目录', '先查看扫描预览，确认导入后才建立索引。文件留在原处；默认只导入目录的直接子项。') +
          `<div class="guide-import-actions">${button('guide-import', `${icon('monitor', 18)} 查看桌面预览`, 'primary', 'data-kind="desktop"')}${button('guide-import', `${icon('folder', 18)} 选择目录…`, 'quiet', 'data-kind="custom"')}</div>` +
          lesson('tag', '按需要整理', '确认导入时可以选择是否按修改年月自动添加时间标签；之后也能手动添加标签和收藏。') +
          hint('已有来源会保留。取消扫描会回到这里，也可以先跳过导入，稍后在文件资源库添加。');
        break;
      case 2:
        title = '了解日常使用';
        body = lesson('app', '应用：添加 → 点击启动 → 收藏', '在首页“我的应用”选择 EXE 或 Windows 快捷方式。点击图标启动，收藏后会加入 Dock。', button('guide-add-app', '添加一个应用…')) +
          lesson('search', '文件：搜索、标签与收藏', '在文件资源库点击“打开”使用文件；点击文件夹整行进入浏览。用快速搜索、标签和收藏找回常用内容。') +
          lesson('scenes', '场景：把多个动作组合起来', '在场景中心创建场景，添加应用、网页、文件等动作，保存后一次启动。常用应用和场景可固定到 Mini。') +
          `<div class="guide-shortcuts"><span><kbd>${e(settings().workspaceHotkey || 'Ctrl+Alt+W')}</kbd> 打开工作空间</span><span><kbd>${e(settings().dockHotkey || 'Ctrl+Alt+D')}</kbd> 展开 Dock</span><span><kbd>Esc</kbd> 收起工作空间</span></div>`;
        break;
      case 3:
        title = '是否隐藏 Windows 桌面图标';
        body = lesson('monitor', iconsKnown ? `桌面图标当前${icons.visible ? '显示' : '隐藏'}` : '暂时无法读取桌面图标状态', '隐藏后，桌面会更简洁；文件仍可从资源库和资源管理器打开。只改变图标整体可见性，原文件不受影响。') +
          (!icons?.firstHideAuthorized ? `<fieldset class="guide-policy"><legend>如果选择隐藏，完整退出后如何处理？</legend><label class="check-row"><input type="radio" name="guide-policy" value="restore" data-change="guide-policy" ${hidePolicy === 'restore' ? 'checked' : ''} ${busy ? 'disabled' : ''}><span>恢复隐藏前的图标状态</span></label><label class="check-row"><input type="radio" name="guide-policy" value="keepHidden" data-change="guide-policy" ${hidePolicy === 'keepHidden' ? 'checked' : ''} ${busy ? 'disabled' : ''}><span>保持隐藏，由我手动恢复</span></label></fieldset>` : hint(`完整退出后的策略：${icons.exitPolicy === 'keepHidden' ? '保持隐藏' : '恢复隐藏前的图标状态'}。可在系统设置中更改。`)) +
          `<div class="guide-actions">${button('guide-icons', '显示桌面图标', 'quiet', `data-visible="true" ${!iconsKnown ? 'disabled' : ''}`)}${button('guide-icons', '隐藏桌面图标', 'quiet', `data-visible="false" ${!iconsKnown || (!icons?.firstHideAuthorized && !hidePolicy) ? 'disabled' : ''}`)}</div>` +
          hint(`随时恢复：<strong>桌面空白处右键 → 查看 → 显示桌面图标</strong>。关闭工作空间会继续常驻；完整退出请使用托盘菜单。`) +
          (icons?.message && (!icons.success || icons.conflict) ? hint(e(icons.message)) : '') +
          (!iconsKnown ? button('guide-refresh', '重新读取状态') : '');
        break;
      case 4:
        title = '是否随 Windows 登录启动';
        body = lesson('settings', startupKnown ? `开机自启当前${startup.enabled ? '开启' : '关闭'}` : '暂时无法读取启动项状态', '开启后，在当前用户登录 Windows 时安静常驻。不会自动打开工作空间，也不会自动执行场景。') +
          `<div class="guide-actions">${button('guide-startup', startup?.needsRepair ? '按当前位置修复自启' : '开启开机自启', 'quiet', `data-enabled="true" ${!startupKnown ? 'disabled' : ''}`)}${button('guide-startup', '关闭开机自启', 'quiet', `data-enabled="false" ${!startupKnown ? 'disabled' : ''}`)}</div>` +
          hint('这一步按你的需要选择，也可以保持当前设置直接继续。之后可在“设置 → 系统与恢复”更改。') +
          (startup?.message && (!startup.success || startup.needsRepair) ? hint(e(startup.message)) : '') +
          (!startupKnown ? button('guide-refresh', '重新读取状态') : '');
        break;
      default:
        title = '准备好了';
        body = lesson('grid', '从工作空间开始', '首页管理应用，文件资源库整理文件，场景中心组合常用操作。') +
          lesson('mini', 'Mini、Dock 与托盘随时可用', `Mini 是桌边的快捷入口；在 Dock 所在屏幕边缘停留可展开 Dock${settings().hoverEnabled === false ? '（当前已关闭悬停，可在设置中开启）' : ''}。关闭工作空间后，托盘仍可打开设置、恢复图标或完整退出。`) +
          hint('引导已走完。已有导入、应用、收藏和设置会保留；以后可从设置重新引导。');
    }
    const progress = `<ol class="guide-progress" aria-label="引导进度">${steps.map((label, index) => `<li class="${index === step ? 'current' : index < step ? 'done' : ''}" ${index === step ? 'aria-current="step"' : ''}><span>${index < step ? icon('check', 12) : index + 1}</span>${label}</li>`).join('')}</ol>`;
    const status = feedback ? `<p class="guide-feedback ${feedbackError ? 'form-error' : ''}" role="${feedbackError ? 'alert' : 'status'}" aria-live="polite">${e(feedback)}</p>` : '';
    const footer = button('guide-skip', '跳过引导') + (step > 0 ? button('guide-back', '上一步', '') : '') +
      (step === steps.length - 1 ? button('guide-finish', '开始使用', 'primary') : button('guide-next', step === 0 ? '开始引导' : '下一步', 'primary'));
    H.showModal(title, `<div class="native-guide" data-guide-step="${step}" aria-busy="${busy}">${progress}<div class="guide-content">${body}</div>${status}</div>`, footer, { wide: true, locked: true });
  }

  function start() {
    if (active || !host() || H.ui.modal || H.ui.sceneDraftDirty) return false;
    active = true; step = 0; suspended = false; hidePolicy = ''; feedback = ''; feedbackError = false;
    renderGuide();
    return true;
  }
  function startIfNeeded() {
    if (!autoStarted && host()?.onboardingRequired === true && !settings().onboardingCompleted && start()) autoStarted = true;
  }
  async function mutate(type, payload, success) {
    if (busy || !active || suspended) return false;
    busy = true; feedback = ''; feedbackError = false; renderGuide();
    try {
      const reply = await bridge.request(type, payload);
      const next = reply?.state || reply;
      if (!next?.settings) throw new Error('未收到有效设置，请重试。');
      desktop.applyHostState(next);
      if (type.startsWith('system.') && type !== 'system.getState' && reply?.operation?.success !== true)
        throw new Error(reply?.operation?.message || reply?.operation?.error || '设置未生效，请重试。');
      if (type === 'settings.save' && next.settings.onboardingCompleted !== true)
        throw new Error('引导完成记录未保存，请重试。');
      feedback = success;
      return true;
    } catch (error) {
      feedback = error.message; feedbackError = true; return false;
    } finally { busy = false; renderGuide(); }
  }
  async function finish() {
    if (!await mutate('settings.save', { settings: { onboardingCompleted: true } }, '引导记录已保存。')) return;
    active = false; suspended = false; H.closeModal(); H.navigate('home');
    H.toast(step === steps.length - 1 ? '可以开始使用了。设置中可随时重新引导。' : '已跳过引导，可从设置重新开始。');
  }
  function resumeImport() {
    if (!active || !suspended || rescanning) return;
    const resources = window.DesktopHubResources?.getState();
    if (resources?.scan || resources?.preview) return;
    suspended = false;
    feedback = resources?.result?.sources?.length ? '来源已保存或保留，可以继续，也可以再添加一个目录。' : '尚未导入。可以重新选择来源，或直接进入下一步。';
    feedbackError = false; renderGuide();
  }
  // Existing preview dialogs keep their own cancel/confirm and time-tag consent flow.
  for (const action of ['nr-cancel', 'nr-confirm']) {
    const previous = H.actions[action];
    H.actions[action] = async (...args) => { const result = await previous(...args); resumeImport(); return result; };
  }
  const previousRescan = H.actions['nr-rescan'];
  H.actions['nr-rescan'] = async (...args) => {
    const guided = active && suspended;
    if (guided) rescanning = true;
    try { return await previousRescan(...args); }
    finally { if (guided) { rescanning = false; resumeImport(); } }
  };
  Object.assign(H.actions, {
    'settings-replay-onboarding': start,
    'replay-onboarding': start,
    'guide-next': () => { if (!active || busy || suspended) return; step = Math.min(steps.length - 1, step + 1); feedback = ''; feedbackError = false; renderGuide(); },
    'guide-back': () => { if (!active || busy || suspended) return; step = Math.max(0, step - 1); feedback = ''; feedbackError = false; renderGuide(); },
    'guide-skip': finish,
    'guide-finish': finish,
    'guide-import': async el => {
      if (!active || step !== 1 || busy || suspended || !['desktop', 'custom'].includes(el.dataset.kind)) return;
      suspended = true;
      try { await window.DesktopHubResources.previewSource(el.dataset.kind); }
      catch (error) { feedback = error.message; feedbackError = true; }
      resumeImport();
    },
    'guide-add-app': async () => {
      if (!active || step !== 2 || busy || suspended) return;
      busy = true; renderGuide();
      try {
        await H.actions['app-add']();
        const launch = desktop.getState().launch;
        feedback = launch?.message || '之后也可以在首页继续添加应用。'; feedbackError = launch?.status === 'error';
      } catch (error) { feedback = error.message; feedbackError = true; }
      finally { busy = false; renderGuide(); }
    },
    'guide-policy': el => {
      if (busy || !['restore', 'keepHidden'].includes(el.value)) return;
      hidePolicy = el.value;
      const hide = document.querySelector('[data-action="guide-icons"][data-visible="false"]');
      if (hide) hide.disabled = integration().desktopIcons?.supported !== true || typeof integration().desktopIcons?.visible !== 'boolean';
    },
    'guide-icons': el => {
      if (step !== 3 || busy || suspended) return;
      const icons = integration().desktopIcons, visible = el.dataset.visible === 'true';
      if (!icons?.supported || typeof icons.visible !== 'boolean' || (!visible && !icons.firstHideAuthorized && !hidePolicy)) return;
      return mutate('system.setDesktopIcons', { visible, ...(!visible && !icons.firstHideAuthorized ? { firstHidePolicy: hidePolicy } : {}) }, visible ? '桌面图标已显示。' : '桌面图标已隐藏。');
    },
    'guide-startup': el => {
      const startup = integration().startup;
      if (step !== 4 || typeof startup?.enabled !== 'boolean' || ['registry_unavailable', 'startup_entry_unsupported'].includes(startup.code)) return;
      const enabled = el.dataset.enabled === 'true';
      return mutate('system.setStartup', { enabled }, enabled ? '已开启当前用户开机自启。' : '已关闭当前用户开机自启。');
    },
    'guide-refresh': () => mutate('system.getState', {}, '已重新读取 Windows 状态。')
  });
  H.openOnboarding = start;
  const previousRender = H.render;
  H.render = function () { previousRender(); startIfNeeded(); };
  const previousNavigate = H.navigate;
  H.navigate = function (...args) { if (active) return; return previousNavigate(...args); };
  window.DesktopHubOnboarding = Object.freeze({ start, startIfNeeded, getState: () => ({ active, step, busy, suspended, feedback, feedbackError }) });
  document.addEventListener('DOMContentLoaded', startIfNeeded);
}());
