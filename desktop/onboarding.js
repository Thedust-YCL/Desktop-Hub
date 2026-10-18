/* Native first-use guide. Preferences stay in settings.json; real actions use the existing bridges. */
(function () {
  'use strict';
  const t = window.DesktopHubI18n?.text || ((value, ...args) => Array.isArray(value) ? value.reduce((out, part, i) => out + part + (i < args.length ? args[i] : ''), '') : value);
  const H = window.Hub, desktop = window.DesktopHubDesktop, bridge = window.DesktopHubNative.bridge;
  if (desktop.getState().surface !== 'workspace') return;
  const e = value => H.escape(String(value ?? '')), icon = (name, size = 20) => H.icon(name, size);
  const steps = ['欢迎', '添加应用', '固定入口', '可选文件', '常驻设置', '完成'];
  let active = false, step = 0, busy = false, suspended = false, autoStarted = false, rescanning = false;
  let suspendedKind = '';
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
        title = t('欢迎使用 Desktop Hub');
        body = t`<div class="guide-welcome"><span class="guide-mark">${icon('grid', 36)}</span><h3>把常用内容，放在顺手的位置</h3><p>先添加并试用常用应用，再选择固定入口和常驻设置。</p></div><div class="guide-overview"><span>${icon('app', 18)} 添加应用</span><span>${icon('dock', 18)} 快速打开</span><span>${icon('settings', 18)} 常驻设置</span></div>${hint(t('随时可以跳过。之后在“设置 → 系统与恢复 → 使用帮助”重新引导。'))}`;
        break;
      case 1:
        title = t('添加并尝试启动应用');
        body = lesson('app', t('先添加常用软件'), t('从桌面、开始菜单或多选 EXE / Windows 快捷方式，预览后确认添加。也可将程序拖入工作空间。'), button('guide-add-app',t('添加应用…'),'primary')) +
          `<div class="guide-app-list">${(settings().appEntries || []).map(app => `<div><span>${e(app.name)}</span>${button('guide-launch',t('尝试启动'),'quiet',`data-id="${e(app.id)}"`)}</div>`).join('')}</div>` + hint(t('只有点击“尝试启动”才会启动程序；可以跳过，之后在首页继续。'));
        break;
      case 2:
        title = t('固定常用入口');
        body = lesson('pin', t('分别选择 Mini 与 Dock 的入口'), t('固定只添加启动入口，不会改变收藏。Mini 最多放 4 个应用或场景；顺序由你选择。'), `<div class="guide-actions">${button('guide-pins',t('选择 Mini 入口…'),'primary','data-surface="mini"')}${button('guide-pins',t('选择 Dock 入口…'),'quiet','data-surface="dock"')}</div>`) +
          hint(t('已有入口和收藏保持原样。可以先跳过，之后在首页“固定入口”调整。')) +
          t`<div class="guide-shortcuts"><span><kbd>${e(settings().workspaceHotkey || 'Ctrl+Alt+W')}</kbd> 打开工作空间</span><span><kbd>${e(settings().dockHotkey || 'Ctrl+Alt+D')}</kbd> 展开 Dock</span></div>`;
        break;
      case 3:
        title = t('可选：导入文件来源');
        body = lesson('folder', t('选择桌面或常用目录'), t('先查看扫描预览，确认导入后才建立索引。文件留在原处；默认只导入目录的直接子项。')) +
          `<div class="guide-import-actions">${button('guide-import', t`${icon('monitor', 18)} 查看桌面预览`, 'primary', 'data-kind="desktop"')}${button('guide-import', t`${icon('folder', 18)} 选择目录…`, 'quiet', 'data-kind="custom"')}</div>` +
          lesson('tag', t('按需要整理'), t('确认导入时可以选择是否按修改年月自动添加时间标签；之后也能手动添加标签和收藏。')) +
          hint(t('已有来源会保留。取消扫描会回到这里，也可以先跳过导入，稍后在文件资源库添加。'));
        break;
      case 4:
        title = t('常驻与桌面设置');
        body = lesson('monitor', iconsKnown ? t`桌面图标当前${icons.visible ? t('显示') : t('隐藏')}` : t('暂时无法读取桌面图标状态'), t('隐藏后，桌面会更简洁；文件仍可从资源库和资源管理器打开。只改变图标整体可见性，原文件不受影响。')) +
          (!icons?.firstHideAuthorized ? t`<fieldset class="guide-policy"><legend>如果选择隐藏，完整退出后如何处理？</legend><label class="check-row"><input type="radio" name="guide-policy" value="restore" data-change="guide-policy" ${hidePolicy === 'restore' ? 'checked' : ''} ${busy ? 'disabled' : ''}><span>恢复隐藏前的图标状态</span></label><label class="check-row"><input type="radio" name="guide-policy" value="keepHidden" data-change="guide-policy" ${hidePolicy === 'keepHidden' ? 'checked' : ''} ${busy ? 'disabled' : ''}><span>保持隐藏，由我手动恢复</span></label></fieldset>` : hint(t`完整退出后的策略：${icons.exitPolicy === 'keepHidden' ? t('保持隐藏') : t('恢复隐藏前的图标状态')}。可在系统设置中更改。`)) +
          `<div class="guide-actions">${button('guide-icons', t('显示桌面图标'), 'quiet', `data-visible="true" ${!iconsKnown ? 'disabled' : ''}`)}${button('guide-icons', t('隐藏桌面图标'), 'quiet', `data-visible="false" ${!iconsKnown || (!icons?.firstHideAuthorized && !hidePolicy) ? 'disabled' : ''}`)}</div>` +
          hint(t`随时恢复：<strong>桌面空白处右键 → 查看 → 显示桌面图标</strong>。关闭工作空间会继续常驻；完整退出请使用托盘菜单。`) +
          (icons?.message && (!icons.success || icons.conflict) ? hint(e(icons.message)) : '') +
          (!iconsKnown ? button('guide-refresh', t('重新读取状态')) : '');
        body += lesson('settings', startupKnown ? t`开机自启当前${startup.enabled ? t('开启') : t('关闭')}` : t('暂时无法读取启动项状态'), t('开启后，在当前用户登录 Windows 时安静常驻。不会自动打开工作空间，也不会自动执行场景。')) +
          `<div class="guide-actions">${button('guide-startup', startup?.needsRepair ? t('按当前位置修复自启') : t('开启开机自启'), 'quiet', `data-enabled="true" ${!startupKnown ? 'disabled' : ''}`)}${button('guide-startup', t('关闭开机自启'), 'quiet', `data-enabled="false" ${!startupKnown ? 'disabled' : ''}`)}</div>` +
          hint(t('这一步按你的需要选择，也可以保持当前设置直接继续。之后可在“设置 → 系统与恢复”更改。')) +
          (startup?.message && (!startup.success || startup.needsRepair) ? hint(e(startup.message)) : '') +
          (!startupKnown ? button('guide-refresh', t('重新读取状态')) : '');
        break;
      default:
        title = t('准备好了');
        body = lesson('grid', t('从工作空间开始'), t('首页管理应用，文件资源库整理文件，场景中心组合常用操作。')) +
          lesson('mini', t('Mini、Dock 与托盘随时可用'), t`Mini 是桌边的快捷入口；在 Dock 所在屏幕边缘停留可展开 Dock${settings().hoverEnabled === false ? t('（当前已关闭悬停，可在设置中开启）') : ''}。关闭工作空间后，托盘仍可打开设置、恢复图标或完整退出。`) +
          hint(t('引导已走完。已有导入、应用、收藏和设置会保留；以后可从设置重新引导。'));
    }
    const progress = t`<ol class="guide-progress" aria-label="引导进度">${steps.map((label, index) => `<li class="${index === step ? 'current' : index < step ? 'done' : ''}" ${index === step ? 'aria-current="step"' : ''}><span>${index < step ? icon('check', 12) : index + 1}</span>${t(label)}</li>`).join('')}</ol>`;
    const status = feedback ? `<p class="guide-feedback ${feedbackError ? 'form-error' : ''}" role="${feedbackError ? 'alert' : 'status'}" aria-live="polite">${e(feedback)}</p>` : '';
    const footer = button('guide-skip', t('跳过引导')) + (step > 0 ? button('guide-back', t('上一步'), '') : '') +
      (step === steps.length - 1 ? button('guide-finish', t('开始使用'), 'primary') : button('guide-next', step === 0 ? t('开始引导') : t('下一步'), 'primary'));
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
      if (!next?.settings) throw new Error(t('未收到有效设置，请重试。'));
      desktop.applyHostState(next);
      if (type.startsWith('system.') && type !== 'system.getState' && reply?.operation?.success !== true)
        throw new Error(reply?.operation?.message || reply?.operation?.error || t('设置未生效，请重试。'));
      if (type === 'settings.save' && next.settings.onboardingCompleted !== true)
        throw new Error(t('引导完成记录未保存，请重试。'));
      feedback = success;
      return true;
    } catch (error) {
      feedback = error.message; feedbackError = true; return false;
    } finally { busy = false; renderGuide(); }
  }
  async function finish() {
    if (!await mutate('settings.save', { settings: { onboardingCompleted: true } }, t('引导记录已保存。'))) return;
    active = false; suspended = false; H.closeModal(); H.navigate('home');
    H.toast(step === steps.length - 1 ? t('可以开始使用了。设置中可随时重新引导。') : t('已跳过引导，可从设置重新开始。'));
  }
  function resumeImport() {
    if (!active || !suspended || rescanning || busy || H.ui.modal) return;
    const resources = window.DesktopHubResources?.getState();
    if (suspendedKind === 'resources' && (resources?.scan || resources?.preview)) return;
    if (suspendedKind === 'apps' && desktop.isImporting?.()) return;
    suspended = false;
    feedback = suspendedKind === 'apps' ? desktop.getState().launch?.message || t('应用保持已保存的状态。可以继续添加、尝试启动或进入下一步。') : suspendedKind === 'pins' ? t('固定入口保持已保存的选择。') : t('文件来源保持已保存的状态。可以继续，也可以跳过。');
    suspendedKind = ''; feedbackError = false; renderGuide();
  }
  const closeModal = H.closeModal;
  H.closeModal = function (...args) { const result = closeModal.apply(H,args); if (active && suspended) Promise.resolve().then(resumeImport); return result; };
  async function suspendFor(kind, action) {
    suspended = true; suspendedKind = kind; H.closeModal();
    try { await action(); }
    catch (error) { feedback = error.message; feedbackError = true; }
    resumeImport();
  }
  // Existing preview dialogs keep their own cancel/confirm and time-tag consent flow.
  for (const action of ['nr-cancel', 'nr-confirm', 'app-import-confirm', 'launcher-pins-save', 'close-modal']) {
    const previous = H.actions[action];
    H.actions[action] = async (...args) => { const result = await previous?.(...args); resumeImport(); return result; };
  }
  const previousRescan = H.actions['nr-rescan'];
  H.actions['nr-rescan'] = async (...args) => {
    const guided = active && suspended;
    if (guided) rescanning = true;
    try { return await previousRescan?.(...args); }
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
      if (!active || step !== 3 || busy || suspended || !['desktop', 'custom'].includes(el.dataset.kind)) return;
      await suspendFor('resources',() => window.DesktopHubResources.previewSource(el.dataset.kind));
    },
    'guide-add-app': async () => {
      if (!active || step !== 1 || busy || suspended) return;
      await suspendFor('apps',() => H.actions['app-add']());
    },
    'guide-pins': async el => {
      if (!active || step !== 2 || busy || suspended || !['mini','dock'].includes(el.dataset.surface)) return;
      await suspendFor('pins',() => H.actions['launcher-manage'](el));
    },
    'guide-launch': async el => {
      if (!active || step !== 1 || busy || suspended || !settings().appEntries?.some(app => app.id === el.dataset.id)) return;
      busy = true; renderGuide();
      try { const result = await H.actions['app-launch'](el); feedback = result?.message || t('请查看应用启动结果。'); feedbackError = result?.success !== true; }
      catch (error) { feedback = error.message; feedbackError = true; }
      finally { busy = false; renderGuide(); }
    },
    'guide-policy': el => {
      if (busy || !['restore', 'keepHidden'].includes(el.value)) return;
      hidePolicy = el.value;
      const hide = document.querySelector('[data-action="guide-icons"][data-visible="false"]');
      if (hide) hide.disabled = integration().desktopIcons?.supported !== true || typeof integration().desktopIcons?.visible !== 'boolean';
    },
    'guide-icons': el => {
      if (step !== 4 || busy || suspended) return;
      const icons = integration().desktopIcons, visible = el.dataset.visible === 'true';
      if (!icons?.supported || typeof icons.visible !== 'boolean' || (!visible && !icons.firstHideAuthorized && !hidePolicy)) return;
      return mutate('system.setDesktopIcons', { visible, ...(!visible && !icons.firstHideAuthorized ? { firstHidePolicy: hidePolicy } : {}) }, visible ? t('桌面图标已显示。') : t('桌面图标已隐藏。'));
    },
    'guide-startup': el => {
      const startup = integration().startup;
      if (step !== 4 || typeof startup?.enabled !== 'boolean' || ['registry_unavailable', 'startup_entry_unsupported'].includes(startup.code)) return;
      const enabled = el.dataset.enabled === 'true';
      return mutate('system.setStartup', { enabled }, enabled ? t('已开启当前用户开机自启。') : t('已关闭当前用户开机自启。'));
    },
    'guide-refresh': () => mutate('system.getState', {}, t('已重新读取 Windows 状态。'))
  });
  H.openOnboarding = start;
  const previousRender = H.render;
  H.render = function () { previousRender(); startIfNeeded(); };
  const previousNavigate = H.navigate;
  H.navigate = function (...args) { if (active) return; return previousNavigate(...args); };
  window.DesktopHubOnboarding = Object.freeze({ start, startIfNeeded, getState: () => ({ active, step, busy, suspended, feedback, feedbackError }) });
  document.addEventListener('DOMContentLoaded', startIfNeeded);
}());
