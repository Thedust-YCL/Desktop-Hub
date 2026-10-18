import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import test from 'node:test';

function setup() {
  const document = { documentElement: { lang: '' }, title: '' };
  const context = vm.createContext({ document });
  context.window = context;
  for (const file of ['translations.js', 'i18n.js']) {
    const path = new URL('../' + file, import.meta.url);
    if (fs.existsSync(path)) vm.runInContext(fs.readFileSync(path, 'utf8'), context);
  }
  assert.ok(context.DesktopHubI18n, 'the desktop language service must be installed');
  return { i18n: context.DesktopHubI18n, document };
}

test('Chinese is the default and brand follows the chosen language', () => {
  const { i18n, document } = setup();
  i18n.setLanguage();
  assert.equal(i18n.text('Desktop Hub'), '桌面工作台');
  assert.equal(document.documentElement.lang, 'zh-CN');
  i18n.setLanguage('en-US');
  assert.equal(i18n.text('Desktop Hub'), 'Desktop Hub');
  assert.equal(document.title, 'Desktop Hub');
  assert.equal(i18n.text('我的应用'), 'My apps');
  i18n.setLanguage('unsupported');
  assert.equal(i18n.language, 'zh-CN');
});

test('template interpolation preserves user text, paths and markup escapes', () => {
  const { i18n } = setup();
  i18n.setLanguage('en-US');
  const userName = '我的应用 Desktop Hub &lt;b&gt;中文项目&lt;/b&gt;';
  assert.equal(i18n.text`<strong>我的应用</strong><span>${userName}</span>`, `<strong>My apps</strong><span>${userName}</span>`);
  assert.equal(i18n.text`<button aria-label="设置" data-id="${'中文ID'}">设置</button>`, '<button aria-label="Settings" data-id="中文ID">Settings</button>');
});

test('switching back translates newly rendered content without touching existing values', () => {
  const { i18n } = setup();
  const render = value => i18n.text`<p>文件资源库</p><input value="${value}">`;
  i18n.setLanguage('en-US');
  assert.equal(render('中文.txt'), '<p>File library</p><input value="中文.txt">');
  i18n.setLanguage('zh-CN');
  assert.equal(render('中文.txt'), '<p>文件资源库</p><input value="中文.txt">');
});
