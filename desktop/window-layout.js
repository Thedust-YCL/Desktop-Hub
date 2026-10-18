/* Window layout is an explicit workspace flow. The host owns all window authority. */
(function () {
  'use strict';
  const t = window.DesktopHubI18n?.text || ((value, ...args) => Array.isArray(value) ? value.reduce((out, part, i) => out + part + (i < args.length ? args[i] : ''), '') : value);
  const H = window.Hub, desktop = window.DesktopHubDesktop, bridge = window.DesktopHubNative.bridge;
  const e = value => H.escape(value), isWorkspace = () => desktop.getState().surface === 'workspace';
  let epoch = 0, busy = false, current, results = [], localOrder = 0;
  const showModal = H.showModal, closeModal = H.closeModal, navigate = H.navigate;
  H.showModal = function (...args) { epoch++; return showModal.apply(this, args); };
  H.closeModal = function (...args) { epoch++; current = undefined; return closeModal.apply(this, args); };
  if (navigate) H.navigate = function (...args) { epoch++; current = undefined; return navigate.apply(this, args); };
  const ownership = () => ({ epoch, modal: H.ui.modal, page: H.ui.page });
  const owns = owner => owner.epoch === epoch && owner.modal === H.ui.modal && owner.page === H.ui.page;
  const settings = () => desktop.getState().hostState?.settings;
  const appName = id => settings()?.appEntries?.find(app => app.id === id)?.name || t('已移除的应用');
  const button = (action, text, attrs = '', primary = false) => `<button class="button${primary ? ' primary' : ''}" data-action="${action}" ${attrs}>${text}</button>`;
  const labels = { restored: '已恢复', skipped: '已跳过', notFound: '未找到窗口', needsChoice: '需要选择', failed: '恢复失败', cancelled: '已取消' };
  const skipText = code => ({ fullscreen: t('全屏窗口已跳过'), elevated: t('更高权限窗口已跳过'), minimized: t('请先还原此窗口，再重新捕获'), monitorUnavailable: t('无法读取显示器，请重新捕获'), accessDenied: t('无法确认窗口权限') }[code] || t('此窗口当前不可调整'));
  function allowed() {
    if (!isWorkspace() || busy) return false;
    if (H.ui.modal) { H.toast(t('请先完成或关闭当前编辑窗口。')); return false; }
    return true;
  }
  async function capture(sceneId) {
    if (!allowed()) return;
    const owner = ownership(); busy = true;
    try {
      const preview = await bridge.request('scenes.layoutCapture', { sceneId });
      if (!owns(owner)) return;
      if (!preview?.captureToken || !Array.isArray(preview.candidates)) throw new Error(t('未收到窗口列表，请重新捕获。'));
      const saved = settings()?.scenes?.find(scene => scene.id === sceneId)?.windowLayout;
      const state = { sceneId, preview, selections: new Map(), restoreAfterRun: saved?.restoreAfterRun === true, waitTimeoutMs: saved?.waitTimeoutMs || 15000 };
      H.showModal(t('保存当前窗口布局'), captureMarkup(state), captureFooter(state));
      state.owner = ownership(); current = state;
    } catch (error) { if (owns(owner)) H.toast(error.message, 'error'); }
    finally { busy = false; }
  }
  function captureMarkup(state) {
    const rows = state.preview.candidates.map(candidate => {
      const ids = candidate.appIds || [], eligible = !candidate.skipReason;
      state.selections.set(candidate.candidateToken, { checked: false, appEntryId: ids.length === 1 ? ids[0] : '', titleContains: candidate.title.slice(0, 200) });
      const token = e(candidate.candidateToken), bounds = candidate.bounds || {};
      return `<div class="layout-candidate"><label class="layout-check"><input type="checkbox" data-change="layout-pick" data-token="${token}" ${eligible ? '' : 'disabled'}><strong>${e(candidate.title || t('无标题窗口'))}</strong></label><div class="layout-details">${candidate.skipReason ? `<p>${e(skipText(candidate.skipReason))}</p>` : t`<label>关联应用<select data-change="layout-app" data-token="${token}" aria-label="关联应用">${ids.length > 1 ? t('<option value="">请选择应用</option>') : ''}${ids.map(id => `<option value="${e(id)}">${e(appName(id))}</option>`).join('')}</select></label><label>标题包含<input maxlength="200" value="${e(candidate.title.slice(0, 200))}" data-change="layout-title" data-token="${token}" aria-label="标题包含"></label><p>${Math.round(bounds.width || 0)} × ${Math.round(bounds.height || 0)} · ${candidate.state === 'maximized' ? t('最大化') : t('普通窗口')}</p>`}</div></div>`;
    }).join('');
    return t`<div class="layout-dialog"><p>选择要保存位置的窗口。标题经常变化时，可清空标题条件。</p><div class="layout-candidates">${rows || t('<p role="status">没有匹配的可见窗口。请先打开此场景中已登记的应用，再重新捕获。</p>')}</div>${state.preview.truncated ? t('<p role="status">只显示前 128 个窗口。请关闭不需要的窗口后重新捕获。</p>') : ''}<div class="layout-options"><label class="layout-check"><input type="checkbox" data-change="layout-after-run" ${state.restoreAfterRun ? 'checked' : ''}>运行场景后恢复布局</label><label>等待窗口（秒）<input type="number" min="1" max="600" step="1" value="${Math.ceil(state.waitTimeoutMs / 1000)}" data-change="layout-timeout"></label></div><p>最多保存 32 个窗口；保存空列表会清除此场景的布局。</p></div>`;
  }
  const captureFooter = state => button('layout-recapture', t('重新捕获'), `data-id="${e(state.sceneId)}"`) + button('close-modal', t('取消')) + button('layout-save', t('保存布局'), '', true);
  function resultMarkup(result) {
    return t`<div class="layout-dialog"><p>窗口布局结果与应用启动结果分别记录。</p><div class="layout-candidates">${(result.entries || []).map(entry => `<div class="layout-result"><strong>${e(appName(entry.appEntryId))}</strong><span class="layout-status">${e(t(labels[entry.status]) || t('未完成'))}</span><p>${e(entry.message || t('请重新恢复或捕获布局。'))}</p>${entry.status === 'needsChoice' ? t`<label>选择窗口<select data-change="layout-choice" data-entry="${e(entry.entryToken)}"><option value="">跳过此项</option>${(entry.candidates || []).map(candidate => `<option value="${e(candidate.candidateToken)}">${e(candidate.title || t('无标题窗口'))}</option>`).join('')}</select></label>` : ''}</div>`).join('') || t('<p>此场景尚未保存窗口布局。</p>')}</div><p>窗口列表已过期或应用目标已修改时，请重新捕获布局。</p></div>`;
  }
  function openResult(result) {
    if (!isWorkspace() || H.ui.modal || busy || !result) return;
    H.showModal(t('窗口布局结果'), resultMarkup(result), button('close-modal', t('关闭')) + button('layout-recapture', t('重新捕获'), `data-id="${e(result.sceneId)}"`) + (result.restoreToken && result.entries?.some(item => item.status === 'needsChoice') ? button('layout-choose', t('恢复所选窗口'), '', true) : ''));
    current = { result, choices: new Map(), owner: ownership() };
  }
  function remember(item, fallbackKind = 'automatic', previous) {
    const result = item?.result || item?.layout || item;
    if (!result?.sceneId || !Array.isArray(result.entries)) return;
    const executionId = item.executionId || result.executionId;
    const kind = item.kind || result.kind || (executionId ? 'automatic' : fallbackKind);
    const identity = item.resultId || result.resultId || previous?.resultId || (executionId ? `execution:${executionId}` : `${kind}:${result.sceneId}:${result.restoreToken || result.entries.map(entry => entry.entryToken || '').join(',')}`);
    const existing = results.find(row => row.resultId === identity);
    const suppliedOrder = item.order || result.order;
    const order = Number.isSafeInteger(suppliedOrder) && suppliedOrder > 0 ? suppliedOrder : existing?.order || ++localOrder;
    localOrder = Math.max(localOrder, order);
    if (existing && existing.order > order) return;
    results = results.filter(row => row.resultId !== identity);
    results.push({ resultId: identity, order, kind, result });
    results.sort((a, b) => a.order - b.order);
    for (const [retainedKind, maximum] of [['automatic', 32], ['manual', 8]]) {
      while (results.filter(row => row.kind === retainedKind).length > maximum)
        results.splice(results.findIndex(row => row.kind === retainedKind), 1);
    }
  }
  function receiveResults(incoming) {
    if (!Array.isArray(incoming)) return;
    // Host history and direct request replies can arrive in either order. Merge exact identities;
    // an automatic-only or older state snapshot must not erase a completed manual outcome.
    incoming.forEach(item => remember(item));
  }
  async function restore(sceneId) {
    if (!allowed()) return; const owner = ownership(); busy = true;
    try {
      const result = await bridge.request('scenes.layoutRestore', { sceneId }, { timeoutMs: 610000 });
      remember(result, 'manual');
      if (owns(owner)) { busy = false; openResult(result); }
    } catch (error) { if (owns(owner)) H.toast(error.message, 'error'); }
    finally { busy = false; }
  }
  function active() { return current && owns(current.owner) && !busy ? current : undefined; }
  Object.assign(H.actions, {
    'layout-capture': element => capture(element.dataset.id),
    'layout-restore': element => restore(element.dataset.id),
    'layout-results': element => { const row = [...results].reverse().find(item => item.result.sceneId === element.dataset.id); if (row) openResult(row.result); else H.toast(t('还没有窗口布局结果。')); },
    'layout-recapture': element => { if (busy) return; H.closeModal(); return capture(element.dataset.id); },
    'layout-pick': element => { const selection = active()?.selections?.get(element.dataset.token); if (selection) selection.checked = element.checked; },
    'layout-title': element => { const selection = active()?.selections?.get(element.dataset.token); if (selection) selection.titleContains = element.value; },
    'layout-app': element => { const selection = active()?.selections?.get(element.dataset.token); if (selection) selection.appEntryId = element.value; },
    'layout-after-run': element => { const state = active(); if (state) state.restoreAfterRun = element.checked; },
    'layout-timeout': element => { const state = active(); if (state) state.waitTimeoutMs = Number(element.value) * 1000; },
    'layout-choice': element => { const state = active(); if (state?.choices) state.choices.set(element.dataset.entry, element.value); },
    'layout-save': async () => {
      const state = active(); if (!state?.preview) return;
      const selections = [...state.selections].filter(([, value]) => value.checked).map(([candidateToken, value]) => ({ candidateToken, appEntryId: value.appEntryId, titleContains: value.titleContains }));
      if (selections.length > 32) { H.toast(t('一次最多保存 32 个窗口。'), 'error'); return; }
      if (selections.some(selection => !selection.appEntryId)) { H.toast(t('请为所选窗口选择关联应用。'), 'error'); return; }
      if (!Number.isInteger(state.waitTimeoutMs) || state.waitTimeoutMs < 1 || state.waitTimeoutMs > 600000) { H.toast(t('等待时间须在 1 至 600 秒之间。'), 'error'); return; }
      busy = true;
      try {
        const result = await bridge.request('scenes.layoutSave', { sceneId: state.sceneId, captureToken: state.preview.captureToken, selections, restoreAfterRun: state.restoreAfterRun, waitTimeoutMs: state.waitTimeoutMs });
        if (result?.settings) desktop.applyHostState(result);
        if (owns(state.owner)) { H.closeModal(); H.toast(t('窗口布局已保存。')); H.render(); }
      } catch (error) { if (owns(state.owner)) H.toast(error.message + t(' 请重新捕获后重试。'), 'error'); }
      finally { busy = false; }
    },
    'layout-choose': async () => {
      const state = active(); if (!state?.result?.restoreToken) return;
      const choices = [...state.choices].filter(([, value]) => value).map(([entryToken, candidateToken]) => ({ entryToken, candidateToken }));
      busy = true;
      try {
        const result = await bridge.request('scenes.layoutChoose', { restoreToken: state.result.restoreToken, choices });
        remember(result, state.result.kind || 'manual', results.find(row => row.result === state.result));
        if (owns(state.owner)) { H.closeModal(); busy = false; openResult(result); }
      } catch (error) { if (owns(state.owner)) H.toast(error.message + t(' 请重新恢复布局。'), 'error'); }
      finally { busy = false; }
    }
  });
  const controls = sceneId => isWorkspace() ? t`<div class="layout-controls" aria-label="窗口布局">${button('layout-capture', t('保存当前布局'), `data-id="${e(sceneId)}"`)}${button('layout-restore', t('恢复窗口布局'), `data-id="${e(sceneId)}"`)}${button('layout-results', t('布局结果'), `data-id="${e(sceneId)}"`)}</div>` : '';
  window.DesktopHubWindowLayout = { capture, restore, openResult, receiveResults, controls, getState: () => ({ busy, modalOpen: !!current && owns(current.owner), resultCount: results.length }) };
  bridge.on?.('scenes.layoutResults', payload => receiveResults(payload?.layoutResults || payload));
  receiveResults(desktop.getState().hostState?.layoutResults);
})();



