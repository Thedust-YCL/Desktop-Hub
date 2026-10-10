/* Native scene UI. The original prototype and independent app launcher remain intact. */
(function () {
  'use strict';
  const H = window.Hub, desktop = window.DesktopHubDesktop, bridge = window.DesktopHubNative.bridge;
  const surface = desktop.getState().surface;
  const e = value => H.escape(value), icon = (name, size = 18) => H.icon(name, size);
  const copy = value => JSON.parse(JSON.stringify(value));
  const types = { application: '启动应用', web: '打开网页', file: '打开文件', folder: '打开文件夹', script: '执行脚本' };
  const labels = { Pending: '待执行', Running: '执行中', Success: '成功', Skipped: '已跳过', Failed: '失败', PartialSuccess: '部分成功' };
  const observations = { completed: '前置动作完成', process: '前置进程出现', window: '前置窗口出现' };
  const sceneIcons = { scenes: '组合', pen: '创作', game: '游戏', coffee: '休闲', app: '应用', folder: '文件夹', globe: '网页', code: '脚本', music: '音乐', star: '收藏' };
  let executions = [], selectedExecution = '', saving = false, editorError = '';
  const settings = () => desktop.getState().hostState?.settings;
  const scenes = () => [...(settings()?.scenes || [])].sort((a, b) => a.displayOrder - b.displayOrder);
  const apps = () => settings()?.appEntries || [];
  const draft = () => H.ui.sceneDraft;
  const selected = () => draft()?.actions.find(action => action.id === H.ui.selectedActionId);
  const uuid = () => window.crypto.randomUUID();
  const btn = (action, text, attributes = '', style = '') => `<button class="button ${style}" data-action="${action}" ${attributes}>${text}</button>`;
  const options = (values, chosen) => Object.entries(values).map(([value, label]) => `<option value="${e(value)}" ${value === chosen ? 'selected' : ''}>${e(label)}</option>`).join('');
  const sceneSymbol = (scene, size = 22) => ['scenes', 'pen', 'game', 'coffee', 'app', 'folder', 'globe', 'code', 'music', 'star'].includes(scene.icon)
    ? icon(scene.icon, size) : `<span class="scene-custom-icon">${e(scene.icon)}</span>`;
  function pin(where, id) { return settings()?.launcherPins.some(pin => pin.enabled && pin.surface === where && pin.itemType === 'scene' && pin.itemId === id); }
  function dirty() { H.ui.sceneDraftDirty = true; editorError = ''; }
  function openEditor(scene) {
    H.ui.sceneDraft = copy(scene); H.ui.sceneDraftDirty = false; H.ui.selectedActionId = scene.actions[0]?.id;
    H.ui.sceneEditorAdvanced = false; editorError = ''; H.navigate('editor');
  }
  async function change(type, payload) {
    if (saving) return false;
    saving = true;
    try {
      const result = await bridge.request(type, payload);
      if (!result?.settings) throw new Error('未收到保存结果，请重试。');
      desktop.applyHostState(result);
      return true;
    } catch (error) { editorError = error.message; H.toast(error.message, 'error'); return false; }
    finally { saving = false; H.render(); }
  }
  function executionMarkup(execution, compact = false) {
    if (!execution) return '';
    const finished = execution.actions.filter(action => !['Pending', 'Running'].includes(action.status)).length;
    return `<section class="scene-execution" data-execution-id="${e(execution.id)}" aria-label="场景执行结果"><div class="run-heading"><strong>${e(execution.sceneName)} · <span data-state="${execution.status}">${labels[execution.status] || '执行中'}</span></strong><span>${finished}/${execution.actions.length}</span></div><p class="scene-run-summary" role="status" aria-live="polite">${e(execution.summary)}</p><div class="progress-track"><span style="width:${execution.actions.length ? finished / execution.actions.length * 100 : 100}%"></span></div><details ${compact ? '' : 'open'}><summary>查看 ${execution.actions.length} 个动作的结果</summary><div class="scene-results">${execution.actions.map(action => `<div class="scene-result" data-state="${action.status}"><span>${icon(action.status === 'Success' ? 'check' : action.status === 'Failed' ? 'alert' : action.status === 'Skipped' ? 'arrow-right' : 'clock', 15)}</span><div><strong>${e(action.name)}</strong><p>${e(action.message)}</p><small>${e(action.code)}${action.processId ? ` · PID ${action.processId}` : ''}</small></div><span class="scene-result-status">${labels[action.status]}</span></div>`).join('')}</div></details>${compact ? '' : `<label class="scene-history-choice">本次会话的执行<select data-change="scene-execution-select">${[...executions].reverse().map(item => `<option value="${e(item.id)}" ${item.id === execution.id ? 'selected' : ''}>${e(item.sceneName)} · ${labels[item.status]} · ${e(new Date(item.createdAt).toLocaleTimeString())}</option>`).join('')}</select></label>`}</section>`;
  }
  function refreshExecution() {
    const execution = executions.find(item => item.id === selectedExecution) || executions.at(-1);
    document.querySelector('.scene-execution')?.remove();
    document.querySelector('.mini-scene-feedback')?.remove();
    document.querySelectorAll('[data-action="run-scene"]').forEach(button => {
      const latest = executions.filter(item => item.sceneId === button.dataset.id).at(-1);
      button.dataset.sceneStatus = latest?.status || '';
      const scene = scenes().find(item => item.id === button.dataset.id);
      if (scene) { button.disabled = !scene.enabled; button.title = `${scene.name} · ${latest ? labels[latest.status] : scene.enabled ? '执行场景' : '已停用'}`; }
      if (scene && button.querySelector('.scene-icon')) button.querySelector('.scene-icon').innerHTML = sceneSymbol(scene);
    });
    if (!execution) return;
    if (surface === 'mini') {
      const finished = execution.actions.filter(action => !['Pending', 'Running'].includes(action.status)).length;
      document.querySelector('.mini-bar')?.insertAdjacentHTML('beforeend', `<button class="mini-scene-feedback" data-action="scene-results" title="${e(execution.sceneName)}：${e(execution.summary)}。打开 Dock 查看结果" data-state="${execution.status}" aria-label="${e(execution.sceneName)} ${labels[execution.status]}，查看结果">${icon(execution.status === 'Failed' || execution.status === 'PartialSuccess' ? 'alert' : 'scenes', 16)}<small>${finished}/${execution.actions.length}</small></button>`);
    } else {
      const container = document.querySelector(surface === 'dock' ? '.dock-panel' : '.workspace-content');
      container?.insertAdjacentHTML('beforeend', executionMarkup(execution, surface === 'dock'));
    }
  }
  function acceptExecutions(next) {
    if (!Array.isArray(next)) return;
    executions = copy(next);
    refreshExecution();
  }
  H.pages.scenes = () => `<div class="page-heading"><div><h1 class="page-title" tabindex="-1">场景中心</h1><p class="subtitle">把常用应用和操作组合起来，一次开始。</p></div>${btn('new-scene', icon('plus') + ' 新建场景', '', 'primary')}</div><div class="scene-toolbar"><p class="subtitle">${scenes().length} 个场景</p>${btn('launcher-manage', '管理固定入口', 'data-surface="dock"', 'quiet')}</div><div class="scenes-grid">${scenes().map((scene, index) => {
    const invalid = scene.actions.some(action => action.appEntryId && !apps().some(app => app.id === action.appEntryId));
    return `<article class="scene-card"><div class="scene-card-header"><span class="scene-icon">${sceneSymbol(scene, 26)}</span><div><h2>${e(scene.name)}</h2><p>${scene.actions.length} 个动作 · ${scene.enabled ? '已启用' : '已停用'}</p></div>${btn('edit-scene', icon('edit'), `data-id="${e(scene.id)}" aria-label="编辑 ${e(scene.name)}"`, 'quiet')}</div><p class="scene-card-description">${e(scene.actions.map(action => action.name).join(' · ') || '添加应用、网页或其他动作')}</p>${invalid ? '<p class="native-warning">应用引用待修复，请编辑场景重新关联。</p>' : ''}<div class="scene-card-meta"><span class="badge">${scene.dependencies.length ? `${scene.dependencies.length} 条依赖` : '动作彼此独立'}</span><span>${pin('mini', scene.id) ? 'Mini ' : ''}${pin('dock', scene.id) ? 'Dock' : ''}</span></div><div class="scene-card-footer">${btn('run-scene', icon('play') + ' 执行场景', `data-id="${e(scene.id)}" ${!scene.enabled ? 'disabled' : ''}`, 'primary')}${btn('scene-pin', icon('pin'), `data-id="${e(scene.id)}" aria-label="${pin('mini', scene.id) ? '取消' : ''}固定到 Mini"`, 'quiet')}${btn('scene-dock-pin', 'Dock', `data-id="${e(scene.id)}" aria-pressed="${!!pin('dock', scene.id)}"`, 'quiet')}${btn('scene-move', '↑', `data-id="${scene.id}" data-direction="-1" aria-label="上移场景" ${index === 0 ? 'disabled' : ''}`, 'quiet')}${btn('scene-move', '↓', `data-id="${scene.id}" data-direction="1" aria-label="下移场景" ${index === scenes().length - 1 ? 'disabled' : ''}`, 'quiet')}${btn('scene-delete', icon('trash'), `data-id="${scene.id}" aria-label="删除场景"`, 'quiet')}</div></article>`;
  }).join('') || `<div class="launcher-empty"><h3>准备你的第一个场景</h3><p>从应用库选择多个应用，再按需要加入网页、文件或脚本。</p>${btn('new-scene', '新建场景', '', 'primary')}</div>`}</div>`;

  function field(label, name, value, type = 'text', attributes = '') {
    return `<label class="field"><span>${label}</span><input data-change="native-action-field" data-field="${name}" type="${type}" value="${e(value)}" ${attributes}></label>`;
  }
  function config(action) {
    const reference = action.type === 'application' && action.appEntryId;
    const app = apps().find(item => item.id === action.appEntryId);
    const edges = draft().dependencies.filter(edge => edge.actionId === action.id);
    return `<section class="panel native-action-config"><h2>${e(types[action.type])}</h2><div class="stack">${field('动作名称', 'name', action.name, 'text', 'maxlength="200"')}
      ${action.type === 'application' ? `<label class="field"><span>从应用库选择</span><select data-change="native-action-app"><option value="">高级：直接指定程序</option>${action.appEntryId && !app ? `<option value="${e(action.appEntryId)}" selected>引用失效，请重新选择</option>` : ''}${apps().map(item => `<option value="${item.id}" ${item.id === action.appEntryId ? 'selected' : ''}>${e(item.name)}</option>`).join('')}</select></label>${reference ? `<p class="scene-app-reference ${!app ? 'native-warning' : ''}">${app ? e(app.target) + '<br>沿用应用库的参数与工作目录。' : '应用已移除，选择其他应用即可修复。'}</p>` : ''}` : ''}
      ${!reference ? `<div class="field"><span>${action.type === 'web' ? 'HTTP / HTTPS 地址' : '目标路径'}</span><div class="scene-target-input"><input data-change="native-action-field" data-field="target" value="${e(action.target)}" ${action.type === 'web' ? 'placeholder="https://example.com"' : 'placeholder="选择电脑上的文件"'}>${action.type !== 'web' ? btn('native-scene-pick', icon('folder') + ' 选择', `data-kind="${action.type}"`, 'quiet') : ''}</div></div>${action.type === 'script' ? '<p class="scene-form-note">只执行你添加的脚本。终端可见；超时后请检查终端中的输入或错误。</p>' : ''}` : ''}
      <label class="check-row"><input type="checkbox" data-change="native-action-field" data-field="enabled" ${action.enabled ? 'checked' : ''}><span>启用这个动作</span></label>
      <div class="scene-schedule"><h3>执行安排</h3><p class="scene-form-note">无依赖的动作并行执行。列表顺序只影响展示。</p><div class="form-grid">${field('开始前延迟（毫秒）', 'delayMs', action.delayMs, 'number', 'min="0" max="600000"')}<label class="field"><span>完成条件</span><select data-change="native-action-field" data-field="completionCondition">${options({ submitted: '请求已提交', process: '进程已出现', window: '窗口已出现' }, action.completionCondition)}</select></label>${field('等待超时（毫秒）', 'timeoutMs', action.timeoutMs, 'number', 'min="1" max="600000"')}</div><h3>依赖的动作</h3>${draft().actions.filter(item => item.id !== action.id).map(item => {
        const edge = edges.find(edge => edge.dependsOnActionId === item.id);
        return `<div class="scene-dependency"><label class="check-row"><input type="checkbox" data-change="native-dependency-toggle" data-id="${item.id}" ${edge ? 'checked' : ''}><span>${e(item.name)}${!item.enabled ? '（已停用）' : ''}</span></label>${edge ? `<select data-change="native-dependency-condition" data-id="${item.id}" aria-label="等待 ${e(item.name)} 的条件">${options(observations, edge.condition)}</select><input type="number" value="${edge.timeoutMs}" data-change="native-dependency-timeout" data-id="${item.id}" aria-label="依赖等待超时，毫秒" min="1" max="600000">` : ''}</div>`;
      }).join('') || '<p class="scene-form-note">添加更多动作后可以设置依赖。</p>'}</div>
      <details class="scene-advanced" ${H.ui.sceneEditorAdvanced ? 'open' : ''}><summary>高级参数</summary><div class="stack">${!reference && ['application', 'script'].includes(action.type) ? `${action.type === 'script' ? `<label class="field"><span>脚本参数（每行一个参数）</span><textarea rows="3" data-change="native-script-arguments">${e(action.argumentList.join('\n'))}</textarea></label>` : field('启动参数', 'arguments', action.arguments)}${field('工作目录（留空使用目标目录）', 'workingDirectory', action.workingDirectory)}${action.type === 'script' ? `<label class="check-row"><input type="checkbox" data-change="native-action-field" data-field="runAsAdministrator" ${action.runAsAdministrator ? 'checked' : ''}><span>通过 Windows UAC 请求管理员权限</span></label>` : ''}` : ''}${['application', 'script'].includes(action.type) ? `<label class="field"><span>重复执行规则</span><select data-change="native-action-field" data-field="repeatPolicy">${options(action.type === 'script' ? { default: '每次场景执行最多一次', oncePerSession: '本次 Desktop Hub 会话只执行一次' } : { default: '已运行则跳过', always: '每次都提交启动请求' }, action.repeatPolicy)}</select></label>` : ''}${field('指定等待的 EXE 路径（可选）', 'waitTargetPath', action.waitTargetPath)}${field('窗口标题包含（可选）', 'windowTitle', action.windowTitle)}<p class="scene-form-note">默认等待当前目标的进程或窗口。启动器可指定其最终应用路径。</p></div></details>
      ${btn('native-action-delete', icon('trash') + ' 删除动作', `data-id="${action.id}"`, 'danger')}
    </div></section>`;
  }
  H.pages.editor = () => {
    const scene = draft(); if (!scene) return H.pages.scenes();
    return `<div class="page-heading"><div><h1 class="page-title" tabindex="-1">场景编辑器</h1><p class="subtitle">${e(scene.name)}${H.ui.sceneDraftDirty ? ' · 尚未保存' : ''}</p></div><div class="toolbar">${btn('native-editor-back', '返回场景中心', '', 'quiet')}${btn('native-scene-save', saving ? '保存中…' : '保存场景', saving ? 'disabled' : '', 'primary')}</div></div><div class="editor-scene-meta"><label class="field"><span>场景名称</span><input data-change="native-scene-field" data-field="name" value="${e(scene.name)}" maxlength="200"></label><label class="field"><span>场景图标</span><select data-change="native-scene-icon">${options({ ...sceneIcons, custom: '自定义符号' }, sceneIcons[scene.icon] ? scene.icon : 'custom')}</select></label>${!sceneIcons[scene.icon] ? `<label class="field"><span>自定义符号</span><input data-change="native-scene-field" data-field="icon" value="${e(scene.icon)}" maxlength="8" placeholder="例如 ☕"></label>` : ''}<label class="check-row"><input type="checkbox" data-change="native-scene-field" data-field="enabled" ${scene.enabled ? 'checked' : ''}><span>启用场景</span></label></div><p class="form-error scene-editor-error" role="alert">${e(editorError)}</p><div class="native-scene-editor"><aside class="panel native-action-list"><div class="scene-list-heading"><h2>动作 · ${scene.actions.length}</h2>${btn('native-add-action', icon('plus') + ' 添加', '', 'quiet')}</div><div class="scene-sequence-tools">${btn('native-sequence', '按列表顺序执行', '', 'quiet')}${btn('native-parallel', '全部独立执行', '', 'quiet')}</div><p class="scene-form-note">顺序执行会建立明确依赖；上下移动不会改变依赖。</p>${scene.actions.map((action, index) => {
      const dependencies = scene.dependencies.filter(edge => edge.actionId === action.id);
      return `<div class="native-action-row ${action.id === H.ui.selectedActionId ? 'selected' : ''}"><button class="native-action-select" data-action="native-select-action" data-id="${action.id}"><span class="scene-action-index">${index + 1}</span><span><strong>${e(action.name)}</strong><small>${e(types[action.type])}${action.enabled ? '' : ' · 已停用'}</small><small>${dependencies.length ? dependencies.map(edge => '← ' + e(scene.actions.find(item => item.id === edge.dependsOnActionId)?.name)).join('、') : '独立 · 可并行'}${action.delayMs ? ` · 延迟 ${action.delayMs}ms` : ''}</small></span></button><div class="native-action-order">${btn('native-action-move', '↑', `data-id="${action.id}" data-direction="-1" aria-label="上移动作" ${index === 0 ? 'disabled' : ''}`, 'quiet')}${btn('native-action-move', '↓', `data-id="${action.id}" data-direction="1" aria-label="下移动作" ${index === scene.actions.length - 1 ? 'disabled' : ''}`, 'quiet')}</div></div>`;
    }).join('') || '<p class="launcher-pin-empty">先从应用库添加一个动作。</p>'}</aside>${selected() ? config(selected()) : '<section class="panel launcher-empty"><h3>组合你的日常操作</h3><p>添加动作后，在这里选择目标和执行安排。</p></section>'}</div>`;
  };
  Object.assign(H.actions, {
    'new-scene': () => openEditor({ id: uuid(), name: '新场景', icon: 'scenes', enabled: true, displayOrder: scenes().length, actions: [], dependencies: [] }),
    'edit-scene': el => { const scene = scenes().find(item => item.id === el.dataset.id); if (scene) { if (surface !== 'workspace') H.navigate('scenes'); else openEditor(scene); } },
    'native-editor-back': () => H.navigate('scenes'),
    'native-scene-field': el => { if (!draft()) return; draft()[el.dataset.field] = el.type === 'checkbox' ? el.checked : el.value; dirty(); },
    'native-scene-icon': el => { if (draft()) { draft().icon = el.value === 'custom' ? '✨' : el.value; dirty(); H.render(); } },
    'native-select-action': el => { H.ui.selectedActionId = el.dataset.id; H.render(); },
    'native-add-action': () => H.showModal('添加动作', `<div class="action-type-choices">${Object.entries(types).map(([type, label]) => btn('native-add-type', e(label), `data-type="${type}"`)).join('')}</div>`),
    'native-add-type': el => {
      if (!draft() || !types[el.dataset.type]) return;
      const action = { id: uuid(), name: types[el.dataset.type], type: el.dataset.type, appEntryId: el.dataset.type === 'application' ? apps()[0]?.id || null : null, referenceInvalid: false, target: '', arguments: '', argumentList: [], workingDirectory: '', enabled: true, displayOrder: draft().actions.length, delayMs: 0, timeoutMs: 10000, completionCondition: 'submitted', waitTargetPath: '', windowTitle: '', repeatPolicy: 'default', runAsAdministrator: false };
      if (action.appEntryId) action.name = apps()[0].name;
      draft().actions.push(action); H.ui.selectedActionId = action.id; dirty(); H.closeModal(); H.render();
    },
    'native-action-field': el => {
      const action = selected(); if (!action) return;
      const key = el.dataset.field;
      action[key] = el.type === 'checkbox' ? el.checked : el.type === 'number' ? Number(el.value) : el.value;
      dirty();
      document.querySelector(`.native-action-select[data-id="${action.id}"] strong`)?.replaceChildren(document.createTextNode(action.name));
    },
    'native-action-app': el => { if (!selected()) return; selected().appEntryId = el.value || null; selected().referenceInvalid = false; if (el.value) { selected().target = ''; selected().arguments = ''; selected().workingDirectory = ''; selected().name = apps().find(app => app.id === el.value)?.name || selected().name; } dirty(); H.render(); },
    'native-script-arguments': el => { if (selected()) { selected().argumentList = el.value === '' ? [] : el.value.split(/\r?\n/); dirty(); } },
    'native-scene-pick': async el => {
      const action = selected(); if (!action) return;
      try { const result = await bridge.request('scenes.pickTarget', { kind: el.dataset.kind }, { timeoutMs: 120000 }); if (!result.cancelled) { action.target = result.target; dirty(); H.render(); } }
      catch (error) { H.toast(error.message, 'error'); }
    },
    'native-dependency-toggle': el => {
      const action = selected(); if (!action) return;
      draft().dependencies = draft().dependencies.filter(edge => !(edge.actionId === action.id && edge.dependsOnActionId === el.dataset.id));
      if (el.checked) draft().dependencies.push({ actionId: action.id, dependsOnActionId: el.dataset.id, condition: 'completed', timeoutMs: 10000 });
      dirty(); H.render();
    },
    'native-dependency-condition': el => { const edge = draft()?.dependencies.find(edge => edge.actionId === selected()?.id && edge.dependsOnActionId === el.dataset.id); if (edge) { edge.condition = el.value; dirty(); H.render(); } },
    'native-dependency-timeout': el => { const edge = draft()?.dependencies.find(edge => edge.actionId === selected()?.id && edge.dependsOnActionId === el.dataset.id); if (edge) { edge.timeoutMs = Number(el.value); dirty(); } },
    'native-sequence': () => { if (!draft()) return; draft().dependencies = draft().actions.slice(1).map((action, index) => ({ actionId: action.id, dependsOnActionId: draft().actions[index].id, condition: 'completed', timeoutMs: 10000 })); dirty(); H.render(); },
    'native-parallel': () => { if (draft()) { draft().dependencies = []; dirty(); H.render(); } },
    'native-action-move': el => { if (!draft()) return; const from = draft().actions.findIndex(action => action.id === el.dataset.id), to = from + Number(el.dataset.direction); if (from < 0 || to < 0 || to >= draft().actions.length) return; [draft().actions[from], draft().actions[to]] = [draft().actions[to], draft().actions[from]]; draft().actions.forEach((action, index) => action.displayOrder = index); dirty(); H.render(); },
    'native-action-delete': el => H.showModal('删除动作？', '<p>动作及其依赖关系会从草稿移除，保存后生效。</p>', btn('close-modal', '取消') + btn('native-action-delete-confirm', '删除动作', `data-id="${el.dataset.id}"`, 'danger')),
    'native-action-delete-confirm': el => { draft().actions = draft().actions.filter(action => action.id !== el.dataset.id); draft().dependencies = draft().dependencies.filter(edge => edge.actionId !== el.dataset.id && edge.dependsOnActionId !== el.dataset.id); H.ui.selectedActionId = draft().actions[0]?.id; dirty(); H.closeModal(); H.render(); },
    'native-scene-save': async () => {
      const scene = draft(); if (!scene || saving) return;
      if (await change('scenes.save', { scene: copy(scene) })) { H.ui.sceneDraftDirty = false; H.navigate('scenes'); H.toast('场景已保存。'); }
    },
    'scene-delete': el => H.showModal('删除场景？', '<p>只删除场景和固定入口，应用库及原始文件保留。</p>', btn('close-modal', '取消') + btn('scene-delete-confirm', '删除场景', `data-id="${el.dataset.id}"`, 'danger')),
    'scene-delete-confirm': async el => { if (await change('scenes.remove', { sceneId: el.dataset.id })) H.closeModal(); },
    'scene-move': async el => { const list = scenes(), from = list.findIndex(scene => scene.id === el.dataset.id), to = from + Number(el.dataset.direction); if (from < 0 || to < 0 || to >= list.length) return; [list[from], list[to]] = [list[to], list[from]]; await change('scenes.reorder', { sceneIds: list.map(scene => scene.id) }); },
    'scene-dock-pin': async el => {
      const list = settings().launcherPins.filter(pin => pin.surface === 'dock').map(pin => ({ itemType: pin.itemType, itemId: pin.itemId, enabled: pin.enabled }));
      const index = list.findIndex(pin => pin.itemType === 'scene' && pin.itemId === el.dataset.id);
      if (index < 0) list.push({ itemType: 'scene', itemId: el.dataset.id, enabled: true }); else list.splice(index, 1);
      await change('launcher.setPins', { surface: 'dock', pins: list });
    },
    'run-scene': async el => {
      try { const result = await bridge.request('scenes.run', { sceneId: el.dataset.id }); if (!result?.execution?.id) throw new Error('未收到场景执行实例，请查看本地诊断。'); selectedExecution = result.execution.id; acceptExecutions(result.executions || [...executions, result.execution]); }
      catch (error) { H.toast(error.message, 'error'); }
    },
    'scene-execution-select': el => { selectedExecution = el.value; refreshExecution(); },
    'scene-results': () => H.setSurface('dock')
  });
  const render = H.render;
  H.render = () => { render(); refreshExecution(); H.restoreViewport?.(); };
  bridge.on('scene.executions', payload => acceptExecutions(payload.executions));
  bridge.on('state.changed', state => acceptExecutions(state.executions));
  document.addEventListener('DOMContentLoaded', () => acceptExecutions(desktop.getState().hostState?.executions));
  document.addEventListener('toggle', event => { if (event.target.classList?.contains('scene-advanced')) H.ui.sceneEditorAdvanced = event.target.open; }, true);
  window.DesktopHubScenes = Object.freeze({ getExecutions: () => copy(executions), executionMarkup, sceneSymbol, acceptExecutions });
}());
