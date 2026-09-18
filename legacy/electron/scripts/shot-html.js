'use strict';

/**
 * 开发用：把一个本地 HTML 渲染出来截图，用来检查易读性/排版。
 * 用法： electron scripts/shot-html.js <html路径> <输出png> [宽] [高] [缩放] [滚动Y]
 *
 * 注意：Windows 会把窗口高度夹到工作区（屏幕高 - 任务栏），所以想看整页得靠缩放或分段滚动。
 */

const { app, BrowserWindow } = require('electron');
const fs = require('fs');
const path = require('path');

const [, , htmlPath, outPath, wArg, hArg, zArg, sArg] = process.argv;
const W = Number(wArg) || 860;
const H = Number(hArg) || 1392;
const Z = Number(zArg) || 1;
const SCROLL = Number(sArg) || 0;

app.whenReady().then(async () => {
  const win = new BrowserWindow({
    width: W,
    height: H,
    show: false,
    frame: false,
    backgroundColor: '#ffffff',
    webPreferences: { offscreen: false, javascript: true },
  });

  await win.loadFile(path.resolve(htmlPath));
  if (Z !== 1) win.webContents.setZoomFactor(Z);
  await new Promise((r) => setTimeout(r, 900));
  if (SCROLL) {
    await win.webContents.executeJavaScript(`window.scrollTo(0, ${SCROLL}); true`);
    await new Promise((r) => setTimeout(r, 400));
  }

  const img = await win.webContents.capturePage();
  fs.writeFileSync(path.resolve(outPath), img.toPNG());
  console.log('saved', outPath, JSON.stringify(img.getSize()));

  app.quit();
});
