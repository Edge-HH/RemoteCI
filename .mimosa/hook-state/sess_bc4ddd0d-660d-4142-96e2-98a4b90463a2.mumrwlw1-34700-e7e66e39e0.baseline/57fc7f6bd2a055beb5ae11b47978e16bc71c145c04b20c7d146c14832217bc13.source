#!/usr/bin/env bash

# 将 GitHub tag 解析为三端统一使用的版本号。
# 稳定版遵循 ClassIsland 的四段纯数字格式；Beta 保留 v 前缀标签但输出时去掉前缀。
set -euo pipefail

tag="${1:-${GITHUB_REF_NAME:-}}"
if [[ -z "$tag" ]]; then
  echo "缺少发布标签" >&2
  exit 2
fi

if [[ "$tag" =~ ^3\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  version="$tag"
  channel="stable"
  prerelease="false"
elif [[ "$tag" =~ ^v3\.[0-9]+\.[0-9]+-beta\.([1-9][0-9]*)$ ]]; then
  beta_number="${BASH_REMATCH[1]}"
  if (( beta_number > 998 )); then
    echo "Beta 序号必须处于 1..998：$tag" >&2
    exit 1
  fi
  version="${tag#v}"
  channel="beta"
  prerelease="true"
else
  echo "不支持的发布标签：$tag（稳定版应为 3.x.x.x，Beta 应为 v3.x.x-beta.y）" >&2
  exit 1
fi

# 直接执行时输出经过严格校验的 shell 赋值；Release workflow 通过 source 读取同名变量。
printf 'version=%q\n' "$version"
printf 'channel=%q\n' "$channel"
printf 'prerelease=%q\n' "$prerelease"
