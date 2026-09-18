#!/usr/bin/env bash
# 开发环境变量 —— 所有构建/运行命令都先 source 这一份，保证行为一致。
#
# 为什么需要它：这台机器上 CLI 直连 GitHub/NuGet 会被阻断（POST 类请求超时），
# 必须走本地代理；另外把 NuGet 缓存固定到工作区内，既能在沙箱下可写，
# 也能让多次 restore 命中缓存（首次已经下好 ~1GB，重下很贵）。
#
#   用法：source tools/dev-env.sh
set -a

# --- .NET SDK（用户级安装，不需要 sudo）---
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$HOME/.local/bin:$PATH"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# --- 把 dotnet 的 HOME / 缓存固定在仓库外层，避免写进 ~ (可能不可写) ---
# REPO_OUTER = 本脚本所在目录的上上级（即包含 remember-er/ 的那个目录）
_DEV_SH_SELF="${BASH_SOURCE[0]}"
_DEV_SH_DIR="$(cd "$(dirname "$_DEV_SH_SELF")" && pwd)"
export REPO_OUTER="$(cd "$_DEV_SH_DIR/../.." && pwd)"
export DOTNET_CLI_HOME="$REPO_OUTER/.dev/cli-home"
export NUGET_PACKAGES="$REPO_OUTER/.dev/nuget/packages"
export XDG_DATA_HOME="$REPO_OUTER/.dev/local/share"
export XDG_CONFIG_HOME="$REPO_OUTER/.dev/local/config"
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES" "$XDG_DATA_HOME" "$XDG_CONFIG_HOME" 2>/dev/null

# --- 代理：直连 GitHub 的 OAuth/POST 端点会被阻断 ---
export HTTP_PROXY="http://127.0.0.1:7897"
export HTTPS_PROXY="http://127.0.0.1:7897"
export http_proxy="$HTTP_PROXY"
export https_proxy="$HTTPS_PROXY"
export ALL_PROXY="socks5://127.0.0.1:7897"
export NO_PROXY="localhost,127.0.0.1,::1,192.168.0.0/16,10.0.0.0/8,172.16.0.0/12,*.local"
export no_proxy="$NO_PROXY"

set +a
