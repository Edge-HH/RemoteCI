# RemoteCI 协议 V3

协议版本和软件版本是两个概念。当前稳定版服务端、插件、手机和手表的软件发布版本统一为 `3.3.1.8`，但所有 WebSocket 信封都使用整数 `protocolVersion: 3`。稳定版使用四段纯数字版本，Beta 使用 `v3.x.x-beta.y`；软件版本只用于更新和诊断，不参与连接拒绝；只有协议号不是 `3` 时才返回 `PROTOCOL_VERSION_UNSUPPORTED`。

V3 内只能增加可选字段、新消息和新能力，未知字段与未知能力标识必须安全忽略。删除字段、改变既有字段含义或改变既有命令语义属于破坏性变更，必须升级为 V4。

## 身份与权限

有效权限是位掩码：

| 值 | 权限 |
| ---: | --- |
| 1 | `ViewCurrentCourse` |
| 2 | `AccessWebUi` |
| 4 | `ManageUsers` |
| 8 | `SendNotifications` |
| 16 | `ManageSchedule` |
| 32 | `PowerControl`（旧名称 `SystemControl` 保留为别名） |
| 64 | `TeacherComing`（旧协议保留，当前 UI 暂时隐藏） |
| 128 | `RunExtensions` |
| 256 | `MainMenuControl` |
| 512 | `SendVoiceMessages`（发送语音） |
| 1024 | `ChangeDisplayName`（修改用户名） |
| 2048 | `ApiAccess`（REST API Key；插件协议仅透传该位） |
| 4096 | `RequestScheduleSwap`（老师主动换课：发起与审批换课申请） |
| 8192 | `ForceScheduleSwap`（强制换课：不经审批立即生效，对方可撤回） |
| 16384 | `SendFiles`（文件分发：经服务端中转把文件发到有权班级设备的桌面、下载或文档文件夹） |
| 32768 | `CaptureScreen`（屏幕截图：获取有权班级教室电脑的当前屏幕） |

管理员的有效权限固定为 65535（全部位）。`SendFiles` 默认不授予任何内置角色，由系统管理员按需开启；`CaptureScreen` 默认授予内置班主任角色（升级迁移会为已有的班主任角色补上一次），老师需由系统管理员在账号或角色中开启。`RequestScheduleSwap` 与 `ForceScheduleSwap` 是全局权限，由系统管理员在“角色配置”中开关：内置老师和班主任默认开启前者（升级迁移会为已有的两个内置角色补上一次），后者默认关闭。普通用户固定包含值 1，其余权限来自服务端授权。权限设置界面将值 2 显示为“概览”；七日课表查看只要求账号已登录，手动拉取课表允许系统管理员，以及班级自治策略允许时的本班班主任；定时自动拉取间隔是全局设置，只有系统管理员可以修改，值 16 保护换课。`TeacherComing` 权限位和命令仅为旧客户端兼容保留，当前不在 WebUI、各端 App 或权限设置中显示；`SendNotifications` 只保护自定义通知与清除提醒，`SendVoiceMessages` 独立保护语音消息，`RunExtensions` 是所有插件扩展的独立权限，`MainMenuControl` 保护主界面显隐，`PowerControl` 保护音量和 Windows 电源操作，`ChangeDisplayName` 仅保留为兼容旧权限数据，实际只有系统管理员可以修改用户可见用户名（DisplayName，登录 ID 不变）；用户名会影响老师获取的日程信息。`ApiAccess` 允许账号创建 API Key 并调用 REST API；管理员、班主任与老师默认拥有 API 访问。

账号密码只用于 `POST /api/auth/login` 登录请求，不在设备上持久化。启用二步验证时，首次请求返回 `twoFactorRequired: true`，不签发会话；客户端提示验证码后，在同一端点重发账号、密码和 `twoFactorCode`，使用恢复码时再传 `useRecoveryCode: true`。`/api/auth/mobile-login` 同样支持这两个可选字段，提示验证时原票据保持有效，完成后一次性消费。验证码错误返回 401，并计入账号锁定。旧客户端需升级才能登录已启用二步验证的账号，详见 [二步验证](two-factor.md)。生产环境必须使用 HTTPS；Android 手机端为兼容尚未配置 TLS 的现有部署，允许用户在持续显示风险提示的情况下明确连接 HTTP 云服务器。成功响应包含 1 小时 `accessToken`、30 天 `deviceSessionId/deviceSecret` 和用户有效权限。`POST /api/auth/refresh` 会同时轮换访问令牌和设备密钥；旧值立即失效。

## 局域网挑战认证

插件建立连接后发送 `auth_challenge`：

```json
{"challengeId":"...","nonce":"base64","expiresAt":"..."}
```

手表计算：

1. `verifier = SHA256(UTF8(deviceSecret))`。
2. 生成随机 `clientNonce`。
3. 构造 UTF-8 文本 `3|challengeId|nonce|clientNonce|deviceSessionIdWithoutHyphensLowercase`。
4. `proof = Base64(HMAC-SHA256(verifier, canonicalText))`。
5. 发送 `auth_proof`，只包含挑战号、设备会话 ID、客户端随机数和证明。

插件只保存服务器同步的 `verifier`，挑战有效期 30 秒且先消费后校验，因此失败和成功请求都不能重放。授权镜像超过 24 小时仍可认证查看课程，但有效权限被收缩为 `ViewCurrentCourse`。

## WebSocket 消息

| 类型 | 方向 | 载荷 |
| --- | --- | --- |
| `auth_challenge` | 插件 → 手表 | `AuthChallenge` |
| `auth_proof` | 手表 → 插件 | `AuthProof` |
| `auth_state` | 接入端 → 手表 | 当前用户、有效权限、所连接服务端的 `serverVersion`，或错误码 |
| `account_sync` | 服务端 → 插件 | 账号元数据、有效权限、设备验证器、`serverVersion`、可选服务端能力、镜像版本和生成时间 |
| `peer_capabilities` | 插件/手表 → 服务端 | 当前端的 `softwareVersion` 和稳定字符串能力列表 |
| `capabilities_sync` | 服务端/插件 → 手表 | 服务端能力，以及接收方每个可访问班级主插件的能力 `classPlugins: [{classId, plugin}]`；`plugin` 为兼容旧客户端保留 |
| `software_inventory` | 插件 → 服务端 | 设备名称、ClassIsland 主程序与当前已加载插件的版本、可用更新和最近升级状态 |
| `state_push` | 插件 → 服务端/手表 | 高频当前课程、提醒播放、主界面显隐与可用电源状态，不含完整课表 |
| `schedule_sync` | 插件 → 服务端/手表 | 今天起 30 天（旧版插件 7 天）的日期、课程、科目清单和每日修订号；课表页、我的日程、日历与手表浏览只取前 7 天，其余日期供换课选择；新版插件附带调课回执 `adjustments`（只发给服务端，服务端对账后不保存、不转发），见下文“调课记录与托管临时层” |
| `schedule_pull` | 服务端/手表/手机 → 插件 | 只读请求，载荷可含 `{taskId, source, requestedAt}`，要求插件立即重新生成并推送七日课表；手机端 `source` 为 6。云端拒绝无权请求时向发起方回一条 `Failed` 状态的 `schedule_sync_status` |
| `schedule_sync_status` | 插件 → 服务端/手表 | 全局课表任务状态：Running、Completed、Failed 或 Busy，以及任务来源和占用任务 ID |
| `extensions_sync` | 插件 → 服务端/手表 | 扩展功能清单（id、displayName、icon、requiredPermission、parameters，可选 description、groupId） |
| `extension_groups_sync` | 插件 → 服务端 | 扩展分组（通常对应一个 ClassIsland 插件）：id、displayName、description、icon、设置字段 `settings` 与设备当前值 `values`；仅供 WebUI，手表与局域网不使用 |
| `event_notify` | 插件 → 服务端/手表 | 上课、下课、放学、课表变更、自定义消息、ClassIsland 自动化或第三方插件通知 |
| `command` | 手表/服务端 → 插件 | 结构化换课、通知、主界面、音量、电源、软件升级、插件管理、档案分发、时间表、远程终端、文件分发与集控命令 |
| `command_result` | 插件 → 发起者 | 真实成功、失败码、消息和可选新修订号 |
| `settings_sync` | 服务端 → 手表 | 全局通知设置快照（目前含 `forceSenderInTitle`） |
| `plugin_network_info` | 插件 → 服务端 → 手表 | 插件局域网直连地址与端口（每次云端重连时重新发现网卡） |
| `connection_bootstrap` | 插件 → 手表 | 用户选中局域网插件后，插件返回的云端连接信息 |
| `user_notify` | 服务端 → 手机/手表 | 面向当前账号的个人通知 `UserNotificationView {id, kind, title, body, swapRequestId?, createdAt, readAt?}`，投递给该账号全部在线连接，不按班级过滤；旧客户端忽略未知类型 |
| `holiday_calendar` | 服务端 → 插件 | 调休日历 `HolidayCalendar {enabled, generatedAt, days[]}`，见下文“调休”；只发给声明了 `schedule.holiday-calendar` 能力的插件 |
| `semester` | 服务端 → 插件 | 学期设置 `SemesterSync {weekOneMonday}`，见下文“学期”；只发给声明了 `schedule.semester` 能力的插件 |
| `schedule_adjustments` | 服务端 → 插件 | 该班今天起的全部有效调课 `ScheduleAdjustmentSet {classId, version, generatedAt, items[], acknowledgedLocalIds[]}`，见下文“调课记录与托管临时层”；只发给声明了 `schedule.adjustments` 能力的班级插件 |
| `file_transfer_progress` | 插件 → 服务端 | 中转文件的下载进度与最终结果，见下文“文件分发（中转方式）” |
| `app_guard_policy` | 服务端 → 插件 | 该设备的应用守护生效策略 `AppGuardPolicy`，见下文“应用守护”；只发给声明了 `host.app-guard` 能力的插件 |
| `app_guard_event` | 插件 → 服务端 | 守护进程记录的事件 `AppGuardEventBatch {events:[{kind, atUnixMs, detail?}]}`，未分配班级的设备也会上报 |

## 局域网设备发现

手表可以在登录页扫描同一局域网中的插件，无需手动填写电脑 IP：

1. 手表向固定 UDP 端口 `48765` 发送广播串 `REMOTECI_DISCOVER_V3`；插件应答 JSON `{protocolVersion, instanceName, port}`，`protocolVersion` 为整数 `3`，`port` 是插件当前局域网 WebSocket 端口。
2. 用户选中应答条目后，手表连接 `ws://<应答来源地址>:<port>/bootstrap`，插件返回 `connection_bootstrap` 载荷 `{instanceName, cloudServerUrl}`。该端点未认证，只提供云端地址与实例名，密码和会话凭据始终只交给云服务器。
3. 插件每次云端重连时重新发现本机网卡，通过 `plugin_network_info`（`{lanServerEnabled, addresses[], port}`）上报服务端；服务端归一化后广播给在线手表并缓存最新一份，新连接的手表在 `auth_state` 之后立即收到。手表据此更新局域网候选地址，地址或端口变化且当前走云端中转时自动重试直连。

发现与 bootstrap 均无认证，理论上可被同一局域网内的设备伪造；手表会原样展示获取到的云服务器地址，明文 HTTP 地址会额外提示风险，用户应在输入密码前确认地址可信。

`command_result.replyToMessageId` 必须等于请求的 `messageId`。服务端只把结果交给对应的 WebSocket 或等待中的 WebUI 请求，等待上限 15 秒。

插件通过云端 WebSocket 认证后，服务端必须立即发送一次 `schedule_pull`，避免插件启动时的首次 `schedule_sync` 早于云端连接建立而丢失。任何已认证手表都可以发送该只读消息：云端连接由服务端立即转发给在线插件，局域网连接直接交给插件。插件端是最终任务锁，服务端同时维护云端入口的前置锁；插件推送、WebUI 拉取、手表拉取、自动拉取和连接初始化任一正在运行时，新请求返回 `schedule_sync_status.state=Busy`，其中 `activeTaskId` 指向占用任务。Running、Completed、Failed 和 Busy 状态会广播到在线手表并供插件设置页、WebUI 展示；任务成功、失败、插件断开或 15 秒超时后释放。WebUI 收到新 `schedule_sync` 后用完整 `ScheduleBundle` 整体替换旧缓存，不做字段合并。该请求不授予换课能力，也不绕过 `ChangeSchedule` 的权限检查。插件离线期间不排队。

插件从未同步过课表的班级（服务端没有该班插件课表缓存）由服务端按该班当前班级档案推算七日课表，以 `schedule_sync` 下发给手机与手表，载荷 `ScheduleBundle.source` 为 `"profile"`（插件课表不带该字段）：手表/手机连接时随初始数据推送；班级档案保存后推送给覆盖该班的在线连接；手机/手表发送 `schedule_pull` 时先向发起方补发一份重新推算的结果（例如跨天后刷新），再照常转发给插件（插件离线时随后收到 `Failed` 状态）。推算课表不写入服务端插件课表缓存，修订号带 `profile:` 前缀，只供查看：客户端应隐藏换课入口，收到它时不把进行中的拉取任务当作成功；插件第一次推送 `schedule_sync` 后各端改用插件课表。

云端服务端直接在认证成功的 `auth_state.serverVersion` 中下发自身软件版本；插件通过 `account_sync.serverVersion` 保存同一版本，并在局域网认证成功时转发给手表。服务端与手表正式渠道只选择协议主版本相同的四段纯数字 Release，Beta 渠道额外选择 `v3.x.x-beta.y`及在 GitHub 标为预发布的四段 Release，旧三段 `v3.x.x` 稳定标签和 V4 不会进入自动更新候选。手表不再以 WebUI 软件版本为上限，仍保留渠道筛选、禁止降级、同版本强制覆盖和 APK 签名校验。

## 能力协商

V3 的基础能力（自 3.1.0 引入）为 `class-state.read`、`schedule.read`、`schedule.pull`、`schedule.change`、`notification.send`、`notification.clear`、`teacher-coming`、`main-menu.visibility`、`power.control`、`volume.control` 和 `extensions.run`。其中 `teacher-coming` 仅为旧 V3 客户端兼容保留，当前版本各端不显示入口。后续新增的 `voice-message.send`、`software.inventory`、`software.upgrade-plugins`、`software.upgrade-classisland`、`plugin.install`、`plugin.uninstall`、`plugin.enable`、`plugin.management-policy`、`profile.distribute`、`profile.apply`、`schedule.time-layout`、`management.join`、`schedule.subject-teacher`、`terminal.execute`、`file.distribute`、`extensions.settings`、`schedule.holiday-calendar`、`profile.read`、`profile.temp-layer`、`schedule.semester`、`file.transfer`、`host.app-guard`、`host.app-guard-launch`、`host.app-guard-manage`、`schedule.change-permanent`、`schedule.adjustments`、`screen.capture`、`plugin.package-install` 和 `host.app-guard-update` 只进入当前版本能力列表，不加入旧 V3 端默认获得的基础能力。插件和手表连接后通过 `peer_capabilities` 上报软件版本与能力；服务端通过 `capabilities_sync` 向手表发送自身和当前主插件的能力。未上报能力的旧 V3 端按上述基础能力处理，未知能力标识被忽略。

WebUI 的有效能力是“服务端 ∩ 当前班级主插件”，手表与手机的有效能力是“本地 ∩ 服务端 ∩ 当前班级主插件”。服务端在 `capabilities_sync.classPlugins` 中按接收方可访问的班级逐个下发主插件能力，客户端切换班级时按新班级重算；没有对应条目表示该班插件离线。旧服务端没有该字段时客户端退回 `plugin`。多插件时，当前主插件仍是最早接入的健康插件；主插件切换、断开或能力更新后，服务端重新广播能力快照。界面应隐藏缺失能力的入口，服务端转发命令前仍需按统一映射复核主插件能力，缺少能力时返回 `CAPABILITY_UNSUPPORTED`。能力声明不能绕过账号权限或扩展策略检查。

`event_notify.payload.event` 的值 6 表示 ClassIsland 自动化“显示提醒”行动产生的通知，值 7 表示第三方 ClassIsland 插件产生的通知。手表分别持久化开关；内置课程、天气等通知不会被值 7 重复转发。

自定义通知的标题与正文均可留空：标题留空时插件统一显示默认标题 `RemoteCI 通知`（仍会按上述规则添加前缀），正文留空时保持为空，由 ClassIsland 只显示标题。通知标题是否添加 `由用户名发送：` 前缀由服务端全局设置 `forceSenderInTitle` 决定（系统配置页开关，默认开启，仅系统管理员可修改）。开启时插件最终执行会把标题格式化为 `由用户名发送：原标题`；关闭时不添加前缀。发送者名称取自已认证账号的 `displayName`（界面称“用户名”），而 `username` 是唯一登录 ID；服务端转发命令时会按全局设置覆盖客户端请求中的署名标志，客户端不能绕过。设置变更时服务端通过 `settings_sync` 推送在线手表，手表通知页据此决定是否显示“将显示发送人”提示。通知请求还可通过 `isNotificationEffectEnabled`、`isNotificationSoundEnabled` 和 `isSpeechEnabled` 分别控制 ClassIsland 的提醒强调特效、提醒音效和语音朗读；省略时均为关闭。`isNotificationTopmostEnabled` 控制提醒时置顶主界面；`durationSeconds`（默认 5，范围 1-3600）与 `repeatCounts`（默认 1，范围 1-10）对齐 ClassIsland 集控，省略或 0 表示默认值。`isRollingEnabled` 是三态字段，控制正文是否横向滚动：省略时（旧 V3 客户端）保持升级前的行为，正文滚动 `repeatCounts` 遍；显式 `true` 同样滚动，总时长为 `durationSeconds × repeatCounts`；只有显式 `false` 才静态显示正文 `durationSeconds` 秒，并把整条提醒依次显示 `repeatCounts` 次。服务端转发时不会把缺失字段补成 `false`，新客户端关闭滚动时必须显式发送 `false`。旧插件忽略该字段并始终滚动。

通知参数上限：标题 60 字（超出部分由插件截断，与旧行为一致）、正文 500 字、持续时间 1-3600 秒、重复次数 1-10 次。WebSocket 命令、`POST /api/commands`、`POST /api/commands/broadcast` 与 WebUI 共用同一校验，越界请求返回 `INVALID_REQUEST`；插件执行端对局域网直连请求再次校验，并对时长与重复次数限幅。`POST /api/commands` 与 WebSocket 一样按全局 `forceSenderInTitle` 覆盖请求中的署名标志。

控制命令值 3 为清除当前 ClassIsland 提醒，值 4 通过 `mainMenuVisible` 设置主界面显隐，值 5 通过 `powerAction` 选择关机、重启、睡眠或休眠。值 6 通过 `volume.level` 设置 Windows 默认播放设备的 0-100 主音量，或通过 `volume.muted` 设置静音状态；WebUI 在静音状态下向高调节时会在同一命令中同时发送 `level` 与 `muted: false`。休眠入口只在插件状态报告 Windows 已启用休眠时显示。

主界面文件即 ClassIsland 设置中“主界面”的组件配置文件（`Config/ComponentLayouts/<名称>.json`，当前文件由 ClassIsland 设置 `CurrentComponentConfig` 选择）。命令值 29 `ApplyMainWindowLayout` 通过 `mainWindowLayout` 上传：`layoutJson` 为文件内容（必须是含 `Lines` 数组的 JSON 对象，UTF-8 不超过 2 MB）；`mode` 为 1 时覆盖设备当前使用的主界面文件并立即重新加载，为 2 时按 `name`（不含 `.json`，最长 64 字符，不能含路径或非法字符）新建文件，已存在同名文件时拒绝、绝不覆盖；`activate`（默认 `true`）只对新建生效，决定是否立即切换到新文件。覆盖后重新加载失败会恢复原文件，新建后切换失败会删除新文件并切回原主界面。命令值 30 `ReadMainWindowLayout` 读取当前主界面文件，成功时 `data` 是 JSON 字符串 `{"name","layoutJson","layouts","isManagementMode"}`：`name` 为当前文件名，`layouts` 为设备上全部主界面文件名。设备处于 ClassIsland 集控模式时主界面由集控管理，只能读取，上传返回 `INVALID_REQUEST`。两条命令都需要“主界面”权限（`MainMenuControl`，256），对应能力分别为 `main-window.layout-apply` 与 `main-window.layout-read`，可经 WebSocket 或 `POST /api/commands` 发送；WebUI 在控制页“当前班级 → 主界面文件”提供上传（覆盖 / 新建并使用）与获取（下载 JSON）。

`extensions_sync` 的载荷是其他 ClassIsland 插件通过 RemoteCI 注册的扩展功能列表；云端服务端和插件局域网服务都会缓存最近一次清单，并在手表完成认证后主动补发。命令值 7 为 `RunExtension`；命令值 8 的旧提醒指令仍由协议层兼容处理，但当前各端不提供入口。通过 `extensionId` 指定目标扩展，`extensionArgs` 携带参数字典（值统一为字符串）。扩展调用必须同时通过独立的 `RunExtensions` 权限和管理员为该扩展设置的启用/普通账号开放策略；`RequiredPermission` 只作为旧扩展兼容字段传输，不再关联通知、电源等权限。账号的 `allowedExtensionIds` 与 `visibleExtensionIds` 随认证状态和 `account_sync` 下发，后者只控制自己的手表入口。未注册、缺少必填参数或权限不足时分别返回 `INVALID_REQUEST` / `FORBIDDEN`，执行异常统一返回 `INTERNAL_ERROR`。

扩展参数与扩展设置字段共用 `ExtensionParameter` 结构：`key`、`label`（显示名称）、`type`（1=文本、2=数字、3=开关、4=选项、5=颜色）、`defaultValue`、`required`、`options`，以及可选的 `optionLabels`（与 `options` 按下标对应的选项显示名称）、`description`（字段说明）、`placeholder`、`multiline`（WebUI 多行文本）、`min` / `max`（数字范围）和 `step`（数字步长，仅影响输入控件）。服务端预检与插件执行端使用同一套校验：数字必须可解析且在范围内，开关只能是 `true` / `false`（规范化为小写），选项必须是 `options` 之一，颜色必须是 `#RGB` / `#ARGB` / `#RRGGBB` / `#AARRGGBB`；未声明的执行参数键原样透传以兼容旧扩展。旧手表忽略新增字段，选项按原值显示，颜色按文本处理。

扩展设置字段还可以声明 WebUI 设置页布局，让插件在 WebUI 中复刻自己在 ClassIsland 里的设置页（全部可选，旧服务端忽略后退回平铺表单）：`section`（分区标题，连续同名字段归为一段）、`card` / `cardDescription`（设置卡片标题与说明，同一分区内连续同名字段归为一张可折叠卡片）、`cardToggle`（开关字段渲染在卡片标题行，作为卡片总开关）、`slider`（数字字段同时显示滑块，需同时声明 `min` 与 `max`），以及 `visibleWhen` / `enabledWhen` 联动条件数组。每个条件为 `{ key, values, not }`：引用同一表单内另一字段的当前值，值属于 `values` 之一即满足（数字按数值比较，其余忽略大小写），`not` 取反；数组内条件全部满足才显示 / 可编辑。卡片总开关只决定摆放位置，卡片内条目是否随开关禁用须在条目的 `enabledWhen` 中显式声明。隐藏或禁用的字段仍按原值提交，不会被清空；卡片内字段全部隐藏时整张卡片隐藏。

扩展清单的 `icon` 为可选 Material 图标名，由手表端白名单解析（不区分大小写，忽略下划线、连字符、空格与 `Icons.Rounded.` 前缀）；未命中白名单或缺失时手表按钮回退为纯文字，服务端与 WebUI 不解析该字段。

状态快照中的 `isVolumeControlAvailable`、`volumePercent` 和 `isMuted` 分别表示默认播放设备是否可控、当前主音量百分比和静音状态，手表必须以这些真实状态刷新音量页。

状态快照中的 `currentTimeLayoutItem` 使用插件本地时间（如 `16:30-17:10 语文`），并携带 `timeZoneOffsetMinutes`（插件本地时区相对 UTC 的偏移分钟数，东八区为 480）。手表端以快照的 `generatedAt`（UTC）加该偏移推算“插件本地当前时间”，再计算课程进度环，避免两端时区不一致时进度环显示为空。旧版插件不携带 `timeZoneOffsetMinutes` 时，手表回退到自身本地时间，行为与旧版一致。

## 换课

`ScheduleChangeRequest` 包含：

- `date`：今天起 30 天内的 `yyyy-MM-dd`（旧版插件只接受 7 天内）。
- `mode`：1 为交换，2 为替换。
- `sourceIndex`：原节次的零基索引。
- `targetIndex`：交换模式必填。
- `replacementSubjectId`：替换模式必填。
- `expectedRevision`：客户端读取当天课表时的修订号。
- `permanent`：为 `true` 时直接修改 ClassIsland 源课表，本周及以后每周持续生效；当天已有临时层时写入临时层的来源课表（`OverlaySourceId`），而不是只改当天（`schedule.change-permanent` 能力）。默认 `false`，只影响当天。

声明 `schedule.adjustments` 的插件把临时换课（`permanent: false`，无论来自 WebUI 课表页、手机、手表、`POST /api/commands` 还是局域网）记为本地调课：按当天生效科目与基础科目换算成逐节调课，写入该日的托管临时层，并随下一次课表推送上报给服务端收编，见“调课记录与托管临时层”。客户端无需改动；该日无法托管（没有课表、外来临时层节数与基础课表不同）时退回直接修改当天课表。永久换课写入源课表后，插件按新的基础课表重建托管临时层，守卫对不上的调课回报为冲突。

手机端的能力快照只用于界面提示，不能代替服务端鉴权；换课入口在当前班级拥有 `ManageSchedule` 时仍可提交，最终由服务端和目标班级主插件复核能力并返回 `CAPABILITY_UNSUPPORTED` 等结果。

插件发现修订号已变化时返回 `SCHEDULE_STALE` 和最新修订号，不覆盖别人刚完成的修改。

## 调休

服务端定时从 holiday-cn 拉取法定节假日与调休安排，组装今天往前 1 天到往后 60 天的调休日历，经 `holiday_calendar` 消息推送给声明了 `schedule.holiday-calendar` 能力的插件：插件上报能力后立即发送一次，之后内容变化（刷新、管理员修改设置或补课安排、跨天窗口滚动）时再广播给所有此类插件，而不只发给班级主插件。

`days[]` 每项为 `{date, kind, name, followWeekday?, followSource?}`：`kind` 为 `off`（放假日）或 `makeup`（调休上学日）；调休上学日的 `followWeekday` 为 1-5（周一至周五），为 null 时表示不补课或无法推算，`followSource` 为 `auto` / `manual` / `skip` / `unresolved`。`enabled` 为 `false` 时 `days` 为空，插件应撤销此前做过的全部调整。

插件把日历缓存到本地，断网时继续生效：放假日把宿主 `LessonsService.IsClassPlanEnabled` 设为 `false`，并且只恢复自己关掉的开关；调休上学日在今天起 30 天内建立托管临时层，基础课表为该周对应工作日按规则匹配的课表（忽略那天的临时层，不会把那天的换课复制过来），当天的调课叠加在上面；管理员给该日预定了普通课表时以管理员为准。调休安排变化时托管临时层按新的基础课表重建，其中的调课保留或按守卫回报冲突。放假日上的调课回报冲突 `HOLIDAY`。旧版插件建立的调休临时课表（`HolidayState.json`）在升级后首次运行时接管，其中旧版换课的改动转为本地调课保留。

插件上报的 `ScheduleDay` 新增可选字段 `dayKind`（`holiday` / `makeup`）和 `holidayName`，普通日期不输出。放假日 `enabled` 为 `false` 且不含课程，因此换课会被拒绝。

## 学期

系统管理员在 WebUI“学期与调休”页或 `PUT /api/settings/semester` 设置全局学期开始日期。学期开始日期所在周（周一起）为第 1 教学周，单周为第 1、3、5… 周；多周轮换按 `(教学周序号 − 1) mod 轮换周数 + 1` 得到轮换中的第几周，与 ClassIsland 课表 `TimeRule.WeekCountDiv / WeekCountDivTotal` 的含义一致。

- `GET /api/settings/semester`（任意已登录账号）：返回 `{startDate, syncToClassrooms, currentWeek?}`；`startDate` 为 `yyyy-MM-dd` 或 null（未设置），`currentWeek` 为按服务端今天计算的教学周，未设置或尚未开学时省略。
- `PUT /api/settings/semester`（仅系统管理员）：请求体 `{startDate, syncToClassrooms}`，`startDate` 为空表示清除；格式错误返回 400。`syncToClassrooms` 默认 true。

学期开始日期用于：服务端按班级档案推算课表时选择多周轮换课表（未设置时按“本周为第 1 周”推算并提示可能与教室端不同）；教师自定义课程的单双周与多周轮换。

开启 `syncToClassrooms` 且已设置日期时，服务端向声明了 `schedule.semester` 能力的插件发送 `semester` 消息 `{weekOneMonday: "yyyy-MM-dd"}`：插件上报能力后立即发送一次，管理员修改后广播给所有此类插件。插件把第 1 教学周的周一写入 ClassIsland 设置的 `SingleWeekStartTime`，清除本机 `MultiWeekRotationOffset`（“调整轮换”偏移）并保存设置，随后重新上报课表；与本机设置已一致时不写入。未设置日期或关闭同步时不发送，教室端保留自己的设置，清除学期日期也不会改回教室端。

## 应用守护

系统管理员在 WebUI“应用守护”页设置全局默认策略，可对单台设备（插件凭据）整体覆盖；使用说明见 [应用守护](app-guard.md)。服务端按“有覆盖用覆盖，否则用全局”算出每台设备的生效策略，向声明了 `host.app-guard` 能力的插件发送 `app_guard_policy`。每台设备的策略可能不同，所以逐个连接发送，不使用同一份广播：

- 插件上报能力后立即发送一次。未启用时也发送，插件据此注销已部署的守护进程。
- 修改全局策略或系统配置中的组织名后，发给全部此类在线插件。
- 修改某台设备的覆盖后，只发给该设备。

载荷 `AppGuardPolicy`：

| 字段 | 说明 |
| --- | --- |
| `enabled` | 总开关 |
| `restartAfterExit` | 正常退出后自动启动 |
| `restartAfterExitDelayMinutes` | 退出后延时分钟数，0–1440；0 为立即 |
| `restartOnCrash` | 意外退出时重启 |
| `preventDeletion` | 定时备份，被删除后恢复 |
| `backupIntervalHours` | 备份间隔小时数，1–168，默认 24 |
| `enhancedMode` | 安装自动启动 Windows 服务，维护普通权限用户代理 |
| `randomBackupPath` | 启用 ACL 受限的安全备份目录 |
| `backupDirectoryName` | `%LOCALAPPDATA%\RemoteCI\ProtectedBackups` 下的子目录名；空表示随机生成并复用，最长 64 字符 |
| `processName` | 守护代理文件名（可省略 `.exe`），默认 `RemoteCI.Guard`，最长 48 字符 |
| `backupPaths` | 最多 3 个额外绝对备份路径，每项最长 260 字符 |
| `organizationName` | 系统配置中的组织名，未设置时为 `RemoteCI` |

越界数值由服务端与插件各自截断。插件把策略缓存为 `AppGuardPolicy.json`，并把守护进程部署到 `%LOCALAPPDATA%\RemoteCI\Guard\<processName>.exe`，从不直接运行 ClassIsland 安装目录内的守护副本。

守护使用相同设备令牌连接 `/ws?token=...&client=guard`，声明 `host.app-guard`、`host.app-guard-launch` 与 `host.app-guard-manage`。插件与守护按凭据合并为同一设备，完整插件优先参与普通路由；守护只接收 `app_guard_policy` 与命令 28 `LaunchClassIsland`、32 `StopAppGuard`、33 `UninstallAppGuard`，不接收账号或课表数据。三条命令仅限具有 `ManageUsers` 的系统管理员，执行端复核 `requestedBy.role=Admin` 和权限位；课表已运行时命令 28 幂等成功。命令 32 停用策略并退出守护进程，保留安装文件；命令 33 先请求 UAC 移除增强服务与登录任务，再停用策略、退出守护并延迟删除默认目录和额外备份。守护先发送 `command_result`，成功后才断开连接；`app_guard_event` 与 `command_result` 是守护允许上报的其他消息。WebUI 只在守护在线且声明 `host.app-guard-manage` 时提供关闭/卸载入口，命令成功后把该设备保存为停用的单设备覆盖。同一凭据有多条守护连接时（旧守护被替换后连接尚未超时断开），服务端按最新接入的守护判断能力并发送守护命令。

命令 34 `UpdateAppGuard` 发给课表插件而不是守护：插件结束守护，用插件包随附的 `RemoteCI.Guard.exe` 强制覆盖后重新部署并启动，在 `command_result.message` 中返回新版本或失败原因；策略未启用时返回 `INVALID_REQUEST`。它同样仅限系统管理员（`ManageUsers` 且 `role=Admin`），是服务端专用命令，需要插件声明 `host.app-guard-update`；该过程不弹 UAC，不改动增强服务。WebUI 在策略启用、课表插件在线且声明该能力，并且守护不是最新时显示“更新守护”；守护在线、声明 `host.app-guard-manage` 且 `peer_capabilities.softwareVersion` 与插件相同即视为最新。守护与插件都上报去掉构建元数据（`+` 之后部分）的 InformationalVersion。

守护使用 .NET Framework 的 `DataContractJsonSerializer`，信封 `timestamp` 必须按 ISO 8601 字符串收发；`DateTimeOffset` 的默认对象格式会让服务端丢弃整条消息。

守护进程记录的事件由插件或独立守护通道以 `app_guard_event` 上报，`kind` 取值：

- `restored`：已自动恢复；
- `restore-failed`：恢复失败；
- `crash-restarted`：意外退出后重启；
- `exit-restarted`：退出后自启；
- `crash-loop-paused`：频繁崩溃，暂停重启；
- `backup-completed`：备份完成；
- `backup-failed`：备份失败。

服务端按插件凭据记录这些事件，保留 90 天。`restored` 与 `restore-failed` 另向全部系统管理员发送个人通知 `app_guard`，`link` 为 `/AppGuard`。

REST 接口全部仅限系统管理员，其他账号返回 403：

- `GET/PUT /api/settings/app-guard`：全局策略，请求体与响应都是 `AppGuardPolicy`。`organizationName` 为只读，在系统配置中修改。
- `GET/PUT/DELETE /api/devices/{credentialId}/app-guard`：单台设备。响应为 `{credentialId, overridden, policy}`。`PUT` 设置覆盖，`DELETE` 恢复沿用全局；设备不存在返回 404。
- `GET /api/app-guard/events?credentialId=&limit=`：最近事件，按时间倒序，`limit` 默认 100、最大 500。每条为 `[{credentialId, deviceName, kind, kindName, detail, atUnixMs}]`。

全局策略、组织名与设备覆盖随配置包导出与恢复；守护事件不进入配置包。

## 换课申请

拥有 `RequestScheduleSwap` 的老师可以向其他老师发起临时换课申请，对方同意后服务端写成调课记录，由教室端插件生成临时层生效（见“调课记录与托管临时层”）。教室端插件必须声明 `schedule.adjustments`：插件在线但版本过旧时，提交或审批返回 422 `CAPABILITY_UNSUPPORTED`（“教室端插件版本过旧，请先升级 RemoteCI 插件”）。

- **模式**：`mode` 1 为互换，`source` 与 `target` 两节课可以跨班、跨日，也可以选别人的课，但至少一节的老师必须是申请人；跨班时学科按名称在各自班级匹配。2 为替换，只填 `target` 与申请人任教的 `subjectName`。
- **审批人**：对方课位老师字段匹配到的账号；匹配不到时回退为该班班主任，再回退为系统管理员。两节都是申请人自己的课时直接生效。
- **重复申请**：同一对课位（不分先后、不分申请人）、同一模式与换入学科只能有一份待审批申请，否则返回 409 `SWAP_STATE_CONFLICT`。
- **冲突提示**：提交、预检（`POST /api/swap-requests/preview`）与审批时，服务端按真实上下课时间检查换课后的任课老师是否同时在另一个班上课（`teacher_clash`，各班时间表可以不同，不比较节次序号；本次换课腾出的课位不算），以及涉及的课是否还有其他待审批申请（`slot_pending`）。这两类需要确认：未带 `acknowledgeConflicts: true` 时返回 409 `SWAP_CONFLICTS`，错误体 `conflicts[]` 列出 `SwapConflict {kind, message, requiresAcknowledgement, classId?, className?, date?, index?, label?, subject?, teacher?, startTime?, endTime?, swapRequestId?}`；确认后照常执行（例如合班上课），确认过的冲突记录在申请的 `acknowledgedConflicts` 中。`slot_adjusted`（课位当天已调整过）只作提示，不需要确认。审批通过的 `SwapDecisionRequest` 同样带 `acknowledgeConflicts`。
- **执行**：执行前按服务端叠加调课后的最新课表复核课位快照，变化时返回 `SWAP_SLOT_CHANGED`。随后写入调课记录（守卫为各节基础科目）、下发调课集合，并等待插件回执约 8 秒：全部生效即通过；插件报告冲突时撤掉这次的记录，申请保持待审批并返回 409 `SWAP_SLOT_CHANGED`；插件离线或超时时照常通过，`delivery` 为 `pending`（“教室电脑暂未确认，上线后自动生效”），插件上线后补发。
- **单节临时任课老师**：ClassIsland 只有“班级学科 → 老师”。换课后某节课的实际老师与学科默认老师不一致时，调课记录带上 `teacherName`，服务端叠加到下发给手机、手表、WebUI 的课表和“我的日程”；叠加按科目 ID 匹配，课位已不是调课的新科目时不叠加。叠加有效期间，临时任课老师按任教老师获得该班访问。ClassIsland 本机仍显示学科默认老师。
- **同一节课的其他申请**：一份申请通过或强制生效后，触及同一节课的其他待审批申请置为已失效，并通知申请人（`swap_invalidated`）。
- **强制换课**：拥有 `ForceScheduleSwap` 时可以带 `force: true` 立即生效，并通知对方老师与班主任。对方可撤回，撤回即撤销调课记录，不依赖教室端当前课表，不会因课位已变化而失败。同一申请人当天不能再强制换走这一节课（`SWAP_FORCE_LOCKED`），其他老师、其他课以及之后各周的同一节课不受影响。
- **班主任撤回**：相关班级的班主任或系统管理员可以撤回已通过或已强制的换课（`POST /api/swap-requests/{id}/revert`），状态变为已撤回并通知申请人与对方老师；列表项 `canRevert` 表示当前用户能否撤回。班主任可以查看本班相关换课的详情。
- **失效**：换课生效后，教室端报告调课冲突（档案下发改了基础课表、该日改为放假、插件过旧等）时，申请置为已失效（`invalidReason` 为原因），同一申请的其余调课一并撤销（不留半个换课），并通知申请人、对方老师与相关班主任（`swap_invalidated`）。
- **状态**：1 待审批、2 已通过、3 已拒绝、4 已撤销、5 已过期（每 10 分钟清扫）、6 已强制、7 已撤回、8 已失效。已通过与已强制的申请带 `delivery`（`pending` / `applied`）。

## 调课记录与托管临时层

调课记录是单日调整的权威来源（见 [ADR 0001](adr/0001-timetable-source-of-truth.md)）：某班某日某节从基础科目改为另一科目，来源可以是换课申请、经 RemoteCI 的直接临时换课或在 ClassIsland 里直接修改临时层。同一课位可叠放多条记录，序号最大的有效记录生效，撤回上层后回落到下层；每条记录带守卫 `fromSubjectId`（该节基础课表应有的科目），同一课位的记录守卫相同。

- **下发**：服务端每次有效调课变化都递增该班的集合版本，向班级插件发送 `schedule_adjustments`：`items[]` 为今天起每个课位最上层的一条 `ScheduleSlotChange {id, date, index, fromSubjectId, toSubjectId}`，`acknowledgedLocalIds[]` 为服务端已经记录的插件本地调课 ID。插件上报能力后、回执版本落后时（限频 30 秒）也会补发。集合是期望状态，可以重复发送；插件忽略版本低于已应用版本的集合。
- **托管临时层**：插件为需要的日期生成临时层，内容为“基础课表 + 服务端调课 + 本地调课”。基础课表依次取管理员给该日预定的普通课表、调休上学日所跟星期那天的课表、宿主按规则匹配的课表，一律忽略临时层。插件建立的临时层原地改写（课表 ID 不变，修订号只在内容变化时变化），基础课表或其时间表变化时重建，丢失（例如“新建并启用”档案、手动删除）时重建，不再需要时删除并恢复被顶替的预定课表。档案下发、ClassIsland 自带换课或升级前遗留的外来临时层与基础课表节数相同时接管（保留其时间表），与基础课表不同的节次转为本地调课；节数不同时该日不托管，服务端调课以 `FOREIGN_OVERLAY` 冲突返回。
- **本地调课**：临时换课命令与在 ClassIsland 里对托管临时层的修改转为 `LocalScheduleAdjustment {id, date, index, fromSubjectId, toSubjectId, origin: direct|classisland, requestedByUserId?, requestedByName?, createdAt}`，持久化在插件本地，随课表上报，服务端按 ID 幂等收编（来源为直接换课或 ClassIsland），之后在 `acknowledgedLocalIds` 中确认。
- **回执**：`schedule_sync` 的 `adjustments` 为 `ScheduleAdjustmentReport {version, items[], local[], unmanagedDates[]}`；`items[]` 为每条服务端调课的 `{id, state: 0 待处理|1 已生效|2 冲突, code?, message?}`。冲突码：`BASE_CHANGED`（基础课表已变化）、`INDEX_OUT_OF_RANGE`、`NO_PLAN`、`HOLIDAY`、`FOREIGN_OVERLAY`、`SUBJECT_MISSING`、`PLUGIN_OUTDATED`、`SAVE_FAILED`。服务端把冲突课位的整叠记录置为失效并通知相关换课申请的老师。托管日期的 `CourseEntry` 带 `baseSubjectId`（与生效科目不同时）与 `adjustmentId`；服务端叠加尚未送达的调课时标记 `adjustmentPending: true`。
- **旧插件**：不声明 `schedule.adjustments` 的插件上线时，该班尚未送达的调课置为失效（`PLUGIN_OUTDATED`）并通知相关老师。
- **升级回填**：服务端升级后启动时，把旧版逐节命令执行、今天及以后仍有效的换课回填为调课记录（同一课位以最早一次的原科目为守卫）；插件接管旧临时层后再回执。

每次提交、通过、拒绝、撤销、强制和撤回都会写入个人通知（`UserNotification`），并同时推送：在线手机和手表走 `user_notify`，已订阅的浏览器走 Web Push（RFC 8291 aes128gcm + RFC 8292 VAPID；VAPID 密钥首次使用时生成并存入 `SystemMetadata`）。AstrBot 等外部客户端轮询 `GET /api/me/notifications`。

## 语音消息

新增命令 `SendVoiceMessage = 9`，要求权限 `SendVoiceMessages = 512` 和能力 `voice-message.send`。新能力仅列入当前版本能力列表，不能加入未声明能力的旧 V3 端默认获得的基础能力。

```json
{
  "command": 9,
  "voiceMessage": {
    "format": "pcm_s16le_16000_mono",
    "audioBase64": "AAA=",
    "forceComplete": true
  }
}
```

可选字段 `forceComplete`（默认 `false`）表示强制播放完成：课表端在整条语音播完前锁定暂停、拖动进度和关闭（包括点击浮窗外部和系统关闭请求），只允许后退重听；播完或音频设备出错后解除锁定。插件停止时仍会立即关闭。旧版插件忽略该字段。WebUI 录音弹窗、手机 App 和手表在发送前提供“强制播放完成”勾选项；WebUI 原始 PCM 上传通过查询参数 `forceComplete=true` 传递。

音频为 16 kHz、16 位有符号小端、单声道 PCM，最多 60 秒（1,920,000 字节），不能为空或包含半个采样；`audioBase64` 使用标准 Base64。服务端和插件校验音频格式与大小，不接收音频 URL 或本地路径。WebSocket 信封接收上限为 16 MiB，覆盖 Base64 中 `+` 被 JSON 转义成 `\u002B` 的最坏情况；解码后仍受上述音频上限约束。

WebUI 通过 `POST /Control?handler=VoiceMessage` 上传 `application/octet-stream` 原始 PCM，携带 Cookie 和 `X-CSRF-TOKEN` 防伪头；服务端在内存中转为同一命令并等待插件回执。HTTP 页面会显示“（当前使用不安全的HTTP连接）”；但浏览器通常只在 HTTPS、localhost 或管理员明确放行明文麦克风的策略下提供 `getUserMedia`。手表通过已认证的云端或局域网 WebSocket 发送。所有接入端覆盖 `requestedBy`，插件使用认证账号的 `displayName` 生成“来自xxx的语音消息”，不受文本通知署名开关影响。

Windows 插件自动播放录音，并显示上述 ClassIsland 通知：强调特效开启、通知音效关闭、朗读关闭（仍遵循宿主全局提醒限制）。底部浮窗上滑出现，拖动标题可移动；顶部显示发送人和总时长，中间使用 ClassIsland 原生 Slider 拖动播放位置，底部三个无底色的 ClassIsland Fluent 图标分开放置，分别用于暂停/继续、后退 5 秒和关闭，关闭图标使用系统危险色，播完后可重新播放。浮窗使用宿主 Fluent 浮层背景、描边、圆角、深浅色主题和 UI 字体；点击浮窗外部也会关闭并停止播放。关闭或插件停止时立即释放音频资源。已有浮窗且当前语音尚未播完（播放中或暂停中）时，新语音进入内存队列并立即回执“已排队”，当前语音播完后自动关闭浮窗并播放下一条；当前浮窗被关闭时也会立即开始下一条。若当前语音已播完只是停留在浮窗上，新语音直接替换它。队列最多 10 条，已满时返回 `BUSY`；插件停止时清空队列。离线不缓存重发。成功回执表示播放器已启动或已排队，不表示整条语音已经播放完毕。音频不写入文件或通知历史，广播事件仅包含发送人提示。

## 远程软件升级

RemoteCI 插件在云端连接建立后通过 `software_inventory` 上报设备名称、ClassIsland 主程序版本和当前已加载插件版本。服务端按插件长期凭据保存最后一份清单，因此设备离线时 WebUI 仍能显示最近一次版本；重新上线后由新清单覆盖。

命令值 10 为 `UpgradePlugins`，命令值 11 为 `UpgradeClassIsland`，命令值 12 为 `RefreshSoftwareInventory`，命令值 20 为 `RestartClassIsland`。前三者都要求账号具有 `ManageUsers` 权限，并分别要求插件声明 `software.upgrade-plugins`、`software.upgrade-classisland`、`software.inventory` 能力；`RestartClassIsland` 仅允许系统管理员使用，不重启 Windows。WebUI 的“批量控制”页按在线插件连接逐台下发，避免同一班级多台设备只收到最早连接的一条命令。

升级命令只表示任务已被设备接受；实际下载、部署和重启由 ClassIsland 官方插件市场或官方 `UpdateService` 在后台完成。插件升级写入宿主的 `.cipx` 缓存并在重启后安装；ClassIsland 升级使用官方文件图校验和部署流程。完成后设备会主动重启并重新连接，服务端以新的 `software_inventory` 更新版本和最近升级状态。失败或已是最新版本时不会重启，状态随清单回传。
## 插件管理、档案分发与集控

命令值 13 为 `InstallPlugins`、14 为 `UninstallPlugins`、15 为 `SetPluginEnabled`、16 为 `SetPluginManagementPolicy`、17 为 `DistributeProfile`、18 为 `UpdateTimeLayout`、19 为 `JoinManagement`。这些命令都要求 `ManageUsers` 权限，并在服务端和插件端再次确认系统管理员身份；能力标识分别为 `plugin.install`、`plugin.uninstall`、`plugin.enable`、`plugin.management-policy`、`profile.distribute`、`schedule.time-layout` 和 `management.join`。

- 插件安装使用 ClassIsland 官方插件市场解析依赖并下载 `.cipx` 缓存，卸载和启停使用宿主公开的 `PluginInfo` 状态接口；RemoteCI 拒绝操作自身，避免远程控制链路被卸载或禁用。
- 命令值 31 为 `InstallPluginPackages`（能力 `plugin.package-install`，同样要求 `ManageUsers` 并仅限系统管理员；与截图一样只能由服务端入口构造，因为服务端要先为目标设备登记插件包下载权限，通用 `POST /api/commands`、手机/手表通道与局域网直连一律拒绝）：插件从服务端文件中转下载一组 `.cipx` 插件包并安装或更新。参数 `pluginPackages` 为 `{mode, packages, force, restartAfter}`：`mode=1`（Install）安装插件，已安装的显式插件包会被覆盖安装、`isDependency=true` 且本机已安装的依赖包跳过；`mode=2`（Upgrade）只更新本机已安装且包版本更高的插件，`force=true` 时不比较版本。每个 `packages[]` 为 `{pluginId, name, version, isDependency, transferId, downloadKey, size, sha256}`，最多 32 个，下载方式与文件中转相同（`GET /api/plugin/files/{transferId}` + `X-RemoteCI-Transfer-Key`，断点续传、SHA-256 校验），下载进度经 `file_transfer_progress` 回报到对应传输。插件核对包内 `manifest.yml` 的 `id` 与 `pluginId` 一致后，把包放入宿主插件包缓存（`PluginPackages/<id>.cipx`，与 ClassIsland“从本地安装插件”相同），全部处理完后按 `restartAfter` 重启 ClassIsland 由宿主安装。命令立即回执，最终结果随 `software_inventory.lastUpdate`（`operation=2`）回传。该命令可以更新 RemoteCI 自身。
- WebUI 的“安装插件”直接列出 ClassIsland 插件市场（服务端读取与宿主相同的 `index.zip`，显示名称、Id、作者、版本、下载量与星标），“升级插件”列出设备上报的可更新插件供勾选（不勾选时升级全部）。两者都可开启“服务端预下载”：服务端从市场下载插件包（按 `Server:PluginMarketMirror` 选择镜像并依次回退，校验 MD5，安装时连同必选依赖），再以 `InstallPluginPackages` 分发；不开启时仍发送 `InstallPlugins` / `UpgradePlugins`，由设备自己访问插件市场。“从 CIPX 安装”先把 `.cipx` 上传到服务端中转（单个不超过 512 MB，服务端校验清单），再以 `mode=1` 分发。
- `DistributeProfile` 只接收 ClassIsland 档案 JSON 和分发范围（时间表、课表、科目），不接收任意文件路径；请求可带 `importProfileName`、`replaceCurrentProfile`（默认 `false`）和 `enableImportedProfile`（默认 `true`）。默认会把选中的内容写入一个新档案并启用该档案；只有明确开启 `replaceCurrentProfile` 才会写入当前档案。旧命令的 `replaceExisting=true` 会先清空整个所选类别，再写入源数据；关闭时按同 Id 更新并保留其他项目。`UpdateTimeLayout` 接收结构化时间点并由插件在 UI 线程写入档案。
- 插件对 `Profile` 的字典属性按属性名运行时读取，以兼容 ClassIsland 2.1.0 的 `ObservableDictionary` 与 2.1.1 起的字典实现；因此升级宿主后无需重新生成档案 JSON。
- `JoinManagement` 由管理员上传 ClassIsland 集控配置文件 `ManagementPreset.json`（即宿主 ManagementSettings 的 JSON）发起。插件解析后按配置文件里的服务器类型（Serverless manifest 模板或 ManagementServer 的 API + gRPC）注册，先校验集控清单核心版本，再写入宿主的 Management 配置并重启；配置文件里的 ID（ClassIdentity）由服务端按目标设备所属班级名自动填充，不需要管理员填写。
- `SetPluginManagementPolicy` 只约束 RemoteCI 后续发起的远程插件安装或卸载。ClassIsland 当前没有公开的宿主级“禁止本地安装/卸载插件”策略 API，因此该策略不阻止用户在 ClassIsland 本地设置页操作。

WebUI 批量控制页把功能参数和目标设备分成两步：先填写参数，再选择班级、分组或具体设备。班级级选择只投递到该班最早接入的在线设备；具体设备选择按插件连接逐台投递。命令下发成功只表示设备端已接受任务，下载、部署、重启或档案拉取的最终结果由后续 `software_inventory`、连接状态或用户再次查看时体现。

## 服务端档案库与档案应用

WebUI 管理区的 `/Profiles` 提供全局档案库和批量编辑；每班菜单的 `/ClassProfiles` 只管理当前班级。时间表修改与档案分发从“控制”和“批量控制”页移入档案页，“加入集控”仍保留在控制页；远程插件管理策略的 WebUI 入口已移除，协议命令只为旧客户端保留。课表页继续展示七日课表、换课和科目教师设置；档案页编辑服务端保存的源档案。设备当前档案不会自动同步，需要时用“从设备收集”读取后作为草稿编辑。

服务端通过 SQLite／EF Core 保存全局模板与每班一份独立工作副本，记录完整 JSON、修订号、来源模板和更新时间。分配模板时保留 ClassIsland 对象 ID 并复制 JSON，模板与班级副本的后续修改相互隔离。上传 JSON 上限为 5 MB，先解析预览档案名称、时间表、课表与科目数量及引用错误，确认后才保存。编辑只覆盖相关节点的字段，保留未知字段和附加配置；无效时间、缺失时间表或科目引用阻止保存和下发。调整上课时段时保留原时段对应课程、新时段留空，删除已有课程的时段需确认。

WebUI 的档案读写通过带 Cookie 与防伪令牌的 Razor Page handlers 完成；`/api/profiles` 下的 REST 接口（列表、读取、保存、复制、删除、预览、`collect` 收集、`apply` 下发）与页面共用同一服务层和权限校验。系统管理员可管理全局及所有班级；班主任必须在目标班级的成员身份为班主任且拥有 `ManageSchedule`，只能管理本班副本。每次上传、保存、导出和下发均重新鉴权，普通老师即使拥有 `ManageSchedule` 也不能管理档案。批量保存先验证所有草稿和预期修订号，在一个事务内提交；任一修订冲突使整批不写入，浏览器保留草稿。保存与下发分开，离线时可保存；下发只使用已经保存并匹配预期修订号的版本。

命令值 25 为 `ApplyProfile`，能力标识 `profile.apply`，仅进入当前能力列表。`profileApply` 载荷为 `ProfileApplyRequest`，字段包含 `profileJson`、`sections`（位掩码：1 时间表、2 课表、4 科目）、`mode`（1 `MergeCurrent`、2 `ReplaceSections`、3 `CreateAndActivate`、4 `TempLayers`）、可选 `importProfileName`、`restartAfter`（默认 `false`）和 `replaceExistingTempLayers`（默认 `false`）。应用方式每次必须明确选择，界面不预选：

- **更新当前档案**：更新所选对象及其必要依赖，保留其他内容。
- **整体替换所选类别**：清空明确列出的类别后写入新数据，操作前必须确认；合并后的档案存在悬空引用时拒绝执行。
- **创建并启用新档案**：使用填写的设备档案名创建并启用，重名返回错误。
- **作为临时层下发**（`TempLayers`，需要能力 `profile.temp-layer`）：只写入载荷中按日期安排的临时层，不改动设备的常规时间表与课表，`sections` 忽略。见下文“临时层”。

`ApplyProfile` 与 `ReadProfile` 为 `serverOnly` 命令，仅由服务端档案管理入口构造并定向发送；手机、手表的通用 WebSocket 命令通道、`POST /api/commands`、广播接口及插件局域网直连均拒绝。服务端校验管理员或本班班主任身份及 `ManageSchedule`，插件再次验证认证发送人的 `ManageSchedule` 权限。目标可选班级、分组或具体设备；班级级选择只发送到最早接入的在线设备，班主任目标固定本班。各班不同档案按目标设备所属班级组装，离线、缺少 `profile.apply` 或设备执行失败逐台返回结果，不排队；失败后可手动重试。旧插件仍可用于服务端档案编辑，但下发必须升级到支持 `profile.apply` 的插件，不能用旧命令模拟新应用方式。

插件在 UI 线程验证、应用并保存档案，失败恢复原状态，成功后立即重新同步七日课表。创建并启用新档案时插件按宿主实际签名调用 `SettingsService.SaveSettings(string)` 保存档案选择（ClassIsland 2.1 没有无参重载）。旧命令 17 `DistributeProfile`、18 `UpdateTimeLayout` 的编号、载荷、管理员权限和既有语义继续保留，档案页面使用命令 25。

配置备份 schema 升为 5，包含全局档案库与班级工作副本；恢复旧备份时缺少档案数据按空库处理。schema 7 起再包含集控运维设置（操作记录保留天数、离线告警、每日档案差异检查）与自动化规则；恢复 schema 6 及更早的配置包时保留当前的这些设置与规则。操作记录、在线记录、课时台账与档案版本属于运行数据，不进入配置包，完整数据库备份会保留它们。设备本地修改不会自动回写服务端；首次使用档案管理可从设备收集、上传或新建档案。

### 收集设备档案

命令值 26 为 `ReadProfile`，能力标识 `profile.read`，需要 `ManageSchedule`，无载荷。插件在 UI 线程把当前内存档案序列化为 JSON，放在 `CommandResult.data` 中返回；超过 5 MB 返回失败。服务端逐班校验管理权限后，向每班在线插件并发发送（20 秒超时），离线、缺少 `profile.read`、无权管理（不回显该班名称）或执行失败逐班报告；所选班级一个都不能管理时整体返回 403。成功结果按宿主 `RefreshClassesList` 规则把课程数补齐或截断为上课时段数，并清除悬空的临时课表指针与预定课表，然后连同剩余校验错误返回给调用方。收集结果**只作草稿**：WebUI 载入班级草稿（保留服务端档案 ID 与修订号，保存时照常校验修订号）或新的全局模板草稿，保存前不写入档案库。入口为页面 handler `Collect` 和 `POST /api/profiles/collect`（请求体 `{ "classIds": [...] }`，班级页固定当前班级）。

### 临时层

ClassIsland 的临时层是 `ClassPlans` 中 `IsOverlay=true` 的课表：`OverlaySourceId` 指向来源课表，`OverlaySetupTime` 为生效日期，`OrderedSchedules[日期].ClassPlanId` 指向它；宿主每天只允许一个临时层。宿主按日期取课表时，只有档案开关 `IsOverlayClassPlanEnabled` 为 true 才会使用日期条目指向的临时层（不论哪天）；`OverlayClassPlanId` 只是当天临时层的指针，宿主每次加载课表都会重算。可选的临时层时间表是 `IsOverlay=true`、`OverlaySourceId` 指向原时间表的副本。宿主清理时删除早于今天的日期条目、不再被日期引用的临时层课表和不再被使用的临时层时间表。

- 常规类别不包含临时层：`MergeCurrent`、`ReplaceSections`、`CreateAndActivate` 在“全部对象”时跳过临时层，显式选择临时层对象返回错误。设备原有临时层不属于所选类别，更新或整体替换后按宿主规则修复：课程数随时间表补齐或截断，缺失的科目改为空课，缺失的课表群改为默认课表群；只有所用时间表已不存在时才连同日期一并移除。
- 服务端 `ProfileDispatchRequest` 在 `mode=4` 时不要求 `sections`，可带 `tempLayerIds`（省略表示全部；多份档案批量下发时忽略）和 `replaceExistingTempLayers`。载荷只包含所选临时层、它们的日期条目及依赖。某班档案没有临时层时只让该班失败。
- 插件逐个写入：早于设备今天的跳过并计数，全部过期时失败；设备同日已有临时层或预定课表时，未设置 `replaceExistingTempLayers` 则整台设备拒绝并列出日期，设置后删除该日条目及不再被引用的旧临时层。科目和课表群只补缺，不覆盖设备现有对象；时间表在设备有同 ID 且时段一致时直接引用，否则以新 ID 写入临时层时间表副本，不改动设备的常规时间表。临时层课表以新 ID 写入，来源课表在设备上不存在时 `OverlaySourceId` 置空，并按来源课表重算 `IsChangedClass`。写入后总是打开 `IsOverlayClassPlanEnabled`（与宿主 `CreateTempClassPlan` 一致，否则未来日期的临时层不会生效），日期为今天时同时设置 `OverlayClassPlanId`。回执消息为“已下发 N 个临时层（，跳过 M 个已过期的临时层）”。
- 校验同时拒绝 `OrderedSchedules` 中同一天出现多个条目（例如带与不带时区后缀的同一日期）。
- 设备声明了 `schedule.adjustments` 时，插件会接管下发的临时层（与基础课表节数相同的才接管，与基础课表不同的节次转为本地调课）；覆盖已有调课的日期时，原有托管临时层被替换，其中的调课按守卫重新应用或回报冲突。
- WebUI 按各班最近状态快照上报的时区偏移计算教室端“今天”（无快照时用服务端本地时区），据此标注临时层“今天生效/已过期”；模板没有班级时用浏览器日期。设备写入时仍以设备自身日期跳过过期项。

### 档案版本历史与设备差异

每次保存（含复制、恢复）都会在同一事务内写入一个版本快照（`StoredProfileVersions`：修订号、名称、完整 JSON、操作人、时间、来源），每份档案保留最近 50 个。升级前已有的档案在第一次修改前自动补一个“历史”基线版本，因此第一次修改也能对比和回滚。

差异按语义比较（`ProfileDocument.Diff`，位于共享库）：科目的增删、改名与任课教师，时间表时段的增删与起止时间，课表每节课的科目、星期与关联时间表，以及按日期的临时层。对象先按 ID 配对，ID 对不上时按名称配对（设备档案可能来自别处导入）。单次最多列出 200 处，其余合并为一条。

- `GET /api/profiles/{id}/versions`：版本列表，每项含 `revision`、`savedByName`、`savedAt`、`originLabel`、`changeCount`（相比上一版本）与 `isCurrent`。
- `GET /api/profiles/{id}/versions/{revision}/diff?against=previous|current`：该版本与上一版本或当前内容的差异 `{revision, againstRevision, againstLabel, changes:[{category, text}]}`。
- `POST /api/profiles/{id}/versions/{revision}/restore`：请求体 `{ "revision": 当前修订号 }`，把历史版本另存为新修订；修订号不一致返回 409，不覆盖别人刚保存的内容。设备档案不会随之改变，需要时重新下发。

设备差异检查复用 `ReadProfile`：读取教室电脑当前档案，与服务端班级档案比较时间表、课表和科目（不含临时层），每班保存最新一份结果；服务端档案在检查后又保存过时，旧结果不再返回。差异只记录不处理，以设备为准用“从设备收集”，以服务端为准重新下发。

- `POST /api/profiles/drift`：请求体 `{ "classIds": [...] }`，逐班返回 `{classId, className, success, message, changeCount}`。
- `GET /api/profiles/drift`：调用者可管理班级的最新结果 `[{classId, className, checkedAt, changeCount, changes}]`。
- 每日自动检查：系统配置“集控运维”可开关并设定时刻（默认 12:30，服务端本地时间），以第一位启用的系统管理员身份读取全部在线且有班级档案的设备；审计来源记为“档案差异检查”。

## 远程终端与文件分发

命令值 22 为 `ExecuteTerminalCommand`，23 为 `SendFile`。远程终端要求 `ManageUsers` 权限，并在服务端和插件端再次确认系统管理员身份；文件分发要求 `SendFiles` 权限（值 16384，系统管理员固定拥有，其他账号由管理员在账号权限或角色配置中开启，按目标班级逐个复核）。能力标识分别为 `terminal.execute`、`file.distribute`（内联方式）与 `file.transfer`（中转方式），只进入当前版本能力列表。

- 远程终端在设备上以当前登录用户执行单条命令，等价于 `cmd /d /c`：每次执行相互独立，进程之间不保留 shell 状态。载荷 `terminalCommand` 包含 `command`（≤4000 字符）、可选 `workingDirectory`（≤260 字符，目录不存在时返回 `INVALID_REQUEST`）和可选 `timeoutSeconds`（1-10，默认 10）。插件合并标准输出与标准错误（中间以 `--- 标准错误 ---` 分隔），放入 `command_result.data` 返回，总上限 64K 字符，超出截断并注明；超时的进程会被强制结束并返回 `COMMAND_TIMEOUT`，`data` 携带已捕获的部分输出。命令正常结束时回执 `success=true`，退出代码写入 `message`，非零退出代码不视为失败。

### 文件分发（中转方式，file.transfer）

文件先上传到服务端，再由设备分段下载，单个文件上限由服务端配置 `Server:FileTransferMaxBytes` 决定（默认 2 GiB，协议上限 4 GiB），中转目录总量上限 `Server:FileTransferQuotaBytes`（默认 20 GiB），文件保留 `Server:FileTransferRetention`（默认 24 小时）后删除。反向代理需同步放开请求体大小（如 nginx `client_max_body_size`）。

1. `POST /api/files?name=<文件名>`：请求体就是文件内容（`application/octet-stream`，也可用 `X-File-Name` 头传 URL 编码的文件名）。需要至少在一个班级拥有 `SendFiles`。服务端边写盘边计算 SHA-256，返回 201 和 `FileTransferView {id, fileName, size, sha256, ownerName, createdAt, expiresAt, deliveries:[]}`；超过上限返回 413。
2. `POST /api/files/{id}/send`：请求体 `{classIds?, classNames?, nextClass?, timeZoneOffsetMinutes?, targetFolder: 1 桌面|2 下载|3 文档, overwrite}`。`classNames` 完全匹配优先、否则要求片段唯一匹配；`nextClass: true` 加入当前账号“我的日程”中接下来要上课的班（没有后续课程时取正在上的课）。每个目标班级都必须拥有 `SendFiles`，否则 403。服务端为每台设备（班级的主插件）登记接收记录后并行下发 `SendFile`，返回同一个 `FileTransferView`，`deliveries` 为逐台状态。
3. `GET /api/files/{id}`：上传者本人或系统管理员查看逐台进度 `deliveries:[{classId, targetName, state, receivedBytes, totalBytes, percent, message, savedPath, updatedAt}]`，`state` 为 0 等待、1 接收中、2 已送达、3 失败。`DELETE /api/files/{id}` 提前删除。

命令载荷 `fileDistribution` 为 `{fileName, transferId, downloadKey, size, sha256, targetFolder, overwrite}`，不含文件内容。插件立即回执“开始接收”，随后在后台用插件长期凭据（`Authorization: Bearer`）加 `X-RemoteCI-Transfer-Key: <downloadKey>` 请求 `GET /api/plugin/files/{transferId}`：服务端只对该传输登记过的插件凭据放行，支持 `Range` 断点续传。插件先写入暂存目录，网络中断时按已下载长度续传（最多 8 次，单次读取 60 秒无数据视为中断），完成后校验 SHA-256，再移动到目标文件夹；同时最多 2 个文件并行下载。

插件以 `file_transfer_progress` 消息上报进度 `FileTransferProgress {transferId, state, receivedBytes, totalBytes, message?, savedPath?}`：下载中每秒最多一次，结束时必报一次最终状态（云端暂时断线时约 2 分钟内重发）。服务端只接受该插件凭据登记过的传输，最终状态不会被迟到的进度覆盖。

不支持 `file.transfer` 的旧插件：系统管理员发送且文件不超过 10 MB 时，服务端自动改用下文的内联方式；否则该设备记为失败（“插件版本过旧”）。通用命令接口（`POST /api/commands`、手机/手表 WebSocket）拒绝携带 `transferId` 的 `SendFile`。

### 文件分发（内联方式，file.distribute）

以内联 Base64 经 WebSocket 传输一个文件，解码后上限 10 MB（复用语音消息的 16 MiB 信封上限），不接受 URL 或服务器本地路径，仅系统管理员可用。载荷 `fileDistribution` 包含 `fileName`、`contentBase64`、`targetFolder`（1=桌面、2=下载、3=文档）和 `overwrite`；成功回执的 `data` 是设备上的完整保存路径。

两种方式共用同一校验：文件名剥掉目录部分并过滤非法字符，拦截 Windows 保留设备名，长度上限 200；保存目录只允许 `targetFolder` 枚举列出的用户文件夹（下载目录按系统已知文件夹解析），不接收任意路径。`overwrite` 为 `false`（默认）时同名文件自动追加序号，绝不覆盖。

WebUI 在“批量控制”与班级“控制”页的远程维护区开放这两个功能，均需先勾选高风险确认。终端结果逐设备展示输出；文件分发先显示上传进度，再每秒刷新逐台下载进度条与最终保存路径。

## 屏幕截图（screen.capture）

命令值 27 为 `CaptureScreen`，要求 `CaptureScreen` 权限（值 32768）与 `screen.capture` 能力。它和档案命令一样只能由服务端截图入口构造：通用 `POST /api/commands`、手机/手表 WebSocket 与插件局域网直连一律拒绝，因为服务端必须先登记上传凭据。

1. `POST /api/classes/{classId}/screenshot`：请求体可省略；系统管理员可传 `{ "credentialId": "<插件凭据 Id>" }` 指定同班某台设备（其他账号传该字段返回 403），缺省时与普通命令一样取该班最早接入的教室电脑。服务端为目标插件凭据登记一条待上传记录（5 分钟内有效），下发命令并最多等待 45 秒。成功返回 201 和 `ScreenshotView {id, classId, deviceName, requestedBy, width, height, size, capturedAt, expiresAt, url}`；插件离线 503、插件不支持 `screen.capture` 409、超时 504、截图或上传失败 422（响应体为 `CommandResult`）。
2. `GET /api/classes/{classId}/screenshots`：该班保留期内的截图，新到旧。
3. `GET /api/screenshots/{id}`：返回 `image/png`。以上接口都要求对截图所属班级拥有 `CaptureScreen`（系统管理员始终拥有），无权或已过期统一返回 404。

命令载荷 `screenshot` 为 `{id, uploadKey, notify, maxWidth}`：`id` 为 32 位小写十六进制，`maxWidth` 默认 1920（320-7680），`notify` 取自系统设置“截图后在教室端提示”（默认关闭）。插件截取全部显示器拼成的虚拟桌面（按物理像素，宽度超出 `maxWidth` 时等比缩小），编码为 PNG，再用插件长期凭据（`Authorization: Bearer`）加 `X-RemoteCI-Upload-Key: <uploadKey>` 请求 `POST /api/plugin/screenshots/{id}` 上传，请求体为 PNG 原始字节。服务端只接受登记的插件凭据、校验 PNG 签名、每条记录只接受一次，单张上限 `Server:ScreenshotMaxBytes`（默认与协议上限均为 20 MiB）。上传成功后插件才回执成功，`command_result.data` 为 `{width, height, size}`；`notify` 为 `true` 时随后在 ClassIsland 显示不带音效和朗读的“远程截图”提示，提示不会出现在截图里。插件回执成功但服务端没有收到截图时按失败处理。

截图保存在数据目录的 `screenshots` 下，保留 `Server:ScreenshotRetention`（默认 24 小时），每个班级最多保留 30 张；备份不包含截图。每次截图都会写入命令操作记录。WebUI 在班级“控制”页“当前班级”区提供“屏幕截图”，系统管理员还可在“设备状态”页对每台在线设备单独截图；Android 应用在“课堂控制 → 屏幕截图”中提供同样功能。

## 扩展分组与扩展设置

插件在云端连接建立、分组注册/注销和设置值变化时发送 `extension_groups_sync`，服务端按插件凭据回填 `classId` 并缓存最新快照（以设备上报为准）。课表、扩展功能与扩展分组（含设置当前值）同时持久化到 `ClassStateCaches` 表，服务端重启后、插件重连前仍可查看课表与日程、发起换课申请、打开扩展设置页并排队修改；插件重新连接后会立即重新拉取课表并覆盖缓存；包含非法分组 Id 的整条同步会被忽略。扩展功能通过 `groupId` 关联到分组，WebUI 控制页与批量控制页按分组展示，未关联或分组未注册的扩展归入“其他扩展”。

命令值 24 为 `ApplyExtensionSettings`，能力标识 `extensions.settings`（只进入当前版本能力列表）。载荷 `extensionSettings` 为 `{groupId, values}`，`values` 只包含本次要修改的字段（部分更新），未出现的字段保持设备当前值；字段必须已在分组中声明，必填字段不能清空。权限口径：

- 服务端只在 WebUI 扩展插件页与 `PUT /api/classes/{classId}/extension-groups/{groupId}/settings` 中接受该操作。班级区的 `/ClassExtensions` 只修改当前班级、不提供范围选择；管理区的 `/ExtensionSettings`（插件批量设置，仅系统管理员）按所选班级、分组或具体设备下发，与当前选中的班级无关。系统管理员可修改任意班级；班主任只能修改系统管理员逐个开放给班级自行管理的插件（扩展插件详情页“班级自治”或 `PUT /api/extension-groups/{groupId}/class-admin-access`，默认都不开放），且在本班拥有 `RunExtensions` 权限。手表、手机的通用命令通道和 `POST /api/commands` 一律拒绝。
- 插件端要求 `requestedBy` 携带 `RunExtensions` 权限位作为纵深防御；局域网直连无法复核班级自治策略，因此直接返回 `FORBIDDEN`。
- 下发时该班插件离线：服务端把字段合并保存为待补发并返回 `QUEUED`（REST 为 202）；插件下次发送 `extension_groups_sync` 时，服务端在后台复核下发者权限后定向补发，插件返回明确结果后删除记录，连接再次中断时保留。
- 写入成功或失败后，插件都会重新发送 `extension_groups_sync`，让 WebUI 预填值反映设备真实状态。批量下发时服务端向所选班级的每一台在线设备逐台发送并逐台返回结果。
## 老师、班主任角色与我的日程

服务端内置“老师”角色（`AccountRoleKind.Teacher = 5`，固定 ID `55555555-5555-5555-5555-555555555555`，启动时幂等种子并随 `roleId`/`roleName` 下发）。`UserProfile` 新增可选 `roleKind`（整数角色种类），客户端据此识别内置老师角色，不受角色改名影响；旧服务端不下发该字段。

内置“班主任”角色（`AccountRoleKind.ClassAdministrator = 4`，固定 ID `44444444-4444-4444-4444-444444444444`，原名“班管理员”）保留原有的本班管理能力，同时和老师一样拥有个人“我的日程”：全局角色为班主任的账号也按显示名匹配课表科目教师名，可使用 WebUI 侧栏“我的日程”和下方两个日程端点。按姓名获得的“任教班级访问与任教权限”仍只属于老师角色；班主任对班级的访问仍来自班级成员关系。内置角色名由服务端固定，角色配置中只能调整默认权限，不能改名；班主任与老师一样不能自行修改用户名，用户名只能由系统管理员修改。批量导入仍接受旧角色名“班管理员”；旧版客户端在没有 `roleKind` 时，“班主任”和“班管理员”两个角色名都识别为本班班主任。

老师的显示名（DisplayName）即“姓名”：显示名与班级课表中的科目教师名（ClassIsland 档案 `Subject.TeacherName`，随 `schedule_sync` 下发）一致时，该老师账号绑定到对应课程。匹配规则为去首尾空白后完全相等，或教师字段按分隔符（`/`、`、`、`,`、`;`、`|`、`&`、`+`、`\`，半角与全角均可，空格不算）拆分后任一姓名完全相等；因此多教师科目写成 `张三/李四` 时，两位老师都会绑定到这门课。绑定完全动态计算，不落库：班级插件从未推送过课表时绑定（任教班级访问权）暂不可见，收到课表后自动生效（插件离线后沿用最后一次同步的课表）；这期间“我的日程”和下一节课按该班当前服务端档案推算，相应条目带 `source: "profile"`，不授予班级访问权，且该班匹配教师集合变化时会触发 `account_sync` 重新推送授权镜像。

老师在任教班级的默认权限为其全局老师角色默认权限：`ViewCurrentCourse | SendNotifications | SendVoiceMessages | ApiAccess`，因此老师默认可以创建 API Key 读取自己的日程。`ClassSummary` 新增可选 `roleKind`（该用户在本班的角色种类），客户端据此判断是否为本班班主任，旧服务端不下发时退回角色名。用户名由系统管理员维护，因为它是老师获取日程信息时匹配课表科目教师名的依据。非任教班级的访问与普通用户一致：服务端不再有默认班级，新账号只加入创建时指定的班级。绑定按姓名匹配，同名教师会绑定到同名课程，建议使用完整姓名；管理员可在 WebUI 课表页“科目教师”区显式分配（见下）以统一写法。

`schedule_sync` 载荷中的 `CourseEntry` 与 `SubjectEntry` 新增可选 `teacher` 字段（教师名，未设置时省略），旧版服务端与插件不下发、所有端可安全忽略。

- `GET /api/me/schedule`：返回“我的日程”`MyScheduleResponse { fromDate, generatedAt, days[] }`。老师或班主任账号按姓名绑定的跨班级课程，与本账号的自定义课程按日期合并；其他账号只返回自己的自定义课程。每天 `items[]` 含 `classId`、`className`、`courses[]`。可选查询参数 `timeZoneOffsetMinutes`（调用方本机的 UTC 偏移分钟数，-840～840，超出范围返回 400）只在还没有任何班级上报过状态快照时，用于确定按档案推算课表的“今天”；自定义课程按保存的时区偏移展开为每一次课的绝对时间。
- `GET /api/me/schedule/next`：返回 `MyNextCourseResponse { at, current?, next? }`，分别是指定时刻正在上的课与接下来的第一节课；每项含 `date`、`classId`、`className`、`course`、`startsAt`、`endsAt`。可选查询参数 `at`（ISO 8601）指定计算时刻，省略时取服务端当前时间。课程钟点是教室电脑本地时间，服务端依次按以下时区偏移换算为绝对时间：该班最近状态快照的 `timeZoneOffsetMinutes`；该班没有快照时（例如按档案推算课表、从未连接插件的班级）取任一班级最近上报的偏移；还没有任何班级快照时取可选查询参数 `timeZoneOffsetMinutes`（调用方本机的 UTC 偏移分钟数，-840～840，超出范围返回 400，手机 App 自动携带）；都没有时才退回服务端本地时区（Docker 容器默认是 UTC）。同时纳入本账号自定义课程（不限于班级课表七日窗口）；正在上的一次之外，同一课程的下一次（例如下周同一时间）也参与比较。没有当前或后续课程时对应字段省略。两个日程端点都接受 API Key。
- `POST /api/me/display-name`：系统管理员修改显示名（即老师姓名），载荷 `{displayName}`（1-40 字），成功返回 204 并触发授权镜像同步；普通账号即使保留旧的 `ChangeDisplayName` 权限也会被拒绝。
- 命令值 21 为 `SetSubjectTeacher`，要求 `ManageSchedule` 权限和能力 `schedule.subject-teacher`。载荷 `subjectTeacher` 包含 `subjectId` 与 `teacherName`（≤100 字，空表示清除）；插件在 UI 线程写入档案 `Subject.TeacherName` 并保存，保存失败回滚并返回 `SAVE_FAILED`，成功后立即重推课表。WebUI 课表页“科目教师”区由此为班级科目分配授课教师。

手机端老师登录后课表页默认展示“我的日程”（可切换“本班课表”）；班主任课表页默认展示“本班课表”，也可切换到“我的日程”，列表按天分组显示班级、科目、教师与节次时间。


### 私人自定义课程

所有已认证账号均可使用以下端点（Bearer 设备令牌或用户 API Key），只访问凭据所属账号，不接受 userId 或 classId：

- `GET /api/me/schedule/courses`：列出本人的自定义课程（每周课程按周一到周日、开始时刻排序，单次课程在后）。
- `POST /api/me/schedule/courses`：创建一节课程。
- `PUT /api/me/schedule/courses/{id}`：完整替换本人的课程；修改前先查询当前字段。
- `DELETE /api/me/schedule/courses/{id}`：删除本人的课程，返回 204。操作他人的编号与不存在的编号均返回 404。

自定义课程按“每周星期几 + 当天起止时刻 + 周次”重复，周次规则与档案课表相同，单双周与多周轮换按上文“学期”的教学周计算：

```json
{"subject":"自习","location":"图书馆","weekDay":3,"startTime":"14:00","endTime":"15:00","weekCountDiv":1,"weekCountDivTotal":2,"timeZoneOffsetMinutes":480,"reminderMinutes":20,"notifyAtStart":true}
```

- `weekDay`：0=周日，1=周一 … 6=周六。
- `startTime` / `endTime`：`HH:mm`，结束晚于开始且在同一天内，按 `timeZoneOffsetMinutes`（-840～840，省略时为服务端时区）解释；修改课程时客户端应保留原偏移。
- `weekCountDiv`：0 为每周；否则为 `weekCountDivTotal`（2–4）周一轮中的第几周，例如 1/2 单周、2/2 双周、3/3 三周轮换第 3 周。管理员未设置学期开始日期时只能选每周，否则返回 400；设置后又被清除时，轮换课程暂不出现也不提醒。
- 未提供 `weekDay` 而提供含偏移的 `startsAt` / `endsAt` 时，创建只在该日出现的单次课程（兼容旧客户端与“明天下午加一节课”这类单次安排），须在当天内结束。

课程名称 1–100 字、地点最多 200 字。`reminderMinutes` 为 1–10080 分钟，null 或省略表示关闭课前提醒；`notifyAtStart` 默认 true。响应在上述字段外包含 `id`、`summary`（如“每周三 14:00–15:00 · 单周”）、单次课程的 `date`，以及下一次（或正在进行的一次）课的绝对时间 `startsAt` / `endsAt`（单次课程过期后仍为原日期）。数据库按账号持久保存，删除账号时级联清除；升级前创建的课程保留为单次课程。

合并后的个人日程以 `items[].source: "personal"` 标识自定义课程，`classId` 为全零 GUID（不代表班级、不授予班级访问权限），`className` 为地点或“自定义课程”，`timeZoneOffsetMinutes` 为课程保存时的偏移。`courses[]` 包含稳定的 `personalCourseId`、该次课的 `startsAt`、`endsAt`、`reminderMinutes`、`notifyAtStart`；同一课程在窗口内每周各出现一次，应以 `personalCourseId` + 日期识别某一次课，不应以节次索引识别。`GET /api/me/schedule` 展开今天起 7 天内的每一次课；`/next` 向后查找到最长轮换周期。课程不写入班级课表，也不受班级节假日停课影响。

服务端每 15 秒检查一次课前和上课提醒，分别生成 `personal_course_upcoming` 与 `personal_course_start` 个人通知，沿 `user_notify`、浏览器 Web Push 和 `/api/me/notifications` 分发。每周的每一次课分别提醒；上课提醒在开始后的 5 分钟内可补发，过期不补发。发送标记（那次课的开始时刻）与通知一同落库，重启后不会重复生成。客户端勿再对自定义课程独立生成同类提醒；手机的常驻课堂状态仍可显示课程，但保持静默。

WebUI 和手机日程、手表自定义课程列表每 30 秒重新读取，WebUI 有未保存编辑时保留表单。AstrBot 通过个人通知通道转发提醒，专用 `remoteci_personal_courses` / `remoteci_save_personal_course` / `remoteci_delete_personal_course` 工具仅允许私聊；个人日程与个人通知也不可通过通用 API 工具向群聊输出。

## 集控运维（服务端功能）

以下功能全部在服务端实现，只新增 REST 接口和个人通知种类，不改 WebSocket 协议、不新增插件能力或权限位，旧客户端不受影响。可见范围统一为：系统管理员看全部；班主任（班内角色为“班主任”）只看自己担任班主任的班级；其他账号返回 403。

### 操作记录（命令审计）

所有经服务端下发到教室端插件的命令（含手机/手表 WebSocket 命令、REST、WebUI、批量控制、换课执行、档案下发与收集、自动化）都会记录：时间、操作人（`requestedBy` 的显示名）、入口（`source`：`WebUi`、`Mobile`、`Watch`、`ApiKey`、`DeviceRest`、`Automation`、`SwapWorkflow`、`DriftCheck`、`System`）、入口详情（API Key 名称、设备会话名称、自动化规则名）、班级、设备、命令与结果。摘要只保留必要信息：通知标题与正文开头、电源/音量动作、扩展 Id 与参数、终端命令前 200 字、文件名与大小；不保存语音、文件内容或档案 JSON。因离线或能力不足未执行的命令同样记录失败结果；在服务端鉴权前就被拒绝的请求不记录。记录写入在后台批量完成，不阻塞命令下发；默认保留 180 天（系统配置 7–3650 天）。

- `GET /api/audit?from=&to=&classId=&userId=&command=&success=&before=&limit=`：按时间倒序分页，返回 `{items:[{id, createdAt, completedAt, actorUserId, actorName, source, sourceLabel, sourceDetail, classId, className, deviceName, command, commandLabel, summary, success, resultCode, resultMessage}], nextBefore}`；下一页把 `nextBefore` 作为 `before` 传回。`success=false` 包含失败与仍在等待回执的记录。
- `GET /api/audit/export`：同样的筛选条件导出 Excel（最多 10000 行）。

### 设备状态与离线告警

- `GET /api/devices/health`：已分配班级的教室电脑列表 `[{credentialId, classId, className, groupNames, deviceName, online, lastSeenAt, offlineSince, pluginVersion, classIslandVersion, outdated, currentCourse, volumePercent, muted, profileDriftCount, timeline:[{start, end}]}]`。`outdated` 表示插件版本低于服务端版本；`timeline` 是服务端本地今天 0 点至今的在线时段；`profileDriftCount` 为最近一次设备差异检查的差异数。
- 插件上下线写入在线记录（保留 30 天）。每分钟检查一次：设备在班级“在校时段”（当天第一节课前 30 分钟至最后一节下课，按教室端时区；放假或没有课时不算）连续离线超过阈值（默认 10 分钟，5–120）时，向系统管理员和该班班主任发送个人通知 `device_offline`；告警过的设备重新上线后发送 `device_online`。每次离线只告警一次，状态落库，服务端重启后不重复也不漏发。可在系统配置关闭告警、只通知其中一类人，或取消“只在在校时段告警”。

### 自动化规则

系统管理员可创建规则，按以下触发对所选班级/分组执行 1–5 个动作：

- 触发：`kind=1` 定时（`weekdays` 1=周一…7=周日、`time` HH:mm，按各班教室端本地时间；`schoolDaysOnly` 为真时放假日跳过、调休上学日照常）；`kind=2` 课表事件（`event` 1 上课、2 下课、3 放学，可选 `subject` 只匹配某科目，`delayMinutes` 0–180）；`kind=3` 教室电脑上线（`delaySeconds` 0–600）。可选生效日期 `validFrom`/`validTo`。
- 动作：`kind=1` 发送通知（`notification` 同 `NotificationRequest`，署名开关仍按全局设置）、2 清除提醒、3 主界面显隐（`mainMenuVisible`）、4 音量（`volume`）、5 电源（`powerAction`）、6 运行扩展（`extensionId`、`extensionArgs`）、7 下发该班服务端档案的临时层（`replaceExistingTempLayers`）。不开放语音、终端、文件、升级与插件安装。
- 执行：规则以最后保存它的系统管理员身份执行（该账号停用或不再是系统管理员时执行失败并提示重新保存）；每班每次触发只执行一次（去重键）；定时触发错过 5 分钟以上、延迟任务积压 30 分钟以上记为“已错过”，不补跑。任一班级失败时向规则保存人发送个人通知 `automation_failed`。执行结果保留 90 天，审计来源为 `Automation`、入口详情为规则名。
- `GET/POST /api/automation/rules`、`GET/PUT/DELETE /api/automation/rules/{id}`：规则对象 `{id, name, enabled, trigger, actions, target:{classIds, groupIds}, validFrom, validTo, createdByName, updatedAt, lastRunAt, lastSuccess, lastResult, description}`；`PUT` 为整体保存。
- `POST /api/automation/rules/{id}/run`：立即对全部目标班级执行一次（忽略触发条件与生效日期），返回各班执行记录。
- `GET /api/automation/runs?ruleId=&limit=`：执行记录 `[{id, ruleId, ruleName, classId, className, trigger, dueAt, finishedAt, status, statusLabel, message}]`，`status` 为 `Pending`、`Running`、`Succeeded`、`Failed`、`Missed`。

### 课时台账与统计

服务端每 5 分钟把已经结束的课写入课时台账：班级、日期、节次、科目、实际老师（已叠加换课产生的单节临时任课老师）、默认老师、是否代课、来源（教室端或按档案推算）。一节课结束后不再修改；服务端停机期间结束的课无法补录。统计时教师字段按“我的日程”的分隔规则拆分，多位老师合上的课各计一节：`total` 实际课时（含代课）、`substitute` 其中代别人上的、`substituted` 本该自己上而被代的。默认不统计按档案推算的课。

- `GET /api/lesson-stats?from=&to=&classId=&teacher=&includeProfile=`：`{from, to, includesProfile, lessonCount, teachers:[{teacher, total, substitute, substituted, classes:[{classId, className, total}]}]}`；日期为教室端本地日期，省略时为本月 1 日至今天，区间最长 400 天。
- `GET /api/lesson-stats/export`：导出 Excel（汇总与明细两个工作表）。
- `GET /api/me/lesson-stats?from=&to=`：按本人显示名在全部班级统计，任何已登录账号可用。

### 一次性确认链接（聊天卡片按钮）

- `POST /api/me/action-links`：请求体 `{ "action": "swap.approve" | "swap.reject" | "swap.revoke", "targetId": 换课申请 Id }`，返回 `{url, path, expiresAt}`。签发时校验调用者当前确实能执行该动作（审批人才能同意/拒绝，被强制换课的对方才能撤回），否则返回 400。
- 链接 `/Act/{token}` 匿名访问、24 小时有效、只能成功使用一次；数据库只保存令牌 SHA-256 摘要，账号停用或安全戳变化后失效。打开链接（GET）只显示申请详情和确认按钮，聊天软件预览链接不会执行；确认提交（POST）后经换课服务执行，通知与审计与其他入口一致。执行失败（例如教室电脑离线）时链接恢复可用。页面返回 `Cache-Control: no-store`、`Referrer-Policy: no-referrer` 与 `X-Robots-Tag: noindex`。

### 电子班牌

系统管理员或本班班主任在 WebUI“电子班牌”页生成私密链接 `/Board/{token}`（每班最多 10 个，令牌只在生成时显示一次，可撤销）。页面匿名、全屏显示班名、教室端本地时钟、当前课程及进度、下一节和今日课表，每 15 秒刷新 `?handler=Content`，可选择不显示任课老师。撤销后返回 404；配置包恢复会删除班级及其班牌链接，需要重新生成。

### 个人通知新增字段与种类

`UserNotificationView` 新增可选字段 `classId`（相关班级）与 `link`（点击打开的站内相对路径，如 `/Devices`、`/Automation`），旧客户端忽略。新增种类：`device_offline`、`device_online`（浏览器推送 1 小时后过期）、`automation_failed`（12 小时）与 `app_guard`（应用守护自动恢复或恢复失败，见“应用守护”）。AstrBot 等外部客户端照常轮询 `GET /api/me/notifications`。

## REST API

主要端点：

- `POST /api/plugin/pair`
- `POST /api/auth/login`、`/api/auth/refresh`、`/api/auth/logout`
- `GET /api/setup`、`POST /api/setup/system-admin`：首次部署初始化，无需登录。还没有任何账号时 `POST` 创建系统管理员（`isSystemOwner`）并返回登录令牌，之后返回 403；系统管理员只能由本人维护。服务端不再自动创建默认班级，`/api/setup` 的 `needsFirstClass` 提示还需新建第一个班级。
- `POST /api/auth/web-qr/scan`、`POST /api/auth/web-qr/confirm`：网页扫码登录。WebUI 登录页生成 `remoteci://weblogin?server=…&code=…` 二维码，浏览器自己持有轮询令牌并每 2 秒查询状态；已登录的手机 App 用设备会话扫码（返回浏览器系统/类型与 IP 供确认）并确认后，只有持有轮询令牌的浏览器能换取网页会话。挑战只保存在内存中 3 分钟、只能兑换一次，确认必须由扫码的同一账号完成，API Key 返回 403。
- `POST /api/auth/web-ticket`：客户端一键打开 WebUI。需设备会话 Bearer 令牌（API Key 返回 403），返回 `{ticket, path, expiresAt}`；用浏览器打开 `服务器地址 + path` 即以该账号登录 WebUI，可追加 `&returnUrl=`（仅接受本站相对路径，外部地址被忽略）与 `&classId=`（仅接受该账号可访问的班级）。票据有效期 1 分钟、只能成功兑换一次；同一账号签发新票据会作废旧票据。服务端只保存票据的 SHA-256 摘要，持久化在数据库中，服务重启或多实例共享数据库时仍可兑换，兑换按行原子删除，并发或跨实例重复兑换都会失败。签发后账号被停用、锁定、改密或安全戳变化，票据即失效。落地页返回 `Cache-Control: no-store` 与 `Referrer-Policy: no-referrer`；兑换成功后返回本站页面并由页面跳转（不使用 302），因为链接由 App 交给浏览器打开属于跨站导航，`SameSite=Strict` 的登录 Cookie 不会随跨站重定向发送。票据会出现在浏览器地址栏，生产环境必须使用 HTTPS；与手机端连接策略一致，服务端不拒绝 HTTP 部署，但 HTTP 下票据可能被同一网络中的攻击者截获并抢先兑换。
- `GET /api/me`、`POST /api/me/password`、`POST /api/me/display-name`
- `GET /api/me/schedule`、`GET /api/me/schedule/next`
- `GET /api/swap-requests/catalog`、`GET /api/swap-requests?box=incoming|outgoing|all&status=`、`GET /api/swap-requests/{id}`
- `GET /api/swap-requests/catalog/day?classId=&date=`：换课选择器按天读取某班某天的课程 `SwapCatalogDay {date, courses[]}`（放假或没有课表时 `courses` 为空）。换课可选今天起 30 天内任意一天：`catalog` 的每个班带 `minDate`、`maxDate`（含；旧版插件只同步 7 天时为其最后一天）与 `myDates`（范围内本人有课的日期），`days` 只含今天起 7 天，更远的日期用本接口读取；超出范围或教室端未同步该日返回 400
- `POST /api/swap-requests`、`POST /api/swap-requests/{id}/approve|reject|cancel|revoke|revert`（`{id}` 也接受 8 位 `shortId`；`revert` 仅相关班级班主任与系统管理员）
- `POST /api/swap-requests/preview`：请求体同 `POST /api/swap-requests`，不写入数据，返回 `{conflicts[]}`
- `GET /api/classes/{classId}/schedule-adjustments?from=`：该班调课记录（默认从班级今天起），`ScheduleAdjustmentView {id, classId, date, index, fromSubject, toSubject, teacher?, origin: swap|direct|classisland|backfill, status: active|reverted|invalidated|expired, delivery: pending|applied|conflict, effective, conflictCode?, reason?, swapRequestId?, createdByName, createdAt}`
- `DELETE /api/schedule-adjustments/{id}`：删除直接换课或教室端本地修改产生的有效调课（本班班主任或系统管理员）；来自换课申请的请走撤回
- `POST /api/swap-requests/seen`（`{ids}`，省略或为空表示全部）：强制换课看过即处理。被强制换课的老师看过后，对应的 `swap_forced` 通知标记已读，该申请不再计入“待我处理”（仍可撤回）；返回 `{pending}` 为剩余待处理数。`GET /api/swap-requests/{id}` 查看强制换课详情也会自动标记。列表项的 `needsAction` 表示是否计入“待我处理”（可审批，或强制换课尚未看过），客户端角标应按它计数，而不是 `canDecide || canRevoke`。
- `GET /api/me/notifications?after=&unread=&limit=`、`POST /api/me/notifications/read`（`{ids}` 或 `{all:true}`）
- `GET/DELETE /api/me/sessions`
- `GET /api/state`、`GET /api/schedule`
- `POST /api/commands`
- `POST /api/files?name=`、`POST /api/files/{id}/send`、`GET/DELETE /api/files/{id}`：文件中转分发；`GET /api/plugin/files/{id}` 供插件续传下载（见“远程终端与文件分发”）
- `GET/POST/PUT/DELETE /api/users`、`/api/roles`、`/api/visitor`
- `POST /api/users/{id}/password`
- `POST /api/plugin/pairing-code`
- `GET /api/admin/status`
- `GET/POST/PUT/DELETE /api/roles`、`GET/PUT /api/visitor`
- `GET/PUT /api/settings/notifications`（读取需登录，修改仅系统管理员）、`GET/PUT /api/settings/schedule-pull`、`GET/PUT /api/settings/semester`（见“学期”）
- `GET/PUT /api/extensions`
- `GET /api/admin/system`、`POST /api/admin/updates/check`（请求体 `{"channel":"stable|beta"}`，省略渠道时使用系统设置中保存的更新渠道）、`POST /api/admin/updates/upload`（请求体为服务端发布包 zip，调试用，安装后自动重启）、`GET/POST/DELETE /api/admin/backups`
- `GET /api/audit`、`GET /api/audit/export`、`GET /api/devices/health`（见“集控运维”）
- `GET/PUT /api/settings/app-guard`、`GET/PUT/DELETE /api/devices/{credentialId}/app-guard`、`GET /api/app-guard/events`（见“应用守护”）
- `GET/POST /api/automation/rules`、`GET/PUT/DELETE /api/automation/rules/{id}`、`POST /api/automation/rules/{id}/run`、`GET /api/automation/runs`
- `GET /api/lesson-stats`、`GET /api/lesson-stats/export`、`GET /api/me/lesson-stats`
- `POST /api/me/action-links`
- `GET /api/profiles/{id}/versions`、`GET /api/profiles/{id}/versions/{revision}/diff`、`POST /api/profiles/{id}/versions/{revision}/restore`、`GET/POST /api/profiles/drift`

REST 手表请求使用 `Authorization: Bearer <accessToken>`。Razor WebUI 使用 HttpOnly、SameSite Cookie 和表单防伪令牌，不把令牌放入浏览器存储。



## 自定义角色兼容

服务端自定义角色在现有协议中仍以 `UserRole.User` 传输，并通过 `roleId`、`roleName` 可选字段提供管理信息。插件与旧版手表可以忽略新增字段，授权仍以 `effectivePermissions` 为准。


插件每次云端 WebSocket 首次连接或重连成功后，都会重新发送当前 `extensions_sync` 快照。这保证其他 ClassIsland 插件在云端连接建立前已注册的扩展功能，也能进入服务端缓存并显示在 WebUI 控制页。服务端首次发现扩展时默认启用但不向非管理员开放；管理员可在控制页逐项编辑。策略和个人手表展示偏好持久化到 SQLite 并进入授权镜像，插件局域网服务据此向每个已认证手表发送对应清单。
