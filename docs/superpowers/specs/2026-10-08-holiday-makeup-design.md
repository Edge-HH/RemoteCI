# 调休自动适配设计

- 日期：2026-10-08
- 状态：待审阅
- 分支：`worktree-holiday-makeup`（独立 worktree，不触碰并行进行中的档案管理改动）

## 1. 目标

RemoteCI 自动获取中国法定节假日与调休安排，并在每台教室电脑的 ClassIsland 上：

1. **放假日**关闭课表（不显示课程、不触发上下课提醒）；
2. **调休上学日**（原本是周末但要上学）开启一个临时课表，内容为该天应上的某个工作日的课。

成功标准：

- 管理员无需手动操作，2026 年国庆（9/20 周日上学、10/1–10/7 放假、10/10 周六上学）这类安排能自动生效；
- 管理员可在 WebUI / REST API / AstrBot 中把某个调休上学日改成上别的周几，或设为不补课；
- 插件断网时继续按最后一次收到的日历生效；
- 不覆盖教师/管理员在 ClassIsland 中的手动设置（手动关掉的课表、手动安排的预定课表）。

非目标（本次不做）：按班级单独开关；学校自定义假日（运动会、寒暑假）；插件在不连服务器时自行拉取数据。

## 2. 术语

| 术语 | 含义 |
| --- | --- |
| 放假日（off day） | 数据源中 `isOffDay=true` 的日期。只有落在周一至周五的放假日会对课表产生影响。 |
| 调休上学日（makeup day） | 数据源中 `isOffDay=false` 的日期，一定落在周六或周日。 |
| 补课周几（follow weekday） | 调休上学日要上的那一个工作日的课（周一至周五）。 |
| 调休日历（holiday calendar） | 服务器计算好、下发给插件的最终结果：未来一段时间内的放假日与调休上学日，后者带最终的补课周几。 |

## 3. 数据源

使用 [NateScarlet/holiday-cn](https://github.com/NateScarlet/holiday-cn)，该仓库根据国务院公告自动更新，每年一个文件：

```json
{ "year": 2026, "papers": ["..."], "days": [
  { "name": "国庆节", "date": "2026-09-20", "isOffDay": false },
  { "name": "国庆节", "date": "2026-10-01", "isOffDay": true }
] }
```

- 默认地址模板按顺序尝试：
  1. `https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{year}.json`
  2. `https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{year}.json`
- 管理员可设置自定义地址模板（必须包含 `{year}`、必须是 https）。设置后只使用这个地址，便于以后改用自己维护的同格式仓库。
- 每次拉取当年和次年的文件；次年返回 404 时视为"尚未发布"，不算错误。
- 解析时只认 `days[].name/date/isOffDay` 这三个字段；`date` 必须是合法的 `yyyy-MM-dd`，并且属于请求的年份或相邻年份，否则丢弃整份文件（视为拉取失败）。单个文件大小上限为 256 KiB。

## 4. 服务器

### 4.1 存储（EF Core 迁移）

- `SystemMetadata` 新增字段：
  - `HolidayCalendarEnabled`（bool，默认 `true`）
  - `HolidaySourceUrlTemplate`（string?，为 null 时使用默认源）
- 新表 `HolidayYearSnapshots`：`Year`（主键）、`RawJson`、`SourceUrl`、`FetchedAt`。只保存最近一次拉取成功的结果。
- 新表 `HolidayMakeupOverrides`：`Date`（主键，DateOnly）、`FollowWeekday`（int?，1–5 对应周一至周五，null 表示"不补课"）、`UpdatedByUserId`、`UpdatedAt`。
- 最近一次刷新的结果（成功时间、失败原因）只保存在内存里，用于页面显示。

> 迁移冲突：并行进行中的档案管理改动也会新增迁移并修改 `AppDbContextModelSnapshot`。本分支合并前要先同步到包含该改动的 main，再删掉本分支的迁移并重新生成。

### 4.2 `HolidayCalendarService`

职责：拉取、解析、推算、组装调休日历。推算逻辑是纯函数（`HolidayCalendarBuilder`），可以单独做单元测试。

**补课周几自动推算**（每个年份文件内部，按 `name` 分组）：

1. 把这一组的调休上学日按日期升序排列，记为 M₁…Mₙ；
2. 把这一组落在周一至周五的放假日按日期升序排列，取最后 n 个，记为 W₁…Wₙ；
3. Mᵢ 的补课周几就是 Wᵢ 的星期。如果工作日放假日不足 n 个，多出来的调休上学日标记为"无法推算"，不会自动开课，并在页面上提示管理员手动指定。

2026 年数据的推算结果（作为单元测试用例）：

| 调休上学日 | 假期 | 补课周几 |
| --- | --- | --- |
| 01-04（周日） | 元旦 | 周五（对应 01-02） |
| 02-14（周六） | 春节 | 周五（对应 02-20） |
| 02-28（周六） | 春节 | 周一（对应 02-23） |
| 05-09（周六） | 劳动节 | 周二（对应 05-05） |
| 09-20（周日） | 国庆节 | 周二（对应 10-06） |
| 10-10（周六） | 国庆节 | 周三（对应 10-07） |

**覆盖优先级**：手动覆盖 > 自动推算。覆盖记录对应的日期在当前数据源里已经不是调休上学日时，覆盖记录保留但不生效，页面上标注"已失效"。

**调休日历**：取今天往前 1 天到往后 60 天的范围（按服务器本地时区），包含：

```json
{
  "enabled": true,
  "generatedAt": "2026-10-08T08:00:00+08:00",
  "days": [
    { "date": "2026-10-10", "kind": "makeup", "name": "国庆节", "followWeekday": 3, "followSource": "auto" },
    { "date": "2026-10-01", "kind": "off", "name": "国庆节" }
  ]
}
```

- `kind`：`off` / `makeup`。
- `followWeekday`：1–5，或 null（不补课/无法推算）。
- `followSource`：`auto` / `manual` / `skip` / `unresolved`。
- `enabled=false` 时 `days` 为空数组。插件收到后会撤销之前做过的所有调整。

### 4.3 后台刷新 `HolidayCalendarWorker`

- 启动后立即刷新，之后每 12 小时刷新一次；管理员也可以手动触发刷新。
- 拉取超时为 15 秒。失败时保留数据库中的旧快照。
- 以下情况会重新组装日历，并推送给所有在线、且声明了能力的插件：刷新后日历内容有变化、设置变化、覆盖变化、跨天（每天 00:05 推送一次，使 60 天窗口向前滚动）。

### 4.4 协议

- 新消息类型 `Protocol.MessageTypeHolidayCalendar = "holiday_calendar"`（服务器→插件），payload 即 4.2 中的日历 JSON（`HolidayCalendar` 模型，放在 `shared/RemoteCI.Shared/Models/HolidayModels.cs`）。
- 新能力 `RemoteCiCapabilities.HolidayCalendar = "schedule.holiday-calendar"`。服务器只向声明了这个能力的插件发送。
- 插件完成认证后，服务器立即发送一次当前日历。
- 协议主版本号（`Protocol.Version = 3`）保持不变。旧插件不声明这个能力，所以不会收到该消息。
- `ScheduleDay` 新增两个可选字段（缺省时不序列化，旧客户端忽略）：
  - `dayKind`：`"holiday"` / `"makeup"`，普通日不输出；
  - `holidayName`：假期名。

### 4.5 REST API

| 方法 | 路径 | 权限 | 说明 |
| --- | --- | --- | --- |
| GET | `/api/holidays` | 已登录（ViewCurrentCourse） | 返回开关、数据源、上次刷新状态、未来 60 天的放假日和调休上学日（含推算值与覆盖值） |
| PUT | `/api/admin/holidays/settings` | 管理员 | `{ enabled, sourceUrlTemplate }` |
| PUT | `/api/admin/holidays/overrides/{date}` | 管理员 | `{ followWeekday: 1-5 \| null }`，null 表示不补课；date 必须是当前数据中的调休上学日 |
| DELETE | `/api/admin/holidays/overrides/{date}` | 管理员 | 恢复自动推算 |
| POST | `/api/admin/holidays/refresh` | 管理员 | 立即拉取并返回刷新结果 |

错误码沿用现有 `ApiErrorCodes` 风格；无效日期或非调休上学日返回 400。

### 4.6 WebUI

- 新增管理员页 `/Holidays`（「调休」），在系统设置导航中添加入口：
  - 总开关、数据源地址（留空使用默认源）、上次成功刷新时间与错误信息、「立即刷新」按钮；
  - 接下来的假期列表：每个假期显示放假日期范围，以及其中每个调休上学日一行，下拉框可选「自动（周X）/ 周一…周五 / 不补课」；无法推算的行标红。
- 课表页（`Schedule` / `MySchedule`）读取 `dayKind`：放假日显示「放假 · 国庆节」，调休上学日显示「调休 · 国庆节」。

### 4.7 备份

设置与覆盖记录要纳入 `ConfigurationArchiveService` 快照。由于该文件正在被并行改动，这一项放在同步 main 之后实施（快照版本号与对方协调后递增）。

## 5. 插件

### 5.1 日历接收与缓存

- `CloudClient` 收到 `holiday_calendar` 后，交给 `HolidayScheduleApplier`。
- 日历以 JSON 写入插件配置目录下的 `holiday-calendar.json`（先写临时文件再替换，避免写一半断电损坏）；启动时读取，断网也能生效。
- 插件在 `RemoteCiCapabilities.Current` 中声明 `schedule.holiday-calendar`。

### 5.2 `HolidayScheduleApplier`

在三种时机运行：插件启动并初始化完成后、收到新日历后、本地日期变化时（每分钟检查一次日期）。所有 ClassIsland 写操作都在 UI 线程上执行（沿用现有调度方式），并通过防腐层接口完成，以便单元测试：

```csharp
public interface IHolidayHostOperations
{
    bool? IsClassPlanEnabled { get; set; }   // 宿主没有该设置时返回 null
    ClassPlan? GetClassPlan(DateTime date, out Guid? planId);
    bool HasOrderedSchedule(DateTime date, out Guid? planId);
    Guid? CreateTempClassPlan(Guid sourcePlanId, DateTime date);
    void RemoveOrderedSchedule(DateTime date, Guid planId); // 同时删除对应的临时课表
    void SaveProfile();
}
```

插件自身的持久状态（`holiday-state.json`）：

```json
{ "disabledClassPlanOn": "2026-10-01", "makeupPlans": { "2026-10-10": { "planId": "<临时课表 guid>", "followWeekday": 3 } } }
```

**放假日——关闭课表**

- 今天是 `off`，且宿主 `IsClassPlanEnabled == true`：把它设为 false，并记录 `disabledClassPlanOn = 今天`。
- 今天不是 `off`，且 `disabledClassPlanOn` 有值：如果宿主当前是 false，就恢复成 true；然后清除这条记录。
- 只恢复插件自己关掉的开关。如果放假期间有人手动把课表打开，插件当天不会再关（记录照常清除）。
- 宿主没有 `IsClassPlanEnabled`（反射找不到）时，记录一条警告日志并跳过放假日处理；调休上学日的处理不受影响。

**调休上学日——开启临时课表**（今天起往后 7 天内）

1. 跳过 `followWeekday == null` 的日期。
2. 这一天已经有预定课表时：
   - 如果正是插件之前创建的（ID 与 `makeupPlans` 中记录的 `planId` 一致），而且补课周几没有变化，就跳过；
   - 如果是别人安排的，就不动，也不记录。
3. 计算源日期：以调休上学日所在周（周一为第一天）中对应 `followWeekday` 的那一天。例如 10-10（周六）补周三，源日期为 10-07。
4. `GetClassPlan(源日期)` 得到源课表（ClassIsland 会按规则匹配，多周轮换按源日期所在周计算）。拿不到就跳过并记录日志。
5. `CreateTempClassPlan(源课表Id, 调休上学日)` 创建临时课表，然后 `SaveProfile()`，并把临时课表 ID 和 `followWeekday` 记入 `makeupPlans`（后者用于第 2 步判断补课周几是否变化）。

**日历变化后的清理**

- 对 `makeupPlans` 中的每条记录：如果日期已过，就只删除记录；如果日期在未来，但新日历中这一天已经不是调休上学日、改成了不补课，或者补课周几变了，就在预定课表仍指向这份临时课表时调用 `RemoveOrderedSchedule` 删掉它，再删除记录（补课周几变了的情况会在同一轮里按新值重新创建）。
- 日历 `enabled=false`：撤销所有未来的临时课表，并按上面的规则恢复课表开关。

### 5.3 课表上报标记

`ScheduleCatalog.BuildDay` 读取当前缓存的日历：

- `off` 日：`Enabled = false`，`Courses = []`，`DayKind = "holiday"`，`HolidayName` 为假期名；
- `makeup` 日：保留实际课表，`DayKind = "makeup"`，`HolidayName` 为假期名；
- 两个新字段都参与修订号计算。

这样放假日在 WebUI 和手机上显示为不可编辑。换课流程原本就会拒绝 `Enabled = false` 的日期。

## 6. 智能体入口与文档

- `skills/remoteci`：在 references 中新增调休章节（查询、改补课周几、刷新），并在 SKILL.md 中加入口说明。
- AstrBot 插件（`D:\Files\Codes\Projects\astrbot_plugin_remoteci`）：同步复制 skill；新增指令 `/调休`（列出接下来的假期和补课安排），以及两个 LLM 工具：查询调休、设置某个调休上学日的补课周几（需要管理员 API Key）。
- `docs/protocol.md`：补充 `holiday_calendar` 消息、能力和 `ScheduleDay` 新字段。该文件目前有并行改动未提交，这部分在同步 main 后再写入。
- RemoteCI-Docs 文档站：新增「调休」使用说明页，并更新 API 页。

## 7. 错误处理汇总

| 情况 | 行为 |
| --- | --- |
| 数据源全部不可达或解析失败 | 保留旧快照，页面显示错误，日历照常基于旧快照组装 |
| 从未成功拉取过 | 日历为空，插件不做任何调整 |
| 推算失败（工作日放假日不足） | 该日 `unresolved`，不自动开课，页面标红 |
| 插件找不到源课表 | 跳过该日，记录日志，下次运行重试 |
| 宿主不支持 `IsClassPlanEnabled` | 跳过放假日处理，记录警告 |
| `SaveProfile` 失败 | 删除刚创建的预定课表，不写入状态，下次运行重试 |

## 8. 测试

- 服务器单元测试：解析（合法文件、坏日期、超大文件）；用 2026 年真实数据验证第 4.2 节表格中的推算结果；覆盖优先级与失效覆盖；日历窗口裁剪；`enabled=false`。
- 服务器集成测试：REST API 的权限（非管理员写入返回 403）、覆盖读写往返；插件认证后收到 `holiday_calendar`（仅限声明了能力的插件）。
- 插件单元测试（使用假的 `IHolidayHostOperations`）：放假日关闭并恢复课表开关；不恢复手动关闭的开关；调休上学日只创建一次临时课表；不覆盖他人安排的预定课表；补课周几变化或改为不补课时的清理；`enabled=false` 时全部撤销；`ScheduleCatalog` 的标记与修订号变化。
- 验证在临时快照目录中构建和测试（并行会话共用工作区时，直接在仓库里 `dotnet build` 可能卡住）。
