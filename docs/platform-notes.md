# 平台适配笔记（Wear OS / 环境）

> 本文记录 Wear OS 开发环境的实际配置（本机已就绪）、模拟器使用方式，以及开发过程中沉淀的技术决策，为后续扩展（Tiles 小组件、小米 HyperOS 适配、watchOS 端）保留上下文。

## 1. 本机开发环境（2026-08 已配好）

| 组件 | 位置/版本 | 说明 |
| --- | --- | --- |
| Android Studio | `E:\Android Studio`（含 JBR JDK 25） | IDE |
| Android SDK | `C:\Users\YangTianming\AppData\Local\Android\Sdk` | 命令行工具已装 cmdline-tools |
| JDK（Gradle 用） | `E:\Android Studio\jbr` | JetBrains Runtime 25，构建时设 `JAVA_HOME` |
| Wear OS 模拟器 | AVD `PixelWatch2_API35` | Wear OS 5.1（API 35），450×450 圆屏，2GB RAM |
| 模拟器硬件加速 | WHPX（Windows Hypervisor Platform） | `emulator-check accel` 确认可用 |

`wearos/local.properties` 已写入 `sdk.dir`（该文件被 .gitignore 忽略，换机器需重建）。

## 2. 模拟器与设备

### 2.1 创建模拟器（如需重建）

```powershell
$env:JAVA_HOME = 'E:\Android Studio\jbr'
$env:ANDROID_HOME = 'C:\Users\YangTianming\AppData\Local\Android\Sdk'

# 首次需先安装系统镜像（已装，勿重复下载）
# & "$env:ANDROID_HOME\cmdline-tools\latest\bin\sdkmanager.bat" "system-images;android-35-ext15;android-wear;x86_64"

# 创建 AVD（设备定义 wearos_small_round，圆屏）
& "$env:ANDROID_HOME\cmdline-tools\latest\bin\avdmanager.bat" create avd `
  -n PixelWatch2_API35 `
  -k "system-images;android-35-ext15;android-wear;x86_64" `
  -d wearos_small_round
```

创建后调整 `%USERPROFILE%\.android\avd\PixelWatch2_API35.avd\config.ini`：
`hw.lcd.width=450`、`hw.lcd.height=450`、`hw.lcd.circular=true`、`hw.ramSize=2048`。

### 2.2 启动模拟器

```powershell
& "$env:ANDROID_HOME\emulator\emulator.exe" -avd PixelWatch2_API35 -no-snapshot -no-boot-anim -gpu auto
```

也可运行仓库脚本 `wearos/dev.ps1 emulator`。

### 2.3 构建 / 安装 / 运行

```powershell
cd wearos
$env:JAVA_HOME = 'E:\Android Studio\jbr'
.\gradlew.bat assembleDebug                       # 构建 APK
& "$env:ANDROID_HOME\platform-tools\adb.exe" install -r app\build\outputs\apk\debug\app-debug.apk
& "$env:ANDROID_HOME\platform-tools\adb.exe" shell am start -n com.remoteci.watch/.MainActivity
```

一键脚本：`wearos/dev.ps1 run`（构建 + 安装 + 启动）。

如果源码位于云盘、NAS 按需同步盘或其他会把文件转换为重解析点的目录，Gradle 可能报告 `Cannot snapshot ...: not a regular file`。在不入库的 `wearos/local.properties` 中配置普通本地磁盘目录即可把所有 Gradle 构建产物移出同步盘：

```properties
remoteci.buildDir=C:/Users/<用户名>/AppData/Local/RemoteCI/wearos-build
```

也可以设置环境变量 `REMOTECI_WEAROS_BUILD_DIR`，或在命令行传入 `-Premoteci.buildDir=<目录>`。配置后 Debug APK 位于 `<目录>/app/outputs/apk/debug/app-debug.apk`，`wearos/dev.ps1 run` 会自动使用该位置；CI 未配置时仍保留默认的 `app/build` 路径。

### 2.4 模拟器网络说明

- 模拟器内访问宿主机使用 `http://10.0.2.2:8080`（已在手表端默认设置）。
- 局域网直连测试：模拟器与电脑同网，手表端填电脑局域网 IP 即可。
- 真机测试：10.0.2.2 无效，需填电脑/服务器局域网或公网地址。

## 3. 关键技术决策（必读）

### 3.1 AGP 9 新 DSL 与 Kotlin 插件（`android.newDsl=false`）

- AGP 9.x 默认启用 `android.newDsl=true`，经典 KGP（`org.jetbrains.kotlin.android`）与之不兼容，构建会直接失败。
- 当前在 `wearos/gradle.properties` 设 `android.newDsl=false` 回退旧 DSL，保留经典 KGP + Kotlin 2.4.10 + Compose/Serialization 插件（版本自洽），这是 AGP 9 官方过渡方案。
- **技术债**：AGP 10 将移除 `builtInKotlin` / `newDsl` 开关。届时需按官方指南迁移到 AGP 内置 Kotlin：移除 `kotlin.android` 插件，Compose/Serialization 插件版本需匹配 AGP 捆绑的 Kotlin 版本（见 https://kotl.in/gradle/agp-built-in-kotlin）。

### 3.2 Wear Compose 1.6 移除了 TextField

- `androidx.wear.compose.material.TextField` 在 1.6.x 已不存在（AAR 中无该类）。
- 文本输入统一改用标准 `androidx.compose.material3.TextField`（版本由 compose-bom 管理），API 签名（value/onValueChange/placeholder/label）与旧 Wear TextField 一致，改动小。
- 引入依赖：`androidx.compose.material3:material3`（见 `libs.versions.toml` 的 `compose-material3`）。

### 3.3 ScalingLazyColumn 的包路径

- Wear Compose 1.6 中 `ScalingLazyColumn` / `rememberScalingLazyListState` 位于 **`androidx.wear.compose.foundation.lazy`** 子包（不是 `androidx.wear.compose.foundation`）。
- 引用前先 `jar tf` 检查 AAR 实际类路径，避免按旧版记忆写 import。

### 3.4 编译目标与 JDK

- `compileSdk = 37`（本机已装 platform android-37.0）；`minSdk = 30`（Wear OS 3+）；`targetSdk = 37`。
- Java/Kotlin 目标 17；构建用 Android Studio 自带 JBR（JDK 25）运行 Gradle 9.7。

### 3.5 手表端 M3 主题配色（设置 → 外观）

- “设置 → 外观”可切换 6 套 Material Design 3 配色：淡紫（默认）、经典紫、蓝、绿、橙、粉，色值取自 M3 官方 tonal palette。
- `WatchPalette` 数据类集中管理整套配色；`LocalWatchPalette`（CompositionLocal）由 `AppTheme` 提供，屏幕组件统一从主题取色，禁止再硬编码容器色。
- 主题 id 通过 `SettingsStore.themeId`（SharedPreferences）持久化；`AppTheme` 在 `RemoteCiApp` 内根据设置提供，切换即时生效。

### 3.6 圆形与矩形屏幕

- 根画布读取 Android `Configuration.isScreenRound`：圆屏使用短边构成的圆形安全画布，矩形屏使用完整可用宽高。
- 页面内的尺寸仍以屏幕短边作为缩放基准，避免矩形屏变高后按钮和文字被不成比例放大；矩形屏根布局不得再套用 `CircleShape` 裁剪。

### 3.7 云端连接开发者开关

- 手表普通“连接”页只配置云端地址和局域网参数，不显示关闭云端的开关；关闭入口集中在“设置 → 开发者”。
- 插件普通“RemoteCI 设置”同样不显示关闭云端中转的开关；“显示开发者设置菜单”默认关闭，开启并重启 ClassIsland 后才会注册“RemoteCI 开发者设置”页。该页除高级连接开关外，还可按完整接收流程测试语音通知、播放和浮窗。
- `ConnectionManager` 把密码登录视为必要的云端认证引导，不受开发者的后续云端回退偏好限制；因此旧配置已关闭云端时，退出账号后仍可重新登录。

## 4. 后续扩展点（v0.2+）

- **Tiles 小组件**：`androidx.wear.tiles` 官方库，数据层（ConnectionManager/StateFlow）已与 UI 分离，Tiles 直接消费同一状态源。
- **小米 HyperOS 适配**：HyperOS 手表端（小米手表）主要兼容 Wear OS 应用；关注中国版 ROM 的权限/通知差异，小组件可能走小米私有协议，v0.2 再调研。
- **watchOS 端**：协议为平台无关 JSON（见 docs/protocol.md），Swift/SwiftUI 端实现同一协议即可；记录协议版本与错误码，避免平台间漂移。
- **通知与振动**：当前用系统通知（NotificationHelper），后续可加 TTS、自定义表盘交互。

## 5. 服务端与插件（ClassIsland）本地开发环境

### 5.1 工具链（本机已就绪）

- .NET SDK **10.0.x**（`C:\Program Files\dotnet`）。服务端目标 net10.0；插件与共享协议库目标 **net8.0**（ClassIsland 2.x 最低运行时），三端共用协议库。
- PowerShell 7（pwsh 7.6.4）：ClassIsland 打包工具依赖它生成 cipx 校验文件，**必须安装**。
  - 安装命令：`winget install --id Microsoft.PowerShell --exact`。
  - 若缺失，`-p:CreateCipx=true` 打包会报 `'pwsh' 不是内部或外部命令`（zip 已生成但构建失败）。

### 5.2 常用命令

```powershell
# 构建三端（shared / server / plugin + 测试项目）
dotnet build RemoteCI.slnx

# 服务端单元测试（覆盖配对认证 / 登录锁定 / REST / WebSocket 中转 / 自更新）
dotnet test server/tests/RemoteCI.Server.Tests

# 启动服务端（默认 0.0.0.0:8080；首次启动生成的一次性管理员密码与插件配对码写入控制台日志）
dotnet run --project server/RemoteCI.Server --no-build

# 插件打包 cipx（输出 plugin/RemoteCI.Plugin/cipx/RemoteCI.Plugin.cipx + checksums.md）
dotnet build plugin/RemoteCI.Plugin -p:CreateCipx=true
```

### 5.3 服务端 API 冒烟测试（PowerShell）

```powershell
# 插件配对码在 WebUI“生成插件配对码”或首次启动的控制台日志中获取，只能被消费一次。
$body = @{ pairCode = '从 WebUI 获取的配对码'; role = 'plugin' } | ConvertTo-Json
$r = Invoke-RestMethod -Uri 'http://127.0.0.1:8080/api/plugin/pair' -Method Post -ContentType 'application/json' -Body $body
# $r.token 即插件长期凭据；手表登录走 /api/auth/login，后续请求用 Bearer accessToken
```

注意：在 PowerShell 中用 `curl`（curl.exe）传 JSON 易被引号转义搞坏，冒烟测试建议用 `Invoke-RestMethod`。

### 5.4 插件在 ClassIsland 中加载调试

- 开发模式：设环境变量 `ClassIsland_DebugBinaryFile` 指向 `plugin/RemoteCI.Plugin/bin/Debug/net8.0/RemoteCI.Plugin.dll`，ClassIsland 启动时自动加载。
- 分发模式：把 `cipx/RemoteCI.Plugin.cipx` 放入 ClassIsland 插件目录，或在应用内"插件市场"离线安装。
- 插件端依赖：`ClassIsland.PluginSdk`（LGPLv3，仅编译期，ExcludeAssets runtime/native）+ `Fleck`（MIT，局域网 WebSocket 服务器）。
## Wear OS 构建环境

手表端固定使用 JDK 17。Windows 上应设置用户级 `JAVA_HOME`，并确保 `wearos/local.properties` 指向已安装的 Android SDK：

```powershell
$env:JAVA_HOME="C:\path\to\jdk-17"
cd wearos
.\gradlew.bat testDebugUnitTest assembleDebug
```

本机 2026-08-11 验证使用 Eclipse Temurin 17.0.20 和现有 Android SDK，Debug APK 构建成功。
