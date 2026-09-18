# 编译纯原生 WPF 版（不用 WebView2）。
#
# 这台机器没有 .NET SDK，也没有 .NET Framework 目标包，所以：
#   1. 用 tools/get-refasm.js 把官方那个参考程序集 NuGet 包解到 native/refasm/
#   2. 用 VS 2022 自带的 MSBuild，把 TargetFrameworkRootPath 指过去
# 这样 MarkupCompilePass 就能正常跑，XAML 会被编成 BAML 嵌进 exe。

$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$root = (Resolve-Path (Join-Path $here '..')).Path
$msbuild = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
$refasm  = Join-Path $here 'refasm\build'

if (-not (Test-Path $msbuild)) { throw "找不到 MSBuild：$msbuild" }

if (-not (Test-Path (Join-Path $refasm '.NETFramework\v4.8\mscorlib.dll'))) {
  Write-Host '缺少 .NET Framework 4.8 参考程序集，先取一下…' -ForegroundColor Yellow
  & node (Join-Path $here 'tools\get-refasm.js')
  if (-not (Test-Path (Join-Path $refasm '.NETFramework\v4.8\mscorlib.dll'))) {
    throw '参考程序集仍然缺失'
  }
}

$proj = Join-Path $here 'RemeberEr.Native.csproj'
$out  = Join-Path $here 'out'

Write-Host "编译 -> $out\RemeberEr.Native.exe"
$log = & $msbuild $proj /nologo /v:minimal /p:Configuration=Release "/p:TargetFrameworkRootPath=$refasm\" 2>&1
$code = $LASTEXITCODE
foreach ($line in $log) { Write-Host $line }
if ($code -ne 0) { throw "编译失败，退出码 $code" }

$exe = Join-Path $out 'RemeberEr.Native.exe'
if (-not (Test-Path $exe)) { throw "没生成 $exe" }

Write-Host ''
Write-Host '产物：'
$files = Get-ChildItem $out -File
$files | Sort-Object Length -Descending |
  Select-Object @{N='文件';E={$_.Name}}, @{N='KB';E={[math]::Round($_.Length/1KB,1)}} |
  Format-Table -AutoSize
$tot = ($files | Measure-Object Length -Sum).Sum
Write-Host ("合计 {0:N2} MB / {1} 个文件" -f ($tot/1MB), $files.Count)
