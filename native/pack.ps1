# 打包成一个可以发出去的 zip。
#
# 发的是什么：一个 exe + 一份使用说明。就这些。
#   原生版没有运行时依赖（.NET Framework 4.8 是 Win10/11 自带的），
#   所以不像 Electron 那样要捆 100 MB 的 Chromium。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File native\pack.ps1
#   powershell -ExecutionPolicy Bypass -File native\pack.ps1 -NoBuild

param(
    [switch]$NoBuild   # 跳过编译，直接用 out\ 里现成的
)

$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$root = (Resolve-Path (Join-Path $here '..')).Path
$out  = Join-Path $here 'out'
$exe  = Join-Path $out 'Snip2Done.exe'
$dist = Join-Path $root 'dist'
$stage = Join-Path $root '.tmp\pack'

# 版本号从 Program.cs 里读，免得两处不一致
$verLine = Select-String -Path (Join-Path $here 'src\Program.cs') -Pattern 'public const string Version = "([^"]+)"'
if (-not $verLine) { throw '没在 src\Program.cs 里找到 Version 常量' }
$ver = $verLine.Matches[0].Groups[1].Value
Write-Host "版本 v$ver" -ForegroundColor Cyan

if (-not $NoBuild) {
    & (Join-Path $here 'build.ps1')
}
if (-not (Test-Path $exe)) { throw "没找到 $exe，先编译" }

# --- 摆台 ---
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage -Force | Out-Null

$name = 'Snip2Done.exe'
Copy-Item $exe (Join-Path $stage $name) -Force

# 说明文档：HTML 是主文档（能带截图、排版好看），TXT 是纯文本兜底
foreach ($doc in @('使用说明.html', '使用说明.txt')) {
    $p = Join-Path $here "pack\$doc"
    if (-not (Test-Path $p)) {
        throw ("缺少 pack\$doc —— 先跑这三步生成：`n" +
               "  node native\tools\make-guide-data.js`n" +
               "  native\out\Snip2Done.exe --data=.tmp\guide-data --flow=ui --shotpad=0`n" +
               "  node native\tools\make-guide.js")
    }
    Copy-Item $p $stage -Force
}

$exeKB = [math]::Round((Get-Item (Join-Path $stage $name)).Length / 1KB, 1)
Write-Host "  $name  $exeKB KB" -ForegroundColor Green

# --- 打 zip ---
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$zip = Join-Path $dist "Snip2Done-$ver-win64.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

$zipKB = [math]::Round((Get-Item $zip).Length / 1KB, 1)
Write-Host ''
Write-Host "打包完成 -> $zip  ($zipKB KB)" -ForegroundColor Green

# --- 自检：解出来看看里面到底是什么 ---
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::OpenRead($zip)
Write-Host ''
Write-Host '包内清单：'
foreach ($e in $z.Entries) {
    Write-Host ("  {0,-24} {1,8} KB" -f $e.Name, [math]::Round($e.Length / 1KB, 1))
}
$z.Dispose()

$totalKB = [math]::Round((Get-Item $zip).Length / 1KB, 1)
Write-Host ''
Write-Host "整个包 $totalKB KB —— 直接把这个 zip 发出去就行" -ForegroundColor Cyan
Write-Host '朋友收到后：解压 -> 双击「使用说明.html」看一眼 -> 双击「记忆面板.exe」'
