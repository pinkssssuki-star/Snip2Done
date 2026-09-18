'use strict';

/**
 * 截图层：抓取显示器画面 + 按选区裁剪。
 * 全部用 Electron 自带能力，零原生依赖。
 */

const { desktopCapturer, screen } = require('electron');

/**
 * 抓取指定显示器的完整画面。
 * 以「物理像素」为尺寸请求，避免高 DPI 下裁剪坐标错位。
 * @returns {Promise<Electron.NativeImage|null>}
 */
async function grabDisplay(display) {
  const scale = display.scaleFactor || 1;
  const width = Math.max(1, Math.round(display.bounds.width * scale));
  const height = Math.max(1, Math.round(display.bounds.height * scale));

  const sources = await desktopCapturer.getSources({
    types: ['screen'],
    thumbnailSize: { width, height },
    fetchWindowIcons: false,
  });
  if (!sources || !sources.length) return null;

  // 优先按 display_id 精确匹配，匹配不到就退回第一块屏
  let src = sources.find((s) => String(s.display_id) === String(display.id));
  if (!src) src = sources[0];
  const img = src.thumbnail;
  if (!img || img.isEmpty()) return null;
  return img;
}

/**
 * 按 overlay 上报的 CSS 像素矩形裁剪。
 * overlay 覆盖整个 display.bounds，所以：
 *   scale = 截图实际像素宽 / overlay 的 CSS 宽
 */
function cropByCssRect(image, cssRect, overlayCssWidth) {
  const size = image.getSize();
  const scale = overlayCssWidth > 0 ? size.width / overlayCssWidth : 1;

  const x = Math.round(cssRect.x * scale);
  const y = Math.round(cssRect.y * scale);
  const w = Math.round(cssRect.width * scale);
  const h = Math.round(cssRect.height * scale);

  const sx = Math.max(0, Math.min(x, Math.max(0, size.width - 1)));
  const sy = Math.max(0, Math.min(y, Math.max(0, size.height - 1)));

  return image.crop({
    x: sx,
    y: sy,
    width: Math.max(1, Math.min(w, size.width - sx)),
    height: Math.max(1, Math.min(h, size.height - sy)),
  });
}

/** 找出包含某点（或光标所在）的显示器。 */
function displayForPoint(point) {
  try {
    return screen.getDisplayNearestPoint(point || screen.getCursorScreenPoint());
  } catch (_) {
    return screen.getPrimaryDisplay();
  }
}

module.exports = { grabDisplay, cropByCssRect, displayForPoint };
