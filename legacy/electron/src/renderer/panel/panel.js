'use strict';

const $ = (id) => document.getElementById(id);

const els = {
  panel: $('panel'),
  quick: $('quick'),
  quickInput: $('quick-input'),
  quickRooms: document.querySelectorAll('.quick-rooms button'),

  viewMain: $('view-main'),
  viewBin: $('view-bin'),
  viewDone: $('view-done'),
  viewSet: $('view-set'),

  todoList: $('todo-list'),
  wantList: $('want-list'),
  wantEmpty: $('want-empty'),
  binList: $('bin-list'),
  binEmpty: $('bin-empty'),
  doneList: $('done-list'),
  doneEmpty: $('done-empty'),

  nTodo: $('n-todo'),
  nWant: $('n-want'),
  nBin: $('n-bin'),
  nDone: $('n-done'),
  stats: $('stats'),
  demoTab: $('demo-tag'),

  sOpacity: $('s-opacity'),
  vOpacity: $('v-opacity'),
  sFade: $('s-fade'),
  vFade: $('v-fade'),
  sBin: $('s-bin'),
  vBin: $('v-bin'),
};

// 展开尺寸要和 main.js 的 PANEL_W / PANEL_H 保持一致
const EXPANDED = { w: 340, h: 500 };
const COLLAPSED = { w: 190, h: 44 };

let state = null;
let quickRoom = 'todo';
let collapsed = false;
/** 'main' | 'bin' | 'set' */
let view = 'main';
/** 拖动透明度滑块时的本地预览值；null = 用已保存的值 */
let opacityPreview = null;

// ---------------------------------------------------------------- 工具

const esc = (s) =>
  String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));

function relTime(ts) {
  const m = Math.floor((Date.now() - ts) / 60000);
  if (m < 1) return '刚刚';
  if (m < 60) return `${m} 分钟前`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h} 小时前`;
  const d = Math.floor(h / 24);
  if (d < 30) return `${d} 天前`;
  return new Date(ts).toLocaleDateString('zh-CN');
}

const imgSrc = (item) => (item.image ? `appimg://local/${item.image}` : null);

/** 标题：没有就是空字符串 —— 界面上不写「（没有标题）」那种占位文字 */
function titleOf(item) {
  return (item.title || '').trim();
}

/** 没有截图时，用标题首字做占位 */
function firstChar(s) {
  const t = String(s || '').trim();
  return t ? Array.from(t)[0] : '·';
}

/** 磁贴副标题：来源 · 标题，都没有就退回时间 */
function shortSrc(item) {
  const parts = [item.source, titleOf(item)].filter(Boolean);
  return parts.length ? parts.join(' · ') : relTime(item.createdAt);
}

// ---------------------------------------------------------------- 主题

function applyTheme() {
  if (!state) return;
  const saved = state.settings && typeof state.settings.panelOpacity === 'number'
    ? state.settings.panelOpacity
    : 0.8;
  const a = opacityPreview == null ? saved : opacityPreview;
  document.documentElement.style.setProperty('--panel-alpha', String(a));
  els.vOpacity.textContent = `${Math.round(a * 100)}%`;
}

// ---------------------------------------------------------------- 渲染

function render() {
  if (!state) return;
  const items = state.items;

  // Todo 列表：排除纸篓，也排除「已完成并收走」的
  // （已勾选但还没收走的仍留在原地，方便反悔）
  const todo = items
    .filter((i) => i.room === 'todo' && i.state !== 'binned' && !i.filedAt)
    .sort((a, b) => {
      if (a.state !== b.state) return a.state === 'done' ? 1 : -1;
      const rank = (x) => (x.dueBucket === 'today' ? 0 : x.dueBucket === 'soon' ? 1 : 2);
      if (rank(a) !== rank(b)) return rank(a) - rank(b);
      return a.createdAt - b.createdAt;
    });

  const want = items
    .filter((i) => i.room === 'want' && i.state === 'active')
    .sort((a, b) => b.lastTouchedAt - a.lastTouchedAt);

  const bin = items
    .filter((i) => i.state === 'binned')
    .sort((a, b) => (b.binnedAt || 0) - (a.binnedAt || 0));

  // 完成列表：只有「已完成并已收走」的才进来
  // （已勾选但还没收走的还在 Todo 列表原地，避免同时出现在两个地方）
  const done = items
    .filter((i) => i.state === 'done' && i.doneAt && i.filedAt)
    .sort((a, b) => b.doneAt - a.doneAt);

  els.nTodo.textContent = todo.filter((i) => i.state === 'active').length;
  els.nWant.textContent = want.length;
  els.nBin.textContent = bin.length;
  els.nDone.textContent = done.length;

  // --- Todo ---
  els.todoList.innerHTML = todo.length
    ? todo.map(todoCard).join('')
    : `<div class="empty">还没有 Todo。<br /><span>按 Ctrl+Shift+A 截一张，选 Today 或 Soon。</span></div>`;

  // --- Wantdo ---
  // 空的时候要把 list 也藏掉，否则它会和空态各占一半高度
  els.wantList.innerHTML = want.map(wantCard).join('');
  els.wantList.classList.toggle('hide', want.length === 0);
  els.wantEmpty.classList.toggle('hide', want.length > 0);

  // --- Paper Plane Bin ---
  els.binList.innerHTML = bin.map(binCard).join('');
  els.binList.classList.toggle('hide', bin.length === 0);
  els.binEmpty.classList.toggle('hide', bin.length > 0);

  // --- Done 页（按完成日期分组）---
  els.doneList.innerHTML = donePage(done);
  els.doneList.classList.toggle('hide', done.length === 0);
  els.doneEmpty.classList.toggle('hide', done.length > 0);

  // --- 底栏 ---
  const s = state.stats || {};
  els.stats.textContent = `本周完成 ${s.doneThisWeek || 0} 件 · 纸篓里 ${bin.length} 个想法`;

  const demoOn = !!(state.settings && state.settings.demoFastDecay);
  els.demoTab.classList.toggle('hide', !demoOn);
  $('b-demo').classList.toggle('on', demoOn);
  $('b-set').classList.toggle('on', view === 'set');

  els.viewMain.classList.toggle('hide', view !== 'main');
  els.viewBin.classList.toggle('hide', view !== 'bin');
  els.viewDone.classList.toggle('hide', view !== 'done');
  els.viewSet.classList.toggle('hide', view !== 'set');

  // --- 设置页回显 ---
  if (view === 'set') {
    const cfg = state.decayCfg || {};
    const fade = cfg.fadeStartDays != null ? cfg.fadeStartDays : 7;
    const binDays = cfg.binAfterDays != null ? cfg.binAfterDays : 21;
    els.sFade.value = String(fade);
    els.vFade.textContent = `${fade} 天`;
    els.sBin.value = String(binDays);
    els.vBin.textContent = `${binDays} 天`;
    if (opacityPreview == null) {
      els.sOpacity.value = String(Math.round((state.settings.panelOpacity ?? 0.8) * 100));
    }
    // 「灌入演示数据」只在还没有任何条目时出现
    $('seed-row').classList.toggle('hide', items.length > 0);
  }

  applyTheme();
}

function todoCard(it) {
  const thumb = imgSrc(it);
  const done = it.state === 'done';
  const title = titleOf(it);

  const pills = [];
  if (it.dueBucket === 'today') {
    pills.push(it.overdue ? `<span class="pill bad">逾期</span>` : `<span class="pill today">Today</span>`);
  } else if (it.dueBucket === 'soon') {
    pills.push(it.overdue ? `<span class="pill bad">逾期</span>` : `<span class="pill soon">Soon</span>`);
  }
  // 没标题时，把来源放到 meta 里，卡片才不至于只剩一个时间
  const metaBits = [pills.join('')];
  if (!title && it.source) metaBits.push(`<span>${esc(it.source)}</span>`);
  metaBits.push(`<span>${relTime(it.createdAt)}</span>`);

  return `
    <div class="card ${done ? 'done' : ''} ${it.overdue && !done ? 'overdue' : ''}"
         data-card-id="${it.id}" data-act="open" data-id="${it.id}"
         title="${done ? '再点一下，让它散掉' : '点开看看'}">
      <button class="chk" data-act="done" data-id="${it.id}">✓</button>
      <div class="c-body">
        ${title ? `<div class="c-title">${esc(title)}</div>` : ''}
        <div class="c-meta">${metaBits.join('')}</div>
      </div>
      ${thumb ? `<img class="c-thumb" src="${esc(thumb)}" alt="" />` : ''}
      <button class="x" data-act="bin" data-id="${it.id}" title="丢进纸篓">✕</button>
    </div>`;
}

/** Wantdo：一张图 = 一个想法。大图在上，来源 + 生命条在下。 */
function wantCard(it) {
  const thumb = imgSrc(it);
  const op = typeof it.opacity === 'number' ? it.opacity : 1;
  const life = Math.round((typeof it.life === 'number' ? it.life : 1) * 100);
  const left = typeof it.daysLeft === 'number' ? it.daysLeft : null;

  const hint =
    life <= 32 && left != null ? `<div class="want-fading">⏳ 还能撑 ${left.toFixed(1)} 天</div>` : '';

  const face = thumb
    ? `<img class="want-img" src="${esc(thumb)}" alt="" />`
    : `<div class="want-img-ph">${esc(firstChar(titleOf(it)))}</div>`;

  return `
    <div class="want" style="opacity:${op.toFixed(3)}" data-card-id="${it.id}" data-act="open" data-id="${it.id}" title="点一下 = 看它一眼 = 续命">
      <button class="want-x" data-act="bin" data-id="${it.id}" title="放下它">✕</button>
      ${face}
      <div class="want-foot">
        <div class="want-src">${esc(shortSrc(it))}</div>
        <div class="life"><i style="width:${Math.max(0, Math.min(100, life))}%"></i></div>
        ${hint}
      </div>
    </div>`;
}

function binCard(it) {
  const thumb = imgSrc(it);
  const face = thumb
    ? `<img class="want-img" src="${esc(thumb)}" alt="" />`
    : `<div class="want-img-ph">${esc(firstChar(titleOf(it)))}</div>`;
  const chip = it.room === 'want' ? 'chip-want' : 'chip-todo';
  const roomName = it.room === 'want' ? 'Wantdo' : 'Todo';

  return `
    <div class="bin-card" data-card-id="${it.id}" data-act="restore" data-id="${it.id}" title="捡回来">
      ${face}
      <div class="want-foot">
        <div class="want-src"><span class="chip ${chip}">${roomName}</span> ${esc(shortSrc(it))}</div>
      </div>
      <div class="bin-pick">捡回来</div>
    </div>`;
}

// ---------------------------------------------------------------- Done 页

/** 把「完成于哪一天」变成人话 */
function dayLabel(ts) {
  const d = new Date(ts);
  const today = new Date();
  const startOf = (x) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  const diff = Math.round((startOf(today) - startOf(d)) / 86400000);
  if (diff === 0) return '今天';
  if (diff === 1) return '昨天';
  if (diff === 2) return '前天';
  const wd = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'][d.getDay()];
  return `${d.getMonth() + 1} 月 ${d.getDate()} 日 · ${wd}`;
}

const dayKey = (ts) => {
  const d = new Date(ts);
  return `${d.getFullYear()}-${d.getMonth() + 1}-${d.getDate()}`;
};

/** 按完成日期分组，组内按时间倒序 */
function donePage(done) {
  if (!done.length) return '';

  const groups = [];
  const map = new Map();
  for (const it of done) {
    const k = dayKey(it.doneAt);
    if (!map.has(k)) {
      const g = { key: k, ts: it.doneAt, items: [] };
      map.set(k, g);
      groups.push(g);
    }
    map.get(k).items.push(it);
  }

  return groups
    .map(
      (g) => `
      <div class="day-h">${esc(dayLabel(g.ts))}<em>${g.items.length}</em></div>
      ${g.items.map(doneItem).join('')}`
    )
    .join('');
}

function doneItem(it) {
  const thumb = imgSrc(it);
  const face = thumb
    ? `<img class="done-thumb" src="${esc(thumb)}" alt="" />`
    : `<div class="done-thumb-ph">${esc(firstChar(titleOf(it)))}</div>`;

  const chip = it.room === 'want' ? 'chip-want' : 'chip-todo';
  const roomName = it.room === 'want' ? 'Wantdo' : 'Todo';
  const t = new Date(it.doneAt);
  const hhmm = `${String(t.getHours()).padStart(2, '0')}:${String(t.getMinutes()).padStart(2, '0')}`;

  // 完成时长是一等信息，用户明确要求显示
  const meta = [`用时 ${fmtDur(it.elapsedMs)}`, `${hhmm} 完成`];

  return `
    <div class="done-item" data-act="openDone" data-id="${it.id}" title="看当时的心得与小结">
      ${face}
      <div class="done-body">
        <div class="done-top">
          <span class="chip ${chip}">${roomName}</span>
          <span class="done-title">${esc(titleOf(it) || it.source || '（没有标题）')}</span>
        </div>
        <div class="done-meta">${meta.map(esc).join('<span>·</span>')}</div>
      </div>
    </div>`;
}

/** 时长：人话版（和 record 页保持一致） */
function fmtDur(ms) {
  if (ms == null || !isFinite(ms) || ms < 0) return '—';
  const s = Math.floor(ms / 1000);
  if (s < 60) return `${s} 秒`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m} 分钟`;
  const h = Math.floor(m / 60);
  if (h < 24) return m % 60 ? `${h} 小时 ${m % 60} 分` : `${h} 小时`;
  const d = Math.floor(h / 24);
  if (d < 30) return h % 24 ? `${d} 天 ${h % 24} 小时` : `${d} 天`;
  const mo = Math.floor(d / 30);
  return d % 30 ? `${mo} 个月 ${d % 30} 天` : `${mo} 个月`;
}

// ---------------------------------------------------------------- 悬停预览

// 停够这么久才弹预览 —— 太短会在扫视时乱闪，太长又等不及
const HOVER_DELAY = 550;
let hoverTimer = null;
let hoverId = null;

function cancelHover() {
  if (hoverTimer) {
    clearTimeout(hoverTimer);
    hoverTimer = null;
  }
  if (hoverId) {
    window.api.previewHide();
    hoverId = null;
  }
}

document.addEventListener('mouseover', (e) => {
  const card = e.target.closest && e.target.closest('[data-card-id]');
  if (!card) return cancelHover();

  const id = card.dataset.cardId;
  if (id === hoverId) return;

  cancelHover();

  // 没有图就没什么好预览的
  if (!card.querySelector('.want-img, .c-thumb')) return;

  hoverTimer = setTimeout(() => {
    hoverTimer = null;
    hoverId = id;
    const r = card.getBoundingClientRect();
    window.api.previewShow(id, Math.round(r.top), Math.round(r.height));
  }, HOVER_DELAY);
});

document.addEventListener('mouseout', (e) => {
  const card = e.target.closest && e.target.closest('[data-card-id]');
  if (!card) return;
  // 在卡片内部移动不算离开
  if (e.relatedTarget && card.contains(e.relatedTarget)) return;
  cancelHover();
});

// 滚动、点击、鼠标离开窗口、面板失焦 —— 都要收掉预览
document.addEventListener('scroll', cancelHover, true);
document.addEventListener('click', cancelHover, true);
document.addEventListener('mouseleave', cancelHover);
window.addEventListener('blur', cancelHover);

// ---------------------------------------------------------------- 事件

/** 绑事件的安全版本：元素不存在就跳过，不让一个笔误把整个面板搞崩 */
const on = (el, ev, fn) => {
  if (el) el.addEventListener(ev, fn);
};

const itemById = (id) => (state && state.items.find((i) => i.id === id)) || null;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

document.addEventListener('click', async (e) => {
  const el = e.target.closest('[data-act]');
  if (!el) return;
  const { act, id } = el.dataset;
  e.stopPropagation();

  // 勾选框：只标记完成，先不收走 —— 留在原地让你能反悔，
  // 再点一下卡片才真的消散进完成列表
  if (act === 'done') {
    await window.api.done({ id, file: false });
    return;
  }

  if (act === 'bin') {
    await window.api.bin(id);
    return;
  }

  if (act === 'restore') {
    await window.api.restore(id);
    return;
  }

  // 完成列表里点一条 → 看当时的心得与小结
  if (act === 'openDone') {
    await window.api.recordOpen(id, 'view');
    return;
  }

  if (act === 'open') {
    const it = itemById(id);

    // 已完成的 Todo 再点一下 → 轻轻化掉 → 收进完成列表
    if (it && it.room === 'todo' && it.state === 'done') {
      const card = document.querySelector(`.card[data-card-id="${id}"]`);
      if (card) {
        // 先把真实高度写成内联值，动画才能从「实际高度」精确收到 0
        card.style.height = `${card.getBoundingClientRect().height}px`;
        card.classList.add('dissolve');
      }
      await sleep(320);
      await window.api.file(id);
      return;
    }

    await window.api.openDetail(id);
  }
});

on($('b-snip'), 'click', () => window.api.startCapture());

on($('b-add'), 'click', () => {
  const hidden = els.quick.classList.toggle('hide');
  if (!hidden) els.quickInput.focus();
});

on($('b-demo'), 'click', async () => {
  const cur = !!(state && state.settings && state.settings.demoFastDecay);
  await window.api.setSetting('demoFastDecay', !cur);
});

on($('b-quit'), 'click', () => window.api.quit());

on($('b-bin'), 'click', () => {
  view = 'bin';
  render();
});
on($('b-back'), 'click', () => {
  view = 'main';
  render();
});
on($('b-done'), 'click', () => {
  view = 'done';
  render();
});
on($('b-back-done'), 'click', () => {
  view = 'main';
  render();
});
on($('b-set'), 'click', () => {
  view = view === 'set' ? 'main' : 'set';
  opacityPreview = null;
  render();
});
on($('b-back-set'), 'click', () => {
  view = 'main';
  opacityPreview = null;
  render();
});

on($('b-collapse'), 'click', async () => {
  collapsed = !collapsed;
  document.body.classList.toggle('collapsed', collapsed);
  $('b-collapse').textContent = collapsed ? '▸' : '▾';
  const t = collapsed ? COLLAPSED : EXPANDED;
  await window.api.resize(t.w, t.h);
});

// ---- 设置页 ----

// 透明度：拖动时实时预览（不写盘），松手才保存
on(els.sOpacity, 'input', () => {
  opacityPreview = Number(els.sOpacity.value) / 100;
  applyTheme();
});
on(els.sOpacity, 'change', async () => {
  const v = Number(els.sOpacity.value) / 100;
  opacityPreview = null;
  await window.api.setSetting('panelOpacity', v);
});

function currentDecayInput() {
  return {
    fadeStartDays: Number(els.sFade.value),
    // 「放手」必须晚于「保护期」
    binAfterDays: Math.max(Number(els.sBin.value), Number(els.sFade.value) + 1),
  };
}

on(els.sFade, 'input', () => {
  els.vFade.textContent = `${els.sFade.value} 天`;
  // 保护期超过放手期时，把放手期顶上去
  if (Number(els.sBin.value) <= Number(els.sFade.value)) {
    els.sBin.value = String(Number(els.sFade.value) + 1);
    els.vBin.textContent = `${els.sBin.value} 天`;
  }
});
on(els.sBin, 'input', () => {
  els.vBin.textContent = `${els.sBin.value} 天`;
});

async function saveDecay() {
  const { fadeStartDays, binAfterDays } = currentDecayInput();
  // fadeEndDays 跟着 binAfterDays 走：一直淡到进纸篓那一刻
  await window.api.setSetting('decay', {
    fadeStartDays,
    fadeEndDays: binAfterDays,
    binAfterDays,
  });
}
on(els.sFade, 'change', saveDecay);
on(els.sBin, 'change', saveDecay);

on($('b-seed'), 'click', async () => {
  await window.api.seedDemo();
});

els.quickRooms.forEach((b) => {
  b.addEventListener('click', () => {
    quickRoom = b.dataset.room;
    els.quickRooms.forEach((x) => x.classList.toggle('on', x === b));
  });
});

on(els.quickInput, 'keydown', async (e) => {
  if (e.key === 'Escape') {
    els.quick.classList.add('hide');
    return;
  }
  if (e.key !== 'Enter') return;
  const v = els.quickInput.value.trim();
  if (!v) return;
  await window.api.addManual({ room: quickRoom, title: v });
  els.quickInput.value = '';
});

on(document.querySelector('.hdr'), 'dblclick', () => {
  if (collapsed) $('b-collapse').click();
});

window.api.onState((s) => {
  state = s;
  render();
});

window.api.getState().then((s) => {
  state = s;
  render();
  window.api.ping('panel-loaded');
});
