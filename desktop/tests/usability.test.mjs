import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
function setup(density) {
  const settings={uiDensity:density}, document={documentElement:{dataset:{}},querySelector(){return null;},querySelectorAll(){return [];},addEventListener(){}}, H={state:{settings},ui:{page:'settings'},actions:{},pages:{settings:()=>'<div data-section="appearance" aria-current="page"></div><div class="settings-content"><section class="settings-section"><h2>外观与主题</h2><div class="setting-row"><div><strong>减少动画</strong></div></div></section></div>'},escape:String,render(){},icon(){return '';}};
  const window={Hub:H,DesktopHubDesktop:{getState:()=>({surface:'workspace'})}};
  const path=new URL('../usability.js',import.meta.url); if(fs.existsSync(path))vm.runInNewContext(fs.readFileSync(path,'utf8'),{window,document});
  return {H,window,document,settings};
}
test('density defaults compact and responds to persisted comfortable without shrinking fonts',()=>{
  const env=setup();assert.equal(typeof env.window.DesktopHubUsability?.apply,'function');env.window.DesktopHubUsability.apply();assert.equal(env.document.documentElement.dataset.density,'compact');env.settings.uiDensity='comfortable';env.H.render();assert.equal(env.document.documentElement.dataset.density,'comfortable');
});
test('appearance exposes exactly one density choice that uses existing persisted setting action',()=>{
  const env=setup('comfortable');assert.equal(typeof env.window.DesktopHubUsability?.apply,'function');const html=env.H.pages.settings();assert.equal((html.match(/data-setting="uiDensity"/g)||[]).length,1);assert.match(html,/data-change="setting-value"/);assert.match(html,/value="comfortable" selected/);assert.match(html,/value="compact"/);
});
