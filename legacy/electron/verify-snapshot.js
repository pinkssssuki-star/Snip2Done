'use strict';

/*
 * 移植忠实度核对：把「现在」钉死成同一个时间戳，让 JS 版 store.js 和 C# 版 Store.cs
 * 对同一份 data.json 各算一次 snapshot()，然后逐字段 diff。
 *
 * 用法：
 *   node verify-snapshot.js <now_ms> <js数据目录> <js输出> <cs输出>
 *
 * 之所以要钉死 now：snapshot 里的 opacity / life / daysLeft 全是时间的函数，
 * 两次调用差几毫秒就会不一样，不钉死的话根本分不清是「移植算错了」还是「跑得晚了」。
 */

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..', '..');
const [nowArg, jsDir, jsOut, csOut] = process.argv.slice(2);
if (!nowArg || !jsDir || !jsOut || !csOut) {
  console.error('用法：node verify-snapshot.js <now_ms> <js数据目录> <js输出> <cs输出>');
  process.exit(2);
}
const NOW = Number(nowArg);

// --- 1. 用固定时间戳跑 JS 版 ---
const realNow = Date.now;
Date.now = () => NOW;
const { Store } = require(path.join(__dirname, 'src', 'main', 'store.js'));
const jsSnap = new Store(path.resolve(ROOT, jsDir)).snapshot();
Date.now = realNow;
fs.writeFileSync(jsOut, JSON.stringify(jsSnap), 'utf8');

// --- 2. 读 C# 版的结果 ---
if (!fs.existsSync(csOut)) {
  console.error('C# 结果不存在：' + csOut + '（先跑 --dumpsnap）');
  process.exit(2);
}
const csSnap = JSON.parse(fs.readFileSync(csOut, 'utf8'));

// --- 3. 逐字段比 ---
const diffs = [];
const NUM_TOL = 1e-9;

/*
 * 原生版新加、Electron 版没有的**派生字段**。它们只给界面用，不写进 data.json，
 * 所以不影响「两个实现指向同一份数据」这条不变量。
 * 加新字段时往这里补一条，别去动 legacy（它已经封版了）。
 */
const NATIVE_ONLY = /\.(urgency)$/;

function walk(a, b, p) {
  if (a === null || b === null || a === undefined || b === undefined) {
    if (NATIVE_ONLY.test(p) && (a === undefined || b === undefined)) return;
    if (a !== b) diffs.push(`${p}: JS=${JSON.stringify(a)} CS=${JSON.stringify(b)}`);
    return;
  }
  if (typeof a === 'number' && typeof b === 'number') {
    if (Math.abs(a - b) > NUM_TOL) diffs.push(`${p}: JS=${a} CS=${b} (差 ${Math.abs(a - b).toExponential(3)})`);
    return;
  }
  if (Array.isArray(a) || Array.isArray(b)) {
    if (!Array.isArray(a) || !Array.isArray(b)) { diffs.push(`${p}: 一边是数组一边不是`); return; }
    if (a.length !== b.length) { diffs.push(`${p}: 长度 JS=${a.length} CS=${b.length}`); return; }
    for (let i = 0; i < a.length; i++) walk(a[i], b[i], `${p}[${i}]`);
    return;
  }
  if (typeof a === 'object' && typeof b === 'object') {
    const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
    for (const k of keys) walk(a[k], b[k], p ? `${p}.${k}` : k);
    return;
  }
  if (a !== b) diffs.push(`${p}: JS=${JSON.stringify(a)} CS=${JSON.stringify(b)}`);
}

walk(jsSnap, csSnap, '');

const items = jsSnap.items.length;
const wantActive = jsSnap.items.filter((i) => i.room === 'want' && i.state === 'active').length;
console.log(`JS  items=${items} (want/active=${wantActive})  settings=${JSON.stringify(jsSnap.settings)}`);
console.log(`CS  items=${csSnap.items.length} (want/active=${csSnap.items.filter((i) => i.room === 'want' && i.state === 'active').length})  settings=${JSON.stringify(csSnap.settings)}`);
console.log('');

if (diffs.length === 0) {
  console.log(`✔ 逐字段完全一致（${items} 个条目 × 全部衰减字段，容差 ${NUM_TOL}）`);
  process.exit(0);
}
console.log(`✘ 发现 ${diffs.length} 处不一致：`);
for (const d of diffs.slice(0, 60)) console.log('   ' + d);
if (diffs.length > 60) console.log(`   …还有 ${diffs.length - 60} 处`);
process.exit(1);
