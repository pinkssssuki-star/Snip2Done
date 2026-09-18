'use strict';

const thumb = document.getElementById('thumb');
const srcEl = document.getElementById('src');
const titleEl = document.getElementById('title');

const escapeHtml = (s) =>
  String(s || '').replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));

window.ov.onChooserInit(({ previewUrl, source, title }) => {
  if (previewUrl) thumb.src = previewUrl;

  // 来源软件 + 窗口标题 —— 纯图片原则下唯一保留的文本元数据
  const parts = [];
  if (source) parts.push(`<span class="app">${escapeHtml(source)}</span>`);
  if (title) parts.push(escapeHtml(title));
  srcEl.innerHTML = parts.length ? parts.join(' · ') : '（没拿到来源信息）';

  // 把窗口标题预填进去，用户想改就改；删空了就自动回落到窗口标题
  titleEl.value = title || '';
  titleEl.focus();
  titleEl.select();
});

const pick = (choice) => window.ov.choose(choice, titleEl.value.trim());

document.querySelectorAll('button[data-choice]').forEach((btn) => {
  btn.addEventListener('click', () => pick(btn.dataset.choice));
});

window.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') return window.ov.cancelChooser();

  // 只有在输入框里打字时才拦截；否则 1/2/3 直接选
  const typing = e.target === titleEl;

  if (e.key === 'Enter') {
    // 输入框里回车只是收起焦点，避免手一抖就误存
    if (typing) titleEl.blur();
    return;
  }
  if (typing) return;

  if (e.key === '1') return pick('today');
  if (e.key === '2') return pick('soon');
  if (e.key === '3') return pick('want');
});

window.addEventListener('contextmenu', (e) => {
  e.preventDefault();
  window.ov.cancelChooser();
});
