'use strict';

/**
 * 生成应用图标（build/icon.png + build/icon.ico）。
 *
 * 手搓像素而不是塞一张图：这样仓库里不用存二进制，
 * 而且改配色只要动几个数。PNG 编码复用 devseed 里那套（零依赖）。
 *
 * 图形：紫渐变圆角方块（记忆/注意力）+ 一张白色「截图卡片」+ 一个小圆点。
 *
 * 用法：node scripts/make-icon.js
 */

const fs = require('fs');
const path = require('path');
const { makePng } = require('../src/main/devseed');

const OUT_DIR = path.join(__dirname, '..', 'build');

// ---------------------------------------------------------------- 画图

const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);

/** 圆角矩形的覆盖率（0~1），带一点边缘抗锯齿 */
function roundedCoverage(x, y, w, h, r) {
  // 到圆角矩形边界的「有符号距离」，负数在内部
  const cx = Math.abs(x - w / 2) - (w / 2 - r);
  const cy = Math.abs(y - h / 2) - (h / 2 - r);
  const dx = Math.max(cx, 0);
  const dy = Math.max(cy, 0);
  const outside = Math.sqrt(dx * dx + dy * dy);
  const inside = Math.min(Math.max(cx, cy), 0);
  const d = outside + inside - r;
  // d < -0.5 完全在内，> 0.5 完全在外
  return clamp01(0.5 - d);
}

function drawIcon(size) {
  const s = size;
  const pad = s * 0.06; // 外边距
  const R = s * 0.22; // 圆角半径

  // 内部「截图卡片」的位置
  const cw = s * 0.5;
  const ch = s * 0.36;
  const cx0 = (s - cw) / 2;
  const cy0 = (s - ch) / 2 - s * 0.02;
  const cr = s * 0.07;

  // 卡片里的「山与太阳」（照片感的经典符号）
  const sunR = s * 0.045;
  const sunX = cx0 + cw * 0.28;
  const sunY = cy0 + ch * 0.32;

  return makePng(s, s, (px, py) => {
    const x = px + 0.5;
    const y = py + 0.5;

    const outer = roundedCoverage(x, y, s, s, R);
    if (outer <= 0) return [0, 0, 0, 0];

    // 底：斜向紫渐变
    const t = clamp01((x / s) * 0.55 + (y / s) * 0.45);
    let r = Math.round(124 + (168 - 124) * t);
    let g = Math.round(58 + (85 - 58) * t);
    let b = Math.round(237 + (247 - 237) * t);

    // 高光：左上角一点柔光
    const glow = Math.max(0, 1 - Math.hypot(x - s * 0.28, y - s * 0.22) / (s * 0.55));
    r = Math.min(255, r + glow * 34);
    g = Math.min(255, g + glow * 22);
    b = Math.min(255, b + glow * 20);

    // 卡片
    const cardIn = roundedCoverage(x - cx0, y - cy0, cw, ch, cr);
    // 卡片边缘一点点描边感
    const cardEdge = roundedCoverage(x - cx0, y - cy0, cw, ch, cr) -
      roundedCoverage(x - cx0 - 1.2, y - cy0 - 1.2, cw - 2.4, ch - 2.4, cr - 1.2);

    // 卡片内部的「山」
    const baseY = cy0 + ch * 0.78;
    const m1 = baseY - Math.abs(x - (cx0 + cw * 0.34)) * 0.9;
    const m2 = baseY - Math.abs(x - (cx0 + cw * 0.72)) * 0.62;
    const onMountain = y > Math.max(m1, m2) && y < baseY;

    // 太阳
    const onSun = Math.hypot(x - sunX, y - sunY) < sunR;

    let a = outer;

    if (cardIn > 0) {
      const white = clamp01(cardIn - cardEdge * 0.0);
      // 卡片内部先铺白
      r = Math.round(r + (255 - r) * white);
      g = Math.round(g + (255 - g) * white);
      b = Math.round(b + (255 - b) * white);

      // 山与太阳用紫色
      if (onMountain || onSun) {
        const k = 0.9 * cardIn;
        r = Math.round(r + (139 - r) * k);
        g = Math.round(g + (92 - g) * k);
        b = Math.round(b + (246 - b) * k);
      }
    }

    // 右下角一个小圆点（注意力 / 「碰过」的提示）
    const dotR = s * 0.055;
    const dotX = s * 0.78;
    const dotY = s * 0.78;
    const dotD = Math.hypot(x - dotX, y - dotY) - dotR;
    const dotCov = clamp01(0.5 - dotD);
    if (dotCov > 0) {
      r = Math.round(r + (236 - r) * dotCov);
      g = Math.round(g + (72 - g) * dotCov);
      b = Math.round(b + (153 - b) * dotCov);
    }

    return [r, g, b, Math.round(a * 255)];
  });
}

// ---------------------------------------------------------------- ICO 封装

/**
 * 把 PNG 包成单张 256×256 的 .ico。
 * Vista 之后的 ICO 允许直接内嵌 PNG，所以不需要再做 BMP 那些老格式。
 */
function pngToIco(pngBuffer, size) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0); // reserved
  header.writeUInt16LE(1, 2); // type: icon
  header.writeUInt16LE(1, 4); // count

  const entry = Buffer.alloc(16);
  entry.writeUInt8(size >= 256 ? 0 : size, 0); // 0 表示 256
  entry.writeUInt8(size >= 256 ? 0 : size, 1);
  entry.writeUInt8(0, 2); // palette
  entry.writeUInt8(0, 3); // reserved
  entry.writeUInt16LE(1, 4); // color planes
  entry.writeUInt16LE(32, 6); // bits per pixel
  entry.writeUInt32LE(pngBuffer.length, 8);
  entry.writeUInt32LE(6 + 16, 12); // 数据偏移

  return Buffer.concat([header, entry, pngBuffer]);
}

// ---------------------------------------------------------------- 输出

fs.mkdirSync(OUT_DIR, { recursive: true });

const png512 = drawIcon(512);
fs.writeFileSync(path.join(OUT_DIR, 'icon.png'), png512);

const png256 = drawIcon(256);
fs.writeFileSync(path.join(OUT_DIR, 'icon.ico'), pngToIco(png256, 256));

console.log('✓ build/icon.png', png512.length, 'bytes');
console.log('✓ build/icon.ico', fs.statSync(path.join(OUT_DIR, 'icon.ico')).size, 'bytes');
