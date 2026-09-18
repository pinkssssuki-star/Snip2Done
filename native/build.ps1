# 编译纯原生 WPF 版（不用 WebView2）。
#
# 两条路，脚本自己挑：
#   1. 有 VS / Build Tools 的 MSBuild —— 把参考程序集目录用 TargetFrameworkRootPath 指过去
#   2. 只有 .NET SDK —— 走 dotnet msbuild，同样指过去
# 参考程序集来自 tools/get-refasm.js（官方把它作为 NuGet 包发布），缺了会自动取。
# 这样这份仓库在别的机器上也能直接编，不用改脚本。

$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$root = (Resolve-Path (Join-Path $here '..')).Path
$refasm  = Join-Path $here 'refasm\build'

# --- 找 MSBuild：别写死路径，换台机器就编不了了 ---
function Find-MSBuildTool {
    # 1) vswhere（VS 安装器自带，最靠谱，能找到任何版本的 VS / Build Tools）
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $hit = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
                          -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null | Select-Object -First 1
        if ($hit -and (Test-Path $hit)) { return @{ Exe = $hit; Kind = 'msbuild' } }
    }
    # 2) 常见安装位置
    foreach ($ver in @('2022', '2019')) {
        foreach ($ed in @('Enterprise', 'Professional', 'Community', 'BuildTools')) {
            $p = "C:\Program Files\Microsoft Visual Studio\$ver\$ed\MSBuild\Current\Bin\MSBuild.exe"
            if (Test-Path $p) { return @{ Exe = $p; Kind = 'msbuild' } }
        }
    }
    # 3) PATH 上的 msbuild
    $cmd = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($cmd) { return @{ Exe = $cmd.Source; Kind = 'msbuild' } }
    # 4) 退到 .NET SDK
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return @{ Exe = $cmd.Source; Kind = 'dotnet' } }
    return $null
}

$tool = Find-MSBuildTool
if (-not $tool) {
    throw @'
找不到任何能编译的工具。需要下面任意一个：
  · Visual Studio 2022（含「.NET 桌面开发」工作负载）
  · Visual Studio Build Tools 2022（勾 .NET 桌面生成工具）
  · .NET SDK 6.0 或更高
'@
}
Write-Host ("编译器：" + $tool.Exe) -ForegroundColor DarkGray

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
$msbuildArgs = @($proj, '/nologo', '/v:minimal', '/p:Configuration=Release',
                 "/p:TargetFrameworkRootPath=$refasm\")
if ($tool.Kind -eq 'dotnet') {
    # 只有 .NET SDK 的机器走这条：dotnet msbuild 一样能编经典 csproj
    $log = & $tool.Exe msbuild @msbuildArgs 2>&1
} else {
    $log = & $tool.Exe @msbuildArgs 2>&1
}
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
