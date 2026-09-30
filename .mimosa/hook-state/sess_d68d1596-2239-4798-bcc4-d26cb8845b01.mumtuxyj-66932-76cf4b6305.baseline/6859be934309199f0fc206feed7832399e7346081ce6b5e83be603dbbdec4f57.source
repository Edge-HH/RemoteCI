#!/usr/bin/env bash
# 验证离线包架构别名：fnOS 把 Intel 设备标成 x86，ARM64 标成 arm。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# shellcheck source=fnos/cmd/arch_compat
. "$ROOT/fnos/cmd/arch_compat"

assert_ok() {
  local image_arch="$1"
  local system_arch="$2"
  if ! remoteci_image_arch_compatible "$image_arch" "$system_arch"; then
    echo "expected compatible: $image_arch vs $system_arch" >&2
    exit 1
  fi
}

assert_fail() {
  local image_arch="$1"
  local system_arch="$2"
  if remoteci_image_arch_compatible "$image_arch" "$system_arch"; then
    echo "expected incompatible: $image_arch vs $system_arch" >&2
    exit 1
  fi
}

assert_ok amd64 x86_64
assert_ok amd64 amd64
assert_ok arm64 aarch64
assert_ok arm64 arm64
assert_fail amd64 aarch64
assert_fail arm64 x86_64
assert_fail amd64 i386
assert_fail arm64 x86

# fnOS 应用中心对 Intel 64 位设备传入 TRIM_SYS_ARCH=x86，不是 32 位。
assert_ok amd64 x86
assert_ok arm64 arm

echo "arch alias checks passed"
