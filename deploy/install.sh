#!/usr/bin/env bash
set -euo pipefail
install_dir="$PWD/ProbablyStolenServer"
install_port=17266
guest_test=0
start_after=1
release_base='https://github.com/sx050907-png/probably-stolen-server/releases/latest/download'
while (($#)); do
  case "$1" in
    --dir) install_dir="$2"; shift 2;;
    --port) install_port="$2"; shift 2;;
    --guest-test) guest_test=1; shift;;
    --no-start) start_after=0; shift;;
    --release-base) release_base="$2"; shift 2;;
    *) echo "Unknown option: $1" >&2; exit 1;;
  esac
done
[[ $(uname -s) == Linux && $(uname -m) == x86_64 ]] || { echo 'Requires Linux x86_64/glibc.' >&2; exit 1; }
[[ $install_port =~ ^[0-9]{1,5}$ ]] && ((10#$install_port>=1 && 10#$install_port<=65535)) || { echo 'Invalid port.' >&2; exit 1; }
for command in curl tar sha256sum; do command -v "$command" >/dev/null || { echo "Missing command: $command" >&2; exit 1; }; done
mkdir -p -- "$install_dir"
install_dir=$(cd -- "$install_dir" && pwd -P)
[[ $install_dir != / ]] || { echo 'Choose a dedicated folder.' >&2; exit 1; }
if command -v pgrep >/dev/null && pgrep -f -- "^$install_dir/app/NightStud.Server([[:space:]]|$)" >/dev/null; then echo 'Stop this server before updating.' >&2; exit 1; fi
stage=$(mktemp -d "$install_dir/.install-XXXXXXXX")
cleanup(){ if [[ $stage == "$install_dir"/.install-* && -d $stage ]]; then rm -rf -- "$stage"; fi; }
trap cleanup EXIT
asset=nightstud-server-linux-x64.tar.gz
curl --fail --location --retry 2 --connect-timeout 15 --max-time 60 "$release_base/SHA256SUMS" --output "$stage/SHA256SUMS"
curl --fail --location --retry 2 --connect-timeout 15 --max-time 600 "$release_base/$asset" --output "$stage/$asset"
expected=$(awk -v name="$asset" '$2==name {print $1}' "$stage/SHA256SUMS")
[[ $expected =~ ^[a-fA-F0-9]{64}$ ]] || { echo 'Missing release checksum.' >&2; exit 1; }
printf '%s  %s\n' "$expected" "$stage/$asset" | sha256sum --check --status || { echo 'Download checksum mismatch; existing files preserved.' >&2; exit 1; }
tar -tzf "$stage/$asset" > "$stage/members"
if grep -Eq '(^/|(^|/)\.\.(/|$))' "$stage/members"; then echo 'Invalid archive path.' >&2; exit 1; fi
mkdir "$stage/unpacked"
tar -xzf "$stage/$asset" -C "$stage/unpacked" --no-same-owner
[[ -f $stage/unpacked/app/NightStud.Server ]] || { echo 'Server executable missing.' >&2; exit 1; }
if [[ -d $install_dir/app ]]; then
  backup="$install_dir/backups/install-$(date -u +%Y%m%d-%H%M%S)-${RANDOM}"
  mkdir -p -- "$backup"; cp -a -- "$install_dir/app" "$backup/"
fi
for name in app THIRD-PARTY start.cmd start.ps1 start.sh config.env.example announcement.example.json service-status.example.json README.md LICENSE; do
  [[ ! -e $stage/unpacked/$name ]] || cp -a -- "$stage/unpacked/$name" "$install_dir/"
done
if [[ ! -f $install_dir/config.env ]]; then
  umask 077
  printf 'NIGHTSTUD_PORT=%s\nNIGHTSTUD_SCHEME=http\nNIGHTSTUD_ALLOW_GUEST=%s\nNIGHTSTUD_ALLOW_LEGACY_LOGIN=0\nNIGHTSTUD_RECONNECT_SECONDS=90\n' "$install_port" "$guest_test" > "$install_dir/config.env"
fi
chmod u+x "$install_dir/start.sh" "$install_dir/app/NightStud.Server"
if [[ -f $install_dir/update.request ]]; then rm -- "$install_dir/update.request"; fi
echo "Installed: $install_dir"
echo 'Existing config.env, data and certificates were preserved. Public account login requires HTTPS; --guest-test is an explicit private testing option.'
cleanup
trap - EXIT
if ((start_after)); then exec bash "$install_dir/start.sh"; fi
