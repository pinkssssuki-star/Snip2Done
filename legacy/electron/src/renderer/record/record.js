'use strict';

const $ = (id) => document.getElementById(id);

const ROOM_LABEL = { todo: 'Todo', want: 'Wantdo' };

let current = null;
let mode = 'view';

// ---------------------------------------------------------------- 工具

const esc = (s) =>
  String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));

/** 时长：人话版 */
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

function fmtTime(ts) {
  if (!ts) return '—';
  const d = new Date(ts);
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(
    d.getMinutes()
  )}`;
}

function relTime(ts) {
  if (!ts) return '—';
  const m = Math.floor((Date.now() - ts) / 60000);
  if (m < 1) return '刚刚';
  if (m < 60) return `${m} 分钟前`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h} 小时前`;
  const d = Math.floor(h / 24);
  if (d < 30) return `${d} 天前`;
  return new Date(ts).toLocaleDateString('zh-CN');
}

const thumbHtml = (item, cls) =>
  item.image
    ? `<img class="${cls}" src="appimg://local/${encodeURI(item.image)}" alt="" />`
    : `<div class="thumb-ph">${esc((item.title || '·').trim().charAt(0) || '·')}</div>`;

// ---------------------------------------------------------------- 渲染

function render(item, m) {
  current = item;
  mode = m;

  $('tag').textContent = ROOM_LABEL[item.room] || item.room;
  $('tag').className = 'tag' + (item.room === 'todo' ? ' todo' : '');
  $('ttl').textContent = item.title || item.source || '（没有标题）';

  m === 'write' ? renderWrite(item) : renderView(item);
}

/** 还没完成：写心得，然后 Get Done */
function renderWrite(item) {
  $('body').innerHTML = `
    <div class="top">
      ${thumbHtml(item, 'thumb')}
      <div class="facts">
        <div class="fact"><b>记于</b><span>${fmtTime(item.createdAt)}</span></div>
        <div class="fact"><b>到目前为止</b><span>${fmtDur(item.elapsedMs)}</span></div>
        <div class="fact"><b>你看过它</b><span>${item.touchCount || 0} 次</span></div>
      </div>
    </div>

    <div class="sec-lbl">完成它之前，写点什么</div>
    <textarea id="reflection" placeholder="为什么想做它？做完／看完了有什么感觉？不用长，一两句就行。"></textarea>
  `;

  $('foot').innerHTML = `
    <span class="counter" id="counter">0 字</span>
    <span class="sp"></span>
    <button class="btn btn-ghost" id="cancel">取消</button>
    <button class="btn btn-go" id="go">Get Done</button>
  `;

  const ta = $('reflection');
  ta.value = item.reflection || '';
  const upd = () => ($('counter').textContent = `${ta.value.trim().length} 字`);
  ta.addEventListener('input', upd);
  upd();
  ta.focus();

  $('cancel').addEventListener('click', () => window.rec.close());
  $('go').addEventListener('click', commit);
  ta.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) commit();
  });
}

async function commit() {
  if (!current) return;
  const ta = $('reflection');
  await window.rec.commit(current.id, ta ? ta.value.trim() : '');
}

/** 已完成：回看心得 + 自动小结 */
function renderView(item) {
  const isWant = item.room === 'want';
  const days = item.elapsedMs / 86400000;

  // 一句话总结 —— 比表格更有温度，这是「小小的总结」的主角
  let line;
  if (isWant) {
    const bits = [`你把它记了 <b>${fmtDur(item.elapsedMs)}</b>`];
    if (item.touchCount) bits.push(`看了 <b>${item.touchCount}</b> 次`);
    if (item.reflectionChars) bits.push(`最后写下 <b>${item.reflectionChars}</b> 个字`);
    line = bits.join('，') + '。';
  } else {
    const onTime = item.doneAt && item.dueAt ? item.doneAt <= item.dueAt : null;
    line =
      `这件事你花了 <b>${fmtDur(item.elapsedMs)}</b> 处理完` +
      (onTime === true
        ? '，<b>赶上了期限</b>。'
        : onTime === false
        ? '，虽然<b>晚了</b>，但总算做完了。'
        : '。');
  }

  const stats = [];
  stats.push(
    `<div class="stat"><span class="k">从记下到完成</span><span class="v">${fmtDur(
      item.elapsedMs
    )}</span></div>`
  );
  stats.push(
    `<div class="stat"><span class="k">完成于</span><span class="v" style="font-size:11.5px">${fmtTime(
      item.doneAt
    )}</span></div>`
  );

  if (isWant) {
    stats.push(
      `<div class="stat"><span class="k">你看过它</span><span class="v">${
        item.touchCount || 0
      }<small>次</small></span></div>`
    );
    stats.push(
      `<div class="stat"><span class="k">最后一次看它</span><span class="v" style="font-size:11.5px">${relTime(
        item.lastTouchedAt
      )}</span></div>`
    );
    if (item.reflectionChars) {
      stats.push(
        `<div class="stat"><span class="k">心得</span><span class="v">${item.reflectionChars}<small>字</small></span></div>`
      );
    }
    if (item.touchCount > 0 && days > 0.5) {
      stats.push(
        `<div class="stat"><span class="k">平均多久看一次</span><span class="v">${fmtDur(
          item.elapsedMs / item.touchCount
        )}</span></div>`
      );
    }
    // 只在这条真的进过纸篓时才提 —— 不做成失败，就是一段经历
    if (item.binCount > 0) {
      stats.push(
        `<div class="stat"><span class="k">在纸篓里待过</span><span class="v">${fmtDur(
          item.binMs
        )}<small>${item.binCount} 次</small></span></div>`
      );
    }
  }

  const reflectionBlock = isWant
    ? `
      <div class="sec-lbl">心得</div>
      ${
        item.reflection
          ? `<div class="quote">${esc(item.reflection)}</div>`
          : `<div class="quote empty">当时没写下什么。也没关系。</div>`
      }`
    : '';

  $('body').innerHTML = `
    <div class="top">
      ${thumbHtml(item, 'thumb')}
      <div class="facts">
        <div class="fact"><b>来源</b><span>${esc(item.source || '未知')}</span></div>
        <div class="fact"><b>记于</b><span>${fmtTime(item.createdAt)}</span></div>
        <div class="fact"><b>完成于</b><span>${fmtTime(item.doneAt)}</span></div>
      </div>
    </div>

    <div class="summary">${line}</div>

    ${reflectionBlock}

    <div class="sec-lbl">小结</div>
    <div class="stats">${stats.join('')}</div>
  `;

  $('foot').innerHTML = `
    <span class="counter">${isWant ? '这是你曾经想要的东西' : '这件事已经了结'}</span>
    <span class="sp"></span>
    <button class="btn btn-ghost" id="done-close">关闭</button>
  `;
  $('done-close').addEventListener('click', () => window.rec.close());
}

// ---------------------------------------------------------------- 事件

window.rec.onInit(({ item, mode: m }) => render(item, m));

$('close').addEventListener('click', () => window.rec.close());

window.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') window.rec.close();
});
