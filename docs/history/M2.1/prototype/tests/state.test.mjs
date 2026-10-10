/**
 * M0 business-flow integration tests. The application scripts run unchanged in
 * a VM with a small synthetic DOM and deterministic timers. These checks verify
 * state and event dispatch, not browser layout, focus behavior or native APIs.
 * This file reads only the four prototype source files beside this directory.
 */
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import test from 'node:test';

const scripts = await Promise.all(['app.js', 'scenes.js', 'resources.js', 'settings.js'].map(async name => ({
  name,
  source: await readFile(new URL('../' + name, import.meta.url), 'utf8')
})));
const plain = value => JSON.parse(JSON.stringify(value));

function createHarness(storage = new Map()) {
  const listeners = new Map();
  const nodes = new Map();
  const errors = [];
  const timers = new Map();
  const history = [];
  let now = 0;
  let timerId = 0;
  let systemDark = false;
  let document;

  class Element {
    constructor(id = '') {
      this.id = id;
      this.dataset = {};
      this.tagName = 'DIV';
      this.type = '';
      this.value = '';
      this.checked = false;
      this.disabled = false;
      this.isConnected = true;
      this.innerHTML = '';
      this.textContent = '';
      this.children = [];
      this.events = new Map();
      this.classList = { contains: () => false, add() {}, remove() {}, toggle() {} };
    }
    addEventListener(name, callback) { this.events.set(name, callback); }
    focus() { document.activeElement = this; }
    append(child) { this.children.push(child); }
    remove() { this.isConnected = false; }
    scrollTo() {}
    setSelectionRange() {}
    closest(selector) {
      if (selector === '[data-action]') return this.dataset.action ? this : null;
      if (selector === '[data-change]') return this.dataset.change ? this : null;
      if (selector === '[data-scene-drag]') return this.dataset.sceneDrag ? this : null;
      if (selector === '[data-action-drag]') return this.dataset.actionDrag ? this : null;
      return null;
    }
  }

  function node(id) {
    if (!nodes.has(id)) nodes.set(id, new Element(id));
    return nodes.get(id);
  }

  document = {
    documentElement: { dataset: {} },
    activeElement: new Element(),
    getElementById: node,
    createElement: () => new Element(),
    querySelector: selector => node('query:' + selector),
    querySelectorAll: () => [],
    addEventListener(name, callback) {
      if (!listeners.has(name)) listeners.set(name, []);
      listeners.get(name).push(callback);
    }
  };
  const mediaListeners = [];
  const context = vm.createContext({
    window: {},
    document,
    localStorage: {
      getItem: key => storage.get(key) ?? null,
      setItem: (key, value) => storage.set(key, String(value)),
      removeItem: key => storage.delete(key)
    },
    matchMedia: () => ({ get matches() { return systemDark; }, addEventListener: (_, callback) => mediaListeners.push(callback) }),
    setTimeout: (callback, delay = 0) => {
      const id = ++timerId;
      timers.set(id, { callback, due: now + Number(delay) });
      return id;
    },
    clearTimeout: id => timers.delete(id),
    console: { log() {}, warn() {}, error: (...args) => errors.push(args) }
  });
  scripts.forEach(({ name, source }) => vm.runInContext(source, context, { filename: name }));
  const H = context.window.Hub;
  const realRender = H.render;
  H.render = () => {
    realRender();
    history.push({ surface: H.ui.surface, page: H.ui.page, statuses: H.ui.run ? plain(H.ui.run.items.map(item => item.status)) : [] });
  };

  function fire(type, event = {}) {
    event.type = type;
    event.preventDefault ||= () => { event.defaultPrevented = true; };
    event.target ||= document.activeElement;
    for (const callback of listeners.get(type) || []) callback(event);
    assert.equal(errors.length, 0, 'No application handler should throw: ' + String(errors[0] || ''));
    return event;
  }
  function click(action, data = {}) {
    const target = new Element();
    target.tagName = 'BUTTON';
    target.dataset = { action, ...data };
    assert.equal(typeof H.actions[action], 'function', 'Action exists: ' + action);
    fire('click', { target });
  }
  function change(action, value, data = {}, kind = 'text') {
    const target = new Element();
    target.dataset = { change: action, ...data };
    target.tagName = kind === 'select' ? 'SELECT' : 'INPUT';
    target.type = kind === 'select' ? '' : kind;
    target.value = String(value);
    target.checked = !!value;
    const type = ['select', 'checkbox', 'radio'].includes(kind) ? 'change' : 'input';
    assert.equal(typeof H.actions[action], 'function', 'Change handler exists: ' + action);
    fire(type, { target });
  }
  function key(value, modifiers = {}) {
    return fire('keydown', { key: value, altKey: false, ctrlKey: false, shiftKey: false, metaKey: false, ...modifiers });
  }
  function advance(milliseconds) {
    const targetTime = now + milliseconds;
    let count = 0;
    while (true) {
      const next = [...timers.entries()].filter(([, timer]) => timer.due <= targetTime).sort((a, b) => a[1].due - b[1].due || a[0] - b[0])[0];
      if (!next) break;
      assert.ok(++count < 1000, 'Timer queue should be bounded');
      timers.delete(next[0]);
      now = next[1].due;
      next[1].callback();
    }
    now = targetTime;
  }
  function finishRun() {
    for (let count = 0; count < 100 && !H.ui.run?.finished; count++) advance(450);
    assert.equal(H.ui.run?.finished, true, 'Synthetic run finishes without native execution');
  }
  function completeOnboarding() {
    H.openOnboarding();
    click('onboarding-next');
    click('onboarding-confirm-scan');
    click('onboarding-next');
    click('onboarding-finish');
    click('onboarding-dismiss');
  }
  function createScene(name, type = 'program') {
    click('scene-new');
    change('scene-draft-field', name, { field: 'name' });
    click('scene-add-action');
    assert.match(node('modal-root').innerHTML, /添加动作/);
    click('scene-add-action-type', { type });
    click('scene-pick-target');
    click('scene-target-selected', { index: '0' });
    const id = H.ui.sceneDraft.id;
    click('scene-save');
    return H.state.scenes.find(item => item.id === id);
  }
  return { H, storage, node, fire, click, change, key, advance, finishRun, completeOnboarding, createScene, history, errors,
    theme: () => document.documentElement.dataset.theme,
    setSystemDark(value) { systemDark = value; mediaListeners.forEach(callback => callback()); } };
}

test('1. First run requires scan confirmation, saves Mini position, then creates and pins a local scene', () => {
  const h = createHarness();
  const H = h.H;
  assert.equal(H.state.settings.theme, 'system');
  assert.equal(H.state.files.length, 0);
  assert.equal(H.state.indexConfirmed, false);
  h.fire('DOMContentLoaded');
  assert.match(h.node('modal-root').innerHTML, /欢迎使用 Desktop Hub/);
  assert.equal(H.ui.modal.locked, true);
  h.key('Escape');
  assert.equal(H.ui.modal.locked, true, 'Welcome cannot be silently skipped with Esc');
  h.click('onboarding-next');
  assert.match(h.node('modal-root').innerHTML, /确认模拟扫描结果/);
  assert.equal(H.state.files.length, 0, 'A preview is not a durable index');
  const excluded = H.seedFiles()[0].id;
  h.change('onboarding-file', false, { id: excluded }, 'checkbox');
  h.click('onboarding-confirm-scan');
  assert.equal(H.state.indexConfirmed, true);
  assert.equal(H.state.files.length, H.seedFiles().length - 1);
  assert.equal(H.state.files.some(file => file.id === excluded), false);
  h.click('onboarding-next');
  h.click('onboarding-position', { position: 'right' });
  h.click('onboarding-finish');
  assert.equal(H.state.settings.miniPosition, 'right');
  assert.equal(H.state.onboardingComplete, true);
  assert.equal(H.ui.surface, 'workspace');
  assert.equal(H.ui.page, 'home');
  h.click('onboarding-create-scene');
  assert.equal(H.ui.page, 'editor', 'Onboarding connects to the actual scene editor action');
  h.change('scene-draft-field', '我的第一个场景', { field: 'name' });
  h.click('scene-add-action-type', { type: 'program' });
  h.click('scene-target-selected', { index: '0' });
  const sceneId = H.ui.sceneDraft.id;
  h.click('scene-save');
  h.click('scene-pin', { id: sceneId });
  assert.equal(H.state.scenes.find(item => item.id === sceneId).pinned, true);
  assert.equal(H.state.scenes.filter(item => item.pinned).length, 4);
  const restored = createHarness(h.storage).H;
  assert.equal(restored.state.onboardingComplete, true);
  assert.equal(restored.state.settings.miniPosition, 'right');
  assert.equal(restored.state.scenes.some(item => item.id === sceneId), true);
});

test('2. Creation run shows all five simulated states, skips failed dependencies and keeps Dock open', () => {
  const h = createHarness();
  h.completeOnboarding();
  h.H.setSurface('mini');
  h.click('run-scene', { id: 'creation' });
  assert.equal(h.H.ui.surface, 'dock');
  assert.ok(h.H.ui.run.items.every(item => item.status === 'pending'));
  h.advance(450);
  assert.ok(h.H.ui.run.items.some(item => item.status === 'running'));
  h.finishRun();
  assert.deepEqual(plain(h.H.ui.run.items.map(item => item.status)), ['success', 'skip', 'success', 'fail', 'skip']);
  assert.equal(h.H.ui.run.items.find(item => item.id === 'c5').dependencyFailed, true);
  assert.equal(h.H.ui.run.items.find(item => item.id === 'c3').status, 'success', 'Independent successful chain continues after parallel failure');
  assert.ok(h.H.ui.run.items.every(item => item.detail.includes('模拟')));
  assert.equal(h.H.ui.surface, 'dock', 'Completion does not automatically close Dock');
  assert.ok(h.history.some(entry => entry.statuses.includes('pending')));
  assert.ok(h.history.some(entry => entry.statuses.includes('running')));
  assert.match(h.node('app').innerHTML, /模拟.*完成/);
  h.click('edit-scene', { id: 'creation' });
  assert.equal(h.H.ui.page, 'editor', 'Dock edit action integrates with scene module');
});

test('3. Scene CRUD, waiting parameters, cycle validation, ordering and Mini 2–4 protection integrate', () => {
  const h = createHarness();
  h.completeOnboarding();
  const H = h.H;
  const current = h.createScene('测试工作环境');
  const firstId = current.actions[0].id;
  h.click('scene-edit', { id: current.id });
  h.click('scene-add-action-type', { type: 'script' });
  h.click('scene-target-selected', { index: '0' });
  const scriptId = H.ui.selectedActionId;
  assert.equal(H.ui.sceneDraft.actions[1].repeat, 'once');
  const selectedScriptTarget = H.ui.sceneDraft.actions[1].target;
  h.change('scene-action-field', 'C:\\真实脚本\\禁止执行.ps1', { field: 'target' });
  assert.equal(H.ui.sceneDraft.actions[1].target, selectedScriptTarget, 'Script target can only be chosen from the synthetic existing-file picker');
  h.change('scene-action-field', firstId, { field: 'dependsOn' }, 'select');
  h.change('scene-action-field', 'window', { field: 'mode' }, 'select');
  h.click('scene-advanced');
  h.change('scene-action-field', 3, { field: 'delay' }, 'number');
  h.change('scene-action-field', 10, { field: 'timeout' }, 'number');
  assert.match(H.pages.editor(), /等待类型/);
  h.click('scene-select-action', { id: firstId });
  h.change('scene-action-field', scriptId, { field: 'dependsOn' }, 'select');
  h.click('scene-save');
  assert.match(h.node('modal-root').innerHTML, /存在循环依赖/);
  assert.equal(H.state.scenes.find(item => item.id === current.id).actions.length, 1, 'Invalid draft is not persisted');
  h.click('close-modal');
  h.change('scene-action-field', '', { field: 'dependsOn' }, 'select');
  h.click('scene-save');
  const saved = H.state.scenes.find(item => item.id === current.id);
  assert.equal(saved.actions[1].mode, 'window');
  assert.equal(saved.actions[1].delay, 3);
  assert.equal(saved.actions[1].timeout, 10);
  assert.equal(saved.actions[1].dependsOn, firstId);
  h.click('scene-edit', { id: current.id });
  h.click('scene-action-up', { id: scriptId });
  assert.equal(H.ui.sceneDraft.actions[0].id, scriptId);
  h.click('scene-action-delete', { id: firstId });
  h.click('scene-action-delete-confirm', { id: firstId });
  assert.equal(H.ui.sceneDraft.actions[0].dependsOn, '');
  h.click('scene-back');
  assert.match(h.node('modal-root').innerHTML, /放弃未保存/);
  h.click('scene-discard');
  assert.equal(H.state.scenes.find(item => item.id === current.id).actions.length, 2, 'Discard preserves saved actions');
  h.click('scene-pin', { id: 'creation' });
  assert.equal(H.state.scenes.filter(item => item.pinned).length, 2);
  h.click('scene-pin', { id: 'game' });
  h.click('scene-delete', { id: 'game' });
  assert.equal(H.state.scenes.find(item => item.id === 'game').pinned, true);
  assert.equal(H.state.scenes.filter(item => item.pinned).length, 2);
  h.click('scene-pin', { id: 'creation' });
  h.click('scene-pin', { id: current.id });
  assert.equal(H.state.scenes.filter(item => item.pinned).length, 4);
  const extra = h.createScene('第五个场景');
  h.click('scene-pin', { id: extra.id });
  h.click('settings-pin', { id: extra.id });
  assert.equal(extra.pinned, false, 'Both scene and settings pages protect the four-item maximum');
  const oldIndex = H.state.scenes.findIndex(item => item.id === extra.id);
  h.click('scene-move-up', { id: extra.id });
  assert.equal(H.state.scenes.findIndex(item => item.id === extra.id), oldIndex - 1);
  h.click('scene-delete', { id: extra.id });
  assert.equal(H.state.scenes.some(item => item.id === extra.id), true, 'Delete asks for confirmation');
  h.click('scene-delete-confirm', { id: extra.id });
  assert.equal(H.state.scenes.some(item => item.id === extra.id), false);
});

test('4. Suggested tags require acceptance; list/grid/tag views, filename/tag search and favorites use confirmed relations', () => {
  const h = createHarness();
  h.completeOnboarding();
  const H = h.H;
  const before = H.state.files.length;
  h.click('resource-add-demo');
  assert.equal(H.state.files.length, before, 'New-file dialog does not index before its confirmation');
  h.click('resource-add-demo-confirm');
  assert.equal(H.state.files.length, before + 1);
  const file = H.state.files[0];
  const originalPath = file.path;
  assert.deepEqual(plain(file.tags), []);
  assert.equal(file.suggestions.length, 3);
  h.click('resource-suggestion-confirm', { id: file.id });
  assert.deepEqual(plain(file.tags), [], 'Showing confirmation still does not write formal tags');
  h.click('resource-modal-close');
  assert.deepEqual(plain(file.tags), [], 'Cancel preserves suggestions only');
  h.click('resource-suggestion-confirm', { id: file.id });
  h.click('resource-suggestion-accept', { id: file.id });
  assert.deepEqual(plain(file.tags).sort(), ['course', 'design', 'oct']);
  assert.equal(file.suggestions.length, 0);
  assert.equal(file.path, originalPath, 'Virtual grouping never relocates the file');
  h.click('resource-view', { view: 'list' });
  assert.match(H.pages.library(), /<table/);
  h.click('resource-view', { view: 'grid' });
  assert.match(H.pages.library(), /class="resource-grid"/);
  assert.doesNotMatch(H.pages.library(), /<img/);
  h.click('resource-filter-tag', { id: 'design' });
  assert.equal(H.ui.libraryView, 'tags');
  assert.match(H.pages.library(), /设计素材/);
  assert.match(H.pages.library(), new RegExp(H.escape(file.name).replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
  h.change('resource-search-query', file.name);
  assert.match(H.pages.search(), /找到 1 个资源/);
  h.change('resource-search-query', '课程作业');
  assert.match(H.pages.search(), /设计参考图/);
  h.change('resource-search-tag', 'oct', {}, 'select');
  assert.match(H.pages.search(), /设计参考图/);
  h.change('resource-search-query', '不存在的演示文件');
  assert.match(H.pages.search(), /没有找到相关资源/);
  h.click('resource-favorite', { id: file.id });
  assert.equal(file.favorite, true);
  assert.match(H.pages.favorites(), /设计参考图/);
  const folder = H.state.files.find(item => item.type === 'folder');
  const indexedCount = H.state.files.length;
  h.click('resource-open', { id: folder.id });
  assert.match(h.node('modal-root').innerHTML, /临时浏览/);
  h.click('resource-temp-open', { name: '草稿笔记.md' });
  assert.equal(H.state.files.length, indexedCount, 'Temporary folder browsing does not index children');
});

test('5. Missing repair changes only synthetic path/state, preserving tags/favorite; cleanup removes selected invalid indexes only', () => {
  const h = createHarness();
  h.completeOnboarding();
  const H = h.H;
  const file = H.state.files.find(item => item.status === 'Missing');
  const tags = plain(file.tags);
  const favorite = file.favorite;
  const oldPath = file.path;
  h.click('resource-repair', { id: file.id });
  assert.equal(file.status, 'Missing', 'Repair preview is not applied');
  h.node('resource-repair-path').value = '';
  h.click('resource-repair-confirm', { id: file.id });
  assert.equal(file.status, 'Missing', 'Empty repair path is rejected');
  h.node('resource-repair-path').value = 'D:\\模拟重新关联\\旧版提案.pdf';
  h.click('resource-repair-confirm', { id: file.id });
  assert.equal(file.status, 'Active');
  assert.notEqual(file.path, oldPath);
  assert.deepEqual(plain(file.tags), tags);
  assert.equal(file.favorite, favorite);
  const pending = H.state.files.find(item => item.status === 'PendingRepair');
  const active = H.state.files.find(item => item.status === 'Active');
  H.ui.missingSelected = [pending.id, active.id];
  h.click('resource-clean-selected');
  assert.equal(H.state.files.some(item => item.id === pending.id), true, 'Cleanup requires confirmation');
  h.click('resource-clean-confirm');
  assert.equal(H.state.files.some(item => item.id === pending.id), false);
  assert.equal(H.state.files.some(item => item.id === active.id), true, 'Valid indexes are excluded from cleanup');
  assert.equal(H.state.files.some(item => item.id === file.id), true, 'Repaired resource remains indexed');
});

test('6. Surface navigation, 500ms edge hover, game protection, Esc return and system/light/dark themes integrate', () => {
  const h = createHarness();
  h.completeOnboarding();
  const H = h.H;
  for (const page of ['home', 'scenes', 'library', 'tags', 'favorites', 'search', 'missing', 'settings']) {
    h.click('navigate', { page });
    assert.equal(H.ui.page, page);
    assert.equal(H.ui.surface, 'workspace');
    assert.ok(H.pages[page]().length > 0);
  }
  h.click('demo-surface', { surface: 'mini' });
  h.node('edge-trigger').events.get('mouseenter')();
  h.advance(499);
  assert.equal(H.ui.surface, 'mini');
  h.advance(1);
  assert.equal(H.ui.surface, 'dock', 'Default hover opens at 500ms');
  h.click('demo-surface', { surface: 'mini' });
  h.node('edge-trigger').events.get('mouseenter')();
  h.advance(250);
  h.change('foreground', 'game', {}, 'select');
  h.advance(500);
  assert.equal(H.ui.surface, 'mini', 'Changing to a game cancels the pending hover timer');
  h.change('foreground', 'edge', {}, 'select');
  h.node('edge-trigger').events.get('mouseenter')();
  h.advance(250);
  h.change('foreground', 'excluded:0', {}, 'select');
  h.advance(500);
  assert.equal(H.ui.surface, 'mini', 'Changing to an excluded program cancels the pending hover timer');
  h.node('edge-trigger').events.get('mouseenter')();
  h.advance(600);
  assert.equal(H.ui.surface, 'mini', 'Excluded foreground programs cannot begin edge activation');
  h.change('foreground', 'edge', {}, 'select');
  h.node('edge-trigger').events.get('mouseenter')();
  h.advance(250);
  H.ui.foreground = 'game';
  h.advance(250);
  assert.equal(H.ui.surface, 'mini', 'Hover callback rechecks game protection even without a surface mutation');
  h.change('foreground', 'edge', {}, 'select');
  h.node('edge-trigger').events.get('mouseenter')();
  h.advance(250);
  H.ui.foreground = 'excluded:0';
  h.advance(250);
  assert.equal(H.ui.surface, 'mini', 'Hover callback rechecks excluded-program protection');
  h.change('foreground', 'edge', {}, 'select');
  h.node('edge-trigger').events.get('mouseenter')();
  h.advance(250);
  H.state.settings.allowForeground = false;
  h.advance(250);
  assert.equal(H.ui.surface, 'mini', 'Hover callback rechecks changed foreground permission');
  H.state.settings.allowForeground = true;
  h.node('edge-trigger').events.get('mouseenter')();
  h.advance(250);
  h.node('edge-trigger').events.get('mouseleave')();
  h.advance(500);
  assert.equal(H.ui.surface, 'mini', 'Leaving the trigger cancels a pending hover');
  h.node('edge-trigger').events.get('mouseenter')();
  h.advance(250);
  h.click('open-workspace');
  h.advance(500);
  assert.equal(H.ui.surface, 'workspace', 'Opening Workspace cancels pending Dock activation');
  h.click('open-workspace');
  h.key('Escape');
  assert.equal(H.ui.surface, 'mini');
  assert.equal(H.ui.foreground, 'edge', 'Esc preserves the simulated previous foreground');
  h.change('foreground', 'game', {}, 'select');
  h.node('edge-trigger').events.get('mouseenter')();
  h.advance(1000);
  assert.equal(H.ui.surface, 'mini', 'Game protection blocks edge activation');
  h.change('foreground', 'edge', {}, 'select');
  h.key('d', { altKey: true });
  assert.equal(H.ui.surface, 'dock');
  h.key('w', { altKey: true });
  assert.equal(H.ui.surface, 'workspace');
  h.change('setting-value', 'labels', { setting: 'miniMode' }, 'select');
  const sceneName = '创作 <测试 & "引号">';
  H.state.scenes[0].name = sceneName;
  h.click('demo-surface', { surface: 'mini' });
  assert.match(h.node('app').innerHTML, /mode-labels/);
  assert.ok(h.node('app').innerHTML.includes('<span class="mini-item-label">' + H.escape(sceneName) + '</span>'), 'Mini labels mode renders escaped scene names');
  assert.equal(h.node('app').innerHTML.includes(sceneName), false);
  const actionName = '<script>模拟动作</script> & "目标"';
  H.state.scenes[0].actions[0].name = actionName;
  h.click('open-dock');
  assert.ok(h.node('app').innerHTML.includes(H.escape(actionName)), 'Dock scene previews escape action names');
  assert.equal(h.node('app').innerHTML.includes(actionName), false, 'Action names are displayed as text rather than injected HTML');
  h.click('open-workspace');
  h.change('setting-value', 'dark', { setting: 'theme' }, 'select');
  assert.equal(h.node('app').innerHTML.includes('Desktop Hub'), true);
  assert.equal(H.state.settings.theme, 'dark');
  assert.equal(h.theme(), 'dark');
  h.change('setting-value', 'light', { setting: 'theme' }, 'select');
  assert.equal(H.state.settings.theme, 'light');
  assert.equal(h.theme(), 'light');
  h.change('setting-value', 'system', { setting: 'theme' }, 'select');
  assert.equal(h.theme(), 'light');
  h.setSystemDark(true);
  assert.equal(H.state.settings.theme, 'system');
  assert.equal(h.theme(), 'dark', 'System-theme changes update the surface tokens');
  assert.match(H.pages.settings(), /跟随系统/);
});

test('7. Desktop-icon demo requires restore choice first; independent shortcuts reject collisions and save valid settings', () => {
  const h = createHarness();
  h.completeOnboarding();
  const H = h.H;
  h.click('setting-toggle', { setting: 'hideDesktopIcons' });
  assert.equal(H.state.settings.hideDesktopIcons, false);
  assert.equal(H.state.settings.restorePolicy, '');
  assert.match(h.node('modal-root').innerHTML, /选择桌面图标恢复策略/);
  h.click('settings-confirm-restore', { enable: 'true' });
  assert.equal(H.state.settings.hideDesktopIcons, false, 'Enable is rejected before choosing a policy');
  h.change('restore-policy', 'on-exit', {}, 'radio');
  h.click('settings-confirm-restore', { enable: 'true' });
  assert.equal(H.state.settings.restorePolicy, 'on-exit');
  assert.equal(H.state.settings.hideDesktopIcons, true);
  h.click('settings-restore-icons');
  assert.equal(H.state.settings.hideDesktopIcons, false);
  h.click('navigate', { page: 'settings' });
  h.click('settings-section', { section: 'shortcuts' });
  h.change('shortcut-value', 'alt + d', { setting: 'dockShortcut' });
  h.change('shortcut-value', 'ALT+D', { setting: 'workspaceShortcut' });
  h.click('settings-save-shortcuts');
  assert.equal(H.state.settings.workspaceShortcut, 'Alt+W');
  assert.match(h.node('shortcut-conflict').textContent, /快捷键冲突/);
  h.change('shortcut-value', 'Ctrl+Shift+W', { setting: 'workspaceShortcut' });
  h.click('settings-save-shortcuts');
  assert.equal(H.state.settings.dockShortcut, 'Alt+D');
  assert.equal(H.state.settings.workspaceShortcut, 'Ctrl+Shift+W');
  h.click('settings-section', { section: 'shortcuts' });
  h.change('shortcut-value', 'alt + space', { setting: 'dockShortcut' });
  h.click('settings-save-shortcuts');
  assert.equal(H.state.settings.dockShortcut, 'Alt+Space');
  h.click('demo-surface', { surface: 'mini' });
  h.key(' ', { altKey: true });
  assert.equal(H.ui.surface, 'dock', 'A browser Space key matches the normalized Space shortcut');
  h.key(' ', { altKey: true });
  assert.equal(H.ui.surface, 'mini', 'Space shortcut toggles Dock closed');
  h.key('W', { ctrlKey: true, shiftKey: true });
  assert.equal(H.ui.surface, 'workspace', 'Distinct Workspace shortcut still works after editing Dock shortcut');
  h.click('settings-confirm-reset');
  assert.equal(H.state.settings.theme, 'system');
  assert.equal(H.state.files.length, 0);
  assert.equal(H.state.onboardingComplete, false);
  assert.match(h.node('modal-root').innerHTML, /欢迎使用 Desktop Hub/);
});
