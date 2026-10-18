(function () {
  'use strict';
  const t = window.DesktopHubI18n?.text || ((value, ...args) => Array.isArray(value) ? value.reduce((out, part, i) => out + part + (i < args.length ? args[i] : ''), '') : value);

  const H = window.Hub;
  const esc = value => H.escape(String(value == null ? '' : value));
  const clone = value => JSON.parse(JSON.stringify(value));
  const typeLabels = { get program() { return t('程序'); }, get web() { return t('网页'); }, get file() { return t('文件'); }, get folder() { return t('文件夹'); }, get script() { return t('脚本'); } };
  const typeIcons = { program: 'app', web: 'globe', file: 'file', folder: 'folder', script: 'code' };
  const modeLabels = { get sequential() { return t('顺序执行'); }, get parallel() { return t('并行执行'); }, get delay() { return t('固定延迟'); }, get process() { return t('等待进程'); }, get window() { return t('等待窗口'); } };
  const statusLabels = { get pending() { return t('待执行'); }, get running() { return t('执行中'); }, get success() { return t('成功'); }, get skip() { return t('跳过'); }, get fail() { return t('失败'); } };
  let runTimer = null;
  let runToken = 0;
  let dragItem = null;

  function button(action, label, icon, attributes, className) {
    return '<button type="button" class="' + (className || 'button') + '" data-action="' + action + '" ' + (attributes || '') + '>' + (icon ? H.icon(icon, 16) : '') + '<span>' + esc(label) + '</span></button>';
  }

  function iconButton(action, label, icon, attributes) {
    return '<button type="button" class="icon-button" title="' + esc(label) + '" aria-label="' + esc(label) + '" data-action="' + action + '" ' + (attributes || '') + '>' + H.icon(icon, 17) + '</button>';
  }

  function options(items, value) {
    return items.map(item => '<option value="' + esc(item[0]) + '"' + (String(item[0]) === String(value) ? ' selected' : '') + '>' + esc(item[1]) + '</option>').join('');
  }

  function scene(id) { return H.state.scenes.find(item => item.id === id); }
  function uid(prefix) { return prefix + '-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 7); }
  function draft() { return H.ui.sceneDraft || null; }
  function selectedAction() {
    const current = draft();
    return current && current.actions.find(item => item.id === H.ui.selectedActionId);
  }

  function openEditor(item) {
    H.ui.editingSceneId = item.id;
    H.ui.sceneDraft = clone(item);
    H.ui.sceneDraftDirty = false;
    H.ui.selectedActionId = item.actions[0] ? item.actions[0].id : '';
    H.ui.sceneEditorAdvanced = false;
    H.navigate('editor');
  }

  function hasCycle(actions) {
    const byId = new Map(actions.map(item => [item.id, item]));
    const active = new Set();
    const completed = new Set();
    function visit(id) {
      if (active.has(id)) return true;
      if (completed.has(id) || !byId.has(id)) return false;
      active.add(id);
      const dependency = byId.get(id).dependsOn;
      if (dependency && visit(dependency)) return true;
      active.delete(id);
      completed.add(id);
      return false;
    }
    return actions.some(item => visit(item.id));
  }

  H.validateSceneDependencies = hasCycle;

  H.toggleScenePin = function (id) {
    const item = scene(id);
    if (!item) return false;
    const count = H.state.scenes.filter(entry => entry.pinned).length;
    if (!item.pinned && count >= 4) {
      H.toast(t('Mini 最多固定 4 个场景。请先取消一个固定项。'), 'warning');
      return false;
    }
    if (item.pinned && count <= 2) {
      H.toast(t('Mini 需保留至少 2 个固定场景。请先固定其他场景。'), 'warning');
      return false;
    }
    item.pinned = !item.pinned;
    H.save();
    H.render();
    H.toast(item.pinned ? t('已固定到 Mini（本地演示）') : t('已取消 Mini 固定（本地演示）'));
    return true;
  };

  function move(items, id, direction) {
    const index = items.findIndex(item => item.id === id);
    const destination = index + direction;
    if (index < 0 || destination < 0 || destination >= items.length) return false;
    const item = items.splice(index, 1)[0];
    items.splice(destination, 0, item);
    return true;
  }

  function sceneCard(item, index) {
    const attributes = 'data-id="' + esc(item.id) + '"';
    const run = H.ui.run && H.ui.run.sceneId === item.id ? H.ui.run : null;
    const completed = run ? run.items.filter(action => ['success', 'skip', 'fail'].includes(action.status)).length : 0;
    return '<article class="card scene-card" draggable="true" data-scene-drag="' + esc(item.id) + '">' +
      '<div class="scene-card-top"><div class="scene-symbol">' + H.icon(item.icon || 'pen', 25) + '</div><div class="scene-card-title"><h2>' + esc(item.name) + '</h2><span class="subtitle">' + item.actions.length + t(' 个动作</span></div>') +
      iconButton('scene-pin', item.pinned ? t('取消固定到 Mini') : t('固定到 Mini'), 'pin', attributes + ' aria-pressed="' + !!item.pinned + '"') + '</div>' +
      '<p class="scene-description">' + esc(item.description || t('为这一刻，准备常用工具。')) + '</p>' +
      '<div class="scene-flow" title="' + esc(item.actions.map(action => action.name).join(' → ')) + '">' + esc(item.actions.length ? item.actions.map(action => action.name).join(' → ') : t('尚未添加动作')) + '</div>' +
      '<div class="scene-card-meta"><span class="badge' + (item.pinned ? ' active' : '') + '">' + (item.pinned ? t('已固定 Mini') : t('未固定')) + '</span>' + (run ? t('<span class="subtitle">模拟 · ') + (run.finished ? t('已结束') : completed + '/' + run.items.length) + '</span>' : t('<span class="subtitle">本地模拟场景</span>')) + '</div>' +
      '<div class="scene-card-footer">' + button('run-scene', run && !run.finished ? t('模拟执行中') : t('模拟启动'), 'play', attributes + (run && !run.finished ? ' disabled' : ''), 'button primary') +
      iconButton('scene-edit', t('编辑 ') + item.name, 'edit', attributes) +
      '<span class="scene-order-controls">' + iconButton('scene-move-up', t('向前移动 ') + item.name, 'arrow-up', attributes + (index === 0 ? ' disabled' : '')) + iconButton('scene-move-down', t('向后移动 ') + item.name, 'arrow-down', attributes + (index === H.state.scenes.length - 1 ? ' disabled' : '')) + '</span>' +
      iconButton('scene-delete', t('删除 ') + item.name, 'trash', attributes) + '</div></article>';
  }

  H.pages.scenes = function () {
    const count = H.state.scenes.filter(item => item.pinned).length;
    return t('<div class="page-heading"><div><div class="eyebrow">YOUR ROUTINES</div><h1 class="page-title" tabindex="-1">场景中心</h1><p class="subtitle">把常用工具放在一起，一次准备好。</p></div>') + button('scene-new', t('新建场景'), 'plus', '', 'button primary') + '</div>' +
      '<div class="toolbar scene-toolbar"><span class="subtitle">' + H.state.scenes.length + t(' 个场景 · Mini 已固定 ') + count + t('/4</span><span class="subtitle">拖拽卡片或使用箭头调整顺序</span></div>') +
      (H.state.scenes.length ? '<div class="scenes-grid">' + H.state.scenes.map(sceneCard).join('') + '</div>' : '<div class="empty-state panel">' + H.icon('pen', 36) + t('<h2>从第一个场景开始</h2><p>添加几项常用工具，体验一次模拟启动。</p>') + button('scene-new', t('添加第一个场景'), 'plus', '', 'button primary') + '</div>') +
      t('<p class="page-note">所有启动均为模拟。场景只保存在此浏览器中，不调用真实软件、网页或脚本。</p>');
  };

  function actionRow(item, index, actions) {
    const isSelected = item.id === H.ui.selectedActionId;
    const dependency = actions.find(entry => entry.id === item.dependsOn);
    const attributes = 'data-id="' + esc(item.id) + '"';
    return '<div class="action-item' + (isSelected ? ' selected' : '') + '" draggable="true" data-action-drag="' + esc(item.id) + '">' +
      t('<span class="action-grip" title="拖拽调整顺序">') + H.icon('grip', 14) + '</span>' +
      '<button class="action-select" type="button" data-action="scene-select-action" ' + attributes + ' aria-pressed="' + isSelected + '"><span class="action-number">' + (index + 1) + '</span><span class="action-type-icon">' + H.icon(typeIcons[item.type] || 'app', 19) + '</span><span class="action-item-copy"><strong>' + esc(item.name) + '</strong><small>' + esc(modeLabels[item.mode] || t('顺序执行')) + (dependency ? t(' · 依赖 ') + esc(dependency.name) : '') + '</small></span></button>' +
      '<span class="action-row-tools">' + iconButton('scene-action-up', t('上移动作'), 'arrow-up', attributes + (index === 0 ? ' disabled' : '')) + iconButton('scene-action-down', t('下移动作'), 'arrow-down', attributes + (index === actions.length - 1 ? ' disabled' : '')) + '</span></div>';
  }

  function field(label, input, hint) {
    return '<label class="field"><span>' + esc(label) + '</span>' + input + (hint ? '<small>' + esc(hint) + '</small>' : '') + '</label>';
  }

  function textInput(value, name, extra) {
    return '<input data-change="scene-action-field" data-field="' + name + '" value="' + esc(value) + '" ' + (extra || '') + '>';
  }

  function actionConfiguration(item, actions) {
    const dependencyItems = [['', t('无依赖')]].concat(actions.filter(entry => entry.id !== item.id).map(entry => [entry.id, entry.name]));
    const modeHint = item.mode === 'process' || item.mode === 'window' ? t('模拟等待目标就绪；默认超时 10 秒。') : item.mode === 'parallel' ? t('与其他可执行动作同时模拟启动。') : item.mode === 'delay' ? t('在设定的延迟后模拟执行。') : t('按列表顺序模拟执行；显式依赖优先。');
    return '<div class="action-config-header"><div><div class="eyebrow">ACTION SETTINGS</div><h2>' + esc(item.name) + '</h2></div>' + iconButton('scene-action-delete', t('删除当前动作'), 'trash', 'data-id="' + esc(item.id) + '"') + '</div>' +
      '<div class="form-grid">' +
      field(t('动作名称'), textInput(item.name, 'name', t('maxlength="40" placeholder="例如：打开设计工具"'))) +
      field(t('动作类型'), '<select data-change="scene-action-field" data-field="type">' + options(Object.entries(typeLabels), item.type) + '</select>') +
      t('</div><div class="field action-target-field"><span>动作目标 <span class="badge">模拟</span></span><div class="input-with-button">') + textInput(item.target || '', 'target', item.type === 'script' ? t('readonly aria-label="已选择的模拟脚本文件" placeholder="选择一个已有的模拟脚本文件"') : t('aria-label="模拟动作目标" placeholder="输入模拟路径或网址，不会实际打开"')) + button('scene-pick-target', item.type === 'script' ? t('选择已有文件') : t('选择模拟目标'), 'folder', '', 'button quiet') + '</div><small>' + (item.type === 'script' ? t('只选择已有模拟文件；原型不提供脚本编辑，也不会运行脚本。') : t('仅记录演示目标，不访问文件、不连接网址、不启动程序。')) + '</small></div>' +
      '<div class="form-grid">' +
      field(t('运行方式'), '<select data-change="scene-action-field" data-field="mode">' + options(Object.entries(modeLabels), item.mode) + '</select>', modeHint) +
      field(t('依赖动作'), '<select data-change="scene-action-field" data-field="dependsOn">' + options(dependencyItems, item.dependsOn || '') + '</select>', t('依赖失败则跳过本动作，独立动作继续。')) +
      '</div>' +
      ((item.mode === 'process' || item.mode === 'window') ? t('<div class="detail-row"><span>等待类型</span><span>') + (item.mode === 'process' ? t('目标进程出现（模拟）') : t('目标窗口就绪（模拟）')) + '</span></div>' : '') +
      '<button type="button" class="advanced-toggle" data-action="scene-advanced" aria-expanded="' + !!H.ui.sceneEditorAdvanced + '">' + H.icon('chevron-down', 16) + t('<span>高级参数</span><small>延时、超时、重复策略</small></button>') +
      (H.ui.sceneEditorAdvanced ? '<div class="advanced-fields"><div class="form-grid">' +
        field(t('延时（秒）'), textInput(item.delay || 0, 'delay', 'type="number" min="0" max="300" step="1"'), t('M0 演示会压缩等待时间。')) +
        field(t('超时（秒）'), textInput(item.timeout || 10, 'timeout', 'type="number" min="1" max="300" step="1"'), t('等待进程 / 窗口的默认值为 10 秒。')) +
        '</div>' + field(t('重复策略'), '<select data-change="scene-action-field" data-field="repeat">' + options([['skip', t('已运行时跳过（程序默认）')], ['always', t('每次执行（网页 / 文件默认）')], ['once', t('同一执行实例最多一次（脚本默认）')]], item.repeat || 'skip') + '</select>') + '</div>' : '') +
      t('<div class="simulation-config"><span class="field-label">模拟反馈样例</span><select aria-label="模拟反馈样例" data-change="scene-action-field" data-field="demoResult">') + options([['success', t('模拟成功')], ['skip', t('模拟跳过（已运行 / 已完成）')], ['fail', t('模拟失败')]], item.demoResult || 'success') + t('</select><small>用于评审反馈与依赖行为，不代表真实系统状态。</small></div>');
  }

  function editorFeedback(current) {
    const run = H.ui.run;
    if (!run || run.sceneId !== current.id) return t('<div class="editor-feedback"><div class="detail-row"><strong>执行反馈</strong><span class="badge">模拟</span></div><p class="subtitle">保存后点击「模拟启动」，查看待执行、执行中、成功、跳过与失败状态。</p></div>');
    return t('<div class="editor-feedback"><div class="detail-row"><strong>模拟执行反馈</strong><span class="badge">') + (run.finished ? t('已结束') : t('执行中')) + '</span></div><div class="feedback-list">' + run.items.map(item => '<div class="feedback-row"><span class="status-dot ' + item.status + '"></span><span>' + esc(item.name) + '</span><span class="badge ' + item.status + t('">模拟') + statusLabels[item.status] + '</span></div>').join('') + '</div></div>';
  }

  H.pages.editor = function () {
    if (!draft()) {
      const existing = scene(H.ui.editingSceneId);
      if (existing) {
        H.ui.sceneDraft = clone(existing);
        H.ui.selectedActionId = existing.actions[0] ? existing.actions[0].id : '';
      }
    }
    const current = draft();
    if (!current) return t('<div class="empty-state"><h2>请选择要编辑的场景</h2>') + button('scene-back', t('返回场景中心'), 'arrow-left') + '</div>';
    const item = selectedAction();
    const existing = scene(current.id);
    return '<div class="page-heading editor-heading"><div><button type="button" class="text-button" data-action="scene-back">' + H.icon('arrow-left', 16) + t('场景中心</button><h1 class="page-title" tabindex="-1">') + (existing ? t('编辑场景') : t('新建场景')) + t('</h1><p class="subtitle">按习惯排列动作，再选择它们之间的等待关系。</p></div><div class="toolbar">') + button('scene-back', t('取消'), '', '', 'button quiet') + button('scene-save', t('保存场景'), 'check', '', 'button primary') + '</div></div>' +
      '<div class="panel scene-identity"><div class="scene-identity-icon">' + H.icon(current.icon || 'pen', 25) + '</div><div class="form-grid">' +
      field(t('场景名称'), '<input data-change="scene-draft-field" data-field="name" value="' + esc(current.name) + t('" maxlength="24" placeholder="为场景取个名字">')) +
      field(t('场景图标'), '<select data-change="scene-draft-field" data-field="icon">' + options([['pen', t('创作')], ['game', t('游戏')], ['coffee', t('休闲')]], current.icon || 'pen') + '</select>') +
      '</div>' + field(t('简短说明'), '<input data-change="scene-draft-field" data-field="description" value="' + esc(current.description || '') + t('" maxlength="80" placeholder="例如：进入专注创作的节奏">')) + '</div>' +
      t('<div class="editor-layout"><section class="panel action-list"><div class="action-list-header"><h2>动作列表 <span class="subtitle">') + current.actions.length + '</span></h2>' + iconButton('scene-add-action', t('添加动作'), 'plus') + '</div>' +
      (current.actions.length ? '<div class="action-list-items">' + current.actions.map((action, index) => actionRow(action, index, current.actions)).join('') + '</div>' : '<div class="empty-state compact">' + H.icon('plus', 28) + t('<h3>添加第一个动作</h3><p>程序、网页或已有文件，按你的需要选择。</p></div>')) +
      button('scene-add-action', t('添加动作'), 'plus', '', 'button quiet full-width') + t('<p class="page-note">拖拽手柄或使用箭头排序。</p></section><section class="panel action-config">') +
      (item ? actionConfiguration(item, current.actions) : '<div class="empty-state compact">' + H.icon('app', 32) + t('<h2>从一个动作开始</h2><p>添加动作后，在这里设置目标与运行方式。</p>') + button('scene-add-action', t('添加动作'), 'plus', '', 'button primary') + '</div>') +
      editorFeedback(current) + t('</section></div><p class="page-note">编辑仅修改本地演示数据。显式循环依赖不能保存；脚本和系统操作始终只模拟。</p>');
  };

  H.actions['scene-new'] = function () {
    openEditor({ id: uid('scene'), name: t('新场景'), description: '', icon: 'pen', pinned: false, actions: [] });
  };

  H.actions['scene-edit'] = function (el) {
    const item = scene(el.dataset.id);
    if (item) openEditor(item);
  };
  H.actions['edit-scene'] = H.actions['scene-edit'];

  function exitEditor() {
    H.ui.sceneDraft = null;
    H.ui.sceneDraftDirty = false;
    H.ui.editingSceneId = '';
    H.navigate('scenes');
  }

  H.actions['scene-back'] = function () {
    if (!H.ui.sceneDraftDirty) { exitEditor(); return; }
    H.showModal(t('放弃未保存的修改？'), t('<p>场景仍保留上次保存的内容。本次修改仅在当前草稿中。</p>'), button('close-modal', t('继续编辑'), '', '', 'button quiet') + button('scene-discard', t('放弃修改'), '', '', 'button'));
  };
  H.actions['scene-discard'] = function () { H.closeModal(); exitEditor(); };

  H.actions['scene-pin'] = function (el) { H.toggleScenePin(el.dataset.id); };
  H.actions['scene-move-up'] = function (el) { if (move(H.state.scenes, el.dataset.id, -1)) { H.save(); H.render(); } };
  H.actions['scene-move-down'] = function (el) { if (move(H.state.scenes, el.dataset.id, 1)) { H.save(); H.render(); } };

  H.actions['scene-delete'] = function (el) {
    const item = scene(el.dataset.id);
    if (!item) return;
    if (item.pinned && H.state.scenes.filter(entry => entry.pinned).length <= 2) { H.toast(t('请先固定其他场景，确保 Mini 至少保留 2 个固定项。'), 'warning'); return; }
    H.showModal(t('删除「') + item.name + '」？', t('<p>只删除这个本地模拟场景及其动作，不影响任何真实软件或文件。</p>'), button('close-modal', t('取消'), '', '', 'button quiet') + button('scene-delete-confirm', t('删除模拟场景'), 'trash', 'data-id="' + esc(item.id) + '"', 'button danger'));
  };

  H.actions['scene-delete-confirm'] = function (el) {
    const item = scene(el.dataset.id);
    if (!item) { H.closeModal(); return; }
    if (item.pinned && H.state.scenes.filter(entry => entry.pinned).length <= 2) { H.toast(t('Mini 需保留至少 2 个固定场景。'), 'warning'); return; }
    H.state.scenes = H.state.scenes.filter(entry => entry.id !== item.id);
    H.save(); H.closeModal(); H.render(); H.toast(t('已删除本地模拟场景'));
  };

  H.actions['scene-draft-field'] = function (el, event) {
    const current = draft();
    if (!current) return;
    current[el.dataset.field] = el.value;
    H.ui.sceneDraftDirty = true;
    if (el.dataset.field === 'icon' && event.type === 'change') H.render();
  };

  H.actions['scene-select-action'] = function (el) { H.ui.selectedActionId = el.dataset.id; H.render(); };
  H.actions['scene-advanced'] = function () { H.ui.sceneEditorAdvanced = !H.ui.sceneEditorAdvanced; H.render(); };
  H.actions['scene-action-up'] = function (el) { if (draft() && move(draft().actions, el.dataset.id, -1)) { H.ui.sceneDraftDirty = true; H.render(); } };
  H.actions['scene-action-down'] = function (el) { if (draft() && move(draft().actions, el.dataset.id, 1)) { H.ui.sceneDraftDirty = true; H.render(); } };

  H.actions['scene-add-action'] = function () {
    H.showModal(t('添加动作'), t('<p class="subtitle">选择动作类型。所有目标均为本地模拟数据。</p><div class="action-type-choices">') + Object.entries(typeLabels).map(pair => button('scene-add-action-type', pair[1], typeIcons[pair[0]], 'data-type="' + pair[0] + '"', 'button action-type-choice')).join('') + '</div>');
  };

  const sampleTargets = {
    program: [ [t('加速器'), t('C:\\模拟程序\\加速器.exe')], ['Clash', t('C:\\模拟程序\\Clash.exe')], ['ChatGPT', t('C:\\模拟程序\\ChatGPT.exe')], ['Edge', t('C:\\模拟程序\\Edge.exe')], ['DSH', t('C:\\模拟程序\\DSH.exe')], ['Steam', t('C:\\模拟程序\\Steam.exe')] ],
    web: [ ['ChatGPT', 'https://chatgpt.com'], [t('Edge 起始页'), 'https://example.com'] ],
    file: [ [t('创作笔记.md'), t('C:\\模拟桌面\\创作笔记.md')], [t('设计参考图.png'), t('C:\\模拟桌面\\设计参考图.png')], [t('课程作业.docx'), t('C:\\模拟桌面\\课程作业.docx')] ],
    folder: [ [t('创作资料'), t('C:\\模拟桌面\\创作资料')], [t('游戏资料'), t('C:\\模拟桌面\\游戏资料')] ],
    script: [ [t('准备工作环境.ps1'), t('C:\\模拟脚本\\准备工作环境.ps1')], [t('游戏前准备.bat'), t('C:\\模拟脚本\\游戏前准备.bat')], [t('打开资料.cmd'), t('C:\\模拟脚本\\打开资料.cmd')] ]
  };

  H.actions['scene-add-action-type'] = function (el) {
    const current = draft();
    const type = el.dataset.type;
    if (!current || !typeLabels[type]) return;
    const id = uid('action');
    current.actions.push({ id: id, name: t('新') + typeLabels[type] + t('动作'), type: type, target: '', mode: 'sequential', dependsOn: '', delay: 0, timeout: 10, repeat: type === 'script' ? 'once' : ['web', 'file', 'folder'].includes(type) ? 'always' : 'skip', demoResult: 'success' });
    H.ui.selectedActionId = id;
    H.ui.sceneDraftDirty = true;
    H.closeModal(); H.render();
  };

  H.actions['scene-action-field'] = function (el, event) {
    const item = selectedAction();
    if (!item) return;
    const key = el.dataset.field;
    if (key === 'target' && item.type === 'script') return;
    if (key === 'delay' || key === 'timeout') item[key] = Math.max(key === 'timeout' ? 1 : 0, Math.min(300, Number(el.value) || (key === 'timeout' ? 10 : 0)));
    else item[key] = el.value;
    if (key === 'type') {
      item.repeat = item.type === 'script' ? 'once' : ['web', 'file', 'folder'].includes(item.type) ? 'always' : 'skip';
      item.target = '';
    }
    H.ui.sceneDraftDirty = true;
    if (key === 'name') { const label=document.querySelector('.action-select[data-id="'+item.id+'"] strong');if(label)label.textContent=item.name;const title=document.querySelector('.action-config-header h2');if(title)title.textContent=item.name; }
    if (event.type === 'change' && ['type', 'mode', 'name', 'dependsOn'].includes(key)) H.render();
  };

  H.actions['scene-pick-target'] = function () {
    const item = selectedAction();
    if (!item) return;
    H.showModal(item.type === 'script' ? t('选择已有脚本文件（模拟）') : t('选择动作目标（模拟）'), t('<p class="subtitle">以下项目为内置假数据，不读取真实目录。</p><div class="target-picker">') + sampleTargets[item.type].map((entry, index) => '<button type="button" class="list-row target-option" data-action="scene-target-selected" data-index="' + index + '">' + H.icon(typeIcons[item.type], 21) + '<span><strong>' + esc(entry[0]) + '</strong><small>' + esc(entry[1]) + '</small></span>' + H.icon('arrow-right', 16) + '</button>').join('') + '</div>', button('close-modal', t('取消'), '', '', 'button quiet'));
  };

  H.actions['scene-target-selected'] = function (el) {
    const item = selectedAction();
    if (!item) return;
    const target = sampleTargets[item.type][Number(el.dataset.index)];
    if (!target) return;
    item.target = target[1];
    if (!item.name.trim() || /^新.*动作$/.test(item.name)) item.name = target[0];
    H.ui.sceneDraftDirty = true;
    H.closeModal(); H.render();
  };

  H.actions['scene-action-delete'] = function (el) {
    const current = draft();
    if (!current) return;
    const item = current.actions.find(entry => entry.id === el.dataset.id);
    if (!item) return;
    H.showModal(t('删除这个动作？'), t('<p>删除「') + esc(item.name) + t('」后，依赖它的动作将改为无依赖。修改在保存场景后生效。</p>'), button('close-modal', t('取消'), '', '', 'button quiet') + button('scene-action-delete-confirm', t('删除动作'), 'trash', 'data-id="' + esc(item.id) + '"', 'button danger'));
  };

  H.actions['scene-action-delete-confirm'] = function (el) {
    const current = draft();
    if (!current) return;
    current.actions = current.actions.filter(entry => entry.id !== el.dataset.id);
    current.actions.forEach(entry => { if (entry.dependsOn === el.dataset.id) entry.dependsOn = ''; });
    H.ui.selectedActionId = current.actions[0] ? current.actions[0].id : '';
    H.ui.sceneDraftDirty = true;
    H.closeModal(); H.render();
  };

  H.actions['scene-save'] = function () {
    const current = draft();
    if (!current) return;
    current.name = current.name.trim();
    if (!current.name) { H.toast(t('请填写场景名称。'), 'warning'); return; }
    if (!current.actions.length) { H.toast(t('请至少添加一个动作，再保存场景。'), 'warning'); return; }
    const invalid = current.actions.find(item => !item.name.trim() || !item.target.trim());
    if (invalid) { H.ui.selectedActionId = invalid.id; H.render(); H.toast(t('请为每个动作填写名称并选择模拟目标。'), 'warning'); return; }
    if (hasCycle(current.actions)) {
      H.showModal(t('无法保存：存在循环依赖'), '<div class="notice warning">' + H.icon('alert', 20) + t('<p>动作之间不能相互等待。例如 A 依赖 B、B 又依赖 A，会形成循环。请去掉其中一条依赖后再保存。</p></div>'), button('close-modal', t('返回修改'), '', '', 'button primary'));
      return;
    }
    const index = H.state.scenes.findIndex(item => item.id === current.id);
    if (index < 0) H.state.scenes.push(clone(current));
    else H.state.scenes[index] = clone(current);
    H.save();
    exitEditor();
    H.toast(t('场景已保存（本地模拟数据）'));
  };

  function resultDetail(item, action) {
    if (item.status === 'fail') return action.mode === 'process' || action.mode === 'window' ? t('模拟等待超时（配置 ') + (action.timeout || 10) + t(' 秒，演示时间已压缩）') : t('模拟启动失败，可在场景编辑器检查目标');
    if (item.status === 'skip') return action.type === 'program' ? t('模拟程序已运行，按策略跳过') : t('模拟本次执行已完成，按策略跳过');
    return t('模拟') + (action.type === 'web' ? t('打开网页') : action.type === 'file' ? t('打开文件') : action.type === 'folder' ? t('打开文件夹') : action.type === 'script' ? t('执行已有脚本（没有真实执行）') : t('启动程序')) + t('成功');
  }

  H.startScene = function (id) {
    const current = scene(id);
    if (!current) { H.toast(t('未找到这个模拟场景。'), 'warning'); return false; }
    if (H.ui.run && !H.ui.run.finished) { H.ui.surface = 'dock'; H.render(); H.toast(t('当前模拟执行尚未结束，请稍候。')); return false; }
    if (!current.actions.length) { H.toast(t('此场景没有动作，请先添加。'), 'warning'); return false; }
    if (hasCycle(current.actions)) { H.toast(t('循环依赖不能模拟启动，请先修改依赖。'), 'warning'); return false; }
    if (runTimer) clearTimeout(runTimer);
    const token = ++runToken;
    const runActions = clone(current.actions);
    const run = {
      sceneId: current.id,
      name: current.name,
      items: runActions.map(action => ({ id: action.id, name: action.name, status: 'pending', detail: t('等待模拟执行') })),
      finished: false,
      simulated: true,
      step: 0
    };
    H.ui.surface = 'dock';
    H.ui.runningSceneId = current.id;
    H.ui.run = run;
    H.render();
    H.toast(t('开始模拟「') + current.name + t('」；不会调用真实软件。'));

    function tick() {
      if (token !== runToken || H.ui.run !== run) return;
      run.step++;
      run.items.forEach((item, index) => {
        const action = runActions[index];
        if (item.status === 'running' && run.step >= item.finishStep) {
          item.status = ['success', 'skip', 'fail'].includes(action.demoResult) ? action.demoResult : 'success';
          item.detail = resultDetail(item, action);
        }
      });
      // Resolve failed or unavailable dependencies before starting independent work.
      run.items.forEach((item, index) => {
        const action = runActions[index];
        if (item.status !== 'pending' || !action.dependsOn) return;
        const dependency = run.items.find(entry => entry.id === action.dependsOn);
        if (!dependency || dependency.status === 'fail' || dependency.dependencyFailed) {
          item.status = 'skip';
          item.dependencyFailed = true;
          item.detail = t('模拟跳过：依赖动作失败或不可用');
        }
      });
      let running = run.items.some(item => item.status === 'running');
      for (let index = 0; index < run.items.length; index++) {
        const item = run.items[index];
        const action = runActions[index];
        if (item.status !== 'pending') continue;
        const dependency = action.dependsOn ? run.items.find(entry => entry.id === action.dependsOn) : null;
        if (dependency && !['success', 'skip'].includes(dependency.status)) continue;
        if (running && action.mode !== 'parallel') continue;
        item.status = 'running';
        item.detail = t('模拟') + (modeLabels[action.mode] || t('顺序执行')) + t('中');
        item.finishStep = run.step + ((action.mode === 'delay' || Number(action.delay) > 0 || action.mode === 'process' || action.mode === 'window') ? 2 : 1);
        running = true;
      }
      run.finished = run.items.every(item => ['success', 'skip', 'fail'].includes(item.status));
      H.render();
      if (run.finished) {
        H.ui.runningSceneId = '';
        const failed = run.items.filter(item => item.status === 'fail').length;
        H.toast('「' + current.name + t('」模拟执行结束') + (failed ? '，' + failed + t(' 项模拟失败。') : '。'), failed ? 'warning' : 'success');
      } else runTimer = setTimeout(tick, 450);
    }
    runTimer = setTimeout(tick, 450);
    return true;
  };

  H.actions['run-scene'] = function (el) { H.startScene(el.dataset.id || el.dataset.scene); };

  document.addEventListener('dragstart', function (event) {
    const actionRow = event.target.closest('[data-action-drag]');
    const sceneCard = event.target.closest('[data-scene-drag]');
    if (actionRow) dragItem = { kind: 'action', id: actionRow.dataset.actionDrag };
    else if (sceneCard) dragItem = { kind: 'scene', id: sceneCard.dataset.sceneDrag };
    else return;
    if (event.dataTransfer) {
      event.dataTransfer.effectAllowed = 'move';
      event.dataTransfer.setData('text/plain', dragItem.id);
    }
  });
  document.addEventListener('dragover', function (event) {
    if (!dragItem) return;
    const target = event.target.closest(dragItem.kind === 'scene' ? '[data-scene-drag]' : '[data-action-drag]');
    if (target) { event.preventDefault(); if (event.dataTransfer) event.dataTransfer.dropEffect = 'move'; }
  });
  document.addEventListener('drop', function (event) {
    if (!dragItem) return;
    const target = event.target.closest(dragItem.kind === 'scene' ? '[data-scene-drag]' : '[data-action-drag]');
    if (!target) return;
    event.preventDefault();
    const items = dragItem.kind === 'scene' ? H.state.scenes : draft() && draft().actions;
    const targetId = dragItem.kind === 'scene' ? target.dataset.sceneDrag : target.dataset.actionDrag;
    if (items && targetId !== dragItem.id) {
      const from = items.findIndex(item => item.id === dragItem.id);
      const to = items.findIndex(item => item.id === targetId);
      if (from >= 0 && to >= 0) {
        items.splice(to, 0, items.splice(from, 1)[0]);
        if (dragItem.kind === 'scene') H.save();
        else H.ui.sceneDraftDirty = true;
        H.render();
      }
    }
    dragItem = null;
  });
  document.addEventListener('dragend', function () { dragItem = null; });
})();
