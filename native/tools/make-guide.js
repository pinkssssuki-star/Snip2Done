'use strict';

/*
 * 生成随包发出去的说明文档。
 *
 * HTML 和 TXT 都从下面同一份 CONTENT 出来 —— 两份各写各的迟早会对不上。
 * 截图用的必须是「干净演示数据」跑出来的那份（native/tools/make-guide-data.js），
 * 不能用真实数据的截图：那里面是你自己的桌面和窗口标题。
 *
 * 用法：
 *   node native/tools/make-guide-data.js    # 先造干净数据
 *   native\out\RemeberEr.Native.exe --data=.tmp\guide-data --flow=ui --shotpad=0
 *   node native/tools/make-guide.js         # 再生成文档
 */

const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const ROOT = path.resolve(__dirname, '..', '..');
const NATIVE = path.resolve(__dirname, '..');
const GUIDE = path.join(ROOT, '.guide');
const TMP = path.join(ROOT, '.tmp', 'guide-img');
const OUT = path.join(NATIVE, 'pack');

const VERSION = (() => {
  const src = fs.readFileSync(path.join(NATIVE, 'src', 'Program.cs'), 'utf8');
  const m = src.match(/public const string Version = "([^"]+)"/);
  if (!m) throw new Error('没在 src/Program.cs 里找到 Version 常量');
  return m[1];
})();

// ---------------------------------------------------------------- 内容（唯一来源）
//
// 每一节是一串「块」，**按数组顺序渲染**。
// 早先的写法是固定字段顺序（p → keys → ol → ul → callout），
// 结果「顶栏那几个按钮：」跑到了通用条目前面，读起来是断的。

const CONTENT = [
  {
    t: '怎么开始',
    blocks: [
      ['p', '双击「Snip2Done.exe」就行。不用安装，不用装任何东西。'],
      ['p', '第一次打开是空的。想先看看它长什么样，点右上角 ⚙ → 「灌入演示数据」，会给你塞几条样例。然后点右上角的 ⚡（演示加速），等一两分钟，能看着 Wantdo 磁贴慢慢变淡、最后掉进纸篓 —— 真实节奏是 7 天 / 21 天，那个按钮只是把时间压缩了 6000 倍给你看。'],
      ['img', 'panel', '面板长这样。上面是 Todo（有 DDL 的），下面是 Wantdo（你的念想）。'],
    ],
  },
  {
    t: '只记两个热键就够',
    blocks: [
      ['keys', [
        ['Ctrl+Shift+A', '截图（主要入口）'],
        ['Ctrl+Shift+H', '显示 / 隐藏面板'],
        ['Ctrl+Shift+Q', '退出'],
      ]],
      ['p', '截图之后：'],
      ['ol', [
        '屏幕会暗下来，拖动鼠标框选你想记住的那块',
        '松手以后框还在，可以拖角微调、拖框里面平移',
        '点框**外面**右下角那个黑底白勾（或者按 Enter）确认',
        '弹出三个按钮：Today（一天内）/ Soon（三天内）/ Wantdo（无期限）—— 选一个，它就去对应的房间了',
      ]],
      ['note', '嫌第三步多余？设置里打开「快速模式」：一框，一分类。'],
    ],
  },
  {
    t: '几个藏起来的操作',
    blocks: [
      ['ul', [
        '点一下 Wantdo 磁贴 = 看它一眼 = **续命**（生命条会回满）',
        '在一个条目上停半秒，左边会弹出放大预览，不用点进去就能看清',
        '勾掉 Todo 只是标记完成，它还留在原地让你能反悔；再点一下整行，它会「化掉」收进完成列表',
        '完成记录里会记着你用了多久、看过几次、写了多少字的心得',
        '按 ✕ 不是删除，是「暂时放下」，进 Paper Plane Bin，随时能捡回来',
      ]],
      ['p', '顶栏那几个按钮：'],
      ['keys', [
        ['⇧', '窗口置顶开关。嫌它一直压在最上面挡事，点一下关掉'],
        ['▾', '折叠成一条，只留顶栏'],
        ['⚙', '设置（透明度、Wantdo 保护期、多久后放手、快速模式）'],
      ]],
      ['callout', '**只有一块屏的话**：把面板往屏幕边缘外面拖，松手它就会贴边藏起来，只留一条窄边和一个紫色小条。鼠标移上去滑出来，移开再滑回去。不占位置，也不用关掉它。'],
      ['img', 'collapsed', '折叠起来就只剩顶栏这么一条。'],
    ],
  },
  {
    t: '可能遇到的问题',
    blocks: [
      ['ul', [
        '第一次运行 Windows 可能弹「已保护你的电脑」。点「更多信息」→「仍要运行」。这是因为程序没有买代码签名证书，不是因为它有问题 —— 一共就一个 170 KB 的 exe，没有网络请求、没有模型调用、不收集任何东西。',
        '杀毒软件可能报可疑。也是同一个原因：这个程序会注册全局热键、会截屏，这类行为容易触发启发式规则。',
        '只支持 Windows 10 / 11。需要 .NET Framework 4.8（系统自带，不用装）。',
        '任务栏里不会出现图标 —— 面板是设计成常驻桌面角落的。想找它按 Ctrl+Shift+H，退出按 Ctrl+Shift+Q 或顶栏的 ✕。',
      ]],
    ],
  },
  {
    t: '你的数据在哪',
    blocks: [
      ['p', '`%APPDATA%\\remeber-er\\` —— 在资源管理器地址栏粘贴这行就能打开。'],
      ['p', '里面是 data.json + 一个 images 文件夹，全是明文和普通 PNG。想从干净状态重来，直接把这个文件夹删掉。卸载 = 删掉 exe + 删掉这个文件夹。'],
    ],
  },
  {
    t: '还想让它变成什么样',
    blocks: [
      ['p', '这个版本主要是想验证一个想法：**截图不该是待办，应该是记忆钩子。**'],
      ['p', '所以我最想知道的是：'],
      ['ul', [
        '你真的会去按那个热键吗？还是装了就不管了？',
        '「Todo / Wantdo」这个二分，你判断起来犹豫吗？',
        '东西变淡然后掉进纸篓这件事，让你焦虑还是让你松一口气？',
        '哪个地方让你觉得多余、或者反过来觉得不够？',
      ]],
      ['note', '有什么想法直接跟我说，不用客气。这个阶段改起来很快。'],
    ],
  },
];

// ---------------------------------------------------------------- 图片处理

function convert(srcName, outName, width) {
  const src = path.join(GUIDE, srcName);
  if (!fs.existsSync(src)) return null;
  fs.mkdirSync(TMP, { recursive: true });
  const dst = path.join(TMP, outName);

  // 用 PowerShell + System.Drawing 缩放并转 JPEG：一张 425×625 的 PNG 是 87 KB，
  // 转成 360px 宽的 JPEG 只有二十几 KB，base64 内联进 HTML 也不心疼。
  const cmd = [
    'Add-Type -AssemblyName System.Drawing;',
    `$s=[System.Drawing.Image]::FromFile('${src}');`,
    `$w=${width};$h=[int]($s.Height*$w/$s.Width);`,
    '$b=New-Object System.Drawing.Bitmap $w,$h;',
    '$g=[System.Drawing.Graphics]::FromImage($b);',
    "$g.InterpolationMode='HighQualityBicubic';",
    '$g.DrawImage($s,0,0,$w,$h);',
    "$c=[System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders()|Where-Object{$_.MimeType -eq 'image/jpeg'};",
    '$p=New-Object System.Drawing.Imaging.EncoderParameters 1;',
    '$p.Param[0]=New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality),88;',
    `$b.Save('${dst}',$c,$p);`,
  ].join('');

  execFileSync('powershell', ['-NoProfile', '-Command', cmd], { stdio: 'inherit' });
  return fs.existsSync(dst) ? fs.readFileSync(dst).toString('base64') : null;
}

const imgPanel = convert('native-ui-expanded.png', 'panel.jpg', 380);
const imgCollapsed = convert('native-ui-collapsed.png', 'collapsed.jpg', 380);
console.log(`插图：面板 ${imgPanel ? Math.round(imgPanel.length / 1024) + ' KB' : '缺'}，`
  + `折叠 ${imgCollapsed ? Math.round(imgCollapsed.length / 1024) + ' KB' : '缺'}`);

// ---------------------------------------------------------------- 渲染

/** 极小的行内标记：**粗体** 和 `等宽` */
const md = (s) =>
  s.replace(/&/g, '&amp;').replace(/</g, '&lt;')
    .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
    .replace(/`(.+?)`/g, '<code>$1</code>');

const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

function renderHtml() {
  const IMGS = {
    panel: imgPanel
      ? `<figure><img src="data:image/jpeg;base64,${imgPanel}" alt="Snip2Done"><figcaption>{cap}</figcaption></figure>`
      : '',
    collapsed: imgCollapsed
      ? `<figure class="narrow"><img src="data:image/jpeg;base64,${imgCollapsed}" alt="折叠态"><figcaption>{cap}</figcaption></figure>`
      : '',
  };

  const block = ([kind, val, cap]) => {
    switch (kind) {
      case 'p': return `<p>${md(val)}</p>`;
      case 'note': return `<p class="note">${md(val)}</p>`;
      case 'callout': return `<aside>${md(val)}</aside>`;
      case 'ol': return '<ol>' + val.map((x) => `<li>${md(x)}</li>`).join('') + '</ol>';
      case 'ul': return '<ul>' + val.map((x) => `<li>${md(x)}</li>`).join('') + '</ul>';
      case 'keys':
        return '<dl class="keys">'
          + val.map(([k, v]) => `<div><dt>${esc(k)}</dt><dd>${md(v)}</dd></div>`).join('')
          + '</dl>';
      case 'img': {
        const tpl = IMGS[val];
        return tpl ? tpl.replace('{cap}', esc(cap || '')) : '';
      }
      default: return '';
    }
  };

  const body = CONTENT.map((sec) =>
    `<section><h2>${esc(sec.t)}</h2>\n${sec.blocks.map(block).join('\n')}\n</section>`
  ).join('\n');

  return `<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Snip2Done · 试用说明</title>
<style>
  :root { --accent: #8b5cf6; --accent-deep: #7c3aed; --tx1: #1c1c1e; --tx2: #3a3a3c; --tx3: #86868c; }
  * { box-sizing: border-box; }
  body {
    margin: 0; padding: 48px 20px 80px;
    font: 15px/1.75 "Segoe UI", "Microsoft YaHei", system-ui, sans-serif;
    color: var(--tx1);
    background: #f6f6f8;
  }
  main { max-width: 680px; margin: 0 auto; }

  header.hero {
    text-align: center; margin-bottom: 44px;
  }
  header.hero .dot {
    display: inline-block; width: 10px; height: 10px; border-radius: 50%;
    background: var(--accent); box-shadow: 0 0 12px rgba(139,92,246,.7);
    vertical-align: middle; margin-right: 10px;
  }
  header.hero h1 { display: inline; font-size: 26px; letter-spacing: .5px; vertical-align: middle; }
  header.hero .sub { margin-top: 18px; color: var(--tx2); font-size: 15.5px; line-height: 1.9; }
  header.hero .sub b { color: var(--accent-deep); }

  section {
    background: #fff; border-radius: 14px; padding: 26px 28px 28px;
    margin-bottom: 16px; border: 1px solid rgba(0,0,0,.06);
    box-shadow: 0 1px 3px rgba(0,0,0,.04);
  }
  h2 {
    font-size: 16px; margin: 0 0 16px; padding-bottom: 12px;
    border-bottom: 1px solid rgba(0,0,0,.07); letter-spacing: .3px;
  }
  p { margin: 0 0 14px; color: var(--tx2); }
  p:last-child { margin-bottom: 0; }
  strong { color: var(--accent-deep); font-weight: 600; }
  code {
    font-family: Consolas, "Cascadia Mono", monospace; font-size: 13.5px;
    background: rgba(139,92,246,.10); color: var(--accent-deep);
    padding: 2px 6px; border-radius: 5px;
  }

  dl.keys { margin: 0 0 16px; }
  dl.keys > div {
    display: flex; align-items: baseline; gap: 14px;
    padding: 9px 0; border-bottom: 1px dashed rgba(0,0,0,.07);
  }
  dl.keys > div:last-child { border-bottom: 0; }
  dl.keys dt {
    flex: none; min-width: 120px;
    font-family: Consolas, "Cascadia Mono", monospace; font-size: 13px;
    background: #f2f2f5; border: 1px solid rgba(0,0,0,.09);
    border-radius: 6px; padding: 4px 9px; text-align: center; color: var(--tx1);
  }
  dl.keys dd { margin: 0; color: var(--tx2); }

  ol, ul { margin: 0 0 16px; padding-left: 22px; color: var(--tx2); }
  li { margin-bottom: 8px; }
  ol { counter-reset: n; list-style: none; padding-left: 0; }
  ol li { counter-increment: n; padding-left: 34px; position: relative; }
  ol li::before {
    content: counter(n);
    position: absolute; left: 0; top: 3px;
    width: 22px; height: 22px; border-radius: 50%;
    background: rgba(139,92,246,.13); color: var(--accent-deep);
    font-size: 12px; font-weight: 700; text-align: center; line-height: 22px;
  }

  aside {
    margin: 18px 0 0; padding: 14px 16px;
    background: linear-gradient(150deg, rgba(139,92,246,.09), rgba(139,92,246,.03));
    border-left: 3px solid var(--accent); border-radius: 9px;
    color: var(--tx2); font-size: 14.5px;
  }
  p.note { margin-top: 16px; color: var(--tx3); font-size: 14px; }

  figure { margin: 20px 0 0; text-align: center; }
  figure img {
    max-width: 100%; border-radius: 12px;
    border: 1px solid rgba(0,0,0,.08);
    box-shadow: 0 8px 28px rgba(0,0,0,.13);
  }
  figure.narrow img { box-shadow: 0 4px 16px rgba(0,0,0,.13); }
  figcaption { margin-top: 10px; font-size: 13px; color: var(--tx3); }

  footer {
    text-align: center; margin-top: 34px;
    font-size: 13px; color: #a1a1a6; line-height: 1.9;
  }
  footer .ver { color: var(--accent-deep); font-weight: 600; }
</style>
</head>
<body>
<main>
  <header class="hero">
    <div><span class="dot"></span><h1>Snip2Done</h1></div>
    <div class="sub">
      一个常驻桌面角落的小面板。<br>
      看到以后要处理的东西，按 <code>Ctrl+Shift+A</code> 截下来，<br>
      做一次判断 —— 这是 <b>Todo</b> 还是 <b>Wantdo</b> —— 然后它就一直挂在你桌面上。
    </div>
  </header>

${body}

  <footer>
    截图不是待办，是记忆钩子。<br>
    <span class="ver">v${VERSION}</span> · 原生版（WPF，单进程约 110 MB）
  </footer>
</main>
</body>
</html>
`;
}

function renderTxt() {
  const out = [];
  out.push('Snip2Done · 试用说明');
  out.push('='.repeat(40));
  out.push('');
  out.push('一个常驻桌面角落的小面板。');
  out.push('');
  out.push('你看到以后要处理的东西，按 Ctrl+Shift+A 截下来，做一次判断 ——');
  out.push('这是「Todo」还是「Wantdo」—— 然后它就一直挂在你桌面上。');
  out.push('');
  out.push('  Todo     有截止时间的，被 DDL 催着走');
  out.push('  Wantdo   没有期限的想做的事，靠你的注意力续命：');
  out.push('           你不碰它，它就慢慢变淡，最后滑进「Paper Plane Bin」，');
  out.push('           但永远能捞回来。');
  out.push('');
  out.push('截图不是待办，是记忆钩子。');
  out.push('');

  for (const sec of CONTENT) {
    out.push('');
    out.push('-'.repeat(40));
    out.push(sec.t);
    out.push('-'.repeat(40));
    out.push('');
    for (const [kind, val] of sec.blocks) {
      const plain = (x) => x.replace(/\*\*/g, '').replace(/`/g, '');
      if (kind === 'p' || kind === 'note' || kind === 'callout') { out.push(plain(val)); out.push(''); }
      else if (kind === 'keys') val.forEach(([k, v]) => { out.push(`  ${k.padEnd(14)} ${plain(v)}`); out.push(''); });
      else if (kind === 'ol') val.forEach((x, i) => { out.push(`  ${i + 1}. ${plain(x)}`); out.push(''); });
      else if (kind === 'ul') val.forEach((x) => { out.push(`  · ${plain(x)}`); out.push(''); });
      // 纯文本版不带图
    }
  }

  out.push('');
  out.push('-'.repeat(40));
  out.push(`v${VERSION} · 原生版（WPF，单进程约 110 MB）`);
  out.push('');
  return out.join('\r\n');
}

fs.mkdirSync(OUT, { recursive: true });
const htmlPath = path.join(OUT, '使用说明.html');
const txtPath = path.join(OUT, '使用说明.txt');
fs.writeFileSync(htmlPath, renderHtml(), 'utf8');
fs.writeFileSync(txtPath, renderTxt(), 'utf8');

console.log(`  ${htmlPath}  ${Math.round(fs.statSync(htmlPath).size / 1024)} KB`);
console.log(`  ${txtPath}  ${Math.round(fs.statSync(txtPath).size / 1024)} KB`);
