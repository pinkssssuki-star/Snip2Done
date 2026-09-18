'use strict';

/**
 * 衰减（decay）—— 本项目最核心、也最不确定的机制。
 * 刻意写成纯函数、零依赖：以后从 Electron 迁到 Tauri 时，这个文件可以原样搬走。
 *
 * 设计原则（见设计稿）：
 *  - 衰减只作用于 Want（想做）。Todo 永不衰减 —— 任务不会因为你不看它就消失，它只会逾期。
 *  - 衰减依据是「注意力」，不是「日历」：你碰过它就续命，一直不碰才变淡。
 *  - 淡出不是删除，是进纸篓，永远可以捞回来。
 */

const DAY = 24 * 60 * 60 * 1000;

/**
 * 真实使用下的默认值。
 * 刻意偏慢 —— 太快会让你每次打开都在抢救自己的愿望，那就变成负担了。
 * 一周内不打扰，三周后才放手，这段时间足够你判断「我是真的想做，还是当时手滑」。
 */
const DEFAULT_DECAY = {
  fadeStartDays: 7, // 头一周满血，完全不衰减
  fadeEndDays: 21, // 第 21 天降到最低可见度
  minOpacity: 0.22, // 最低可见度（再低就看不见了，所以有下限）
  binAfterDays: 21, // 超过 21 天没碰 → 进纸篓
};

/**
 * 演示模式的时间压缩：约 5 分钟跑完一次完整生命周期。
 *   1 天 ≈ 14.4 秒 → 第 7 天(≈100s)开始变淡，第 21 天(≈5min)进纸篓
 * 这是为了让你几分钟内就能看到机制跑完，而不是等三周。
 */
const DEMO_TIME_SCALE = 6000;

/**
 * @param {number} lastTouchedAt 上次被「看见/碰过」的时间戳
 * @param {number} now
 * @param {number} timeScale 1 = 真实时间；1440 = 演示加速
 */
function elapsedDays(lastTouchedAt, now, timeScale = 1) {
  return ((now - lastTouchedAt) * timeScale) / DAY;
}

/** 返回 0~1 的不透明度。 */
function opacityFor(item, now, cfg = DEFAULT_DECAY, timeScale = 1) {
  const d = elapsedDays(item.lastTouchedAt, now, timeScale);
  if (d <= cfg.fadeStartDays) return 1;
  if (d >= cfg.fadeEndDays) return cfg.minOpacity;
  const t = (d - cfg.fadeStartDays) / (cfg.fadeEndDays - cfg.fadeStartDays);
  return 1 - t * (1 - cfg.minOpacity);
}

/** 是否该进纸篓了。 */
function shouldBin(item, now, cfg = DEFAULT_DECAY, timeScale = 1) {
  return elapsedDays(item.lastTouchedAt, now, timeScale) >= cfg.binAfterDays;
}

/**
 * 归一化「生命值」0~1：1 = 刚碰过，0 = 就要进纸篓。
 * UI 用它画生命条，比 opacity 更直观。
 */
function lifeFor(item, now, cfg = DEFAULT_DECAY, timeScale = 1) {
  const d = elapsedDays(item.lastTouchedAt, now, timeScale);
  if (d <= 0) return 1;
  if (d >= cfg.binAfterDays) return 0;
  return 1 - d / cfg.binAfterDays;
}

/** 距离进纸篓还剩多少「天」（演示模式下是虚拟天）。 */
function daysLeftFor(item, now, cfg = DEFAULT_DECAY, timeScale = 1) {
  const d = elapsedDays(item.lastTouchedAt, now, timeScale);
  return Math.max(0, cfg.binAfterDays - d);
}

function cfgFor(settings = {}) {
  return Object.assign({}, DEFAULT_DECAY, settings.decay || {});
}

function timeScaleFor(settings = {}) {
  return settings.demoFastDecay ? DEMO_TIME_SCALE : 1;
}

module.exports = {
  DAY,
  DEFAULT_DECAY,
  DEMO_TIME_SCALE,
  elapsedDays,
  opacityFor,
  shouldBin,
  lifeFor,
  daysLeftFor,
  cfgFor,
  timeScaleFor,
};
