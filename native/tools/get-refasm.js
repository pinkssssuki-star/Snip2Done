'use strict';

/*
 * 下载 .NET Framework 4.8 的参考程序集（reference assemblies）。
 *
 * 为什么需要：这台机器装了 VS 2022，也有 Microsoft.WinFX.targets 和 PresentationBuildTasks.dll，
 * 唯独没有 `C:\Program Files (x86)\Reference Assemblies\...\.NETFramework\v4.8` 目标包。
 * 没有它 MSBuild 编不了 net48 工程，XAML 的 MarkupCompilePass 也就跑不起来。
 *
 * 官方把这个目标包作为 NuGet 包发布，所以直接从 nuget.org 取，解到 csharp/refasm/，
 * 再用 MSBuild 的 TargetFrameworkRootPath 指过去。
 *
 * 用法：node native/tools/get-refasm.js
 */

const fs = require('fs');
const path = require('path');

const NATIVE = path.resolve(__dirname, '..');          // native/
const ROOT = path.resolve(NATIVE, '..');               // 仓库根
const DEST = path.join(NATIVE, 'refasm');
const TMP = path.join(ROOT, '.tmp');
const PKG = 'microsoft.netframework.referenceassemblies.net48';
const VER = '1.0.3';

async function main() {
  fs.mkdirSync(TMP, { recursive: true });

  const nupkg = path.join(TMP, `${PKG}.${VER}.nupkg`);
  if (!fs.existsSync(nupkg)) {
    const url = `https://api.nuget.org/v3-flatcontainer/${PKG}/${VER}/${PKG}.${VER}.nupkg`;
    console.log('下载 ' + url);
    const res = await fetch(url);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const buf = Buffer.from(await res.arrayBuffer());
    fs.writeFileSync(nupkg, buf);
    console.log('  -> ' + nupkg + '  ' + (buf.length / 1048576).toFixed(2) + ' MB');
  } else {
    console.log('已有缓存 ' + nupkg);
  }

  // .nupkg 就是 zip，只是 Expand-Archive 不认这个后缀
  fs.rmSync(DEST, { recursive: true, force: true });
  fs.mkdirSync(DEST, { recursive: true });

  const { execFileSync } = require('child_process');
  execFileSync('powershell', [
    '-NoProfile', '-Command',
    `Add-Type -AssemblyName System.IO.Compression.FileSystem; ` +
    `[System.IO.Compression.ZipFile]::ExtractToDirectory('${nupkg}', '${DEST}')`,
  ], { stdio: 'inherit' });

  const buildDir = path.join(DEST, 'build', '.NETFramework');
  if (!fs.existsSync(buildDir)) {
    throw new Error('包里没找到 build/.NETFramework，解压结构可能变了');
  }
  console.log('\n参考程序集目录：' + buildDir);
  for (const v of fs.readdirSync(buildDir)) {
    const d = path.join(buildDir, v);
    const n = fs.readdirSync(d).filter((f) => f.endsWith('.dll')).length;
    const hasList = fs.existsSync(path.join(d, 'RedistList', 'FrameworkList.xml'));
    console.log(`  ${v}: ${n} 个 dll, RedistList=${hasList}`);
  }
  console.log('\nMSBuild 用法：/p:TargetFrameworkRootPath=' + buildDir + '\\');
}

main().catch((e) => {
  console.error('失败：' + e.message);
  process.exit(1);
});
