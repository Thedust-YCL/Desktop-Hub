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
  let query = '', tagIds = [], view = 'list', page = 0, status = '', loading = false, error = '', sequence = 0, loaded = false;
  let selected = new Set(), scan = null, preview = null, folder = null, searchTimer = null, reloadTimer = null, busy = false;
  let discovery = null;
  const uuid = () => window.crypto.randomUUID();
  const itemById = id => result.items.find(x => x.resource.id === id);
  function rerender() {
    const active = document.activeElement, key = active?.dataset?.change, selection = active?.selectionStart;
    H.render();
    if (key) { const input = document.querySelector(`[data-change="${key}"]`); input?.focus(); if (typeof selection === 'number' && input?.type === 'search') input.setSelectionRange(selection, selection); }
  }
  async function request(type, payload = {}, timeoutMs = 120000) { return bridge.request(type, payload, { timeoutMs }); }
  async function reload() {
    const order = ++sequence; loading = true; error = '';
    const currentPage = H.ui.page;
    try {
      const next = await request('resources.query', { query, tagIds, page, favoritesOnly: currentPage === 'favorites', invalidOnly: currentPage === 'missing', ...(status ? { status } : {}) });
      if (order !== sequence) return;
      result = next; page = next.page; loaded = true;
    } catch (err) { if (order === sequence) error = err.message; }
    finally { if (order === sequence) { loading = false; if (resourcePages.has(H.ui.page)) rerender(); } }
  }
  async function mutate(type, payload, close = false) {
    if (busy) { H.toast('正在保存，请稍后再试。'); return false; }
    busy = true;
    try { await request(type, payload); if (close) H.closeModal(); await reload(); return true; }
    catch (err) { H.toast(err.message, 'error'); return false; }
    finally { busy = false; }
  }
  function tagsMarkup(item) { return item.tags.map(tag => `<button class="tag-chip" data-action="nr-filter-tag" ${idAttr(tag.id)}>${e(tag.name)}</button>`).join('') || '<span class="muted small">未添加标签</span>'; }
  function typeIcon(resource) { return resource.resourceType === 'folder' ? 'folder' : /\.(png|jpe?g|gif|webp|bmp)$/i.test(resource.extension) ? 'image' : /\.(zip|7z|rar)$/i.test(resource.extension) ? 'archive' : /\.(xlsx?|csv)$/i.test(resource.extension) ? 'table' : 'document'; }
  function suggestionsMarkup(item) {
    return item.suggestions.length ? `<div class="nr-suggestions"><span class="nr-suggestion-label">建议</span>${item.suggestions.slice(0, 2).map(s => `<span class="nr-suggestion" title="${e(s.suggestionBasis)}"><span>${e(s.suggestedName)}</span><button class="icon-button small" data-action="nr-accept" ${idAttr(s.id)} aria-label="接受建议 ${e(s.suggestedName)}">${icon('check', 13)}</button><button class="icon-button small" data-action="nr-edit-suggestion" ${idAttr(s.id)} aria-label="编辑建议 ${e(s.suggestedName)}">${icon('edit', 13)}</button><button class="icon-button small" data-action="nr-reject" ${idAttr(s.id)} aria-label="拒绝建议 ${e(s.suggestedName)}">${icon('close', 13)}</button></span>`).join('')}${item.suggestions.length > 2 ? `<button class="text-button" data-action="nr-detail" ${idAttr(item.resource.id)}>更多建议</button>` : ''}</div>` : '';
  }
  function stateMarkup(r) { return `<span class="badge ${r.status === 'Active' ? '' : 'status-warning'}">${e(statuses[r.status])}${r.cloudPlaceholder ? ' · 云占位' : ''}${r.reparsePoint ? ' · 链接' : ''}</span>`; }
  function controls(r) {
    return `<div class="nr-row-actions"><button class="icon-button ${r.favorite ? 'is-favorite' : ''}" data-action="nr-favorite" ${idAttr(r.id)} aria-label="${r.favorite ? '取消收藏' : '收藏'} ${e(r.displayName)}" aria-pressed="${r.favorite}">${icon('star')}</button>${r.status === 'Active' ? btn('nr-open', '打开', `${idAttr(r.id)} data-mode="open"`) : btn('nr-repair', '重新关联', idAttr(r.id))}${r.resourceType === 'folder' && r.status === 'Active' ? btn('nr-browse', '浏览', idAttr(r.id)) : ''}${btn('nr-detail', '详情', idAttr(r.id))}</div>`;
  }
  function row(item, grid = false) {
    const r = item.resource, invalid = H.ui.page === 'missing';
    return `<${grid ? 'article' : 'div'} class="nr-resource ${grid ? 'nr-card' : 'nr-row'}" data-resource-id="${e(r.id)}"><div class="nr-resource-heading">${invalid ? `<input type="checkbox" data-change="nr-select" ${idAttr(r.id)} ${selected.has(r.id) ? 'checked' : ''} ${r.status === 'SourceUnavailable' ? 'disabled' : ''} aria-label="选择索引 ${e(r.displayName)}">` : ''}<span class="nr-type-icon">${icon(typeIcon(r), grid ? 32 : 23)}</span><div class="nr-identity"><strong>${e(r.displayName)}</strong><small title="${e(r.canonicalPath)}">${e(r.canonicalPath)}</small></div>${stateMarkup(r)}</div><div class="nr-tags">${tagsMarkup(item)}</div>${suggestionsMarkup(item)}${controls(r)}</${grid ? 'article' : 'div'}>`;
  }
  function sourceMarkup() {
    return `<details class="nr-sources" ${!result.sources.length ? 'open' : ''}><summary>索引来源 · ${result.sources.length} 个已授权目录</summary><div class="nr-source-body"><div class="toolbar">${btn('nr-source-add', '当前用户桌面', 'data-kind="desktop"')}${btn('nr-source-add', '公共桌面…', 'data-kind="publicDesktop"')}${btn('nr-source-add', icon('plus') + ' 选择目录…', 'data-kind="custom"', 'primary')}</div>${!result.sources.length ? '<p class="muted">先查看扫描预览，确认后才建立索引和监测。</p>' : result.sources.map(source => `<div class="nr-source"><div><strong>${e({ desktop: '当前用户桌面', publicDesktop: '公共桌面', custom: '自定义目录' }[source.sourceType])}</strong><p>${e(source.path)}</p><small>${source.recursiveEnabled ? '递归索引' : '仅直接子项'} · ${source.availabilityStatus === 'Active' ? '可访问' : '来源不可用'} · ${source.monitoringStatus === 'Watching' ? '正在监测' : source.monitoringStatus === 'Disabled' ? '监测关闭' : '监测需检查'}${source.lastReconciledAt ? ' · 核对 ' + e(new Date(source.lastReconciledAt).toLocaleTimeString()) : ''}</small>${source.error ? `<p class="form-error">${e(source.error)}</p>` : ''}</div><div class="toolbar">${btn('nr-source-recursive', source.recursiveEnabled ? '关闭递归…' : '开启递归…', `${idAttr(source.id)} data-recursive="${!source.recursiveEnabled}"`)}${btn('nr-source-remove', '移除来源…', idAttr(source.id))}</div></div>`).join('')}</div></details>`;
  }
  function toolbar() {
    return `<div class="nr-toolbar"><label class="nr-search">${icon('search')}<input type="search" data-change="nr-search" value="${e(query)}" placeholder="搜索文件名或标签" aria-label="搜索文件名或标签"></label><div class="segmented" aria-label="资源视图">${[['list','列表'],['grid','图标'],['tags','标签']].map(([key,label]) => `<button data-action="nr-view" data-view="${key}" class="${view === key ? 'active' : ''}" aria-pressed="${view === key}">${label}</button>`).join('')}</div>${H.ui.page === 'missing' ? `<select data-change="nr-status" aria-label="失效状态筛选"><option value="">全部失效状态</option>${Object.entries(statuses).filter(([key]) => key !== 'Active').map(([key,label]) => `<option value="${key}" ${status === key ? 'selected' : ''}>${label}</option>`).join('')}</select>` : ''}</div><div class="nr-filter-tags">${result.tags.map(tag => `<button class="tag-chip ${tagIds.includes(tag.id) ? 'selected' : ''}" data-action="nr-filter-tag" ${idAttr(tag.id)} aria-pressed="${tagIds.includes(tag.id)}">${e(tag.name)}</button>`).join('')}${tagIds.length || query ? btn('nr-clear', '清除筛选') : ''}</div>`;
  }
  function heading(title) {
    return `<div class="page-heading"><div><h1 class="page-title" tabindex="-1">${title}</h1><p class="subtitle">${result.total} 项资源${tagIds.length ? ' · 同时匹配所选标签' : ''}</p></div><div class="toolbar">${btn('nr-refresh', loading ? '正在加载…' : icon('refresh') + ' 刷新', loading ? 'disabled' : '')}${btn('nr-invalid', '失效资源')}${btn('nr-tags', '管理标签')}</div></div>`;
  }
  function pagination() { const pages = Math.max(1, Math.ceil(result.total / result.pageSize)); return `<div class="nr-pagination">${btn('nr-page', '上一页', `data-page="${page - 1}" ${page === 0 ? 'disabled' : ''}`)}<span>${page + 1} / ${pages} · 每页 ${result.pageSize} 项</span>${btn('nr-page', '下一页', `data-page="${page + 1}" ${page + 1 >= pages ? 'disabled' : ''}`)}</div>`; }
  function libraryPage() {
    if (!loaded && !loading) { loading = true; queueMicrotask(reload); }
    const title = { library: '文件资源库', favorites: '收藏', search: '快速搜索', missing: '失效资源' }[H.ui.page] || '文件资源库';
    let content = '';
    if (view === 'tags') content += `<div class="nr-tag-navigation">${result.tags.length ? result.tags.map(tag => btn('nr-filter-tag', `${icon('tag')} ${e(tag.name)} · ${kinds[tag.tagType]}`, `${idAttr(tag.id)} aria-pressed="${tagIds.includes(tag.id)}"`, tagIds.includes(tag.id) ? 'primary' : 'quiet')).join('') : '<p class="muted">尚无正式标签。接受建议或在标签中心新建。</p>'}</div>`;
    content += result.items.length ? `<div class="${view === 'grid' ? 'nr-grid' : 'nr-list'}">${result.items.map(item => row(item, view === 'grid')).join('')}</div>${pagination()}` : `<div class="empty-state"><h3>${loading ? '正在读取资源…' : result.sources.length ? '没有匹配的资源' : '文件留在原处，从这里建立关联'}</h3><p>${result.sources.length ? '调整搜索、标签或收藏筛选，也可以刷新来源。' : '选择桌面或其他目录，预览后确认导入。'}</p></div>`;
    return `${heading(title)}${H.ui.page === 'library' ? sourceMarkup() : ''}${result.warning || error ? `<div class="notice warning" role="alert">${e(result.warning || error)}${btn('nr-refresh', '重试')}</div>` : ''}${toolbar()}${H.ui.page === 'missing' ? `<div class="nr-clean-bar"><p>来源不可用时先检查磁盘和权限。清理仅移除缺失/待修复索引。</p>${btn('nr-clean', `清理所选索引…（${selected.size}）`, selected.size ? '' : 'disabled', 'danger')}</div>` : ''}<div aria-busy="${loading}">${content}</div>`;
  }
  for (const name of ['library','favorites','search','missing']) H.pages[name] = libraryPage;
  H.pages.tags = () => `${heading('标签中心')}<div class="toolbar">${btn('nr-new-tag', icon('plus') + ' 新建标签', '', 'primary')}${btn('nr-library', '返回资源库')}</div><div class="nr-tag-manager">${result.tags.map(tag => `<div class="nr-tag-row"><span>${icon('tag')} <strong>${e(tag.name)}</strong> <small>${kinds[tag.tagType]}</small></span><div class="toolbar">${btn('nr-edit-tag', '编辑', idAttr(tag.id))}${btn('nr-delete-tag', '删除…', idAttr(tag.id))}</div></div>`).join('') || '<div class="empty-state">创建主题、时期或自定义标签，资源可以同时绑定多个标签。</div>'}</div>`;
  H.pages.folder = () => `<div class="page-heading"><div><h1 class="page-title" tabindex="-1">文件夹浏览</h1><p class="subtitle nr-path">${e(folder?.path)}</p><p class="subtitle">临时浏览 · ${folder?.total || 0} 项，添加后才成为长期索引。</p></div><div class="toolbar">${btn('nr-library', '返回资源库')}${btn('nr-folder-recursive', '为此文件夹开启递归…')}</div></div>${folder?.warnings?.length ? `<p class="form-error">${folder.warnings.map(e).join('<br>')}</p>` : ''}<div class="nr-list">${(folder?.items || []).map(r => `<div class="nr-folder-row"><span class="nr-type-icon">${icon(typeIcon(r),24)}</span><strong>${e(r.displayName)}</strong><div class="toolbar">${r.resourceType === 'folder' ? btn('nr-folder-child', '浏览', `data-name="${e(r.displayName)}"`) : ''}${btn('nr-folder-open','打开',`data-name="${e(r.displayName)}"`)}${btn('nr-folder-add','加入资源库',`data-name="${e(r.displayName)}"`)}</div></div>`).join('') || '<div class="empty-state">文件夹为空。</div>'}</div><div class="nr-pagination">${btn('nr-folder-page','上一页',`data-page="${(folder?.page || 0)-1}" ${!folder?.page ? 'disabled' : ''}`)}<span>${(folder?.page || 0)+1} / ${Math.max(1,Math.ceil((folder?.total || 0)/60))}</span>${btn('nr-folder-page','下一页',`data-page="${(folder?.page || 0)+1}" ${(folder?.page || 0)+1 >= Math.ceil((folder?.total || 0)/60) ? 'disabled' : ''}`)}</div>`;
  const previousNavigate = H.navigate;
  H.navigate = function (next) {
    if (next !== H.ui.page && resourcePages.has(next)) { query = ''; tagIds = []; page = 0; status = ''; selected.clear(); }
    previousNavigate(next);
    if (resourcePages.has(next)) void reload();
  };
  const previousRender = H.render;
  H.render = function () {
    previousRender();
    const sourceBody = document.querySelector('.nr-source-body');
    if (sourceBody && discovery?.desktop) sourceBody.insertAdjacentHTML('afterbegin', `<p class="nr-discovered-path">当前用户桌面 · ${result.sources.some(s=>s.sourceType==='desktop') ? '已授权' : '等待确认导入'}<br>${e(discovery.desktop)}${discovery.cloudPath ? '<br>可能由 OneDrive 同步 · 云占位不会主动下载' : ''}</p>`);
    if (['missing','folder'].includes(H.ui.page)) { const breadcrumb=document.querySelector('.workspace-subnav > span:not(.breadcrumb-divider)'); if(breadcrumb)breadcrumb.textContent=H.ui.page==='missing'?'失效资源':'文件夹浏览'; }
    const footer = document.querySelector('.workspace-footer > span:last-child');
    if (footer) footer.innerHTML = `文件索引在本地保存 <span class="footer-separator">·</span> <kbd>Esc</kbd> 返回桌面`;
    if (H.ui.page === 'settings' && document.querySelector('.settings-nav [data-section="sources"].active')) {
      const section = document.querySelector('.settings-content');
      if (section) section.innerHTML = `<h2>文件来源</h2><p class="subtitle">在资源库查看和管理已授权目录。</p>${btn('nr-library','打开文件资源库','','primary')}`;
    }
  };
  function confirm(title, body, action, attrs = '') { H.showModal(title, body, `${btn('close-modal','取消')}${btn(action,'确认',attrs,'primary')}`); }
  async function previewSource(kind, sourceId, recursive = false, browseToken = null, authorizePublic = false) {
    if (scan) return;
    const operationId = uuid(); scan = operationId;
    H.showModal('扫描预览', '<p>正在读取目录元信息…</p><p id="nr-scan-progress" role="status" aria-live="polite">等待选择或开始扫描。</p><p class="muted">确认前不会建立正式索引。</p>', btn('nr-cancel','取消扫描'), { locked: true });
    try {
      const next = await request('resources.preview', { operationId, recursive, ...(browseToken ? { browseToken } : sourceId ? { sourceId } : { kind }), ...(kind==='publicDesktop' ? {authorizePublic} : {}) }, 600000);
      if (scan !== operationId) return;
      if (next.cancelled) { scan = null; H.closeModal(); return; }
      preview = next;
      H.showModal('确认索引来源', `<p class="nr-path">${e(next.path)}</p><p>${next.files} 个文件 · ${next.folders} 个文件夹 · ${next.recursive ? '递归索引' : '只索引直接子项'}</p><p class="muted">${next.cloudPath ? '此路径可能由 OneDrive 同步。云占位不会主动下载。' : '只保存引用，原文件留在原处。'}${sourceId && !recursive ? '关闭递归后，仅此来源引用的深层索引与标签会移除，原文件保留。' : ''}</p>${next.warnings?.length ? `<details><summary>扫描提示 · ${next.warnings.length}</summary><p class="nr-path">${next.warnings.map(e).join('<br>')}</p></details>` : '<p class="muted">目录可以读取。来源确认后自动监测。</p>'}`, `${btn('nr-cancel','取消')}${!sourceId ? btn('nr-rescan',next.recursive ? '改为直接子项' : '改为递归预览',`data-kind="${e(kind)}" data-recursive="${!next.recursive}"`) : ''}${btn('nr-confirm','确认导入','','primary')}`, { locked: true });
    } catch (err) { if (scan === operationId) { scan = null; preview = null; H.closeModal(); H.toast(`扫描未完成：${err.message}`,'error'); } }
  }
  function tagEditor(tag) { H.showModal(tag ? '编辑标签' : '新建标签', `<label class="field"><span>标签名称</span><input id="nr-tag-name" maxlength="80" value="${e(tag?.name)}" autofocus></label><label class="field"><span>类型</span><select id="nr-tag-kind">${Object.entries(kinds).map(([key,label]) => `<option value="${key}" ${tag?.tagType === key ? 'selected' : ''}>${label}</option>`).join('')}</select></label>`, `${btn('close-modal','取消')}${btn('nr-save-tag','保存',tag ? idAttr(tag.id) : '', 'primary')}`); }
  function detail(id) {
    const item = itemById(id); if (!item) return; const r = item.resource;
    H.showModal(r.displayName, `<div class="nr-path">${e(r.canonicalPath)}</div><p>${stateMarkup(r)}</p><p class="subtitle">创建 ${e(new Date(r.creationTime).toLocaleString())} · 修改 ${e(new Date(r.modificationTime).toLocaleString())}</p><h3>正式标签</h3><div class="nr-bind-tags">${result.tags.map(tag => `<label class="check-row"><input type="checkbox" data-change="nr-bind" data-resource-id="${e(id)}" ${idAttr(tag.id)} ${item.tags.some(t => t.id === tag.id) ? 'checked' : ''}><span>${e(tag.name)}</span></label>`).join('') || '先在标签中心新建标签。'}</div><h3>待确认建议</h3>${item.suggestions.map(s => `<div class="nr-detail-suggestion"><strong>${e(s.suggestedName)}</strong><p class="muted">${e(s.suggestionBasis)}</p><div class="toolbar">${btn('nr-accept','接受',idAttr(s.id))}${btn('nr-edit-suggestion','编辑后接受',idAttr(s.id))}${btn('nr-reject','拒绝',idAttr(s.id))}</div></div>`).join('') || '<p class="muted">暂无待确认建议。</p>'}`, `${btn('nr-open','在资源管理器中显示',`${idAttr(id)} data-mode="reveal"`)}${btn('nr-open','打开所在文件夹',`${idAttr(id)} data-mode="folder"`)}${btn('close-modal','关闭')}`, { wide: true });
  }
  async function browse(payload) { try { folder = await request('resources.browse',payload); H.ui.page = 'folder'; H.render(); } catch(err) { H.toast(err.message,'error'); } }
  Object.assign(H.actions, {
    'nr-library': () => H.navigate('library'), 'nr-invalid': () => H.navigate('missing'), 'nr-tags': () => H.navigate('tags'),
    'nr-source-add': el => el.dataset.kind==='publicDesktop' ? H.showModal('公共桌面', '<p>公共桌面包含所有用户可见的桌面项目。</p><label class="check-row"><input id="nr-allow-public" type="checkbox" data-change="nr-public-consent"><span>允许读取公共桌面的目录元信息并查看扫描预览</span></label>', `${btn('close-modal','取消')}${btn('nr-public-preview','查看预览','disabled','primary')}`) : previewSource(el.dataset.kind),
    'nr-public-consent': el => { const button=document.querySelector('[data-action=nr-public-preview]');if(button)button.disabled=!el.checked; },
    'nr-public-preview': () => { if(document.querySelector('#nr-allow-public')?.checked)return previewSource('publicDesktop',null,false,null,true); },
    'nr-source-recursive': el => previewSource(null,el.dataset.id,el.dataset.recursive === 'true'),
    'nr-cancel': async () => { const operationId = scan; scan = null; preview = null; H.closeModal(); if (operationId) { try { await request('resources.cancel',{operationId}); } catch(err) { H.toast(err.message,'error'); } } },
    'nr-rescan': async el => { await H.actions['nr-cancel'](); await previewSource(el.dataset.kind,null,el.dataset.recursive === 'true',null,el.dataset.kind==='publicDesktop'); },
    'nr-confirm': async () => { if (!preview || busy) return; const token = preview.token; const ok = await mutate('resources.confirm',{token},true); if (ok) { scan = null; preview = null; H.toast('索引来源已保存，自动监测已开启。'); } },
    'nr-refresh': async () => { if (scan) return; scan = uuid(); H.showModal('核对索引','<p id="nr-scan-progress" role="status">正在核对已授权来源…</p>',btn('nr-cancel','取消核对'),{locked:true}); const id = scan; try { await request('resources.refresh',{operationId:id},600000); } catch(err) { if(scan===id) H.toast(err.message,'error'); } finally { if(scan===id){scan=null;H.closeModal();} await reload(); } },
    'nr-source-remove': el => confirm('移除索引来源？','<p>停止此目录的监测，并移除仅由此来源引用的索引、标签关系与收藏。其他来源引用的资源仍保留。</p><p class="muted">不会删除或改动实际文件。</p>','nr-source-remove-confirm',idAttr(el.dataset.id)),
    'nr-source-remove-confirm': el => mutate('resources.removeSource',{sourceId:el.dataset.id,confirmed:true},true),
    'nr-search': el => { query=el.value;page=0;clearTimeout(searchTimer);searchTimer=setTimeout(reload,180); },
    'nr-view': el => { view=el.dataset.view;H.render(); },
    'nr-status': el => { status=el.value;page=0;void reload(); },
    'nr-filter-tag': el => { const id=el.dataset.id; tagIds=tagIds.includes(id)?tagIds.filter(x=>x!==id):[...tagIds,id];page=0;void reload(); },
    'nr-clear': () => { query='';tagIds=[];page=0;void reload(); }, 'nr-page': el => {page=Number(el.dataset.page);void reload();},
    'nr-select': el => {el.checked?selected.add(el.dataset.id):selected.delete(el.dataset.id);H.render();},
    'nr-clean': () => confirm('清理所选失效索引？',`<p>将清理 ${selected.size} 项索引及其标签、收藏关系。只清理缺失或待修复项。</p><p class="muted">不会删除实际文件。此操作不能撤销。</p>`,'nr-clean-confirm'),
    'nr-clean-confirm': async () => {if(await mutate('resources.clean',{resourceIds:[...selected],confirmed:true},true)){selected.clear();H.render();}},
    'nr-favorite': el => {const item=itemById(el.dataset.id);if(item)return mutate('resources.favorite',{resourceId:el.dataset.id,favorite:!item.resource.favorite});},
    'nr-detail': el => detail(el.dataset.id),
    'nr-open': async el => {try{const response=await request('resources.open',{resourceId:el.dataset.id,mode:el.dataset.mode || 'open'});if(response.submitted)H.toast(response.message);}catch(err){H.toast(err.message,'error');}},
    'nr-repair': async el => {if(busy)return; if(!await mutate('resources.beginRepair',{resourceId:el.dataset.id}))return; await mutate('resources.repair',{resourceId:el.dataset.id});},
    'nr-new-tag': () => tagEditor(), 'nr-edit-tag': el => tagEditor(result.tags.find(t=>t.id===el.dataset.id)),
    'nr-save-tag': el => mutate('resources.saveTag',{...(el.dataset.id?{tagId:el.dataset.id}:{}),name:document.querySelector('#nr-tag-name').value,kind:document.querySelector('#nr-tag-kind').value},true),
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
  window.DesktopHubResources = Object.freeze({reload,getState:()=>({result,query,tagIds,view,page,loading,busy,scan,preview,folder}),previewSource});
  void request('resources.discover').then(value => { discovery=value; if(H.ui.page==='library')H.render(); }).catch(err => {error=err.message;});
}());
