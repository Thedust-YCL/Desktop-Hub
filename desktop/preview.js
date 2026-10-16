/* Authorized stable IDs in; bounded PNG content out. Never load file URLs. */
(function () {
  'use strict';
  const H = window.Hub, bridge = window.DesktopHubNative.bridge;
  if (window.DesktopHubDesktop.getState().surface !== 'workspace') return;
  const escape = value => H.escape(String(value ?? ''));
  let selection = null, focusReturn = null, dialog = null, current = null, generation = 0, observer = null, paused = false;
  const thumbnails = new Map();
  const thumbFallback = new WeakMap();
  let selectionContext = '', openedContext = '';
  function contextKey() {
    const state = window.DesktopHubResources?.getState() || {};
    const resource = state.result?.items?.find(item => item.resource.id === selection?.id)?.resource;
    return JSON.stringify([H.ui.page, state.query, state.tagIds, state.view, state.page, state.status, state.category, state.extension, state.sort, state.filterId, state.favoritesOnly,
      selection?.id, resource?.canonicalPath, resource?.modificationTime, resource?.status, resource?.filesystemIdentity, (state.selectedIds?.length || 0) > 1]);
  }
  const cancel = id => { void bridge.request('resources.previewCancel', { requestId: id }).catch(() => {}); };
  // The bridge timeout ends only its promise; native work still owns its decoder slot.
  const cancelTimedOut = (error, id) => { if (error?.code === 'TIMEOUT') cancel(id); };
  const editable = target => !!target?.closest?.('input,textarea,select,[contenteditable]:not([contenteditable="false"]),[role="textbox"]');
  const controlledImage = response => typeof response?.png === 'string' && response.png.length <= 22369624 && /^iVBORw0KGgo/.test(response.png) && Number.isInteger(response.width) && response.width > 0 && response.width <= 1600 && Number.isInteger(response.height) && response.height > 0 && response.height <= 1600;
  function stopThumbnails() {
    observer?.disconnect(); observer = null;
    for (const [id] of thumbnails) cancel(id);
    for (const node of document.querySelectorAll('[data-preview-thumb]')) { delete node.dataset.previewVisible; if (thumbFallback.has(node)) node.innerHTML = thumbFallback.get(node); }
  }
  function close(restore = true, restart = true) {
    if (!dialog) return false;
    ++generation; if (current) cancel(current); current = null;
    dialog.remove(); dialog = null;
    if (restore && focusReturn?.isConnected) focusReturn.focus();
    if (restart && !paused) refresh();
    return true;
  }
  function select(resource, element) {
    if (!resource?.id) return;
    if (selection?.id !== resource.id) close(false);
    selection = { id: resource.id, displayName: resource.displayName || '', extension: resource.extension || '' }; focusReturn = element;
    selectionContext = contextKey();
    for (const node of document.querySelectorAll('.nr-resource[data-resource-id]')) { const chosen = node.dataset.resourceId === resource.id; node.classList.toggle('nr-preview-selected', chosen); node.setAttribute?.('aria-selected', String(chosen)); }
  }
  function paint(body) {
    if (!dialog) return;
    const focused = dialog.contains?.(document.activeElement);
    dialog.innerHTML = `<section class="rp-panel" role="dialog" aria-modal="true" aria-label="资源预览"><header><div><strong>${escape(selection.displayName)}</strong><small>内容预览 · <kbd>Esc</kbd> 返回</small></div><button class="icon-button" data-preview-close aria-label="关闭预览">×</button></header><div class="rp-content" aria-live="polite">${body}</div><footer><button class="button" data-preview-external>外部打开</button><button class="button quiet" data-preview-close>关闭</button></footer></section>`;
    for (const button of dialog.querySelectorAll('[data-preview-close]')) button.addEventListener('click', () => close());
    dialog.querySelector('[data-preview-external]')?.addEventListener('click', () => { const id = selection.id; close(); void H.actions['nr-open']({ dataset: { id, mode: 'open' } }); });
    if (focused) dialog.querySelector('[data-preview-close]')?.focus();
  }
  async function open() {
    if (paused || !selection || H.ui.modal || (window.DesktopHubResources?.getState().selectedIds?.length || 0) > 1) return;
    close(false); stopThumbnails();
    const revision = ++generation, id = window.crypto.randomUUID(); current = id;
    openedContext = contextKey();
    dialog = document.createElement('div'); dialog.className = 'rp-overlay'; document.body.appendChild(dialog);
    paint('<p class="muted">正在生成预览…</p>'); dialog.querySelector('[data-preview-close]')?.focus();
    try {
      const response = await bridge.request('resources.previewContent', { resourceId: selection.id, thumbnail: false, requestId: id }, { timeoutMs: 120000 });
      if (revision !== generation || current !== id || !dialog || paused) return;
      if (!controlledImage(response)) throw new Error('预览内容无效，可在外部应用打开。');
      paint(`<img class="rp-image" src="data:image/png;base64,${response.png}" alt="${escape(selection.displayName)} 内容预览"><p class="rp-meta">${escape(response.detail)} · ${response.width} × ${response.height}</p>`);
    } catch (error) { cancelTimedOut(error, id); if (revision === generation && current === id && dialog) paint(`<div class="rp-error" role="status"><strong>暂不能预览</strong><p>${escape(error.message)}</p><p class="muted">文件仍留在原处。可外部打开，或关闭后刷新 / 重新关联。</p></div>`); }
    finally { if (current === id) current = null; }
  }
  function pump() {
    if (paused || dialog || thumbnails.size >= 2) return;
    // Do not enqueue a page: choose only currently intersecting, unrequested nodes when a slot is free.
    for (const node of document.querySelectorAll('[data-preview-thumb][data-preview-visible="true"]')) {
      if (thumbnails.size >= 2) break;
      if (node.dataset.previewRequested || !node.isConnected) continue;
      const id = window.crypto.randomUUID(), revision = generation; node.dataset.previewRequested = 'true'; thumbnails.set(id, node);
      void bridge.request('resources.previewContent', { resourceId: node.dataset.previewThumb, thumbnail: true, requestId: id }, { timeoutMs: 120000 }).then(response => {
        if (revision !== generation || paused || dialog || !node.isConnected || node.dataset.previewVisible !== 'true' || !controlledImage(response)) return;
        const image = document.createElement('img'); image.className = 'rp-thumbnail'; image.alt = ''; image.src = `data:image/png;base64,${response.png}`; node.replaceChildren(image);
      }).catch(error => { cancelTimedOut(error, id); }).finally(() => { thumbnails.delete(id); pump(); });
    }
  }
  function refresh() {
    const context = contextKey();
    const row = [...document.querySelectorAll('.nr-resource[data-resource-id]')].find(node => node.dataset.resourceId === selection?.id);
    if (dialog && !paused && !document.hidden && context === openedContext) {
      if (row) { focusReturn = row; row.classList.toggle('nr-preview-selected', true); row.setAttribute?.('aria-selected', 'true'); }
      return;
    }
    if (dialog) close(false, false);
    ++generation; stopThumbnails();
    if (selection && context !== selectionContext) selection = null;
    if (paused || document.hidden || !window.IntersectionObserver) return;
    observer = new window.IntersectionObserver(entries => {
      for (const entry of entries) {
        entry.target.dataset.previewVisible = entry.isIntersecting ? 'true' : 'false';
        if (!entry.isIntersecting) { for (const [id, node] of thumbnails) if (node === entry.target) cancel(id); if (thumbFallback.has(entry.target)) entry.target.innerHTML = thumbFallback.get(entry.target); delete entry.target.dataset.previewRequested; }
      }
      pump();
    }, { root: document.querySelector('.workspace-content'), threshold: 0.01 });
    for (const node of document.querySelectorAll('.nr-grid [data-preview-thumb]')) { if (!thumbFallback.has(node)) thumbFallback.set(node, node.innerHTML); delete node.dataset.previewRequested; observer.observe(node); }
    if (selection && !row) selection = null;
  }
  function suspend() { paused = true; ++generation; close(false); stopThumbnails(); }
  function resume() { paused = false; refresh(); }
  document.addEventListener('click', event => {
    if (event.target.closest?.('button,input,select,textarea,a')) return;
    const row = event.target.closest?.('.nr-resource[data-resource-id]'); if (!row) return;
    const resource = window.DesktopHubResources?.getState().result.items.find(item => item.resource.id === row.dataset.resourceId)?.resource;
    if (resource) { select(resource, row); row.focus(); }
  });
  document.addEventListener('focusin', event => {
    const row = event.target.closest?.('.nr-resource[data-resource-id]');
    if (!row || event.target !== row) return;
    const resource = window.DesktopHubResources?.getState().result.items.find(item => item.resource.id === row.dataset.resourceId)?.resource;
    if (resource) select(resource, row);
  });
  document.addEventListener('keydown', event => {
    if (event.isComposing || event.keyCode === 229 || editable(event.target)) return;
    if (event.key === 'Escape' && close()) { event.preventDefault(); event.stopPropagation?.(); }
    else if ((event.key === ' ' || event.code === 'Space') && selection && !dialog && !H.ui.modal && !paused && (window.DesktopHubResources?.getState().selectedIds?.length || 0) <= 1) { event.preventDefault(); void open(); }
    else if (event.key === 'Tab' && dialog) {
      const focusable = [...dialog.querySelectorAll('button')]; const first = focusable[0], last = focusable.at(-1);
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
    }
  });
  document.addEventListener('visibilitychange', () => { if (document.hidden) suspend(); else resume(); });
  const priorRender = H.render; H.render = function (...args) { const result = priorRender.apply(this, args); refresh(); return result; };
  window.DesktopHubPreview = Object.freeze({ select, open, handleEscape: () => close(), refresh, suspend, resume, isOpen: () => !!dialog });
  refresh();
}());
