# 2026-10 安全影响说明：网页登录票据、通知限幅与跨端一致性

本文记录 `e086b6b` 审查后修复批次的安全影响、验证证据与待人工确认事项。协议细节以 [protocol.md](protocol.md) 为准，用户文档见 RemoteCI-Docs 的 `src/server/api.md`（`/api/auth/web-ticket`）与 `src/guide/features.md`（自定义通知、Android 桌面小组件）。

## 1. 网页登录票据 `/api/auth/web-ticket`

**能力**：已登录的手机端用设备会话换取一次性票据，浏览器打开 `/WebLogin?t=…` 即以同一账号登录 WebUI。

| 风险 | 处理 |
| --- | --- |
| 票据泄露后被重放 | 1 分钟有效；兑换按数据库行原子删除，只能成功一次；同账号签发新票据作废旧票据 |
| 数据库泄露得到可用票据 | 只保存 SHA-256 摘要（票据为 192 位随机数，无需加盐） |
| 重启或多实例下行为不一致 | 票据持久化在 `WebLoginTickets` 表，跨实例兑换同样原子 |
| 签发后账号状态变化 | 兑换时复核启用、锁定、待设密码和安全戳（改密等操作会刷新安全戳） |
| 开放重定向 | `returnUrl` 只接受 `Url.IsLocalUrl`；`classId` 只接受账号可访问的班级 |
| 地址栏、缓存或 Referer 泄露 | 落地页 `Cache-Control: no-store`、`Referrer-Policy: no-referrer`，兑换后立即重定向 |
| API Key 被用来换浏览器会话 | API Key 调用返回 403，只接受设备会话令牌 |
| HTTP 部署下被截获 | **已知残余风险**：与手机端连接策略一致，服务端不拒绝 HTTP，文档要求生产环境使用 HTTPS |
| 登录 CSRF（攻击者把自己的票据链接发给受害者） | **已知残余风险**：受害者会登录到攻击者账号；票据 1 分钟过期，且落地页会替换原登录，影响有限 |

验证：`WebLoginTests`（一次性兑换、外部 returnUrl、只存摘要、过期、并发兑换恰好一次、新票据作废旧票据、停用账号、改密与安全戳轮换、共享数据库的跨实例兑换）。

## 2. 通知参数上限

`NotificationRequest.Validate` 由 WebSocket 命令、`POST /api/commands`、`POST /api/commands/broadcast`、WebUI 与设备批量控制共用（标题 60 字由插件截断，正文 500 字，时长 1-3600 秒，重复 1-10 次）。插件执行端对局域网直连请求再次校验，并对时长与重复次数限幅，防止恶意客户端让一条命令排队大量提醒。验证：`RemoteNotificationProviderTests`。

**顺带修复**：`POST /api/commands` 此前没有按全局 `forceSenderInTitle` 覆盖请求中的署名标志，REST 客户端可以在管理员开启“强制显示发送人”时发送匿名通知；现与 WebSocket 路径一致。

## 3. 局域网授权镜像与云端权限一致

`CreateSyncAsync` 对“老师角色且同时是班级成员”的账号，此前只计算班内角色权限，导致在 LAN 上的权限比云端少（会误拒，而不是越权）。现在与 `ClassAccessService.GetEffectivePermissionsAsync` 使用同一公式。验证：`TeacherBindingTests.CreateSyncAsync_TeacherAlsoClassMember_MatchesCloudEffectivePermissions`。

## 4. 手机缓存隐私

小组件缓存此前不记录归属，切换账号后仍可能展示上一账号的个人日程。现在缓存按服务器、账号与班级绑定，切换账号或服务器、退出登录、设备会话失效时清空，切换班级时丢弃上一班级的课堂与课表。旧版本不带归属的缓存在升级时删除。验证：`WidgetCacheTest`。

## 人工审核

- [ ] 网页登录票据流程与残余风险（HTTP、登录 CSRF）已由维护者确认
- [ ] 数据库迁移 `AddWebLoginTickets` 已在升级环境演练
- [ ] HyperOS 真机：小组件添加、曝光刷新与深色模式检查
