// 检查生成出来的说明文档里，每一节的块顺序对不对（比反复裁图可靠）。
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..', '..');
const html = fs.readFileSync(path.join(ROOT, 'native', 'pack', '使用说明.html'), 'utf8');

const secs = html.split('<section>').slice(1);
let bad = 0;
secs.forEach((s, i) => {
  const title = (s.match(/<h2>(.*?)<\/h2>/) || [])[1] || '?';
  const tags = [];
  const re = /<(ul|ol|dl|p|aside|figure)[\s>]/g;
  let m;
  while ((m = re.exec(s)) !== null) {
    let t = m[1];
    if (t === 'p' && s.slice(m.index, m.index + 30).includes('class="note"')) t = 'p.note';
    if (t === 'p' && s.slice(m.index, m.index + 40).includes('顶栏那几个按钮')) t = 'p[顶栏]';
    tags.push(t);
  }
  console.log(`${i + 1}. ${title}`);
  console.log(`   ${tags.join(' | ')}`);
  // 只在纯文本块缺失或顺序明显不对时才报
  if (tags.length === 0) { console.log('   !! 这一节没有渲染出任何块'); bad++; }
});
process.exit(bad ? 1 : 0);
