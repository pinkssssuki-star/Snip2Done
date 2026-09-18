'use strict';

/*
 * 造一份「可以对外示人」的演示数据。
 *
 * 为什么不直接用真实数据截图：真实数据里的缩略图是你自己屏幕的截图，
 * 桌面壁纸、Edge 标签、聊天窗口标题全在里面 —— 发给朋友等于把自己的屏泄出去。
 * 这里复用 Electron 版那套 demo seed（纯渐变图 + 通用标题），截出来的图是干净的。
 *
 * 用法：node native/tools/make-guide-data.js
 */

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..', '..');
const ELECTRON = path.join(ROOT, 'legacy', 'electron');
const DEST = path.join(ROOT, '.tmp', 'guide-data');

const { Store } = require(path.join(ELECTRON, 'src', 'main', 'store.js'));
const devseed = require(path.join(ELECTRON, 'src', 'main', 'devseed.js'));

fs.rmSync(DEST, { recursive: true, force: true });
fs.mkdirSync(path.join(DEST, 'images'), { recursive: true });

const store = new Store(DEST);
devseed.seed(store);

// 面板底色不透明，截图才不会有桌面透出来
store.setSetting('panelOpacity', 1.0);
store.setSetting('alwaysOnTop', true);
store.setSetting('demoFastDecay', false);
store.save();

const snap = store.snapshot();
const byRoom = (r) => snap.items.filter((i) => i.room === r).length;
console.log(`演示数据 -> ${DEST}`);
console.log(`  共 ${snap.items.length} 条：Todo ${byRoom('todo')}，Wantdo ${byRoom('want')}`);
console.log(`  图片 ${fs.readdirSync(path.join(DEST, 'images')).length} 张（全是程序生成的渐变，无隐私内容）`);
console.log(`  面板透明度 = 1.0（截图时不透出桌面）`);
