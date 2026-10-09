#!/usr/bin/env bash
# 验证容器入口：数据卷中的自更新版本只在同一镜像上使用，首次启动失败会回退到镜像版本。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ENTRYPOINT="$ROOT/server/RemoteCI.Server/docker-entrypoint.sh"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# 用桩 dotnet 记录实际启动的程序集、工作目录和启动标记，代替真实服务端。
mkdir -p "$WORK/bin"
cat > "$WORK/bin/dotnet" <<'EOF'
#!/bin/sh
echo "$1|$(pwd)|${REMOTECI_STARTUP_MARKER:-}|${REMOTECI_IMAGE_VERSION:-}" > "$RESULT"
EOF
chmod +x "$WORK/bin/dotnet"

image="$WORK/image"
overlay="$WORK/data/app"
mkdir -p "$image" "$WORK/data"
touch "$image/RemoteCI.Server.dll"
echo "3.3.0.2+20261008" > "$image/IMAGE_VERSION"

run() {
  RESULT="$WORK/result" PATH="$WORK/bin:$PATH" REMOTECI_IMAGE_DIR="$image" REMOTECI_OVERLAY_DIR="$overlay" \
    sh "$ENTRYPOINT"
  cat "$WORK/result"
}

install_overlay() {
  rm -rf "$overlay"
  mkdir -p "$overlay"
  touch "$overlay/RemoteCI.Server.dll" "$overlay/.pending-start"
  echo "$1" > "$overlay/.base-image-version"
}

expect() {
  local actual="$1" expected="$2" message="$3"
  if [[ "$actual" != $expected ]]; then
    echo "FAIL: $message" >&2
    echo "  expected: $expected" >&2
    echo "  actual:   $actual" >&2
    exit 1
  fi
}

# 1. 没有自更新版本：运行镜像内置版本，并告知服务端可用的数据卷目录与镜像版本。
expect "$(run)" "$image/RemoteCI.Server.dll|$image||3.3.0.2+20261008" "fresh container runs image"

# 2. 新装的自更新版本：首次启动带启动标记运行数据卷版本。
install_overlay "3.3.0.2+20261008"
expect "$(run)" "$overlay/RemoteCI.Server.dll|$overlay|$overlay/.started|*" "pending overlay starts with marker"
[[ -f "$overlay/.start-attempted" ]] || { echo "FAIL: start attempt not recorded" >&2; exit 1; }

# 3. 首次启动成功（服务端写入标记）后，清除待验证状态并继续使用数据卷版本。
touch "$overlay/.started"
expect "$(run)" "$overlay/RemoteCI.Server.dll|$overlay||*" "verified overlay keeps running"
[[ ! -e "$overlay/.pending-start" ]] || { echo "FAIL: pending flag not cleared" >&2; exit 1; }

# 4. 首次启动没能完成：回退到镜像版本并删除损坏的自更新版本。
install_overlay "3.3.0.2+20261008"
touch "$overlay/.start-attempted"
expect "$(run 2>/dev/null)" "$image/RemoteCI.Server.dll|$image||*" "failed overlay falls back to image"
[[ ! -e "$overlay" ]] || { echo "FAIL: failed overlay not removed" >&2; exit 1; }

# 5. 镜像已更换（安装了新 FPK）：作废旧镜像上安装的自更新版本。
install_overlay "3.3.0.1+20260901"
expect "$(run)" "$image/RemoteCI.Server.dll|$image||*" "image change discards overlay"
[[ ! -e "$overlay" ]] || { echo "FAIL: stale overlay not removed" >&2; exit 1; }

# 6. 上次替换中断留下的临时目录会被清理。
mkdir -p "$overlay.next" "$overlay.old"
run > /dev/null
[[ ! -e "$overlay.next" && ! -e "$overlay.old" ]] || { echo "FAIL: leftover directories not removed" >&2; exit 1; }

echo "container entrypoint checks passed"
