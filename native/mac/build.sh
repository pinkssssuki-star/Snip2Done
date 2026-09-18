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

OUT="$HERE/bin/$CONFIG/net10.0/RemeberEr.Mac"
if [ -x "$OUT" ]; then
  echo ""
  echo "✓ 产物：$OUT"
  echo "  体积：$(du -h "$OUT" | cut -f1)"
else
  echo "!! 编译似乎成功但产物不存在：$OUT" >&2
  exit 1
fi
