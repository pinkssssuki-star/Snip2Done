'use strict';

/**
 * 生成《给朋友的话.html》—— 单文件、离线可看、把截图直接内联进去。
 *
 * 为什么要生成而不是手写：
 *   1. 截图要 base64 内联，手写不现实
 *   2. 以后界面改了，重跑一次就能更新说明里的图
 *
 * 用法：
 *   1. 先抓图（REMEBER_DEV_SCENE=all 会输出到 .guide/）
 *   2. node scripts/make-guide.js
 */

const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const GUIDE = path.join(ROOT, '.guide');
const OUT = path.join(ROOT, '给朋友的话.html');

/** 读一张图并转成 data URI；读不到就返回 null（页面里自动跳过那一块） */
function img(name) {
  const p = path.join(GUIDE, name);
  try {
    const b = fs.readFileSync(p);
    return 'data:image/png;base64,' + b.toString('base64');
  } catch (_) {
    return null;
  }
}

async function main() {
  const icon = (() => {
    try {
      const b = fs.readFileSync(path.join(ROOT, 'build', 'icon.png'));
      return 'data:image/png;base64,' + b.toString('base64');
    } catch (_) {
      return null;
    }
  })();

  const shots = {
    panel: img('shot-panel.png'),
    chooser: img('shot-chooser.png'),
    detail: img('shot-detail.png'),
    preview: img('shot-preview.png'),
    done: img('shot-done.png'),
    record: img('shot-record.png'),
  };

  /** 有图就渲染成 figure，没图就整块不出现 */
  const fig = (src, caption, cls = '') => {
    if (!src) return '';
    return `<figure class="shot ${cls}">
      <img src="${src}" alt="${caption.replace(/"/g, '')}" />
      <figcaption>${caption}</figcaption>
    </figure>`;
  };

  const html = `<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>记忆面板 · 给测试的朋友</title>
<style>
  :root{
    --purple:#7c3aed; --purple-l:#a855f7; --pink:#ec4899;
    --ink:#1c1c1e; --ink2:#3a3a3c; --mute:#8a8a8e; --line:rgba(0,0,0,.08);
    --bg:#fbfbfc; --card:#fff;
  }
  *{box-sizing:border-box}
  body{
    margin:0; background:var(--bg); color:var(--ink);
    font:16px/1.75 -apple-system,"Segoe UI","Microsoft YaHei",system-ui,sans-serif;
    -webkit-font-smoothing:antialiased;
  }
  .wrap{max-width:820px;margin:0 auto;padding:48px 24px 96px}

  /* ---- 头部 ---- */
  header{display:flex;gap:20px;align-items:center;margin-bottom:12px}
  header img{width:72px;height:72px;border-radius:18px;flex:none;
    box-shadow:0 8px 24px rgba(124,58,237,.28)}
  header h1{margin:0;font-size:30px;letter-spacing:-.5px}
  header p{margin:4px 0 0;color:var(--mute);font-size:14px}
  .lede{font-size:17px;color:var(--ink2);margin:22px 0 8px;
    padding-left:16px;border-left:3px solid var(--purple-l)}

  /* ---- 章节 ---- */
  h2{margin:56px 0 16px;font-size:21px;letter-spacing:-.3px;
    padding-bottom:10px;border-bottom:1px solid var(--line)}
  h2 .num{display:inline-flex;width:26px;height:26px;border-radius:8px;
    background:var(--purple);color:#fff;font-size:14px;font-weight:700;
    align-items:center;justify-content:center;margin-right:10px;vertical-align:2px}
  h3{margin:30px 0 10px;font-size:16.5px;color:var(--ink)}
  p{margin:12px 0}
  ul,ol{margin:12px 0;padding-left:24px}
  li{margin:7px 0}
  strong{color:var(--ink);font-weight:600}
  code{background:rgba(124,58,237,.09);color:var(--purple);
    padding:2px 7px;border-radius:6px;font-size:13.5px;
    font-family:Consolas,"Courier New",monospace}

  /* ---- 卡片 ---- */
  .card{background:var(--card);border:1px solid var(--line);border-radius:14px;
    padding:20px 22px;margin:18px 0;box-shadow:0 1px 3px rgba(0,0,0,.04)}
  .card.two{display:grid;grid-template-columns:1fr 1fr;gap:14px;padding:0;background:none;border:0;box-shadow:none}
  .room{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:18px;
    box-shadow:0 1px 3px rgba(0,0,0,.04)}
  .room h4{margin:0 0 10px;font-size:15px}
  .room.todo h4{color:#1d4ed8}
  .room.want h4{color:var(--purple)}
  .room ul{padding-left:18px;margin:0;font-size:14.5px;color:var(--ink2)}

  /* ---- 提示框 ---- */
  .note{border-radius:12px;padding:16px 18px;margin:18px 0;font-size:14.5px;line-height:1.7}
  .note .t{font-weight:700;margin-bottom:6px;display:block}
  .warn{background:#fffbeb;border:1px solid #fde68a;color:#78350f}
  .warn .t{color:#92400e}
  .info{background:#f5f3ff;border:1px solid #ddd6fe;color:#3b0764}
  .info .t{color:var(--purple)}
  .danger{background:#fef2f2;border:1px solid #fecaca;color:#7f1d1d}
  .danger .t{color:#b91c1c}

  /* ---- 截图 ---- */
  figure.shot{margin:18px 0;text-align:center}
  figure.shot img{max-width:100%;border-radius:12px;
    border:1px solid var(--line);box-shadow:0 6px 22px rgba(0,0,0,.10);
    background:#fff}
  figure.shot.narrow img{max-width:340px}
  figcaption{margin-top:9px;font-size:13px;color:var(--mute)}

  /* ---- 快捷键 ---- */
  table{width:100%;border-collapse:collapse;margin:16px 0;font-size:14.5px}
  th,td{text-align:left;padding:10px 12px;border-bottom:1px solid var(--line);vertical-align:top}
  th{font-size:12.5px;letter-spacing:.4px;color:var(--mute);font-weight:600;
    text-transform:uppercase;background:rgba(0,0,0,.018)}
  kbd{
    display:inline-block;min-width:22px;text-align:center;
    padding:2px 8px;border-radius:6px;font-size:12.5px;font-weight:600;
    font-family:Consolas,monospace;color:var(--ink2);
    background:#fff;border:1px solid rgba(0,0,0,.16);
    box-shadow:0 1.5px 0 rgba(0,0,0,.14);margin:0 1px;
  }

  /* ---- 提问 ---- */
  .q{counter-reset:none;list-style:none;padding:0;margin:18px 0}
  .q li{background:var(--card);border:1px solid var(--line);border-left:3px solid var(--pink);
    border-radius:10px;padding:14px 18px;margin:10px 0;font-size:15px;color:var(--ink2)}
  .q li b{color:var(--ink)}

  footer{margin-top:64px;padding-top:22px;border-top:1px solid var(--line);
    color:var(--mute);font-size:13.5px;text-align:center}

  @media(max-width:640px){
    .card.two{grid-template-columns:1fr}
    .wrap{padding:28px 16px 64px}
    header h1{font-size:24px}
  }
</style>
</head>
<body>
<div class="wrap">

<header>
  ${icon ? `<img src="${icon}" alt="图标" />` : ''}
  <div>
    <h1>记忆面板</h1>
    <p>给测试的朋友 · v0.5.0</p>
  </div>
</header>

<p class="lede">
  这是一个我自己做的小工具，还在很早期的阶段。<br />
  想请你用几天，看看有没有用。<br />
  <strong>随时告诉我哪里别扭、哪里没搞懂 —— 不好用也是有用的反馈。</strong>
</p>

${fig(shots.panel, '它长这样：一个常驻在桌面右上角的小面板', 'narrow')}

<h2><span class="num">1</span>这是什么</h2>

<p>用来记住「<strong>我看到过、但以后要处理</strong>」的事情。</p>
<p>它跟普通待办软件最大的不同，有两处。</p>

<h3>一、你只需要截一张图，不用打字</h3>
<p>
  看到一段聊天、一篇文章、一个订单、一个视频，按快捷键框一下就存进去了。<br />
  不用写标题、不用填日期、不用选分类 —— <strong>你只要回答一个问题：这是「必须做」还是「想做」</strong>。
</p>
${fig(shots.chooser, '截完图只问你这一句，点一下就好了')}

<h3>二、它有「两个房间」，而且管法完全相反</h3>

<div class="card two">
  <div class="room todo">
    <h4>Todo</h4>
    <ul>
      <li>有期限的活（作业、工作）</li>
      <li><strong>永远不变</strong>，只会逾期</li>
      <li>做完了就进完成列表</li>
    </ul>
  </div>
  <div class="room want">
    <h4>Wantdo</h4>
    <ul>
      <li>没期限的愿望（想看的、想学的）</li>
      <li>你不碰它，它就<strong>慢慢变淡</strong></li>
      <li>三周后滑进纸篓 —— 但随时能捞回来，满血复活</li>
    </ul>
  </div>
</div>

<div class="note info">
  <span class="t">「想做」的东西会自己变淡，是故意的</span>
  你的注意力有限，淡掉的那些说明你其实没那么想要它。
  <strong>这不是失败，是筛选。</strong>
</div>

<h2><span class="num">2</span>怎么用</h2>

<h3>打开</h3>
<ul>
  <li>双击 <code>RemeberEr-0.5.0-portable.exe</code>（免安装，放哪都行）</li>
  <li>桌面<strong>右上角</strong>会出现一个白色小面板</li>
  <li>关掉：点面板右上角 ✕，或按 <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Q</kbd></li>
</ul>

<h3>先看它长什么样（推荐第一步）</h3>
<ol>
  <li>面板上点 <strong>⚙</strong> → 「<strong>灌入演示数据</strong>」→ 回到主界面</li>
  <li>再点右上角 <strong>⚡</strong> 打开演示加速</li>
</ol>
<p>
  时间会被压缩 <strong>6000 倍</strong>（1 天 ≈ 14 秒）。
  等 1~2 分钟，你会看到 Wantdo 的卡片慢慢变淡；
  再等几分钟，最旧的那张会自己滑进「Paper Plane Bin」。
</p>
<p style="color:#8a8a8e;font-size:14px">演示加速只是临时开关，关掉就恢复正常速度。</p>

<h3>真正用起来</h3>
<table>
  <tr><th style="width:38%">操作</th><th>说明</th></tr>
  <tr>
    <td><kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>A</kbd></td>
    <td><strong>截图（主入口）</strong>。屏幕变暗后拖一个框，松手会让你选 Todo / Wantdo</td>
  </tr>
  <tr>
    <td><kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>H</kbd></td>
    <td>显示 / 隐藏面板</td>
  </tr>
  <tr>
    <td><kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Q</kbd></td>
    <td>退出</td>
  </tr>
  <tr>
    <td>点 Todo 那一条</td>
    <td>打开详情，可以改标题、写备注</td>
  </tr>
  <tr>
    <td><strong>在一条上停半秒</strong></td>
    <td>左边会弹出放大预览 —— <strong>不点进去就能看清图里记了什么</strong></td>
  </tr>
  <tr>
    <td>勾掉 Todo</td>
    <td>变成已完成（还能反悔）；<strong>再点它一下</strong>，它会像泡泡一样消失，进完成列表</td>
  </tr>
  <tr>
    <td>点 Wantdo 卡片</td>
    <td>打开详情 → <strong>Get Done</strong> 会让你写一句心得，然后归档</td>
  </tr>
</table>

${fig(shots.preview, '鼠标停半秒，左边弹出放大预览')}
${fig(shots.detail, '点开可以改标题、写备注')}
${fig(shots.done, '完成的都会进这里，按日期分组，还标了用了多久')}
${fig(shots.record, 'Wantdo 完成时会留下心得 + 一份小结')}

<div class="note warn">
  <span class="t">一个可能的麻烦：快捷键被占用</span>
  <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>A</kbd> 可能被别的软件抢走
  （微信、QQ、各种截图工具都爱用这个组合）。
  如果按了没反应，先用面板上的 <strong>✂</strong> 按钮代替，并且告诉我。
</div>

<h2><span class="num">3</span>第一次打开被拦住了</h2>

<p>
  这个程序<strong>没有买代码签名证书</strong>（个人项目，一年好几百刀），
  所以 Windows 会拦一下。<strong>这不是病毒，是我自己写的。</strong>
</p>

<div class="note warn">
  <span class="t">表现</span>
  双击后弹出蓝框「Windows 已保护你的电脑」
</div>

<div class="note info">
  <span class="t">解决</span>
  1. 点框里的「<strong>更多信息</strong>」<br />
  2. 点出现的「<strong>仍要运行</strong>」<br /><br />
  如果被 SmartScreen 直接删掉了，去
  <strong>Windows 安全中心 → 病毒和威胁防护 → 保护历史记录</strong>，把它「允许」恢复。
</div>

<div class="note danger">
  <span class="t">如果杀毒软件报毒</span>
  也大概率是误报。原因是这个程序会做三件事：<br />
  ① 注册全局快捷键　② 截取屏幕　③ <strong>调用系统的 PowerShell 去读取「当前窗口叫什么名字」</strong><br /><br />
  这三件事单独都很正常，但凑在一个没有签名的 exe 里，很容易触发启发式规则。<br />
  <strong>要是你介意，就别装 —— 完全理解。</strong>
</div>

<h2><span class="num">4</span>我不确定的地方</h2>
<p>这几个问题特别想听你的真实想法：</p>
<ul class="q">
  <li>
    <b>「想做的东西会变淡」这个机制，你觉得是「被解放」还是「被冒犯」？</b><br />
    这是整个设计里我最没底的一点。它是核心，也可能是最让人不舒服的地方。
  </li>
  <li>
    <b>按 <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>A</kbd> 之后，到屏幕变暗，你感觉有延迟吗？</b><br />
    我这边测出来大约 0.8 秒（Snipaste 那种是 0.05 秒量级），想知道在你机器上体感如何。
  </li>
  <li><b>弹出来的那个小卡片，你会不会嫌它打断你？</b></li>
  <li><b>有没有哪些你截图存下来的东西，其实根本不想让它出现在这里？</b></li>
</ul>

<h2><span class="num">5</span>数据在哪 / 怎么删</h2>
<p>所有东西（截图原图 + 数据）都只存在你自己的电脑上：</p>
<p><code>%APPDATA%\\RemeberEr\\</code></p>
<p><strong>不联网、不上传、没有账号。</strong></p>
<p>想彻底删掉：退出程序，把这个文件夹删掉，再把 exe 删掉就行了。</p>

<footer>
  谢谢！有任何问题随时找我，截图发我也行。
</footer>

</div>
</body>
</html>
`;

  fs.writeFileSync(OUT, html, 'utf8');

  const kb = (Buffer.byteLength(html, 'utf8') / 1024).toFixed(0);
  const used = Object.entries(shots).filter(([, v]) => v).map(([k]) => k);
  console.log(`✓ ${path.relative(ROOT, OUT)}  ${kb} KB`);
  console.log(`  内联截图: ${used.length ? used.join(', ') : '（一张都没有，说明还没抓图）'}`);
  console.log(`  图标: ${icon ? '有' : '无'}`);
}

main();
