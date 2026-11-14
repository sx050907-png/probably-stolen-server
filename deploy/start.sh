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
if [[ ! -f app/NightStud.Server || -f update.request ]]; then
  if ! command -v curl >/dev/null; then echo '缺少 curl，无法从 GitHub 下载。请通过文件管理上传 Linux 完整包，再点击启动。' >&2; exit 1; fi
  mkdir -p .nightstud-bootstrap
  installer="$PWD/.nightstud-bootstrap/install.sh"
  echo '首次安装或收到 update.request，正在从 GitHub 下载安装器…'
  if ! curl --fail --location --retry 2 --connect-timeout 15 --max-time 180 \
    'https://github.com/sx050907-png/probably-stolen-server/releases/latest/download/install.sh' --output "$installer.pending"; then
    echo '下载未完成，现有程序、配置和数据未改动。可稍后再次点击启动，或上传完整包。' >&2; exit 1
  fi
  mv -- "$installer.pending" "$installer"
  # Replace this process before the installer replaces start.sh itself.
  exec bash "$installer" --dir "$PWD" --port "${SERVER_PORT:-${NIGHTSTUD_PORT:-17266}}"
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
if [[ -f backup.request ]]; then
  echo '收到 backup.request，先生成数据库一致性备份…'
  if ./app/NightStud.Server backup; then rm -- backup.request;
  else echo '备份失败，标记已保留。请检查文件和日志后重试。' >&2; exit 1; fi
fi
cd app
echo "可能是赌的联机服务 / 账号登录 / TCP $nightstud_bind_port"
exec ./NightStud.Server
