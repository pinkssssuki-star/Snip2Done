# 纯原生 WPF 版启动器（无 WebView2、无 Electron、无 Chromium）
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File csharp\native\run.ps1
#   powershell -ExecutionPolicy Bypass -File csharp\native\run.ps1 -Bench -Primary
#   powershell -ExecutionPolicy Bypass -File csharp\native\run.ps1 -View set

param(
  # 打开时直接进哪一页：main / bin / done / set
  [ValidateSet('main', 'bin', 'done', 'set')]
  [string]$View,

  # 跑完自动退出并打印内存 / 启动 / 截图延迟
  [switch]$Bench,

  # 截图只抓主屏（和 Electron 那组 2560x1440 口径一致）
  [switch]$Primary,

  # 关掉基准测试里的 4 次截图，只量内存和启动
  [switch]$NoCapture,

  # 强制重新编译
  [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$root = (Resolve-Path (Join-Path $here '..')).Path
$exe  = Join-Path $here 'out\Snip2Done.exe'

if ($Rebuild -or -not (Test-Path $exe)) {
  Write-Host '先跑一遍 build.ps1 …' -ForegroundColor Yellow
  & (Join-Path $here 'build.ps1')
  if (-not (Test-Path $exe)) { throw "编译失败，$exe 没有生成" }
}

$cliArgs = @("--root=$root")
if ($View)      { $cliArgs += "--view=$View" }
if ($Bench)     { $cliArgs += '--bench'; $cliArgs += '--benchdelay=3000' }
if ($Primary)   { $cliArgs += '--primary' }
if ($NoCapture) { $cliArgs += '--nocapture' }

Write-Host '启动：Snip2Done.exe（纯原生 WPF，1 进程、无 Chromium）' -ForegroundColor Cyan
Write-Host '  面板在屏幕右下角；热键 Ctrl+Shift+A 截图 / Ctrl+Shift+H 显示隐藏 / Ctrl+Shift+Q 退出'
Write-Host ''

& $exe $cliArgs
