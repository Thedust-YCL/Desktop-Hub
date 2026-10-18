/* Desktop Hub M0 — local, simulated resource and tag interfaces. */
(function () {
  'use strict';
  const t = window.DesktopHubI18n?.text || ((value, ...args) => Array.isArray(value) ? value.reduce((out, part, i) => out + part + (i < args.length ? args[i] : ''), '') : value);
  const H = window.Hub;
  const esc = (value) => H.escape(String(value == null ? '' : value));
  const icon = (name, size) => H.icon(name, size || 20);
  const fileById = (id) => H.state.files.find((file) => file.id === id);
  const tagById = (id) => H.state.tags.find((tag) => tag.id === id);
  const statusNames = { get Active() { return t('可用'); }, get Missing() { return t('文件失效'); }, get PendingRepair() { return t('待修复'); }, get SourceUnavailable() { return t('来源不可用'); } };
  const tagKinds = ['主题', '时期', '自定义'];
  const typeNames = { get png() { return t('图像'); }, get jpg() { return t('图像'); }, get jpeg() { return t('图像'); }, get pdf() { return t('PDF 文档'); }, get docx() { return t('Word 文档'); }, get xlsx() { return t('表格'); }, get folder() { return t('文件夹'); }, get zip() { return t('压缩包'); }, get md() { return t('文本'); }, get txt() { return t('文本'); } };
  const typeIcon = (type) => ({ png: 'image', jpg: 'image', jpeg: 'image', pdf: 'document', docx: 'document', xlsx: 'table', folder: 'folder', zip: 'archive', md: 'file', txt: 'file' }[type] || 'file');
  const ids = () => `demo-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  const selectedAttr = (value, expected) => value === expected ? ' selected' : '';
  const checkedAttr = (value) => value ? ' checked' : '';
  const disabledAttr = (value) => value ? ' disabled' : '';
  const tagLabel = (id) => tagById(id)?.name || t('未命名标签');
  const tagsOf = (file) => (file.tags || []).filter((id) => tagById(id));
  const suggestionsOf = (file) => (file.suggestions || []).filter((id) => tagById(id) && !tagsOf(file).includes(id));
  function rerender() {
    const current = document.activeElement;
    const key = current?.dataset?.change;
    const selection = current && typeof current.selectionStart === 'number' ? [current.selectionStart, current.selectionEnd] : null;
    H.render();
    if (key) {
      const next = Array.from(document.querySelectorAll('[data-change]')).find((element) => element.dataset.change === key);
      if (next) {
        next.focus();
        if (selection && typeof next.setSelectionRange === 'function') next.setSelectionRange(...selection);
      }
    }
  }
  function saveAndRender(message) { H.save(); H.closeModal(); rerender(); if (message) H.toast(message); }
  function pageHeading(title, subtitle, actions = '') {
    return `<div class="page-heading"><div><div class="eyebrow">WORKSPACE / RESOURCES</div><h1 class="page-title">${title}</h1><p class="subtitle">${subtitle}</p></div>${actions ? `<div class="toolbar">${actions}</div>` : ''}</div>`;
  }
  function tagChips(file, limit) {
    const tags = tagsOf(file);
    const visible = typeof limit === 'number' ? tags.slice(0, limit) : tags;
    return visible.map((id) => t`<button class="tag-chip" data-action="resource-filter-tag" data-id="${esc(id)}" title="查看标签 ${esc(tagLabel(id))}">${esc(tagLabel(id))}</button>`).join('') + (typeof limit === 'number' && tags.length > limit ? `<span class="tag-chip muted">+${tags.length - limit}</span>` : '') || t('<span class="muted small">未添加标签</span>');
  }
  function statusBadge(file) { return `<span class="badge file-status ${file.status === 'Active' ? 'status-active' : 'status-warning'}">${file.status === 'Active' ? icon('check', 13) : icon('alert', 13)} ${statusNames[file.status] || t('可用')}</span>`; }
  function favoriteButton(file) { return `<button class="icon-button ${file.favorite ? 'is-favorite' : ''}" data-action="resource-favorite" data-id="${esc(file.id)}" aria-label="${file.favorite ? t('取消收藏') : t('收藏')} ${esc(file.name)}" aria-pressed="${!!file.favorite}" title="${file.favorite ? t('取消收藏') : t('收藏')}">${icon('star', 18)}</button>`; }
  function suggestionRow(file) {
    const suggestions = suggestionsOf(file);
    if (!suggestions.length) return '';
    return t`<div class="suggestion-row"><span class="small muted">建议标签</span><div class="file-tags">${suggestions.map((id) => `<span class="tag-chip suggested">${esc(tagLabel(id))}</span>`).join('')}</div><div class="resource-actions"><button class="button quiet small" data-action="resource-suggestion-confirm" data-id="${esc(file.id)}">确认</button><button class="button quiet small" data-action="resource-suggestion-edit" data-id="${esc(file.id)}">编辑</button><button class="icon-button small" data-action="resource-suggestion-reject" data-id="${esc(file.id)}" aria-label="拒绝 ${esc(file.name)} 的建议标签" title="拒绝建议">${icon('close', 16)}</button></div></div>`;
  }
  function fileCard(file) {
    return t`<article class="resource-card"><div class="resource-card-top"><button class="resource-type-icon" data-action="resource-detail" data-id="${esc(file.id)}" aria-label="查看 ${esc(file.name)}">${icon(typeIcon(file.type), 28)}</button>${favoriteButton(file)}</div><button class="resource-name" data-action="resource-detail" data-id="${esc(file.id)}">${esc(file.name)}</button><div class="resource-meta">${esc(typeNames[file.type] || file.type.toUpperCase())} · ${esc(file.created || '2026-10-08')}</div><div class="file-tags">${tagChips(file, 3)}</div>${statusBadge(file)}${suggestionRow(file)}<div class="resource-card-footer"><button class="button quiet small" data-action="resource-open" data-id="${esc(file.id)}">${icon(file.type === 'folder' ? 'folder' : 'external', 15)} ${file.type === 'folder' ? t('临时浏览') : t('打开 · 模拟')}</button><button class="icon-button" data-action="resource-detail" data-id="${esc(file.id)}" aria-label="${esc(file.name)} 详情">${icon('more', 18)}</button></div></article>`;
  }
  function fileList(files, selectable = false) {
    const selections = H.ui.missingSelected || [];
    return t`<div class="resource-table-wrap"><table class="resource-table"><thead><tr>${selectable ? t('<th class="selection-cell"><span class="sr-only">选择</span></th>') : ''}<th>名称</th><th>标签</th><th>状态</th><th class="resource-date-column">创建时间</th><th class="resource-actions-column"><span class="sr-only">操作</span></th></tr></thead><tbody>${files.map((file) => t`<tr>${selectable ? t`<td class="selection-cell"><input type="checkbox" data-change="resource-select-missing" data-id="${esc(file.id)}" aria-label="选择 ${esc(file.name)}"${checkedAttr(selections.includes(file.id))}></td>` : ''}<td><div class="table-name">${icon(typeIcon(file.type), 22)}<div><button class="resource-name" data-action="resource-detail" data-id="${esc(file.id)}">${esc(file.name)}</button><div class="small muted resource-path" title="${esc(file.path)}">${esc(file.path)}</div>${suggestionRow(file)}</div></div></td><td><div class="file-tags">${tagChips(file, 3)}</div></td><td>${statusBadge(file)}</td><td class="resource-date-column small muted">${esc(file.created || '2026-10-08')}</td><td><div class="resource-actions">${favoriteButton(file)}${file.status !== 'Active' ? t`<button class="button quiet small" data-action="resource-repair" data-id="${esc(file.id)}">修复</button>` : `<button class="icon-button" data-action="resource-open" data-id="${esc(file.id)}" aria-label="${file.type === 'folder' ? t('临时浏览') : t('模拟打开')} ${esc(file.name)}" title="${file.type === 'folder' ? t('临时浏览') : t('模拟打开')}">${icon(file.type === 'folder' ? 'folder' : 'external', 17)}</button>`}<button class="icon-button" data-action="resource-detail" data-id="${esc(file.id)}" aria-label="查看 ${esc(file.name)} 详情">${icon('more', 18)}</button></div></td></tr>`).join('')}</tbody></table></div>`;
  }
  function empty(title, description, button = '') { return `<div class="empty-state">${icon('folder', 32)}<h3>${title}</h3><p>${description}</p>${button}</div>`; }
  function filterFiles(query, tag, files = H.state.files, type = 'all') {
    const q = (query || '').trim().toLowerCase();
    return files.filter((file) => (!q || file.name.toLowerCase().includes(q) || tagsOf(file).some((id) => tagLabel(id).toLowerCase().includes(q))) && (!tag || tag === 'all' || tagsOf(file).includes(tag)) && (type === 'all' || file.type === type));
  }
  function tagOptions(current = 'all') { return t`<option value="all">全部标签</option>${tagKinds.map((kind) => `<optgroup label="${t(kind)}">${H.state.tags.filter((tag) => tag.kind === kind).map((tag) => `<option value="${esc(tag.id)}"${selectedAttr(current, tag.id)}>${esc(tag.name)}</option>`).join('')}</optgroup>`).join('')}`; }
  function libraryControls() {
    const view = H.ui.libraryView || 'list';
    return t`<div class="toolbar resource-filter"><label class="search-field resource-search">${icon('search', 18)}<input class="input" type="search" placeholder="搜索文件名或标签" aria-label="搜索文件名或标签" value="${esc(H.ui.fileQuery || '')}" data-change="resource-query"></label><select class="select" aria-label="筛选正式标签" data-change="resource-tag-filter">${tagOptions(H.ui.fileTag || 'all')}</select><div class="segmented" aria-label="资源库视图"><button data-action="resource-view" data-view="list" aria-pressed="${view === 'list'}" class="${view === 'list' ? 'active' : ''}" title="列表视图">${icon('list', 17)} 列表</button><button data-action="resource-view" data-view="grid" aria-pressed="${view === 'grid'}" class="${view === 'grid' ? 'active' : ''}" title="图标网格视图">${icon('grid', 17)} 图标</button><button data-action="resource-view" data-view="tags" aria-pressed="${view === 'tags'}" class="${view === 'tags' ? 'active' : ''}" title="标签视图">${icon('tag', 17)} 标签</button></div></div>`;
  }
  H.pages.library = () => {
    const files = filterFiles(H.ui.fileQuery, H.ui.fileTag);
    const view = H.ui.libraryView || 'list';
    let body = '';
    if (!files.length) body = empty(t('没有匹配的资源'), t('调整搜索词或标签筛选，查看本地模拟索引。'), t('<button class="button" data-action="resource-reset-filters">清除筛选</button>'));
    else if (view === 'grid') body = `<div class="resource-grid">${files.map(fileCard).join('')}</div>`;
    else if (view === 'tags') {
      const kind = H.ui.libraryTagKind || '主题';
      const groupedTags = H.state.tags.filter((tag) => tag.kind === kind && (!H.ui.fileTag || H.ui.fileTag === 'all' || H.ui.fileTag === tag.id));
      body = t`<div class="toolbar"><div class="segmented" aria-label="标签分组类型">${tagKinds.map((item) => `<button class="${kind === item ? 'active' : ''}" data-action="resource-tag-kind" data-kind="${item}" aria-pressed="${kind === item}">${t(item)}</button>`).join('')}</div><span class="small muted">一个文件可以出现在多个集合中</span></div>${groupedTags.map((tag) => { const group = files.filter((file) => tagsOf(file).includes(tag.id)); return t`<section class="tag-group"><div class="tag-group-header"><h2>${icon('tag', 19)} ${esc(tag.name)}</h2><span class="muted small">${group.length} 个资源</span></div>${group.length ? `<div class="resource-grid">${group.map(fileCard).join('')}</div>` : t('<div class="panel small muted">这个标签还没有正式关联的资源。</div>')}</section>`; }).join('')}${files.filter((file) => !tagsOf(file).some((id) => tagById(id)?.kind === kind)).length ? t`<section class="tag-group"><div class="tag-group-header"><h2>未设置${t(kind)}标签</h2></div><div class="resource-grid">${files.filter((file) => !tagsOf(file).some((id) => tagById(id)?.kind === kind)).map(fileCard).join('')}</div></section>` : ''}`;
    } else body = `<div class="panel resource-table-panel">${fileList(files)}</div>`;
    const unavailable = H.state.files.filter((file) => file.status !== 'Active').length;
    return t`${pageHeading(t('文件资源库'), t('按你的方式虚拟归集。文件保留在原位置，界面仅使用模拟索引。'), '<button class="button" data-action="resource-add-demo">' + icon('plus', 17) + t(' 模拟新文件</button>'))}<div class="resource-summary"><span>${H.state.files.length} 个索引资源</span><span>类型图标 · 无缩略图</span><button class="button quiet small" data-action="resource-go-missing">${icon('alert', 15)} ${unavailable} 个失效资源</button></div>${libraryControls()}${body}<p class="small muted folder-note">文件夹默认不递归索引。点击文件夹可临时浏览模拟内容，临时浏览不会写入长期索引。</p>`;
  };
  H.pages.tags = () => {
    return t`${pageHeading(t('标签中心'), t('主题、时期与自定义标签共同描述资源，多个标签不会移动或复制原文件。'), '<button class="button primary" data-action="tag-create">' + icon('plus', 17) + t(' 新建标签</button>'))}${tagKinds.map((kind) => `<section class="tag-group"><div class="tag-group-header"><h2>${t(kind)}</h2><span class="small muted">${kind === '主题' ? t('按内容与用途归集') : kind === '时期' ? t('基于文件创建时间归集') : t('你的个人分类')}</span></div><div class="tag-manager-grid">${H.state.tags.filter((tag) => tag.kind === kind).map((tag) => { const count = H.state.files.filter((file) => tagsOf(file).includes(tag.id)).length; return t`<article class="panel tag-manager-card"><button class="tag-manager-open" data-action="resource-filter-tag" data-id="${esc(tag.id)}"><span class="resource-type-icon">${icon('tag', 23)}</span><div><h3>${esc(tag.name)}</h3><p class="small muted">${count} 个资源 · ${t(kind)}</p></div></button><div class="resource-actions"><button class="button quiet small" data-action="tag-edit" data-id="${esc(tag.id)}">编辑</button><button class="icon-button" data-action="tag-delete" data-id="${esc(tag.id)}" aria-label="删除标签 ${esc(tag.name)}" title="删除标签">${icon('trash', 16)}</button></div></article>`; }).join('') || t('<div class="panel muted small">尚未创建此类标签。</div>')}</div></section>`).join('')}<div class="panel"><div class="inline">${icon('check', 18)}<span class="small muted">规则建议需要你的确认。接受后才会成为正式标签关系；建议依据文件名、模拟目录与创建时间。</span></div></div>`;
  };
  H.pages.favorites = () => {
    const files = H.state.files.filter((file) => file.favorite);
    return t`${pageHeading(t('收藏'), t('留住常用资源。资源失效后，收藏与标签关系仍然保留。'))}<div class="resource-summary"><span>${files.length} 个收藏资源</span><span>本地模拟数据</span></div>${files.length ? `<div class="resource-grid">${files.map(fileCard).join('')}</div>` : empty(t('收藏常用资源'), t('在资源库点击星标，让常用文件出现在这里。'), t('<button class="button" data-action="resource-go-library">前往资源库</button>'))}`;
  };
  H.pages.search = () => {
    const query = H.ui.searchQuery || '';
    const tag = H.ui.searchTag || 'all';
    const files = filterFiles(query, tag);
    const hasFilter = query.trim() || tag !== 'all';
    return t`${pageHeading(t('快速搜索'), t('搜索文件名或正式标签。搜索范围仅限已确认的本地模拟索引。'))}<div class="panel search-panel"><div class="toolbar resource-filter"><label class="search-field">${icon('search', 21)}<input class="input" type="search" placeholder="搜索文件名、课程作业、设计素材…" aria-label="快速搜索文件名或正式标签" value="${esc(query)}" data-change="resource-search-query" autofocus></label><select class="select" aria-label="搜索标签筛选" data-change="resource-search-tag">${tagOptions(tag)}</select>${hasFilter ? t('<button class="button quiet" data-action="resource-search-clear">清除</button>') : ''}</div></div><div class="resource-summary"><span>${hasFilter ? t`找到 ${files.length} 个资源` : t('浏览全部资源，或输入搜索词')}</span><span>支持文件名与标签</span></div>${files.length ? `<div class="panel resource-table-panel search-results">${fileList(files)}</div>` : empty(t('没有找到相关资源'), t('检查文件名，或选择其他标签试试。'))}`;
  };
  H.pages.missing = () => {
    const allFiles = H.state.files.filter((file) => file.status !== 'Active');
    const filter = H.ui.missingFilter || 'all';
    const files = allFiles.filter((file) => filter === 'all' || file.status === filter);
    H.ui.missingSelected = (H.ui.missingSelected || []).filter((id) => allFiles.some((file) => file.id === id));
    const selectedCount = H.ui.missingSelected.length;
    return t`${pageHeading(t('失效资源中心'), t('索引保留标签与收藏。重新关联路径，或仅清理不再需要的模拟索引。'), '<button class="button" data-action="resource-go-library">' + icon('arrow-left', 17) + t(' 返回资源库</button>'))}<div class="toolbar resource-filter"><div class="segmented" aria-label="失效类型筛选">${[['all', t('全部')], ['Missing', t('文件失效')], ['PendingRepair', t('待修复')], ['SourceUnavailable', t('来源不可用')]].map(([value, label]) => `<button class="${filter === value ? 'active' : ''}" data-action="resource-missing-filter" data-filter="${value}" aria-pressed="${filter === value}">${label} <span class="muted">${value === 'all' ? allFiles.length : allFiles.filter((file) => file.status === value).length}</span></button>`).join('')}</div><div class="resource-actions"><button class="button quiet" data-action="resource-select-all-missing" data-filter="${filter}"${disabledAttr(!files.length)}>选择当前 ${files.length} 项</button><button class="button" data-action="resource-clean-selected"${disabledAttr(!selectedCount)}>${icon('trash', 16)} 清理索引${selectedCount ? ` (${selectedCount})` : ''}</button></div></div><div class="panel inline"><span class="resource-type-icon">${icon('alert', 21)}</span><p class="small muted">文件失效：原路径不可用。待修复：检测到可能的移动。来源不可用：模拟索引目录暂时离线。此原型不会检查或改动磁盘文件。</p></div>${files.length ? `<div class="panel resource-table-panel">${fileList(files, true)}</div>` : empty(t('当前没有此类失效资源'), t('你的标签和收藏关系会一直保留，直到你确认清理对应索引。'))}`;
  };
  H.actions['resource-query'] = (element) => { H.ui.fileQuery = element.value; rerender(); };
  H.actions['resource-tag-filter'] = (element) => { H.ui.fileTag = element.value; rerender(); };
  H.actions['resource-view'] = (element) => { H.ui.libraryView = element.dataset.view; rerender(); };
  H.actions['resource-tag-kind'] = (element) => { H.ui.libraryTagKind = element.dataset.kind; if (H.ui.fileTag && H.ui.fileTag !== 'all' && tagById(H.ui.fileTag)?.kind !== element.dataset.kind) H.ui.fileTag = 'all'; rerender(); };
  H.actions['resource-reset-filters'] = () => { H.ui.fileQuery = ''; H.ui.fileTag = 'all'; rerender(); };
  H.actions['resource-filter-tag'] = (element) => { H.ui.fileTag = element.dataset.id; H.ui.fileQuery = ''; H.ui.libraryTagKind = tagById(element.dataset.id)?.kind || '主题'; H.ui.libraryView = 'tags'; H.closeModal(); H.navigate('library'); };
  H.actions['resource-go-library'] = () => H.navigate('library');
  H.actions['resource-go-missing'] = () => H.navigate('missing');
  H.actions['resource-favorite'] = (element) => { const file = fileById(element.dataset.id); if (!file) return; file.favorite = !file.favorite; H.save(); rerender(); H.toast(file.favorite ? t('已加入本地模拟收藏') : t('已取消收藏')); };
  H.actions['resource-search-query'] = (element) => { H.ui.searchQuery = element.value; rerender(); };
  H.actions['resource-search-tag'] = (element) => { H.ui.searchTag = element.value; rerender(); };
  H.actions['resource-search-clear'] = () => { H.ui.searchQuery = ''; H.ui.searchTag = 'all'; rerender(); };
  H.actions['resource-missing-filter'] = (element) => { H.ui.missingFilter = element.dataset.filter; rerender(); };
  H.actions['resource-select-missing'] = (element) => { const set = new Set(H.ui.missingSelected || []); element.checked ? set.add(element.dataset.id) : set.delete(element.dataset.id); H.ui.missingSelected = Array.from(set); rerender(); };
  H.actions['resource-select-all-missing'] = (element) => { H.ui.missingSelected = H.state.files.filter((file) => file.status !== 'Active' && (element.dataset.filter === 'all' || file.status === element.dataset.filter)).map((file) => file.id); rerender(); };
  function tagCheckboxes(selected, attr = 'data-resource-tag') {
    return `<div class="tag-choice-list">${tagKinds.map((kind) => `<div><p class="small muted">${t(kind)}</p><div class="tag-choice-group">${H.state.tags.filter((tag) => tag.kind === kind).map((tag) => `<label class="check-row"><input type="checkbox" ${attr}="${esc(tag.id)}"${checkedAttr(selected.includes(tag.id))}><span>${esc(tag.name)}</span></label>`).join('') || t('<span class="small muted">暂无标签</span>')}</div></div>`).join('')}</div>`;
  }
  H.actions['resource-detail'] = (element) => {
    const file = fileById(element.dataset.id); if (!file) return;
    H.ui.detailFile = file.id;
    H.showModal(t('资源详情'), t`<div class="resource-detail-heading"><span class="resource-type-icon">${icon(typeIcon(file.type), 30)}</span><div><h3>${esc(file.name)}</h3><p class="small muted">${esc(typeNames[file.type] || file.type)} · 本地模拟索引</p></div>${statusBadge(file)}</div><div class="detail-row"><span>原始路径</span><code class="resource-detail-path">${esc(file.path)}</code></div><div class="detail-row"><span>创建时间</span><span>${esc(file.created || '2026-10-08')}</span></div><div class="detail-row"><span>来源目录</span><span>${esc(H.state.sources.find((source) => source.id === file.source)?.name || t('个人桌面 · 模拟'))}</span></div><div class="divider"></div><h3 class="small">正式标签</h3><p class="small muted">选择多个标签，保存后生效。不会移动原文件。</p>${tagCheckboxes(tagsOf(file))}${suggestionsOf(file).length ? t`<div class="panel"><span class="small muted">另有 ${suggestionsOf(file).length} 个待确认建议，可返回资源库处理。</span></div>` : ''}<label class="check-row"><input id="resource-detail-favorite" type="checkbox"${checkedAttr(file.favorite)}><span>收藏此资源</span></label>`, t`<button class="button quiet" data-action="resource-locate" data-id="${esc(file.id)}">${icon('folder', 16)} 定位 · 模拟</button>${file.status !== 'Active' ? t`<button class="button" data-action="resource-repair" data-id="${esc(file.id)}">修复 · 模拟</button>` : `<button class="button" data-action="resource-open" data-id="${esc(file.id)}">${file.type === 'folder' ? t('临时浏览') : t('打开 · 模拟')}</button>`}<button class="button primary" data-action="resource-detail-save" data-id="${esc(file.id)}">保存标签与收藏</button>`, { wide: true });
  };
  H.actions['resource-detail-save'] = (element) => {
    const file = fileById(element.dataset.id); if (!file) return;
    file.tags = Array.from(document.querySelectorAll('[data-resource-tag]:checked')).map((input) => input.dataset.resourceTag);
    file.favorite = !!document.getElementById('resource-detail-favorite')?.checked;
    file.suggestions = suggestionsOf(file);
    saveAndRender(t('标签与收藏已保存至模拟索引，原文件保持原位置'));
  };
  H.actions['resource-open'] = (element) => {
    const file = fileById(element.dataset.id); if (!file) return;
    if (file.status !== 'Active') { H.toast(t('模拟打开未执行：资源失效，请先重新关联'), 'warning'); return; }
    if (file.type !== 'folder') { H.toast(t`模拟打开：${file.name}。未调用系统程序。`); return; }
    const folderFiles = [{ name: t('草稿笔记.md'), type: 'md' }, { name: t('素材清单.xlsx'), type: 'xlsx' }, { name: t('参考图片.png'), type: 'png' }];
    H.showModal(t('文件夹临时浏览'), t`<div class="panel"><p class="small muted">模拟目录：${esc(file.path)}</p><p class="small">以下为预置的模拟内容，尚未加入长期索引。没有读取磁盘文件。</p></div><div class="stack">${folderFiles.map((item) => t`<div class="list-row"><span class="resource-type-icon">${icon(typeIcon(item.type), 22)}</span><div class="grow"><strong>${item.name}</strong><div class="small muted">临时浏览 · 未索引</div></div><button class="button quiet small" data-action="resource-temp-open" data-name="${item.name}">打开 · 模拟</button></div>`).join('')}</div>`, t`<button class="button" data-action="resource-locate" data-id="${esc(file.id)}">资源管理器打开 · 模拟</button><button class="button primary" data-action="resource-modal-close">完成</button>`, { wide: true });
  };
  H.actions['resource-temp-open'] = (element) => H.toast(t`模拟打开临时资源：${element.dataset.name}。未加入索引。`);
  H.actions['resource-locate'] = (element) => { const file = fileById(element.dataset.id); if (file) H.toast(t`模拟定位：${file.path}。未打开资源管理器。`); };
  H.actions['resource-modal-close'] = () => H.closeModal();
  H.actions['resource-suggestion-confirm'] = (element) => {
    const file = fileById(element.dataset.id); if (!file) return;
    const suggestions = suggestionsOf(file); if (!suggestions.length) return;
    H.showModal(t('确认建议标签'), t`<p>将以下建议关联到 <strong>${esc(file.name)}</strong>：</p><div class="file-tags">${suggestions.map((id) => `<span class="tag-chip">${esc(tagLabel(id))}</span>`).join('')}</div><p class="small muted">建议来自文件名、模拟目录与文件创建时间。确认后才写入正式标签关系，不移动原文件。</p>`, t`<button class="button" data-action="resource-modal-close">取消</button><button class="button primary" data-action="resource-suggestion-accept" data-id="${esc(file.id)}">确认接受</button>`);
  };
  H.actions['resource-suggestion-accept'] = (element) => {
    const file = fileById(element.dataset.id); if (!file) return;
    file.tags = Array.from(new Set([...tagsOf(file), ...suggestionsOf(file)])); file.suggestions = [];
    saveAndRender(t('建议已确认，已加入正式标签关系 · 模拟'));
  };
  H.actions['resource-suggestion-reject'] = (element) => { const file = fileById(element.dataset.id); if (!file) return; file.suggestions = []; H.save(); rerender(); H.toast(t('已拒绝建议，正式标签未改变')); };
  H.actions['resource-suggestion-edit'] = (element) => {
    const file = fileById(element.dataset.id); if (!file) return;
    H.showModal(t('编辑标签建议'), t`<p class="small muted">调整 <strong>${esc(file.name)}</strong> 的待确认标签。此处保存仍为建议，不写入正式关系。</p>${tagCheckboxes(suggestionsOf(file), 'data-suggestion-tag')}`, t`<button class="button" data-action="resource-modal-close">取消</button><button class="button primary" data-action="resource-suggestion-save" data-id="${esc(file.id)}">保存建议</button>`, { wide: true });
  };
  H.actions['resource-suggestion-save'] = (element) => { const file = fileById(element.dataset.id); if (!file) return; file.suggestions = Array.from(document.querySelectorAll('[data-suggestion-tag]:checked')).map((input) => input.dataset.suggestionTag).filter((id) => !tagsOf(file).includes(id)); saveAndRender(t('建议已更新，接受前不会成为正式标签')); };
  H.actions['resource-add-demo'] = () => {
    H.showModal(t('模拟发现新文件'), t`<div class="inline"><span class="resource-type-icon">${icon('image', 30)}</span><div><h3>设计参考图.png</h3><p class="small muted">个人桌面 · 创建于 2026 年 10 月</p></div></div><p class="small muted">添加一条预置的模拟索引，并展示课程作业、设计素材与 2026 年 10 月的待确认建议。不会扫描或创建真实文件。</p>`, t('<button class="button" data-action="resource-modal-close">取消</button><button class="button primary" data-action="resource-add-demo-confirm">添加模拟资源</button>'));
  };
  H.actions['resource-add-demo-confirm'] = () => {
    const suggestedNames = [[t('课程作业'), '主题'], [t('设计素材'), '主题'], [t('2026年10月'), '时期']];
    const suggestions = suggestedNames.map(([name, kind]) => { let tag = H.state.tags.find((entry) => entry.name.replace(/\s/g, '') === name); if (!tag) { tag = { id: ids(), name, kind }; H.state.tags.push(tag); } return tag.id; });
    const count = H.state.files.filter((file) => file.name.startsWith(t('设计参考图'))).length;
    const name = count ? t`设计参考图 (${count + 1}).png` : t('设计参考图.png');
    H.state.files.unshift({ id: ids(), name, type: 'png', path: `C:\\Users\\Demo\\Desktop\\${name}`, tags: [], suggestions, favorite: false, status: 'Active', created: '2026-10-08', source: H.state.sources[0]?.id || 'desktop' });
    H.ui.fileQuery = ''; H.ui.fileTag = 'all'; H.ui.libraryView = 'grid';
    saveAndRender(t('已发现新文件 · 模拟。建议标签等待你确认。'));
  };
  function showTagEditor(tag) {
    H.showModal(tag ? t('编辑标签') : t('新建标签'), t`<div class="form-grid"><label class="field"><span>标签名称</span><input class="input" id="tag-editor-name" type="text" maxlength="32" placeholder="例如：设计素材" value="${esc(tag?.name || '')}" autofocus></label><label class="field"><span>类型</span><select class="select" id="tag-editor-kind">${tagKinds.map((kind) => `<option value="${kind}"${selectedAttr(tag?.kind || '自定义', kind)}>${t(kind)}</option>`).join('')}</select></label></div><p id="tag-editor-error" class="form-error" role="alert"></p><p class="small muted">标签仅组织模拟索引，可同时关联多个资源。</p>`, t`<button class="button" data-action="resource-modal-close">取消</button><button class="button primary" data-action="tag-save" data-id="${esc(tag?.id || '')}">保存标签</button>`);
  }
  H.actions['tag-create'] = () => showTagEditor(null);
  H.actions['tag-edit'] = (element) => showTagEditor(tagById(element.dataset.id));
  H.actions['tag-save'] = (element) => {
    const name = document.getElementById('tag-editor-name')?.value.trim();
    const kind = document.getElementById('tag-editor-kind')?.value || '自定义';
    const error = document.getElementById('tag-editor-error');
    if (!name) { if (error) error.textContent = t('请输入标签名称。'); document.getElementById('tag-editor-name')?.focus(); return; }
    if (H.state.tags.some((tag) => tag.id !== element.dataset.id && tag.name === name && tag.kind === kind)) { if (error) error.textContent = t('此类型下已存在同名标签。'); return; }
    const tag = tagById(element.dataset.id);
    if (tag) { tag.name = name; tag.kind = kind; } else H.state.tags.push({ id: ids(), name, kind });
    saveAndRender(t('标签已保存至本地模拟数据'));
  };
  H.actions['tag-delete'] = (element) => {
    const tag = tagById(element.dataset.id); if (!tag) return;
    const count = H.state.files.filter((file) => tagsOf(file).includes(tag.id)).length;
    H.showModal(t('删除标签'), t`<p>删除 <strong>${esc(tag.name)}</strong> 及其与 ${count} 个资源的关联？</p><p class="small muted">只移除标签关系，资源索引、收藏与原文件保持不变。</p>`, t`<button class="button" data-action="resource-modal-close">取消</button><button class="button primary" data-action="tag-delete-confirm" data-id="${esc(tag.id)}">删除标签</button>`);
  };
  H.actions['tag-delete-confirm'] = (element) => {
    const id = element.dataset.id;
    H.state.tags = H.state.tags.filter((tag) => tag.id !== id);
    H.state.files.forEach((file) => { file.tags = (file.tags || []).filter((tag) => tag !== id); file.suggestions = (file.suggestions || []).filter((tag) => tag !== id); });
    if (H.ui.fileTag === id) H.ui.fileTag = 'all'; if (H.ui.searchTag === id) H.ui.searchTag = 'all';
    saveAndRender(t('标签及关联已删除，资源索引与收藏仍保留'));
  };
  H.actions['resource-repair'] = (element) => {
    const file = fileById(element.dataset.id); if (!file) return;
    const target = file.path.replace(/Desktop/g, 'Documents\\HubDemo');
    H.showModal(t('重新关联资源 · 模拟'), t`<div class="inline"><span class="resource-type-icon">${icon('repair', 26)}</span><div><h3>${esc(file.name)}</h3><p class="small muted">${statusNames[file.status]} · ${tagsOf(file).length} 个标签${file.favorite ? t(' · 已收藏') : ''}</p></div></div><label class="field"><span>原模拟路径</span><code class="resource-detail-path">${esc(file.path)}</code></label><label class="field"><span>重新关联的模拟路径</span><input class="input" id="resource-repair-path" value="${esc(target)}" aria-label="重新关联的模拟路径"></label><p class="small muted">仅修改本地模拟索引中的路径与状态。现有标签、收藏不会丢失，不检查磁盘文件。</p><p id="resource-repair-error" class="form-error" role="alert"></p>`, t`<button class="button" data-action="resource-modal-close">取消</button><button class="button primary" data-action="resource-repair-confirm" data-id="${esc(file.id)}">确认重新关联 · 模拟</button>`);
  };
  H.actions['resource-repair-confirm'] = (element) => {
    const file = fileById(element.dataset.id); if (!file) return;
    const path = document.getElementById('resource-repair-path')?.value.trim();
    if (!path) { const error = document.getElementById('resource-repair-error'); if (error) error.textContent = t('请输入用于演示的模拟路径。'); return; }
    file.path = path; file.status = 'Active'; H.ui.missingSelected = (H.ui.missingSelected || []).filter((id) => id !== file.id);
    saveAndRender(t('重新关联成功 · 模拟。标签与收藏已保留。'));
  };
  H.actions['resource-clean-selected'] = () => {
    const selected = H.ui.missingSelected || [];
    const files = H.state.files.filter((file) => selected.includes(file.id) && file.status !== 'Active'); if (!files.length) return;
    H.showModal(t('仅清理模拟索引'), t`<p>将清理 ${files.length} 个失效资源的索引、标签关联与收藏记录：</p><div class="cleanup-preview">${files.map((file) => `<div class="list-row">${icon(typeIcon(file.type), 18)}<span>${esc(file.name)}</span><span class="badge">${statusNames[file.status]}</span></div>`).join('')}</div><p class="small muted">此操作只更新浏览器中的模拟数据，不会删除、移动或修改任何真实文件。可在设置中重置演示数据。</p>`, t('<button class="button" data-action="resource-modal-close">取消</button><button class="button primary" data-action="resource-clean-confirm">确认清理索引 · 模拟</button>'), { wide: true });
  };
  H.actions['resource-clean-confirm'] = () => {
    const selected = H.ui.missingSelected || [];
    const remove = H.state.files.filter((file) => selected.includes(file.id) && file.status !== 'Active').map((file) => file.id);
    H.state.files = H.state.files.filter((file) => !remove.includes(file.id)); H.ui.missingSelected = [];
    saveAndRender(t`已清理 ${remove.length} 条模拟索引。真实文件未受影响。`);
  };
})();
