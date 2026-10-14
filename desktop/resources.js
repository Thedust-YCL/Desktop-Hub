/* M2.2: native resource metadata, authorization previews and paged views. */
(function () {
  'use strict';
  const H = window.Hub, bridge = window.DesktopHubNative.bridge;
  const surface = window.DesktopHubDesktop.getState().surface;
  // Never present the M0 sample paths as a real indexed library.
  H.state.files = []; H.state.tags = []; H.state.sources = [];
  if (surface !== 'workspace') return;
  const e = value => H.escape(String(value ?? '')), icon = (name, size = 18) => H.icon(name, size);
  const btn = (action, label, attrs = '', kind = 'quiet') => `<button class="button ${kind}" data-action="${action}" ${attrs}>${label}</button>`;
  const idAttr = id => `data-id="${e(id)}"`;
  const statuses = { Active: '正常', Missing: '缺失', PendingRepair: '需修复', SourceUnavailable: '来源不可用' };
  const kinds = { theme: '主题', period: '时期', custom: '自定义' };
  const resourcePages = new Set(['library', 'favorites', 'search', 'missing', 'tags', 'folder']);
  let result = { items: [], total: 0, page: 0, pageSize: 60, sources: [], tags: [] };
  let category = '', extension = '';
  let query = '', tagIds = [], view = 'list', page = 0, status = '', loading = false, error = '', sequence = 0, loaded = false;
  let selected = new Set(), scan = null, preview = null, folder = null, searchTimer = null, reloadTimer = null, busy = false;
  let discovery = null;
  let cleanupSelected = new Set(), sort = 'type', filterId = '', selectionRevision = 0, batchDraft = null, filterDraft = null;
  let favoritesOnly = false;
  let cleanupDraft = null, selectionBlocked = false;
  let navigationRevision = 0, navigationOperation = 0;
  // Modal/page transitions supersede a deferred browse or tag-validation result,
  // including an editor opened and then closed while the native request was pending.
  const showModal = H.showModal, closeModal = H.closeModal;
  H.showModal = function (...args) { ++navigationRevision; return showModal.apply(this, args); };
  H.closeModal = function (...args) { ++navigationRevision; return closeModal.apply(this, args); };
  const beginNavigation = () => ({ revision: navigationRevision, operation: ++navigationOperation, page: H.ui.page });
  const currentNavigation = intent => intent.revision === navigationRevision && intent.operation === navigationOperation && intent.page === H.ui.page && !H.ui.modal;
  const uuid = () => window.crypto.randomUUID();
  const categories = () => result.tagCategories || Object.entries(kinds).map(([id, name]) => ({ id, name }));
  const categoryOf = tag => tag.categoryId || tag.tagType;
  const categoryName = tag => categories().find(c => c.id === categoryOf(tag))?.name || kinds[tag.tagType];
  const extensionName = value => value === '__folders' ? '文件夹' : value === '__none' ? '无扩展名' : value;
  function categoryFilters() {
    const choices = categories();
    return `<div class="nr-category-filter"><span>标签分类</span><div class="nr-category-choices">${choices.map(c => btn('nr-category', `${e(c.name)} <small>${result.tags.filter(t => categoryOf(t) === c.id).length}</small>`, `data-category="${e(c.id)}" aria-pressed="${category === c.id}"`, category === c.id ? 'primary' : 'quiet')).join('')}${btn('nr-new-category', icon('plus') + ' 新建分类')}</div></div>${category ? `<div class="nr-filter-tags" aria-label="${e(choices.find(c => c.id === category)?.name)}分类下的标签">${result.tags.filter(t => categoryOf(t) === category).map(tag => `<button class="tag-chip ${tagIds.includes(tag.id) ? 'selected' : ''}" data-action="nr-filter-tag" ${idAttr(tag.id)} aria-pressed="${tagIds.includes(tag.id)}">${e(tag.name)}</button>`).join('') || '<span class="muted">此分类暂无标签。</span>'}</div>` : ''}${tagIds.length ? `<div class="nr-selected-tags"><span>已选标签</span>${tagIds.map(id => { const tag = result.tags.find(t => t.id === id); return btn('nr-filter-tag', (tag ? e(tag.name) : '标签已删除') + ' ×', idAttr(id)); }).join('')}</div>` : ''}`;
  }
  const itemById = id => result.items.find(x => x.resource.id === id);
  function rerender() {
    const content = document.querySelector('.workspace-content'), scroll = content && { top: content.scrollTop, left: content.scrollLeft };
    H.render();
    const current = document.querySelector('.workspace-content');
    if (current && scroll) { current.scrollTop = scroll.top; current.scrollLeft = scroll.left; }
  }
  function clearSelection() { selected.clear(); cleanupSelected.clear(); ++selectionRevision; }
  function criteriaChanged() { clearTimeout(searchTimer); ++sequence; loading = false; page = 0; filterId = ''; selectionBlocked = true; clearSelection(); }
  const sameIds = (ids, set) => ids.length === set.size && ids.every(id => set.has(id));
  function applyFilterCriteria(filter) {
    const nextTags = [...filter.tagIds], nextExtension = filter.extension || '';
    const changed = query !== filter.query || !sameIds(nextTags, new Set(tagIds)) || extension !== nextExtension || favoritesOnly !== filter.favoritesOnly || status !== '';
    if (changed) clearSelection();
    query = filter.query; tagIds = nextTags; extension = nextExtension; favoritesOnly = filter.favoritesOnly; sort = filter.sort; status = '';
  }
  async function request(type, payload = {}, timeoutMs = 120000) { return bridge.request(type, payload, { timeoutMs }); }
  async function reload() {
    const order = ++sequence; loading = true; error = '';
    const currentPage = H.ui.page, selectedVersion = selectionRevision;
    const selectedIds = [...selected], cleanupIds = [...cleanupSelected];
    try {
      const next = await request('resources.query', { page, selectedIds, cleanupIds, ...(filterId ? { filterId } : { query, tagIds, sort, ...(extension ? { extension } : {}), favoritesOnly: favoritesOnly || currentPage === 'favorites', invalidOnly: currentPage === 'missing', ...(status ? { status } : {}) }) });
      if (order !== sequence || currentPage !== H.ui.page) return;
      const active = next.filterStatus?.filter || next.savedFilters?.find(saved => saved.filter.id === filterId)?.filter;
      // A stable filter ID always queries the host's current criteria, including
      // missing tags. Reconcile before rendering/history or pruning selection.
      if (filterId && active?.id === filterId) applyFilterCriteria(active);
      if (selectedVersion === selectionRevision) {
        if (next.selectedResources) { const live = new Set(next.selectedResources.filter(r => r.status === 'Active').map(r => r.id)); for (const id of selectedIds) if (!live.has(id)) selected.delete(id); }
        if (next.cleanupResources) { const live = new Set(next.cleanupResources.filter(r => ['Missing','PendingRepair'].includes(r.status)).map(r => r.id)); for (const id of cleanupIds) if (!live.has(id)) cleanupSelected.delete(id); }
        if (!sameIds(selectedIds,selected) || !sameIds(cleanupIds,cleanupSelected)) ++selectionRevision;
      }
      result = next; page = next.page; loaded = true; selectionBlocked = false;
    } catch (err) { if (order === sequence) error = err.message; }
    finally { if (order === sequence) { loading = false; if (resourcePages.has(H.ui.page)) rerender(); } }
  }
  async function mutate(type, payload, close = false) {
    if (busy) { H.toast('正在保存，请稍后再试。'); return false; }
    if (['resources.confirm','resources.removeSource'].includes(type)) { criteriaChanged(); }
    const revision = navigationRevision, modal = H.ui.modal;
    busy = true;
    try { await request(type, payload); if (close && revision === navigationRevision && H.ui.modal === modal) H.closeModal(); await reload(); return true; }
    catch (err) { H.toast(err.message, 'error'); return false; }
    finally { busy = false; rerender(); }
  }
  function tagsMarkup(item) { return item.tags.map(tag => `<button class="tag-chip" data-action="nr-filter-tag" ${idAttr(tag.id)}>${e(tag.name)}</button>`).join('') || '<span class="muted small">未添加标签</span>'; }
  function typeIcon(resource) { return resource.resourceType === 'folder' ? 'folder' : /\.(png|jpe?g|gif|webp|bmp)$/i.test(resource.extension) ? 'image' : /\.(zip|7z|rar)$/i.test(resource.extension) ? 'archive' : /\.(xlsx?|csv)$/i.test(resource.extension) ? 'table' : 'document'; }
  function suggestionsMarkup(item) {
    return item.suggestions.length ? `<div class="nr-suggestions"><span class="nr-suggestion-label">建议</span>${item.suggestions.slice(0, 2).map(s => `<span class="nr-suggestion" title="${e(s.suggestionBasis)}"><span>${e(s.suggestedName)}</span><button class="icon-button small" data-action="nr-accept" ${idAttr(s.id)} aria-label="接受建议 ${e(s.suggestedName)}">${icon('check', 13)}</button><button class="icon-button small" data-action="nr-edit-suggestion" ${idAttr(s.id)} aria-label="编辑建议 ${e(s.suggestedName)}">${icon('edit', 13)}</button><button class="icon-button small" data-action="nr-reject" ${idAttr(s.id)} aria-label="拒绝建议 ${e(s.suggestedName)}">${icon('close', 13)}</button></span>`).join('')}${item.suggestions.length > 2 ? `<button class="text-button" data-action="nr-detail" ${idAttr(item.resource.id)}>更多建议</button>` : ''}</div>` : '';
  }
  function stateMarkup(r) { return `<span class="badge ${r.status === 'Active' ? '' : 'status-warning'}">${e(statuses[r.status])}${r.cloudPlaceholder ? ' · 云占位' : ''}${r.reparsePoint ? ' · 链接' : ''}</span>`; }
  function controls(r) {
    return `<div class="nr-row-actions"><button class="icon-button ${r.favorite ? 'is-favorite' : ''}" data-action="nr-favorite" ${idAttr(r.id)} title="${r.favorite ? '取消收藏' : '收藏'}" aria-label="${r.favorite ? '取消收藏' : '收藏'} ${e(r.displayName)}" aria-pressed="${r.favorite}">${icon('star', 21)}</button>${btn('nr-detail', '详情', `${idAttr(r.id)} aria-label="查看 ${e(r.displayName)} 详情"`)}${r.status === 'Active' ? btn('nr-open', '打开 ' + icon('external', 16), `${idAttr(r.id)} data-mode="open" aria-label="打开 ${e(r.displayName)}${r.resourceType === 'folder' ? '（资源管理器）' : ''}"`, 'nr-open-button') : btn('nr-repair', '重新关联', idAttr(r.id))}</div>`;
  }
  function row(item, grid = false) {
    const r = item.resource, invalid = H.ui.page === 'missing', browsable = r.resourceType === 'folder' && r.status === 'Active';
    const suggestions = grid ? suggestionsMarkup(item) : item.suggestions.length ? `<button class="nr-suggestion-summary" data-action="nr-detail" ${idAttr(r.id)}>${item.suggestions.length} 个标签建议</button>` : '';
    const selectable = invalid ? ['Missing','PendingRepair'].includes(r.status) : r.status === 'Active';
    const selection = invalid ? cleanupSelected : selected;
    return `<${grid ? 'article' : 'div'} class="nr-resource ${grid ? 'nr-card' : 'nr-row'} ${browsable ? 'nr-browsable' : ''}" data-resource-id="${e(r.id)}">${browsable ? `<button class="nr-row-hit" data-action="nr-browse" ${idAttr(r.id)} aria-label="浏览文件夹 ${e(r.displayName)}" title="点击整行浏览文件夹"></button>` : ''}<div class="nr-resource-heading"><input type="checkbox" data-change="nr-select" ${idAttr(r.id)} ${selection.has(r.id) ? 'checked' : ''} ${!selectable || busy ? 'disabled' : ''} aria-label="选择${invalid ? '失效索引' : '资源'} ${e(r.displayName)}"><span class="nr-type-icon">${icon(typeIcon(r), grid ? 32 : 25)}</span><div class="nr-identity"><strong>${e(r.displayName)}</strong><small title="${e(r.canonicalPath)}">${e(r.canonicalPath)}</small></div>${browsable ? `<span class="nr-browse-hint">浏览 ${icon('arrow-right', 15)}</span>` : ''}</div><div class="nr-tag-cell"><div class="nr-tags">${tagsMarkup(item)}</div>${suggestions}</div><div class="nr-status">${stateMarkup(r)}</div>${controls(r)}</${grid ? 'article' : 'div'}>`;
  }
  function sourceMarkup() {
    return `<details id="nr-sources" class="nr-sources" ${!result.sources.length ? 'open' : ''}><summary>索引来源 · ${result.sources.length} 个已授权目录</summary><div class="nr-source-body"><div class="toolbar">${btn('nr-source-add', '当前用户桌面', 'data-kind="desktop"')}${btn('nr-source-add', '公共桌面…', 'data-kind="publicDesktop"')}${btn('nr-source-add', icon('plus') + ' 选择目录…', 'data-kind="custom"', 'primary')}</div>${!result.sources.length ? '<p class="muted">先查看扫描预览，确认后才建立索引和监测。</p>' : result.sources.map(source => `<div class="nr-source"><div><strong>${e({ desktop: '当前用户桌面', publicDesktop: '公共桌面', custom: '自定义目录' }[source.sourceType])}</strong><p>${e(source.path)}</p><small>${source.recursiveEnabled ? '递归索引' : '仅直接子项'} · ${source.availabilityStatus === 'Active' ? '可访问' : '来源不可用'} · ${source.monitoringStatus === 'Watching' ? '正在监测' : source.monitoringStatus === 'Disabled' ? '监测关闭' : '监测需检查'}${source.lastReconciledAt ? ' · 核对 ' + e(new Date(source.lastReconciledAt).toLocaleTimeString()) : ''}</small>${source.error ? `<p class="form-error">${e(source.error)}</p>` : ''}</div><div class="toolbar">${btn('nr-source-recursive', source.recursiveEnabled ? '关闭递归…' : '开启递归…', `${idAttr(source.id)} data-recursive="${!source.recursiveEnabled}"`)}${btn('nr-source-remove', '移除来源…', idAttr(source.id))}</div></div>`).join('')}</div></details>`;
  }
  function toolbar() {
    const extensions = new Set(result.extensions || result.items.map(x => x.resource.resourceType === 'folder' ? '__folders' : x.resource.extension || '__none'));
    if (extension) extensions.add(extension);
    return `<div class="nr-toolbar"><label class="nr-search">${icon('search')}<input type="search" data-change="nr-search" value="${e(query)}" placeholder="搜索文件名或标签" aria-label="搜索文件名或标签"></label><div class="segmented" aria-label="资源视图">${[['list','列表'],['grid','图标'],['tags','标签']].map(([key,label]) => `<button data-action="nr-view" data-view="${key}" class="${view === key ? 'active' : ''}" aria-pressed="${view === key}">${label}</button>`).join('')}</div><select data-change="nr-extension" aria-label="按扩展名筛选"><option value="">全部扩展名</option>${[...extensions].map(value => `<option value="${e(value)}" ${extension === value ? 'selected' : ''}>${e(extensionName(value))}</option>`).join('')}</select>${H.ui.page === 'missing' ? `<select data-change="nr-status" aria-label="失效状态筛选"><option value="">全部失效状态</option>${Object.entries(statuses).filter(([key]) => key !== 'Active').map(([key,label]) => `<option value="${key}" ${status === key ? 'selected' : ''}>${label}</option>`).join('')}</select>` : ''}${tagIds.length || query || extension ? btn('nr-clear', '清除筛选') : ''}</div>${categoryFilters()}`;
  }
  function heading(title) {
    return `<div class="page-heading"><div><h1 class="page-title" tabindex="-1">${title}</h1><p class="subtitle">${result.total} 项资源${tagIds.length ? ' · 同时匹配所选标签' : ''}</p></div><div class="toolbar">${btn('nr-refresh', loading ? '正在加载…' : icon('refresh') + ' 刷新', loading ? 'disabled' : '')}${H.ui.page === 'missing' ? btn('nr-library', icon('arrow-left') + ' 返回文件资源库') : btn('nr-invalid', '失效资源')}${btn('nr-tags', '管理标签')}</div></div>`;
  }
  function pagination() { const pages = Math.max(1, Math.ceil(result.total / result.pageSize)); return `<div class="nr-pagination">${btn('nr-page', '上一页', `data-page="${page - 1}" ${page === 0 ? 'disabled' : ''}`)}<span>${page + 1} / ${pages} · 每页 ${result.pageSize} 项</span>${btn('nr-page', '下一页', `data-page="${page + 1}" ${page + 1 >= pages ? 'disabled' : ''}`)}</div>`; }
  function listHeading() { return '<div class="nr-list-heading" aria-hidden="true"><span>名称 / 路径</span><span>标签</span><span>状态</span><span>操作</span></div>'; }
  const sorts = { type: '按类型', name: '按名称', modifiedAsc: '修改时间从早到晚', modifiedDesc: '修改时间从晚到早' };
  function savedFiltersMarkup() {
    if (H.ui.page === 'missing') return '';
    const active = (result.savedFilters || []).find(s => s.filter.id === filterId);
    return `<div class="nr-saved-filters"><div class="toolbar"><label>排序 <select data-change="nr-sort" aria-label="资源排序">${Object.entries(sorts).map(([value,name])=>`<option value="${value}" ${sort === value ? 'selected' : ''}>${name}</option>`).join('')}</select></label><label class="check-row"><input type="checkbox" data-change="nr-favorites-only" ${favoritesOnly || H.ui.page === 'favorites' ? 'checked' : ''} ${H.ui.page === 'favorites' ? 'disabled' : ''}><span>仅收藏</span></label>${btn('nr-filter-new','保存当前筛选')}</div>${active ? `<p class="nr-filter-current">正在使用：${e(active.filter.name)} ${btn('nr-filter-exit','退出保存筛选')}</p>` : ''}${result.savedFilters?.length ? `<details id="nr-saved-list"><summary>保存的筛选 · ${result.savedFilters.length}</summary>${result.savedFilters.map(s=>`<div class="nr-saved-row"><button class="text-button" data-action="nr-filter-use" ${idAttr(s.filter.id)}>${e(s.filter.name)}</button>${!s.valid ? '<span class="badge status-warning">标签已失效</span>' : ''}<div class="toolbar">${btn('nr-filter-edit','编辑',idAttr(s.filter.id))}${btn('nr-filter-delete','删除…',idAttr(s.filter.id))}</div></div>`).join('')}</details>` : ''}</div>`;
  }
  function batchToolbar() {
    if (H.ui.page === 'missing') return '';
    return `<div class="nr-batch-bar" role="region" aria-label="批量整理资源"><span aria-live="polite">${selected.size ? `已选 ${selected.size} 项 · 跨页保留` : '选择正常资源以批量整理'}</span><div class="toolbar">${btn('nr-select-page','选择本页',busy || result.filterInvalid ? 'disabled' : '')}${selected.size ? `${btn('nr-select-clear','清空选择')}${btn('nr-batch-tags','添加标签…','data-operation="addTags"')}${btn('nr-batch-tags','移除标签…','data-operation="removeTags"')}${btn('nr-batch-favorite','收藏','data-operation="favorite"')}${btn('nr-batch-favorite','取消收藏','data-operation="unfavorite"')}${btn('nr-batch-suggestions','接受同类建议…')}` : ''}</div></div>`;
  }
  function libraryPage() {
    if (!loaded && !loading) { loading = true; queueMicrotask(reload); }
    const title = { library: '文件资源库', favorites: '收藏', search: '快速搜索', missing: '失效资源' }[H.ui.page] || '文件资源库';
    let content = '';

    content += result.items.length ? `<div class="${view === 'grid' ? 'nr-grid' : 'nr-list'}">${view === 'grid' ? '' : listHeading()}${result.items.map(item => row(item, view === 'grid')).join('')}</div>${pagination()}` : `<div class="empty-state"><h3>${loading ? '正在读取资源…' : result.sources.length ? '没有匹配的资源' : '文件留在原处，从这里建立关联'}</h3><p>${result.sources.length ? '调整搜索、标签或收藏筛选，也可以刷新来源。' : '选择桌面或其他目录，预览后确认导入。'}</p></div>`;
    if (filterId && result.filterInvalid) content = `<div class="empty-state" role="alert"><h3>${result.filterMissing ? '此保存筛选已删除' : '筛选中的标签已失效'}</h3><p>${result.filterMissing ? '退出此筛选后重新选择。' : '编辑筛选，移除或替换已删除的标签后再使用。'}</p>${result.filterMissing ? '' : btn('nr-filter-edit','编辑筛选',idAttr(filterId),'primary')}${btn('nr-filter-exit','退出保存筛选')}</div>`;
    return `${heading(title)}${H.ui.page === 'library' ? sourceMarkup() : ''}${result.warning || error ? `<div class="notice warning" role="alert">${e(result.warning || error)}${btn('nr-refresh', '重试')}</div>` : ''}${toolbar()}${savedFiltersMarkup()}${batchToolbar()}${H.ui.page === 'missing' ? `<div class="nr-clean-bar"><p>来源不可用时先检查磁盘和权限。清理仅移除缺失/待修复索引。</p>${btn('nr-clean', `清理所选索引…（${cleanupSelected.size}）`, cleanupSelected.size ? '' : 'disabled', 'danger')}</div>` : ''}<div aria-busy="${loading}">${content}</div>`;
  }
  for (const name of ['library','favorites','search','missing']) H.pages[name] = libraryPage;
  H.pages.tags = () => `${heading('标签中心')}<div class="toolbar">${btn('nr-new-tag', icon('plus') + ' 新建标签', '', 'primary')}${btn('nr-library', '返回资源库')}</div>${categoryFilters()}<div class="nr-tag-manager">${category ? result.tags.filter(t => categoryOf(t) === category).map(tag => `<div class="nr-tag-row"><span>${icon('tag')} <strong>${e(tag.name)}</strong> <small>${e(categoryName(tag))}</small></span><div class="toolbar">${btn('nr-edit-tag', '编辑', idAttr(tag.id))}${btn('nr-delete-tag', '删除…', idAttr(tag.id))}</div></div>`).join('') || '<div class="empty-state">此分类暂无标签，可以新建一个。</div>' : '<div class="empty-state">先选择分类，再管理其中的标签。</div>'}</div>`;

  H.pages.folder = () =>     `<div class="page-heading"><div><h1 class="page-title" tabindex="-1">文件夹浏览</h1><p class="subtitle nr-path">${e(folder?.path)}</p><p class="subtitle">临时浏览 · ${folder?.total || 0} 项，添加后才成为长期索引。</p></div><div class="toolbar">${btn('nr-library', '返回资源库')}${btn('nr-folder-recursive', '为此文件夹开启递归…')}</div></div>${folder?.warnings?.length ? `<p class="form-error">${folder.warnings.map(e).join('<br>')}</p>` : ''}<div class="nr-list">${(folder?.items || []).map(r => `<div class="nr-folder-row ${r.resourceType === 'folder' ? 'nr-browsable' : ''}">${r.resourceType === 'folder' ? `<button class="nr-row-hit" data-action="nr-folder-child" data-name="${e(r.displayName)}" aria-label="浏览文件夹 ${e(r.displayName)}" title="点击整行浏览文件夹"></button>` : ''}<span class="nr-type-icon">${icon(typeIcon(r),25)}</span><strong>${e(r.displayName)}</strong>${r.resourceType === 'folder' ? `<span class="nr-browse-hint">浏览 ${icon('arrow-right',15)}</span>` : ''}<div class="toolbar">${btn('nr-folder-add','加入资源库',`data-name="${e(r.displayName)}"`)}${btn('nr-folder-open','打开',`data-name="${e(r.displayName)}"`)}</div></div>`).join('') || '<div class="empty-state">文件夹为空。</div>'}</div><div class="nr-pagination">${btn('nr-folder-page','上一页',`data-page="${(folder?.page || 0)-1}" ${!folder?.page ? 'disabled' : ''}`)}<span>${(folder?.page || 0)+1} / ${Math.max(1,Math.ceil((folder?.total || 0)/60))}</span>${btn('nr-folder-page','下一页',`data-page="${(folder?.page || 0)+1}" ${(folder?.page || 0)+1 >= Math.ceil((folder?.total || 0)/60) ? 'disabled' : ''}`)}</div>`;
  const previousNavigate = H.navigate;
  H.navigate = function (next) {
    ++navigationRevision;
    if (next !== H.ui.page) { criteriaChanged(); query = ''; tagIds = []; status = ''; extension = ''; category = ''; sort = 'type'; favoritesOnly = next === 'favorites'; }
    previousNavigate(next);
    if (resourcePages.has(next)) void reload();
  };
  const previousRender = H.render;
  H.render = function () {
    previousRender();
    const sourceBody = document.querySelector('.nr-source-body');
    if (sourceBody && discovery?.desktop) sourceBody.insertAdjacentHTML('afterbegin', `<p class="nr-discovered-path">当前用户桌面 · ${result.sources.some(s=>s.sourceType==='desktop') ? '已授权' : '等待确认导入'}<br>${e(discovery.desktop)}${discovery.cloudPath ? '<br>可能由 OneDrive 同步 · 云占位不会主动下载' : ''}</p>`);
    if (['missing','folder'].includes(H.ui.page)) {
      const breadcrumb = document.querySelector('.workspace-subnav > span:not(.breadcrumb-divider)');
      if (breadcrumb) breadcrumb.outerHTML = `<button class="text-button" data-action="nr-library">文件资源库</button><span class="breadcrumb-divider">/</span><span>${H.ui.page === 'missing' ? '失效资源' : '文件夹浏览'}</span>`;
    }
    const footer = document.querySelector('.workspace-footer > span:last-child');
    if (footer) footer.innerHTML = `文件索引在本地保存 <span class="footer-separator">·</span> <kbd>Esc</kbd> 返回桌面`;
    if (H.ui.page === 'settings' && document.querySelector('.settings-nav [data-section="sources"].active')) {
      const section = document.querySelector('.settings-content');
      if (section) section.innerHTML = `<h2>文件来源</h2><p class="subtitle">在资源库查看和管理已授权目录。</p>${btn('nr-library','打开文件资源库','','primary')}`;
    }
    H.restoreViewport?.();
  };
  function confirm(title, body, action, attrs = '') { H.showModal(title, body, `${btn('close-modal','取消')}${btn(action,'确认',attrs,'primary')}`); }
  function selectResource(el) {
    if (busy || H.ui.modal || selectionBlocked || loading) return;
    const item = itemById(el.dataset.id), invalid = H.ui.page === 'missing', set = invalid ? cleanupSelected : selected;
    if (!item || !(invalid ? ['Missing','PendingRepair'].includes(item.resource.status) : item.resource.status === 'Active') || result.filterInvalid) return;
    if (el.checked && !set.has(el.dataset.id) && set.size >= 500) { el.checked = false; H.toast('最多选择 500 项资源。'); return; }
    el.checked ? set.add(el.dataset.id) : set.delete(el.dataset.id); ++selectionRevision; rerender();
  }
  async function batchEditor(operation) {
    if (busy || H.ui.modal || !selected.size) return;
    const ids = [...selected], revision = navigationRevision;
    await reload();
    if (revision !== navigationRevision || H.ui.modal) return;
    if (!sameIds(ids,selected)) { H.toast('选择已变化，请重新选择后整理。','error'); return; }
    let body;
    if (operation === 'acceptSuggestions') {
      body = `<p>为所选 ${ids.length} 项接受一组同名、同类型建议。</p>${(result.commonSuggestions || []).map((s,index)=>`<label class="check-row"><input type="radio" name="nr-batch-suggestion" data-change="nr-batch-suggestion" data-index="${index}"><span>${e(s.name)} · ${e(kinds[s.type])}</span></label>`).join('') || '<p class="muted">所选资源没有共同的待确认建议。可调整选择或逐项查看详情。</p>'}`;
    } else body = `<p>${operation === 'addTags' ? '添加所选标签，保留原有标签。' : '仅移除勾选标签，保留其他原有标签。'}将应用于 ${ids.length} 项资源。</p><div class="nr-bind-tags">${result.tags.map(tag=>`<label class="check-row"><input type="checkbox" data-change="nr-batch-tag" data-batch-tag value="${e(tag.id)}"><span>${e(tag.name)}</span></label>`).join('') || '<p class="muted">请先在标签中心创建标签。</p>'}</div>`;
    H.showModal(operation === 'acceptSuggestions' ? '接受同类建议' : operation === 'addTags' ? '批量添加标签' : '批量移除标签',body,`${btn('close-modal','取消')}${btn('nr-batch-confirm','确认整理','','primary')}`);
    batchDraft = { ids, operation, values: new Set(), groups: result.commonSuggestions || [], suggestion: null, revision: navigationRevision, modal: H.ui.modal };
  }
  async function submitBatch(operation, draft = null) {
    if (busy || !selected.size) { if (draft) H.toast('选择已失效，请重新选择。','error'); return; }
    const ids = draft ? draft.ids : [...selected], revision = navigationRevision, modal = H.ui.modal, selectionVersion = selectionRevision;
    if (draft && (draft.revision !== revision || draft.modal !== modal || !sameIds(ids,selected))) { H.toast('选择已变化，请重新选择后整理。','error'); return; }
    if (draft && (operation === 'acceptSuggestions' ? !draft.suggestion : !draft.values.size)) { H.toast(operation === 'acceptSuggestions' ? '请选择一组建议。' : '请选择标签。','error'); return; }
    const type = operation === 'acceptSuggestions' ? 'resources.batchSuggestions' : 'resources.batch';
    const payload = operation === 'acceptSuggestions' ? { resourceIds: ids, suggestionName: draft.suggestion.name, suggestionType: draft.suggestion.type } : { resourceIds: ids, operation, values: draft ? [...draft.values] : [] };
    busy = true;
    try {
      await request(type,payload);
      if (revision !== navigationRevision || selectionVersion !== selectionRevision || modal !== H.ui.modal) return;
      selected.clear(); ++selectionRevision;
      if (draft) { batchDraft = null; H.closeModal(); }
      await reload(); H.toast(`已整理 ${ids.length} 项资源。`);
    } catch (err) { if (revision === navigationRevision) H.toast(err.message,'error'); }
    finally { busy = false; rerender(); }
  }
  function filterEditor(id = null) {
    if (busy || H.ui.modal) return;
    const saved = id && (result.savedFilters || []).find(s=>s.filter.id===id);
    if (id && !saved) { H.toast('此筛选已删除，请刷新。','error'); return; }
    filterDraft = saved ? { ...saved.filter, tagIds: [...saved.filter.tagIds] } : { id: uuid(), name: '', query, tagIds: [...tagIds], extension: extension || null, favoritesOnly: favoritesOnly || H.ui.page === 'favorites', sort };
    renderFilterEditor();
  }
  function renderFilterEditor() {
    const f = filterDraft;
    H.showModal('保存筛选',`<label class="field"><span>筛选名称</span><input id="nr-filter-name" value="${e(f.name)}" maxlength="80" required autofocus></label><label class="field"><span>名称或标签关键词</span><input id="nr-filter-query" value="${e(f.query)}" maxlength="200"></label><label class="field"><span>扩展名（留空为全部）</span><input id="nr-filter-extension" value="${e(f.extension)}" maxlength="260" placeholder="例如 .txt"></label><label class="check-row"><input id="nr-filter-favorites" type="checkbox" ${f.favoritesOnly ? 'checked' : ''}><span>仅收藏</span></label><label class="field"><span>排序</span><select id="nr-filter-sort">${Object.entries(sorts).map(([value,name])=>`<option value="${value}" ${f.sort === value ? 'selected' : ''}>${name}</option>`).join('')}</select></label><fieldset class="nr-filter-editor-tags"><legend>同时满足以下标签</legend>${result.tags.map(tag=>`<label class="check-row"><input type="checkbox" data-change="nr-filter-tag-choice" value="${e(tag.id)}" ${f.tagIds.includes(tag.id) ? 'checked' : ''}><span>${e(tag.name)}</span></label>`).join('')}${f.tagIds.filter(id=>!result.tags.some(t=>t.id===id)).map(id=>`<p class="form-error">标签已删除，筛选失效 ${btn('nr-filter-tag-remove','移除此条件',idAttr(id))}</p>`).join('')}</fieldset><p class="form-error" id="nr-filter-error" role="alert"></p>`,`${btn('close-modal','取消')}${btn('nr-filter-save','保存','','primary')}`);
    f.revision = navigationRevision; f.modal = H.ui.modal;
  }
  function readFilterFields() {
    if (!filterDraft) return;
    for (const [field,selector] of [['name','#nr-filter-name'],['query','#nr-filter-query'],['extension','#nr-filter-extension'],['sort','#nr-filter-sort']]) { const node=document.querySelector(selector); if (node) filterDraft[field]=node.value; }
    const favorite = document.querySelector('#nr-filter-favorites'); if (favorite) filterDraft.favoritesOnly=favorite.checked;
  }
  async function saveFilter() {
    if (!filterDraft || busy || filterDraft.revision !== navigationRevision || filterDraft.modal !== H.ui.modal) return;
    readFilterFields(); const f=filterDraft;
    if (!f.name.trim()) { H.toast('请填写筛选名称。','error'); return; }
    if (f.tagIds.some(id=>!result.tags.some(t=>t.id===id))) { H.toast('请移除或替换已删除的标签。','error'); return; }
    const revision=navigationRevision, modal=H.ui.modal;
    const filter={id:f.id,name:f.name.trim(),query:f.query,tagIds:[...f.tagIds],extension:f.extension||null,favoritesOnly:f.favoritesOnly,sort:f.sort};
    busy=true;
    try {
      await request('resources.filterSave',{filter});
      if (revision !== navigationRevision || modal !== H.ui.modal) return;
      if (filterId === filter.id) { ++sequence; loading=false; applyFilterCriteria(filter); }
      filterDraft=null;H.closeModal();await reload();H.toast('筛选已保存。');
    } catch (err) { if (revision===navigationRevision) { const node=document.querySelector('#nr-filter-error');if(node)node.textContent=err.message;else H.toast(err.message,'error'); } }
    finally {busy=false;rerender();}
  }
  function timeTagConsent() {
    if (result.autoTagByModificationTime != null) return '';
    return `<fieldset class="nr-time-tag-choice"><legend>是否按修改时间自动添加所有标签</legend><p>按修改年月为所有资源添加时间标签，例如“2026年10月”。允许后，新导入的文件也会自动添加；已有标签保留。</p><label class="check-row"><input type="radio" name="nr-time-tags" value="yes"><span>允许，自动添加时间标签</span></label><label class="check-row"><input type="radio" name="nr-time-tags" value="no" checked><span>暂不添加，稍后手动整理</span></label></fieldset>`;
  }
  async function previewSource(kind, sourceId, recursive = false, browseToken = null, authorizePublic = false) {
    if (scan) return;
    const operationId = uuid(); scan = operationId;
    H.showModal('扫描预览', '<p>正在读取目录元信息…</p><p id="nr-scan-progress" role="status" aria-live="polite">等待选择或开始扫描。</p><p class="muted">确认前不会建立正式索引。</p>', btn('nr-cancel','取消扫描'), { locked: true });
    try {
      const next = await request('resources.preview', { operationId, recursive, ...(browseToken ? { browseToken } : sourceId ? { sourceId } : { kind }), ...(kind==='publicDesktop' ? {authorizePublic} : {}) }, 600000);
      if (scan !== operationId) return;
      if (next.cancelled) { scan = null; H.closeModal(); return; }
      preview = next;
      H.showModal('确认索引来源', `<p class="nr-path">${e(next.path)}</p><p>${next.files} 个文件 · ${next.folders} 个文件夹 · ${next.recursive ? '递归索引' : '只索引直接子项'}</p><p class="muted">${next.cloudPath ? '此路径可能由 OneDrive 同步。云占位不会主动下载。' : '只保存引用，原文件留在原处。'}${sourceId && !recursive ? '关闭递归后，仅此来源引用的深层索引与标签会移除，原文件保留。' : ''}</p>${next.warnings?.length ? `<details><summary>扫描提示 · ${next.warnings.length}</summary><p class="nr-path">${next.warnings.map(e).join('<br>')}</p></details>` : '<p class="muted">目录可以读取。来源确认后自动监测。</p>'}${timeTagConsent()}`, `${btn('nr-cancel','取消')}${!sourceId ? btn('nr-rescan',next.recursive ? '改为直接子项' : '改为递归预览',`data-kind="${e(kind)}" data-recursive="${!next.recursive}"`) : ''}${btn('nr-confirm','确认导入','','primary')}`, { locked: true });
    } catch (err) { if (scan === operationId) { scan = null; preview = null; H.closeModal(); H.toast(`扫描未完成：${err.message}`,'error'); } }
  }
  function tagEditor(tag) {
    H.showModal(tag ? '编辑标签' : '新建标签', `<label class="field"><span>标签名称</span><input id="nr-tag-name" maxlength="80" value="${e(tag?.name)}" required autofocus></label><label class="field"><span>分类（必选）</span><select id="nr-tag-kind" required><option value="">请选择分类</option>${categories().map(c => `<option value="${e(c.id)}" ${(tag ? categoryOf(tag) : category) === c.id ? 'selected' : ''}>${e(c.name)}</option>`).join('')}<option value="__new">＋ 新建分类</option></select></label><label class="field"><span>新分类名称（选择“新建分类”时填写）</span><input id="nr-new-category-name" maxlength="40" placeholder="例如：项目、客户、用途"></label><p id="nr-tag-error" class="form-error" role="alert"></p>`, `${btn('close-modal','取消')}${btn('nr-save-tag','保存',tag ? idAttr(tag.id) : '', 'primary')}`);
  }
  function tagError(message) { const target = document.querySelector('#nr-tag-error'); if (target) target.textContent = message; else H.toast(message, 'error'); }
  async function saveTag(el) {
    const name = document.querySelector('#nr-tag-name')?.value.trim() || '';
    let categoryId = document.querySelector('#nr-tag-kind')?.value || '';
    if (!name || !categoryId) { tagError('请填写标签名称并选择分类。'); return; }
    if (busy) return;
    if (categoryId === '__new') {
      const name = document.querySelector('#nr-new-category-name')?.value.trim() || '';
      if (!name) { tagError('请填写新分类名称。'); return; }
      busy = true;
      try {
        const response = await request('resources.saveCategory', { name }); categoryId = response.category.id;
        await reload();
        const select = document.querySelector('#nr-tag-kind');
        if (select) { select.insertAdjacentHTML('beforeend', `<option value="${e(categoryId)}">${e(name)}</option>`); select.value = categoryId; }
      } catch (err) { tagError(err.message); return; }
      finally { busy = false; }
    }
    const kind = Object.hasOwn(kinds, categoryId) ? categoryId : 'custom';
    await mutate('resources.saveTag', { ...(el.dataset.id ? { tagId: el.dataset.id } : {}), name, kind, categoryId }, true);
  }
  function detail(id) {
    const item = itemById(id); if (!item) return; const r = item.resource;
    H.showModal(r.displayName, `<div class="nr-path">${e(r.canonicalPath)}</div><p>${stateMarkup(r)}</p><p class="subtitle">创建 ${e(new Date(r.creationTime).toLocaleString())} · 修改 ${e(new Date(r.modificationTime).toLocaleString())}</p><h3>正式标签</h3><div class="nr-bind-tags">${result.tags.map(tag => `<label class="check-row"><input type="checkbox" data-change="nr-bind" data-resource-id="${e(id)}" ${idAttr(tag.id)} ${item.tags.some(t => t.id === tag.id) ? 'checked' : ''}><span>${e(tag.name)}</span></label>`).join('') || '先在标签中心新建标签。'}</div><h3>待确认建议</h3>${item.suggestions.map(s => `<div class="nr-detail-suggestion"><strong>${e(s.suggestedName)}</strong><p class="muted">${e(s.suggestionBasis)}</p><div class="toolbar">${btn('nr-accept','接受',idAttr(s.id))}${btn('nr-edit-suggestion','编辑后接受',idAttr(s.id))}${btn('nr-reject','拒绝',idAttr(s.id))}</div></div>`).join('') || '<p class="muted">暂无待确认建议。</p>'}`, `${btn('nr-open','在资源管理器中显示',`${idAttr(id)} data-mode="reveal"`)}${btn('nr-open','打开所在文件夹',`${idAttr(id)} data-mode="folder"`)}${btn('close-modal','关闭')}`, { wide: true });
  }
  async function browse(payload) {
    if (H.ui.modal) return;
    const intent = beginNavigation();
    try {
      const next = await request('resources.browse', payload);
      if (!currentNavigation(intent)) return;
      window.DesktopHubInteractions?.checkpoint(); folder = next; H.navigate('folder');
    } catch (err) { if (currentNavigation(intent)) H.toast(err.message, 'error'); }
  }
  Object.assign(H.actions, {
    'nr-library': () => H.navigate('library'), 'nr-invalid': () => H.navigate('missing'), 'nr-tags': () => H.navigate('tags'),
    'nr-source-add': el => el.dataset.kind==='publicDesktop' ? H.showModal('公共桌面', '<p>公共桌面包含所有用户可见的桌面项目。</p><label class="check-row"><input id="nr-allow-public" type="checkbox" data-change="nr-public-consent"><span>允许读取公共桌面的目录元信息并查看扫描预览</span></label>', `${btn('close-modal','取消')}${btn('nr-public-preview','查看预览','disabled','primary')}`) : previewSource(el.dataset.kind),
    'nr-public-consent': el => { const button=document.querySelector('[data-action=nr-public-preview]');if(button)button.disabled=!el.checked; },
    'nr-public-preview': () => { if(document.querySelector('#nr-allow-public')?.checked)return previewSource('publicDesktop',null,false,null,true); },
    'nr-source-recursive': el => previewSource(null,el.dataset.id,el.dataset.recursive === 'true'),
    'nr-cancel': async () => { const operationId = scan; scan = null; preview = null; H.closeModal(); if (operationId) { try { await request('resources.cancel',{operationId}); } catch(err) { H.toast(err.message,'error'); } } },
    'nr-rescan': async el => { await H.actions['nr-cancel'](); await previewSource(el.dataset.kind,null,el.dataset.recursive === 'true',null,el.dataset.kind==='publicDesktop'); },
    'nr-confirm': async () => {
      if (!preview || busy) return;
      const token = preview.token, choice = document.querySelector('input[name="nr-time-tags"]:checked');
      const consent = choice ? { autoTagByModificationTime: choice.value === 'yes' } : {};
      const ok = await mutate('resources.confirm', { token, ...consent }, true);
      if (ok) { scan = null; preview = null; H.toast(`索引来源已保存，自动监测已开启。${consent.autoTagByModificationTime ? '所有资源已按修改年月添加时间标签。' : ''}`); }
    },
    'nr-refresh': async () => { if (scan) return; scan = uuid(); H.showModal('核对索引','<p id="nr-scan-progress" role="status">正在核对已授权来源…</p>',btn('nr-cancel','取消核对'),{locked:true}); const id = scan; try { await request('resources.refresh',{operationId:id},600000); } catch(err) { if(scan===id) H.toast(err.message,'error'); } finally { if(scan===id){scan=null;H.closeModal();} await reload(); } },
    'nr-source-remove': el => confirm('移除索引来源？','<p>停止此目录的监测，并移除仅由此来源引用的索引、标签关系与收藏。其他来源引用的资源仍保留。</p><p class="muted">不会删除或改动实际文件。</p>','nr-source-remove-confirm',idAttr(el.dataset.id)),
    'nr-source-remove-confirm': el => mutate('resources.removeSource',{sourceId:el.dataset.id,confirmed:true},true),
    'nr-search': el => { criteriaChanged();query=el.value;searchTimer=setTimeout(reload,150); },
    'nr-view': el => { view=el.dataset.view;H.render(); },
    'nr-category': el => { category = category === el.dataset.category ? '' : el.dataset.category; H.render(); },
    'nr-extension': el => { criteriaChanged();extension = el.value;void reload(); },
    'nr-new-category': () => H.showModal('新建标签分类', '<label class="field"><span>分类名称</span><input id="nr-category-name" maxlength="40" required autofocus placeholder="例如：项目、客户、用途"></label><p id="nr-tag-error" class="form-error" role="alert"></p>', btn('close-modal', '取消') + btn('nr-save-category', '创建分类', '', 'primary')),
    'nr-save-category': async () => {
      const name = document.querySelector('#nr-category-name')?.value.trim() || '';
      if (!name) { tagError('请填写分类名称。'); return; }
      if (busy) return; busy = true;
      try { const response = await request('resources.saveCategory', { name }); category = response.category.id; H.closeModal(); await reload(); }
      catch (err) { tagError(err.message); }
      finally { busy = false; }
    },
    'nr-status': el => { criteriaChanged();status=el.value;void reload(); },
    'nr-filter-tag': el => { const id=el.dataset.id;criteriaChanged();tagIds=tagIds.includes(id)?tagIds.filter(x=>x!==id):[...tagIds,id];void reload(); },
    'nr-clear': () => { criteriaChanged();query='';tagIds=[];status='';extension='';favoritesOnly=H.ui.page==='favorites';void reload(); }, 'nr-page': el => {page=Number(el.dataset.page);void reload();},
    'nr-sort': el => { sort=el.value;filterId='';page=0;++sequence;void reload(); },
    'nr-favorites-only': el => { criteriaChanged();favoritesOnly=el.checked;void reload(); },
    'nr-select': selectResource,
    'nr-select-page': () => { if(busy||selectionBlocked||loading)return;const set=H.ui.page==='missing'?cleanupSelected:selected;for(const item of result.items){const eligible=H.ui.page==='missing'?['Missing','PendingRepair'].includes(item.resource.status):item.resource.status==='Active';if(eligible&&set.size<500)set.add(item.resource.id);}++selectionRevision;rerender(); },
    'nr-select-clear': () => {if(busy)return;clearSelection();rerender();},
    'nr-batch-tags': el => batchEditor(el.dataset.operation),
    'nr-batch-tag': el => {if(!batchDraft||busy)return;el.checked?batchDraft.values.add(el.value):batchDraft.values.delete(el.value);},
    'nr-batch-suggestions': () => batchEditor('acceptSuggestions'),
    'nr-batch-suggestion': el => {if(batchDraft&&!busy)batchDraft.suggestion=batchDraft.groups[Number(el.dataset.index)]||null;},
    'nr-batch-confirm': () => batchDraft&&submitBatch(batchDraft.operation,batchDraft),
    'nr-batch-favorite': el => submitBatch(el.dataset.operation),
    'nr-filter-new': () => filterEditor(),
    'nr-filter-edit': el => filterEditor(el.dataset.id),
    'nr-filter-tag-choice': el => {if(!filterDraft||busy)return;filterDraft.tagIds=el.checked?[...new Set([...filterDraft.tagIds,el.value])]:filterDraft.tagIds.filter(id=>id!==el.value);},
    'nr-filter-tag-remove': el => {if(!filterDraft||busy)return;readFilterFields();filterDraft.tagIds=filterDraft.tagIds.filter(id=>id!==el.dataset.id);renderFilterEditor();},
    'nr-filter-save': saveFilter,
    'nr-filter-use': el => {if(busy||H.ui.modal)return;const saved=(result.savedFilters||[]).find(s=>s.filter.id===el.dataset.id);if(!saved)return;if(H.ui.page!=='library')H.navigate('library');criteriaChanged();filterId=saved.filter.id;query=saved.filter.query;tagIds=[...saved.filter.tagIds];extension=saved.filter.extension||'';sort=saved.filter.sort;favoritesOnly=saved.filter.favoritesOnly;status='';void reload();},
    'nr-filter-exit': () => {criteriaChanged();query='';tagIds=[];extension='';favoritesOnly=H.ui.page==='favorites';void reload();},
    'nr-filter-delete': el => {if(!busy&&!H.ui.modal)confirm('删除保存筛选？','<p>移除此筛选，资源与标签保留。</p>','nr-filter-delete-confirm',idAttr(el.dataset.id));},
    'nr-filter-delete-confirm': async el => {if(busy)return;const revision=navigationRevision,modal=H.ui.modal;busy=true;try{await request('resources.filterDelete',{id:el.dataset.id});if(revision!==navigationRevision||modal!==H.ui.modal)return;if(filterId===el.dataset.id){criteriaChanged();query='';tagIds=[];extension='';favoritesOnly=H.ui.page==='favorites';}H.closeModal();await reload();}catch(err){if(revision===navigationRevision)H.toast(err.message,'error');}finally{busy=false;rerender();}},
    'nr-clean': () => {if(busy||H.ui.modal||!cleanupSelected.size)return;confirm('清理所选失效索引？',`<p>将清理 ${cleanupSelected.size} 项索引及其标签、收藏关系。只清理缺失或待修复项。</p><p class="muted">不会删除实际文件。此操作不能撤销。</p>`,'nr-clean-confirm');cleanupDraft={ids:[...cleanupSelected],revision:navigationRevision,modal:H.ui.modal};},
    'nr-clean-confirm': async () => {if(!cleanupDraft||cleanupDraft.revision!==navigationRevision||cleanupDraft.modal!==H.ui.modal||!sameIds(cleanupDraft.ids,cleanupSelected)){H.toast('选择已变化，请重新选择后清理。','error');return;}const draft=cleanupDraft;if(await mutate('resources.clean',{resourceIds:draft.ids,confirmed:true},true)){for(const id of draft.ids)cleanupSelected.delete(id);++selectionRevision;cleanupDraft=null;rerender();}},
    'nr-favorite': el => {const item=itemById(el.dataset.id);if(item)return mutate('resources.favorite',{resourceId:el.dataset.id,favorite:!item.resource.favorite});},
    'nr-detail': el => detail(el.dataset.id),
    'nr-open': async el => {try{const response=await request('resources.open',{resourceId:el.dataset.id,mode:el.dataset.mode || 'open'});if(response.submitted)H.toast(response.message);}catch(err){H.toast(err.message,'error');}},
    'nr-repair': async el => {if(busy)return; if(!await mutate('resources.beginRepair',{resourceId:el.dataset.id}))return; await mutate('resources.repair',{resourceId:el.dataset.id});},
    'nr-new-tag': () => tagEditor(), 'nr-edit-tag': el => tagEditor(result.tags.find(t=>t.id===el.dataset.id)),
    'nr-save-tag': saveTag,
    'nr-delete-tag': el => confirm('删除标签？','<p>只移除标签及其资源关联，资源索引和实际文件保留。</p>','nr-delete-tag-confirm',idAttr(el.dataset.id)),
    'nr-delete-tag-confirm': el => mutate('resources.deleteTag',{tagId:el.dataset.id,confirmed:true},true),
    'nr-bind': el => mutate('resources.bindTag',{resourceId:el.dataset.resourceId,tagId:el.dataset.id,bind:el.checked}),
    'nr-accept': async el => {if(await mutate('resources.suggestion',{suggestionId:el.dataset.id,accept:true},!!H.ui.modal))H.toast('建议已成为正式标签。');},
    'nr-reject': el => mutate('resources.suggestion',{suggestionId:el.dataset.id,accept:false},!!H.ui.modal),
    'nr-edit-suggestion': el => {const suggestion=result.items.flatMap(x=>x.suggestions).find(s=>s.id===el.dataset.id);if(!suggestion)return;H.showModal('编辑建议',`<label class="field"><span>标签名称</span><input id="nr-suggestion-name" maxlength="80" value="${e(suggestion.suggestedName)}" autofocus></label><p class="muted">${e(suggestion.suggestionBasis)}</p>`,`${btn('close-modal','取消')}${btn('nr-edited-accept','接受标签',idAttr(suggestion.id),'primary')}`);},
    'nr-edited-accept': el => mutate('resources.suggestion',{suggestionId:el.dataset.id,accept:true,editedName:document.querySelector('#nr-suggestion-name').value},true),
    'nr-browse': el => browse({resourceId:el.dataset.id}),
    'nr-folder-child': el => browse({token:folder.token,name:el.dataset.name}),
    'nr-folder-page': el => browse({token:folder.token,page:Number(el.dataset.page)}),
    'nr-folder-add': async el => {if(await mutate('resources.addBrowsed',{token:folder.token,name:el.dataset.name}))H.toast('此项已加入长期索引。');},
    'nr-folder-open': async el => {try{const response=await request('resources.open',{token:folder.token,name:el.dataset.name,mode:'open'});H.toast(response.message);}catch(err){H.toast(err.message,'error');}},
    'nr-folder-recursive': () => previewSource('custom',null,true,folder?.token)
  });
  document.addEventListener('dblclick', event => { const row=event.target.closest('[data-resource-id]');if(row&&!event.target.closest('button,input')) void H.actions['nr-open']({dataset:{id:row.dataset.resourceId,mode:'open'}}); });
  bridge.on('resources.changed', () => {clearTimeout(reloadTimer);reloadTimer=setTimeout(reload,150);});
  bridge.on('resources.progress', progress => {if(progress.operationId===scan){const label=document.querySelector('#nr-scan-progress');if(label)label.textContent=`已读取 ${progress.count} 项 · ${progress.path}`;}else if(progress.phase==='Failed')H.toast(`后台核对未完成：${progress.path}`,'error');});
  window.DesktopHubResources = Object.freeze({reload,getState:()=>({result,query,tagIds,view,page,status,loading,busy,scan,preview,folder,category,extension,sort,filterId,favoritesOnly,selectedIds:[...selected],cleanupIds:[...cleanupSelected]}),previewSource,
    browse: id => browse({ resourceId: id }),
    repair: id => H.actions['nr-repair']({ dataset: { id } }),
    filterTag: async id => {
      if (H.ui.modal) return;
      const intent = beginNavigation();
      try {
        const snapshot = await request('resources.query');
        if (!currentNavigation(intent)) return;
        if (!snapshot.tags.some(tag => tag.id === id)) throw new Error('此标签已移除，请重新搜索。');
        window.DesktopHubInteractions?.checkpoint(); H.navigate('library');
        if (H.ui.page !== 'library' || H.ui.modal) return;
        criteriaChanged();query = ''; tagIds = [id]; status = ''; extension = ''; category = '';favoritesOnly=false;
        await reload();
      } catch (err) { if (currentNavigation(intent)) H.toast(err.message, 'error'); }
    },
    navigationState: () => ({ query, tagIds: [...tagIds], view, page, status, category, extension, folder,sort,filterId,favoritesOnly }),
    restoreNavigation: value => { criteriaChanged();query = value.query; tagIds = [...value.tagIds]; view = value.view; page = value.page; status = value.status; category = value.category; extension = value.extension; folder = value.folder;sort=value.sort||'type';filterId=value.filterId||'';favoritesOnly=value.favoritesOnly||false;void reload(); }});
  void request('resources.discover').then(value => { discovery=value; if(H.ui.page==='library')H.render(); }).catch(err => {error=err.message;});
}());
