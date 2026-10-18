/* Desktop Hub M0. All business state is synthetic; no OS or network APIs. */
(() => {
  'use strict';
  const t = window.DesktopHubI18n?.text || ((value, ...args) => Array.isArray(value) ? value.reduce((out, part, i) => out + part + (i < args.length ? args[i] : ''), '') : value);
  const STORAGE_KEY = 'desktop-hub-m0-v07';
  const paths = {
    grid:'<rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/>',
    scenes:'<path d="m12 3 8 5-8 5-8-5 8-5Z"/><path d="m4 12 8 5 8-5M4 16l8 5 8-5"/>',
    mini:'<rect x="4" y="3" width="16" height="18" rx="3"/><path d="M9 3v18"/>',
    dock:'<rect x="4" y="3" width="16" height="18" rx="3"/><path d="M9 3v18m4-13 3 4-3 4"/>',
    pen:'<path d="m14 4 6 6M4 20l5-1L21 7a2.1 2.1 0 0 0-4-4L5 15l-1 5Z"/>',
    game:'<path d="M7 8h10c3 0 4 4 4 9 0 3-3 3-5 0H8c-2 3-5 3-5 0 0-5 1-9 4-9Z"/><path d="M7 11v5m-2.5-2.5h5M16 12h.01M18 15h.01"/>',
    coffee:'<path d="M4 9h12v6a5 5 0 0 1-5 5H9a5 5 0 0 1-5-5V9ZM16 9h2a3 3 0 0 1 0 6h-2M7 3v3m5-3v3M3 21h16"/>',
    folder:'<path d="M3 7V5a2 2 0 0 1 2-2h5l3 4h6a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7Z"/>',
    file:'<path d="M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9l-6-6Z"/><path d="M14 3v6h6"/>',
    document:'<path d="M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9l-6-6Z"/><path d="M14 3v6h6M8 13h8M8 17h6"/>',
    image:'<rect x="3" y="3" width="18" height="18" rx="3"/><circle cx="8" cy="8" r="1.5"/><path d="m3 17 5-5 4 4 4-6 5 7"/>',
    table:'<rect x="3" y="3" width="18" height="18" rx="3"/><path d="M3 9h18M3 15h18M9 3v18"/>',
    archive:'<rect x="4" y="3" width="16" height="18" rx="2"/><path d="M11 3v3h2v3h-2v3h2v3m-3 2h4v3h-4z"/>',
    tag:'<path d="M3 3h8l10 10-8 8L3 11V3Z"/><circle cx="7.5" cy="7.5" r="1"/>',
    star:'<path d="m12 3 2.8 5.7 6.2.9-4.5 4.4 1.1 6.2-5.6-3-5.6 3 1.1-6.2L3 9.6l6.2-.9L12 3Z"/>',
    search:'<circle cx="10.5" cy="10.5" r="7"/><path d="m16 16 5 5"/>',
    settings:'<path d="m10 3-.6 2.6-2 .9L5 5.7 3 9l1.9 1.8v2.4L3 15l2 3.3 2.4-.8 2 .9L10 21h4l.6-2.6 2-.9 2.4.8 2-3.3-1.9-1.8v-2.4L21 9l-2-3.3-2.4.8-2-.9L14 3h-4Z"/><circle cx="12" cy="12" r="3"/>',
    sun:'<circle cx="12" cy="12" r="4"/><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M5 19l1.5-1.5m11-11L19 5"/>',
    moon:'<path d="M20.5 14a9 9 0 0 1-10.5-10.5A9 9 0 1 0 20.5 14Z"/>',
    monitor:'<rect x="3" y="3" width="18" height="13" rx="2"/><path d="M8 21h8m-4-5v5"/>',
    play:'<path d="m8 4 12 8-12 8V4Z"/>',
    plus:'<path d="M12 5v14M5 12h14"/>',
    close:'<path d="m6 6 12 12M6 18 18 6"/>',
    check:'<path d="m5 12 4 4L19 6"/>',
    edit:'<path d="m15 5 4 4M4 20l5-1L20 8a2.8 2.8 0 0 0-4-4L5 15l-1 5Z"/>',
    pin:'<path d="m8 3 8 0-1 6 4 5H5l4-5-1-6Zm4 11v7"/>',
    grip:'<path d="M9 5h.01M15 5h.01M9 12h.01M15 12h.01M9 19h.01M15 19h.01"/>',
    trash:'<path d="M3 6h18M9 6V3h6v3M5 6l1 15h12l1-15M10 10v7m4-7v7"/>',
    'arrow-left':'<path d="m12 5-7 7 7 7M5 12h15"/>',
    'arrow-right':'<path d="m12 5 7 7-7 7M4 12h15"/>',
    'arrow-up':'<path d="m5 12 7-7 7 7m-7-7v15"/>',
    'arrow-down':'<path d="m5 12 7 7 7-7m-7-13v13"/>',
    'chevron-down':'<path d="m6 9 6 6 6-6"/>',
    more:'<path d="M5 12h.01M12 12h.01M19 12h.01"/>',
    alert:'<path d="m12 3 10 18H2L12 3Zm0 6v5m0 3h.01"/>',
    repair:'<path d="m14 6 4-4a6 6 0 0 0-7 8L3 18a2 2 0 0 0 3 3l8-8a6 6 0 0 0 8-7l-4 4-4-4Z"/>',
    external:'<path d="M14 3h7v7m0-7L10 14M10 3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-5"/>',
    app:'<rect x="3" y="3" width="18" height="18" rx="3"/><path d="M3 8h18M7 5.5h.01M10 5.5h.01"/>',
    globe:'<circle cx="12" cy="12" r="9"/><ellipse cx="12" cy="12" rx="4" ry="9"/><path d="M3 12h18"/>',
    code:'<path d="m7 7-5 5 5 5m10-10 5 5-5 5M14 3l-4 18"/>',
    clock:'<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
    list:'<path d="M9 5h12M9 12h12M9 19h12M3 5h.01M3 12h.01M3 19h.01"/>',
    keyboard:'<rect x="2" y="5" width="20" height="14" rx="3"/><path d="M6 9h.01M10 9h.01M14 9h.01M18 9h.01M6 13h.01M10 13h.01M14 13h.01M18 13h.01M8 16h8"/>',
    shield:'<path d="m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6l8-3Z"/><path d="m8 12 3 3 5-6"/>',
    scan:'<path d="M8 3H3v5m13-5h5v5M3 16v5h5m13-5v5h-5M3 12h18"/>',
    refresh:'<path d="M20 8a8 8 0 0 0-14-3L3 8m0-5v5h5M4 16a8 8 0 0 0 14 3l3-3m-5 0h5v5"/>',
    logout:'<path d="M9 3H4v18h5m5-14 5 5-5 5M8 12h11"/>'
  };
  function seedFiles() {
    const data = [
      ['f1',t('设计参考图.png'),'png',['course','design','oct'],false,'Active',['course','design','oct']],
      ['f2',t('Desktop Hub 界面草案.pdf'),'pdf',['design','oct'],true,'Active',[]],
      ['f3',t('课程报告.docx'),'docx',['course','oct'],false,'Active',[]],
      ['f4',t('创作笔记.md'),'md',['creative'],true,'Active',[]],
      ['f5',t('十月素材清单.xlsx'),'xlsx',['design','oct'],false,'Active',[]],
      ['f6',t('设计素材'),'folder',['design'],false,'Active',[]],
      ['f7',t('灵感收藏.zip'),'zip',['design'],true,'Active',[]],
      ['f8',t('周末阅读.pdf'),'pdf',['relax'],false,'Active',[]],
      ['f9',t('旧版提案.pdf'),'pdf',['design'],true,'Missing',[]],
      ['f10',t('项目说明.docx'),'docx',['course'],false,'PendingRepair',[]],
      ['f11',t('参考素材.zip'),'zip',['design'],true,'SourceUnavailable',[]],
      ['f12',t('游戏清单.md'),'md',['game'],false,'Active',[]]
    ];
    return data.map(([id,name,type,tags,favorite,status,suggestions])=>({id,name,type,tags:id==='f1'?[]:tags,favorite,status,suggestions,created:'2026-10-08',source:id==='f11'?'custom':'desktop',path:`C:\\Users\\Demo\\Desktop\\${name}`}));
  }
  function seed() {
    const action=(id,name,target,mode='sequential',dependsOn='',demoResult='success',type='program')=>({id,name,type,target,mode,dependsOn,delay:0,timeout:10,repeat:type==='program'?'skip':type==='script'?'once':'always',demoResult});
    return {
      version:7, onboardingComplete:false, indexConfirmed:false, files:[],
      tags:[{id:'course',name:t('课程作业'),kind:'主题'},{id:'design',name:t('设计素材'),kind:'主题'},{id:'oct',name:t('2026年10月'),kind:'时期'},{id:'creative',name:t('创作'),kind:'自定义'},{id:'game',name:t('游戏'),kind:'主题'},{id:'relax',name:t('休闲'),kind:'自定义'}],
      scenes:[
        {id:'creation',name:t('创作'),icon:'pen',description:t('把工具准备好，留心力给创作。'),pinned:true,actions:[action('c1',t('加速器'),'C:\\Demo\\Accelerator.exe'),action('c2','Clash','C:\\Demo\\Clash.exe','process','c1','skip'),action('c3','ChatGPT','https://chatgpt.com','sequential','c2','success','web'),action('c4','Edge','C:\\Demo\\Edge.exe','parallel','','fail'),action('c5','DSH','C:\\Demo\\DSH.exe','sequential','c4')]},
        {id:'game',name:t('游戏'),icon:'game',description:t('准备游戏环境，开启专注时段。'),pinned:true,actions:[action('g1',t('加速器'),'C:\\Demo\\Accelerator.exe'),action('g2',t('游戏启动器'),'C:\\Demo\\Game.exe','process','g1')]},
        {id:'relax',name:t('休闲'),icon:'coffee',description:t('音乐、阅读，还有一点留白。'),pinned:true,actions:[action('r1',t('音乐播放器'),'C:\\Demo\\Music.exe'),action('r2',t('阅读清单'),'C:\\Demo\\reading.pdf','parallel','','success','file'),action('r3',t('随手记'),'C:\\Demo\\notes','parallel','','success','folder')]}
      ],
      sources:[{id:'desktop',name:t('个人桌面'),path:'C:\\Users\\Demo\\Desktop',recursive:false,enabled:true},{id:'public',name:t('公共桌面'),path:'C:\\Users\\Public\\Desktop',recursive:false,enabled:false}],
      exclusions:[t('示例全屏游戏')],
      settings:{theme:'system',reducedMotion:false,transparency:true,miniPosition:'left',miniSize:'normal',miniMode:'icons',dockPosition:'left',dockSize:'normal',hoverEnabled:true,hoverDelay:500,allowForeground:true,dockShortcut:'Alt+D',workspaceShortcut:'Alt+W',gameProtection:true,autoStart:false,tray:true,hideDesktopIcons:false,restorePolicy:''}
    };
  }
  let state=seed();
  try { const cached=JSON.parse(localStorage.getItem(STORAGE_KEY)); if(cached?.version===7) state=cached; } catch {}
  const H=window.Hub={state,ui:{surface:'workspace',page:'home',libraryView:'list',fileQuery:'',fileTag:'all',missingFilter:'all',foreground:'edge',runningSceneId:null,run:null},pages:{},actions:{},seedFiles,
    escape:s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])),
    icon:(name,size=20)=>`<svg width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.65" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${paths[name]||paths.file}</svg>`,
    save(){try{localStorage.setItem(STORAGE_KEY,JSON.stringify(H.state));}catch{H.toast(t('浏览器未允许存储，本次修改会保留到页面关闭。'));}},
    applyTheme(){let theme=H.state.settings.theme;if(theme==='system')theme=matchMedia('(prefers-color-scheme: dark)').matches?'dark':'light';document.documentElement.dataset.theme=theme;document.documentElement.dataset.motion=H.state.settings.reducedMotion?'reduced':'normal';document.documentElement.dataset.transparency=H.state.settings.transparency?'on':'off';},
    navigate(page){if(H.ui.page==='editor'&&H.ui.sceneDraftDirty&&page!=='editor'){H.showModal(t('离开场景编辑器？'),t('<p>本次草稿尚未保存。可以继续编辑，或放弃修改后离开。</p>'),t`<button class="button" data-action="close-modal">继续编辑</button><button class="button primary" data-action="navigate-discard" data-page="${H.escape(page)}">放弃修改并离开</button>`);return;}const changed=H.ui.page!==page||H.ui.surface!=='workspace';H.ui.page=page;H.ui.surface='workspace';H.closeModal();H.render();if(changed)setTimeout(()=>document.querySelector('.page-title')?.focus({preventScroll:true}),0);},
    setSurface(surface){cancelHover();H.ui.surface=surface;H.closeModal();H.render();if(surface==='workspace')setTimeout(()=>document.querySelector('.page-title')?.focus({preventScroll:true}),0);},
    toast(message,type='info'){const root=document.getElementById('toast-root');const item=document.createElement('div');item.className=`toast ${type}`;item.innerHTML=H.icon(type==='error'?'alert':'check',18)+`<span>${H.escape(message)}</span>`;root.append(item);setTimeout(()=>item.remove(),4500);},
    showModal(title,body,footer='',options={}){const updating=!!H.ui.modal;H.ui.modal={locked:!!options.locked,opener:H.ui.modal?.opener||document.activeElement};document.getElementById('app').inert=true;document.getElementById('modal-root').innerHTML=`<div class="modal-backdrop ${updating?'is-updating':''}"><section class="modal ${options.wide?'wide':''}" role="dialog" aria-modal="true" aria-labelledby="dialog-title"><div class="modal-heading"><h2 id="dialog-title">${H.escape(title)}</h2>${options.locked?'':t`<button class="icon-button" data-action="close-modal" aria-label="关闭对话框">${H.icon('close')}</button>`}</div><div class="modal-body">${body}</div>${footer?`<div class="modal-footer">${footer}</div>`:''}</section></div>`;setTimeout(()=>(document.querySelector('.modal [autofocus]')||document.querySelector('.modal input:not([type=checkbox]):not([type=radio]),.modal select')||document.querySelector('.modal-footer .primary')||document.querySelector('.modal button'))?.focus({preventScroll:true}),0);},
    closeModal(){const opener=H.ui.modal?.opener;H.ui.modal=null;document.getElementById('app').inert=false;document.getElementById('modal-root').innerHTML='';if(opener?.isConnected)opener.focus({preventScroll:true});else document.querySelector('.page-title')?.focus({preventScroll:true});},
    resetDemo(){H.state=seed();H.ui={surface:'workspace',page:'home',libraryView:'list',foreground:'edge',run:null};H.save();H.applyTheme();H.render();H.openOnboarding?.();}
  };
  const modules=[['scenes','场景中心','scenes','把常用工具，组合成一次启动。'],['library','文件资源库','folder','文件留在原处，整理发生在这里。'],['tags','标签中心','tag','按主题与时间，找回关联。'],['favorites','收藏','star','为经常需要的文件留个位置。'],['search','快速搜索','search','从文件名与标签找到所需。'],['settings','设置','settings','让 Desktop Hub 配合你的习惯。']];
  H.pages.home=()=>t`<div class="home-intro"><div><p class="eyebrow">YOUR SPACE, AT EASE</p><h1 class="page-title" tabindex="-1">你的工作空间</h1><p class="subtitle">从这里开始，桌面保持安静。</p></div><span class="local-status"><i></i>本地 · 离线</span></div><div class="launchpad-grid">${modules.map(([page,title,icon,description],i)=>`<button class="launch-card" data-action="navigate" data-page="${page}"><span class="launch-icon">${H.icon(icon,30)}</span><span class="launch-text"><strong>${t(title)}</strong><span>${t(description)}</span></span><span class="launch-arrow">${H.icon('arrow-right',18)}</span><span class="module-index">0${i+1}</span></button>`).join('')}</div><div class="quick-heading"><h2>固定场景</h2><button class="text-button" data-action="navigate" data-page="scenes">管理场景 ${H.icon('arrow-right',15)}</button></div><div class="home-scenes">${H.state.scenes.filter(s=>s.pinned).map(s=>t`<button class="home-scene" data-action="run-scene" data-id="${s.id}"><span class="scene-icon">${H.icon(s.icon,21)}</span><span><strong>${H.escape(s.name)}</strong><small>${s.actions.length} 个动作</small></span><span class="scene-play">${H.icon('play',17)}</span></button>`).join('')||t`<button class="button" data-action="navigate" data-page="scenes">${H.icon('plus')} 添加第一个场景</button>`}</div><div class="home-tip">${H.icon('keyboard',16)}<span><kbd>${H.escape(H.state.settings.workspaceShortcut)}</kbd> 唤出工作空间 <span class="tip-divider">/</span> <kbd>Esc</kbd> 回到桌面</span></div>`;
  function desktop(){return t`<div class="desktop-stage"><div class="desktop-label">${H.icon('monitor',17)}<span>桌面预览</span></div>${H.state.settings.hideDesktopIcons?'':t`<div class="desktop-icons" aria-label="模拟桌面图标"><button data-action="demo-desktop-file">${H.icon('folder',26)}<span>设计素材</span></button><button data-action="demo-desktop-file">${H.icon('document',26)}<span>课程报告</span></button></div>`}<section class="mock-edge ${H.ui.foreground==='game'?'mock-game':''}" aria-label="模拟前台应用"><div class="mock-titlebar"><span class="mock-tab">${H.icon(H.ui.foreground==='game'?'game':'globe',15)} ${H.ui.foreground==='game'?t('示例全屏游戏'):H.ui.foreground.startsWith('excluded:')?H.escape(H.state.exclusions[Number(H.ui.foreground.split(':')[1])]||t('模拟排除程序')):t('Edge · 新建标签页')} ${H.icon('close',12)}</span><span>−　□　×</span></div>${H.ui.foreground==='game'?t`<div class="edge-content"><span class="mock-app-icon">${H.icon('game',44)}</span><h2>模拟全屏游戏</h2><p>保护开启时，屏幕边缘不会唤出 Dock。</p></div>`:t`<div class="mock-address">${H.icon('shield',14)} edge://newtab <span>${H.icon('more',18)}</span></div><div class="edge-content"><span class="mock-app-icon">${H.icon('globe',40)}</span><h2>留一点空间，给专注。</h2><p>这是模拟的 ${H.ui.foreground.startsWith('excluded:')?t('排除程序'):'Edge'}前台窗口。</p><button class="button" data-action="open-workspace">${H.icon('grid',18)} 打开工作空间</button><small>按 Esc 可从工作空间返回此处</small></div>`}</section><div class="desktop-caption">${window.DesktopHubI18n ? t('桌面工作台') : 'Desktop Hub'} <span>Quiet Desktop</span></div></div>`;}
  function mini(){const s=H.state.settings;return t`<aside class="mini-bar position-${s.miniPosition} size-${s.miniSize} mode-${s.miniMode||'icons'}" aria-label="Mini 固定场景"><button class="mini-brand" data-action="open-workspace" aria-label="打开工作空间" title="打开工作空间">${H.icon('grid',22)}</button><span class="mini-separator"></span>${H.state.scenes.filter(x=>x.pinned).map(x=>t`<button class="mini-item ${H.ui.runningSceneId===x.id&&!H.ui.run?.finished?'running':''}" data-action="run-scene" data-id="${x.id}" aria-label="模拟启动${H.escape(x.name)}" data-tooltip="${H.escape(x.name)} · 模拟启动">${H.icon(x.icon,22)}${s.miniMode==='labels'?`<span class="mini-item-label">${H.escape(x.name)}</span>`:''}</button>`).join('')||t`<button class="mini-item" data-action="navigate" data-page="scenes" aria-label="添加第一个场景">${H.icon('plus',22)}</button>`}<span class="mini-separator"></span><button class="mini-item" data-action="open-dock" aria-label="展开 Dock" data-tooltip="展开 Dock">${H.icon('dock',21)}</button></aside>`;}
  const runLabels={get pending() { return t('模拟 · 待执行'); },get running() { return t('模拟 · 执行中'); },get success() { return t('模拟 · 成功'); },get skip() { return t('模拟 · 跳过'); },get fail() { return t('模拟 · 失败'); }};
  function dock(){const s=H.state.settings,run=H.ui.run,progress=run?run.items.filter(i=>!['pending','running'].includes(i.status)).length:0;return t`<aside class="dock-panel position-${s.dockPosition} size-${s.dockSize}" aria-label="Dock 场景启动台"><div class="dock-heading"><span>${H.icon('grid',21)}<strong>${window.DesktopHubI18n ? t('桌面工作台') : 'Desktop Hub'}</strong></span><button class="icon-button" data-action="close-surface" aria-label="收起 Dock">${H.icon('close',18)}</button></div><div class="dock-section-title">固定场景 <span>模拟启动</span></div><div class="dock-scenes">${H.state.scenes.filter(x=>x.pinned).map(x=>`<button class="dock-scene ${run?.sceneId===x.id?'selected':''}" data-action="run-scene" data-id="${x.id}"><span class="scene-icon">${H.icon(x.icon,23)}</span><span><strong>${H.escape(x.name)}</strong><small>${H.escape(x.actions.map(a=>a.name).join(' · ')||t('尚未添加动作'))}</small></span>${H.icon('play',17)}</button>`).join('')||t('<p class="muted">先添加一个场景。</p>')}</div><button class="text-button dock-more" data-action="navigate" data-page="scenes">查看全部场景 ${H.icon('arrow-right',16)}</button>${run?t`<div class="dock-run"><div class="run-heading"><strong>${H.escape(H.state.scenes.find(x=>x.id===run.sceneId)?.name||t('场景'))} · 模拟${run.finished?t('完成'):t('执行中')}</strong><span>${progress}/${run.items.length}</span></div><div class="progress-track"><span style="width:${run.items.length?progress/run.items.length*100:0}%"></span></div><div class="run-items">${run.items.map(i=>`<div class="run-item" title="${H.escape(i.detail||t('模拟反馈'))}"><span class="status-mark ${i.status}">${H.icon(i.status==='success'?'check':i.status==='fail'?'close':i.status==='skip'?'arrow-right':i.status==='running'?'refresh':'clock',14)}</span><span>${H.escape(i.name)}</span><small class="${i.status}">${runLabels[i.status]}</small></div>`).join('')}</div>${run.finished?t`<p class="run-note">${run.items.some(i=>i.status==='fail')?H.escape(run.items.filter(i=>i.status==='fail').map(i=>`${i.name}：${i.detail||t('模拟失败')}`).join('；')):t('模拟执行已结束。没有打开真实应用。')}</p><button class="text-button" data-action="edit-scene" data-id="${run.sceneId}">编辑场景 ${H.icon('arrow-right',15)}</button>`:''}</div>`:''}<button class="dock-workspace" data-action="open-workspace">${H.icon('grid',21)}<span>打开工作空间<small>${H.escape(s.workspaceShortcut)}</small></span>${H.icon('arrow-right',17)}</button></aside>`;}
  function workspace(){const title=modules.find(m=>m[0]===H.ui.page)?.[1]||t('场景编辑器');return t`<main class="workspace" aria-label="Desktop Hub 工作空间"><header class="workspace-header"><button class="brand" data-action="navigate" data-page="home" aria-label="返回启动台"><span class="brand-icon">${H.icon('grid',23)}</span><span>${window.DesktopHubI18n ? t('桌面工作台') : 'Desktop Hub'}<small>工作空间</small></span></button><button class="global-search" data-action="navigate" data-page="search">${H.icon('search',18)}<span>搜索文件、标签</span><kbd>Ctrl K</kbd></button><div class="header-actions"><button class="icon-button" data-action="cycle-theme" aria-label="切换主题" title="切换主题（当前：${{system:t('跟随系统'),light:t('浅色'),dark:t('深色')}[H.state.settings.theme]}）">${H.icon(document.documentElement.dataset.theme==='dark'?'moon':'sun',19)}</button><button class="icon-button" data-action="navigate" data-page="settings" aria-label="设置">${H.icon('settings',19)}</button><span class="header-divider"></span><button class="icon-button" data-action="close-surface" aria-label="关闭 Workspace">${H.icon('close',21)}</button></div></header>${H.ui.page!=='home'?t`<div class="workspace-subnav"><button class="text-button" data-action="navigate" data-page="home">${H.icon('arrow-left',16)} 启动台</button><span class="breadcrumb-divider">/</span><span>${H.escape(t(title))}</span><nav aria-label="工作空间模块">${modules.map(([page,label])=>`<button class="subnav-link ${H.ui.page===page?'active':''}" data-action="navigate" data-page="${page}" ${H.ui.page===page?'aria-current="page"':''}>${t(label)}</button>`).join('')}</nav></div>`:''}<div class="workspace-content ${H.ui.page==='home'?'home-content':''}" id="page-content">${(H.pages[H.ui.page]||H.pages.home)()}</div><footer class="workspace-footer"><span><i></i> M0 交互原型 <span class="footer-separator">·</span> 本地模拟数据</span><span>${H.state.files.length} 项虚拟索引 <span class="footer-separator">·</span> <kbd>Esc</kbd> 返回桌面</span></footer></main>`;}
  function demoBar(){return t`<div class="demo-bar" aria-label="原型演示控制"><span class="demo-label">M0 <span>原型演示</span></span><div class="segmented">${[['mini','Mini'],['dock','Dock'],['workspace','Workspace']].map(([surface,title])=>`<button data-action="demo-surface" data-surface="${surface}" class="${H.ui.surface===surface?'active':''}">${title}</button>`).join('')}</div><span class="demo-divider"></span><button class="demo-shortcut" data-action="open-dock" title="模拟 Dock 独立快捷键">Dock <kbd>${H.escape(H.state.settings.dockShortcut)}</kbd></button><button class="demo-shortcut" data-action="open-workspace" title="模拟 Workspace 独立快捷键">Workspace <kbd>${H.escape(H.state.settings.workspaceShortcut)}</kbd></button><div class="demo-right"><select aria-label="模拟前台应用" data-change="foreground"><option value="edge" ${H.ui.foreground==='edge'?'selected':''}>前台：Edge</option><option value="game" ${H.ui.foreground==='game'?'selected':''}>前台：全屏游戏</option>${H.state.exclusions.map((name,index)=>t`<option value="excluded:${index}" ${H.ui.foreground===`excluded:${index}`?'selected':''}>排除：${H.escape(name)}</option>`).join('')}</select><button class="icon-button" data-action="cycle-theme" aria-label="演示切换深浅主题">${H.icon(document.documentElement.dataset.theme==='dark'?'moon':'sun',18)}</button><button class="icon-button" data-action="replay-onboarding" aria-label="重看首次引导" title="重看首次引导">${H.icon('scan',18)}</button></div></div>`;}
  let renderedView = null, renderRevision = 0;
  H.render = () => {
    const view = `${H.ui.surface}:${H.ui.page}`, sameView = view === renderedView, revision = ++renderRevision;
    const active = document.activeElement;
    const selection = typeof active?.selectionStart === 'number' ? [active.selectionStart, active.selectionEnd] : null;
    const focusData = Object.fromEntries(['action','change','setting','id','resourceId','name','page','view','section','index'].filter(key => active?.dataset?.[key] !== undefined).map(key => [key, active.dataset[key]]));
    const scroll = sameView ? ['.workspace-content', '.dock-panel'].map(selector => {
      const element = document.querySelector(selector);
      return { selector, top: element?.scrollTop || 0, left: element?.scrollLeft || 0 };
    }) : [];
    const expanded = sameView ? [...document.querySelectorAll('details[id]')].map(element => ({ id: element.id, open: element.open })) : [];
    H.applyTheme();
    document.getElementById('app').innerHTML = desktop() + demoBar() + `<div class="edge-trigger position-${H.state.settings.dockPosition}" id="edge-trigger" aria-hidden="true"><span></span></div>` + (H.ui.surface === 'workspace' ? workspace() : H.ui.surface === 'dock' ? dock() : mini());
    renderedView = view;
    const restore = () => {
      if (revision !== renderRevision || view !== `${H.ui.surface}:${H.ui.page}`) return;
      expanded.forEach(item => { const element = document.getElementById(item.id); if (element) element.open = item.open; });
      scroll.forEach(item => { const element = document.querySelector(item.selector); if (element) { element.scrollTop = item.top; element.scrollLeft = item.left; } });
    };
    H.restoreViewport = restore;
    restore();
    if (sameView && !H.ui.modal) {
      const next = active?.id ? document.getElementById(active.id) : Object.keys(focusData).length ? [...document.querySelectorAll('[data-action],[data-change],[data-setting]')].find(element => Object.entries(focusData).every(([key, value]) => element.dataset[key] === value)) : null;
      if (next) { next.focus({ preventScroll: true }); if (selection && next.setSelectionRange) try { next.setSelectionRange(...selection); } catch {} }
    }
    document.querySelectorAll('.page-title').forEach(element => element.tabIndex = -1);
    const edge = document.getElementById('edge-trigger');
    edge.addEventListener('mouseenter', startHover); edge.addEventListener('mouseleave', cancelHover);
  };
  let hoverTimer;
  function cancelHover(){clearTimeout(hoverTimer);}
  function startHover(){cancelHover();const s=H.state.settings;if(H.ui.surface==='workspace'||!s.hoverEnabled)return;if(H.ui.foreground==='game'&&s.gameProtection){H.toast(t('模拟：全屏游戏保护已阻止边缘唤出。'));return;}if(H.ui.foreground.startsWith('excluded:')&&H.state.exclusions[Number(H.ui.foreground.split(':')[1])]){H.toast(t('模拟：排除程序保护已阻止边缘唤出。'));return;}if(H.ui.foreground!=='game'&&!s.allowForeground)return;hoverTimer=setTimeout(()=>{const current=H.state.settings;if(H.ui.surface==='workspace'||!current.hoverEnabled||(H.ui.foreground==='game'&&current.gameProtection)||H.ui.foreground.startsWith('excluded:')||(H.ui.foreground!=='game'&&!current.allowForeground))return;H.setSurface('dock');},Number(s.hoverDelay)||500);}
  Object.assign(H.actions,{
    navigate:el=>H.navigate(el.dataset.page),
    'demo-surface':el=>H.setSurface(el.dataset.surface),
    'open-workspace':()=>H.setSurface('workspace'),
    'open-dock':()=>H.setSurface('dock'),
    'close-surface':()=>H.setSurface('mini'),
    'close-modal':()=>{if(!H.ui.modal?.locked)H.closeModal();},
    'cycle-theme':()=>{const modes=['system','dark','light'];H.state.settings.theme=modes[(modes.indexOf(H.state.settings.theme)+1)%3];H.save();H.applyTheme();H.render();H.toast(t`主题：${{system:t('跟随系统'),dark:t('深色'),light:t('浅色')}[H.state.settings.theme]}`);},
    foreground:el=>{H.ui.foreground=el.value;H.setSurface('mini');},
    'replay-onboarding':()=>H.openOnboarding?.(),
    'navigate-discard':el=>{H.ui.sceneDraftDirty=false;H.ui.sceneDraft=null;H.closeModal();H.navigate(el.dataset.page);},
    'demo-desktop-file':()=>H.toast(t('模拟桌面图标：请在资源库浏览，未读取磁盘。')),
    'reset-demo':()=>H.showModal(t('重置演示数据'),t`<p>恢复示例场景、文件与设置，并重新开始首次引导。</p><p class="muted">只清除本原型的浏览器存储。</p>`,t`<button class="button" data-action="close-modal">取消</button><button class="button primary" data-action="confirm-reset">重置演示数据</button>`),
    'confirm-reset':()=>H.resetDemo()
  });
  function invoke(el,event){const name=el.dataset.action||el.dataset.change;const fn=H.actions[name];if(fn){try{const promise=fn(el,event);promise?.catch?.(e=>{console.error(e);H.toast(t('原型操作未完成，请重试。'),'error');});}catch(e){console.error(e);H.toast(t('原型操作未完成，请重试。'),'error');}}}
  document.addEventListener('click',event=>{const el=event.target.closest('[data-action]');if(el&&!el.disabled)invoke(el,event);if(event.target.classList.contains('modal-backdrop')&&!H.ui.modal?.locked)H.closeModal();});
  for(const type of ['input','change'])document.addEventListener(type,event=>{const el=event.target.closest('[data-change]');if(el){if(type==='input'&&['SELECT'].includes(el.tagName))return;if(type==='input'&&['checkbox','radio'].includes(el.type))return;if(type==='change'&&el.tagName==='INPUT'&&!['checkbox','radio','color','range'].includes(el.type))return;invoke(el,event);}});
  function shortcutMatch(event,shortcut){const parts=shortcut.toLowerCase().replace(/\s/g,'').split('+');return event.altKey===parts.includes('alt')&&event.ctrlKey===parts.includes('ctrl')&&event.shiftKey===parts.includes('shift')&&event.metaKey===parts.includes('meta')&&(event.key===' '?'space':event.key.toLowerCase())===parts.at(-1);}
  document.addEventListener('keydown',event=>{
    if(H.ui.modal){if(event.key==='Escape'){event.preventDefault();if(!H.ui.modal.locked)H.closeModal();return;}if(event.key==='Tab'){const list=[...document.querySelectorAll('.modal button:not([disabled]),.modal input:not([disabled]),.modal select:not([disabled]),.modal textarea:not([disabled]),.modal [tabindex="0"]')].filter(e=>e.offsetParent!==null);if(list.length){const first=list[0],last=list.at(-1);if(event.shiftKey&&document.activeElement===first){event.preventDefault();last.focus();}else if(!event.shiftKey&&document.activeElement===last){event.preventDefault();first.focus();}}}return;}
    if(event.key==='Escape'&&H.ui.surface==='workspace'){event.preventDefault();H.setSurface('mini');H.toast(t`模拟：已返回之前的${H.ui.foreground==='game'?t('全屏游戏'):'Edge'}窗口。`);return;}
    if(event.key==='Escape'&&H.ui.surface==='dock'){event.preventDefault();H.setSurface('mini');return;}
    if(shortcutMatch(event,H.state.settings.workspaceShortcut)){event.preventDefault();H.setSurface(H.ui.surface==='workspace'?'mini':'workspace');return;}
    if(shortcutMatch(event,H.state.settings.dockShortcut)){event.preventDefault();H.setSurface(H.ui.surface==='dock'?'mini':'dock');return;}
    if(event.ctrlKey&&event.key.toLowerCase()==='k'){event.preventDefault();H.navigate('search');setTimeout(()=>document.querySelector('#search-query,input[type=search]')?.focus(),0);}
  });
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change',()=>{if(H.state.settings.theme==='system'){H.applyTheme();H.render();}});
  document.addEventListener('DOMContentLoaded',()=>{H.render();if(!H.state.onboardingComplete)H.openOnboarding?.();});
})();
