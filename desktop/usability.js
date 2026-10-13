/* Density and compact layout reuse existing actions, components and viewport restoration. */
(function () {
  'use strict';
  const H = window.Hub;
  if (window.DesktopHubDesktop.getState().surface !== 'workspace') return;
  const e = value => H.escape(String(value ?? ''));
  const expanded = new Map();
  const density = () => H.state.settings.uiDensity === 'comfortable' ? 'comfortable' : 'compact';
  const pages = [['home','启动台'],['scenes','场景'],['library','文件库'],['tags','标签'],['favorites','收藏'],['search','搜索'],['settings','设置']];
  const settingsPage = H.pages.settings;
  H.pages.settings = function () {
    let html = settingsPage();
    if (!html.includes('data-section="appearance" aria-current="page"')) return html;
    const choices = [['compact','紧凑'],['comfortable','舒适']].map(([value,label]) => `<option value="${value}" ${density() === value ? 'selected' : ''}>${label}</option>`).join('');
    const row = `<div class="setting-row"><div><strong>界面密度</strong><p>调整留白与行高，保持文字清楚。</p></div><div class="setting-control"><select aria-label="界面密度" data-change="setting-value" data-setting="uiDensity">${choices}</select></div></div>`;
    return html.replace('<div class="setting-row"><div><strong>减少动画</strong>', row + '<div class="setting-row"><div><strong>减少动画</strong>');
  };
  function foldControls() {
    const content = document.querySelector('.workspace-content');
    if (!content || !['library','favorites','missing'].includes(H.ui.page)) return;
    const source = document.querySelector('#nr-sources');
    if (source) source.open = expanded.get('nr-sources') ?? density() !== 'compact';
    if (!document.querySelector('#usability-filters')) {
      const toolbar = document.querySelector('.nr-toolbar');
      if (toolbar) {
        const details = document.createElement('details'); details.id = 'usability-filters'; details.className = 'usability-filters';
        details.innerHTML = '<summary>筛选与视图</summary><div class="usability-filter-body"></div>';
        toolbar.after(details);
        const body = details.querySelector('.usability-filter-body');
        for (const selector of ['.nr-category-filter','.nr-filter-tags']) { const element = content.querySelector(selector); if (element) body.append(element); }
        for (const element of [...toolbar.querySelectorAll('.segmented, select')]) body.append(element);
        details.open = expanded.get(details.id) ?? density() !== 'compact';
      }
    }
    const state = window.DesktopHubResources?.getState();
    if (!state || document.querySelector('.usability-conditions')) return;
    const chips = [];
    if (state.query) chips.push(`<button class="button small" data-action="usability-clear-query">名称：${e(state.query)} ×</button>`);
    if (state.extension) chips.push(`<button class="button small" data-action="usability-clear-extension">类型：${e(state.extension === '__folders' ? '文件夹' : state.extension === '__none' ? '无扩展名' : state.extension)} ×</button>`);
    if (state.status) chips.push(`<button class="button small" data-action="usability-clear-status">状态：${e({ Missing: '缺失', PendingRepair: '需修复', SourceUnavailable: '来源不可用' }[state.status] || state.status)} ×</button>`);
    if (chips.length || state.tagIds?.length) {
      const bar = document.createElement('div'); bar.className = 'usability-conditions';
      bar.innerHTML = `<span>当前筛选${state.tagIds?.length ? ' · 同时满足所选标签' : ''}</span>${chips.join('')}<button class="text-button" data-action="nr-clear">清空条件</button>`;
      document.querySelector('.nr-toolbar')?.after(bar);
    }
  }
  function homeLayout() {
    if (H.ui.page !== 'home') return;
    const content = document.querySelector('.home-content'); if (!content) return;
    const intro = content.querySelector('.home-intro .subtitle'); if (intro) intro.textContent = '打开常用工具，继续手头的工作。';
    const modules = content.querySelector('.launchpad-grid'), heading = content.querySelector('.launcher-modules-heading'), scenes = content.querySelector('.home-scenes');
    if (modules && scenes) { scenes.after(modules); if (heading) modules.before(heading); }
    if (!document.querySelector('.usability-home-nav')) {
      const header = document.querySelector('.workspace-header');
      header?.insertAdjacentHTML('afterend', `<nav class="usability-home-nav" aria-label="工作空间模块">${pages.map(([page,label]) => `<button class="subnav-link" data-action="navigate" data-page="${page}">${label}</button>`).join('')}</nav>`);
    }
  }
  function apply() {
    document.documentElement.dataset.density = density();
    homeLayout(); foldControls(); H.restoreViewport?.();
  }
  H.actions['usability-clear-query'] = () => H.actions['nr-search']({ value: '' });
  H.actions['usability-clear-extension'] = () => H.actions['nr-extension']({ value: '' });
  H.actions['usability-clear-status'] = () => H.actions['nr-status']({ value: '' });
  const render = H.render; H.render = function () { render(); apply(); };
  document.addEventListener('toggle', event => { if (['nr-sources','usability-filters'].includes(event.target.id)) expanded.set(event.target.id, event.target.open); }, true);
  window.DesktopHubUsability = Object.freeze({ apply });
  apply();
}());
