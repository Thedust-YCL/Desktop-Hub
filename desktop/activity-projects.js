/* Workspace activity and reference-only project collections. Native success owns history writes. */
(function () {
  'use strict';
  const H = window.Hub, bridge = window.DesktopHubNative.bridge;
  if (window.DesktopHubDesktop.getState().surface !== 'workspace') return;
  const e = value => H.escape(String(value ?? ''));
  const clone = value => JSON.parse(JSON.stringify(value));
  const types = { app: '应用', scene: '场景', file: '文件', folder: '文件夹', web: '网页' };
  const groups = { app: '应用', scene: '场景', files: '文件与文件夹', web: '网页' };
  const group = type => type === 'file' || type === 'folder' ? 'files' : type;
  const status = { Active: '可用', Removed: '已移除 · 关联失效', Missing: '文件缺失，重新关联', PendingRepair: '需要重新关联', SourceUnavailable: '来源不可用，请在文件库核对访问权限', Disabled: '场景已停用，可编辑', Invalid: '场景关联失效，可编辑' };
  let activity = [], projects = [], selected = null, resolved = [], projectId = null;
  let activityWarning = '', projectWarning = '', activityError = '', projectError = '';
  let activityLoaded = false, projectsLoaded = false, activityBusy = false, projectsBusy = false;
  let activityGeneration = 0, projectGeneration = 0, intent = 0, draft = null, draftNames = new Map();
  let selection = null, searchTimer = null, selectionGeneration = 0, composing = false, writing = false, resumeType = null, pendingRestore = null, restoreToken = 0;
  const button = (action, label, attrs = '', kind = '') => `<button class="button small ${kind}" data-action="${action}" ${attrs}>${label}</button>`;
  const idAttr = id => `data-id="${e(id)}"`;
  const error = () => '<p id="ap-error" class="form-error" role="alert"></p><details id="ap-error-details" hidden><summary>诊断详情</summary><p id="ap-error-diagnostic" class="ap-detail"></p></details>';
  const failureText = err => err.code === 'NATIVE_ERROR' ? '操作未完成，请检查存储位置的访问权限后重试。' : err.message || '操作未完成，请重试。';
  const notice = text => text ? `<p class="notice warning" role="status">${e(text)}</p>` : '';
  const storageNotice = (text, label) => text ? `<div class="notice warning" role="status"><p>${label}暂时无法读取或保存，原数据已保留。请重试；持续失败时请修复后重启或升级应用。</p><details><summary>诊断详情</summary><p class="ap-detail">${e(text)}</p></details></div>` : '';
  const heading = (name, tools = '') => `<div class="ap-heading"><h1 class="page-title" tabindex="-1">${e(name)}</h1><div class="toolbar">${tools}</div></div>`;
  const message = (text, action) => `<div class="empty-state"><p>${e(text)}</p>${action || ''}</div>`;
  const emptyProject = () => ({ id: window.crypto.randomUUID(), name: '', description: '', displayOrder: Math.min(projects.length, 199), pinnedToHome: false, items: [] });
  const originalNavigate = H.navigate, originalShowModal = H.showModal, originalCloseModal = H.closeModal;
  H.navigate = function (...args) {
    ++intent;
    // History supplies project state immediately before its destination navigation.
    // Only that first matching navigation may claim the restoration token.
    if (pendingRestore?.awaitingNavigation && args[0] === pendingRestore.page && projectId === pendingRestore.projectId) pendingRestore.awaitingNavigation = false;
    else { pendingRestore = null; ++restoreToken; }
    return originalNavigate.apply(this, args);
  };
  H.showModal = function (...args) { ++intent; ++selectionGeneration; return originalShowModal.apply(this, args); };
  H.closeModal = function (...args) { ++intent; ++selectionGeneration; return originalCloseModal.apply(this, args); };
  function repaint() {
    if (H.ui.modal || !['home', 'activity', 'projects', 'project'].includes(H.ui.page)) return;
    const content = document.querySelector('.workspace-content'), scroll = content?.scrollTop || 0;
    H.render();
    const current = document.querySelector('.workspace-content'); if (current) current.scrollTop = scroll;
  }
  function modalError(err, modal) {
    if (H.ui.modal !== modal) return;
    const node = document.querySelector('#ap-error'); if (node) node.textContent = failureText(err);
    const details = document.querySelector('#ap-error-details'), diagnostic = document.querySelector('#ap-error-diagnostic');
    if (details) details.hidden = err.code !== 'NATIVE_ERROR';
    if (diagnostic) diagnostic.textContent = err.code === 'NATIVE_ERROR' ? err.message : '';
  }
  async function refreshActivity() {
    const generation = ++activityGeneration; activityBusy = true; activityError = '';
    try {
      const result = await bridge.request('activity.query', {});
      if (generation !== activityGeneration) return;
      activity = result.items || []; activityWarning = result.warning || ''; activityLoaded = true;
    } catch (err) { if (generation === activityGeneration) activityError = failureText(err); }
    finally { if (generation === activityGeneration) { activityBusy = false; repaint(); } }
  }
  async function refreshProjects() {
    const generation = ++projectGeneration, requested = projectId, restoration = pendingRestore; projectsBusy = true; projectError = '';
    try {
      const result = await bridge.request('projects.query', requested ? { projectId: requested } : {});
      if (generation !== projectGeneration || requested !== projectId) return;
      projects = result.projects || []; projectWarning = result.warning || ''; projectsLoaded = true;
      if (requested) {
        selected = result.project || null; resolved = result.items || [];
        if (!selected && H.ui.page === 'project' && !H.ui.modal) { projectId = null; H.navigate('projects'); H.toast('项目已删除或移除。'); }
      }
    } catch (err) { if (generation === projectGeneration) projectError = failureText(err); }
    finally {
      if (generation === projectGeneration) {
        projectsBusy = false; repaint();
        if (restoration && pendingRestore === restoration && restoration.token === restoreToken) {
          pendingRestore = null;
          if (!restoration.awaitingNavigation && H.ui.page === restoration.page && projectId === restoration.projectId && !H.ui.modal) {
            const content = document.querySelector('.workspace-content'); if (content) content.scrollTop = restoration.scroll;
            window.DesktopHubInteractions?.checkpoint();
          }
        }
      }
    }
  }
  function open(id) {
    if (!id || H.ui.modal) return false;
    if (H.ui.page === 'editor' && H.ui.sceneDraftDirty) { H.navigate('projects'); return false; }
    const revision = ++intent, generation = ++projectGeneration;
    void bridge.request('projects.query', { projectId: id }).then(result => {
      if (revision !== intent || generation !== projectGeneration || H.ui.modal) return;
      window.DesktopHubInteractions?.checkpoint(); pendingRestore = null; ++restoreToken;
      projects = result.projects || []; selected = result.project || null; resolved = result.items || [];
      projectWarning = result.warning || ''; projectError = ''; projectsLoaded = true;
      projectId = selected ? selected.id : null;
      H.navigate(selected ? 'project' : 'projects');
      if (!selected) H.toast('项目已删除或移除。');
    }).catch(err => { if (revision === intent && generation === projectGeneration) H.toast(err.message, 'error'); });
    return true;
  }
  const actionLabel = row => row.status === 'Removed' ? '已移除' : ['Missing', 'PendingRepair'].includes(row.status) ? '重新关联' : ['Disabled', 'Invalid'].includes(row.status) ? '编辑场景' : row.status === 'SourceUnavailable' ? '查看来源' : '打开';
  function referenceRow(row, attrs, action) {
    const type = row.item?.type || row.record?.type;
    const stamp = row.record ? ` · ${e(new Date(row.record.at).toLocaleString())} · ${row.record.result === 'execution_established' ? '已建立场景执行' : row.record.result === 'started' ? '已启动' : '已提交打开'}` : '';
    return `<div class="ap-row"><div class="ap-row-info"><strong>${e(row.name)}</strong><p>${e(types[type])} · ${e(status[row.status] || row.status)}${stamp}</p><p class="ap-detail" title="${e(row.detail)}">${e(row.detail)}</p></div>${button(action, actionLabel(row), `${attrs} ${row.status === 'Removed' ? 'disabled' : ''}`)}</div>`;
  }
  function activityRows(limit = 50) { return activity.slice(0, limit).map((row, i) => referenceRow(row, `data-recent-index="${i}"`, 'ap-recent-open')).join(''); }
  function activityState(limit) {
    return storageNotice(activityWarning, '最近使用') + (activityError ? notice(activityError) + button('ap-activity-retry', '重试') : '') +
      (activity.length ? activityRows(limit) : message(activityBusy ? '正在读取最近使用…' : '成功打开应用、场景或文件后，会显示在这里。'));
  }
  function projectRow(p, compact = false) {
    return `<div class="ap-row"><div class="ap-row-info"><button class="text-button ap-name" data-action="ap-open" ${idAttr(p.id)}>${e(p.name)}</button><p>${e(p.description)}</p><small>${p.itemCount ?? p.items?.length ?? 0} 个关联${p.pinnedToHome ? ' · 已固定首页' : ''}</small></div><div class="toolbar">${button('ap-open', '查看', idAttr(p.id))}${compact ? '' : button('ap-edit', '编辑', idAttr(p.id)) + button('ap-project-move', '↑', `${idAttr(p.id)} data-direction="-1" aria-label="向前移动 ${e(p.name)}" ${p.displayOrder === 0 ? 'disabled' : ''}`) + button('ap-project-move', '↓', `${idAttr(p.id)} data-direction="1" aria-label="向后移动 ${e(p.name)}" ${p.displayOrder >= projects.length - 1 ? 'disabled' : ''}`) + button('ap-remove', '移除…', idAttr(p.id))}</div></div>`;
  }
  H.pages.activity = () => `<div class="ap-page">${heading('最近使用', button('ap-clear', '清空…', activity.length ? '' : 'disabled'))}${activityState(50)}</div>`;
  H.pages.projects = () => `<div class="ap-page">${heading('项目集合', button('ap-new', '新建项目', '', 'primary'))}<p class="subtitle">把相关应用、场景、文件和网页放在一起，按需逐项打开。</p>${storageNotice(projectWarning, '项目')}${projectError ? notice(projectError) + button('ap-projects-retry', '重试') : ''}${projects.map(p => projectRow(p)).join('') || message(projectsBusy ? '正在读取项目…' : '创建项目，再添加已有内容的关联。原文件留在原处。')}</div>`;
  H.pages.project = () => `<div class="ap-page">${heading(selected?.name || '项目', button('ap-list', '所有项目') + (selected ? button('ap-edit', '编辑项目', idAttr(selected.id)) : ''))}${storageNotice(projectWarning, '项目')}${projectError ? notice(projectError) + button('ap-projects-retry', '重试') : ''}<p class="ap-description">${e(selected?.description)}</p>${selected ? Object.keys(groups).map(type => { const rows = resolved.filter(row => group(row.item.type) === type); return rows.length ? `<section class="ap-group"><h2>${groups[type]}</h2>${rows.map(row => referenceRow(row, idAttr(row.item.id), 'ap-item-open')).join('')}</section>` : ''; }).join('') || message('项目尚无关联。选择“编辑项目”添加内容。') : message('正在读取项目…')}</div>`;
  async function execute(row) {
    if (!row || row.status === 'Removed') return;
    const type = row.item?.type || row.record?.type, id = row.item?.entityId || row.record?.id;
    if (['Missing', 'PendingRepair'].includes(row.status) && ['file', 'folder'].includes(type)) { window.DesktopHubResources?.repair(id); return; }
    if (row.status === 'SourceUnavailable') { H.navigate('library'); H.toast('请在文件库核对来源访问权限，再刷新来源。'); return; }
    if (['Disabled', 'Invalid'].includes(row.status) && type === 'scene') { H.actions['edit-scene']({ dataset: { id } }); return; }
    if (!row.canExecute) return;
    if (type === 'web') {
      try { const result = await bridge.request('projects.openWeb', { projectId, itemId: row.item.id }); H.toast(result.message); } catch (err) { H.toast(err.message, 'error'); }
      return;
    }
    const action = { app: 'app-launch', scene: 'run-scene', file: 'nr-open', folder: 'nr-open' }[type];
    await H.actions[action]?.({ dataset: type === 'file' || type === 'folder' ? { id, mode: 'open' } : { id } });
    void refreshActivity();
  }
  function readDraft() {
    if (!draft) return;
    const name = document.querySelector('#ap-project-name'), description = document.querySelector('#ap-project-description'), pin = document.querySelector('#ap-project-pin');
    if (name) draft.name = name.value;
    if (description) draft.description = description.value;
    if (pin) draft.pinnedToHome = pin.checked;
  }
  function draftRow(item, index, rows) {
    const label = draftNames.get(item.id) || item.name || item.url || '关联失效，请重新选择';
    return `<div class="ap-row"><div class="ap-row-info"><strong>${e(label)}</strong></div><div class="toolbar">${button('ap-draft-move', '↑', `${idAttr(item.id)} data-direction="-1" aria-label="向前移动 ${e(label)}" ${index === 0 ? 'disabled' : ''}`)}${button('ap-draft-move', '↓', `${idAttr(item.id)} data-direction="1" aria-label="向后移动 ${e(label)}" ${index === rows.length - 1 ? 'disabled' : ''}`)}${item.type !== 'web' ? button('ap-reselect', '重新选择', idAttr(item.id)) : ''}${button('ap-draft-remove', '移除关联', idAttr(item.id))}</div></div>`;
  }
  function editDraft() {
    if (!draft) return;
    H.showModal(draft.name ? '编辑项目' : '新建项目', `<div class="ap-editor"><label class="field"><span>项目名称</span><input id="ap-project-name" maxlength="80" value="${e(draft.name)}" required autofocus></label><label class="field"><span>项目说明（可选）</span><textarea id="ap-project-description" maxlength="500" rows="3">${e(draft.description)}</textarea></label><label class="check-row"><input id="ap-project-pin" type="checkbox" ${draft.pinnedToHome ? 'checked' : ''}><span>固定到首页</span></label><h3>项目关联 · ${draft.items.length} / 100</h3><div class="toolbar">${Object.entries(types).map(([type, label]) => button('ap-add-type', '添加' + label, `data-type="${type}" ${draft.items.length >= 100 ? 'disabled' : ''}`)).join('')}</div><div class="ap-draft-items">${Object.keys(groups).map(type => { const rows = draft.items.filter(item => group(item.type) === type); return rows.length ? `<h4>${groups[type]}</h4>${rows.map(draftRow).join('')}` : ''; }).join('')}</div>${error()}</div>`, button('close-modal', '取消') + button('ap-save', '保存项目', '', 'primary'), { wide: true });
  }
  async function beginEdit(id) {
    if (H.ui.modal) return;
    if (!id) { draft = emptyProject(); draftNames = new Map(); editDraft(); return; }
    if (selected?.id === id) { draft = clone(selected); draftNames = new Map(resolved.map(row => [row.item.id, row.name])); editDraft(); return; }
    const revision = ++intent;
    try {
      const result = await bridge.request('projects.query', { projectId: id });
      if (revision !== intent || H.ui.modal) return;
      if (!result.project) { H.toast('项目已移除。'); return; }
      draft = clone(result.project); draftNames = new Map((result.items || []).map(row => [row.item.id, row.name])); editDraft();
    } catch (err) { if (revision === intent) H.toast(err.message, 'error'); }
  }
  function chooseType(type, replacing = null) {
    if (!draft || writing || !types[type]) return;
    readDraft(); selection = { type, replacing, candidates: [] };
    if (type === 'web') {
      H.showModal('添加网页', '<label class="field"><span>HTTP 或 HTTPS 地址</span><input id="ap-web-url" type="url" maxlength="1024" autofocus placeholder="https://"></label><label class="field"><span>显示名称（可选）</span><input id="ap-web-label" maxlength="80"></label>' + error(), button('ap-editor-back', '返回项目') + button('ap-web-add', '添加网页', '', 'primary'));
    } else {
      H.showModal('添加' + types[type] + '关联', `<label class="field"><span>搜索已有${types[type]}</span><input id="ap-select-query" maxlength="200" autofocus></label><div id="ap-candidates" class="ap-candidates" role="status">输入名称查找已有内容。</div><p class="muted">只添加关联，不会打开内容。</p>${button('ap-import-existing', type === 'app' ? '先添加应用' : type === 'scene' ? '先创建场景' : '先索引来源')}<p class="muted">添加内容时会保留项目草稿，完成后选择“返回项目草稿”。</p>${error()}`, button('ap-editor-back', '返回项目'));
    }
  }
  async function searchSelection() {
    if (!selection || selection.type === 'web' || !H.ui.modal) return;
    const modal = H.ui.modal, current = selection, generation = ++selectionGeneration;
    const query = document.querySelector('#ap-select-query')?.value || '';
    try {
      const rows = await bridge.request('search.query', { query, type: current.type });
      if (H.ui.modal !== modal || selection !== current || generation !== selectionGeneration) return;
      current.candidates = (rows || []).filter(row => row.type === current.type && row.status !== 'Removed').slice(0, 50);
      const node = document.querySelector('#ap-candidates'); if (node) node.innerHTML = current.candidates.map(row => `<div class="ap-row"><div class="ap-row-info"><strong>${e(row.name)}</strong><p>${e(status[row.status] || row.status)}</p></div>${button('ap-select', '选择', idAttr(row.id))}</div>`).join('') || '<p>没有匹配内容，可调整名称或先添加内容。</p>';
    } catch (err) { if (generation === selectionGeneration) modalError(err, modal); }
  }
  Object.assign(H.actions, {
    'ap-open': el => open(el.dataset.id),
    'ap-list': () => H.navigate('projects'),
    'ap-activity': () => H.navigate('activity'),
    'ap-new': () => beginEdit(null),
    'ap-edit': el => beginEdit(el.dataset.id),
    'ap-recent-open': el => execute(activity[Number(el.dataset.recentIndex)]),
    'ap-item-open': el => execute(resolved.find(row => row.item.id === el.dataset.id)),
    'ap-activity-retry': () => refreshActivity(),
    'ap-projects-retry': () => refreshProjects(),
    'ap-clear': () => { if (!H.ui.modal) H.showModal('清空最近使用？', '<p>只清空最近使用记录，应用、项目和原文件都会保留。</p>' + error(), button('close-modal', '取消') + button('ap-clear-confirm', '清空记录', '', 'danger')); },
    'ap-clear-confirm': async () => {
      if (writing || !H.ui.modal) return; const modal = H.ui.modal; writing = true; ++activityGeneration;
      try { await bridge.request('activity.clear', {}); ++activityGeneration; activity = []; activityLoaded = true; activityBusy = false; activityError = ''; if (H.ui.modal === modal) H.closeModal(); repaint(); void refreshActivity(); }
      catch (err) { modalError(err, modal); } finally { writing = false; }
    },
    'ap-save': async () => {
      if (!draft || writing || !H.ui.modal) return; readDraft(); const candidate = clone(draft), modal = H.ui.modal; writing = true; ++projectGeneration;
      try {
        const result = await bridge.request('projects.save', { project: candidate }); ++projectGeneration;
        const committed = clone(result.project);
        const next = projects.filter(project => project.id !== committed.id);
        next.splice(Math.min(committed.displayOrder, next.length), 0, {
          id: committed.id, name: committed.name, description: committed.description, displayOrder: committed.displayOrder,
          pinnedToHome: committed.pinnedToHome, itemCount: committed.items.length
        });
        projects = next.map((project, index) => ({ ...project, displayOrder: index }));
        projectsLoaded = true; projectsBusy = false; projectError = ''; projectWarning = '';
        if (projectId === committed.id) {
          selected = clone(committed);
          resolved = resolved.filter(row => committed.items.some(item => item.id === row.item.id && item.type === row.item.type && item.entityId === row.item.entityId && item.url === row.item.url));
        }
        if (H.ui.modal === modal) {
          draft = clone(committed); selected = clone(committed); projectId = committed.id; H.closeModal(); H.navigate('project');
        } else repaint();
        // The commit is authoritative even if its editor was dismissed or replaced.
        // Supersede event queries, then settle fresh metadata without touching a newer draft.
        void refreshProjects();
      } catch (err) { modalError(err, modal); } finally { writing = false; }
    },
    'ap-remove': el => { if (!H.ui.modal) H.showModal('移除项目？', '<p>只移除此项目的关联，不会删除应用、场景、文件或其他项目中的内容。</p>' + error(), button('close-modal', '取消') + button('ap-remove-confirm', '移除项目', idAttr(el.dataset.id), 'danger')); },
    'ap-remove-confirm': async el => {
      if (writing || !H.ui.modal) return; const id = el.dataset.id, modal = H.ui.modal; writing = true; ++projectGeneration;
      try { await bridge.request('projects.remove', { projectId: id }); ++projectGeneration; projects = projects.filter(p => p.id !== id); if (projectId === id) { projectId = null; selected = null; resolved = []; } if (H.ui.modal === modal) { H.closeModal(); H.navigate('projects'); } void refreshProjects(); }
      catch (err) { modalError(err, modal); } finally { writing = false; }
    },
    'ap-project-move': async el => {
      if (H.ui.modal || writing) return; const id = el.dataset.id, revision = ++intent; writing = true;
      try { const result = await bridge.request('projects.query', { projectId: id }); if (revision !== intent || H.ui.modal || !result.project) return; const candidate = clone(result.project); candidate.displayOrder = Math.max(0, Math.min(projects.length - 1, candidate.displayOrder + Number(el.dataset.direction))); ++projectGeneration; await bridge.request('projects.save', { project: candidate }); ++projectGeneration; void refreshProjects(); }
      catch (err) { if (revision === intent) H.toast(err.message, 'error'); } finally { writing = false; }
    },
    'ap-add-type': el => chooseType(el.dataset.type),
    'ap-reselect': el => { const item = draft?.items.find(item => item.id === el.dataset.id); if (item) chooseType(item.type, item.id); },
    'ap-editor-back': () => editDraft(),
    'ap-draft-remove': el => { if (!draft || writing) return; readDraft(); draft.items = draft.items.filter(item => item.id !== el.dataset.id); editDraft(); },
    'ap-draft-move': el => {
      if (!draft || writing) return; readDraft(); const index = draft.items.findIndex(item => item.id === el.dataset.id); if (index < 0) return;
      const positions = draft.items.map((item, i) => group(item.type) === group(draft.items[index].type) ? i : -1).filter(i => i >= 0), next = positions[positions.indexOf(index) + Number(el.dataset.direction)];
      if (next !== undefined) [draft.items[index], draft.items[next]] = [draft.items[next], draft.items[index]]; editDraft();
    },
    'ap-select': el => {
      if (!draft || !selection || writing) return; const row = selection.candidates.find(row => row.id === el.dataset.id); if (!row) return;
      if (draft.items.some(item => item.type === row.type && item.entityId === row.id && item.id !== selection.replacing)) { modalError(new Error('项目中已有此关联。'), H.ui.modal); return; }
      const item = { id: selection.replacing || window.crypto.randomUUID(), type: row.type, entityId: row.id, url: null, name: null }, index = draft.items.findIndex(item => item.id === selection.replacing);
      if (index >= 0) draft.items[index] = item; else if (draft.items.length < 100) draft.items.push(item); else return;
      draftNames.set(item.id, row.name); editDraft();
    },
    'ap-web-add': () => {
      if (!draft || writing || draft.items.length >= 100) return;
      const url = document.querySelector('#ap-web-url')?.value.trim() || '', name = document.querySelector('#ap-web-label')?.value.trim() || null;
      try { const parsed = new URL(url); if (!/^https?:$/.test(parsed.protocol) || !parsed.hostname || parsed.username || parsed.password || /[\s\\\u0000-\u001f\u007f]/.test(url) || url.length > 1024 || (name?.length || 0) > 80) throw new Error(); }
      catch { modalError(new Error('请输入有效的 HTTP 或 HTTPS 网页地址。'), H.ui.modal); return; }
      draft.items.push({ id: window.crypto.randomUUID(), type: 'web', entityId: null, url, name }); editDraft();
    },
    'ap-import-existing': () => {
      if (!selection || writing) return; resumeType = selection.type; H.closeModal();
      if (resumeType === 'app') { H.navigate('home'); void H.actions['app-add']?.(); }
      else if (resumeType === 'scene') { H.navigate('scenes'); H.actions['scene-new']?.(); }
      else { H.navigate('library'); H.toast('请在文件库添加来源，完成后选择“返回项目草稿”。'); }
    },
    'ap-resume-draft': () => {
      if (!resumeType || H.ui.modal || H.ui.page === 'editor' && H.ui.sceneDraftDirty) return;
      const type = resumeType; resumeType = null; chooseType(type);
    }
  });
  function scheduleSearch(event) {
    if (event.target.id !== 'ap-select-query' || composing) return;
    clearTimeout(searchTimer); ++selectionGeneration; searchTimer = setTimeout(() => { void searchSelection(); }, 150);
  }
  document.addEventListener('input', scheduleSearch);
  document.addEventListener('compositionstart', event => { if (event.target.id === 'ap-select-query') { composing = true; clearTimeout(searchTimer); ++selectionGeneration; } });
  document.addEventListener('compositionend', event => { composing = false; scheduleSearch(event); });
  function composeHome() {
    if (H.ui.page !== 'home') return;
    const home = document.querySelector('.home-content'), scenes = home?.querySelector('.home-scenes'); if (!home || !scenes) return;
    const priorProjects = document.querySelector('.projects-home'), priorActivity = document.querySelector('.activity-home');
    if (priorProjects?.remove) priorProjects.remove(); if (priorActivity?.remove) priorActivity.remove();
    // Page adapters without DOM removal still get the same idempotent composition.
    if (priorProjects && !priorProjects.remove || priorActivity && !priorActivity.remove) home.innerHTML = home.innerHTML.replace(/<!--ap-home-start-->[\s\S]*?<!--ap-home-end-->/g, '');
    const pinned = projects.filter(p => p.pinnedToHome);
    scenes.insertAdjacentHTML('afterend', `<!--ap-home-start--><section class="projects-home"><div class="ap-heading"><h2>固定项目</h2>${button('ap-list', '所有项目')}</div>${pinned.map(p => projectRow(p, true)).join('') || '<p class="muted">在项目中选择“固定到首页”，方便继续工作。</p>'}${storageNotice(projectWarning, '项目')}</section><section class="activity-home"><div class="ap-heading"><h2>最近使用</h2><div class="toolbar">${button('ap-activity', '查看全部')}${button('ap-clear', '清空…', activity.length ? '' : 'disabled')}</div></div>${activityState(6)}</section><!--ap-home-end-->`);
  }
  const render = H.render;
  H.render = function () {
    render(); composeHome();
    const breadcrumb = document.querySelector('.workspace-subnav > span:not(.breadcrumb-divider)');
    if (breadcrumb && ['activity', 'projects', 'project'].includes(H.ui.page)) breadcrumb.textContent = H.ui.page === 'activity' ? '最近使用' : H.ui.page === 'projects' ? '项目集合' : selected?.name || '项目';
    for (const nav of document.querySelectorAll('.usability-home-nav, .workspace-subnav nav')) if (!nav.querySelector('[data-action="ap-list"], [data-page="projects"]')) nav.insertAdjacentHTML('beforeend', button('ap-list', '项目集合') + button('ap-activity', '最近使用'));
    if (resumeType && !document.querySelector('.ap-resume-notice')) document.querySelector('.workspace-content')?.insertAdjacentHTML('afterbegin', `<div class="notice ap-resume-notice"><span>项目草稿已保留。</span>${button('ap-resume-draft', '返回项目草稿')}</div>`);
    if (['home', 'activity'].includes(H.ui.page) && !activityLoaded && !activityBusy && !activityError) void refreshActivity();
    if (['home', 'projects', 'project'].includes(H.ui.page) && !projectsLoaded && !projectsBusy && !projectError) void refreshProjects();
    H.restoreViewport?.();
  };
  bridge.on('activity.changed', data => { if (data.warning) activityWarning = data.warning; void refreshActivity(); });
  bridge.on('projects.changed', () => { void refreshProjects(); });
  bridge.on('resources.changed', () => { void refreshActivity(); void refreshProjects(); });
  const metadataKey = state => JSON.stringify([state?.settings?.appEntries, state?.settings?.scenes]);
  let currentMetadata = metadataKey(window.DesktopHubDesktop.getState().hostState);
  bridge.on('state.changed', state => { const next = metadataKey(state); if (next !== currentMetadata) { currentMetadata = next; void refreshActivity(); void refreshProjects(); } });
  window.DesktopHubActivity = Object.freeze({ refresh: refreshActivity });
  window.DesktopHubProjects = Object.freeze({ open, refresh: refreshProjects, navigationState: () => ({ projectId }), restoreNavigation(state) {
    ++intent; ++projectGeneration; ++restoreToken;
    projectId = state?.projectId || null; selected = null; resolved = []; projectsLoaded = false;
    pendingRestore = projectId && Number.isFinite(state?.scroll) ? { page: 'project', projectId, token: restoreToken, scroll: state.scroll, awaitingNavigation: true } : null;
    if (projectId) void refreshProjects();
  } });
}());
