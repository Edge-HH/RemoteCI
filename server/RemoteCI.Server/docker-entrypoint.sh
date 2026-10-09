#!/bin/sh
# RemoteCI 容器入口。
#
# WebUI 自更新把新版本装到数据卷（默认 /app/data/app）而不是覆盖镜像内的 /app：
# 飞牛 fnOS 应用中心与 docker compose 重建容器时只保留数据卷，就地覆盖的文件会丢失，
# 程序会悄悄退回旧版而数据库已迁移到新结构。
#
# 规则：
# - 自更新版本只在“安装它时的同一个镜像”上使用；镜像一旦更换（安装新 FPK、docker pull），
#   说明管理员选择了镜像中的版本，作废数据卷里的旧自更新版本。
# - 自更新版本第一次启动未能完成时，下次启动回退到镜像内置版本，避免容器反复崩溃。
set -eu

image_dir="${REMOTECI_IMAGE_DIR:-/app}"
overlay="${REMOTECI_OVERLAY_DIR:-/app/data/app}"
image_version="$(cat "$image_dir/IMAGE_VERSION" 2>/dev/null || echo unknown)"
# 服务端据这两个变量判断可以把更新装到数据卷（旧镜像没有本脚本，仍按原方式处理）。
export REMOTECI_OVERLAY_DIR="$overlay" REMOTECI_IMAGE_VERSION="$image_version"

rm -rf "$overlay.next" "$overlay.old"

run_image() {
    rm -rf "$overlay"
    cd "$image_dir"
    exec dotnet "$image_dir/RemoteCI.Server.dll" "$@"
}

if [ ! -f "$overlay/RemoteCI.Server.dll" ] ||
   [ "$(cat "$overlay/.base-image-version" 2>/dev/null || true)" != "$image_version" ]; then
    run_image "$@"
fi

if [ -f "$overlay/.pending-start" ]; then
    if [ -f "$overlay/.started" ]; then
        rm -f "$overlay/.pending-start" "$overlay/.start-attempted" "$overlay/.started"
    elif [ -f "$overlay/.start-attempted" ]; then
        echo "RemoteCI: 自更新版本上次未能启动，已回退到镜像内置版本" >&2
        run_image "$@"
    else
        : > "$overlay/.start-attempted"
        # 服务端启动完成后写入该文件（见 Program.cs 的启动标记），证明新版本可以正常运行。
        export REMOTECI_STARTUP_MARKER="$overlay/.started"
    fi
fi

cd "$overlay"
exec dotnet "$overlay/RemoteCI.Server.dll" "$@"
