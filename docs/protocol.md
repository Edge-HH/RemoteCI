# RemoteCI 协议 V3

协议版本和软件版本是两个概念。当前稳定版服务端、插件、手机和手表的软件发布版本统一为 `3.3.0.2`，但所有 WebSocket 信封都使用整数 `protocolVersion: 3`。稳定版使用四段纯数字版本，Beta 使用 `v3.x.x-beta.y`；软件版本只用于更新和诊断，不参与连接拒绝；只有协议号不是 `3` 时才返回 `PROTOCOL_VERSION_UNSUPPORTED`。

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

管理员的有效权限固定为 16383（全部位）。`RequestScheduleSwap` 与 `ForceScheduleSwap` 是全局权限，由系统管理员在“角色配置”中开关：内置老师和班主任默认开启前者（升级迁移会为已有的两个内置角色补上一次），后者默认关闭。普通用户固定包含值 1，其余权限来自服务端授权。权限设置界面将值 2 显示为“概览”；七日课表查看只要求账号已登录，手动拉取课表允许系统管理员，以及班级自治策略允许时的本班班主任；定时自动拉取间隔是全局设置，只有系统管理员可以修改，值 16 保护换课。`TeacherComing` 权限位和命令仅为旧客户端兼容保留，当前不在 WebUI、各端 App 或权限设置中显示；`SendNotifications` 只保护自定义通知与清除提醒，`SendVoiceMessages` 独立保护语音消息，`RunExtensions` 是所有插件扩展的独立权限，`MainMenuControl` 保护主界面显隐，`PowerControl` 保护音量和 Windows 电源操作，`ChangeDisplayName` 仅保留为兼容旧权限数据，实际只有系统管理员可以修改用户可见用户名（DisplayName，登录 ID 不变）；用户名会影响老师获取的日程信息。`ApiAccess` 允许账号创建 API Key 并调用 REST API；管理员、班主任与老师默认拥有 API 访问。

账号密码只出现在第一次 `POST /api/auth/login` 的请求内。生产环境必须使用 HTTPS；Android 手机端为兼容尚未配置 TLS 的现有部署，允许用户在持续显示风险提示的情况下明确连接 HTTP 云服务器。响应包含 1 小时 `accessToken`、30 天 `deviceSessionId/deviceSecret` 和用户有效权限。`POST /api/auth/refresh` 会同时轮换访问令牌和设备密钥；旧值立即失效。

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
| `schedule_sync` | 插件 → 服务端/手表 | 今天起七天的日期、课程、科目清单和每日修订号 |
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

## 局域网设备发现

手表可以在登录页扫描同一局域网中的插件，无需手动填写电脑 IP：

1. 手表向固定 UDP 端口 `48765` 发送广播串 `REMOTECI_DISCOVER_V3`；插件应答 JSON `{protocolVersion, instanceName, port}`，`protocolVersion` 为整数 `3`，`port` 是插件当前局域网 WebSocket 端口。
2. 用户选中应答条目后，手表连接 `ws://<应答来源地址>:<port>/bootstrap`，插件返回 `connection_bootstrap` 载荷 `{instanceName, cloudServerUrl}`。该端点未认证，只提供云端地址与实例名，密码和会话凭据始终只交给云服务器。
3. 插件每次云端重连时重新发现本机网卡，通过 `plugin_network_info`（`{lanServerEnabled, addresses[], port}`）上报服务端；服务端归一化后广播给在线手表并缓存最新一份，新连接的手表在 `auth_state` 之后立即收到。手表据此更新局域网候选地址，地址或端口变化且当前走云端中转时自动重试直连。

发现与 bootstrap 均无认证，理论上可被同一局域网内的设备伪造；手表会原样展示获取到的云服务器地址，明文 HTTP 地址会额外提示风险，用户应在输入密码前确认地址可信。

`command_result.replyToMessageId` 必须等于请求的 `messageId`。服务端只把结果交给对应的 WebSocket 或等待中的 WebUI 请求，等待上限 15 秒。

插件通过云端 WebSocket 认证后，服务端必须立即发送一次 `schedule_pull`，避免插件启动时的首次 `schedule_sync` 早于云端连接建立而丢失。任何已认证手表都可以发送该只读消息：云端连接由服务端立即转发给在线插件，局域网连接直接交给插件。插件端是最终任务锁，服务端同时维护云端入口的前置锁；插件推送、WebUI 拉取、手表拉取、自动拉取和连接初始化任一正在运行时，新请求返回 `schedule_sync_status.state=Busy`，其中 `activeTaskId` 指向占用任务。Running、Completed、Failed 和 Busy 状态会广播到在线手表并供插件设置页、WebUI 展示；任务成功、失败、插件断开或 15 秒超时后释放。WebUI 收到新 `schedule_sync` 后用完整 `ScheduleBundle` 整体替换旧缓存，不做字段合并。该请求不授予换课能力，也不绕过 `ChangeSchedule` 的权限检查。插件离线期间不排队。

云端服务端直接在认证成功的 `auth_state.serverVersion` 中下发自身软件版本；插件通过 `account_sync.serverVersion` 保存同一版本，并在局域网认证成功时转发给手表。服务端与手表正式渠道只选择协议主版本相同的四段纯数字 Release，Beta 渠道额外选择 `v3.x.x-beta.y`，旧三段 `v3.x.x` 稳定标签和 V4 不会进入自动更新候选。手表不再以 WebUI 软件版本为上限，仍保留渠道筛选、禁止降级、同版本强制覆盖和 APK 签名校验。

## 能力协商

V3 的基础能力（自 3.1.0 引入）为 `class-state.read`、`schedule.read`、`schedule.pull`、`schedule.change`、`notification.send`、`notification.clear`、`teacher-coming`、`main-menu.visibility`、`power.control`、`volume.control` 和 `extensions.run`。其中 `teacher-coming` 仅为旧 V3 客户端兼容保留，当前版本各端不显示入口。后续新增的 `voice-message.send`、`software.inventory`、`software.upgrade-plugins`、`software.upgrade-classisland`、`plugin.install`、`plugin.uninstall`、`plugin.enable`、`plugin.management-policy`、`profile.distribute`、`schedule.time-layout`、`management.join`、`schedule.subject-teacher`、`terminal.execute`、`file.distribute` 和 `extensions.settings` 只进入当前版本能力列表，不加入旧 V3 端默认获得的基础能力。插件和手表连接后通过 `peer_capabilities` 上报软件版本与能力；服务端通过 `capabilities_sync` 向手表发送自身和当前主插件的能力。未上报能力的旧 V3 端按上述基础能力处理，未知能力标识被忽略。

WebUI 的有效能力是“服务端 ∩ 当前班级主插件”，手表与手机的有效能力是“本地 ∩ 服务端 ∩ 当前班级主插件”。服务端在 `capabilities_sync.classPlugins` 中按接收方可访问的班级逐个下发主插件能力，客户端切换班级时按新班级重算；没有对应条目表示该班插件离线。旧服务端没有该字段时客户端退回 `plugin`。多插件时，当前主插件仍是最早接入的健康插件；主插件切换、断开或能力更新后，服务端重新广播能力快照。界面应隐藏缺失能力的入口，服务端转发命令前仍需按统一映射复核主插件能力，缺少能力时返回 `CAPABILITY_UNSUPPORTED`。能力声明不能绕过账号权限或扩展策略检查。

`event_notify.payload.event` 的值 6 表示 ClassIsland 自动化“显示提醒”行动产生的通知，值 7 表示第三方 ClassIsland 插件产生的通知。手表分别持久化开关；内置课程、天气等通知不会被值 7 重复转发。

自定义通知的标题与正文均可留空：标题留空时插件统一显示默认标题 `RemoteCI 通知`（仍会按上述规则添加前缀），正文留空时保持为空，由 ClassIsland 只显示标题。通知标题是否添加 `由用户名发送：` 前缀由服务端全局设置 `forceSenderInTitle` 决定（系统配置页开关，默认开启，仅系统管理员可修改）。开启时插件最终执行会把标题格式化为 `由用户名发送：原标题`；关闭时不添加前缀。发送者名称取自已认证账号的 `displayName`（界面称“用户名”），而 `username` 是唯一登录 ID；服务端转发命令时会按全局设置覆盖客户端请求中的署名标志，客户端不能绕过。设置变更时服务端通过 `settings_sync` 推送在线手表，手表通知页据此决定是否显示“将显示发送人”提示。通知请求还可通过 `isNotificationEffectEnabled`、`isNotificationSoundEnabled` 和 `isSpeechEnabled` 分别控制 ClassIsland 的提醒强调特效、提醒音效和语音朗读；省略时均为关闭。`isNotificationTopmostEnabled` 控制提醒时置顶主界面；`durationSeconds`（默认 5，范围 1-3600）与 `repeatCounts`（默认 1，范围 1-10）对齐 ClassIsland 集控，省略或 0 表示默认值。`isRollingEnabled` 是三态字段，控制正文是否横向滚动：省略时（旧 V3 客户端）保持升级前的行为，正文滚动 `repeatCounts` 遍；显式 `true` 同样滚动，总时长为 `durationSeconds × repeatCounts`；只有显式 `false` 才静态显示正文 `durationSeconds` 秒，并把整条提醒依次显示 `repeatCounts` 次。服务端转发时不会把缺失字段补成 `false`，新客户端关闭滚动时必须显式发送 `false`。旧插件忽略该字段并始终滚动。

通知参数上限：标题 60 字（超出部分由插件截断，与旧行为一致）、正文 500 字、持续时间 1-3600 秒、重复次数 1-10 次。WebSocket 命令、`POST /api/commands`、`POST /api/commands/broadcast` 与 WebUI 共用同一校验，越界请求返回 `INVALID_REQUEST`；插件执行端对局域网直连请求再次校验，并对时长与重复次数限幅。`POST /api/commands` 与 WebSocket 一样按全局 `forceSenderInTitle` 覆盖请求中的署名标志。

控制命令值 3 为清除当前 ClassIsland 提醒，值 4 通过 `mainMenuVisible` 设置主界面显隐，值 5 通过 `powerAction` 选择关机、重启、睡眠或休眠。值 6 通过 `volume.level` 设置 Windows 默认播放设备的 0-100 主音量，或通过 `volume.muted` 设置静音状态；WebUI 在静音状态下向高调节时会在同一命令中同时发送 `level` 与 `muted: false`。休眠入口只在插件状态报告 Windows 已启用休眠时显示。

`extensions_sync` 的载荷是其他 ClassIsland 插件通过 RemoteCI 注册的扩展功能列表；云端服务端和插件局域网服务都会缓存最近一次清单，并在手表完成认证后主动补发。命令值 7 为 `RunExtension`；命令值 8 的旧提醒指令仍由协议层兼容处理，但当前各端不提供入口。通过 `extensionId` 指定目标扩展，`extensionArgs` 携带参数字典（值统一为字符串）。扩展调用必须同时通过独立的 `RunExtensions` 权限和管理员为该扩展设置的启用/普通账号开放策略；`RequiredPermission` 只作为旧扩展兼容字段传输，不再关联通知、电源等权限。账号的 `allowedExtensionIds` 与 `visibleExtensionIds` 随认证状态和 `account_sync` 下发，后者只控制自己的手表入口。未注册、缺少必填参数或权限不足时分别返回 `INVALID_REQUEST` / `FORBIDDEN`，执行异常统一返回 `INTERNAL_ERROR`。

扩展参数与扩展设置字段共用 `ExtensionParameter` 结构：`key`、`label`（显示名称）、`type`（1=文本、2=数字、3=开关、4=选项）、`defaultValue`、`required`、`options`，以及可选的 `optionLabels`（与 `options` 按下标对应的选项显示名称）、`description`（字段说明）、`placeholder`、`multiline`（WebUI 多行文本）和 `min` / `max`（数字范围）。服务端预检与插件执行端使用同一套校验：数字必须可解析且在范围内，开关只能是 `true` / `false`（规范化为小写），选项必须是 `options` 之一；未声明的执行参数键原样透传以兼容旧扩展。旧手表忽略新增字段，选项按原值显示。

扩展清单的 `icon` 为可选 Material 图标名，由手表端白名单解析（不区分大小写，忽略下划线、连字符、空格与 `Icons.Rounded.` 前缀）；未命中白名单或缺失时手表按钮回退为纯文字，服务端与 WebUI 不解析该字段。

状态快照中的 `isVolumeControlAvailable`、`volumePercent` 和 `isMuted` 分别表示默认播放设备是否可控、当前主音量百分比和静音状态，手表必须以这些真实状态刷新音量页。

状态快照中的 `currentTimeLayoutItem` 使用插件本地时间（如 `16:30-17:10 语文`），并携带 `timeZoneOffsetMinutes`（插件本地时区相对 UTC 的偏移分钟数，东八区为 480）。手表端以快照的 `generatedAt`（UTC）加该偏移推算“插件本地当前时间”，再计算课程进度环，避免两端时区不一致时进度环显示为空。旧版插件不携带 `timeZoneOffsetMinutes` 时，手表回退到自身本地时间，行为与旧版一致。

## 换课

`ScheduleChangeRequest` 包含：

- `date`：今天起未来七天的 `yyyy-MM-dd`。
- `mode`：1 为交换，2 为替换。
- `sourceIndex`：原节次的零基索引。
- `targetIndex`：交换模式必填。
- `replacementSubjectId`：替换模式必填。
- `expectedRevision`：客户端读取当天课表时的修订号。
- `permanent`：为 `true` 时直接修改 ClassIsland 源课表，本周及以后每周持续生效；默认 `false`，只写入当天临时课表层。

手机端的能力快照只用于界面提示，不能代替服务端鉴权；换课入口在当前班级拥有 `ManageSchedule` 时仍可提交，最终由服务端和目标班级主插件复核能力并返回 `CAPABILITY_UNSUPPORTED` 等结果。

插件发现修订号已变化时返回 `SCHEDULE_STALE` 和最新修订号，不覆盖别人刚完成的修改。

## 换课申请

拥有 `RequestScheduleSwap` 的老师可以向其他老师发起临时换课申请，对方同意后服务端代为向教室端下发 `ChangeSchedule`（`permanent: false`）。教室端插件无需升级：执行身份 `RequestedBy` 由服务端生成，显示名为“换课申请 · 姓名”，只携带 `ManageSchedule`。

- **模式**：`mode` 1 为互换，`source` 与 `target` 两节课可以跨班、跨日，也可以选别人的课，但至少一节的老师必须是申请人；跨班时学科按名称在各自班级匹配。2 为替换，只填 `target` 与申请人任教的 `subjectName`。
- **审批人**：对方课位老师字段匹配到的账号；匹配不到时回退为该班班主任，再回退为系统管理员。两节都是申请人自己的课时直接生效。
- **执行**：同班同日的互换下发一条 `Exchange`。其余情况逐节下发 `Replace`，修订号取最新值或上一条回执的 `scheduleRevision`；后一条失败时，前面已生效的会反向替换补偿。执行前按服务端最新课表复核课位快照，变化时返回 `SWAP_SLOT_CHANGED`。插件离线或超时时申请保持待审批。
- **单节临时任课老师**：ClassIsland 只有“班级学科 → 老师”。换课后某节课的实际老师与学科默认老师不一致时，服务端记录单节覆盖 `LessonTeacherOverride (classId, date, index) → teacherName`，叠加到下发给手机、手表、WebUI 的课表和“我的日程”。该节学科再次变化或日期过去后，覆盖自动失效。覆盖有效期间，临时任课老师按任教老师获得该班访问。ClassIsland 本机仍显示学科默认老师。
- **强制换课**：拥有 `ForceScheduleSwap` 时可以带 `force: true` 立即生效，并通知对方老师与班主任。对方可撤回，撤回后按记录的原学科反向替换。同一申请人当天不能再强制换走这一节课（`SWAP_FORCE_LOCKED`），其他老师、其他课以及之后各周的同一节课不受影响。
- **状态**：1 待审批、2 已通过、3 已拒绝、4 已撤销、5 已过期（每 10 分钟清扫）、6 已强制、7 已撤回。

每次提交、通过、拒绝、撤销、强制和撤回都会写入个人通知（`UserNotification`），并同时推送：在线手机和手表走 `user_notify`，已订阅的浏览器走 Web Push（RFC 8291 aes128gcm + RFC 8292 VAPID；VAPID 密钥首次使用时生成并存入 `SystemMetadata`）。AstrBot 等外部客户端轮询 `GET /api/me/notifications`。

## 语音消息

新增命令 `SendVoiceMessage = 9`，要求权限 `SendVoiceMessages = 512` 和能力 `voice-message.send`。新能力仅列入当前版本能力列表，不能加入未声明能力的旧 V3 端默认获得的基础能力。

```json
{
  "command": 9,
  "voiceMessage": {
    "format": "pcm_s16le_16000_mono",
    "audioBase64": "AAA="
  }
}
```

音频为 16 kHz、16 位有符号小端、单声道 PCM，最多 60 秒（1,920,000 字节），不能为空或包含半个采样；`audioBase64` 使用标准 Base64。服务端和插件校验音频格式与大小，不接收音频 URL 或本地路径。WebSocket 信封接收上限为 16 MiB，覆盖 Base64 中 `+` 被 JSON 转义成 `\u002B` 的最坏情况；解码后仍受上述音频上限约束。

WebUI 通过 `POST /Control?handler=VoiceMessage` 上传 `application/octet-stream` 原始 PCM，携带 Cookie 和 `X-CSRF-TOKEN` 防伪头；服务端在内存中转为同一命令并等待插件回执。HTTP 页面会显示“（当前使用不安全的HTTP连接）”；但浏览器通常只在 HTTPS、localhost 或管理员明确放行明文麦克风的策略下提供 `getUserMedia`。手表通过已认证的云端或局域网 WebSocket 发送。所有接入端覆盖 `requestedBy`，插件使用认证账号的 `displayName` 生成“来自xxx的语音消息”，不受文本通知署名开关影响。

Windows 插件自动播放录音，并显示上述 ClassIsland 通知：强调特效开启、通知音效关闭、朗读关闭（仍遵循宿主全局提醒限制）。底部浮窗上滑出现，拖动标题可移动；顶部显示发送人和总时长，中间使用 ClassIsland 原生 Slider 拖动播放位置，底部三个无底色的 ClassIsland Fluent 图标分开放置，分别用于暂停/继续、后退 5 秒和关闭，关闭图标使用系统危险色，播完后可重新播放。浮窗使用宿主 Fluent 浮层背景、描边、圆角、深浅色主题和 UI 字体；点击浮窗外部也会关闭并停止播放。关闭或插件停止时立即释放音频资源。已有浮窗时返回 `BUSY`，不排队、不打断现有语音；离线不缓存重发。成功回执表示播放器已启动，不表示整条语音已经播放完毕。音频不写入文件或通知历史，广播事件仅包含发送人提示。

## 远程软件升级

RemoteCI 插件在云端连接建立后通过 `software_inventory` 上报设备名称、ClassIsland 主程序版本和当前已加载插件版本。服务端按插件长期凭据保存最后一份清单，因此设备离线时 WebUI 仍能显示最近一次版本；重新上线后由新清单覆盖。

命令值 10 为 `UpgradePlugins`，命令值 11 为 `UpgradeClassIsland`，命令值 12 为 `RefreshSoftwareInventory`，命令值 20 为 `RestartClassIsland`。前三者都要求账号具有 `ManageUsers` 权限，并分别要求插件声明 `software.upgrade-plugins`、`software.upgrade-classisland`、`software.inventory` 能力；`RestartClassIsland` 仅允许系统管理员使用，不重启 Windows。WebUI 的“批量控制”页按在线插件连接逐台下发，避免同一班级多台设备只收到最早连接的一条命令。

升级命令只表示任务已被设备接受；实际下载、部署和重启由 ClassIsland 官方插件市场或官方 `UpdateService` 在后台完成。插件升级写入宿主的 `.cipx` 缓存并在重启后安装；ClassIsland 升级使用官方文件图校验和部署流程。完成后设备会主动重启并重新连接，服务端以新的 `software_inventory` 更新版本和最近升级状态。失败或已是最新版本时不会重启，状态随清单回传。
## 插件管理、档案分发与集控

命令值 13 为 `InstallPlugins`、14 为 `UninstallPlugins`、15 为 `SetPluginEnabled`、16 为 `SetPluginManagementPolicy`、17 为 `DistributeProfile`、18 为 `UpdateTimeLayout`、19 为 `JoinManagement`。这些命令都要求 `ManageUsers` 权限，并在服务端和插件端再次确认系统管理员身份；能力标识分别为 `plugin.install`、`plugin.uninstall`、`plugin.enable`、`plugin.management-policy`、`profile.distribute`、`schedule.time-layout` 和 `management.join`。

- 插件安装使用 ClassIsland 官方插件市场解析依赖并下载 `.cipx` 缓存，卸载和启停使用宿主公开的 `PluginInfo` 状态接口；RemoteCI 拒绝操作自身，避免远程控制链路被卸载或禁用。
- `DistributeProfile` 只接收 ClassIsland 档案 JSON 和分发范围（时间表、课表、科目），不接收任意文件路径；请求可带 `importProfileName`、`replaceCurrentProfile`（默认 `false`）和 `enableImportedProfile`（默认 `true`）。默认会把选中的内容写入一个新档案并启用该档案；只有明确开启 `replaceCurrentProfile` 才会写入当前档案。`replaceExisting` 仍只控制选中分发范围内的同 Id 数据是否先清空。`UpdateTimeLayout` 接收结构化时间点并由插件在 UI 线程写入档案。
- 插件对 `Profile` 的字典属性按属性名运行时读取，以兼容 ClassIsland 2.0 的 `ObservableDictionary` 与后续 2.x 的字典实现；因此升级宿主后无需重新生成档案 JSON。
- `JoinManagement` 由管理员上传 ClassIsland 集控配置文件 `ManagementPreset.json`（即宿主 ManagementSettings 的 JSON）发起。插件解析后按配置文件里的服务器类型（Serverless manifest 模板或 ManagementServer 的 API + gRPC）注册，先校验集控清单核心版本，再写入宿主的 Management 配置并重启；配置文件里的 ID（ClassIdentity）由服务端按目标设备所属班级名自动填充，不需要管理员填写。
- `SetPluginManagementPolicy` 只约束 RemoteCI 后续发起的远程插件安装或卸载。ClassIsland 当前没有公开的宿主级“禁止本地安装/卸载插件”策略 API，因此该策略不阻止用户在 ClassIsland 本地设置页操作。

WebUI 批量控制页把功能参数和目标设备分成两步：先填写参数，再选择班级、分组或具体设备。班级级选择只投递到该班最早接入的在线设备；具体设备选择按插件连接逐台投递。命令下发成功只表示设备端已接受任务，下载、部署、重启或档案拉取的最终结果由后续 `software_inventory`、连接状态或用户再次查看时体现。

## 远程终端与文件分发

命令值 22 为 `ExecuteTerminalCommand`，23 为 `SendFile`。两者都要求 `ManageUsers` 权限，并在服务端和插件端再次确认系统管理员身份；能力标识分别为 `terminal.execute` 和 `file.distribute`，只进入当前版本能力列表。

- 远程终端在设备上以当前登录用户执行单条命令，等价于 `cmd /d /c`：每次执行相互独立，进程之间不保留 shell 状态。载荷 `terminalCommand` 包含 `command`（≤4000 字符）、可选 `workingDirectory`（≤260 字符，目录不存在时返回 `INVALID_REQUEST`）和可选 `timeoutSeconds`（1-10，默认 10）。插件合并标准输出与标准错误（中间以 `--- 标准错误 ---` 分隔），放入 `command_result.data` 返回，总上限 64K 字符，超出截断并注明；超时的进程会被强制结束并返回 `COMMAND_TIMEOUT`，`data` 携带已捕获的部分输出。命令正常结束时回执 `success=true`，退出代码写入 `message`，非零退出代码不视为失败。
- 文件分发以内联 Base64 经 WebSocket 传输一个文件，解码后上限 10 MB（复用语音消息的 16 MiB 信封上限），不接受 URL 或服务器本地路径。载荷 `fileDistribution` 包含 `fileName`、`contentBase64`、`targetFolder`（1=桌面、2=下载、3=文档）和 `overwrite`。服务端与插件共用同一校验：文件名剥掉目录部分并过滤非法字符，拦截 Windows 保留设备名，长度上限 200；保存目录只允许 `targetFolder` 枚举列出的用户文件夹（下载目录按系统已知文件夹解析），不接收任意路径。`overwrite` 为 `false`（默认）时同名文件自动追加序号，绝不覆盖；成功回执的 `data` 是设备上的完整保存路径。
- WebUI 在“批量控制”与班级“控制”页的远程维护区开放这两个功能，均需先勾选高风险确认。终端与文件分发结果通过 AJAX 逐设备展示（输出或保存路径），不走表单跳转；普通班主任即使被授予 `ManageUsers` 也会被服务端与插件拒绝，只有系统管理员可以使用。

## 扩展分组与扩展设置

插件在云端连接建立、分组注册/注销和设置值变化时发送 `extension_groups_sync`，服务端按插件凭据回填 `classId` 并只缓存在内存中（以设备上报为准，插件重连后恢复）；包含非法分组 Id 的整条同步会被忽略。扩展功能通过 `groupId` 关联到分组，WebUI 控制页与批量控制页按分组展示，未关联或分组未注册的扩展归入“其他扩展”。

命令值 24 为 `ApplyExtensionSettings`，能力标识 `extensions.settings`（只进入当前版本能力列表）。载荷 `extensionSettings` 为 `{groupId, values}`，`values` 只包含本次要修改的字段（部分更新），未出现的字段保持设备当前值；字段必须已在分组中声明，必填字段不能清空。权限口径：

- 服务端只在 WebUI 扩展设置页与 `PUT /api/classes/{classId}/extension-groups/{groupId}/settings` 中接受该操作：系统管理员可修改任意班级；班主任需要系统管理员在“班级管理 → 班主任权限”中开启“修改本班的扩展插件设置”，且在本班拥有 `RunExtensions` 权限。手表、手机的通用命令通道和 `POST /api/commands` 一律拒绝。
- 插件端要求 `requestedBy` 携带 `RunExtensions` 权限位作为纵深防御；局域网直连无法复核班级自治策略，因此直接返回 `FORBIDDEN`。
- 下发时该班插件离线：服务端把字段合并保存为待补发并返回 `QUEUED`（REST 为 202）；插件下次发送 `extension_groups_sync` 时，服务端在后台复核下发者权限后定向补发，插件返回明确结果后删除记录，连接再次中断时保留。
- 写入成功或失败后，插件都会重新发送 `extension_groups_sync`，让 WebUI 预填值反映设备真实状态。批量下发时服务端向所选班级的每一台在线设备逐台发送并逐台返回结果。
## 老师、班主任角色与我的日程

服务端内置“老师”角色（`AccountRoleKind.Teacher = 5`，固定 ID `55555555-5555-5555-5555-555555555555`，启动时幂等种子并随 `roleId`/`roleName` 下发）。`UserProfile` 新增可选 `roleKind`（整数角色种类），客户端据此识别内置老师角色，不受角色改名影响；旧服务端不下发该字段。

内置“班主任”角色（`AccountRoleKind.ClassAdministrator = 4`，固定 ID `44444444-4444-4444-4444-444444444444`，原名“班管理员”）保留原有的本班管理能力，同时和老师一样拥有个人“我的日程”：全局角色为班主任的账号也按显示名匹配课表科目教师名，可使用 WebUI 侧栏“我的日程”和下方两个日程端点。按姓名获得的“任教班级访问与任教权限”仍只属于老师角色；班主任对班级的访问仍来自班级成员关系。内置角色名由服务端固定，角色配置中只能调整默认权限，不能改名；班主任与老师一样不能自行修改用户名，用户名只能由系统管理员修改。批量导入仍接受旧角色名“班管理员”；旧版客户端在没有 `roleKind` 时，“班主任”和“班管理员”两个角色名都识别为本班班主任。

老师的显示名（DisplayName）即“姓名”：显示名与班级课表中的科目教师名（ClassIsland 档案 `Subject.TeacherName`，随 `schedule_sync` 下发）一致时，该老师账号绑定到对应课程。匹配规则为去首尾空白后完全相等，或教师字段按 `、 , ， / ; ； |` 分隔符拆分后任一姓名完全相等；因此多教师科目（如 `张三/李四`）可填写其中之一。绑定完全动态计算，不落库：班级插件尚未推送课表（如服务端刚重启）时绑定暂不可见，收到课表后自动生效，且该班匹配教师集合变化时会触发 `account_sync` 重新推送授权镜像。

老师在任教班级的默认权限为其全局老师角色默认权限：`ViewCurrentCourse | SendNotifications | SendVoiceMessages | ApiAccess`，因此老师默认可以创建 API Key 读取自己的日程。`ClassSummary` 新增可选 `roleKind`（该用户在本班的角色种类），客户端据此判断是否为本班班主任，旧服务端不下发时退回角色名。用户名由系统管理员维护，因为它是老师获取日程信息时匹配课表科目教师名的依据。非任教班级的访问与普通用户一致（默认班级的自动成员关系沿用既有语义）。绑定按姓名匹配，同名教师会绑定到同名课程，建议使用完整姓名；管理员可在 WebUI 课表页“科目教师”区显式分配（见下）以统一写法。

`schedule_sync` 载荷中的 `CourseEntry` 与 `SubjectEntry` 新增可选 `teacher` 字段（教师名，未设置时省略），旧版服务端与插件不下发、所有端可安全忽略。

- `GET /api/me/schedule`：返回当前老师或班主任账号按姓名绑定后跨班级聚合的“我的日程”`MyScheduleResponse { fromDate, generatedAt, days[] }`，每天 `items[]` 按班级分组（`classId`、`className`、`courses[]`，课程按节次排序）。全局角色既不是老师也不是班主任，或没有绑定时，`days` 为空。
- `GET /api/me/schedule/next`：返回 `MyNextCourseResponse { at, current?, next? }`，分别是指定时刻正在上的课与接下来的第一节课；每项含 `date`、`classId`、`className`、`course`、`startsAt`、`endsAt`。可选查询参数 `at`（ISO 8601）指定计算时刻，省略时取服务端当前时间。课程钟点是教室电脑本地时间，服务端按该班最近状态快照的 `timeZoneOffsetMinutes` 换算为绝对时间，尚无快照时退回服务端本地时区。非老师/班主任账号、无绑定或七日内没有后续课程时对应字段省略。两个日程端点都接受 API Key。
- `POST /api/me/display-name`：系统管理员修改显示名（即老师姓名），载荷 `{displayName}`（1-40 字），成功返回 204 并触发授权镜像同步；普通账号即使保留旧的 `ChangeDisplayName` 权限也会被拒绝。
- 命令值 21 为 `SetSubjectTeacher`，要求 `ManageSchedule` 权限和能力 `schedule.subject-teacher`。载荷 `subjectTeacher` 包含 `subjectId` 与 `teacherName`（≤100 字，空表示清除）；插件在 UI 线程写入档案 `Subject.TeacherName` 并保存，保存失败回滚并返回 `SAVE_FAILED`，成功后立即重推课表。WebUI 课表页“科目教师”区由此为班级科目分配授课教师。

手机端老师登录后课表页默认展示“我的日程”（可切换“本班课表”）；班主任课表页默认展示“本班课表”，也可切换到“我的日程”，列表按天分组显示班级、科目、教师与节次时间。

## REST API

主要端点：

- `POST /api/plugin/pair`
- `POST /api/auth/login`、`/api/auth/refresh`、`/api/auth/logout`
- `POST /api/auth/web-ticket`：客户端一键打开 WebUI。需设备会话 Bearer 令牌（API Key 返回 403），返回 `{ticket, path, expiresAt}`；用浏览器打开 `服务器地址 + path` 即以该账号登录 WebUI，可追加 `&returnUrl=`（仅接受本站相对路径，外部地址被忽略）与 `&classId=`（仅接受该账号可访问的班级）。票据有效期 1 分钟、只能成功兑换一次；同一账号签发新票据会作废旧票据。服务端只保存票据的 SHA-256 摘要，持久化在数据库中，服务重启或多实例共享数据库时仍可兑换，兑换按行原子删除，并发或跨实例重复兑换都会失败。签发后账号被停用、锁定、改密或安全戳变化，票据即失效。落地页返回 `Cache-Control: no-store` 与 `Referrer-Policy: no-referrer`；兑换成功后返回本站页面并由页面跳转（不使用 302），因为链接由 App 交给浏览器打开属于跨站导航，`SameSite=Strict` 的登录 Cookie 不会随跨站重定向发送。票据会出现在浏览器地址栏，生产环境必须使用 HTTPS；与手机端连接策略一致，服务端不拒绝 HTTP 部署，但 HTTP 下票据可能被同一网络中的攻击者截获并抢先兑换。
- `GET /api/me`、`POST /api/me/password`、`POST /api/me/display-name`
- `GET /api/me/schedule`、`GET /api/me/schedule/next`
- `GET /api/swap-requests/catalog`、`GET /api/swap-requests?box=incoming|outgoing|all&status=`、`GET /api/swap-requests/{id}`
- `POST /api/swap-requests`、`POST /api/swap-requests/{id}/approve|reject|cancel|revoke`（`{id}` 也接受 8 位 `shortId`）
- `GET /api/me/notifications?after=&unread=&limit=`、`POST /api/me/notifications/read`（`{ids}` 或 `{all:true}`）
- `GET/DELETE /api/me/sessions`
- `GET /api/state`、`GET /api/schedule`
- `POST /api/commands`
- `GET/POST/PUT/DELETE /api/users`、`/api/roles`、`/api/visitor`
- `POST /api/users/{id}/password`
- `POST /api/plugin/pairing-code`
- `GET /api/admin/status`
- `GET/POST/PUT/DELETE /api/roles`、`GET/PUT /api/visitor`
- `GET/PUT /api/settings/notifications`（读取需登录，修改仅系统管理员）、`GET/PUT /api/settings/schedule-pull`
- `GET/PUT /api/extensions`
- `GET /api/admin/system`、`POST /api/admin/updates/check`、`GET/POST/DELETE /api/admin/backups`

REST 手表请求使用 `Authorization: Bearer <accessToken>`。Razor WebUI 使用 HttpOnly、SameSite Cookie 和表单防伪令牌，不把令牌放入浏览器存储。



## 自定义角色兼容

服务端自定义角色在现有协议中仍以 `UserRole.User` 传输，并通过 `roleId`、`roleName` 可选字段提供管理信息。插件与旧版手表可以忽略新增字段，授权仍以 `effectivePermissions` 为准。


插件每次云端 WebSocket 首次连接或重连成功后，都会重新发送当前 `extensions_sync` 快照。这保证其他 ClassIsland 插件在云端连接建立前已注册的扩展功能，也能进入服务端缓存并显示在 WebUI 控制页。服务端首次发现扩展时默认启用但不向非管理员开放；管理员可在控制页逐项编辑。策略和个人手表展示偏好持久化到 SQLite 并进入授权镜像，插件局域网服务据此向每个已认证手表发送对应清单。
