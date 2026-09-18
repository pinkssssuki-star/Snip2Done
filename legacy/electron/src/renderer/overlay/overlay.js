'use strict';

const shot = document.getElementById('shot');
const veil = document.getElementById('veil');
const sel = document.getElementById('sel');
const sizeTag = document.getElementById('size');
const hint = document.getElementById('hint');

let dragging = false;
let startX = 0;
let startY = 0;
let inited = false;

/** 图片解码完成、第一帧真的上屏了，才通知主进程显示窗口 —— 避免「先黑一下再出图」 */
function announcePainted() {
  requestAnimationFrame(() => requestAnimationFrame(() => window.ov.painted()));
}

/** 每次重新截图都要把上一次的框选痕迹清干净（窗口是复用的，不会重建） */
function resetSelection() {
  dragging = false;
  inited = false;
  sel.classList.remove('on');
  sizeTag.classList.remove('on');
  veil.classList.remove('off');
  hint.classList.remove('off');
  sel.style.width = '0px';
  sel.style.height = '0px';
}

window.ov.onInit(({ dataUrl }) => {
  resetSelection();
  shot.onload = () => {
    inited = true;
    announcePainted();
    // 诊断：如果显示之后渲染视口还在变，那 `100vw/100vh` 就会短暂算错，
    // 图被 object-fit:fill 拉伸填满 —— 看起来正是「轻轻放大一下」。
    if (window.ov.debug) {
      const t = Date.now();
      [0, 16, 33, 50, 80, 130, 220, 350].forEach((d) =>
        setTimeout(() => {
          const r = shot.getBoundingClientRect();
          window.ov.debug(
            `视口 +${Date.now() - t}ms inner=${window.innerWidth}x${window.innerHeight}` +
              ` dpr=${window.devicePixelRatio} img=${Math.round(r.width)}x${Math.round(r.height)}` +
              ` natural=${shot.naturalWidth}x${shot.naturalHeight} complete=${shot.complete}`
          );
        }, d)
      );
    }
  };
  shot.onerror = () => {
    // 图挂了也得让用户能操作，别把窗口永远卡在隐藏状态
    inited = true;
    announcePainted();
  };
  shot.src = dataUrl;
});

// 等主进程把截图发过来
setTimeout(() => window.ov.ready(), 0);

function clamp(v, lo, hi) {
  return Math.max(lo, Math.min(v, hi));
}

function toRect(x1, y1, x2, y2) {
  const x = Math.min(x1, x2);
  const y = Math.min(y1, y2);
  return {
    x,
    y,
    width: Math.abs(x2 - x1),
    height: Math.abs(y2 - y1),
  };
}

function paint(rect) {
  sel.classList.add('on');
  sel.style.left = rect.x + 'px';
  sel.style.top = rect.y + 'px';
  sel.style.width = rect.width + 'px';
  sel.style.height = rect.height + 'px';

  sizeTag.classList.add('on');
  const label = `${Math.round(rect.width)} × ${Math.round(rect.height)}`;
  // 贴在选区上方，顶到屏幕顶就挪到里面
  const above = rect.y > 26;
  sizeTag.textContent = label;
  sizeTag.style.left = clamp(rect.x, 6, window.innerWidth - 92) + 'px';
  sizeTag.style.top = (above ? rect.y - 22 : rect.y + 8) + 'px';
}

window.addEventListener('mousedown', (e) => {
  if (e.button !== 0 || !inited) return;
  dragging = true;
  startX = e.clientX;
  startY = e.clientY;
  veil.classList.add('off');
  hint.classList.add('off');
  paint({ x: startX, y: startY, width: 0, height: 0 });
});

window.addEventListener('mousemove', (e) => {
  if (!dragging) return;
  paint(toRect(startX, startY, e.clientX, e.clientY));
});

window.addEventListener('mouseup', (e) => {
  if (!dragging) return;
  dragging = false;

  const rect = toRect(startX, startY, e.clientX, e.clientY);

  // 太小的框选（基本等于误点）→ 当取消处理
  if (rect.width < 6 || rect.height < 6) {
    window.ov.cancel();
    return;
  }

  window.ov.pick({
    x: rect.x,
    y: rect.y,
    width: rect.width,
    height: rect.height,
  });
});

window.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') window.ov.cancel();
});

// 右键也当取消，符合直觉
window.addEventListener('contextmenu', (e) => {
  e.preventDefault();
  window.ov.cancel();
});
