'use strict';

/**
 * 开发用：截取「面板所在的那一小块屏幕」，用来肉眼验证 UI。
 * 刻意只裁面板区域 —— 不抓整屏，避免把桌面上的私人内容带出来。
 *
 * 用法： electron scripts/shot.js <输出路径>
 */

const { app, desktopCapturer, screen } = require('electron');
const fs = require('fs');

// 必须和 main.js 的 PANEL_W / PANEL_H 保持一致，否则会裁歪
const PANEL_W = 340;
const PANEL_H = 500;
const MARGIN = 20;

app.whenReady().then(async () => {
  await new Promise((r) => setTimeout(r, 1200));

  const d = screen.getPrimaryDisplay();
  const scale = d.scaleFactor || 1;
  const fullW = Math.round(d.bounds.width * scale);
  const fullH = Math.round(d.bounds.height * scale);

  const sources = await desktopCapturer.getSources({
    types: ['screen'],
    thumbnailSize: { width: fullW, height: fullH },
  });
  if (!sources.length) {
    console.error('no screen source');
    return app.exit(1);
  }

  const img = sources[0].thumbnail;
  const size = img.getSize();
  const s = size.width / d.bounds.width; // 物理像素 / DIP

  const panelX = d.workArea.x + d.workArea.width - PANEL_W - MARGIN;
  const panelY = d.workArea.y + MARGIN;

  const pad = 26;
  const rect = {
    x: Math.max(0, Math.round((panelX - pad) * s)),
    y: Math.max(0, Math.round((panelY - pad) * s)),
    width: Math.round((PANEL_W + pad * 2) * s),
    height: Math.round((PANEL_H + pad * 2) * s),
  };
  rect.width = Math.min(rect.width, size.width - rect.x);
  rect.height = Math.min(rect.height, size.height - rect.y);

  const cropped = img.crop(rect);
  const out = process.argv[2] || 'panel-shot.png';
  fs.writeFileSync(out, cropped.toPNG());
  console.log('saved', out, JSON.stringify(rect), 'scale', s);

  app.quit();
});
