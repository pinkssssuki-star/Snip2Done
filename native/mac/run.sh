#!/usr/bin/env bash
# 记忆面板 · macOS 版启动器（Avalonia 原生，无 Electron、无 Chromium）
#
# 与 Windows 版 native/run.ps1 的用法一一对应：
#   Windows:  powershell -File native\run.ps1 -Bench -Primary
#   macOS:    native/mac/run.sh --bench --primary
#
#   ./run.sh                    # 正常启动
#   ./run.sh --view set         # 直接进设置页（main/bin/done/set）
#   ./run.sh --bench --primary  # 基准测试，只抓主屏
#   ./run.sh --rebuild          # 强制重新编译
#   ./run.sh --request-perms    # 只申请系统权限（屏幕录制），不起窗口
#
# 权限说明（首次使用必须做一次）：
#   屏幕录制 —— 抓屏需要。授权后**必须重启本进程**才生效，这是 macOS 的既定行为。
#   辅助功能 —— 读取「鼠标下窗口标题」需要。同样授权后要重启。
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"

# shellcheck source=../../tools/dev-env.sh
source "$REPO/tools/dev-env.sh"

VIEW=""
BENCH=0
PRIMARY=0
NOCAPTURE=0
REBUILD=0
REQUEST_PERMS=0
EXTRA=()

while [ $# -gt 0 ]; do
  case "$1" in
    --view)          VIEW="${2:-}"; shift 2 ;;
    --view=*)        VIEW="${1#*=}"; shift ;;
    --bench)         BENCH=1; shift ;;
    --primary)       PRIMARY=1; shift ;;
    --nocapture)     NOCAPTURE=1; shift ;;
    --rebuild)       REBUILD=1; shift ;;
    --request-perms) REQUEST_PERMS=1; shift ;;
    -h|--help)       sed -n '2,20p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *)               EXTRA+=("$1"); shift ;;
  esac
done

EXE="$HERE/bin/Release/net10.0/RemeberEr.Mac"

if [ "$REBUILD" = 1 ] || [ ! -x "$EXE" ]; then
  echo "先跑一遍 build.sh …"
  "$HERE/build.sh"
  [ -x "$EXE" ] || { echo "编译失败：$EXE 没有生成" >&2; exit 1; }
fi

CLI=("--root=$REPO")
[ -n "$VIEW" ]       && CLI+=("--view=$VIEW")
[ "$BENCH" = 1 ]     && CLI+=("--bench" "--benchdelay=3000")
[ "$PRIMARY" = 1 ]   && CLI+=("--primary")
[ "$NOCAPTURE" = 1 ] && CLI+=("--nocapture")
[ "$REQUEST_PERMS" = 1 ] && CLI+=("--request-perms")
[ ${#EXTRA[@]} -gt 0 ] && CLI+=("${EXTRA[@]}")

echo "启动：RemeberEr.Mac（Avalonia 原生）"
echo "  面板在屏幕右下角"
echo "  热键 ⌘⇧A 截图 / ⌘⇧H 显示隐藏 / ⌘⇧Q 退出"
echo ""

exec "$EXE" "${CLI[@]}"
