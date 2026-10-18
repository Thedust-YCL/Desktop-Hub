/* Translate source-owned strings only. Template values are deliberately opaque. */
(function (root) {
  'use strict';
  let language = 'zh-CN';
  function fragment(value) {
    if (language === 'zh-CN') return value.replaceAll('Desktop Hub', '桌面工作台');
    const catalog = root.DesktopHubTranslations || {};
    return value.split(/([<>"])/g).map(part => {
      const trimmed = part.trim();
      const translated = catalog[trimmed];
      return translated === undefined ? part : part.replace(trimmed, translated);
    }).join('');
  }
  function text(value, ...args) {
    if (Array.isArray(value)) return value.reduce((out, part, i) => out + fragment(part) + (i < args.length ? args[i] : ''), '');
    return fragment(String(value ?? ''));
  }
  function setLanguage(value) {
    language = value === 'en-US' ? value : 'zh-CN';
    if (root.document) {
      root.document.documentElement.lang = language;
      root.document.title = text('Desktop Hub');
    }
  }
  root.DesktopHubI18n = Object.freeze({ text, setLanguage, get language() { return language; }, get locale() { return language; } });
  setLanguage();
}(globalThis));
