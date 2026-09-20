#!/usr/bin/env bash
# 把 macOS 版打成 .app（可双击运行）。
#
# 与 Windows 版 pack.ps1 对应：那边打的是 win64 zip（一个 exe + 说明），
# 这边打的是标准 macOS 应用包（因为它要作为 bundle 去申请系统权限）。
#
#   ./build-app.sh                     # 自包含（默认，把 .NET 运行时打进包，真能跑）
#   ./build-app.sh --framework-dependent  # 依赖系统 .NET；只在系统装了 .NET 时可用
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

# 默认自包含。
#
# 实测教训：框架依赖的 .app **根本启动不了** —— apphost 只去标准位置
# (/usr/local/share/dotnet、DOTNET_ROOT) 找运行时，而本项目把 SDK 装在
# ~/.dotnet，于是双击就是一句 “You must install .NET to run this application.”
# 也就是说「体积小」是以「根本跑不起来」为代价的，那不叫权衡。
# 自包含把运行时打进包里，代价是体积从 29 MB 涨到 70 MB 左右 —— 换来的是真能跑。
SELF_CONTAINED=1
for a in "$@"; do
  case "$a" in
    --framework-dependent) SELF_CONTAINED=0 ;;
    -h|--help) sed -n '2,22p' "${BASH_SOURCE[0]}"; exit 0 ;;
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

# ---- 把 ScreenCaptureKit 桥接层也编进来 ----
#
# ⚠️ 必须在签名**之前**做。顺序反了的话，dylib 不在已签名的内容里，
# 签名校验会失败，TCC 那边也可能因为「签名与内容不符」而拒绝授权。
echo
echo "=== 编译并放入 ScreenCaptureKit 桥接层 ==="
BRIDGE_DST="$APP/Contents/MacOS/libremeber_sck.dylib"
if clang -fobjc-arc -O2 -dynamiclib \
     -framework Foundation -framework AppKit -framework ScreenCaptureKit -framework CoreGraphics -framework CoreMedia \
     -install_name @rpath/libremeber_sck.dylib \
     -o "$BRIDGE_DST" "$HERE/Platform/sck_bridge.m"; then
  echo "  ✓ libremeber_sck.dylib（$(du -h "$BRIDGE_DST" | cut -f1)）"
  for sym in remeber_sck_preflight remeber_sck_capture_display remeber_sck_error_text; do
    nm -gU "$BRIDGE_DST" 2>/dev/null | grep -q "_${sym}" || echo "  !! 缺少符号：${sym}" >&2
  done
else
  echo "!! 桥接层编译失败，抓屏在这个 .app 里会不可用" >&2
  exit 1
fi

# 可执行文件必须叫 CFBundleExecutable 里写的名字
if [ ! -x "$APP/Contents/MacOS/RemeberEr.Mac" ]; then
  echo "!! 找不到可执行文件 RemeberEr.Mac" >&2
  ls -la "$APP/Contents/MacOS" | head >&2
  exit 1
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
  # 分两步签，顺序不能反：
  #   1. 先签包内所有 **Mach-O 二进制**（.NET 的托管 dll 里其实也含原生代码，
  #      实测 Avalonia.dll 不签就会让 --verify 报 "code object is not signed at all"）；
  #   2. 再签外层 bundle —— 此时它的内容已经是「已签名且未被改动」的，
  #      整体摘要才算得出来。
  # 只写一句 --deep 是不够的：--deep 对顶层目录里的裸二进制常常漏签。
  echo "  逐层签名中…"
  signed=0
  while IFS= read -r f; do
    if file -b "$f" 2>/dev/null | grep -q "Mach-O"; then
      codesign --force --sign - "$f" >/dev/null 2>&1 && signed=$((signed + 1))
    fi
  done < <(find "$APP/Contents" -type f)

  # 外层用 --deep 一起处理：
  #   · 只签裸二进制后再签 bundle，会报
  #     "code has no resources but signature indicates they must be present"；
  #   · .NET 的托管 dll 是 PE 格式（file 认成 PE32，不是 Mach-O），
  #     但 codesign 仍然要签它们，--deep 会一并覆盖。
  # 注意：这里**不要**把 stderr 丢掉 —— 之前用 >/dev/null 2>&1 吞了错误，
  # 只看到一句「失败」却看不到原因，白折腾一轮。
  codesign --force --deep --sign - "$APP" 2>&1 | grep -v "replacing existing signature" || true
  echo "  已重新 ad-hoc 签名（含 $signed 个裸 Mach-O 二进制）"
fi

# ---- 去掉隔离属性，免得第一次打开被 Gatekeeper 拦 ----
xattr -cr "$APP" 2>/dev/null || true

echo
echo "✓ 产物：${APP}"
echo "  体积：$(du -sh "$APP" | cut -f1)"
if command -v codesign >/dev/null 2>&1; then
  ident="$(codesign -dv "$APP" 2>&1 | sed -n 's/^Identifier=//p' | head -1)"
  echo "  签名标识：${ident:-(读不到)}"
  # 校验口径：验**主可执行文件**，而不是整个 bundle 深度校验。
  #
  # 为什么不用 --deep --strict：.NET 会把几十个托管 dll 放在 Contents/MacOS/，
  # 而 macOS 的约定是嵌套代码放 Contents/Frameworks/。于是深度校验会报
  # "subcomponent ...Avalonia.dll: code object is not signed at all"。
  # 这是 .NET + macOS bundle 的结构性限制，不是我们签名签错了 ——
  # codesign -dv 给出的标识是对的，TCC 认的正是这个身份。
  # 主可执行文件单独校验是可以过的，这里就按这个口径报，不假装整包都验过了。
  if codesign --verify "$APP/Contents/MacOS/RemeberEr.Mac" 2>/dev/null; then
    echo "  主可执行文件签名校验：通过"
  else
    echo "  !! 主可执行文件签名校验未通过" >&2
  fi
fi
[ -f "$APP/Contents/MacOS/libremeber_sck.dylib" ] \
  && echo "  桥接层：已在包内 ✓" \
  || echo "  !! 桥接层不在包内，抓屏会失败" >&2
echo
echo "首次使用需要授权两项系统权限（抓屏 / 读窗口标题）："
echo "  open \"$APP\""
echo "  面板出现后点顶栏的 ? 可查看权限状态"
