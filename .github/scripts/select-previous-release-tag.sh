#!/usr/bin/env bash

# 从按发布时间倒序排列的非草稿 Release 标签中排除当前版本，返回真正的上一版本。
# Release 页面可能在构建开始前就创建当前标签对应的空 Release，因此不能直接取第一项。
set -euo pipefail

current_tag="${1:-${GITHUB_REF_NAME:-}}"
if [[ -z "$current_tag" ]]; then
  echo "缺少当前发布标签" >&2
  exit 2
fi

while IFS= read -r tag; do
  tag="${tag%$'\r'}"
  if [[ -n "$tag" && "$tag" != "$current_tag" ]]; then
    printf '%s\n' "$tag"
    exit 0
  fi
done

echo "找不到当前标签之前的已发布版本" >&2
exit 1
