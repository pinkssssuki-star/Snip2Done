#!/usr/bin/env bash
# 记忆面板 · macOS 版编译脚本。
#
# 与 Windows 版 native/build.ps1 对应，但简单得多：
# Windows 版要满世界找 MSBuild、还要从 NuGet 拉 .NET Framework 参考程序集，
# 是因为那台机器没有 .NET SDK；macOS 这边直接用 .NET 10 SDK 就行。
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"

# shellcheck source=../../tools/dev-env.sh
source "$REPO/tools/dev-env.sh"

CONFIG="${1:-Release}"
shift || true

# 注意：下面这行**必须**用 ${CONFIG} 而不是不带括号的写法。
# 全角括号「）」的 UTF-8 尾字节落在 0x80-0xBF，bash 会把它当成变量名的一部分，
# 于是 ${CONFIG} 后面紧跟全角括号时，括号的尾字节会被吃进变量名，
# 在 set -u 下直接报 unbound variable。
# 中文注释/输出里出现 $变量 时，一律用花括号划清边界。
echo "=== 编译 macOS 版（${CONFIG}）==="
echo "  DOTNET_ROOT    = $DOTNET_ROOT"
echo "  NUGET_PACKAGES = $NUGET_PACKAGES"
dotnet --version

cd "$HERE"
dotnet build -c "$CONFIG" "$@"

# ------------------------------------------------------------------ 编译 SCK 桥接层
#
# 抓屏走 ScreenCaptureKit，而它在 C# 侧不好直接调（异步 block + objc_msgSend），
# 所以有一层极小的 ObjC 桥接（Platform/sck_bridge.m），在这里编成 dylib。
# 产物与可执行文件同目录：P/Invoke 会先到可执行文件所在目录找 lib*.dylib，
# 所以不需要额外配置 DllImport 搜索路径，也不需要改 DYLD_LIBRARY_PATH。
echo ""
echo "=== 编译 ScreenCaptureKit 桥接层 ==="
BRIDGE_SRC="$HERE/Platform/sck_bridge.m"
BRIDGE_OUT="$HERE/bin/$CONFIG/net10.0/libremeber_sck.dylib"

if [ ! -f "$BRIDGE_SRC" ]; then
  echo "!! 找不到 $BRIDGE_SRC" >&2
  exit 1
fi

mkdir -p "$(dirname "$BRIDGE_OUT")"
if clang -fobjc-arc -O2 -dynamiclib \
     -framework Foundation -framework AppKit -framework ScreenCaptureKit -framework CoreGraphics -framework CoreMedia \
     -install_name @rpath/libremeber_sck.dylib \
     -o "$BRIDGE_OUT" "$BRIDGE_SRC"; then
  echo "  ✓ $BRIDGE_OUT"
  # 顺带确认三个导出符号都在 —— 少了任何一个，运行期才会炸，不如现在就看出来
  missing=0
  for sym in remeber_sck_preflight remeber_sck_capture_display remeber_sck_error_text; do
    if ! nm -gU "$BRIDGE_OUT" 2>/dev/null | grep -q "_${sym}"; then
      echo "  !! 缺少导出符号：${sym}" >&2
      missing=1
    fi
  done
  [ "$missing" = 0 ] && echo "  导出符号齐全（preflight / capture_display / error_text）"
else
  echo "!! 桥接层编译失败 —— 抓屏将不可用" >&2
  exit 1
fi

OUT="$HERE/bin/$CONFIG/net10.0/RemeberEr.Mac"
if [ -x "$OUT" ]; then
  echo ""
  echo "✓ 产物：$OUT"
  echo "  体积：$(du -h "$OUT" | cut -f1)"
  if [ -f "$HERE/bin/$CONFIG/net10.0/libremeber_sck.dylib" ]; then
    echo "  桥接层：libremeber_sck.dylib $(du -h "$HERE/bin/$CONFIG/net10.0/libremeber_sck.dylib" | cut -f1)"
  else
    echo "  !! 警告：libremeber_sck.dylib 不在产物目录，抓屏会失败" >&2
  fi
else
  echo "!! 编译似乎成功但产物不存在：$OUT" >&2
  exit 1
fi
