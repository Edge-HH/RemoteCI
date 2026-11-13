# 平台适配笔记（Wear OS / 环境）

> 本文记录 Wear OS 开发环境的实际配置（本机已就绪）、模拟器使用方式，以及开发过程中沉淀的技术决策，为后续扩展（Tiles 小组件、小米 HyperOS 适配、watchOS 端）保留上下文。

## 1. 本机开发环境（2026-08 已配好）

| 组件 | 位置/版本 | 说明 |
| --- | --- | --- |
| Android Studio | `C:\Program Files\Android\Android Studio`（含 JBR JDK 25，示例路径） | IDE |
| Android SDK | `%LOCALAPPDATA%\Android\Sdk` | 命令行工具已装 cmdline-tools |
| JDK（Gradle 用） | `C:\Program Files\Android\Android Studio\jbr` | JetBrains Runtime 25，构建时设 `JAVA_HOME` |
| Wear OS 模拟器 | AVD `PixelWatch2_API35` | Wear OS 5.1（API 35），450×450 圆屏，2GB RAM |
| 模拟器硬件加速 | WHPX（Windows Hypervisor Platform） | `emulator-check accel` 确认可用 |

`wearos/local.properties` 已写入 `sdk.dir`（该文件被 .gitignore 忽略，换机器需重建）。

## 2. 模拟器与设备

### 2.1 创建模拟器（如需重建）

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"

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
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'
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
- Java/Kotlin 目标 17；运行 Gradle 9.7 需 JDK 17 及以上，手表端未固定守护进程 JVM 版本（CI 与本机验证均用 JDK 17，Android Studio 自带 JBR 亦可）。

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
- **Android 手机桌面小组件**：手机端使用标准 AppWidget/RemoteViews、尺寸回调和夜间资源，直接兼容 HyperOS 桌面；不依赖小米私有 SDK，厂商桌面白名单与系统权限仍由设备侧控制。
- **watchOS 端**：协议为平台无关 JSON（见 docs/protocol.md），Swift/SwiftUI 端实现同一协议即可；记录协议版本与错误码，避免平台间漂移。
- **通知与振动**：当前用系统通知（NotificationHelper），后续可加 TTS、自定义表盘交互。

## 5. 服务端与插件（ClassIsland）本地开发环境

### 5.1 工具链（本机已就绪）

- .NET SDK **10.0.x**（`C:\Program Files\dotnet`）。服务端目标 net10.0；插件与共享协议库目标 **net8.0**（最低支持的 ClassIsland 2.1.0 运行在 .NET 8；2.1.1 起宿主为 .NET 10，仍可加载），三端共用协议库。
- PowerShell 7（pwsh 7.6.4）：ClassIsland 打包工具依赖它生成 cipx 校验文件，**必须安装**。
  - 安装命令：`winget install --id Microsoft.PowerShell --exact`。
  - 若缺失，`-p:CreateCipx=true` 打包会报 `'pwsh' 不是内部或外部命令`（zip 已生成但构建失败）。

### 5.2 常用命令

```powershell
# 构建三端（shared / server / plugin + 测试项目）
dotnet build RemoteCI.slnx

# 服务端单元测试（覆盖配对认证 / 登录锁定 / REST / WebSocket 中转 / 自更新）
dotnet test server/tests/RemoteCI.Server.Tests

# 启动服务端（默认 0.0.0.0:8080；不再随机生成管理员密码，首次打开 WebUI 由初始化向导创建，
# 或用 REMOTECI_ADMIN_PASSWORD 预置；自动生成的一次性插件配对码默认不写日志、30 分钟有效、用后失效，
# 可在 WebUI 重新生成，或用 REMOTECI_PLUGIN_PAIR_CODE 预置）
dotnet run --project server/RemoteCI.Server --no-build

# 插件打包 cipx（输出 plugin/RemoteCI.Plugin/cipx/RemoteCI.Plugin.cipx + checksums.md）
dotnet build plugin/RemoteCI.Plugin -p:CreateCipx=true
```

### 5.3 服务端 API 冒烟测试（PowerShell）

```powershell
# 插件配对码在 WebUI“生成插件配对码”获取，只能被消费一次；默认不写日志，仅当开启 Server:LogBootstrapSecrets 时才输出到首次启动日志。
$body = @{ pairCode = '从 WebUI 获取的配对码'; role = 'plugin' } | ConvertTo-Json
$r = Invoke-RestMethod -Uri 'http://127.0.0.1:8080/api/plugin/pair' -Method Post -ContentType 'application/json' -Body $body
# $r.token 即插件长期凭据；手表登录走 /api/auth/login，后续请求用 Bearer accessToken
```

注意：在 PowerShell 中用 `curl`（curl.exe）传 JSON 易被引号转义搞坏，冒烟测试建议用 `Invoke-RestMethod`。

### 5.4 插件在 ClassIsland 中加载调试

- 开发模式：设环境变量 `ClassIsland_DebugBinaryFile` 指向 `plugin/RemoteCI.Plugin/bin/Debug/net8.0/RemoteCI.Plugin.dll`，ClassIsland 启动时自动加载。
- 分发模式：把 `cipx/RemoteCI.Plugin.cipx` 放入 ClassIsland 插件目录，或在应用内"插件市场"离线安装。
- 插件端依赖：`ClassIsland.PluginSdk` 2.1.0.1（LGPLv3，仅编译期，ExcludeAssets runtime/native；最后一个 .NET 8 SDK，对应 manifest `apiVersion: 2.1.0.0`，不再适配 ClassIsland 2.0.x）+ `Fleck`（MIT，局域网 WebSocket 服务器）。
## Wear OS 构建环境

手表端编译目标为 Java/Kotlin 17，运行 Gradle 9.7 需 JDK 17 及以上，未固定守护进程 JVM 版本（android 端则由 `android/gradle/gradle-daemon-jvm.properties` 固定为 JDK 25，本机缺失时按文件内地址自动下载）。Windows 上应设置用户级 `JAVA_HOME` 指向 JDK 17+，并确保 `wearos/local.properties` 指向已安装的 Android SDK：

```powershell
$env:JAVA_HOME="C:\path\to\jdk-17"
cd wearos
.\gradlew.bat testDebugUnitTest assembleDebug
```

本机 2026-08-11 验证使用 Eclipse Temurin 17.0.20 和现有 Android SDK，Debug APK 构建成功。


## Android 手机应用

源码在 `android/`，包名 `com.remoteci.mobile`，minSdk 26。构建方式与手表端相同，使用同一套发布签名。

- WebUI 概览页按需生成“手机扫码登录”二维码：二维码携带服务器地址、登录 ID 与 5 分钟有效的一次性票据，手机端通过竖屏扫码页识别二维码，可打开手电筒或从相册选择二维码图片，凭票据直接登录当前账号，不再输入密码；“账号与设置 → 连接与服务器”页扫码同样可直接切换账号。旧版只含服务器地址的二维码仍作为服务器地址填入。二维码中的服务器地址默认取当前 WebUI 访问地址，系统管理员可在“系统配置 → 手机扫码登录”中改为其他地址（例如反向代理后的公网地址），该设置只影响二维码内容。
- 手机端设备名以 `Android · 厂商 型号` 上报，设备管理据此显示手机图标；旧会话在重新登录前仍可能沿用原设备名。
- 浅色、深色或跟随系统与六套固定 Material 3 配色由根主题统一提供，系统状态栏和导航栏图标亮暗随主题同步；默认选中与 WebUI 同色板的“清新”配色（雾白与浅薄荷表面、深青强调、晴空蓝次要容器，卡片 24dp、大卡片 32dp 圆角）；外观页另提供“动态取色”，Android 12+ 使用 Material 3 原生动态配色接口从系统壁纸读取 Monet 配色，低版本回退到清新配色。已保存的配色选择升级后不变。
- 控制操作统一通过根级 Snackbar 显示真实回执；WebSocket 拒绝发送时立即提示连接已断开。
- 控制项和命令使用当前班级的有效权限；全局学生在担任班管理员的班级也能执行该班允许的控制。云端转发给插件的发送人权限与目标班级一致，插件仍会复核权限。
- 多班级推送按班级缓存，选班后立即使用已收到的状态和课表；普通老师、学生查看缓存课表，不自动发起管理员专用的课表拉取。换班会清除原班级的任务提示。
- 首页“课表已于 HH:mm 同步”按手机本地时区显示，二级页面返回时向右转场。
- 局域网 WebSocket 连接仍会先刷新并保留云端短期访问令牌，供账号、角色、访客、凭证、备份等 WebUI 管理 API 使用；此前只连局域网时令牌为空会导致这些页面整体失效。
- 访客访问与自动进入访客页收纳在独立子页面，账号创建通过下拉列表选择服务端角色，配对码支持选择与复制。
- 首页底栏的四个板块共享同一个页面动画容器，切换板块只替换正文，底栏不参与横向转场。
- 手机端除“清新”外的六套固定主题与 Wear OS `WatchPalette` 使用相同色值，外观页采用 Material 3 `ListItem` 与单选控件；固定主题行只显示主题名称，动态取色单独显示壁纸取色说明。
- 桌面小组件：当前时间状态、今日课表和 1×2 换课小组件，由最近一次有效快照驱动并按尺寸动态显示课程数量；使用标准 AppWidget/RemoteViews 兼容小米澎湃 OS。小米小部件标识默认关闭（`miuiWidget` 占位符），按小米规范审核通过后用 `-Premoteci.miuiWidget=true` 构建，此后不得再关闭（见 `android/app/build.gradle.kts`）。
- “上课状态”持续通知：上学期间发布一条可更新通知，学生显示当前时间状态、这节课、剩余时间和下一节课（剩余时间由系统计时器倒数到本阶段结束时刻，超级岛右侧同样显示“距下课/上课”倒计时；正文第一行为本时段时间，第二行为“下一节：课程 时间”，上课时图标为日历、课间为勿扰月亮；课间超级岛显示“课间-下一节课名”，进入 ClassIsland 上课提醒设置的准备时长（室内/室外课分别生效，含科目/时间点附加设置）后切换为“即将上课-下一节课名”；上课期间“下一节”为当堂课结束后的第一节课）；放学或没有课程时自动撤销。
- 老师账号使用“我的日程”跨班级接口计算当前课与下一节课，不会误用当前选中的班级；老师模式上课中倒计时到下课；下一节课开始前 10 分钟内显示“即将上课”并倒计时到上课（通知出现时提醒一次）；今天稍后还有课但未到这 10 分钟时首页显示“空闲”、通知收起，今天没有课了显示“放学”，不会因为下一节在明天或下周而整晚、整个周末显示“即将上课”。
- 通知带有小米 HyperOS 焦点通知/超级岛及息屏（AOD）字段；Android 16+ 还会请求 Live Update promoted ongoing。在支持且已开启对应系统权限的设备上由系统显示；其他 Android 设备继续显示普通持续通知。小米 17 Pro、17 Pro Max、18 Pro、18 Pro Max 的原生 Smart Assistant 背屏卡片仍由 HyperOS 系统服务和设备白名单控制，正式上线还需要按小米平台要求提交场景审核和设备白名单联调。

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'
cd android
.\gradlew.bat :app:assembleRelease
```

本机可将构建目录放到 `android/local.properties` 的 `remoteci.buildDir`。Debug/Release APK 默认在 `android/app/build/outputs/apk/`，若配置了外部目录则在该目录的 `app/outputs/apk/`。
