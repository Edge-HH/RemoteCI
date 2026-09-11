#!/usr/bin/env bash
# 构建 RemoteCI 飞牛 fnOS 应用包（.fpk）。
#
# 用法：
#   ./fnos/build.sh [版本] [fnpack 路径]
#   REMOTECI_FPK_MODE=offline REMOTECI_FPK_ARCH=amd64 \
#     REMOTECI_IMAGE_ARCHIVE=/path/to/remoteci-amd64.tar.gz ./fnos/build.sh 3.2.1.3
#
# 版本缺省从 REMOTECI_VERSION 或最近的 git tag 读取，均无时回退到
# server/RemoteCI.Server/RemoteCI.Server.csproj 的 <Version>（本地默认版本唯一来源）。
# fnpack 路径缺省为 .tools/fnpack；可通过 FNPACK 环境变量或第二个参数指定。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FNOS_DIR="$ROOT/fnos"

LOCAL_FALLBACK="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' \
  "$ROOT/server/RemoteCI.Server/RemoteCI.Server.csproj" | head -n 1)"
VERSION="${1:-${REMOTECI_VERSION:-$(git -C "$ROOT" describe --tags --abbrev=0 2>/dev/null || echo "${LOCAL_FALLBACK:-0.0.0}")}}"
VERSION="${VERSION#v}"
FNPACK="${2:-${FNPACK:-$ROOT/.tools/fnpack}}"
FPK_MODE="${REMOTECI_FPK_MODE:-online}"
FPK_ARCH="${REMOTECI_FPK_ARCH:-}"
IMAGE_ARCHIVE="${REMOTECI_IMAGE_ARCHIVE:-}"
IMAGE_TAG="ghcr.io/edge-hh/remoteci:$VERSION"

case "$FPK_MODE" in
  online)
    if [ -n "$FPK_ARCH" ] || [ -n "$IMAGE_ARCHIVE" ]; then
      echo "在线 FPK 不接受 REMOTECI_FPK_ARCH 或 REMOTECI_IMAGE_ARCHIVE。" >&2
      exit 1
    fi
    FPK_PLATFORM="all"
    OUTPUT_NAME="RemoteCI-$VERSION.fpk"
    ;;
  offline)
    case "$FPK_ARCH" in
      amd64)
        FPK_PLATFORM="x86"
        OUTPUT_NAME="RemoteCI-$VERSION-fnos-x86_64-offline.fpk"
        ;;
      arm64)
        FPK_PLATFORM="arm"
        OUTPUT_NAME="RemoteCI-$VERSION-fnos-arm64-offline.fpk"
        ;;
      *)
        echo "离线 FPK 要求 REMOTECI_FPK_ARCH=amd64 或 arm64。" >&2
        exit 1
        ;;
    esac
    if [ -z "$IMAGE_ARCHIVE" ] || [ ! -f "$IMAGE_ARCHIVE" ]; then
      echo "离线 FPK 要求 REMOTECI_IMAGE_ARCHIVE 指向 docker save 生成的 gzip 归档。" >&2
      exit 1
    fi
    IMAGE_ARCHIVE="$(cd "$(dirname "$IMAGE_ARCHIVE")" && pwd)/$(basename "$IMAGE_ARCHIVE")"
    gzip -t "$IMAGE_ARCHIVE" || {
      echo "离线镜像归档不是有效的 gzip 文件: $IMAGE_ARCHIVE" >&2
      exit 1
    }
    ;;
  *)
    echo "REMOTECI_FPK_MODE 仅支持 online 或 offline。" >&2
    exit 1
    ;;
esac

if [ ! -x "$FNPACK" ]; then
  echo "fnpack 不存在或不可执行: $FNPACK" >&2
  echo "请先下载 https://developer.fnnas.com/docs/cli/fnpack/ 中的 linux-amd64 版本到 .tools/fnpack 并 chmod +x。" >&2
  exit 1
fi

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

cp -R "$FNOS_DIR/." "$STAGE/"
sed -i "s/__REMOTECI_VERSION__/$VERSION/g" \
  "$STAGE/manifest" \
  "$STAGE/app/docker/docker-compose.yaml"
sed -i "s/^platform=all$/platform=$FPK_PLATFORM/" "$STAGE/manifest"

if [ "$FPK_MODE" = "offline" ]; then
  # docker save 归档内的 config 文件名就是镜像 ID；同时校验归档标签和架构，
  # 防止把错误平台或错误版本的镜像静默塞进可安装包。
  IMAGE_ID="$(python3 - "$IMAGE_ARCHIVE" "$IMAGE_TAG" "$FPK_ARCH" <<'PY'
import json
import pathlib
import re
import sys
import tarfile

archive_path, expected_tag, expected_arch = sys.argv[1:]
with tarfile.open(archive_path, "r:*") as archive:
    manifest_file = archive.extractfile("manifest.json")
    if manifest_file is None:
        raise SystemExit("镜像归档缺少 manifest.json；必须使用 docker save 生成归档")
    manifest = json.load(manifest_file)
    matches = [entry for entry in manifest if expected_tag in (entry.get("RepoTags") or [])]
    if len(matches) != 1:
        raise SystemExit(f"镜像归档必须且只能包含标签 {expected_tag}")
    config_path = matches[0].get("Config", "")
    config_file = archive.extractfile(config_path)
    if config_file is None:
        raise SystemExit(f"镜像归档缺少配置文件 {config_path}")
    config = json.load(config_file)

actual_arch = config.get("architecture")
if actual_arch != expected_arch:
    raise SystemExit(f"镜像架构不匹配：期望 {expected_arch}，实际 {actual_arch}")

config_name = pathlib.PurePosixPath(config_path).name
digest = config_name[:-5] if config_name.endswith(".json") else config_name
if not re.fullmatch(r"[0-9a-f]{64}", digest):
    raise SystemExit(f"无法从配置文件名解析镜像 ID：{config_path}")
print(f"sha256:{digest}")
PY
)"
  IMAGE_ARCHIVE_SHA256="$(sha256sum "$IMAGE_ARCHIVE" | awk '{print $1}')"
  cp "$IMAGE_ARCHIVE" "$STAGE/app/docker/remoteci-image.tar.gz"
  cat > "$STAGE/app/docker/remoteci-image.env" <<EOF
IMAGE_TAG=$IMAGE_TAG
IMAGE_ARCH=$FPK_ARCH
IMAGE_ID=$IMAGE_ID
IMAGE_ARCHIVE_SHA256=$IMAGE_ARCHIVE_SHA256
EOF
  cp "$STAGE/app/docker/remoteci-image.env" "$STAGE/cmd/offline-image.env"
  sed -i "/^    image:/a\\    pull_policy: never" \
    "$STAGE/app/docker/docker-compose.yaml"
fi

# 在交给 fnpack 前验证包清单与镜像标签都来自同一个 release 版本。
grep -Fxq "version=$VERSION" "$STAGE/manifest"
grep -Fxq "platform=$FPK_PLATFORM" "$STAGE/manifest"
grep -Fq "image: $IMAGE_TAG" "$STAGE/app/docker/docker-compose.yaml"
if [ "$FPK_MODE" = "offline" ]; then
  grep -Fq "pull_policy: never" "$STAGE/app/docker/docker-compose.yaml"
else
  ! grep -Fq "pull_policy:" "$STAGE/app/docker/docker-compose.yaml"
fi

mkdir -p "$ROOT/artifacts/release"
cd "$STAGE"
"$FNPACK" build

# fnpack 把产物写到工作目录；find 兜底定位，避免不同版本输出位置差异。
FPK="$(find "$STAGE" -maxdepth 1 -name '*.fpk' | head -n 1)"
if [ -z "$FPK" ]; then
  echo "fnpack 构建成功但未找到 fpk 产物，请检查打包目录。" >&2
  exit 1
fi
mv "$FPK" "$ROOT/artifacts/release/$OUTPUT_NAME"

echo "已生成: artifacts/release/$OUTPUT_NAME"
