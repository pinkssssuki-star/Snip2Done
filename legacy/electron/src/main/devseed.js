'use strict';

/**
 * 仅开发/演示用：灌入样例数据，让 UI 与衰减机制肉眼可见。
 * 只有 REMEBER_SEED=1 且库为空时才会被调用，正常运行永不触发。
 *
 * 顺带用 zlib 手搓 PNG —— 这样不用依赖任何图片就能验证 appimg:// 协议。
 */

const zlib = require('zlib');
const fs = require('fs');
const path = require('path');

const DAY = 24 * 60 * 60 * 1000;

// ------------------------------------------------------------------ PNG 编码

const CRC_TABLE = (() => {
  const t = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c;
  }
  return t;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const typeBuf = Buffer.from(type, 'ascii');
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([typeBuf, data])), 0);
  return Buffer.concat([len, typeBuf, data, crc]);
}

/** 生成一张 w×h 的 RGBA PNG，像素由 pixelFn(x,y) 给出 */
function makePng(w, h, pixelFn) {
  const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0);
  ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // color type: RGBA
  ihdr[10] = 0;
  ihdr[11] = 0;
  ihdr[12] = 0;

  const raw = Buffer.alloc(h * (1 + w * 4));
  let o = 0;
  for (let y = 0; y < h; y++) {
    raw[o++] = 0; // filter: none
    for (let x = 0; x < w; x++) {
      const p = pixelFn(x, y);
      raw[o++] = p[0];
      raw[o++] = p[1];
      raw[o++] = p[2];
      raw[o++] = p.length > 3 ? p[3] : 255;
    }
  }

  return Buffer.concat([
    sig,
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/** 两种配色的渐变，模拟「一张截图的缩略图」 */
function gradient(w, h, from, to, seedShift) {
  return makePng(w, h, (x, y) => {
    const t = (x / w) * 0.6 + (y / h) * 0.4;
    const s = (Math.sin(seedShift + x * 0.12 + y * 0.09) + 1) / 2;
    const mix = (a, b) => Math.round(a + (b - a) * t);
    return [
      Math.min(255, mix(from[0], to[0]) + s * 26),
      Math.min(255, mix(from[1], to[1]) + s * 18),
      Math.min(255, mix(from[2], to[2]) + s * 30),
      255,
    ];
  });
}

// ------------------------------------------------------------------ 灌数据

function seed(store) {
  if (store.data.items.length) return false;

  const now = Date.now();
  const rel = (id, buf) => {
    const dir = path.join(store.dataDir, 'images');
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, `${id}.png`), buf);
    return `images/${id}.png`;
  };

  // --- Todo：永不衰减，带截图 ---
  const t1 = store.add({
    room: 'todo',
    title: 'Review pricing page copy for Marc',
    source: 'Slack',
    dueBucket: 'today',
  });
  t1.image = rel(t1.id, gradient(360, 200, [30, 58, 138], [59, 130, 246], 0.5));

  const t2 = store.add({
    room: 'todo',
    title: '把课程设计的数据跑完，周四要交',
    source: 'Code',
    dueBucket: 'soon',
  });
  t2.image = rel(t2.id, gradient(360, 200, [120, 53, 15], [217, 119, 6], 1.7));
  t2.lastTouchedAt = now - 3 * DAY; // Todo 长时间没碰也不该变淡

  // --- Want：三条不同新鲜度，好让「变淡」一眼可见 ---
  // 默认 fadeStart=7 / fadeEnd=21，所以 0 / 12 / 19 天正好铺出三级亮度
  const wants = [
    { title: '想学一下 Rust，做个小工具', source: '抖音', age: 0, c: [[76, 29, 149], [168, 85, 247]] },
    { title: '那篇讲注意力的长文还没读', source: 'Chrome', age: 12, c: [[131, 24, 67], [236, 72, 153]] },
    { title: '想去看那个摄影展', source: '小红书', age: 19, c: [[19, 78, 74], [45, 212, 191]] },
  ];

  for (const w of wants) {
    const it = store.add({ room: 'want', title: w.title, source: w.source });
    it.image = rel(it.id, gradient(360, 200, w.c[0], w.c[1], w.age + 0.3));
    it.lastTouchedAt = now - w.age * DAY;
  }

  // --- 已完成：用来验证 Done 页的分组、标签和「完成记录」 ---
  const d1 = store.add({
    room: 'todo',
    title: '把上学期的高数错题整理完',
    source: 'Excel',
    dueBucket: 'today',
  });
  d1.image = rel(d1.id, gradient(360, 200, [22, 78, 99], [14, 165, 233], 2.1));
  d1.createdAt = now - 5 * DAY;
  d1.doneAt = now - 2 * DAY;
  d1.filedAt = d1.doneAt;
  d1.state = 'done';

  const d2 = store.add({ room: 'want', title: '看完那部讲记忆的纪录片', source: 'Bilibili' });
  d2.image = rel(d2.id, gradient(360, 200, [88, 28, 135], [217, 70, 239], 3.3));
  d2.createdAt = now - 12 * DAY;
  d2.touchCount = 5;
  d2.lastTouchedAt = now - 3 * DAY;
  d2.binCount = 1;
  d2.binTotalMs = Math.round(2.5 * DAY); // 曾经被放下过两天半
  d2.reflection =
    '原以为是讲记忆术的，结果讲的是人怎么用外部工具替自己记事。看完反而更确定「截图备忘」这件事值得做——记住不该靠脑子，该靠一个你信得过的地方。';
  d2.reflectionAt = now - 1 * DAY;
  d2.doneAt = now - 1 * DAY;
  d2.filedAt = d2.doneAt;
  d2.state = 'done';

  store.save();
  return true;
}

module.exports = { seed, makePng, gradient };
