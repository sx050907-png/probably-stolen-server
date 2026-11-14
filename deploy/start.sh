#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
if [[ -f config.env ]]; then
  set -a
  # This is the operator's local configuration, never a downloaded command.
  source ./config.env
  set +a
fi
if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
  echo '本包适用于 Linux x86_64（glibc）。其他架构请使用对应构建。' >&2
  exit 1
fi
nightstud_bind_port=${SERVER_PORT:-${NIGHTSTUD_PORT:-}}
if [[ ! $nightstud_bind_port =~ ^[0-9]{1,5}$ ]] || (( 10#${nightstud_bind_port:-0} < 1 || 10#${nightstud_bind_port:-0} > 65535 )); then
  echo '平台 SERVER_PORT 或备用 NIGHTSTUD_PORT 需要有效的 TCP 端口。' >&2
  exit 1
fi
access_key=${NIGHTSTUD_ACCESS_KEY:-}
if [[ ${NIGHTSTUD_ALLOW_LEGACY_LOGIN:-0} == 1 && ( ${#access_key} -lt 16 || $access_key == replace-* ) ]]; then
  echo '请在 config.env 设置至少 16 个字符的 NIGHTSTUD_ACCESS_KEY。' >&2
  exit 1
fi
scheme=${NIGHTSTUD_SCHEME:-http}
if [[ $scheme != http && $scheme != https ]]; then echo 'NIGHTSTUD_SCHEME 请选择 http 或 https。' >&2; exit 1; fi
export ASPNETCORE_ENVIRONMENT=Production
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export DOTNET_EnableDiagnostics=0
mkdir -p .tmp
export TMPDIR="$PWD/.tmp"
chmod u+x app/NightStud.Server
cd app
echo "可能是赌的联机服务 / 账号登录 / TCP $nightstud_bind_port"
exec ./NightStud.Server
