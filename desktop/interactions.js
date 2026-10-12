/* Shared settings controls, window resize edges and workspace navigation history. */
(function () {
  'use strict';
  const H = window.Hub, bridge = window.DesktopHubNative.bridge;
  if (window.DesktopHubDesktop.getState().surface !== 'workspace') return;
  const e = value => H.escape(String(value ?? ''));
  const previousRender = H.render, previousNavigate = H.navigate;
  const positions = new Map();
  let entries = [], index = -1, restoring = false, deferredIndex = null, lastSideNavigation = 0;
  const resourceState = () => window.DesktopHubResources?.navigationState();
  const routeKey = () => H.ui.page + (H.ui.page === 'folder' ? ':' + (resourceState()?.folder?.path || '') : '');
  const snapshot = () => ({ page: H.ui.page, key: routeKey(), resources: resourceState(), scroll: document.querySelector('.workspace-content')?.scrollTop || 0 });
  function checkpoint() {
    if (!restoring && entries[index]?.key === routeKey()) entries[index] = snapshot();
  }
  document.addEventListener('scroll', event => {
    if (event.target.matches?.('.workspace-content')) checkpoint();
  }, true);
  H.navigate = function (page) { checkpoint(); return previousNavigate(page); };
  function syncHistory() {
    if (restoring) return;
    const next = snapshot();
    if (entries[index]?.key === next.key) entries[index] = next;
    else { entries.splice(index + 1); entries.push(next); index = entries.length - 1; }
  }
  function restore(targetIndex) {
    const target = entries[targetIndex];
    if (!target || H.ui.modal?.locked) return;
    if (H.ui.page === 'editor' && H.ui.sceneDraftDirty && target.page !== 'editor') {
      deferredIndex = targetIndex;
      H.showModal('离开场景编辑器？', '<p>本次草稿尚未保存。可以继续编辑，或放弃修改后离开。</p>', '<button class="button" data-action="close-modal">继续编辑</button><button class="button primary" data-action="history-discard">放弃修改并离开</button>');
      return;
    }
    if (H.ui.modal) { H.closeModal(); return; }
    checkpoint(); restoring = true; index = targetIndex;
    try {
      H.navigate(target.page);
      if (target.resources) window.DesktopHubResources.restoreNavigation(target.resources);
      H.render();
      const content = document.querySelector('.workspace-content'); if (content) content.scrollTop = target.scroll;
    } finally { restoring = false; }
  }
  const navigateHistory = direction => restore(index + direction);
  H.actions['history-back'] = () => navigateHistory(-1);
  H.actions['history-forward'] = () => navigateHistory(1);
  H.actions['history-discard'] = () => {
    const target = deferredIndex; deferredIndex = null;
    H.ui.sceneDraftDirty = false; H.ui.sceneDraft = null; H.closeModal();
    if (target !== null) restore(target);
  };
  function enhanceSettings() {
    if (H.ui.page !== 'settings') return;
    document.querySelectorAll('.settings-content select').forEach((select, ordinal) => {
      const key = select.dataset.setting || select.getAttribute('aria-label') || `choice-${ordinal}`;
      const values = [...select.options];
      if (!values.length) return;
      const selected = Math.max(0, select.selectedIndex), old = positions.get(key);
      const motion = old && old.index !== selected ? { index: selected, from: old.index, started: Date.now() } : old || { index: selected, from: selected, started: 0 };
      positions.set(key, motion);
      const group = document.createElement('div');
      group.className = 'setting-segments'; group.dataset.choiceKey = key;
      group.setAttribute('role', 'radiogroup'); group.setAttribute('aria-label', select.getAttribute('aria-label') || key);
      group.style.setProperty('--count', values.length);
      group.innerHTML = `<span class="setting-slider" aria-hidden="true" style="transform:translateX(${selected * 100}%)"></span>${values.map((option, i) => `<button type="button" role="radio" aria-checked="${i === selected}" tabindex="${i === selected ? 0 : -1}" data-action="setting-choice" data-choice-key="${e(key)}" data-choice-value="${e(option.value)}" ${select.disabled || option.disabled ? 'disabled' : ''}>${e(option.textContent)}</button>`).join('')}`;
      select.hidden = true; select.after(group);
      const slider = group.querySelector('.setting-slider');
      if (motion.from !== selected && Date.now() - motion.started < 240 && !H.state.settings.reducedMotion && !window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
        const animation = slider.animate([{ transform: `translateX(${motion.from * 100}%)` }, { transform: `translateX(${selected * 100}%)` }], { duration: 240, easing: 'cubic-bezier(.2,.8,.2,1)' });
        animation.currentTime = Date.now() - motion.started;
      }
      group._select = select;
    });
  }
  H.actions['setting-choice'] = el => {
    const select = el.closest('.setting-segments')?._select;
    if (!select || select.disabled) return;
    select.value = el.dataset.choiceValue;
    H.actions[select.dataset.change]?.(select, { type: 'change' });
  };
  H.render = function () {
    const active = document.activeElement;
    const focusChoice = active?.dataset?.choiceKey ? { key: active.dataset.choiceKey, value: active.dataset.choiceValue } : null;
    previousRender(); syncHistory(); enhanceSettings();
    const header = document.querySelector('.workspace-header .brand');
    header?.insertAdjacentHTML('afterend', `<div class="workspace-history" aria-label="浏览历史"><button class="icon-button small" data-action="history-back" aria-label="上一步" title="上一步（鼠标后退侧键 / Alt+←）" ${index <= 0 ? 'disabled' : ''}>${H.icon('arrow-left', 18)}</button><button class="icon-button small" data-action="history-forward" aria-label="下一步" title="下一步（鼠标前进侧键 / Alt+→）" ${index + 1 >= entries.length ? 'disabled' : ''}>${H.icon('arrow-right', 18)}</button></div>`);
    document.querySelector('.workspace')?.insertAdjacentHTML('beforeend', ['left','right','top','bottom','top-left','top-right','bottom-left','bottom-right'].map(edge => `<div class="workspace-resize resize-${edge}" data-resize-edge="${edge}" aria-hidden="true" title="拖动调整窗口大小"></div>`).join(''));
    if (focusChoice) [...document.querySelectorAll('[data-action="setting-choice"]')].find(button => button.dataset.choiceKey === focusChoice.key && button.dataset.choiceValue === focusChoice.value)?.focus({ preventScroll: true });
  };
  document.addEventListener('pointerdown', event => {
    if (event.button === 3 || event.button === 4) { event.preventDefault(); return; }
    const edge = event.target.closest('[data-resize-edge]');
    if (edge && event.button === 0) {
      event.preventDefault(); event.stopImmediatePropagation();
      void bridge.request('window.beginResize', { edge: edge.dataset.resizeEdge }).catch(error => H.toast(error.message, 'error'));
    }
  }, true);
  function sideNavigate(direction) {
    const now = Date.now(); if (now - lastSideNavigation < 150) return;
    lastSideNavigation = now; navigateHistory(direction);
  }
  document.addEventListener('pointerup', event => {
    if (event.button !== 3 && event.button !== 4) return;
    event.preventDefault(); event.stopImmediatePropagation(); sideNavigate(event.button === 3 ? -1 : 1);
  }, true);
  document.addEventListener('auxclick', event => { if (event.button === 3 || event.button === 4) event.preventDefault(); }, true);
  document.addEventListener('keydown', event => {
    if (event.altKey && ['ArrowLeft','ArrowRight'].includes(event.key)) {
      event.preventDefault(); navigateHistory(event.key === 'ArrowLeft' ? -1 : 1); return;
    }
    const group = event.target.closest('.setting-segments');
    if (!group || !['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','Home','End'].includes(event.key)) return;
    const buttons = [...group.querySelectorAll('button:not(:disabled)')];
    const from = buttons.indexOf(event.target); if (from < 0) return;
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1 : (from + (['ArrowLeft','ArrowUp'].includes(event.key) ? -1 : 1) + buttons.length) % buttons.length;
    event.preventDefault(); buttons[next].focus(); H.actions['setting-choice'](buttons[next]);
  });
  bridge.on('window.historyNavigate', ({ direction }) => sideNavigate(direction));
  window.DesktopHubInteractions = Object.freeze({ checkpoint, navigateHistory, getState: () => ({ index, entries }) });
}());
