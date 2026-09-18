'use strict';

const $ = (id) => document.getElementById(id);

const stage = $('stage');
const titleEl = $('title');
const noteEl = $('note');
const savedEl = $('saved');

const ROOM_LABEL = { todo: 'Todo', want: 'Wantdo' };

let currentId = null;
/** 最近一次收到的条目，Get Done 时用来判断房间 */
let lastItem = null;
let saveTimer = null;

function fmt(ts) {
  if (!ts) return '—';
  const d = new Date(ts);
  const pad = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(
    d.getMinutes()
  )}`;
}

function flashSaved() {
  savedEl.classList.add('on');
  clearTimeout(flashSaved._t);
  flashSaved._t = setTimeout(() => savedEl.classList.remove('on'), 1200);
}

/** 立即写盘（失去焦点 / 关窗前用） */
function saveNow() {
  if (!currentId) return;
  clearTimeout(saveTimer);
  saveTimer = null;
  window.api.update(currentId, { title: titleEl.value, note: noteEl.value });
  flashSaved();
}

/** 打字时防抖保存，不用手动点保存 */
function saveSoon() {
  if (!currentId) return;
  clearTimeout(saveTimer);
  saveTimer = setTimeout(saveNow, 600);
}

window.api.onDetail(({ item }) => {
  if (!item) return;
  currentId = item.id;
  lastItem = item;

  $('tag').textContent = ROOM_LABEL[item.room] || item.room;
  $('src').textContent = item.source || '未知';
  $('when').textContent = fmt(item.createdAt);

  // 没有标题就是空的 —— 不写「（没有标题）」那种占位文字
  titleEl.value = item.title || '';
  noteEl.value = item.note || '';

  const lifeRow = $('life-row');
  if (item.room === 'want') {
    lifeRow.style.display = '';
    const pct = Math.round(Math.min(1, Math.max(0, item.life || 0)) * 100);
    $('life').textContent = `${pct}%　（被碰过 ${item.touchCount || 0} 次）`;
  } else {
    lifeRow.style.display = 'none';
  }

  // Get Done：Wantdo 会先让你写一句心得，Todo 直接完成
  const isWant = item.room === 'want';
  $('get-done').classList.toggle('hide', item.state === 'done');
  $('done-hint').textContent =
    item.state === 'done' ? '这条已经完成了' : isWant ? '完成时会让你写一句心得' : '';

  stage.innerHTML = item.image
    ? `<img src="appimg://local/${encodeURI(item.image)}" alt="" />`
    : '<div class="none">这条没有截图</div>';
});

// ---- Get Done ----
$('get-done').addEventListener('click', async () => {
  if (!currentId) return;
  saveNow();

  const it = lastItem;
  if (it && it.room === 'want') {
    // 先写心得，再算完成
    await window.api.recordOpen(currentId, 'write');
    return;
  }

  // Todo：直接完成并收走
  window.api.done({ id: currentId, file: true }).then(() => window.close());
});

titleEl.addEventListener('input', saveSoon);
noteEl.addEventListener('input', saveSoon);
titleEl.addEventListener('blur', saveNow);
noteEl.addEventListener('blur', saveNow);

// 关窗前兜底保存一次
window.addEventListener('beforeunload', () => {
  if (currentId) window.api.update(currentId, { title: titleEl.value, note: noteEl.value });
});

$('close').addEventListener('click', () => {
  saveNow();
  window.close();
});

window.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    saveNow();
    window.close();
  }
  // Ctrl+Enter / Cmd+Enter 也当保存并关闭
  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
    saveNow();
    window.close();
  }
});
