#!/usr/bin/env bash
# 把 macOS 版打成 .app（可双击运行）。
#
# 与 Windows 版 pack.ps1 对应：那边打的是 win64 zip（一个 exe + 说明），
# 这边打的是标准 macOS 应用包（因为它要作为 bundle 去申请系统权限）。
#
#   ./build-app.sh              # 依赖系统里的 .NET 运行时（默认，产物小、构建快）
#   ./build-app.sh --self-contained   # 把 .NET 运行时一起打进去（产物大、但换机器能用）
#
# 为什么需要 .app 而不是直接发可执行文件：
#   1. 系统权限（屏幕录制 / 辅助功能）是按 **bundle 身份** 授予并记录的。
#      直接跑裸可执行文件时，授权会落到「终端」上，换个终端就要重新授权，
#      而且系统设置里显示的名字是「终端」，用户根本认不出那是谁。
#   2. LSUIElement=true 让这个常驻工具不出现在 Dock 里 —— 裸可执行文件做不到。
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"

# shellcheck source=../../tools/dev-env.sh
source "$REPO/tools/dev-env.sh"

SELF_CONTAINED=0
for a in "$@"; do
  case "$a" in
    --self-contained) SELF_CONTAINED=1 ;;
    -h|--help) sed -n '2,16p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "未知参数：$a" >&2; exit 2 ;;
  esac
done

APP_NAME="记忆面板"
APP="$REPO/dist/$APP_NAME.app"

# ---- 版本号一致性：Info.plist 与代码里的 Version 常量必须是同一个值 ----
CODE_VER="$(grep -oE 'Version = "[0-9][^"]*"' "$HERE/Program.cs" | head -1 | sed -E 's/.*"([^"]*)".*/\1/')"
PLIST_VER="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$HERE/Info.plist" 2>/dev/null || echo '?')"
echo "=== 版本校验 ==="
echo "  代码里的 Version   = ${CODE_VER:-?}"
echo "  Info.plist 里的    = ${PLIST_VER:-?}"
if [ -z "$CODE_VER" ] || [ "$CODE_VER" != "$PLIST_VER" ]; then
  echo "!! 两处版本号不一致，请先改成同一个值再打包（原版 pack.ps1 也是从代码读版本，避免两处各写一份）" >&2
  exit 1
fi
echo "  一致 ✓"

# ---- 编译/发布 ----
RID="osx-arm64"
[ "$(uname -m)" = "x86_64" ] && RID="osx-x64"

echo
echo "=== 发布（RID=${RID}，自包含=${SELF_CONTAINED}）==="
cd "$HERE"
if [ "$SELF_CONTAINED" = 1 ]; then
  dotnet publish -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -o "$HERE/bin/publish"
  PAYLOAD="$HERE/bin/publish"
else
  # 依赖系统运行时：产物小得多，本机自用/开发最合适
  dotnet publish -c Release -r "$RID" --self-contained false \
    -p:PublishSingleFile=false -o "$HERE/bin/publish"
  PAYLOAD="$HERE/bin/publish"
fi

# ---- 组装 .app ----
echo
echo "=== 组装 .app ==="
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp -R "$PAYLOAD"/. "$APP/Contents/MacOS/"
cp "$HERE/Info.plist" "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"

# 可执行文件必须叫 CFBundleExecutable 里写的名字
if [ ! -x "$APP/Contents/MacOS/RemeberEr.Mac" ]; then
  echo "!! 找不到可执行文件 RemeberEr.Mac" >&2
  ls -la "$APP/Contents/MacOS" | head >&2
  exit 1
fi

# ---- 顺手做个图标（可选，失败不影响打包）----
if [ -f "$HERE/make-icon.js" ] && command -v node >/dev/null 2>&1; then
  :
fi

# ---- 重新做签名 ----
#
# .NET 的 apphost 自带一个 ad-hoc 签名，但它的 CodeDirectory 里 Identifier=apphost、
# 而且只覆盖那一个可执行文件。这有两个后果：
#   · 系统权限（TCC）记录的是**签名标识**，会显示成 apphost，与 Info.plist 里的
#     CFBundleIdentifier 对不上；
#   · bundle 里其它内容没被覆盖。
# 所以整个 bundle 重新 ad-hoc 签一次，让签名标识与 bundle id 一致。
# ad-hoc 签名足以让本机 TCC 正常识别；要分发给别人还需要 Developer ID + 公证。
if command -v codesign >/dev/null 2>&1; then
  codesign --remove-signature "$APP/Contents/MacOS/RemeberEr.Mac" 2>/dev/null || true
  if codesign --force --deep --sign - "$APP" 2>/dev/null; then
    echo "  已重新 ad-hoc 签名（标识应为 com.remeberer.mac）"
  else
    echo "  !! 重新签名失败，权限可能显示成 apphost —— 不影响开发自用"
  fi
fi

# ---- 去掉隔离属性，免得第一次打开被 Gatekeeper 拦 ----
xattr -cr "$APP" 2>/dev/null || true

echo
echo "✓ 产物：${APP}"
echo "  体积：$(du -sh "$APP" | cut -f1)"
if command -v codesign >/dev/null 2>&1; then
  echo "  签名标识：$(codesign -dv "$APP" 2>&1 | grep -m1 '^Identifier=' || echo '(读不到)')"
fi
echo
echo "首次使用需要授权两项系统权限（抓屏 / 读窗口标题）："
echo "  open \"$APP\""
echo "  面板出现后点顶栏的 ? 可查看权限状态"
