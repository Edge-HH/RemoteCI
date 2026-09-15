# RemoteCI fnOS 应用包

本目录是飞牛 fnOS 应用（`.fpk`）工程，把 RemoteCI 服务端以 Docker 应用形式打包，可直接在飞牛 fnOS 应用中心安装。

## 结构

| 路径 | 说明 |
| --- | --- |
| `manifest` | 应用包描述：应用名、版本、平台、桌面入口、服务端口 |
| `config/privilege` | 运行用户声明（Docker 应用由容器承载进程，保持 package 最小权限） |
| `config/resource` | Docker 项目资源声明 |
| `app/docker/docker-compose.yaml` | 容器编排：镜像、端口、数据卷、环境变量 |
| `app/ui/config` | 桌面入口（打开 WebUI） |
| `cmd/` | 生命周期脚本；离线包在安装/升级前校验并导入镜像，start/stop 由应用中心接管 |
| `wizard/install` | 安装向导：Web 端口、管理员密码、插件配对码 |
| `ICON.PNG` / `ICON_256.PNG` | 包图标（64×64 / 256×256） |

## 构建

1. 下载 [fnpack](https://developer.fnnas.com/docs/cli/fnpack/)（linux-amd64）到 `.tools/fnpack` 并授权：

   ```bash
   mkdir -p .tools
   curl -fsSL -o .tools/fnpack https://static2.fnnas.com/fnpack/fnpack-1.2.3-linux-amd64
   chmod +x .tools/fnpack
   ```

2. 构建在线包（版本号从最近的 git tag 读取，也可以用参数覆盖）：

   ```bash
   bash fnos/build.sh          # 使用最近 git tag
   bash fnos/build.sh 3.2.1.4  # 指定稳定版本
   ```

3. 构建单架构离线包时，先用 `docker save` 导出带有正式版本标签的镜像，再指定模式、架构和归档路径：

   ```bash
   docker pull --platform linux/amd64 ghcr.io/edge-hh/remoteci:3.2.1.4
   docker save ghcr.io/edge-hh/remoteci:3.2.1.4 | gzip -1 > remoteci-amd64.tar.gz
   REMOTECI_VERSION=3.2.1.4 \
     REMOTECI_FPK_MODE=offline \
     REMOTECI_FPK_ARCH=amd64 \
     REMOTECI_IMAGE_ARCHIVE=remoteci-amd64.tar.gz \
     bash fnos/build.sh
   ```

`REMOTECI_FPK_ARCH` 支持 `amd64` 和 `arm64`；脚本会校验 docker-save 归档中的标签、架构和 Image ID，拒绝错误镜像。
飞牛应用中心把 Intel/AMD 设备记为 `x86`、把 ARM64 设备记为 `arm`，这与 Docker 的 `amd64`/`arm64` 是同一 64 位架构，不是 32 位。
`x86_64-offline` 包接受 `x86`/`x86_64`/`amd64`，`arm64-offline` 包接受 `arm`/`arm64`/`aarch64`；当前不提供 i386 32 位离线包。
离线镜像在 `install_init`/`upgrade_init` 阶段导入；该阶段应用数据临时目录可能尚未创建，因此加载器只使用应用中心已准备好的安装解压目录或系统可写临时目录。
生命周期脚本按 `root` 运行，仅用于访问 fnOS 管理的 Docker daemon、导入随包镜像和检查容器状态；RemoteCI 服务本身仍运行在应用中心管理的容器内。

每个 GitHub Release 发布三种产物：

| 文件 | 架构 | 预计体积 | 安装行为 |
| --- | --- | --- | --- |
| `RemoteCI-<版本>.fpk` | x86 + ARM | 约 45 KB | 小型在线包，安装时从 GHCR 拉取对应架构镜像 |
| `RemoteCI-<版本>-fnos-x86_64-offline.fpk` | x86_64 | 约 116 MB | 内置 amd64 镜像，不访问 GHCR |
| `RemoteCI-<版本>-fnos-arm64-offline.fpk` | ARM64 | 约 112 MB | 内置 arm64 镜像，不访问 GHCR |

离线包体积取决于对应版本镜像的压缩结果，表中数值用于下载选择，不是固定上限。

GitHub Actions 的 `release.yml` 会在推送四段稳定标签（如 `3.2.1.4`）或保留的 Beta 标签（如 `v3.2.2-beta.1`）时构建并验证多架构镜像，生成以上三个 FPK，再附加到同一个 GitHub Release。稳定标签同时用于 ClassIsland 插件市场；Beta 仅供测试，不进入市场。当前不向飞牛应用商店提交安装包。

## 安装与更新

1. 从 GitHub Releases 下载适合设备和网络环境的 FPK；网络能稳定访问 GHCR 时可用在线包，否则选择对应架构的离线包。
2. 在飞牛 fnOS 应用中心选择"手动安装"，按向导填写端口、管理员密码和插件配对码。
3. 安装后从桌面入口打开 RemoteCI WebUI。
4. 升级时从 GitHub Releases 下载新版本 FPK，并在 fnOS 应用中心手动安装；在线包和对应架构离线包可以相互覆盖升级。

离线包会在应用文件写入前校验并执行 `docker load`，Compose 使用 `pull_policy: never`，不会回退到联网拉取。导入成功后会删除安装目录中重复保存的镜像归档；Docker 镜像层和 SQLite 数据不受影响。

数据（SQLite）保存在 `/var/apps/remoteci/var/data`，升级或切换 FPK 类型不会删除它。ARM64 离线包已由 CI/QEMU 验证镜像运行，仍需 ARM fnOS 真机完成最终安装验收。
