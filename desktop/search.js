/* Unified metadata search. Execution uses existing registered-ID actions only. */
(function () {
  'use strict';
  const H = window.Hub, desktop = window.DesktopHubDesktop, bridge = window.DesktopHubNative.bridge;
  if (desktop.getState().surface !== 'workspace') return;
  const e = value => H.escape(String(value ?? ''));
  const types = { all: '全部', app: '应用', scene: '场景', file: '文件', folder: '文件夹', tag: '标签', project: '项目' };
  const statuses = { Missing: '缺失 · 重新关联', PendingRepair: '需修复 · 重新关联', SourceUnavailable: '来源不可用', Disabled: '已停用 · 编辑启用', Invalid: '引用失效 · 编辑修复' };
  let query = '', type = 'all', rows = [], active = -1, composing = false, timer = null, generation = 0, error = '', loading = false, opener = null;
  let launchFailure = null, launchSequence = 0;
  const isOpen = () => !!H.ui.modal?.unifiedSearch;
  const node = id => document.querySelector('#' + id);
  const settings = () => desktop.getState().hostState?.settings;
  const actionLabel = row => statuses[row.status] || ({ app: '启动', scene: '执行', file: '打开', folder: '浏览', tag: '筛选', project: '查看' }[row.type] || '查看');
  function paint() {
    if (!isOpen()) return;
    const feedback = node('unified-feedback'), results = node('unified-results'), input = node('unified-query');
    if (feedback) feedback.textContent = loading ? '正在搜索…' : error || (rows.length ? `${rows.length} 项结果${rows.length === 50 ? ' · 最多显示 50 项，请缩小范围' : ''}` : '没有匹配结果，换一个关键词或类型。');
    if (results) {
      results.setAttribute('aria-busy', String(loading));
      results.innerHTML = rows.map((row, index) => `<div class="unified-result ${active === index ? 'selected' : ''}" role="option" aria-selected="${active === index}" id="unified-option-${index}"><button class="unified-result-main" data-action="unified-execute" data-index="${index}" data-id="${e(row.id)}"><span class="unified-kind">${e(types[row.type])}</span><span class="unified-identity"><strong>${e(row.name)}</strong><small title="${e(row.detail)}">${e(row.detail)}</small></span><span class="unified-operation">${e(actionLabel(row))}</span></button>${row.type === 'folder' && row.status === 'Active' ? `<button class="icon-button" data-action="unified-reveal" data-index="${index}" aria-label="在资源管理器打开 ${e(row.name)}" title="资源管理器">${H.icon('external', 17)}</button>` : ''}</div>`).join('') + (error ? '<button class="button" data-action="unified-retry">重试</button>' : '');
    }
    if (input) { if (active >= 0) input.setAttribute('aria-activedescendant', `unified-option-${active}`); else input.removeAttribute('aria-activedescendant'); }
    const filters = node('unified-types');
    if (filters) {
      if (!filters.dataset.initialized) {
        filters.innerHTML = Object.entries(types).map(([key, label]) => `<button class="button small" data-action="unified-type" data-type="${key}">${label}</button>`).join('');
        filters.dataset.initialized = 'true';
      }
      // Keep filter nodes connected so keyboard focus survives query/result updates.
      filters.querySelectorAll('[data-type]').forEach(button => {
        const selected = button.dataset.type === type;
        button.className = 'button small ' + (selected ? 'primary' : 'quiet');
        button.setAttribute('aria-pressed', String(selected));
      });
    }
  }
  function invalidate() { ++generation; clearTimeout(timer); timer = null; rows = []; active = -1; }
  function refresh() {
    if (!isOpen()) return;
    invalidate(); loading = true; error = ''; paint();
    if (composing) return;
    const order = generation, text = query, filter = type;
    timer = setTimeout(async () => {
      timer = null;
      try {
        const result = await bridge.request('search.query', { query: text, type: filter });
        if (order !== generation || !isOpen()) return;
        if (!Array.isArray(result) || result.some(row => !types[row.type] || row.type === 'all' || typeof row.id !== 'string' || typeof row.name !== 'string' || typeof row.detail !== 'string' || typeof row.status !== 'string')) throw new Error('搜索结果无效，请重试。');
        rows = result.slice(0, 50); active = rows.findIndex(row => row.status === 'Active'); if (active < 0 && rows.length) active = 0;
      } catch (failure) { if (order !== generation || !isOpen()) return; error = failure.message || '搜索失败，请重试。'; }
      finally { if (order === generation && isOpen()) { loading = false; paint(); } }
    }, 150);
  }
  function open() {
    if (H.ui.modal) return false;
    opener = document.activeElement ? { id: document.activeElement.id, data: { ...document.activeElement.dataset } } : null;
    query = ''; type = 'all'; composing = false;
    H.showModal('搜索', `<div class="unified-search"><label class="unified-input">${H.icon('search', 20)}<input id="unified-query" type="search" maxlength="200" autofocus autocomplete="off" placeholder="名称、中文首字母、路径或标签" aria-label="搜索应用、场景、文件和标签" role="combobox" aria-controls="unified-results" aria-expanded="true" aria-autocomplete="list"></label><div id="unified-types" class="unified-types" aria-label="搜索类型"></div><p id="unified-feedback" class="unified-feedback" role="status" aria-live="polite"></p><div id="unified-results" class="unified-results" role="listbox" aria-label="搜索结果"></div></div>`, '<span class="unified-hint">↑ ↓ 选择 · Enter 打开 · Esc 关闭</span>', { wide: true });
    H.ui.modal.unifiedSearch = true; node('unified-query')?.focus({ preventScroll: true }); refresh(); return true;
  }
  const previousClose = H.closeModal;
  H.closeModal = function () {
    const search = isOpen(), lost = search && !H.ui.modal.opener?.isConnected;
    if (search) { invalidate(); composing = false; loading = false; }
    previousClose();
    if (lost && opener) {
      const restored = opener.id ? document.querySelector('#' + opener.id) : [...document.querySelectorAll('[data-action],[data-change]')].find(element => Object.entries(opener.data).every(([key, value]) => element.dataset[key] === value));
      restored?.focus({ preventScroll: true });
    }
  };
  async function execute(index, reveal = false) {
    if (!isOpen() || composing || loading) return;
    const row = rows[index]; if (!row) return;
    const current = settings();
    if (row.type === 'app' && !current?.appEntries?.some(app => app.id === row.id) || row.type === 'scene' && !current?.scenes?.some(scene => scene.id === row.id)) {
      H.toast('此项已移除，请重新搜索。', 'warning'); refresh(); return;
    }
    H.closeModal();
    try {
      if (row.type === 'app') {
        const order = ++launchSequence;
        const outcome = await H.actions['app-launch']({ dataset: { id: row.id } });
        if (order === launchSequence && outcome?.success === false) {
          launchFailure = { appId: row.id, name: row.name, message: outcome.message || '启动失败，请检查所选文件。' };
          H.render();
        }
      }
      else if (row.type === 'scene') {
        const scene = current.scenes.find(item => item.id === row.id);
        const invalid = scene.actions?.some(action => action.enabled !== false && (action.referenceInvalid || action.appEntryId && !current.appEntries.some(app => app.id === action.appEntryId)));
        await H.actions[!scene.enabled || invalid || row.status !== 'Active' ? 'edit-scene' : 'run-scene']({ dataset: { id: row.id } });
      } else if (row.type === 'tag') await window.DesktopHubResources.filterTag(row.id);
      else if (row.type === 'file' || row.type === 'folder') {
        if (row.status === 'Missing' || row.status === 'PendingRepair') await window.DesktopHubResources.repair(row.id);
        else if (row.status !== 'Active') { H.toast('来源暂不可用，请检查磁盘或权限，再刷新资源库。', 'warning'); H.navigate('library'); }
        else if (row.type === 'folder' && !reveal) await window.DesktopHubResources.browse(row.id);
        else await H.actions['nr-open']({ dataset: { id: row.id, mode: 'open' } });
      } else if (row.type === 'project' && window.DesktopHubProjects?.open) window.DesktopHubProjects.open(row.id);
      else H.toast('此项暂不可用，请刷新后重试。', 'warning');
    } catch (failure) { H.toast(failure.message || '无法打开，请重试。', 'error'); }
  }
  Object.assign(H.actions, {
    'search-open': open, 'unified-retry': refresh,
    'unified-type': el => { if (!Object.hasOwn(types, el.dataset.type)) return; type = el.dataset.type; refresh(); },
    'unified-execute': el => execute(Number(el.dataset.index)), 'unified-reveal': el => execute(Number(el.dataset.index), true),
    'unified-launch-dismiss': () => { launchFailure = null; H.render(); },
    'unified-launch-edit': el => {
      if (H.ui.modal) return;
      if (!settings()?.appEntries?.some(app => app.id === el.dataset.id)) { H.toast('此应用已移除，请重新搜索。', 'warning'); return; }
      H.actions['app-edit']({ dataset: { id: el.dataset.id } });
    }
  });
  const navigate = H.navigate;
  H.navigate = function (page) { if (page === 'search') { open(); return; } return navigate(page); };
  function bindEntry() {
    const top = document.querySelector('.global-search');
    if (top) { top.dataset.action = 'search-open'; top.removeAttribute('data-page'); top.setAttribute('aria-label', '搜索应用、场景、文件和标签'); const label = top.querySelector('span'); if (label) label.textContent = '搜索应用、场景、文件'; }
  }
  function paintLaunchFailure() {
    if (!launchFailure || node('unified-launch-failure')) return;
    document.querySelector('.workspace-content')?.insertAdjacentHTML('afterbegin', `<div id="unified-launch-failure" class="notice warning" role="alert"><span>${e(launchFailure.name)}：${e(launchFailure.message)}</span><button class="button small" data-action="unified-launch-edit" data-id="${e(launchFailure.appId)}">编辑应用 / 更换文件</button><button class="icon-button small" data-action="unified-launch-dismiss" aria-label="关闭启动提示">${H.icon('close', 16)}</button></div>`);
  }
  const render = H.render; H.render = function () { render(); bindEntry(); paintLaunchFailure(); };
  document.addEventListener('input', event => {
    if (!isOpen() || event.target.id !== 'unified-query') return;
    query = event.target.value; if (event.isComposing) composing = true; refresh();
  });
  document.addEventListener('compositionstart', event => { if (isOpen() && event.target.id === 'unified-query') { composing = true; invalidate(); } });
  document.addEventListener('compositionend', event => { if (isOpen() && event.target.id === 'unified-query') { composing = false; query = event.target.value; refresh(); } });
  document.addEventListener('keydown', event => {
    if (event.ctrlKey && event.key?.toLowerCase() === 'k') { event.preventDefault(); event.stopImmediatePropagation(); if (!event.isComposing && event.keyCode !== 229) open(); return; }
    if (!isOpen() || composing || event.isComposing || event.keyCode === 229) return;
    if (event.key === 'Enter' && event.target.id === 'unified-query') { event.preventDefault(); event.stopImmediatePropagation(); void execute(active); }
    else if (['ArrowUp', 'ArrowDown', 'Home', 'End'].includes(event.key) && event.target.id === 'unified-query' && rows.length) {
      event.preventDefault(); event.stopImmediatePropagation(); active = event.key === 'Home' ? 0 : event.key === 'End' ? rows.length - 1 : (active + (event.key === 'ArrowUp' ? -1 : 1) + rows.length) % rows.length; paint(); node(`unified-option-${active}`)?.scrollIntoView({ block: 'nearest' });
    }
  }, true);
  for (const event of ['state.changed', 'resources.changed']) bridge.on(event, () => { if (isOpen() && !composing) refresh(); });
  window.DesktopHubSearch = Object.freeze({ open, refresh });
  bindEntry();
}());
