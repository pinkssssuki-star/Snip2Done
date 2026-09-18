# RemeberEr C#/WPF 原型启动器
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File csharp\wpf\run.ps1
#   powershell -ExecutionPolicy Bypass -File csharp\wpf\run.ps1 -Mode native
#
# 或者直接双击 csharp\wpf\ 下的三个 .cmd。

param(
  # layered = 分层窗口 + 逐像素透明（推荐，和 Electron 版观感一致）
  # native  = 完全不碰 WebView2 的纯 WPF 面板，用来量开销地板
  # acrylic = DWM Acrylic 背景材质（实测被 WebView2 盖住，看不出效果）
  # opaque  = 普通不透明窗口
  [ValidateSet('layered', 'native', 'acrylic', 'opaque')]
  [string]$Mode = 'layered',

  # 跑完自动退出并打印基准数据（内存 / 启动 / 截图延迟）
  [switch]$Bench,

  # 截图只抓主屏，和之前 Electron 那组 2560x1440 的数据口径一致
  [switch]$Primary,

  # 强制重新编译
  [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$root = (Resolve-Path (Join-Path $here '..\..')).Path
$exe  = Join-Path $here 'out\RemeberEr.Wpf.exe'

# out/ 不入库，所以第一次跑（或 git clone 之后）这里会自动编译一遍
if ($Rebuild -or -not (Test-Path $exe)) {
  Write-Host '还没有编译过，先跑一遍 build.ps1 …' -ForegroundColor Yellow
  & (Join-Path $here 'build.ps1')
  if (-not (Test-Path $exe)) { throw "编译失败，$exe 没有生成" }
}

$cliArgs = @("--root=$root", "--mode=$Mode")
if ($Bench)   { $cliArgs += '--bench'; $cliArgs += '--benchdelay=3000' }
if ($Primary) { $cliArgs += '--primary' }

Write-Host ("启动：{0}  --mode={1}{2}" -f 'RemeberEr.Wpf.exe', $Mode, $(if ($Bench) { '  [基准测试]' } else { '' })) -ForegroundColor Cyan
Write-Host '  面板在屏幕右下角；热键 Ctrl+Shift+A 截图 / Ctrl+Shift+H 显示隐藏 / Ctrl+Shift+Q 退出'
Write-Host ''

& $exe $cliArgs
