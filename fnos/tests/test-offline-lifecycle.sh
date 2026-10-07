#!/usr/bin/env bash
# 在 CI 的真实 Docker daemon 上验证离线导入、重复导入和关键失败路径。
set -euo pipefail

if [ "$#" -ne 4 ]; then
  echo "usage: test-offline-lifecycle.sh <online.fpk> <x86.fpk> <arm.fpk> <version>" >&2
  exit 2
fi

ONLINE_FPK="$(realpath "$1")"
X86_FPK="$(realpath "$2")"
ARM_FPK="$(realpath "$3")"
VERSION="$4"
IMAGE_TAG="ghcr.io/edge-hh/remoteci:$VERSION"
WORK="$(mktemp -d)"
trap 'docker image rm "$IMAGE_TAG" >/dev/null 2>&1 || true; rm -rf "$WORK"' EXIT

extract_fpk() {
  local package="$1"
  local target="$2"
  mkdir -p "$target"
  tar -xzf "$package" -C "$target"
}

run_loader() {
  local package_root="$1"
  local system_arch="$2"
  local log_file="$3"
  TRIM_TEMP_TPKFILE="$package_root" \
  TRIM_PKGINST_TEMP_DIR="$package_root" \
  TRIM_PKGTMP="/proc/remoteci-missing-app-temp" \
  TMPDIR="/proc/remoteci-missing-system-temp" \
  TRIM_TEMP_LOGFILE="$log_file" \
  TRIM_SYS_ARCH="$system_arch" \
  TRIM_APPVER="$VERSION" \
    bash "$package_root/cmd/load_offline_image"
}

extract_fpk "$ONLINE_FPK" "$WORK/online"
run_loader "$WORK/online" x86_64 "$WORK/online.log"

extract_fpk "$X86_FPK" "$WORK/x86"
docker image rm "$IMAGE_TAG" >/dev/null 2>&1 || true
run_loader "$WORK/x86" x86_64 "$WORK/x86.log"
test "$(docker image inspect --format '{{.Architecture}}' "$IMAGE_TAG")" = "amd64"
run_loader "$WORK/x86" x86 "$WORK/x86-fnos-alias.log" | grep -Fq "跳过重复导入"
run_loader "$WORK/x86" x86_64 "$WORK/x86-repeat.log" | grep -Fq "跳过重复导入"

mkdir -p "$WORK/appdest/docker"
tar -xOzf "$WORK/x86/app.tgz" docker/remoteci-image.tar.gz \
  > "$WORK/appdest/docker/remoteci-image.tar.gz"
TRIM_APPDEST="$WORK/appdest" \
  TRIM_TEMP_LOGFILE="$WORK/cleanup.log" \
  bash "$WORK/x86/cmd/cleanup_offline_image"
test ! -e "$WORK/appdest/docker/remoteci-image.tar.gz"

if run_loader "$WORK/x86" aarch64 "$WORK/wrong-arch.log"; then
  echo "x86 离线包不应允许安装到 ARM 设备" >&2
  exit 1
fi
grep -Fq "架构 amd64 与设备架构 aarch64 不匹配" "$WORK/wrong-arch.log"

if run_loader "$WORK/x86" i386 "$WORK/wrong-i386.log"; then
  echo "amd64 离线包不应允许安装到 32 位 i386 设备" >&2
  exit 1
fi
grep -Fq "架构 amd64 与设备架构 i386 不匹配" "$WORK/wrong-i386.log"

docker image rm "$IMAGE_TAG" >/dev/null
if [ "${REMOTECI_TEST_FAILURE_CASES:-1}" = "1" ]; then
  extract_fpk "$X86_FPK" "$WORK/hash-mismatch"
  mkdir "$WORK/hash-mismatch-app"
  tar -xzf "$WORK/hash-mismatch/app.tgz" -C "$WORK/hash-mismatch-app"
  printf 'hash-mismatch' >> "$WORK/hash-mismatch-app/docker/remoteci-image.tar.gz"
  tar -czf "$WORK/hash-mismatch/app.tgz" -C "$WORK/hash-mismatch-app" docker ui
  if run_loader "$WORK/hash-mismatch" x86_64 "$WORK/hash-mismatch.log"; then
    echo "哈希不匹配的离线镜像不应通过校验" >&2
    exit 1
  fi
  grep -Fq "SHA-256 校验失败" "$WORK/hash-mismatch.log"

  extract_fpk "$X86_FPK" "$WORK/corrupt"
  mkdir "$WORK/corrupt-app"
  tar -xzf "$WORK/corrupt/app.tgz" -C "$WORK/corrupt-app"
  printf 'not-a-docker-image' > "$WORK/corrupt-app/docker/remoteci-image.tar.gz"
  corrupt_sha="$(sha256sum "$WORK/corrupt-app/docker/remoteci-image.tar.gz" | awk '{print $1}')"
  sed -i "s/^IMAGE_ARCHIVE_SHA256=.*/IMAGE_ARCHIVE_SHA256=$corrupt_sha/" \
    "$WORK/corrupt-app/docker/remoteci-image.env" \
    "$WORK/corrupt/cmd/offline-image.env"
  tar -czf "$WORK/corrupt/app.tgz" -C "$WORK/corrupt-app" docker ui
  if run_loader "$WORK/corrupt" x86_64 "$WORK/corrupt.log"; then
    echo "损坏的 Docker 归档不应被导入" >&2
    exit 1
  fi
  grep -Fq "docker load 执行失败" "$WORK/corrupt.log"

  extract_fpk "$X86_FPK" "$WORK/missing"
  mkdir "$WORK/missing-app"
  tar -xzf "$WORK/missing/app.tgz" -C "$WORK/missing-app"
  rm "$WORK/missing-app/docker/remoteci-image.tar.gz"
  tar -czf "$WORK/missing/app.tgz" -C "$WORK/missing-app" docker ui
  if run_loader "$WORK/missing" x86_64 "$WORK/missing.log"; then
    echo "缺少离线镜像的 FPK 不应继续安装" >&2
    exit 1
  fi
  grep -Fq "缺少离线镜像归档" "$WORK/missing.log"
fi

extract_fpk "$ARM_FPK" "$WORK/arm"
run_loader "$WORK/arm" aarch64 "$WORK/arm.log"
test "$(docker image inspect --format '{{.Architecture}}' "$IMAGE_TAG")" = "arm64"
run_loader "$WORK/arm" arm "$WORK/arm-fnos-alias.log" | grep -Fq "跳过重复导入"
