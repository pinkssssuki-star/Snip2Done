'use strict';

/**
 * 衰减与存储的单元测试。零依赖，直接 `node test/decay.test.js` 就能跑。
 * 这两块是整个产品里唯一「逻辑性」的部分，其余都是壳。
 *
 * 注意：所有时间量都从 decay.DEFAULT_DECAY 推导，不写死天数 ——
 * 这样以后调参数（一定会调）不会把测试调坏。
 */

const assert = require('assert');
const fs = require('fs');
const path = require('path');
const os = require('os');

const decay = require('../src/shared/decay');
const { Store } = require('../src/main/store');

const DAY = decay.DAY;
const CFG = decay.DEFAULT_DECAY;
const SCALE = decay.DEMO_TIME_SCALE;

/** 演示模式下，想模拟「虚拟 n 天」需要经过多少真实毫秒 */
const realMsFor = (virtualDays, scale = SCALE) => (virtualDays * DAY) / scale;

let passed = 0;

function ok(name, fn) {
  try {
    fn();
    passed++;
    console.log(`  ✓ ${name}`);
  } catch (e) {
    console.error(`  ✗ ${name}\n    ${e.message}`);
    process.exitCode = 1;
  }
}

const ago = (days) => Date.now() - days * DAY;

console.log(`\n衰减算法（fadeStart=${CFG.fadeStartDays}d / fadeEnd=${CFG.fadeEndDays}d / bin=${CFG.binAfterDays}d）`);

ok('刚碰过 = 满血', () => {
  const now = Date.now();
  const it = { lastTouchedAt: now };
  assert.strictEqual(decay.opacityFor(it, now), 1);
  assert.strictEqual(decay.lifeFor(it, now), 1);
});

ok('保护期内完全不衰减', () => {
  const now = Date.now();
  const it = { lastTouchedAt: ago(CFG.fadeStartDays * 0.5) };
  assert.strictEqual(decay.opacityFor(it, now), 1);
});

ok('中间区间线性变淡', () => {
  const now = Date.now();
  const mid = (CFG.fadeStartDays + CFG.fadeEndDays) / 2;
  const op = decay.opacityFor({ lastTouchedAt: ago(mid) }, now);
  assert.ok(op > CFG.minOpacity && op < 1, `期望 0~1 之间，实际 ${op}`);
});

ok('超过 fadeEndDays 降到 minOpacity', () => {
  const now = Date.now();
  const op = decay.opacityFor({ lastTouchedAt: ago(CFG.fadeEndDays + 1) }, now);
  assert.strictEqual(op, CFG.minOpacity);
});

ok('超过 binAfterDays 才该进纸篓', () => {
  const now = Date.now();
  assert.strictEqual(decay.shouldBin({ lastTouchedAt: ago(CFG.binAfterDays + 1) }, now), true);
  assert.strictEqual(decay.shouldBin({ lastTouchedAt: ago(CFG.binAfterDays * 0.3) }, now), false);
});

ok('真实使用下：一周不碰还在，三周后才放手', () => {
  const now = Date.now();
  const sixDays = { lastTouchedAt: ago(6) };
  assert.strictEqual(decay.opacityFor(sixDays, now), 1, '第 6 天不该有任何变化');
  assert.strictEqual(decay.shouldBin(sixDays, now), false);

  const threeWeeks = { lastTouchedAt: ago(22) };
  assert.strictEqual(decay.shouldBin(threeWeeks, now), true, '第 22 天该进纸篓了');
});

ok('演示模式：约 5 分钟跑完一个完整周期', () => {
  const now = Date.now();

  // 刚截的：满血
  assert.strictEqual(
    decay.opacityFor({ lastTouchedAt: now - 5_000 }, now, CFG, SCALE),
    1,
    '5 秒（≈0.35 虚拟天）应仍是满血'
  );

  // 越过保护期：开始变淡
  const faded = decay.opacityFor(
    { lastTouchedAt: now - realMsFor(CFG.fadeStartDays + 4) },
    now,
    CFG,
    SCALE
  );
  assert.ok(faded < 1, `越过保护期后应变淡，实际 ${faded}`);

  // 到点：进纸篓
  assert.strictEqual(
    decay.shouldBin({ lastTouchedAt: now - realMsFor(CFG.binAfterDays + 1) }, now, CFG, SCALE),
    true
  );

  // 完整性检查：整套周期应该在 6 分钟以内，否则演示太慢
  const cycleMs = realMsFor(CFG.binAfterDays);
  assert.ok(cycleMs < 6 * 60 * 1000, `演示周期 ${Math.round(cycleMs / 1000)}s 太长了`);
});

ok('时间越久，生命值单调下降', () => {
  const now = Date.now();
  let prev = Infinity;
  for (const f of [0, 0.2, 0.5, 0.8, 1]) {
    const life = decay.lifeFor({ lastTouchedAt: ago(CFG.binAfterDays * f) }, now);
    assert.ok(life <= prev, `f=${f} 时生命值不应高于上一档`);
    prev = life;
  }
});

console.log('\n存储与衰减巡检');

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'remeber-test-'));

ok('Want 超期后被 sweep 送进纸篓', () => {
  const s = new Store(tmp);
  const it = s.add({ room: 'want', title: '学吉他' });
  it.lastTouchedAt = ago(CFG.binAfterDays + 3);
  s.save();

  const binned = s.sweep();
  assert.strictEqual(binned, 1, '应该正好送走 1 条');
  assert.strictEqual(s.get(it.id).state, 'binned');
});

ok('Todo 永远不会被衰减送走', () => {
  const s = new Store(tmp);
  const it = s.add({ room: 'todo', title: '交作业', dueBucket: 'today' });
  it.lastTouchedAt = ago(365);
  s.save();

  s.sweep();
  assert.strictEqual(s.get(it.id).state, 'active', 'Todo 不该被淡出');
});

ok('碰一下 = 续命', () => {
  const s = new Store(tmp);
  const it = s.add({ room: 'want', title: '看那部纪录片' });
  // 先把它变老到「快到保质期」
  it.lastTouchedAt = ago(CFG.binAfterDays - 2);
  s.save();
  assert.ok(decay.lifeFor(s.get(it.id), Date.now()) < 0.2, '前置条件：应该已经很虚了');

  s.touch(it.id);
  const after = s.get(it.id);
  assert.strictEqual(after.touchCount, 1);
  // 时间总是往前走的，生命值只会无限接近 1，所以用容差比较
  assert.ok(decay.lifeFor(after, Date.now()) > 0.999, '续命后生命值应基本回满');
});

ok('纸篓里的能捡回来，且满血复活', () => {
  const s = new Store(tmp);
  const it = s.add({ room: 'want', title: '想去的展' });
  s.bin(it.id);
  assert.strictEqual(s.get(it.id).state, 'binned');

  s.restore(it.id);
  const back = s.get(it.id);
  assert.strictEqual(back.state, 'active');
  assert.ok(decay.lifeFor(back, Date.now()) > 0.999, '捡回来应满血复活');
});

ok('Todo 的今天档会给一个当天截止时间', () => {
  const s = new Store(tmp);
  const it = s.add({ room: 'todo', title: '改简历', dueBucket: 'today' });
  assert.ok(it.dueAt > Date.now(), 'dueAt 应该在未来');
  assert.ok(it.dueAt - Date.now() <= DAY, 'dueAt 应该在 24 小时内');
});

ok('Want 没有截止时间', () => {
  const s = new Store(tmp);
  assert.strictEqual(s.add({ room: 'want', title: '随便想想' }).dueAt, null);
});

ok('snapshot 会把衰减字段算好给 UI', () => {
  const s = new Store(tmp);
  const it = s.add({ room: 'want', title: '快淡出的想法' });
  it.lastTouchedAt = ago((CFG.fadeStartDays + CFG.fadeEndDays) / 2);
  s.save();

  const row = s.snapshot().items.find((i) => i.id === it.id);
  assert.ok(typeof row.opacity === 'number' && row.opacity < 1, 'opacity 应为已衰减的小数');
  assert.ok(typeof row.life === 'number', 'life 应存在');
  assert.ok(typeof row.daysLeft === 'number', 'daysLeft 应存在');
});

ok('Todo 的衰减字段不参与计算（opacity 恒为 1）', () => {
  const s = new Store(tmp);
  const it = s.add({ room: 'todo', title: '老任务' });
  it.lastTouchedAt = ago(100);
  s.save();

  const row = s.snapshot().items.find((i) => i.id === it.id);
  assert.strictEqual(row.opacity, 1, 'Todo 不该变淡');
});

console.log('\n完成 / 纸篓：两条链路不能混');

ok('完成 → 进完成列表，不进纸篓', () => {
  const s = new Store(tmp);
  const it = s.add({ room: 'todo', title: '写周报' });
  s.done(it.id);

  const after = s.get(it.id);
  assert.strictEqual(after.state, 'done', '应该是 done');
  assert.ok(after.doneAt, 'doneAt 应该有值');
  assert.ok(after.filedAt, '默认应该已经从主面板收走');
  assert.strictEqual(after.binnedAt, null, '不该有纸篓时间');
});

ok('刚勾上还能反悔（撤销完成）', () => {
  const s = new Store(tmp);
  const it = s.add({ room: 'todo', title: '可撤销' });

  // file:false 是「勾选框」用的：只标记完成，先留在列表里
  s.done(it.id, { file: false });
  const marked = s.get(it.id);
  assert.strictEqual(marked.state, 'done');
  assert.strictEqual(marked.filedAt, null, 'file:false 时不该被收走');

  s.done(it.id); // 再点一次 = 撤销
  const back = s.get(it.id);
  assert.strictEqual(back.state, 'active');
  assert.strictEqual(back.doneAt, null);
});

ok('收走之后再点不会误撤销', () => {
  const s = new Store(tmp);
  const it = s.add({ room: 'todo', title: '已归档' });
  s.done(it.id); // 默认就收走
  s.done(it.id); // 再调用不该变回 active
  assert.strictEqual(s.get(it.id).state, 'done');
});

ok('完成时带心得', () => {
  const s = new Store(tmp);
  const it = s.add({ room: 'want', title: '看纪录片' });
  s.done(it.id, { reflection: '值得，讲的是外部记忆。' });

  const after = s.get(it.id);
  assert.strictEqual(after.reflection, '值得，讲的是外部记忆。');
  assert.ok(after.reflectionAt, 'reflectionAt 应该有值');
  assert.strictEqual(after.state, 'done');
});

ok('file() 只对已完成的生效', () => {
  const s = new Store(tmp);
  const it = s.add({ room: 'todo', title: '没完成' });
  s.file(it.id);
  assert.strictEqual(s.get(it.id).filedAt, null, '没完成的不该被收走');
});

ok('纸篓次数只在 active → binned 时 +1', () => {
  const s = new Store(tmp);
  const it = s.add({ room: 'want', title: '反复横跳' });

  s.bin(it.id);
  s.bin(it.id); // 重复调用不该重复计数
  assert.strictEqual(s.get(it.id).binCount, 1);

  s.restore(it.id);
  s.bin(it.id);
  assert.strictEqual(s.get(it.id).binCount, 2, '第二次进纸篓应该再 +1');
});

ok('在纸篓里待了多久会被累计', () => {
  const s = new Store(tmp);
  const it = s.add({ room: 'want', title: '睡了一觉的想法' });

  s.bin(it.id);
  s.get(it.id).binnedAt = ago(3); // 手动把「进纸篓的时刻」往前挪 3 天
  s.save();

  s.restore(it.id);
  const back = s.get(it.id);
  assert.strictEqual(back.binnedAt, null, '捞回来应该清掉当前这段');
  assert.ok(Math.abs(back.binTotalMs - 3 * DAY) < 5000, `累计应约 3 天，实际 ${back.binTotalMs}`);
});

ok('从纸篓里直接被完成，也会结掉纸篓时长', () => {
  const s = new Store(tmp);
  const it = s.add({ room: 'want', title: '在纸篓里被想起' });
  s.bin(it.id);
  s.get(it.id).binnedAt = ago(2);
  s.save();

  s.done(it.id, { reflection: '还是做了' });
  const after = s.get(it.id);
  assert.strictEqual(after.state, 'done');
  assert.strictEqual(after.binnedAt, null, '不该还挂着「正在纸篓里」');
  assert.ok(after.binTotalMs > 0, '这段纸篓时长应该被记下来');
});

ok('snapshot 提供完成记录要用的统计字段', () => {
  const s = new Store(tmp);
  const it = s.add({ room: 'want', title: '统计对象' });
  it.createdAt = ago(10);
  it.lastTouchedAt = ago(2);
  it.touchCount = 4;
  it.binCount = 2;
  it.binTotalMs = DAY;
  s.save();
  s.done(it.id, { reflection: '一二三四五' });

  const row = s.snapshot().items.find((i) => i.id === it.id);
  assert.ok(row.elapsedMs > 9 * DAY, `elapsedMs 应约 10 天，实际 ${row.elapsedMs}`);
  assert.strictEqual(row.binMs, DAY);
  assert.strictEqual(row.binCount, 2);
  assert.strictEqual(row.reflectionChars, 5);
  assert.ok(row.doneAt);
});

ok('已完成的不再被衰减巡检动到', () => {
  const s = new Store(tmp);
  const it = s.add({ room: 'want', title: '已完成的老愿望' });
  s.done(it.id, { reflection: 'done' });
  s.get(it.id).lastTouchedAt = ago(999);
  s.save();

  s.sweep();
  assert.strictEqual(s.get(it.id).state, 'done', '完成的愿望不该被丢进纸篓');
});

try {
  fs.rmSync(tmp, { recursive: true, force: true });
} catch (_) {}

console.log(`\n${process.exitCode ? '有失败项' : `全部通过（${passed} 项）`}\n`);
