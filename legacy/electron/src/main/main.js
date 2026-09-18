'use strict';

const path = require('path');
const fs = require('fs');
const { pathToFileURL } = require('url');
const {
  app,
  BrowserWindow,
  ipcMain,
  globalShortcut,
  protocol,
  net,
  screen,
  nativeImage,
} = require('electron');

const { Store } = require('./store');
const { grabDisplay, cropByCssRect, displayForPoint } = require('./capture');
const wininfo = require('./wininfo');

const PANEL_W = 340;
// 500 是让「Todo 露出 4 行 + Wantdo 露出 1 整行」的实测尺寸，
// 改了 panel.css 里的 flex 比例或卡片高度就要重新算（算法见设计稿）
const PANEL_H = 500;
const PANEL_MARGIN = 20;

const HOTKEY_CAPTURE = 'Control+Shift+A';
const HOTKEY_TOGGLE = 'Control+Shift+H';
const HOTKEY_QUIT = 'Control+Shift+Q';

// 必须在 app ready 之前注册自定义协议
const argv = process.argv.slice(1);
const FLAG_DEMO = argv.includes('--demo') || !!process.env.REMEBER_DEMO;

// --demo：用项目内的数据目录 + 灌样例，方便一眼看到有内容的样子，不碰你的真实数据
// 环境变量优先级更高，方便测试
if (process.env.REMEBER_USER_DATA) {
  try {
    fs.mkdirSync(process.env.REMEBER_USER_DATA, { recursive: true });
    app.setPath('userData', process.env.REMEBER_USER_DATA);
  } catch (_) {}
} else if (FLAG_DEMO) {
  const dir = path.join(__dirname, '..', '..', '.userdata');
  try {
    fs.mkdirSync(dir, { recursive: true });
    app.setPath('userData', dir);
  } catch (_) {}
}

const DEBUG = !!process.env.REMEBER_DEBUG;
const log = (...a) => {
  if (DEBUG) console.log('[remeber]', ...a);
};
log('argv =', JSON.stringify(process.argv), '| FLAG_DEMO =', FLAG_DEMO);

protocol.registerSchemesAsPrivileged([
  {
    scheme: 'appimg',
    privileges: { standard: true, secure: true, supportFetchAPI: true, stream: true },
  },
]);

/** @type {Store} */
let store;
/** @type {BrowserWindow|null} */
let panelWin = null;
/** @type {BrowserWindow|null} */
let overlayWin = null;
/** @type {BrowserWindow|null} */
let chooserWin = null;
/** @type {BrowserWindow|null} */
let detailWin = null;
/** 悬停预览用的无焦点小窗 */
let previewWin = null;
/** 完成记录窗：写心得 / 回看心得与统计 */
let recordWin = null;

/** 截图流程中的临时状态 */
let pending = null;
let tickTimer = null;
/** 当前这次截图「等渲染层画完就显示框选层」的回调 */
let overlayShow = null;

// ---------------------------------------------------------------- 面板窗口

/**
 * 重新确认面板置顶。
 *
 * 为什么要反复调：Windows 上顶层窗口共用一个 z-order 带，我们每次弹出
 * 框选层 / 选择器 / 详情 / 完成记录 / 悬停预览，都会在这个带里插一个新窗口；
 * 这些窗口关掉之后，面板不一定自动回到最上面（用户反馈过「程序不置顶了」）。
 * 这个调用很便宜，所以宁可在几个关键时机都补一刀，也不要让它掉下去。
 */
function keepPanelOnTop() {
  if (!panelWin || panelWin.isDestroyed()) return;
  try {
    panelWin.setAlwaysOnTop(true, 'floating');
  } catch (_) {}
}

function createPanel() {
  const { workArea } = screen.getPrimaryDisplay();
  const x = Math.max(0, workArea.x + workArea.width - PANEL_W - PANEL_MARGIN);
  const y = workArea.y + PANEL_MARGIN;

  panelWin = new BrowserWindow({
    width: PANEL_W,
    height: PANEL_H,
    x,
    y,
    minWidth: 280,
    minHeight: 160,
    frame: false,
    transparent: true,
    backgroundColor: '#00000000',
    hasShadow: false,
    alwaysOnTop: true,
    skipTaskbar: true,
    resizable: true,
    maximizable: false,
    minimizable: false,
    fullscreenable: false,
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload', 'panel.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  });

  keepPanelOnTop();
  panelWin.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: false });
  panelWin.loadFile(path.join(__dirname, '..', 'renderer', 'panel', 'index.html'));
  // showInactive：面板常驻但不抢焦点
  panelWin.once('ready-to-show', () => {
    panelWin.showInactive();
    keepPanelOnTop();
    log('panel shown');
  });

  // 开发诊断：直接把渲染层画面存下来（绕开系统合成），用来区分
  // 「DOM 画错了」还是「窗口合成/透明导致的视觉残影」
  //   REMEBER_CAPTURE_CLICK=b-set        先点某个按钮再抓（用来抓设置页等子页面）
  //   REMEBER_DEV_SCENE=all              顺序跑「选择器 / 详情 / 悬停预览」三个场景并各抓一张
  if (process.env.REMEBER_CAPTURE_PANEL || process.env.REMEBER_DEV_SCENE) {
    panelWin.webContents.once('did-finish-load', async () => {
      await new Promise((r) => setTimeout(r, 1500));
      const out = process.env.REMEBER_CAPTURE_PANEL;
      const scene = process.env.REMEBER_DEV_SCENE;
      const wait = (ms) => new Promise((r) => setTimeout(r, ms));
      const shot = async (win, suffix) => {
        if (!win || win.isDestroyed() || !out) return;
        const img = await win.webContents.capturePage();
        const file = suffix ? out.replace(/\.png$/i, `-${suffix}.png`) : out;
        fs.writeFileSync(file, img.toPNG());
        log('dev scene captured ->', file, JSON.stringify(img.getSize()));
      };

      try {
        if (scene === 'capture') {
          // 只跑截图链路，用来量各段耗时
          await startCapture();
          await wait(1600);
          log('--- 模拟松手 ---');
          await finishCapture({ x: 300, y: 260, width: 520, height: 300 });
          await wait(1600);
          log('--- 模拟点 Wantdo ---');
          const t = Date.now();
          commitCapture('want', '计时用的测试标题');
          log(`⏱ 从点击到入库完成 ${Date.now() - t}ms`);
          await wait(600);
          await shot(panelWin, 'capture');
        } else if (scene === 'all') {
          const withImg = store.data.items.find((i) => i.image);
          const doneWant = store.data.items.find((i) => i.state === 'done' && i.room === 'want');
          const display = screen.getPrimaryDisplay();

          // --- 0. 主面板 ---
          await shot(panelWin, 'panel');

          // --- 1. 截图去向选择器（带标题输入框）---
          if (withImg) {
            const img = nativeImage.createFromPath(path.join(store.dataDir, withImg.image));
            pending = {
              image: img,
              info: { title: '关于下周三评审的准备', source: 'Weixin' },
              display,
            };
            openChooser({ x: 300, y: 300, width: 420, height: 220 }, display, img.toDataURL());
            await wait(1100);
            await shot(chooserWin, 'chooser');
            closeChooser();
            pending = null;
          }

          // --- 2. 详情窗（自适应尺寸 + 标题/备注/Get Done）---
          if (withImg) {
            store.touch(withImg.id);
            openDetail(withImg.id);
            await wait(1200);
            await shot(detailWin, 'detail');
            if (detailWin && !detailWin.isDestroyed()) detailWin.close();
            await wait(250);
          }

          // --- 3. 悬停预览 ---
          if (withImg) {
            showPreview(withImg.id, 300, 112);
            await wait(900);
            await shot(previewWin, 'preview');
            hidePreview();
          }

          // --- 4. 完成列表页（面板切到 done）---
          await panelWin.webContents.executeJavaScript(
            `document.getElementById('b-done')?.click(); true`
          );
          await wait(700);

          // 顺手 dump 一下实际布局，省得靠肉眼看缩略图猜
          const dbg = await panelWin.webContents.executeJavaScript(`(() => {
            const els = [...document.querySelectorAll('.done-item')];
            if (!els.length) return 'no .done-item';
            return JSON.stringify(els.map(el => {
              const r = el.getBoundingClientRect();
              return {
                h: Math.round(r.height),
                kids: [...el.children].map(c => {
                  const b = c.getBoundingClientRect();
                  return c.className + ' ' + Math.round(b.width) + 'x' + Math.round(b.height) + ' @' + Math.round(b.left - r.left) + ',' + Math.round(b.top - r.top);
                }),
              };
            }));
          })()`);
          log('done-item layout:', dbg);

          await shot(panelWin, 'done');

          // --- 5. 完成记录（心得 + 小结）---
          if (doneWant) {
            openRecord(doneWant.id, 'view');
            await wait(1200);
            await shot(recordWin, 'record');
            if (recordWin && !recordWin.isDestroyed()) recordWin.close();
            await wait(250);
          }

          // --- 6. 写心得的界面 ---
          if (doneWant) {
            openRecord(doneWant.id, 'write');
            await wait(900);
            await shot(recordWin, 'record-write');
            if (recordWin && !recordWin.isDestroyed()) recordWin.close();
          }

          // --- 7. 真点一遍：勾选 → 点卡片消散 → 必须真的从列表消失 ---
          const activeTodo = store.data.items.find(
            (i) => i.room === 'todo' && i.state === 'active'
          );
          if (activeTodo) {
            await panelWin.webContents.executeJavaScript(
              `document.getElementById('b-back-done')?.click(); true`
            );
            await wait(400);

            const probe = await panelWin.webContents.executeJavaScript(`(async () => {
              const id = ${JSON.stringify(activeTodo.id)};
              const q = () => document.querySelector('.card[data-card-id="' + id + '"]');
              if (!q()) return 'FAIL: 一开始就找不到卡片';

              // 1) 点勾选框
              q().querySelector('.chk').click();
              await new Promise(r => setTimeout(r, 600));
              const afterDone = !!q();
              const markedDone = afterDone && q().classList.contains('done');

              // 2) 点卡片本体 → 泡沫消散
              if (q()) q().click();
              await new Promise(r => setTimeout(r, 1100));
              const afterFile = !!q();

              return JSON.stringify({ afterDone, markedDone, afterFile });
            })()`);
            log('dissolve test:', probe);

            // 顺手抓一张「点完之后」的面板，确认视觉上真的没了
            await wait(300);
            await shot(panelWin, 'after-dissolve');
          }
        } else {
          const clickId = process.env.REMEBER_CAPTURE_CLICK;
          if (clickId) {
            await panelWin.webContents.executeJavaScript(
              `(document.getElementById(${JSON.stringify(clickId)}) || document.querySelector(${JSON.stringify(clickId)}))?.click(); true`
            );
            await wait(600);
          }
          await shot(panelWin, '');
        }
      } catch (e) {
        log('dev scene failed', String(e));
      }
      app.quit();
    });
  }

  panelWin.on('closed', () => {
    panelWin = null;
  });

  return panelWin;
}

function pushState() {
  if (!panelWin || panelWin.isDestroyed()) return;
  try {
    panelWin.webContents.send('state:changed', store.snapshot());
  } catch (_) {}
}

function flashPanel() {
  if (!panelWin || panelWin.isDestroyed()) {
    createPanel();
    return;
  }
  if (!panelWin.isVisible()) panelWin.showInactive();
  pushState();
}

// ---------------------------------------------------------------- 截图流程

/** 截图各阶段计时（只在 DEBUG 下打日志，用来定位卡顿在哪一段） */
const timing = { t0: 0, shot: 0, wininfo: 0, overlayCreated: 0, overlayShown: 0, chooserShown: 0 };
const since = (t) => (t ? `${Date.now() - t}ms` : '-');

async function startCapture() {
  // 注意：不能再判断 overlayWin —— 它现在是常驻窗口，一直存在
  if (pending || chooserWin) return; // 已经在流程里了

  const t0 = Date.now();
  timing.t0 = t0;
  timing.shot = 0;
  timing.wininfo = 0;
  timing.overlayCreated = 0;
  timing.overlayShown = 0;

  const display = displayForPoint();
  const tDisplay = Date.now();

  // 关键：前台窗口信息与屏幕画面「并行」采样，且都在我们的窗口出现之前。
  const [image, info] = await Promise.all([
    (async () => {
      const s = Date.now();
      const r = await grabDisplay(display).catch(() => null);
      timing.shot = Date.now() - s;
      return r;
    })(),
    (async () => {
      const s = Date.now();
      const r = await wininfo.getForeground(app.getPath('userData'), 3000, log);
      timing.wininfo = Date.now() - s;
      return r;
    })(),
  ]);

  log(
    `⏱ 截图计时 | 定位屏幕 ${tDisplay - t0}ms | 抓屏 ${timing.shot}ms | ` +
      `取窗标题 ${timing.wininfo}ms | 两者就绪 ${Date.now() - t0}ms | ` +
      `标题结果=${JSON.stringify((info && info.source) || '')}/${JSON.stringify(
        (info && info.title) || ''
      )}`
  );

  if (!image) {
    return;
  }

  pending = { image, info, display };

  // 框选层是「启动时就建好」的常驻窗口。这里只做三件事：
  //   1. 把它摆到目标屏幕（若和上次不是同一块）
  //   2. 塞进新截图
  //   3. 等渲染层说「图片解码完了」再显示
  //
  // ⚠️ 关键点：显示那一刻绝不能再改窗口尺寸。
  // Windows 会把新建窗口夹到工作区（2560×1392，少掉 48px 任务栏），
  // 若在建窗口时才发现，页面已经按 1392 排版，之后拉到 1440 就会重排，
  // 旧画面被拉伸填满新尺寸 —— 用户看到的就是「轻轻放大一下」。
  const win = ensureOverlay();
  placeOverlay(display);

  const showIt = (why) => {
    if (!overlayWin || overlayWin.isDestroyed() || overlayWin.isVisible()) return;
    placeOverlay(display); // 此时窗口仍隐藏，改了也看不见
    overlayWin.setAlwaysOnTop(true, 'screen-saver');
    overlayWin.showInactive();
    if (timing.t0) {
      log(`⏱ 框选层显示（${why}）| 从按下热键到可见 ${Date.now() - timing.t0}ms`);
    }

    if (DEBUG) {
      const t = Date.now();
      [0, 16, 33, 50, 80, 120, 200, 320].forEach((d) =>
        setTimeout(() => {
          if (!overlayWin || overlayWin.isDestroyed()) return;
          const b = overlayWin.getBounds();
          log(
            `  采样 +${Date.now() - t}ms bounds=${b.x},${b.y} ${b.width}x${b.height}` +
              ` opacity=${overlayWin.getOpacity()} visible=${overlayWin.isVisible()}`
          );
        }, d)
      );
    }
  };

  overlayShow = showIt;

  const send = () => {
    if (!overlayWin || overlayWin.isDestroyed()) return;
    overlayWin.webContents.send('overlay:init', {
      dataUrl: image.toDataURL(),
      cssWidth: display.bounds.width,
      cssHeight: display.bounds.height,
    });
  };
  if (win.webContents.isLoading()) win.webContents.once('did-finish-load', send);
  else send();

  // 兜底：万一 painted 没来（渲染层异常），也得让它出来
  setTimeout(() => {
    if (overlayShow === showIt) {
      overlayShow = null;
      showIt('fallback');
    }
  }, 400);
}

// ---------------------------------------------------------------- 框选层
//
// 常驻窗口：启动时就建好、藏起来，尺寸也摆正。
// 按热键时只塞图 + 显示，全程不动窗口几何 —— 这是「不再有放大动画」的关键。

function ensureOverlay() {
  if (overlayWin && !overlayWin.isDestroyed()) return overlayWin;

  // 先按主屏建；真正截图时再用 placeOverlay 挪到光标所在那块屏
  const b = screen.getPrimaryDisplay().bounds;

  overlayWin = new BrowserWindow({
    x: b.x,
    y: b.y,
    width: b.width,
    height: b.height,
    show: false,
    frame: false,
    transparent: true,
    backgroundColor: '#00000000',
    hasShadow: false,
    alwaysOnTop: true,
    skipTaskbar: true,
    resizable: false,
    movable: false,
    minimizable: false,
    maximizable: false,
    fullscreenable: false,
    enableLargerThanScreen: true,
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload', 'overlay.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  });

  overlayWin.setAlwaysOnTop(true, 'screen-saver');
  overlayWin.loadFile(path.join(__dirname, '..', 'renderer', 'overlay', 'index.html'));

  overlayWin.webContents.on('ipc-message', (_e, channel, payload) => {
    if (channel === 'overlay:painted' && overlayShow) {
      const fn = overlayShow;
      overlayShow = null;
      fn('painted');
    } else if (channel === 'overlay:debug') {
      log('  ' + String(payload));
    }
  });

  overlayWin.on('closed', () => {
    overlayWin = null;
    overlayShow = null;
  });

  // Windows 会把新建窗口夹到工作区（少掉任务栏那几十像素）。
  // 建好之后马上摆正一次 —— 此刻窗口是隐藏的，重排完全看不出来。
  setTimeout(() => {
    if (overlayWin && !overlayWin.isDestroyed()) {
      placeOverlay(screen.getPrimaryDisplay());
      log('overlay 预热完成', JSON.stringify(overlayWin.getBounds()));
    }
    // 关掉这个窗口的 DWM 过渡动画：各层量测都稳定，剩下的嫌疑只有 DWM 自己的呈现动画
    try {
      wininfo.disableTransitions(app.getPath('userData'), overlayWin.getNativeWindowHandle(), log);
    } catch (_) {}
  }, 80);

  return overlayWin;
}

/** 把框选层摆到指定屏幕；尺寸已经对了就什么都不做 */
function placeOverlay(display) {
  if (!overlayWin || overlayWin.isDestroyed()) return;
  const b = display.bounds;
  const cur = overlayWin.getBounds();
  if (cur.x !== b.x || cur.y !== b.y || cur.width !== b.width || cur.height !== b.height) {
    overlayWin.setBounds({ x: b.x, y: b.y, width: b.width, height: b.height }, false);
    log('overlay 换屏', JSON.stringify(cur), '->', JSON.stringify(b));
  }
}

function hideOverlay() {
  overlayShow = null;
  if (overlayWin && !overlayWin.isDestroyed() && overlayWin.isVisible()) overlayWin.hide();
  keepPanelOnTop();
}

function cancelCapture() {
  hideOverlay();
  closeChooser();
  pending = null;
}

async function finishCapture(cssRect) {
  if (!pending) return cancelCapture();
  const { image, info, display } = pending;
  timing.picked = Date.now();

  const cropped = cropByCssRect(image, cssRect, display.bounds.width);
  if (!cropped || cropped.isEmpty()) return cancelCapture();

  hideOverlay();

  const previewUrl = cropped.toDataURL();
  const pngBuffer = cropped.toPNG();

  // 记住这批数据，等用户在 chooser 里做「一次情感判断」
  pending = { ...pending, cropped, previewUrl, pngBuffer, info };

  openChooser(cssRect, display, previewUrl);
}

// ---------------------------------------------------------------- 去向选择器

function openChooser(cssRect, display, previewUrl) {
  const W = 312;
  const H = 212; // 比之前高，给「填标题」那一行留位置
  const { bounds } = display;

  // 贴着选区右下角，超出屏幕就夹回来
  let x = Math.round(bounds.x + cssRect.x + cssRect.width - W);
  let y = Math.round(bounds.y + cssRect.y + cssRect.height + 10);
  x = Math.min(Math.max(x, bounds.x + 8), bounds.x + bounds.width - W - 8);
  if (y + H > bounds.y + bounds.height - 8) {
    y = Math.round(bounds.y + cssRect.y - H - 10);
  }
  y = Math.min(Math.max(y, bounds.y + 8), bounds.y + bounds.height - H - 8);

  chooserWin = new BrowserWindow({
    width: W,
    height: H,
    x,
    y,
    frame: false,
    transparent: true,
    backgroundColor: '#00000000',
    hasShadow: false,
    alwaysOnTop: true,
    skipTaskbar: true,
    resizable: false,
    movable: true,
    minimizable: false,
    fullscreenable: false,
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload', 'overlay.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  });

  chooserWin.setAlwaysOnTop(true, 'screen-saver');
  chooserWin.loadFile(path.join(__dirname, '..', 'renderer', 'chooser', 'index.html'));
  chooserWin.webContents.once('did-finish-load', () => {
    if (chooserWin && !chooserWin.isDestroyed()) {
      chooserWin.webContents.send('chooser:init', {
        previewUrl,
        source: (pending && pending.info && pending.info.source) || '',
        title: (pending && pending.info && pending.info.title) || '',
      });
    }
    if (timing.t0) {
      log(`⏱ 选择器就绪 | 从松手到按钮可用 ${Date.now() - timing.picked}ms`);
    }
  });
  chooserWin.on('closed', () => {
    chooserWin = null;
  });
}

function closeChooser() {
  if (chooserWin && !chooserWin.isDestroyed()) chooserWin.close();
  chooserWin = null;
  keepPanelOnTop();
}

function commitCapture(choice, typedTitle) {
  if (!pending) return cancelCapture();
  const { pngBuffer, info } = pending;

  const room = choice === 'want' ? 'want' : 'todo';
  const dueBucket = choice === 'today' ? 'today' : choice === 'soon' ? 'soon' : null;

  // 标题优先级：用户手填 > 截图时的前台窗口标题 > 空
  const userTitle = typeof typedTitle === 'string' ? typedTitle.trim() : '';
  const title = userTitle || info.title || '';

  // 先写条目拿 id，再存图（图名用 id）
  const item = store.add({
    room,
    title,
    source: info.source || '',
    dueBucket,
  });

  try {
    const rel = store.saveImage(item.id, pngBuffer);
    item.image = rel;
    store.save();
  } catch (_) {
    /* 存图失败不该让条目丢失，退化为纯文本条目 */
  }

  pending = null;
  closeChooser();
  flashPanel();
  pushState();
}

// ---------------------------------------------------------------- 图片协议

function registerImageProtocol() {
  const root = path.resolve(store.dataDir);
  protocol.handle('appimg', (request) => {
    try {
      const u = new URL(request.url);
      const rel = decodeURIComponent(u.pathname).replace(/^\/+/, '');
      const abs = path.resolve(path.join(root, rel));
      if (!abs.startsWith(root)) {
        log('appimg 403:', rel);
        return new Response('forbidden', { status: 403 });
      }
      if (!fs.existsSync(abs)) {
        log('appimg 404:', rel);
        return new Response('not found', { status: 404 });
      }
      return net.fetch(pathToFileURL(abs).toString());
    } catch (err) {
      return new Response('not found', { status: 404 });
    }
  });
}

// ---------------------------------------------------------------- 大图查看

/** 取带衰减字段的条目（原始 item 没有 opacity/life，UI 需要算好的那份） */
function enriched(id) {
  return store.snapshot().items.find((i) => i.id === id) || null;
}

// 详情窗的固定开销：标题栏 + 元信息 + 备注区 + 图片四周留白
const DETAIL_CHROME_H = 288;
const DETAIL_CHROME_W = 32;
const DETAIL_MAX_W = 900;
const DETAIL_MAX_H = 840;
// 下限只保证「备注和元信息能正常排布」，别为了小截图留一大片空白
const DETAIL_MIN_W = 420;
const DETAIL_MIN_H = 340;

/** 让详情窗贴合图片比例，不必再在大窗口里看小图 */
function detailSizeFor(item) {
  let iw = 640;
  let ih = 420;

  if (item && item.image) {
    try {
      const abs = path.join(store.dataDir, item.image);
      const img = nativeImage.createFromPath(abs);
      const s = img.getSize();
      if (s.width > 0 && s.height > 0) {
        iw = s.width;
        ih = s.height;
      }
    } catch (_) {
      /* 读不到就用默认尺寸 */
    }
  }

  const availW = DETAIL_MAX_W - DETAIL_CHROME_W;
  const availH = DETAIL_MAX_H - DETAIL_CHROME_H;
  const scale = Math.min(1, availW / iw, availH / ih);

  return {
    width: Math.max(DETAIL_MIN_W, Math.round(iw * scale) + DETAIL_CHROME_W),
    height: Math.max(DETAIL_MIN_H, Math.round(ih * scale) + DETAIL_CHROME_H),
  };
}

function openDetail(itemId) {
  const item = store.get(itemId);
  if (!item) return;

  if (detailWin && !detailWin.isDestroyed()) {
    // 换条目时一并调整窗口大小，让它重新贴合新图
    try {
      const b = detailWin.getBounds();
      const s = detailSizeFor(item);
      detailWin.setBounds({ x: b.x, y: b.y, width: s.width, height: s.height });
    } catch (_) {}
    detailWin.webContents.send('detail:init', { item: enriched(itemId) });
    detailWin.focus();
    return;
  }

  const size = detailSizeFor(item);

  detailWin = new BrowserWindow({
    width: size.width,
    height: size.height,
    frame: false,
    transparent: true,
    backgroundColor: '#00000000',
    hasShadow: true,
    alwaysOnTop: true,
    skipTaskbar: true,
    resizable: true,
    minWidth: DETAIL_MIN_W,
    minHeight: DETAIL_MIN_H,
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload', 'panel.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  });
  detailWin.setAlwaysOnTop(true, 'floating');
  detailWin.loadFile(path.join(__dirname, '..', 'renderer', 'detail', 'index.html'));
  detailWin.webContents.once('did-finish-load', () => {
    if (detailWin && !detailWin.isDestroyed()) {
      detailWin.webContents.send('detail:init', { item: enriched(itemId) });
    }
  });
  detailWin.on('closed', () => {
    detailWin = null;
  });
  log('detail window opened', itemId, JSON.stringify(size));
}

// ---------------------------------------------------------------- 悬停预览
//
// 鼠标在一个条目上停久一点 → 在面板左侧弹出一张放大图。
// 目的是「不点进去就能看清图里记了什么」。
// 三个关键点：
//   1. focusable: false —— 绝不抢焦点，否则鼠标一移动面板就闪
//   2. setIgnoreMouseEvents(true) —— 预览不吃鼠标事件
//   3. 面板贴在屏幕右侧，所以预览默认放左边，放不下才翻到右边

function ensurePreviewWin() {
  if (previewWin && !previewWin.isDestroyed()) return previewWin;

  previewWin = new BrowserWindow({
    width: 320,
    height: 240,
    show: false,
    frame: false,
    transparent: true,
    backgroundColor: '#00000000',
    hasShadow: true,
    alwaysOnTop: true,
    skipTaskbar: true,
    resizable: false,
    movable: false,
    focusable: false,
    minimizable: false,
    maximizable: false,
    fullscreenable: false,
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload', 'preview.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  });

  previewWin.setAlwaysOnTop(true, 'floating');
  previewWin.setIgnoreMouseEvents(true);
  previewWin.loadFile(path.join(__dirname, '..', 'renderer', 'preview', 'index.html'));
  previewWin.on('closed', () => {
    previewWin = null;
  });
  return previewWin;
}

function showPreview(id, cardTop, cardHeight) {
  const item = store.get(id);
  if (!item || !item.image) return;
  if (!panelWin || panelWin.isDestroyed()) return;

  let iw = 640;
  let ih = 420;
  try {
    const s = nativeImage.createFromPath(path.join(store.dataDir, item.image)).getSize();
    if (s.width > 0 && s.height > 0) {
      iw = s.width;
      ih = s.height;
    }
  } catch (_) {
    /* 读不到就按默认比例 */
  }

  const pb = panelWin.getBounds();
  const work = screen.getDisplayNearestPoint({ x: pb.x + pb.width / 2, y: pb.y }).workArea;

  // 面板左边还剩多少空间；太窄就把预览限小
  const roomLeft = pb.x - work.x - 24;
  const maxW = Math.max(200, Math.min(460, roomLeft > 200 ? roomLeft : work.width * 0.3));
  const maxH = Math.max(160, Math.round(work.height * 0.62));

  // 上限 2 倍：小截图放大一点才看得清
  const scale = Math.min(maxW / iw, maxH / ih, 2);
  const w = Math.max(120, Math.round(iw * scale));
  const h = Math.max(90, Math.round(ih * scale));

  let x = pb.x - w - 12;
  if (x < work.x + 8) x = Math.min(pb.x + pb.width + 12, work.x + work.width - w - 8);

  let y = pb.y + (cardTop || 0) + (cardHeight || 0) / 2 - h / 2;
  y = Math.min(Math.max(y, work.y + 8), work.y + work.height - h - 8);

  const win = ensurePreviewWin();
  win.setBounds({ x: Math.round(x), y: Math.round(y), width: w, height: h });

  const send = () => {
    if (win && !win.isDestroyed()) win.webContents.send('preview:init', { src: `appimg://local/${item.image}` });
  };
  if (win.webContents.isLoading()) win.webContents.once('did-finish-load', send);
  else send();

  win.showInactive();
}

function hidePreview() {
  if (previewWin && !previewWin.isDestroyed() && previewWin.isVisible()) previewWin.hide();
  keepPanelOnTop();
}

// ---------------------------------------------------------------- 完成记录窗
//
// 两种模式：
//   write —— 点「Get Done」后写心得（还没完成）
//   view  —— 完成后回看心得 + 自动生成的统计
// 同一个窗复用，避免又多一个窗口概念。

const RECORD_W = 430;

function openRecord(id, mode) {
  const item = enriched(id);
  if (!item) return;

  const isView = mode === 'view';
  const h = isView ? 566 : 400;

  if (recordWin && !recordWin.isDestroyed()) {
    recordWin.setBounds({ ...recordWin.getBounds(), width: RECORD_W, height: h });
    recordWin.webContents.send('record:init', { item, mode: isView ? 'view' : 'write' });
    recordWin.focus();
    return;
  }

  recordWin = new BrowserWindow({
    width: RECORD_W,
    height: h,
    frame: false,
    transparent: true,
    backgroundColor: '#00000000',
    hasShadow: true,
    alwaysOnTop: true,
    skipTaskbar: true,
    resizable: true,
    minWidth: 360,
    minHeight: 320,
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload', 'record.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  });
  recordWin.setAlwaysOnTop(true, 'floating');
  recordWin.loadFile(path.join(__dirname, '..', 'renderer', 'record', 'index.html'));
  recordWin.webContents.once('did-finish-load', () => {
    if (recordWin && !recordWin.isDestroyed()) {
      recordWin.webContents.send('record:init', {
        item: enriched(id),
        mode: isView ? 'view' : 'write',
      });
    }
  });
  recordWin.on('closed', () => {
    recordWin = null;
  });
  log('record window opened', id, mode);
}

function closeRecord() {
  if (recordWin && !recordWin.isDestroyed()) recordWin.close();
  recordWin = null;
  keepPanelOnTop();
}

// ---------------------------------------------------------------- IPC

function registerIpc() {
  ipcMain.handle('state:get', () => store.snapshot());

  ipcMain.handle('item:touch', (_e, id) => {
    store.touch(id);
    pushState();
    return true;
  });

  // 完成。勾选框只切换状态（还能撤销），真正收走走 item:file
  ipcMain.handle('item:done', (_e, payload) => {
    const id = typeof payload === 'string' ? payload : payload && payload.id;
    const opts = typeof payload === 'object' && payload ? payload : {};
    store.done(id, { reflection: opts.reflection, file: opts.file });
    pushState();
    return true;
  });

  // 完成之后收走（泡沫消散那一下），进完成列表
  ipcMain.handle('item:file', (_e, id) => {
    store.file(id);
    pushState();
    return true;
  });

  ipcMain.handle('item:bin', (_e, id) => {
    store.bin(id);
    pushState();
    return true;
  });

  ipcMain.handle('item:restore', (_e, id) => {
    store.restore(id);
    pushState();
    return true;
  });

  ipcMain.handle('item:remove', (_e, id) => {
    store.remove(id);
    pushState();
    return true;
  });

  ipcMain.handle('item:addManual', (_e, payload) => {
    const room = payload && payload.room === 'want' ? 'want' : 'todo';
    const item = store.add({
      room,
      title: (payload && payload.title) || '（没写内容）',
      source: '手动',
      dueBucket: room === 'todo' ? 'today' : null,
    });
    pushState();
    return item;
  });

  ipcMain.handle('item:openDetail', (_e, id) => {
    store.touch(id);
    openDetail(id);
    pushState();
    return true;
  });

  // 改标题 / 备注
  // 注意：不要回传 detail:init —— 那会把用户正在打的字重置掉
  ipcMain.handle('item:update', (_e, { id, patch }) => {
    const it = store.update(id, patch);
    pushState();
    return !!it;
  });

  // 悬停预览：长悬停时在面板左侧弹出放大图
  ipcMain.on('preview:show', (_e, { id, top, height }) => showPreview(id, top, height));
  ipcMain.on('preview:hide', () => hidePreview());

  // 完成记录窗
  ipcMain.handle('record:open', (_e, { id, mode }) => {
    openRecord(id, mode);
    return true;
  });

  ipcMain.handle('record:commit', (_e, { id, reflection }) => {
    store.done(id, { reflection, file: true });
    pushState();
    closeRecord();
    return true;
  });

  ipcMain.on('record:close', () => closeRecord());

  // 首次使用：灌演示数据（只在库为空时生效，不会污染已有数据）
  ipcMain.handle('demo:seed', () => {
    const seeded = require('./devseed').seed(store);
    if (seeded) pushState();
    return seeded;
  });

  ipcMain.handle('settings:set', (_e, { key, value }) => {
    store.setSetting(key, value);
    pushState();
    return store.settings;
  });

  ipcMain.handle('capture:start', () => {
    startCapture();
    return true;
  });

  ipcMain.handle('panel:quit', () => {
    app.quit();
    return true;
  });

  ipcMain.handle('panel:hide', () => {
    if (panelWin && !panelWin.isDestroyed()) panelWin.hide();
    return true;
  });

  // 折叠/展开：保持右上角不动
  ipcMain.handle('panel:resize', (_e, { width, height }) => {
    if (!panelWin || panelWin.isDestroyed()) return false;
    const b = panelWin.getBounds();
    const w = Math.max(160, Math.round(width));
    const h = Math.max(44, Math.round(height));
    panelWin.setBounds({ x: b.x + (b.width - w), y: b.y, width: w, height: h }, false);
    return true;
  });

  // 渲染层存活探针：UI 真的跑起来了才打这条日志
  ipcMain.on('ui:ping', (_e, tag) => {
    log('ui ping:', tag);
    try {
      fs.writeFileSync(path.join(app.getPath('userData'), `ui-${tag}.ok`), String(Date.now()));
    } catch (_) {}
  });

  // overlay / chooser 通道
  ipcMain.on('overlay:ready', (e) => {
    if (!pending || !overlayWin || overlayWin.isDestroyed()) return;
    const t = Date.now();
    e.sender.send('overlay:init', {
      dataUrl: pending.image.toDataURL(),
      cssWidth: pending.display.bounds.width,
      cssHeight: pending.display.bounds.height,
    });
    timing.overlayShown = Date.now();
    log(
      `⏱ 框选层就绪 | 从按下热键到画面可交互 ${timing.overlayShown - timing.t0}ms | ` +
        `其中生成 dataURL 并发 IPC ${Date.now() - t}ms`
    );
  });

  ipcMain.on('overlay:pick', (_e, rect) => {
    finishCapture(rect);
  });

  ipcMain.on('overlay:cancel', () => cancelCapture());

  ipcMain.on('chooser:pick', (_e, choice, typedTitle) => commitCapture(choice, typedTitle));

  ipcMain.on('chooser:cancel', () => cancelCapture());
}

// ---------------------------------------------------------------- 启动

const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.quit();
} else {
  app.on('second-instance', () => flashPanel());

  app.whenReady().then(() => {
    log('userData =', app.getPath('userData'));
    store = new Store(app.getPath('userData'));
    log('store loaded, items =', store.data.items.length);
    if (process.env.REMEBER_SEED || FLAG_DEMO) {
      const seeded = require('./devseed').seed(store);
      log(seeded ? 'seeded demo data' : 'seed skipped (库非空)');
    }
    registerImageProtocol();
    registerIpc();
    createPanel();
    // 提前把框选层建好并预热（隐藏状态），按热键时就不必再创建窗口/改尺寸
    ensureOverlay();

    // 全局热键
    const reg = (accel, fn) => {
      try {
        return globalShortcut.register(accel, fn);
      } catch (_) {
        return false;
      }
    };
    const hkCapture = reg(HOTKEY_CAPTURE, () => startCapture());
    const hkToggle = reg(HOTKEY_TOGGLE, () => {
      if (!panelWin || panelWin.isDestroyed()) return createPanel();
      if (panelWin.isVisible()) panelWin.hide();
      else panelWin.show();
    });
    const hkQuit = reg(HOTKEY_QUIT, () => app.quit());
    log('hotkeys', { capture: hkCapture, toggle: hkToggle, quit: hkQuit });

    // 定时刷新（衰减要能自己长出来）
    tickTimer = setInterval(() => {
      // 顺手确认一下还置顶 —— 有些软件（含全屏应用）会把自己插到我们前面
      keepPanelOnTop();
      pushState();
    }, 5000);
    log('ready');

    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) createPanel();
    });
  });

  app.on('will-quit', () => {
    if (tickTimer) clearInterval(tickTimer);
    globalShortcut.unregisterAll();
  });

  app.on('window-all-closed', () => app.quit());
}
