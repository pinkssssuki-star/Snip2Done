# 编译 RemeberEr.Wpf —— 不依赖 .NET SDK，直接用 VS 自带的 Roslyn csc + 系统里的 WPF 程序集。
# 这台机器没装 .NET SDK，也没装 .NET Framework 目标包，所以只能这样编。

$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$root = (Resolve-Path (Join-Path $here '..\..')).Path
$out  = Join-Path $here 'out'
$sdk  = Join-Path $root 'csharp\webview2'

$csc = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
$fw  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $fw 'WPF'
$gac = 'C:\Windows\Microsoft.NET\assembly'

if (-not (Test-Path $csc)) { throw "找不到 Roslyn 编译器：$csc" }

function Find-Asm([string]$name) {
  foreach ($c in @((Join-Path $wpf "$name.dll"), (Join-Path $fw "$name.dll"))) {
    if (Test-Path $c) { return $c }
  }
  $hit = Get-ChildItem $gac -Recurse -Filter "$name.dll" -ErrorAction SilentlyContinue |
         Sort-Object FullName -Descending | Select-Object -First 1
  if ($hit) { return $hit.FullName }
  throw "找不到程序集：$name"
}

$need = @(
  'PresentationFramework', 'PresentationCore', 'WindowsBase', 'System.Xaml',
  'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms', 'System.Management'
)
$refs = @()
foreach ($n in $need) { $refs += '/r:' + (Find-Asm $n) }

foreach ($d in @('Microsoft.Web.WebView2.Core', 'Microsoft.Web.WebView2.Wpf')) {
  $p = Join-Path $sdk "lib\net462\$d.dll"
  if (-not (Test-Path $p)) { throw "WebView2 SDK 缺少 $d.dll，先解压 csharp\webview2" }
  $refs += '/r:' + $p
}

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $out -Force | Out-Null

$exe = Join-Path $out 'RemeberEr.Wpf.exe'
$src = @(Get-ChildItem (Join-Path $here 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })

Write-Host "编译 $($src.Count) 个源文件 -> $exe"
$log = & $csc /nologo /target:exe /platform:x64 /langversion:7.3 /optimize+ /warnaserror- `
  /debug:pdbonly "/out:$exe" $refs $src 2>&1
$code = $LASTEXITCODE
foreach ($line in $log) { Write-Host $line }
if ($code -ne 0) { throw "编译失败，退出码 $code" }

# WebView2 运行时需要的托管 DLL + 原生加载器，必须和 exe 放在一起
foreach ($d in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll')) {
  Copy-Item (Join-Path $sdk "lib\net462\$d") $out -Force
}
Copy-Item (Join-Path $sdk 'runtimes\win-x64\native\WebView2Loader.dll') $out -Force

Write-Host ''
Write-Host '产物：'
$files = Get-ChildItem $out -File
$files | Sort-Object Length -Descending |
  Select-Object @{N='文件';E={$_.Name}}, @{N='KB';E={[math]::Round($_.Length/1KB,1)}} |
  Format-Table -AutoSize
$tot = ($files | Measure-Object Length -Sum).Sum
Write-Host ("合计 {0:N2} MB / {1} 个文件" -f ($tot/1MB), $files.Count)
