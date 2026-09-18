'use strict';

/**
 * 用 GitHub 搜索 API 统计「桌面小组件 / 截图工具」这类项目的语言分布。
 *
 * 为什么用 Node 而不是 PowerShell：本机 .NET Framework 的 TLS 栈连不上外网
 * （nvm/npm 正常），Node 的 fetch 可以，所以走 Node。
 *
 * 未认证搜索限流 10 次/分钟 —— 脚本会读响应头里的剩余额度，低了就等。
 * 原始结果存到 .tmp/gh-survey.json，方便不重复查询地重新分析。
 */

const fs = require('fs');
const path = require('path');

const OUT = path.join(__dirname, '..', '.tmp', 'gh-survey.json');
const API = 'https://api.github.com';

const HEADERS = {
  'User-Agent': 'remeber-er-survey',
  Accept: 'application/vnd.github+json',
};

let calls = 0;
let waitMs = 0;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function get(url) {
  for (let attempt = 0; attempt < 4; attempt++) {
    if (waitMs > 0) {
      await sleep(waitMs);
      waitMs = 0;
    }
    const res = await fetch(url, { headers: HEADERS });
    calls++;

    const remain = Number(res.headers.get('x-ratelimit-remaining') ?? '99');
    const reset = Number(res.headers.get('x-ratelimit-reset') ?? '0');

    if (res.status === 403 || res.status === 429) {
      const wait = Math.max(5000, reset * 1000 - Date.now() + 2000);
      console.log(`  [限流] 第 ${calls} 次，等 ${Math.round(wait / 1000)}s`);
      await sleep(wait);
      continue;
    }
    if (!res.ok) {
      console.log(`  [${res.status}] ${url}`);
      return null;
    }

    const json = await res.json();

    // 主动限速：剩余 ≤ 2 就先歇到窗口重置
    if (remain <= 2) {
      const wait = Math.max(3000, reset * 1000 - Date.now() + 2000);
      console.log(`  [额度剩 ${remain}] 等 ${Math.round(wait / 1000)}s`);
      waitMs = wait;
    } else if (remain <= 5) {
      waitMs = 7000;
    }

    return json;
  }
  return null;
}

const count = async (q) => {
  const j = await get(`${API}/search/repositories?q=${encodeURIComponent(q)}&per_page=1`);
  return j ? j.total_count : null;
};

async function top(q, n) {
  const j = await get(
    `${API}/search/repositories?q=${encodeURIComponent(q)}&sort=stars&order=desc&per_page=${n}`
  );
  if (!j) return [];
  return j.items.map((i) => ({
    full_name: i.full_name,
    stars: i.stargazers_count,
    language: i.language,
    pushed: (i.pushed_at || '').slice(0, 10),
    desc: (i.description || '').replace(/\s+/g, ' ').slice(0, 70),
  }));
}

// ---------------------------------------------------------------- 配置

const LANGS = ['C++', 'C#', 'TypeScript', 'Rust', 'Python', 'Swift'];

const GROUPS = [
  { title: '截图 / 截屏类', kws: ['screenshot tool', 'screen capture', '截图'] },
  {
    title: '桌面小组件 / 悬浮窗类',
    kws: ['floating widget desktop', 'desktop sticky notes', 'system tray todo', '悬浮窗'],
  },
];

const TOP_QUERIES = [
  'screenshot tool',
  'screen capture',
  '截图',
  'desktop widget',
  'sticky notes desktop',
  '悬浮窗',
];

const FRAMEWORK_QUERIES = [
  'topic:tauri',
  'topic:electron',
  'topic:tauri topic:tray',
  'topic:electron topic:tray',
  'tauri tray',
  'electron tray',
  'tauri screenshot',
  'electron screenshot',
  'tauri 悬浮窗',
  'electron 悬浮窗',
];

// ---------------------------------------------------------------- 主流程

(async () => {
  const result = { at: new Date().toISOString(), langs: {}, groups: [], tops: {}, frameworks: {} };

  console.log('=== 1. 按语言统计 ===');
  for (const g of GROUPS) {
    const rows = [];
    for (const kw of g.kws) {
      const row = { kw, counts: {} };
      for (const lang of LANGS) {
        const n = await count(`${kw} language:"${lang}"`);
        row.counts[lang] = n;
        process.stdout.write(`  ${g.title} | ${kw} | ${lang} = ${n}\n`);
      }
      rows.push(row);
    }
    result.groups.push({ title: g.title, rows });
  }

  console.log('=== 2. 高星仓库 ===');
  for (const q of TOP_QUERIES) {
    const items = await top(q, 8);
    result.tops[q] = items;
    console.log(`  [${q}] ${items.length} 条`);
  }

  console.log('=== 3. 框架对比 ===');
  for (const q of FRAMEWORK_QUERIES) {
    const n = await count(q);
    result.frameworks[q] = n;
    console.log(`  ${q} = ${n}`);
  }

  fs.mkdirSync(path.dirname(OUT), { recursive: true });
  fs.writeFileSync(OUT, JSON.stringify(result, null, 2));

  console.log(`\n完成，共 ${calls} 次 API 调用`);
  console.log(`原始数据: ${OUT}`);
})();
