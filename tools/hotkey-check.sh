#!/usr/bin/env bash
# 独立验证全局热键（在你的终端里跑，绕开 DSH 沙箱）。
#
# 用法：  ./tools/hotkey-check.sh
# 它会启动面板 25 秒，请在那期间按 ⌘⇧A 三次，然后自动打印结论。
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
EXE="$REPO/native/mac/bin/Release/net10.0/RemeberEr.Mac"
DATA="${TMPDIR:-/tmp}/remeber-hotkey-check"

if [ ! -x "$EXE" ]; then
  echo "!! 找不到 $EXE —— 先在 native/mac 下跑 ./build.sh" >&2
  exit 1
fi

rm -rf "$DATA"; mkdir -p "$DATA"
echo "=== 权限状态 ==="
"$EXE" --data="$DATA" --dumpsnap=/dev/null 2>&1 | grep -E "权限" || true

rm -f "$DATA/native.log"
"$EXE" --data="$DATA" > "$DATA/stdout.txt" 2>&1 &
PID=$!
sleep 3

echo
echo "════════════════════════════════════════════════"
echo "  面板已启动。请按 ⌘⇧A（Command+Shift+A）三次"
echo "  25 秒后自动结束"
echo "════════════════════════════════════════════════"
echo "  后端：$(grep '热键后端' "$DATA/native.log" 2>/dev/null | sed 's/.*= //')"
sleep 25

echo
echo "=== 结果 ==="
HITS=$(grep -c "NSEvent 命中" "$DATA/native.log" 2>/dev/null || echo 0)
echo "  热键命中：$HITS 次"
if [ "$HITS" -gt 0 ]; then
  echo "  ✓✓ 热键工作正常"
else
  echo "  ✗ 没命中。请把下面两行发给我："
  echo "     grep 权限     : $(grep -E '权限' "$DATA/native.log" | tr '\n' ' ')"
  echo "     Carbon handler : $(grep -c 'handler invoked' "$DATA/stdout.txt" 2>/dev/null) 次"
fi
kill "$PID" 2>/dev/null
wait "$PID" 2>/dev/null
echo
echo "完整日志：$DATA/native.log"
