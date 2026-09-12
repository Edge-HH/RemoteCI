# RemoteCI 协议 V3

协议版本和软件版本是两个概念。当前稳定版服务端、插件和手表的软件发布版本统一为 `3.2.1.4`，但所有 WebSocket 信封都使用整数 `protocolVersion: 3`。稳定版使用四段纯数字版本，Beta 使用 `v3.x.x-beta.y`；软件版本只用于更新和诊断，不参与连接拒绝；只有协议号不是 `3` 时才返回 `PROTOCOL_VERSION_UNSUPPORTED`。

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
| 64 | `TeacherComing` |
| 128 | `RunExtensions` |
| 256 | `MainMenuControl` |
| 512 | `SendVoiceMessages`（发送语音） |

管理员的有效权限固定为 1023。普通用户固定包含值 1，其余权限来自服务端授权。权限设置界面将值 2 显示为“概览”；七日课表查看和手动拉取只要求账号已登录，值 16 保护换课和自动拉取设置。`TeacherComing` 单独保护“老师来了”，`SendNotifications` 只保护自定义通知与清除提醒，`SendVoiceMessages` 独立保护语音消息，`RunExtensions` 是所有插件扩展的独立权限，`MainMenuControl` 保护主界面显隐，`PowerControl` 保护音量和 Windows 电源操作。

账号密码只出现在第一次 HTTPS `POST /api/auth/login` 的请求内。响应包含 1 小时 `accessToken`、30 天 `deviceSessionId/deviceSecret` 和用户有效权限。`POST /api/auth/refresh` 会同时轮换访问令牌和设备密钥；旧值立即失效。

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
| `capabilities_sync` | 服务端/插件 → 手表 | 服务端能力和当前主插件能力快照 |
| `state_push` | 插件 → 服务端/手表 | 高频当前课程、提醒播放、主界面显隐与可用电源状态，不含完整课表 |
| `schedule_sync` | 插件 → 服务端/手表 | 今天起七天的日期、课程、科目清单和每日修订号 |
| `schedule_pull` | 服务端/手表 → 插件 | 只读请求，载荷可含 `{taskId, source, requestedAt}`，要求插件立即重新生成并推送七日课表 |
| `schedule_sync_status` | 插件 → 服务端/手表 | 全局课表任务状态：Running、Completed、Failed 或 Busy，以及任务来源和占用任务 ID |
| `extensions_sync` | 插件 → 服务端/手表 | 扩展功能清单（id、displayName、icon、requiredPermission、parameters） |
| `event_notify` | 插件 → 服务端/手表 | 上课、下课、放学、课表变更、自定义消息、ClassIsland 自动化或第三方插件通知 |
| `command` | 手表/服务端 → 插件 | 结构化换课、老师来了、通知、主界面、音量或电源命令 |
| `command_result` | 插件 → 发起者 | 真实成功、失败码、消息和可选新修订号 |
| `settings_sync` | 服务端 → 手表 | 全局通知设置快照（目前含 `forceSenderInTitle`） |
| `plugin_network_info` | 插件 → 服务端 → 手表 | 插件局域网直连地址与端口（每次云端重连时重新发现网卡） |
| `connection_bootstrap` | 插件 → 手表 | 用户选中局域网插件后，插件返回的云端连接信息 |

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

V3 的基础能力（自 3.1.0 引入）为 `class-state.read`、`schedule.read`、`schedule.pull`、`schedule.change`、`notification.send`、`notification.clear`、`teacher-coming`、`main-menu.visibility`、`power.control`、`volume.control` 和 `extensions.run`。插件和手表连接后通过 `peer_capabilities` 上报软件版本与能力；服务端通过 `capabilities_sync` 向手表发送自身和当前主插件的能力。未上报能力的旧 V3 端按上述基础能力处理，未知能力标识被忽略。

WebUI 的有效能力是“服务端 ∩ 当前主插件”，手表的有效能力是“手表本地 ∩ 服务端 ∩ 当前主插件”。多插件时，当前主插件仍是最早接入的健康插件；主插件切换、断开或能力更新后，服务端重新广播能力快照。界面应隐藏缺失能力的入口，服务端转发命令前仍需按统一映射复核主插件能力，缺少能力时返回 `CAPABILITY_UNSUPPORTED`。能力声明不能绕过账号权限或扩展策略检查。

`event_notify.payload.event` 的值 6 表示 ClassIsland 自动化“显示提醒”行动产生的通知，值 7 表示第三方 ClassIsland 插件产生的通知。手表分别持久化开关；内置课程、天气等通知不会被值 7 重复转发。

自定义通知的标题与正文均可留空：标题留空时插件统一显示默认标题 `RemoteCI 通知`（仍会按上述规则添加前缀），正文留空时保持为空，由 ClassIsland 只显示标题。通知标题是否添加 `由用户名发送：` 前缀由服务端全局设置 `forceSenderInTitle` 决定（WebUI 通知页开关，默认开启）。开启时插件最终执行会把标题格式化为 `由用户名发送：原标题`；关闭时不添加前缀。发送者名称取自已认证账号的 `displayName`（界面称“用户名”），而 `username` 是唯一登录 ID；服务端转发命令时会按全局设置覆盖客户端请求中的署名标志，客户端不能绕过。设置变更时服务端通过 `settings_sync` 推送在线手表，手表通知页据此决定是否显示“将显示发送人”提示。通知请求还可通过 `isNotificationEffectEnabled`、`isNotificationSoundEnabled` 和 `isSpeechEnabled` 分别控制 ClassIsland 的提醒强调特效、提醒音效和语音朗读；省略时均为关闭。

控制命令值 3 为清除当前 ClassIsland 提醒，值 4 通过 `mainMenuVisible` 设置主界面显隐，值 5 通过 `powerAction` 选择关机、重启、睡眠或休眠。值 6 通过 `volume.level` 设置 Windows 默认播放设备的 0-100 主音量，或通过 `volume.muted` 设置静音状态；WebUI 在静音状态下向高调节时会在同一命令中同时发送 `level` 与 `muted: false`。休眠入口只在插件状态报告 Windows 已启用休眠时显示。

`extensions_sync` 的载荷是其他 ClassIsland 插件通过 RemoteCI 注册的扩展功能列表；云端服务端和插件局域网服务都会缓存最近一次清单，并在手表完成认证后主动补发。命令值 7 为 `RunExtension`，命令值 8 为 `TeacherComing`；后者由插件完成显示“老师来了”、等待 1 秒和清除提醒的完整流程。通过 `extensionId` 指定目标扩展，`extensionArgs` 携带参数字典（值统一为字符串）。扩展调用必须同时通过独立的 `RunExtensions` 权限和管理员为该扩展设置的启用/普通账号开放策略；`RequiredPermission` 只作为旧扩展兼容字段传输，不再关联通知、电源等权限。账号的 `allowedExtensionIds` 与 `visibleExtensionIds` 随认证状态和 `account_sync` 下发，后者只控制自己的手表入口。未注册、缺少必填参数或权限不足时分别返回 `INVALID_REQUEST` / `FORBIDDEN`，执行异常统一返回 `INTERNAL_ERROR`。

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

插件发现修订号已变化时返回 `SCHEDULE_STALE` 和最新修订号，不覆盖别人刚完成的修改。

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

WebUI 通过 `POST /Control?handler=VoiceMessage` 上传 `application/octet-stream` 原始 PCM，携带 Cookie 和 `X-CSRF-TOKEN` 防伪头；服务端在内存中转为同一命令并等待插件回执。手表通过已认证的云端或局域网 WebSocket 发送。所有接入端覆盖 `requestedBy`，插件使用认证账号的 `displayName` 生成“来自xxx的语音消息”，不受文本通知署名开关影响。

Windows 插件自动播放录音，并显示上述 ClassIsland 通知：强调特效开启、通知音效关闭、朗读关闭（仍遵循宿主全局提醒限制）。底部浮窗上滑出现，拖动标题可移动；顶部显示发送人和总时长，中间使用 ClassIsland 原生 Slider 拖动播放位置，底部三个无底色的 ClassIsland Fluent 图标分开放置，分别用于暂停/继续、后退 5 秒和关闭，关闭图标使用系统危险色，播完后可重新播放。浮窗使用宿主 Fluent 浮层背景、描边、圆角、深浅色主题和 UI 字体；点击浮窗外部也会关闭并停止播放。关闭或插件停止时立即释放音频资源。已有浮窗时返回 `BUSY`，不排队、不打断现有语音；离线不缓存重发。成功回执表示播放器已启动，不表示整条语音已经播放完毕。音频不写入文件或通知历史，广播事件仅包含发送人提示。

## REST API

主要端点：

- `POST /api/plugin/pair`
- `POST /api/auth/login`、`/api/auth/refresh`、`/api/auth/logout`
- `GET /api/me`、`POST /api/me/password`
- `GET/DELETE /api/me/sessions`
- `GET /api/state`、`GET /api/schedule`
- `POST /api/commands`
- `GET/POST/PUT/DELETE /api/users`
- `POST /api/users/{id}/password`
- `POST /api/plugin/pairing-code`
- `GET /api/admin/status`

REST 手表请求使用 `Authorization: Bearer <accessToken>`。Razor WebUI 使用 HttpOnly、SameSite Cookie 和表单防伪令牌，不把令牌放入浏览器存储。



## 自定义角色兼容

服务端自定义角色在现有协议中仍以 `UserRole.User` 传输，并通过 `roleId`、`roleName` 可选字段提供管理信息。插件与旧版手表可以忽略新增字段，授权仍以 `effectivePermissions` 为准。


插件每次云端 WebSocket 首次连接或重连成功后，都会重新发送当前 `extensions_sync` 快照。这保证其他 ClassIsland 插件在云端连接建立前已注册的扩展功能，也能进入服务端缓存并显示在 WebUI 控制页。服务端首次发现扩展时默认启用但不向非管理员开放；管理员可在控制页逐项编辑。策略和个人手表展示偏好持久化到 SQLite 并进入授权镜像，插件局域网服务据此向每个已认证手表发送对应清单。
