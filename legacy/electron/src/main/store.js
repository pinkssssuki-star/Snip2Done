'use strict';

/**
 * 持久化层。Demo 用 JSON 文件 + 图片目录，零原生依赖（装完 electron 就能跑）。
 * 升级路径：换成 SQLite 时只需替换本文件，外层接口保持不变。
 *
 * 三条互不相同的生命周期，别混：
 *   active  → done   完成：进「完成列表」，带完成时间与时长
 *   active  → binned 放下/放弃：进纸篓，随时能捞回来（不算失败，也不算完成）
 *   done 之后 filedAt 被写上 → 从主面板消失（泡沫消散），只活在完成列表里
 */

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const decay = require('../shared/decay');

const ROOMS = ['todo', 'want'];
const STATES = ['active', 'done', 'binned'];

/** 默认设置。panelOpacity 只作用于面板底色，条目卡片始终不透明以保证可读。 */
const DEFAULT_SETTINGS = {
  demoFastDecay: false,
  panelOpacity: 0.8,
};

class Store {
  constructor(dataDir) {
    this.dataDir = dataDir;
    this.imagesDir = path.join(dataDir, 'images');
    // 注意：不能叫 this.file —— 那会遮蔽下面的 file() 方法
    this.dataFile = path.join(dataDir, 'data.json');
    fs.mkdirSync(this.imagesDir, { recursive: true });
    this.data = this._load();
  }

  _load() {
    try {
      const raw = fs.readFileSync(this.dataFile, 'utf8');
      const parsed = JSON.parse(raw);
      if (parsed && Array.isArray(parsed.items)) {
        parsed.settings = Object.assign({}, DEFAULT_SETTINGS, parsed.settings);
        // 老数据补齐新字段
        parsed.items = parsed.items.map((it) => Object.assign(newFields(), it));
        return parsed;
      }
    } catch (_) {
      /* 首次运行或文件损坏 → 重建 */
    }
    return { version: 1, items: [], settings: Object.assign({}, DEFAULT_SETTINGS) };
  }

  save() {
    const tmp = this.dataFile + '.tmp';
    fs.writeFileSync(tmp, JSON.stringify(this.data, null, 2), 'utf8');
    fs.renameSync(tmp, this.dataFile);
  }

  get settings() {
    return this.data.settings;
  }

  setSetting(key, value) {
    this.data.settings[key] = value;
    this.save();
    return this.data.settings;
  }

  /** 保存一张截图，返回相对路径（供 appimg:// 协议使用）。 */
  saveImage(id, pngBuffer) {
    const name = `${id}.png`;
    fs.writeFileSync(path.join(this.imagesDir, name), pngBuffer);
    return `images/${name}`;
  }

  /**
   * 新建条目。
   * @param {'todo'|'want'} room
   */
  add({ room, title = '', source = '', imageRel = null, dueBucket = null }) {
    const now = Date.now();
    const item = Object.assign(newFields(), {
      id: crypto.randomUUID(),
      room,
      state: 'active',
      title,
      source,
      image: imageRel,
      createdAt: now,
      // 关键：衰减基于 lastTouchedAt（注意力），不是 createdAt（日历）
      lastTouchedAt: now,
      dueBucket, // todo: 'today' | 'soon'
      dueAt: dueBucket ? dueTimestamp(dueBucket, now) : null,
    });
    this.data.items.push(item);
    this.save();
    return item;
  }

  get(id) {
    return this.data.items.find((i) => i.id === id) || null;
  }

  /** 改标题 / 备注 / 心得。只接受白名单字段。 */
  update(id, patch) {
    const it = this.get(id);
    if (!it || !patch) return null;
    if (typeof patch.title === 'string') it.title = patch.title.trim();
    if (typeof patch.note === 'string') it.note = patch.note;
    if (typeof patch.reflection === 'string') {
      it.reflection = patch.reflection;
      it.reflectionAt = patch.reflection ? Date.now() : null;
    }
    this.save();
    return it;
  }

  /** 碰它 = 续命。这是 Want 房间唯一的「活下来」方式。 */
  touch(id) {
    const it = this.get(id);
    if (!it) return null;
    it.lastTouchedAt = Date.now();
    it.touchCount = (it.touchCount || 0) + 1;
    this.save();
    return it;
  }

  /**
   * 完成。
   * @param {string} id
   * @param {object} [opts]
   * @param {string} [opts.reflection] 心得（Wantdo 完成时写）
   * @param {boolean} [opts.file] 是否立刻从主面板收走（默认 true）
   */
  done(id, opts = {}) {
    const it = this.get(id);
    if (!it) return null;

    // 再点一次 = 撤销完成（只在还没收走时有效）
    if (it.state === 'done' && !it.filedAt) {
      it.state = 'active';
      it.doneAt = null;
      it.filedAt = null;
      this.save();
      return it;
    }

    // 完成时如果正在纸篓里，先把这段纸篓时长结掉
    this._closeBinStint(it, Date.now());

    it.state = 'done';
    it.doneAt = Date.now();
    if (typeof opts.reflection === 'string') {
      it.reflection = opts.reflection.trim();
      it.reflectionAt = it.reflection ? Date.now() : null;
    }
    if (opts.file !== false) it.filedAt = Date.now();
    this.save();
    return it;
  }

  /** 完成之后从主面板收走（泡沫消散那一下）。 */
  file(id) {
    const it = this.get(id);
    if (!it || it.state !== 'done') return null;
    it.filedAt = it.filedAt || Date.now();
    this.save();
    return it;
  }

  /** 主动放弃 → 进纸篓（不删除，永远能捞回来）。 */
  bin(id) {
    const it = this.get(id);
    if (!it) return null;
    if (it.state !== 'binned') {
      it.state = 'binned';
      it.binnedAt = Date.now();
      it.binCount = (it.binCount || 0) + 1;
      this.save();
    }
    return it;
  }

  /** 从纸篓里捡回来 —— 满血复活。 */
  restore(id) {
    const it = this.get(id);
    if (!it) return null;
    this._closeBinStint(it, Date.now());
    it.state = 'active';
    it.lastTouchedAt = Date.now();
    this.save();
    return it;
  }

  /** 把「当前这段纸篓时间」累加进总时长并结束它。 */
  _closeBinStint(it, now) {
    if (it.binnedAt) {
      it.binTotalMs = (it.binTotalMs || 0) + Math.max(0, now - it.binnedAt);
    }
    it.binnedAt = null;
  }

  /** 真删除。UI 默认不给这个入口。 */
  remove(id) {
    const idx = this.data.items.findIndex((i) => i.id === id);
    if (idx < 0) return false;
    const it = this.data.items[idx];
    if (it.image) {
      try {
        fs.unlinkSync(path.join(this.dataDir, it.image));
      } catch (_) {}
    }
    this.data.items.splice(idx, 1);
    this.save();
    return true;
  }

  /**
   * 衰减巡检：把过了保质期的 Want 送进纸篓。
   * 只动 want，且只动 active —— Todo 永不衰减，已完成/已在纸篓的也不动。
   */
  sweep() {
    const cfg = decay.cfgFor(this.settings);
    const scale = decay.timeScaleFor(this.settings);
    const now = Date.now();
    let binned = 0;
    for (const it of this.data.items) {
      if (it.room !== 'want' || it.state !== 'active') continue;
      if (decay.shouldBin(it, now, cfg, scale)) {
        it.state = 'binned';
        it.binnedAt = now;
        it.binCount = (it.binCount || 0) + 1;
        binned++;
      }
    }
    if (binned) this.save();
    return binned;
  }

  /** 给 UI 的完整快照，附带算好的衰减字段与统计字段。 */
  snapshot() {
    this.sweep();
    const cfg = decay.cfgFor(this.settings);
    const scale = decay.timeScaleFor(this.settings);
    const now = Date.now();

    const items = this.data.items.map((it) => {
      const isWant = it.room === 'want';
      const isActive = it.state === 'active';

      // 在纸篓里待过的总时长（含当前正在待的这一段）
      const binMs =
        (it.binTotalMs || 0) + (it.binnedAt ? Math.max(0, now - it.binnedAt) : 0);

      // 从记下到完成（未完成就是「至今」）
      const elapsedMs = (it.doneAt || now) - it.createdAt;

      return Object.assign({}, it, {
        opacity: isWant && isActive ? decay.opacityFor(it, now, cfg, scale) : 1,
        life: isWant && isActive ? decay.lifeFor(it, now, cfg, scale) : 1,
        daysLeft: isWant && isActive ? decay.daysLeftFor(it, now, cfg, scale) : null,
        overdue: it.room === 'todo' && isActive && it.dueAt && it.dueAt < now,

        // --- 给「完成记录」用的统计 ---
        binMs,
        binCount: it.binCount || 0,
        elapsedMs,
        reflectionChars: (it.reflection || '').length,
        doneHour: it.doneAt ? new Date(it.doneAt).getHours() : null,
      });
    });

    const week = 7 * decay.DAY;
    const doneThisWeek = items.filter(
      (i) => i.state === 'done' && i.doneAt && now - i.doneAt < week
    ).length;
    const rescued = items.filter(
      (i) => i.state === 'active' && i.room === 'want' && (i.binCount || 0) > 0
    ).length;

    return {
      items,
      settings: this.settings,
      // 生效中的衰减参数（默认值 + 用户覆盖），设置页直接用这个回显
      decayCfg: cfg,
      stats: { doneThisWeek, rescued },
      now,
    };
  }
}

/** 新字段集中在这里，老数据升级和新建都用它 */
function newFields() {
  return {
    note: '',
    reflection: '',
    reflectionAt: null,
    createdAt: null,
    lastTouchedAt: null,
    touchCount: 0,
    dueBucket: null,
    dueAt: null,
    doneAt: null,
    /** 完成之后从主面板收走的时间 */
    filedAt: null,
    binnedAt: null,
    binCount: 0,
    binTotalMs: 0,
  };
}

function dueTimestamp(bucket, from) {
  const d = new Date(from);
  if (bucket === 'today') {
    d.setHours(23, 59, 59, 999);
    return d.getTime();
  }
  // 'soon' = 三两天内
  return from + 3 * decay.DAY;
}

module.exports = { Store, ROOMS, STATES };
