#!/usr/bin/env bash
# 开发环境变量 —— 所有构建/运行命令都先 source 这一份，保证行为一致。
#
#   用法：source tools/dev-env.sh
#
# 这份脚本要同时满足两种场景，所以每一项都写成了「有则用之、无则跳过」：
#   1. 本机开发（.NET SDK 装在 ~/.dotnet、CLI 直连 GitHub/NuGet 会被阻断，要走本地代理）
#   2. 别人克隆之后照做（SDK 在 PATH 里、直连网络正常、没有本地代理）
# 早期版本把这三件事都写死了，别人克隆后 source 会因为找不到 SDK 或连不上代理而失败。
set -a

# --- .NET SDK ---
# 优先用 PATH 里已有的（多数人是这种装法：官方安装包 / brew）；
# 找不到再退回用户级安装目录（不需要 sudo 的那种装法）。
if command -v dotnet >/dev/null 2>&1; then
  :
elif [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$DOTNET_ROOT:$HOME/.local/bin:$PATH"
else
  echo "!! 找不到 dotnet。请先安装 .NET 10 SDK：" >&2
  echo "     https://dotnet.microsoft.com/download" >&2
  echo "   或使用官方脚本装到 ~/.dotnet，然后重新 source 本文件。" >&2
  return 1 2>/dev/null || exit 1
fi

export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# --- 缓存目录 ---
# 默认放在仓库**内部**的 .dev/ 下（已被 .gitignore 忽略）：
# 既不写进 ~、又让多次 restore 命中缓存，而且克隆到哪都成立。
# 之前取的是「仓库的上一级」，别人把仓库克隆成父目录时那里可能不可写。
_DEV_SH_SELF="${BASH_SOURCE[0]}"
_DEV_SH_DIR="$(cd "$(dirname "$_DEV_SH_SELF")" && pwd)"
_REPO_ROOT="$(cd "$_DEV_SH_DIR/.." && pwd)"

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$_REPO_ROOT/.dev/cli-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$_REPO_ROOT/.dev/nuget/packages}"
export XDG_DATA_HOME="${XDG_DATA_HOME:-$_REPO_ROOT/.dev/local/share}"
export XDG_CONFIG_HOME="${XDG_CONFIG_HOME:-$_REPO_ROOT/.dev/local/config}"
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES" "$XDG_DATA_HOME" "$XDG_CONFIG_HOME" 2>/dev/null

# --- 代理（自动检测）---
# 开发机直连 GitHub/NuGet 会被阻断，必须走本地代理；但这只对**开发机**成立。
# 无条件设上去会把别人正常的直连也弄坏，所以这里**先探一下有没有本地代理**：
#   · 探测到已知端口在听 → 用它（开发机就是这种）
#   · 没探测到           → 什么都不设，直连（别人的机器）
# 也支持显式覆盖：
#   REMEBER_PROXY=http://别的地址:端口 source tools/dev-env.sh
#   REMEBER_NO_PROXY=1                 source tools/dev-env.sh   # 强制直连
_proxy_port="${REMEBER_PROXY_PORT:-7897}"
_use_proxy=0
if [ "${REMEBER_NO_PROXY:-0}" = "1" ]; then
  _use_proxy=0
elif [ -n "${REMEBER_PROXY:-}" ]; then
  _use_proxy=1
elif command -v nc >/dev/null 2>&1 && nc -z 127.0.0.1 "$_proxy_port" >/dev/null 2>&1; then
  _use_proxy=1
fi

if [ "$_use_proxy" = "1" ]; then
  _proxy_url="${REMEBER_PROXY:-http://127.0.0.1:$_proxy_port}"
  export HTTP_PROXY="$_proxy_url"
  export HTTPS_PROXY="$_proxy_url"
  export http_proxy="$HTTP_PROXY"
  export https_proxy="$HTTPS_PROXY"
  export ALL_PROXY="${ALL_PROXY:-socks5://127.0.0.1:$_proxy_port}"
  export NO_PROXY="${NO_PROXY:-localhost,127.0.0.1,::1,192.168.0.0/16,10.0.0.0/8,172.16.0.0/12,*.local}"
  export no_proxy="$NO_PROXY"
fi
unset _proxy_port _use_proxy _proxy_url 2>/dev/null || true

set +a
