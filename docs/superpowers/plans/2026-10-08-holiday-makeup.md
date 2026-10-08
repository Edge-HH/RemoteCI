# 调休自动适配 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 服务器自动获取中国法定节假日与调休数据并下发给插件；插件在放假日关闭 ClassIsland 课表，在调休上学日为该天建立"补某个工作日的课"的临时课表。

**Architecture:** 服务器 `HolidayCalendarService` 从 holiday-cn 拉取年度 JSON 并存进 SQLite。纯函数 `HolidayCalendarBuilder` 推算每个调休上学日补周几，再叠加管理员的手动覆盖，组装出 60 天的调休日历，通过新消息 `holiday_calendar` 推送给声明了 `schedule.holiday-calendar` 能力的插件。插件把日历缓存到本地，由 `HolidayScheduleApplier` 在 UI 线程上通过防腐层 `IHolidayHostOperations` 切换宿主 `LessonsService.IsClassPlanEnabled`，并用 `CreateTempClassPlan` 建立或撤销调休临时课表；`ScheduleCatalog` 在上报课表时标记放假日和调休日。

**Tech Stack:** .NET 8（插件）/ .NET 10 ASP.NET Core + EF Core SQLite（服务器）、xUnit、ClassIsland PluginSdk 2.0、Python 3 + pytest（AstrBot 插件）、VuePress（文档站）。

**Spec:** `docs/superpowers/specs/2026-10-08-holiday-makeup-design.md`

## Global Constraints

- 所有代码改动都在 worktree `D:\Files\Codes\Projects\RemoteCI\.claude\worktrees\holiday-makeup`（分支 `worktree-holiday-makeup`）中完成，**不得**修改主工作区 `D:\Files\Codes\Projects\RemoteCI`（codex 正在那里开发档案管理）。
- 协议主版本号 `Protocol.Version = 3` 不变；新增消息类型 `"holiday_calendar"`，新增能力 `"schedule.holiday-calendar"`。
- `followWeekday` 取值 1–5，对应周一至周五（ISO 星期编号）；`null` 表示不补课或无法推算。
- `followSource` 取值 `"auto"` / `"manual"` / `"skip"` / `"unresolved"`；日历条目的 `kind` 取值 `"off"` / `"makeup"`；`ScheduleDay.dayKind` 取值 `"holiday"` / `"makeup"`。
- 默认数据源依次为 `https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{year}.json`、`https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{year}.json`；自定义模板必须是 https 地址并且包含 `{year}`，长度不超过 512。设置了自定义模板后只使用它。
- 单个年度文件不超过 256 KiB；拉取超时 15 秒；后台每 12 小时刷新一次。
- 调休日历的窗口为今天往前 1 天到往后 60 天；插件只为今天起 7 天内的调休上学日建立临时课表。
- 调休功能默认开启（`SystemMetadata.HolidayCalendarEnabled` 默认 `true`，迁移中的 `defaultValue` 必须是 `true`）。
- 插件文件名：`HolidayCalendar.json`（缓存的日历）、`HolidayState.json`（插件建立的临时课表），都放在插件配置目录。
- 构建和测试一律带 `-nodeReuse:false`（多个会话并行时 MSBuild 节点会互相卡住）。
- 用户可见文字使用中文，代码注释风格沿用周围代码（中文、简短、解释"为什么"）。
- Commit message 使用 Conventional Commits 中文描述，并以 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` 结尾。

## Review Focus

1. **教师手动关掉的课表被插件意外打开**：放假日之后，插件只能恢复自己关掉的开关；如果放假当天有人手动重新打开课表，插件当天不能再关掉。由 Task 8 的 `OffDay_*` 测试锁定。
2. **调休临时课表重复创建，或者覆盖了别人的预定课表**：插件重启、反复收到同一份日历时，不能重复建立临时课表；如果那一天已经有别人安排的预定课表，插件不能碰它。由 Task 8 的 `Makeup_CreatesOnce_EvenAcrossInstances` 和 `Makeup_LeavesForeignOrderedScheduleAlone` 锁定。
3. **服务器离线或数据源被墙**：jsDelivr 失败时要回退到 raw，全部失败时要保留旧快照并显示错误；次年数据尚未发布（404）不算错误。由 Task 4 的 `Refresh_*` 测试锁定。
4. **管理员改了补课周几，或者改成不补课**：插件必须撤掉之前按旧周几建立的临时课表，并按新值重建。由 Task 8 的 `Makeup_WeekdayChangeRebuilds` 和 `Makeup_SkipRemovesOurPlan` 锁定。
5. **数据源返回畸形数据**（不是 JSON、日期非法、文件过大）：这次拉取要失败，不能写入快照，也不能清空已有日历。由 Task 2 的 `Parse_*` 测试和 Task 4 的 `Refresh_InvalidPayloadKeepsPreviousSnapshot` 锁定。

---

## File Structure

**Shared**（`shared/RemoteCI.Shared/`）
- Create `Models/HolidayModels.cs`：`HolidayCalendar`、`HolidayCalendarDay`、常量类 `HolidayDayKinds` / `HolidayFollowSources` / `ScheduleDayKinds`。
- Modify `Protocol.cs`：消息类型常量、能力常量，并把能力加入 `Current`。
- Modify `Models/Envelope.cs`：`Envelope.HolidayCalendar(...)` 工厂方法。
- Modify `Models/ScheduleModels.cs`：`ScheduleDay.DayKind`、`ScheduleDay.HolidayName`。

**Server**（`server/RemoteCI.Server/`）
- Create `Services/HolidayDataParser.cs`：解析并校验 holiday-cn 年度 JSON。
- Create `Services/HolidayCalendarBuilder.cs`：推算补课周几、叠加覆盖、裁剪日历窗口（纯函数）。
- Create `Data/HolidayEntities.cs`：`HolidayYearSnapshot`、`HolidayMakeupOverride`。
- Modify `Data/Entities.cs`（`SystemMetadata`）、`Data/AppDbContext.cs`；Create 迁移 `Data/Migrations/*_AddHolidayCalendar.cs`。
- Create `Services/HolidaySettingsStore.cs`：设置、覆盖、快照的读写（scoped）。
- Create `Services/HolidayCalendarService.cs`：拉取、状态、总览、发布（singleton），以及 `HolidayClock`、`HolidayOperationException`、总览 DTO。
- Create `Services/HolidayCalendarWorker.cs`：后台刷新与跨天发布。
- Modify `Services/PeerRegistry.cs`：按能力向所有插件广播。
- Modify `WebSocketHub.cs`：插件上报能力后发送当前日历。
- Modify `Services/ServerOptions.cs`：`HolidayAutoRefresh` 开关（测试关闭）。
- Create `HolidayEndpoints.cs`：REST API。
- Modify `Program.cs`：注册服务与端点（只加几行）。
- Create `Pages/Holidays.cshtml(.cs)`；Modify `Pages/Shared/_Layout.cshtml`（导航）、`Pages/Shared/_ScheduleTable.cshtml`（放假/调休标签）、`wwwroot/app.css`（标签样式）。

**Plugin**（`plugin/RemoteCI.Plugin/`）
- Create `Services/HolidayCalendarStore.cs`：日历缓存与按日期查询（`IHolidayCalendarLookup`），以及 `HolidayCalendarMessage` 解包。
- Create `Services/HolidayScheduleApplier.cs`：核心逻辑、`IHolidayHostOperations`、`HolidayStateFile`。
- Create `Services/ClassIslandHolidayHost.cs`：真实宿主适配（反射），以及 `ClassPlanSwitch`。
- Modify `Services/CloudClient.cs`（事件）、`Services/ScheduleCatalog.cs`（标记）、`Services/RemoteCiService.cs`（触发时机）、`Plugin.cs`（DI）。

**Tests**
- Server：`server/tests/RemoteCI.Server.Tests/HolidayTestData.cs`、`HolidayCalendarBuilderTests.cs`、`HolidaySettingsStoreTests.cs`、`HolidayCalendarServiceTests.cs`、`HolidayApiTests.cs`、`HolidayPageTests.cs`，以及在 `WebSocketRelayTests.cs` 中追加一个用例；Modify `TestWebApplicationFactory.cs`。
- Plugin：`plugin/tests/RemoteCI.Plugin.Tests/HolidayCalendarStoreTests.cs`、`HolidayScheduleApplierTests.cs`、`ClassIslandHolidayHostTests.cs`；在 `SchedulePipelineTests.cs` 中追加用例。

**Agent surfaces & docs**
- Create `skills/remoteci/references/holidays.md`；Modify `skills/remoteci/SKILL.md`、`docs/protocol.md`。
- AstrBot 插件（单独的 worktree）：Create `remoteci/makeup.py`、`tests/test_makeup.py`、`remoteci/skill/references/holidays.md`；Modify `remoteci/commands.py`、`main.py`、`remoteci/skill/SKILL.md`。
- RemoteCI-Docs（单独的 worktree）：Create `src/guide/holidays.md`；Modify `src/guide/features.md`、`src/server/api.md`。

---

### Task 1: 共享协议模型

**Files:**
- Create: `shared/RemoteCI.Shared/Models/HolidayModels.cs`
- Modify: `shared/RemoteCI.Shared/Protocol.cs`、`shared/RemoteCI.Shared/Models/Envelope.cs`、`shared/RemoteCI.Shared/Models/ScheduleModels.cs`
- Test: `server/tests/RemoteCI.Server.Tests/HolidayCalendarBuilderTests.cs`（这里先只放序列化用例）

**Interfaces:**
- Produces:
  - `HolidayCalendar { bool Enabled; DateTimeOffset GeneratedAt; List<HolidayCalendarDay> Days }`
  - `HolidayCalendarDay { string Date; string Kind; string Name; int? FollowWeekday; string? FollowSource }`
  - 常量 `HolidayDayKinds.Off/Makeup`、`HolidayFollowSources.Auto/Manual/Skip/Unresolved`、`ScheduleDayKinds.Holiday/Makeup`
  - `Protocol.MessageTypeHolidayCalendar`、`RemoteCiCapabilities.HolidayCalendar`、`Envelope.HolidayCalendar(HolidayCalendar)`
  - `ScheduleDay.DayKind`、`ScheduleDay.HolidayName`（均为 `string?`）

- [ ] **Step 1: Write the failing test**

Create `server/tests/RemoteCI.Server.Tests/HolidayCalendarBuilderTests.cs`:

```csharp
using System.Text.Json;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class HolidayCalendarBuilderTests
{
    [Fact]
    public void Calendar_SerializesWithProtocolFieldNames()
    {
        var envelope = Envelope.HolidayCalendar(new HolidayCalendar
        {
            Enabled = true,
            Days =
            [
                new HolidayCalendarDay { Date = "2026-10-10", Kind = HolidayDayKinds.Makeup, Name = "国庆节", FollowWeekday = 3, FollowSource = HolidayFollowSources.Auto },
                new HolidayCalendarDay { Date = "2026-10-01", Kind = HolidayDayKinds.Off, Name = "国庆节" },
            ],
        });

        var json = JsonSerializer.Serialize(envelope, JsonDefaults.Options);

        Assert.Contains("\"type\":\"holiday_calendar\"", json);
        Assert.Contains("\"followWeekday\":3", json);
        Assert.Contains("\"followSource\":\"auto\"", json);
        Assert.DoesNotContain("\"followWeekday\":null", json);
        Assert.Contains(RemoteCiCapabilities.HolidayCalendar, RemoteCiCapabilities.Current);
        Assert.DoesNotContain(RemoteCiCapabilities.HolidayCalendar, RemoteCiCapabilities.Baseline);
    }

    [Fact]
    public void ScheduleDay_OmitsHolidayFieldsOnNormalDays()
    {
        var normal = JsonSerializer.Serialize(new ScheduleDay { Date = "2026-10-09" }, JsonDefaults.Options);
        var holiday = JsonSerializer.Serialize(
            new ScheduleDay { Date = "2026-10-01", DayKind = ScheduleDayKinds.Holiday, HolidayName = "国庆节" }, JsonDefaults.Options);

        Assert.DoesNotContain("dayKind", normal);
        Assert.Contains("\"dayKind\":\"holiday\"", holiday);
        Assert.Contains("\"holidayName\":\"国庆节\"", holiday);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test server/tests/RemoteCI.Server.Tests -nodeReuse:false --filter "FullyQualifiedName~HolidayCalendarBuilderTests"`
Expected: 编译失败，提示 `HolidayCalendar`、`Envelope.HolidayCalendar` 等不存在。

- [ ] **Step 3: Write minimal implementation**

Create `shared/RemoteCI.Shared/Models/HolidayModels.cs`:

```csharp
using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>服务端下发给插件的调休日历：只含窗口内的放假日与调休上学日，补课周几已按覆盖规则算好。</summary>
public sealed class HolidayCalendar
{
    /// <summary>为 false 时 Days 为空，插件应撤销之前做过的全部调整。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("days")]
    public List<HolidayCalendarDay> Days { get; set; } = [];
}

public sealed class HolidayCalendarDay
{
    /// <summary>yyyy-MM-dd。</summary>
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    /// <summary><see cref="HolidayDayKinds"/>。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>调休上学日补的工作日（1=周一 … 5=周五）；null 表示不补课或无法推算。</summary>
    [JsonPropertyName("followWeekday")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FollowWeekday { get; set; }

    /// <summary><see cref="HolidayFollowSources"/>；仅调休上学日有值。</summary>
    [JsonPropertyName("followSource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FollowSource { get; set; }
}

public static class HolidayDayKinds
{
    public const string Off = "off";
    public const string Makeup = "makeup";
}

public static class HolidayFollowSources
{
    public const string Auto = "auto";
    public const string Manual = "manual";
    public const string Skip = "skip";
    public const string Unresolved = "unresolved";
}

/// <summary>插件上报课表时给特殊日期打的标记（<see cref="ScheduleDay.DayKind"/>）。</summary>
public static class ScheduleDayKinds
{
    public const string Holiday = "holiday";
    public const string Makeup = "makeup";
}
```

In `shared/RemoteCI.Shared/Protocol.cs`, after `MessageTypeUserNotify`:

```csharp
    /// <summary>服务端发给插件的调休日历（放假日与调休上学日），插件据此开关课表、建立调休临时课表。</summary>
    public const string MessageTypeHolidayCalendar = "holiday_calendar";
```

In `RemoteCiCapabilities`, after `TerminalExecute` and its neighbors (before `Baseline`):

```csharp
    /// <summary>接收调休日历并在放假日关闭课表、调休上学日建立临时课表。</summary>
    public const string HolidayCalendar = "schedule.holiday-calendar";
```

Append `HolidayCalendar` to the end of the `Current` collection expression (not `Baseline`).

In `shared/RemoteCI.Shared/Models/Envelope.cs`, after `AccountSync`:

```csharp
    public static Envelope HolidayCalendar(HolidayCalendar payload) =>
        New(Protocol.MessageTypeHolidayCalendar, payload);
```

In `shared/RemoteCI.Shared/Models/ScheduleModels.cs`, inside `ScheduleDay` after `Courses`:

```csharp
    /// <summary>放假日为 "holiday"、调休上学日为 "makeup"，普通日不输出；旧版插件不下发。</summary>
    [JsonPropertyName("dayKind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DayKind { get; set; }

    /// <summary>放假日或调休上学日所属的假期名，例如“国庆节”。</summary>
    [JsonPropertyName("holidayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HolidayName { get; set; }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test server/tests/RemoteCI.Server.Tests -nodeReuse:false --filter "FullyQualifiedName~HolidayCalendarBuilderTests"`
Expected: 2 passed.

- [ ] **Step 5: Run the existing capability tests**

Run: `dotnet test plugin/tests/RemoteCI.Plugin.Tests -nodeReuse:false --filter "FullyQualifiedName~PluginCompatibility|FullyQualifiedName~LanServer"` 和 `dotnet test server/tests/RemoteCI.Server.Tests -nodeReuse:false --filter "FullyQualifiedName~WebSocketRelay"`
Expected: 全部通过。如果有测试断言了 `Current` 的确切内容，就把 `schedule.holiday-calendar` 加进它的期望值。

- [ ] **Step 6: Commit**

```bash
git add shared server/tests/RemoteCI.Server.Tests/HolidayCalendarBuilderTests.cs
git commit -m "feat(protocol): 新增调休日历消息、能力与课表日标记"
```

---

### Task 2: 数据解析与补课周几推算（纯函数）

**Files:**
- Create: `server/RemoteCI.Server/Services/HolidayDataParser.cs`、`server/RemoteCI.Server/Services/HolidayCalendarBuilder.cs`
- Create: `server/tests/RemoteCI.Server.Tests/HolidayTestData.cs`
- Test: `server/tests/RemoteCI.Server.Tests/HolidayCalendarBuilderTests.cs`

**Interfaces:**
- Consumes: Task 1 模型。
- Produces:
  - `record HolidayEntry(DateOnly Date, string Name, bool IsOffDay)`
  - `record HolidayOverride(DateOnly Date, int? FollowWeekday)`
  - `record ResolvedHolidayDay(int Year, DateOnly Date, string Kind, string Name, int? AutoWeekday, int? FollowWeekday, string? FollowSource)`
  - `static IReadOnlyList<HolidayEntry> HolidayDataParser.Parse(int year, string json)`（失败抛 `InvalidDataException`）；`const int HolidayDataParser.MaxBytes = 256 * 1024`
  - `static int HolidayCalendarBuilder.IsoWeekday(DateOnly)`
  - `static IReadOnlyList<ResolvedHolidayDay> HolidayCalendarBuilder.Resolve(IReadOnlyDictionary<int, IReadOnlyList<HolidayEntry>> years, IReadOnlyCollection<HolidayOverride> overrides)`
  - `static HolidayCalendar HolidayCalendarBuilder.ToCalendar(IReadOnlyList<ResolvedHolidayDay> days, bool enabled, DateOnly today, DateTimeOffset generatedAt)`
  - `const int WindowPastDays = 1, WindowFutureDays = 60`
  - 测试数据 `HolidayTestData.Json2026`

- [ ] **Step 1: Add the 2026 fixture**

Create `server/tests/RemoteCI.Server.Tests/HolidayTestData.cs`（内容取自 holiday-cn 2026.json 的 `days` 部分）：

```csharp
namespace RemoteCI.Server.Tests;

internal static class HolidayTestData
{
    public const string Json2026 = """
    {"year":2026,"papers":[],"days":[
    {"name":"元旦","date":"2026-01-01","isOffDay":true},
    {"name":"元旦","date":"2026-01-02","isOffDay":true},
    {"name":"元旦","date":"2026-01-03","isOffDay":true},
    {"name":"元旦","date":"2026-01-04","isOffDay":false},
    {"name":"春节","date":"2026-02-14","isOffDay":false},
    {"name":"春节","date":"2026-02-15","isOffDay":true},
    {"name":"春节","date":"2026-02-16","isOffDay":true},
    {"name":"春节","date":"2026-02-17","isOffDay":true},
    {"name":"春节","date":"2026-02-18","isOffDay":true},
    {"name":"春节","date":"2026-02-19","isOffDay":true},
    {"name":"春节","date":"2026-02-20","isOffDay":true},
    {"name":"春节","date":"2026-02-21","isOffDay":true},
    {"name":"春节","date":"2026-02-22","isOffDay":true},
    {"name":"春节","date":"2026-02-23","isOffDay":true},
    {"name":"春节","date":"2026-02-28","isOffDay":false},
    {"name":"清明节","date":"2026-04-04","isOffDay":true},
    {"name":"清明节","date":"2026-04-05","isOffDay":true},
    {"name":"清明节","date":"2026-04-06","isOffDay":true},
    {"name":"劳动节","date":"2026-05-01","isOffDay":true},
    {"name":"劳动节","date":"2026-05-02","isOffDay":true},
    {"name":"劳动节","date":"2026-05-03","isOffDay":true},
    {"name":"劳动节","date":"2026-05-04","isOffDay":true},
    {"name":"劳动节","date":"2026-05-05","isOffDay":true},
    {"name":"劳动节","date":"2026-05-09","isOffDay":false},
    {"name":"端午节","date":"2026-06-19","isOffDay":true},
    {"name":"端午节","date":"2026-06-20","isOffDay":true},
    {"name":"端午节","date":"2026-06-21","isOffDay":true},
    {"name":"国庆节","date":"2026-09-20","isOffDay":false},
    {"name":"中秋节","date":"2026-09-25","isOffDay":true},
    {"name":"中秋节","date":"2026-09-26","isOffDay":true},
    {"name":"中秋节","date":"2026-09-27","isOffDay":true},
    {"name":"国庆节","date":"2026-10-01","isOffDay":true},
    {"name":"国庆节","date":"2026-10-02","isOffDay":true},
    {"name":"国庆节","date":"2026-10-03","isOffDay":true},
    {"name":"国庆节","date":"2026-10-04","isOffDay":true},
    {"name":"国庆节","date":"2026-10-05","isOffDay":true},
    {"name":"国庆节","date":"2026-10-06","isOffDay":true},
    {"name":"国庆节","date":"2026-10-07","isOffDay":true},
    {"name":"国庆节","date":"2026-10-10","isOffDay":false}
    ]}
    """;

    public static IReadOnlyDictionary<int, IReadOnlyList<RemoteCI.Server.Services.HolidayEntry>> Years2026() =>
        new Dictionary<int, IReadOnlyList<RemoteCI.Server.Services.HolidayEntry>>
        {
            [2026] = RemoteCI.Server.Services.HolidayDataParser.Parse(2026, Json2026),
        };
}
```

- [ ] **Step 2: Write the failing tests**

Append to `HolidayCalendarBuilderTests`:

```csharp
    [Theory]
    [InlineData("2026-01-04", 5)]
    [InlineData("2026-02-14", 5)]
    [InlineData("2026-02-28", 1)]
    [InlineData("2026-05-09", 2)]
    [InlineData("2026-09-20", 2)]
    [InlineData("2026-10-10", 3)]
    public void Resolve_PairsMakeupDaysWithLastOffWeekdays(string date, int weekday)
    {
        var days = HolidayCalendarBuilder.Resolve(HolidayTestData.Years2026(), []);

        var day = Assert.Single(days, x => x.Date == DateOnly.Parse(date));
        Assert.Equal(HolidayDayKinds.Makeup, day.Kind);
        Assert.Equal(weekday, day.AutoWeekday);
        Assert.Equal(weekday, day.FollowWeekday);
        Assert.Equal(HolidayFollowSources.Auto, day.FollowSource);
    }

    [Fact]
    public void Resolve_OverridesWinAndNullMeansSkip()
    {
        var days = HolidayCalendarBuilder.Resolve(HolidayTestData.Years2026(),
        [
            new HolidayOverride(new DateOnly(2026, 10, 10), 5),
            new HolidayOverride(new DateOnly(2026, 9, 20), null),
        ]);

        var oct10 = Assert.Single(days, x => x.Date == new DateOnly(2026, 10, 10));
        Assert.Equal((3, 5, HolidayFollowSources.Manual), (oct10.AutoWeekday!.Value, oct10.FollowWeekday!.Value, oct10.FollowSource));
        var sep20 = Assert.Single(days, x => x.Date == new DateOnly(2026, 9, 20));
        Assert.Null(sep20.FollowWeekday);
        Assert.Equal(HolidayFollowSources.Skip, sep20.FollowSource);
    }

    [Fact]
    public void Resolve_MarksMakeupUnresolvedWhenNoOffWeekdayToPair()
    {
        var entries = new Dictionary<int, IReadOnlyList<HolidayEntry>>
        {
            [2026] = [new(new DateOnly(2026, 3, 7), "测试假", false), new(new DateOnly(2026, 3, 8), "测试假", true)],
        };

        var makeup = Assert.Single(HolidayCalendarBuilder.Resolve(entries, []), x => x.Kind == HolidayDayKinds.Makeup);

        Assert.Null(makeup.FollowWeekday);
        Assert.Equal(HolidayFollowSources.Unresolved, makeup.FollowSource);
    }

    [Fact]
    public void ToCalendar_KeepsOnlyWindowAndEmptiesWhenDisabled()
    {
        var days = HolidayCalendarBuilder.Resolve(HolidayTestData.Years2026(), []);
        var today = new DateOnly(2026, 10, 2);

        var calendar = HolidayCalendarBuilder.ToCalendar(days, enabled: true, today, DateTimeOffset.UnixEpoch);
        var disabled = HolidayCalendarBuilder.ToCalendar(days, enabled: false, today, DateTimeOffset.UnixEpoch);

        Assert.Equal("2026-10-01", calendar.Days.Min(x => x.Date));
        Assert.Contains(calendar.Days, x => x is { Date: "2026-10-10", Kind: HolidayDayKinds.Makeup, FollowWeekday: 3 });
        Assert.DoesNotContain(calendar.Days, x => x.Date == "2026-09-20");
        Assert.False(disabled.Enabled);
        Assert.Empty(disabled.Days);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"days":[{"name":"国庆节","date":"2026-13-01","isOffDay":true}]}""")]
    [InlineData("""{"days":[{"name":"国庆节","date":"2031-10-01","isOffDay":true}]}""")]
    [InlineData("""{"days":[{"name":"国庆节","date":"2026-10-01"}]}""")]
    [InlineData("""{"year":2026}""")]
    public void Parse_RejectsMalformedPayloads(string json)
    {
        Assert.Throws<InvalidDataException>(() => HolidayDataParser.Parse(2026, json));
    }

    [Fact]
    public void Parse_RejectsOversizedPayload()
    {
        var json = "{\"days\":[],\"pad\":\"" + new string('x', HolidayDataParser.MaxBytes) + "\"}";
        Assert.Throws<InvalidDataException>(() => HolidayDataParser.Parse(2026, json));
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test server/tests/RemoteCI.Server.Tests -nodeReuse:false --filter "FullyQualifiedName~HolidayCalendarBuilderTests"`
Expected: 编译失败，`HolidayCalendarBuilder` / `HolidayDataParser` 不存在。

- [ ] **Step 4: Implement the parser**

Create `server/RemoteCI.Server/Services/HolidayDataParser.cs`:

```csharp
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RemoteCI.Server.Services;

/// <summary>holiday-cn 年度文件中的一天。</summary>
public sealed record HolidayEntry(DateOnly Date, string Name, bool IsOffDay);

/// <summary>
/// 解析 NateScarlet/holiday-cn 的年度 JSON。任何一个条目不合法都视为整份文件不可信并拒绝，
/// 避免把半截或被篡改的数据写进快照、进而错误地关闭或开启教室课表。
/// </summary>
public static class HolidayDataParser
{
    public const int MaxBytes = 256 * 1024;
    private const int MaxNameLength = 20;

    public static IReadOnlyList<HolidayEntry> Parse(int year, string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
            throw new InvalidDataException($"{year} 年节假日数据超过 256 KiB");
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("days", out var days) ||
                days.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{year} 年节假日数据缺少 days 数组");
            return days.EnumerateArray().Select(item => ParseDay(year, item)).ToList();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{year} 年节假日数据不是有效的 JSON", ex);
        }
    }

    private static HolidayEntry ParseDay(int year, JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
            name.GetString()!.Trim() is { Length: > 0 and <= MaxNameLength } trimmed &&
            item.TryGetProperty("date", out var date) && date.ValueKind == JsonValueKind.String &&
            DateOnly.TryParseExact(date.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) &&
            Math.Abs(day.Year - year) <= 1 &&
            item.TryGetProperty("isOffDay", out var off) && off.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return new HolidayEntry(day, trimmed, off.GetBoolean());
        throw new InvalidDataException($"{year} 年节假日数据包含无效条目：{item.GetRawText()[..Math.Min(item.GetRawText().Length, 80)]}");
    }
}
```

- [ ] **Step 5: Implement the builder**

Create `server/RemoteCI.Server/Services/HolidayCalendarBuilder.cs`:

```csharp
using System.Globalization;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>管理员对某个调休上学日的手动安排；FollowWeekday 为 null 表示不补课。</summary>
public sealed record HolidayOverride(DateOnly Date, int? FollowWeekday);

/// <summary>叠加推算与覆盖后的一天；AutoWeekday 始终是自动推算值，便于页面同时展示两者。</summary>
public sealed record ResolvedHolidayDay(
    int Year, DateOnly Date, string Kind, string Name, int? AutoWeekday, int? FollowWeekday, string? FollowSource);

public static class HolidayCalendarBuilder
{
    public const int WindowPastDays = 1;
    public const int WindowFutureDays = 60;

    /// <summary>1=周一 … 7=周日。</summary>
    public static int IsoWeekday(DateOnly date) => ((int)date.DayOfWeek + 6) % 7 + 1;

    /// <summary>
    /// 同一年度文件中按假期名分组：调休上学日按日期升序，依次对应该假期最后 N 个落在工作日的放假日，
    /// 补那一天的星期。工作日放假日不够配对时标为 unresolved，交给管理员手动指定。
    /// </summary>
    public static IReadOnlyList<ResolvedHolidayDay> Resolve(
        IReadOnlyDictionary<int, IReadOnlyList<HolidayEntry>> years,
        IReadOnlyCollection<HolidayOverride> overrides)
    {
        var manual = overrides.ToDictionary(x => x.Date);
        var seen = new HashSet<DateOnly>();
        var result = new List<ResolvedHolidayDay>();
        foreach (var (year, entries) in years.OrderBy(x => x.Key))
        {
            foreach (var group in entries.GroupBy(x => x.Name, StringComparer.Ordinal))
            {
                var makeups = group.Where(x => !x.IsOffDay).OrderBy(x => x.Date).ToList();
                var offWeekdays = group.Where(x => x.IsOffDay && IsoWeekday(x.Date) <= 5).OrderBy(x => x.Date).ToList();
                var paired = offWeekdays.Skip(Math.Max(0, offWeekdays.Count - makeups.Count)).ToList();
                for (var i = 0; i < makeups.Count; i++)
                {
                    var date = makeups[i].Date;
                    if (!seen.Add(date)) continue;
                    int? auto = i < paired.Count ? IsoWeekday(paired[i].Date) : null;
                    var (follow, source) = manual.TryGetValue(date, out var chosen)
                        ? (chosen.FollowWeekday, chosen.FollowWeekday is null ? HolidayFollowSources.Skip : HolidayFollowSources.Manual)
                        : (auto, auto is null ? HolidayFollowSources.Unresolved : HolidayFollowSources.Auto);
                    result.Add(new ResolvedHolidayDay(year, date, HolidayDayKinds.Makeup, group.Key, auto, follow, source));
                }
                foreach (var off in group.Where(x => x.IsOffDay))
                    if (seen.Add(off.Date))
                        result.Add(new ResolvedHolidayDay(year, off.Date, HolidayDayKinds.Off, group.Key, null, null, null));
            }
        }
        return result.OrderBy(x => x.Date).ToList();
    }

    public static HolidayCalendar ToCalendar(
        IReadOnlyList<ResolvedHolidayDay> days, bool enabled, DateOnly today, DateTimeOffset generatedAt) => new()
    {
        Enabled = enabled,
        GeneratedAt = generatedAt,
        Days = !enabled
            ? []
            : days.Where(x => x.Date >= today.AddDays(-WindowPastDays) && x.Date <= today.AddDays(WindowFutureDays))
                .Select(x => new HolidayCalendarDay
                {
                    Date = Format(x.Date),
                    Kind = x.Kind,
                    Name = x.Name,
                    FollowWeekday = x.FollowWeekday,
                    FollowSource = x.FollowSource,
                })
                .ToList(),
    };

    public static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test server/tests/RemoteCI.Server.Tests -nodeReuse:false --filter "FullyQualifiedName~HolidayCalendarBuilderTests"`
Expected: 全部通过（2 + 6 + 3 + 5 + 1 个用例）。

- [ ] **Step 7: Commit**

```bash
git add server/RemoteCI.Server/Services/HolidayDataParser.cs server/RemoteCI.Server/Services/HolidayCalendarBuilder.cs server/tests/RemoteCI.Server.Tests/HolidayTestData.cs server/tests/RemoteCI.Server.Tests/HolidayCalendarBuilderTests.cs
git commit -m "feat(server): 解析 holiday-cn 数据并推算调休补课周几"
```

---

### Task 3: 服务器存储（实体、迁移、HolidaySettingsStore）

**Files:**
- Create: `server/RemoteCI.Server/Data/HolidayEntities.cs`、`server/RemoteCI.Server/Services/HolidaySettingsStore.cs`、迁移 `server/RemoteCI.Server/Data/Migrations/<timestamp>_AddHolidayCalendar.cs`（及 Designer）
- Modify: `server/RemoteCI.Server/Data/Entities.cs`（`SystemMetadata`）、`server/RemoteCI.Server/Data/AppDbContext.cs`、`server/RemoteCI.Server/Program.cs`（注册 scoped 服务）
- Test: `server/tests/RemoteCI.Server.Tests/HolidaySettingsStoreTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `HolidayOverride`、`HolidayDataParser.MaxBytes`。
- Produces:
  - `record HolidaySettings(bool Enabled, string? SourceUrlTemplate)`
  - `HolidaySettingsStore`（scoped）：`GetSettingsAsync`、`SetSettingsAsync(bool enabled, string? template, ct)`、`static string? NormalizeTemplate(string?)`（不合法时抛 `ArgumentException`）、`GetOverridesAsync`、`SetOverrideAsync(DateOnly date, int? followWeekday, Guid? userId, DateTimeOffset now, ct)`（周几越界时抛 `ArgumentOutOfRangeException`）、`RemoveOverrideAsync(DateOnly, ct) → bool`、`GetSnapshotsAsync → IReadOnlyList<HolidayYearSnapshot>`、`SaveSnapshotAsync(int year, string rawJson, string sourceUrl, DateTimeOffset fetchedAt, ct)`
  - 实体 `HolidayYearSnapshot { int Year; string RawJson; string SourceUrl; DateTimeOffset FetchedAt }`、`HolidayMakeupOverride { DateOnly Date; int? FollowWeekday; Guid? UpdatedByUserId; DateTimeOffset UpdatedAt }`

- [ ] **Step 1: Write the failing tests**

Create `server/tests/RemoteCI.Server.Tests/HolidaySettingsStoreTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Services;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class HolidaySettingsStoreTests
{
    [Fact]
    public async Task Defaults_EnabledWithDefaultSource()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.CreateClient().GetAsync("/api/health");
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<HolidaySettingsStore>();

        Assert.Equal(new HolidaySettings(true, null), await store.GetSettingsAsync());
    }

    [Fact]
    public async Task SettingsOverridesAndSnapshots_PersistAcrossRestart()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "remoteci.db");
        var now = new DateTimeOffset(2026, 10, 8, 8, 0, 0, TimeSpan.FromHours(8));
        await using (var first = TestWebApplicationFactory.ForDatabase(databasePath))
        {
            await first.CreateClient().GetAsync("/api/health");
            using var scope = first.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<HolidaySettingsStore>();
            await store.SetSettingsAsync(false, " https://example.com/holiday/{year}.json ");
            await store.SetOverrideAsync(new DateOnly(2026, 10, 10), 5, null, now);
            await store.SetOverrideAsync(new DateOnly(2026, 9, 20), null, null, now);
            await store.SaveSnapshotAsync(2026, HolidayTestData.Json2026, "https://a/2026.json", now);
            await store.SaveSnapshotAsync(2026, HolidayTestData.Json2026, "https://b/2026.json", now.AddHours(1));
        }

        await using var second = TestWebApplicationFactory.ForDatabase(databasePath);
        await second.CreateClient().GetAsync("/api/health");
        using var secondScope = second.Services.CreateScope();
        var restored = secondScope.ServiceProvider.GetRequiredService<HolidaySettingsStore>();

        Assert.Equal(new HolidaySettings(false, "https://example.com/holiday/{year}.json"), await restored.GetSettingsAsync());
        Assert.Equal(
            [new HolidayOverride(new DateOnly(2026, 9, 20), null), new HolidayOverride(new DateOnly(2026, 10, 10), 5)],
            await restored.GetOverridesAsync());
        var snapshot = Assert.Single(await restored.GetSnapshotsAsync());
        Assert.Equal("https://b/2026.json", snapshot.SourceUrl);
        Assert.True(await restored.RemoveOverrideAsync(new DateOnly(2026, 10, 10)));
        Assert.False(await restored.RemoveOverrideAsync(new DateOnly(2026, 10, 10)));
    }

    [Theory]
    [InlineData("http://example.com/{year}.json")]
    [InlineData("https://example.com/2026.json")]
    [InlineData("not a url {year}")]
    public void NormalizeTemplate_RejectsInvalidTemplates(string template)
    {
        Assert.Throws<ArgumentException>(() => HolidaySettingsStore.NormalizeTemplate(template));
    }

    [Fact]
    public void NormalizeTemplate_TreatsBlankAsDefault()
    {
        Assert.Null(HolidaySettingsStore.NormalizeTemplate("   "));
    }

    [Fact]
    public async Task SetOverride_RejectsWeekendWeekday()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.CreateClient().GetAsync("/api/health");
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<HolidaySettingsStore>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SetOverrideAsync(new DateOnly(2026, 10, 10), 6, null, DateTimeOffset.UnixEpoch));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test server/tests/RemoteCI.Server.Tests -nodeReuse:false --filter "FullyQualifiedName~HolidaySettingsStoreTests"`
Expected: 编译失败，`HolidaySettingsStore` 不存在。

- [ ] **Step 3: Add entities and DbContext mapping**

Create `server/RemoteCI.Server/Data/HolidayEntities.cs`:

```csharp
namespace RemoteCI.Server.Data;

/// <summary>某一年 holiday-cn 数据最近一次拉取成功的原文；服务端离线重启后据此继续组装调休日历。</summary>
public sealed class HolidayYearSnapshot
{
    public int Year { get; set; }
    public string RawJson { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public DateTimeOffset FetchedAt { get; set; }
}

/// <summary>管理员对某个调休上学日补哪天课的手动安排；FollowWeekday 为 null 表示不补课。</summary>
public sealed class HolidayMakeupOverride
{
    public DateOnly Date { get; set; }
    public int? FollowWeekday { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

In `Data/Entities.cs`, inside `SystemMetadata` (append at end of class):

```csharp
    /// <summary>调休自动适配总开关：放假日关闭教室课表、调休上学日建立临时课表；默认开启。</summary>
    public bool HolidayCalendarEnabled { get; set; } = true;

    /// <summary>自定义节假日数据源地址模板（含 {year}）；null 表示使用内置的 holiday-cn 镜像。</summary>
    public string? HolidaySourceUrlTemplate { get; set; }
```

In `Data/AppDbContext.cs` add DbSets after `WebPushSubscriptions`:

```csharp
    public DbSet<HolidayYearSnapshot> HolidayYearSnapshots => Set<HolidayYearSnapshot>();
    public DbSet<HolidayMakeupOverride> HolidayMakeupOverrides => Set<HolidayMakeupOverride>();
```

In `OnModelCreating`, extend the existing `builder.Entity<SystemMetadata>` block with `entity.Property(x => x.HolidaySourceUrlTemplate).HasMaxLength(512);` and add:

```csharp
        builder.Entity<HolidayYearSnapshot>(entity =>
        {
            entity.HasKey(x => x.Year);
            // 年份是业务主键，不能让 SQLite 自增。
            entity.Property(x => x.Year).ValueGeneratedNever();
            entity.Property(x => x.SourceUrl).HasMaxLength(600);
        });
        builder.Entity<HolidayMakeupOverride>(entity => entity.HasKey(x => x.Date));
```

- [ ] **Step 4: Add the store and register it**

Create `server/RemoteCI.Server/Services/HolidaySettingsStore.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;

namespace RemoteCI.Server.Services;

public sealed record HolidaySettings(bool Enabled, string? SourceUrlTemplate);

/// <summary>调休功能的持久化：总开关与数据源、补课覆盖、年度数据快照。</summary>
public sealed class HolidaySettingsStore(AppDbContext db)
{
    public const int MaxTemplateLength = 512;

    public async Task<HolidaySettings> GetSettingsAsync(CancellationToken ct = default) =>
        await db.SystemMetadata.AsNoTracking()
            .Where(x => x.Id == 1)
            .Select(x => new HolidaySettings(x.HolidayCalendarEnabled, x.HolidaySourceUrlTemplate))
            .SingleAsync(ct);

    public async Task SetSettingsAsync(bool enabled, string? template, CancellationToken ct = default)
    {
        var normalized = NormalizeTemplate(template);
        var metadata = await db.SystemMetadata.SingleAsync(x => x.Id == 1, ct);
        metadata.HolidayCalendarEnabled = enabled;
        metadata.HolidaySourceUrlTemplate = normalized;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>空白视为使用默认源；否则必须是包含 {year} 的 https 地址，避免明文下载被篡改后误关课表。</summary>
    public static string? NormalizeTemplate(string? template)
    {
        var value = template?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > MaxTemplateLength ||
            !value.Contains("{year}", StringComparison.Ordinal) ||
            !Uri.TryCreate(value.Replace("{year}", "2026", StringComparison.Ordinal), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("数据源地址必须是包含 {year} 的 https 地址");
        return value;
    }

    public async Task<IReadOnlyList<HolidayOverride>> GetOverridesAsync(CancellationToken ct = default) =>
        await db.HolidayMakeupOverrides.AsNoTracking()
            .OrderBy(x => x.Date)
            .Select(x => new HolidayOverride(x.Date, x.FollowWeekday))
            .ToListAsync(ct);

    public async Task SetOverrideAsync(
        DateOnly date, int? followWeekday, Guid? userId, DateTimeOffset now, CancellationToken ct = default)
    {
        if (followWeekday is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(followWeekday), "补课只能安排周一至周五（1-5）的课");
        var row = await db.HolidayMakeupOverrides.FindAsync([date], ct);
        if (row is null)
            db.HolidayMakeupOverrides.Add(row = new HolidayMakeupOverride { Date = date });
        row.FollowWeekday = followWeekday;
        row.UpdatedByUserId = userId;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveOverrideAsync(DateOnly date, CancellationToken ct = default) =>
        await db.HolidayMakeupOverrides.Where(x => x.Date == date).ExecuteDeleteAsync(ct) > 0;

    public async Task<IReadOnlyList<HolidayYearSnapshot>> GetSnapshotsAsync(CancellationToken ct = default) =>
        await db.HolidayYearSnapshots.AsNoTracking().OrderBy(x => x.Year).ToListAsync(ct);

    public async Task SaveSnapshotAsync(
        int year, string rawJson, string sourceUrl, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        var row = await db.HolidayYearSnapshots.FindAsync([year], ct);
        if (row is null)
            db.HolidayYearSnapshots.Add(row = new HolidayYearSnapshot { Year = year });
        row.RawJson = rawJson;
        row.SourceUrl = sourceUrl;
        row.FetchedAt = fetchedAt;
        await db.SaveChangesAsync(ct);
    }
}
```

In `Program.cs`, after `builder.Services.AddScoped<SchedulePullSettings>();` add `builder.Services.AddScoped<HolidaySettingsStore>();`.

- [ ] **Step 5: Generate the migration and fix the default**

Run: `dotnet ef migrations add AddHolidayCalendar --project server/RemoteCI.Server --output-dir Data/Migrations -- -nodeReuse:false`

Then open the generated `*_AddHolidayCalendar.cs`. In the `AddColumn<bool>` for `HolidayCalendarEnabled`, change `defaultValue: false` to `defaultValue: true`（已有数据库升级后默认开启，与实体默认值保持一致；先例见 `20261004120000_AddClassSelfServicePolicy.cs`）。确认迁移只包含两张新表和 `SystemMetadata` 的两个新列，不包含与本功能无关的改动。

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test server/tests/RemoteCI.Server.Tests -nodeReuse:false --filter "FullyQualifiedName~HolidaySettingsStoreTests|FullyQualifiedName~SchedulePullSettingsTests"`
Expected: 全部通过（`SchedulePullSettingsTests` 用来确认迁移没有破坏已有的设置读写）。

- [ ] **Step 7: Commit**

```bash
git add server/RemoteCI.Server/Data server/RemoteCI.Server/Services/HolidaySettingsStore.cs server/RemoteCI.Server/Program.cs server/tests/RemoteCI.Server.Tests/HolidaySettingsStoreTests.cs
git commit -m "feat(server): 持久化调休设置、补课覆盖与节假日数据快照"
```

---

### Task 4: HolidayCalendarService、后台刷新与插件下发

**Files:**
- Create: `server/RemoteCI.Server/Services/HolidayCalendarService.cs`、`server/RemoteCI.Server/Services/HolidayCalendarWorker.cs`
- Modify: `server/RemoteCI.Server/Services/PeerRegistry.cs`、`server/RemoteCI.Server/WebSocketHub.cs`、`server/RemoteCI.Server/Services/ServerOptions.cs`、`server/RemoteCI.Server/Program.cs`、`server/tests/RemoteCI.Server.Tests/TestWebApplicationFactory.cs`
- Test: `server/tests/RemoteCI.Server.Tests/HolidayCalendarServiceTests.cs`；在 `server/tests/RemoteCI.Server.Tests/WebSocketRelayTests.cs` 中追加一个用例

**Interfaces:**
- Consumes: Task 2 的 `HolidayDataParser`、`HolidayCalendarBuilder`；Task 3 的 `HolidaySettingsStore`。
- Produces:
  - `class HolidayClock(TimeProvider)`：`virtual DateTimeOffset Now`、`DateOnly Today`
  - `record HolidayRefreshStatus(DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt, string? LastError)`
  - `record HolidayOverview(bool Enabled, string? SourceUrlTemplate, IReadOnlyList<string> DefaultSources, HolidayRefreshStatus Status, IReadOnlyList<HolidayPeriodView> Periods, IReadOnlyList<string> StaleOverrideDates)`
  - `record HolidayPeriodView(string Name, string? OffStart, string? OffEnd, IReadOnlyList<HolidayMakeupView> MakeupDays)`
  - `record HolidayMakeupView(string Date, int? AutoWeekday, int? FollowWeekday, string FollowSource)`
  - `class HolidayOperationException(string message) : Exception`
  - `HolidayCalendarService`（singleton）：`const string HttpClientName = "holiday-cn"`、`static IReadOnlyList<string> DefaultSources`、`HolidayRefreshStatus Status`、`Task<HolidayRefreshStatus> RefreshAsync(ct)`、`Task<HolidayCalendar> BuildCalendarAsync(ct)`、`Task<HolidayOverview> GetOverviewAsync(ct)`、`Task PublishAsync(bool force = false, ct)`、`Task SendToConnectionAsync(Guid connectionId, ct)`、`Task<HolidayOverview> UpdateSettingsAsync(bool enabled, string? template, ct)`、`Task<HolidayOverview> SetOverrideAsync(DateOnly date, int? followWeekday, Guid? userId, ct)`、`Task<HolidayOverview> RemoveOverrideAsync(DateOnly date, ct)`
  - `PeerRegistry.BroadcastToPluginsWithCapabilityAsync(string capability, Envelope envelope, ct) → Task<int>`
  - `ServerOptions.HolidayAutoRefresh`（bool，默认 true）

- [ ] **Step 1: Disable background refresh in tests**

In `TestWebApplicationFactory.ConfigureWebHost`, add to `values`:

```csharp
                // 后台节假日刷新会访问外网；测试按需直接调用 HolidayCalendarService。
                ["Server:HolidayAutoRefresh"] = "false",
```

In `ServerOptions.cs` append:

```csharp
    /// <summary>是否在后台定时拉取节假日数据；测试环境关闭以免访问外网。</summary>
    public bool HolidayAutoRefresh { get; set; } = true;
```

- [ ] **Step 2: Write the failing service tests**

Create `server/tests/RemoteCI.Server.Tests/HolidayCalendarServiceTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class HolidayCalendarServiceTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.FromHours(8));

    internal sealed class FixedClock(DateTimeOffset now) : HolidayClock(TimeProvider.System)
    {
        public override DateTimeOffset Now => now;
    }

    internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    internal static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    /// <summary>jsDelivr 故障、raw 可用、次年尚未发布：最常见的国内网络情形。</summary>
    internal static StubHandler TypicalSources() => new(request => request.RequestUri!.ToString() switch
    {
        var url when url.Contains("cdn.jsdelivr.net") => new HttpResponseMessage(HttpStatusCode.BadGateway),
        var url when url.EndsWith("/2026.json") => Json(HolidayTestData.Json2026),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    });

    internal static WebApplicationFactoryWithHoliday Create(StubHandler handler) => new(handler);

    internal sealed class WebApplicationFactoryWithHoliday(StubHandler handler) : IAsyncDisposable
    {
        public TestWebApplicationFactory Inner { get; } = new();
        private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? _app;

        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> App => _app ??= Inner.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<HolidayClock>(new FixedClock(Now));
                services.AddHttpClient(HolidayCalendarService.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
            }));

        public HolidayCalendarService Service => App.Services.GetRequiredService<HolidayCalendarService>();

        /// <summary>在 App 这个宿主上登录，令牌与后续请求属于同一个 TestServer。</summary>
        public async Task<AuthResponse> LoginAsync(
            string username = TestWebApplicationFactory.AdminUsername,
            string password = TestWebApplicationFactory.AdminPassword)
        {
            var response = await App.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest
            {
                Username = username,
                Password = password,
                DeviceName = "Holiday Test",
            });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null) await _app.DisposeAsync();
            await Inner.DisposeAsync();
        }
    }

    [Fact]
    public async Task Refresh_FallsBackToRawAndIgnoresUnpublishedNextYear()
    {
        var handler = TypicalSources();
        await using var host = Create(handler);
        await host.App.CreateClient().GetAsync("/api/health");

        var status = await host.Service.RefreshAsync();

        Assert.Null(status.LastError);
        Assert.Equal(Now, status.LastSuccessAt);
        Assert.Contains(handler.Requests, x => x.StartsWith("https://raw.githubusercontent.com/") && x.EndsWith("/2026.json"));
        var calendar = await host.Service.BuildCalendarAsync();
        Assert.True(calendar.Enabled);
        Assert.Contains(calendar.Days, x => x is { Date: "2026-10-10", Kind: HolidayDayKinds.Makeup, FollowWeekday: 3 });
    }

    [Fact]
    public async Task Refresh_InvalidPayloadKeepsPreviousSnapshot()
    {
        var broken = false;
        var handler = new StubHandler(request => broken
            ? Json("{\"days\":[{\"name\":\"x\",\"date\":\"bad\",\"isOffDay\":true}]}")
            : request.RequestUri!.ToString().EndsWith("/2026.json") ? Json(HolidayTestData.Json2026) : new HttpResponseMessage(HttpStatusCode.NotFound));
        await using var host = Create(handler);
        await host.App.CreateClient().GetAsync("/api/health");
        await host.Service.RefreshAsync();

        broken = true;
        var status = await host.Service.RefreshAsync();

        Assert.NotNull(status.LastError);
        Assert.Equal(Now, status.LastSuccessAt);
        Assert.Contains((await host.Service.BuildCalendarAsync()).Days, x => x.Date == "2026-10-10");
    }

    [Fact]
    public async Task Refresh_UsesOnlyCustomTemplateWhenConfigured()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host == "mirror.example.com" && request.RequestUri.AbsolutePath == "/h/2026.json"
            ? Json(HolidayTestData.Json2026)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        await using var host = Create(handler);
        await host.App.CreateClient().GetAsync("/api/health");

        await host.Service.UpdateSettingsAsync(true, "https://mirror.example.com/h/{year}.json");

        Assert.All(handler.Requests, x => Assert.StartsWith("https://mirror.example.com/", x));
        Assert.Null(host.Service.Status.LastError);
    }

    [Fact]
    public async Task Overview_GroupsUpcomingPeriodsAndFlagsStaleOverrides()
    {
        await using var host = Create(TypicalSources());
        await host.App.CreateClient().GetAsync("/api/health");
        await host.Service.RefreshAsync();
        await host.Service.SetOverrideAsync(new DateOnly(2026, 10, 10), null, null);
        await Assert.ThrowsAsync<HolidayOperationException>(() =>
            host.Service.SetOverrideAsync(new DateOnly(2026, 10, 9), 1, null));

        var overview = await host.Service.GetOverviewAsync();

        var national = Assert.Single(overview.Periods, x => x.Name == "国庆节");
        Assert.Equal(("2026-10-01", "2026-10-07"), (national.OffStart, national.OffEnd));
        Assert.Contains(national.MakeupDays, x => x is { Date: "2026-10-10", AutoWeekday: 3, FollowWeekday: null, FollowSource: HolidayFollowSources.Skip });
        Assert.DoesNotContain(overview.Periods, x => x.Name == "劳动节");
        Assert.Empty(overview.StaleOverrideDates);
    }

    [Fact]
    public async Task Disabled_PublishesEmptyCalendar()
    {
        await using var host = Create(TypicalSources());
        await host.App.CreateClient().GetAsync("/api/health");
        await host.Service.RefreshAsync();

        await host.Service.UpdateSettingsAsync(false, null);
        var calendar = await host.Service.BuildCalendarAsync();

        Assert.False(calendar.Enabled);
        Assert.Empty(calendar.Days);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test server/tests/RemoteCI.Server.Tests -nodeReuse:false --filter "FullyQualifiedName~HolidayCalendarServiceTests"`
Expected: 编译失败，`HolidayCalendarService` / `HolidayClock` 不存在。

- [ ] **Step 4: Add the PeerRegistry broadcast**

In `PeerRegistry.cs`, after `SendToPluginConnectionAsync`:

```csharp
    /// <summary>
    /// 向所有声明了指定能力的在线插件广播。调休日历要在每台教室电脑上各自生效，
    /// 所以不像命令那样只发给班级主插件。
    /// </summary>
    public async Task<int> BroadcastToPluginsWithCapabilityAsync(
        string capability, Envelope envelope, CancellationToken ct = default)
    {
        var sent = 0;
        var targets = _pluginPeers.Values
            .Where(IsLocallyAuthorized)
            .Where(peer => EffectiveCapabilities(peer).Contains(capability, StringComparer.Ordinal))
            .ToList();
        foreach (var peer in targets)
        {
            if (await TrySendAsync(peer, envelope, ct)) sent++;
            else await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
        }
        return sent;
    }
```

- [ ] **Step 5: Implement the service**

Create `server/RemoteCI.Server/Services/HolidayCalendarService.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>调休功能使用的本地时钟；测试替换为固定时间，避免依赖运行测试当天的日期。</summary>
public class HolidayClock(TimeProvider time)
{
    public virtual DateTimeOffset Now => time.GetLocalNow();
    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);
}

public sealed record HolidayRefreshStatus(DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt, string? LastError);

public sealed record HolidayMakeupView(string Date, int? AutoWeekday, int? FollowWeekday, string FollowSource);

public sealed record HolidayPeriodView(string Name, string? OffStart, string? OffEnd, IReadOnlyList<HolidayMakeupView> MakeupDays);

public sealed record HolidayOverview(
    bool Enabled,
    string? SourceUrlTemplate,
    IReadOnlyList<string> DefaultSources,
    HolidayRefreshStatus Status,
    IReadOnlyList<HolidayPeriodView> Periods,
    IReadOnlyList<string> StaleOverrideDates);

/// <summary>调休操作的业务错误（例如给非调休上学日设置补课），由 API 映射为 400。</summary>
public sealed class HolidayOperationException(string message) : Exception(message);

/// <summary>拉取节假日数据、组装调休日历并推送给插件；管理入口（API/WebUI）也经由这里修改设置。</summary>
public sealed class HolidayCalendarService(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    PeerRegistry peers,
    HolidayClock clock,
    ILogger<HolidayCalendarService> logger)
{
    public const string HttpClientName = "holiday-cn";

    public static IReadOnlyList<string> DefaultSources { get; } =
    [
        "https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{year}.json",
        "https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{year}.json",
    ];

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _publishLock = new();
    private string? _lastPublishedKey;
    private HolidayRefreshStatus _status = new(null, null, null);

    public HolidayRefreshStatus Status => Volatile.Read(ref _status);

    public async Task<HolidayRefreshStatus> RefreshAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<HolidaySettingsStore>();
            var settings = await store.GetSettingsAsync(ct);
            IReadOnlyList<string> sources = settings.SourceUrlTemplate is { } custom ? [custom] : DefaultSources;
            var now = clock.Now;
            var errors = new List<string>();
            foreach (var year in new[] { now.Year, now.Year + 1 })
            {
                var outcome = await FetchYearAsync(year, sources, ct);
                if (outcome.Json is { } json)
                    await store.SaveSnapshotAsync(year, json, outcome.Url!, now, ct);
                // 次年数据通常 11 月后才发布，此前的 404 不算故障。
                else if (!(outcome.NotPublished && year > now.Year))
                    errors.Add(outcome.Error);
            }
            var status = errors.Count == 0
                ? new HolidayRefreshStatus(now, now, null)
                : new HolidayRefreshStatus(now, Status.LastSuccessAt, string.Join("；", errors));
            Volatile.Write(ref _status, status);
            if (status.LastError is not null)
                logger.LogWarning("节假日数据刷新失败，继续使用已有快照：{Error}", status.LastError);
        }
        finally
        {
            _refreshGate.Release();
        }
        await PublishAsync(ct: ct);
        return Status;
    }

    public async Task<HolidayCalendar> BuildCalendarAsync(CancellationToken ct = default)
    {
        var (settings, days, _) = await LoadAsync(ct);
        return HolidayCalendarBuilder.ToCalendar(days, settings.Enabled, clock.Today, clock.Now);
    }

    public async Task<HolidayOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        var (settings, days, overrides) = await LoadAsync(ct);
        var today = clock.Today;
        var periods = days
            .GroupBy(x => (x.Year, x.Name))
            .Where(group => group.Max(x => x.Date) >= today)
            .OrderBy(group => group.Min(x => x.Date))
            .Select(group =>
            {
                var offs = group.Where(x => x.Kind == HolidayDayKinds.Off).Select(x => x.Date).ToList();
                return new HolidayPeriodView(
                    group.Key.Name,
                    offs.Count > 0 ? HolidayCalendarBuilder.Format(offs.Min()) : null,
                    offs.Count > 0 ? HolidayCalendarBuilder.Format(offs.Max()) : null,
                    group.Where(x => x.Kind == HolidayDayKinds.Makeup)
                        .Select(x => new HolidayMakeupView(HolidayCalendarBuilder.Format(x.Date), x.AutoWeekday, x.FollowWeekday, x.FollowSource!))
                        .ToList());
            })
            .ToList();
        var makeupDates = days.Where(x => x.Kind == HolidayDayKinds.Makeup).Select(x => x.Date).ToHashSet();
        var stale = overrides.Where(x => !makeupDates.Contains(x.Date)).Select(x => HolidayCalendarBuilder.Format(x.Date)).ToList();
        return new HolidayOverview(settings.Enabled, settings.SourceUrlTemplate, DefaultSources, Status, periods, stale);
    }

    public async Task<HolidayOverview> UpdateSettingsAsync(bool enabled, string? template, CancellationToken ct = default)
    {
        using (var scope = scopes.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<HolidaySettingsStore>();
            var before = await store.GetSettingsAsync(ct);
            await store.SetSettingsAsync(enabled, template, ct);
            var after = await store.GetSettingsAsync(ct);
            // 换了数据源就立刻按新源拉一次，管理员保存后马上能看到结果。
            if (after.SourceUrlTemplate != before.SourceUrlTemplate)
            {
                await RefreshAsync(ct);
                return await GetOverviewAsync(ct);
            }
        }
        await PublishAsync(ct: ct);
        return await GetOverviewAsync(ct);
    }

    public async Task<HolidayOverview> SetOverrideAsync(
        DateOnly date, int? followWeekday, Guid? userId, CancellationToken ct = default)
    {
        var (_, days, _) = await LoadAsync(ct);
        if (!days.Any(x => x.Kind == HolidayDayKinds.Makeup && x.Date == date))
            throw new HolidayOperationException($"{HolidayCalendarBuilder.Format(date)} 不是调休上学日");
        using (var scope = scopes.CreateScope())
            await scope.ServiceProvider.GetRequiredService<HolidaySettingsStore>()
                .SetOverrideAsync(date, followWeekday, userId, clock.Now, ct);
        await PublishAsync(ct: ct);
        return await GetOverviewAsync(ct);
    }

    public async Task<HolidayOverview> RemoveOverrideAsync(DateOnly date, CancellationToken ct = default)
    {
        using (var scope = scopes.CreateScope())
            await scope.ServiceProvider.GetRequiredService<HolidaySettingsStore>().RemoveOverrideAsync(date, ct);
        await PublishAsync(ct: ct);
        return await GetOverviewAsync(ct);
    }

    /// <summary>日历内容（不含生成时间）变化时才广播，避免每次刷新都让插件重算。</summary>
    public async Task PublishAsync(bool force = false, CancellationToken ct = default)
    {
        var calendar = await BuildCalendarAsync(ct);
        var key = JsonSerializer.Serialize(new { calendar.Enabled, calendar.Days }, JsonDefaults.Options);
        lock (_publishLock)
        {
            if (!force && key == _lastPublishedKey) return;
            _lastPublishedKey = key;
        }
        await peers.BroadcastToPluginsWithCapabilityAsync(
            RemoteCiCapabilities.HolidayCalendar, Envelope.HolidayCalendar(calendar), ct);
    }

    public async Task SendToConnectionAsync(Guid connectionId, CancellationToken ct = default) =>
        await peers.SendToPluginConnectionAsync(connectionId, Envelope.HolidayCalendar(await BuildCalendarAsync(ct)), ct);

    private async Task<(HolidaySettings Settings, IReadOnlyList<ResolvedHolidayDay> Days, IReadOnlyList<HolidayOverride> Overrides)>
        LoadAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<HolidaySettingsStore>();
        var settings = await store.GetSettingsAsync(ct);
        var overrides = await store.GetOverridesAsync(ct);
        var years = new Dictionary<int, IReadOnlyList<HolidayEntry>>();
        foreach (var snapshot in await store.GetSnapshotsAsync(ct))
        {
            try { years[snapshot.Year] = HolidayDataParser.Parse(snapshot.Year, snapshot.RawJson); }
            catch (InvalidDataException ex) { logger.LogWarning(ex, "{Year} 年节假日快照无法解析，已忽略", snapshot.Year); }
        }
        return (settings, HolidayCalendarBuilder.Resolve(years, overrides), overrides);
    }

    private async Task<FetchOutcome> FetchYearAsync(int year, IReadOnlyList<string> sources, CancellationToken ct)
    {
        var client = httpClients.CreateClient(HttpClientName);
        string? error = null;
        var notFound = 0;
        foreach (var template in sources)
        {
            var url = template.Replace("{year}", year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.NotFound) { notFound++; continue; }
                if (!response.IsSuccessStatusCode)
                {
                    error = $"{year} 年：{url} 返回 {(int)response.StatusCode}";
                    continue;
                }
                var json = await ReadLimitedAsync(response.Content, ct);
                HolidayDataParser.Parse(year, json);
                return new FetchOutcome(json, url, false, string.Empty);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException ||
                                       (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                error = $"{year} 年：{ex.Message}";
            }
        }
        return new FetchOutcome(null, null, notFound == sources.Count, error ?? $"{year} 年节假日数据尚未发布");
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > HolidayDataParser.MaxBytes)
                throw new InvalidDataException("节假日数据超过 256 KiB");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed record FetchOutcome(string? Json, string? Url, bool NotPublished, string Error);
}
```

- [ ] **Step 6: Implement the worker and register everything**

Create `server/RemoteCI.Server/Services/HolidayCalendarWorker.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace RemoteCI.Server.Services;

/// <summary>启动后立即刷新节假日数据，之后每 12 小时刷新；跨天后（00:05 起）再发布一次，让 60 天窗口向前滚动。</summary>
public sealed class HolidayCalendarWorker(
    HolidayCalendarService holidays,
    HolidayClock clock,
    IOptions<ServerOptions> options,
    ILogger<HolidayCalendarWorker> logger) : BackgroundService
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan DailyPublishAfter = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.HolidayAutoRefresh) return;
        DateTimeOffset? lastRefresh = null;
        var lastDate = clock.Today;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                var now = clock.Now;
                if (lastRefresh is null || now - lastRefresh >= RefreshInterval)
                {
                    await holidays.RefreshAsync(stoppingToken);
                    lastRefresh = now;
                    lastDate = clock.Today;
                }
                else if (clock.Today != lastDate && now.TimeOfDay >= DailyPublishAfter)
                {
                    await holidays.PublishAsync(ct: stoppingToken);
                    lastDate = clock.Today;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "调休日历后台任务失败");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
```

In `Program.cs`, next to the other singletons (after `builder.Services.AddSingleton(TimeProvider.System);`):

```csharp
builder.Services.AddSingleton<HolidayClock>();
builder.Services.AddSingleton<HolidayCalendarService>();
builder.Services.AddHttpClient(HolidayCalendarService.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("RemoteCI-Server");
});
builder.Services.AddHostedService<HolidayCalendarWorker>();
```

- [ ] **Step 7: Send the calendar after a plugin reports the capability**

In `WebSocketHub.DispatchAsync`, replace the `MessageTypePeerCapabilities` block with:

```csharp
        if (envelope.Type == Protocol.MessageTypePeerCapabilities)
        {
            if (ConvertPayload<PeerCapabilities>(envelope.Payload) is { } capabilities)
            {
                await session.Registry.ReportCapabilitiesAsync(
                    session.ConnectionId, capabilities, session.CancellationToken);
                // 能力上报晚于连接建立，因此调休日历在这里补发，旧插件不声明能力就不会收到。
                if (session.Principal.IsPlugin &&
                    capabilities.Capabilities.Contains(RemoteCiCapabilities.HolidayCalendar, StringComparer.Ordinal))
                    await session.Context.RequestServices.GetRequiredService<HolidayCalendarService>()
                        .SendToConnectionAsync(session.ConnectionId, session.CancellationToken);
            }
            return;
        }
```

- [ ] **Step 8: Add the relay test**

Append to `WebSocketRelayTests`（复用该类已有的 `ConnectPluginAsync`、`SendAsync`、`ReceiveEnvelopeAsync`、`ConvertPayload`）：

```csharp
    [Fact]
    public async Task HolidayCalendar_IsSentOnlyAfterPluginDeclaresCapability()
    {
        using var plugin = await ConnectPluginAsync();
        await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);
        await SendAsync(plugin, Envelope.PeerCapabilities(new PeerCapabilities
        {
            SoftwareVersion = "3.3.0",
            Capabilities = RemoteCiCapabilities.Current,
        }));

        var envelope = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeHolidayCalendar);
        var calendar = ConvertPayload<HolidayCalendar>(envelope.Payload);

        Assert.True(calendar.Enabled);
    }
```

- [ ] **Step 9: Run tests to verify they pass**

Run: `dotnet test server/tests/RemoteCI.Server.Tests -nodeReuse:false --filter "FullyQualifiedName~Holiday|FullyQualifiedName~WebSocketRelay"`
Expected: 全部通过。

- [ ] **Step 10: Commit**

```bash
git add server
git commit -m "feat(server): 定时拉取节假日数据并向插件下发调休日历"
```

---

### Task 5: REST API

**Files:**
- Create: `server/RemoteCI.Server/HolidayEndpoints.cs`
- Modify: `server/RemoteCI.Server/Program.cs`（在 `app.Run();` 前加一行 `app.MapHolidayEndpoints();`）
- Test: `server/tests/RemoteCI.Server.Tests/HolidayApiTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `HolidayCalendarService`、`HolidayOverview`、`HolidayOperationException`。
- Produces: `GET /api/holidays`、`PUT /api/admin/holidays/settings`（body `HolidaySettingsBody(bool Enabled, string? SourceUrlTemplate)`）、`PUT /api/admin/holidays/overrides/{date}`（body `HolidayOverrideBody(int? FollowWeekday)`）、`DELETE /api/admin/holidays/overrides/{date}`、`POST /api/admin/holidays/refresh`；成功时返回 `HolidayOverview`，`refresh` 返回 `HolidayRefreshStatus`。

- [ ] **Step 1: Write the failing tests**

Create `server/tests/RemoteCI.Server.Tests/HolidayApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class HolidayApiTests
{
    [Fact]
    public async Task ReadRequiresLoginAndWritesRequireAdmin()
    {
        await using var host = HolidayCalendarServiceTests.Create(HolidayCalendarServiceTests.TypicalSources());
        var client = host.App.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/holidays")).StatusCode);

        var admin = await host.LoginAsync();
        var created = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/users", admin.AccessToken,
            new CreateUserRequest { Username = "holiday.reader", DisplayName = "普通用户", Password = "Holiday-Reader-2026" }));
        created.EnsureSuccessStatusCode();
        var reader = await host.LoginAsync("holiday.reader", "Holiday-Reader-2026");

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/holidays", reader.AccessToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put, "/api/admin/holidays/overrides/2026-10-10", reader.AccessToken, new { followWeekday = 5 }))).StatusCode);
    }

    [Fact]
    public async Task OverrideRoundTripAndValidation()
    {
        await using var host = HolidayCalendarServiceTests.Create(HolidayCalendarServiceTests.TypicalSources());
        var client = host.App.CreateClient();
        var admin = await host.LoginAsync();
        (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/admin/holidays/refresh", admin.AccessToken))).EnsureSuccessStatusCode();

        var set = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put, "/api/admin/holidays/overrides/2026-10-10", admin.AccessToken, new { followWeekday = 5 }));
        set.EnsureSuccessStatusCode();
        Assert.Contains("\"followSource\":\"manual\"", await set.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put, "/api/admin/holidays/overrides/2026-10-09", admin.AccessToken, new { followWeekday = 1 }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put, "/api/admin/holidays/overrides/10-10", admin.AccessToken, new { followWeekday = 1 }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put, "/api/admin/holidays/overrides/2026-10-10", admin.AccessToken, new { followWeekday = 6 }))).StatusCode);

        var cleared = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Delete, "/api/admin/holidays/overrides/2026-10-10", admin.AccessToken));
        cleared.EnsureSuccessStatusCode();
        Assert.Contains("\"date\":\"2026-10-10\",\"autoWeekday\":3,\"followWeekday\":3,\"followSource\":\"auto\"",
            await cleared.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Settings_RejectInvalidTemplate()
    {
        await using var host = HolidayCalendarServiceTests.Create(HolidayCalendarServiceTests.TypicalSources());
        var client = host.App.CreateClient();
        var admin = await host.LoginAsync();

        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, "/api/admin/holidays/settings",
            admin.AccessToken, new { enabled = true, sourceUrlTemplate = "http://insecure/{year}.json" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
```


- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test server/tests/RemoteCI.Server.Tests -nodeReuse:false --filter "FullyQualifiedName~HolidayApiTests"`
Expected: FAIL，接口返回 404。

- [ ] **Step 3: Implement the endpoints**

Create `server/RemoteCI.Server/HolidayEndpoints.cs`:

```csharp
using System.Globalization;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server;

public sealed record HolidaySettingsBody(bool Enabled, string? SourceUrlTemplate);
public sealed record HolidayOverrideBody(int? FollowWeekday);

/// <summary>调休功能的 REST 接口；独立成文件，避免继续膨胀 Program.cs。</summary>
public static class HolidayEndpoints
{
    public static void MapHolidayEndpoints(this WebApplication app)
    {
        app.MapGet("/api/holidays", async (HttpContext ctx, IdentityCoordinator identities, HolidayCalendarService holidays, CancellationToken ct) =>
            await AuthorizeAsync(ctx, identities, ct) is { User: not null }
                ? Results.Ok(await holidays.GetOverviewAsync(ct))
                : Unauthorized());

        app.MapPut("/api/admin/holidays/settings", async (HolidaySettingsBody body, HttpContext ctx, IdentityCoordinator identities, HolidayCalendarService holidays, CancellationToken ct) =>
            await AdminAsync(ctx, identities, ct, async _ => Results.Ok(await holidays.UpdateSettingsAsync(body.Enabled, body.SourceUrlTemplate, ct))));

        app.MapPut("/api/admin/holidays/overrides/{date}", async (string date, HolidayOverrideBody body, HttpContext ctx, IdentityCoordinator identities, HolidayCalendarService holidays, CancellationToken ct) =>
            await AdminAsync(ctx, identities, ct, async principal =>
                Results.Ok(await holidays.SetOverrideAsync(ParseDate(date), body.FollowWeekday, principal.User!.Id, ct))));

        app.MapDelete("/api/admin/holidays/overrides/{date}", async (string date, HttpContext ctx, IdentityCoordinator identities, HolidayCalendarService holidays, CancellationToken ct) =>
            await AdminAsync(ctx, identities, ct, async _ => Results.Ok(await holidays.RemoveOverrideAsync(ParseDate(date), ct))));

        app.MapPost("/api/admin/holidays/refresh", async (HttpContext ctx, IdentityCoordinator identities, HolidayCalendarService holidays, CancellationToken ct) =>
            await AdminAsync(ctx, identities, ct, async _ => Results.Ok(await holidays.RefreshAsync(ct))));
    }

    private static DateOnly ParseDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new HolidayOperationException("日期格式应为 yyyy-MM-dd");

    private static async Task<IResult> AdminAsync(
        HttpContext ctx, IdentityCoordinator identities, CancellationToken ct, Func<AuthPrincipal, Task<IResult>> action)
    {
        var principal = await AuthorizeAsync(ctx, identities, ct);
        if (principal?.User is null) return Unauthorized();
        if (principal.User.Role != UserRole.Admin) return Results.Json(
            new ApiError { Code = ApiErrorCodes.Forbidden, Message = "权限不足" }, statusCode: StatusCodes.Status403Forbidden);
        try { return await action(principal); }
        catch (Exception ex) when (ex is HolidayOperationException or ArgumentException)
        {
            return Results.BadRequest(new ApiError { Code = ApiErrorCodes.InvalidRequest, Message = ex.Message });
        }
    }

    // 与 Program.cs 中的同名本地函数一致；顶级语句里的本地函数无法跨文件复用。
    private static async Task<AuthPrincipal?> AuthorizeAsync(HttpContext ctx, IdentityCoordinator identities, CancellationToken ct)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var token = header.StartsWith($"{Protocol.BearerScheme} ", StringComparison.OrdinalIgnoreCase)
            ? header[(Protocol.BearerScheme.Length + 1)..].Trim()
            : ctx.Request.Headers["X-API-Key"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(token)) return null;
        return token.StartsWith(IdentityCoordinator.ApiKeyPrefix, StringComparison.Ordinal)
            ? await identities.ValidateApiKeyAsync(token, ct)
            : await identities.ValidateAccessTokenAsync(token, ct);
    }

    private static IResult Unauthorized() => Results.Json(
        new ApiError { Code = ApiErrorCodes.Unauthorized, Message = "未登录或登录已失效" },
        statusCode: StatusCodes.Status401Unauthorized);
}
```

In `Program.cs` immediately before `app.Run();` add `app.MapHolidayEndpoints();`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test server/tests/RemoteCI.Server.Tests -nodeReuse:false --filter "FullyQualifiedName~HolidayApiTests"`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add server
git commit -m "feat(api): 新增调休查询、设置、补课覆盖与刷新接口"
```

---

### Task 6: WebUI 调休页与课表标签

**Files:**
- Create: `server/RemoteCI.Server/Pages/Holidays.cshtml`、`server/RemoteCI.Server/Pages/Holidays.cshtml.cs`
- Modify: `server/RemoteCI.Server/Pages/Shared/_Layout.cshtml`（管理员导航，加在 `/LoginSettings` 链接之后）、`server/RemoteCI.Server/Pages/Shared/_ScheduleTable.cshtml`、`server/RemoteCI.Server/wwwroot/app.css`
- Test: `server/tests/RemoteCI.Server.Tests/HolidayPageTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `HolidayCalendarService`；Task 1 的 `ScheduleDay.DayKind/HolidayName`。
- Produces: `/Holidays` 页面，处理器 `Settings`、`Override`（表单字段 `date`、`follow` ∈ `auto|skip|1..5`）、`Refresh`。

- [ ] **Step 1: Write the failing test**

Create `server/tests/RemoteCI.Server.Tests/HolidayPageTests.cs`:

```csharp
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class HolidayPageTests
{
    [Fact]
    public async Task AdminSeesPeriodsAndCanOverrideMakeupDay()
    {
        await using var host = HolidayCalendarServiceTests.Create(HolidayCalendarServiceTests.TypicalSources());
        await host.App.CreateClient().GetAsync("/api/health");
        await host.Service.RefreshAsync();
        using var browser = host.App.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(browser);

        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/Holidays"));
        Assert.Contains("国庆节", html);
        Assert.Contains("2026-10-10", html);
        Assert.Contains("自动（周三）", html);

        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"").Groups[1].Value;
        var post = await browser.PostAsync("/Holidays?handler=Override", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["date"] = "2026-10-10",
            ["follow"] = "skip",
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        var overview = await host.Service.GetOverviewAsync();
        Assert.Contains(overview.Periods.SelectMany(x => x.MakeupDays), x => x is { Date: "2026-10-10", FollowSource: HolidayFollowSources.Skip });
    }

    [Fact]
    public async Task ScheduleTableShowsHolidayTag()
    {
        await using var factory = new TestWebApplicationFactory();
        _ = await factory.LoginAsync();
        using (var scope = factory.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<IStateStore>().SaveSchedule(Classroom.DefaultId, new ScheduleBundle
            {
                FromDate = "2026-10-01",
                Days =
                [
                    new ScheduleDay { Date = "2026-10-01", DayKind = ScheduleDayKinds.Holiday, HolidayName = "国庆节" },
                    new ScheduleDay { Date = "2026-10-10", Enabled = true, DayKind = ScheduleDayKinds.Makeup, HolidayName = "国庆节" },
                ],
            });
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(browser);

        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/Schedule"));

        Assert.Contains("放假 · 国庆节", html);
        Assert.Contains("调休 · 国庆节", html);
    }

    private static async Task LoginAsync(HttpClient browser)
    {
        var html = await browser.GetStringAsync("/Login");
        var token = Regex.Match(html, "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        var response = await browser.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = TestWebApplicationFactory.AdminUsername,
            ["Input.Password"] = TestWebApplicationFactory.AdminPassword,
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
        }));
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }
}
```


- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test server/tests/RemoteCI.Server.Tests -nodeReuse:false --filter "FullyQualifiedName~HolidayPageTests"`
Expected: FAIL（`/Holidays` 返回 404；课表页没有标签）。

- [ ] **Step 3: Implement the page model**

Create `server/RemoteCI.Server/Pages/Holidays.cshtml.cs`:

```csharp
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;

namespace RemoteCI.Server.Pages;

/// <summary>系统管理员查看节假日数据状态，调整调休上学日补哪天的课。</summary>
[Authorize]
public sealed class HolidaysModel(UserManager<AppUser> users, HolidayCalendarService holidays) : WebPageModel(users)
{
    public static readonly string[] WeekdayNames = ["", "周一", "周二", "周三", "周四", "周五", "周六", "周日"];

    [BindProperty] public SettingsInput Settings { get; set; } = new();
    public HolidayOverview Overview { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        Overview = await holidays.GetOverviewAsync(ct);
        Settings = new SettingsInput { Enabled = Overview.Enabled, SourceUrlTemplate = Overview.SourceUrlTemplate };
        return Page();
    }

    public async Task<IActionResult> OnPostSettingsAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        try
        {
            await holidays.UpdateSettingsAsync(Settings.Enabled, Settings.SourceUrlTemplate, ct);
            TempData["Message"] = "调休设置已保存。";
        }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostOverrideAsync(string date, string follow, CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            TempData["Error"] = "日期无效。";
            return RedirectToPage();
        }
        try
        {
            if (follow == "auto") await holidays.RemoveOverrideAsync(day, ct);
            else await holidays.SetOverrideAsync(day, follow == "skip" ? null : int.Parse(follow, CultureInfo.InvariantCulture), CurrentUser.Id, ct);
            TempData["Message"] = $"{date} 的补课安排已更新。";
        }
        catch (Exception ex) when (ex is HolidayOperationException or ArgumentException or FormatException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRefreshAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        var status = await holidays.RefreshAsync(ct);
        TempData[status.LastError is null ? "Message" : "Error"] = status.LastError ?? "节假日数据已刷新。";
        return RedirectToPage();
    }

    public sealed class SettingsInput
    {
        public bool Enabled { get; set; }
        public string? SourceUrlTemplate { get; set; }
    }
}
```

- [ ] **Step 4: Implement the view**

Create `server/RemoteCI.Server/Pages/Holidays.cshtml`:

```cshtml
@page
@model HolidaysModel
@using RemoteCI.Shared.Models
@{
    ViewData["Title"] = "调休";
    var status = Model.Overview.Status;
}
<div class="page-head">
    <div>
        <p class="eyebrow">服务端管理</p>
        <h1>调休</h1>
    </div>
</div>
<section class="panel">
    <h2>自动适配</h2>
    <p class="muted">放假日自动关闭教室课表；原本是周末但要上学的日子，自动开启一份临时课表，上对应工作日的课。</p>
    <form method="post" asp-page-handler="Settings" class="stack">
        <label class="check"><input type="checkbox" asp-for="Settings.Enabled" /> 启用调休自动适配</label>
        <label>数据源地址（留空使用 holiday-cn 默认镜像）
            <input type="url" asp-for="Settings.SourceUrlTemplate" maxlength="512" placeholder="@Model.Overview.DefaultSources[0]" />
        </label>
        <button type="submit">保存</button>
    </form>
    <p class="muted holiday-status">
        上次成功刷新：@(status.LastSuccessAt?.ToString("yyyy-MM-dd HH:mm") ?? "从未")
        @if (status.LastError is { } error) { <span class="holiday-error">（最近一次失败：@error）</span> }
    </p>
    <form method="post" asp-page-handler="Refresh"><button type="submit" class="secondary">立即刷新</button></form>
</section>
<section class="panel">
    <h2>近期假期与补课</h2>
    @if (Model.Overview.Periods.Count == 0)
    {
        <p class="muted">暂无数据。请检查数据源，或点击“立即刷新”。</p>
    }
    @foreach (var period in Model.Overview.Periods)
    {
        <div class="holiday-period">
            <h3>@period.Name @if (period.OffStart is not null) { <small>@period.OffStart 至 @period.OffEnd 放假</small> }</h3>
            @foreach (var makeup in period.MakeupDays)
            {
                var dayName = HolidaysModel.WeekdayNames[(int)DateTime.Parse(makeup.Date).DayOfWeek == 0 ? 7 : (int)DateTime.Parse(makeup.Date).DayOfWeek];
                var autoLabel = makeup.AutoWeekday is { } auto ? $"自动（{HolidaysModel.WeekdayNames[auto]}）" : "自动（无法推算）";
                var selected = makeup.FollowSource switch
                {
                    HolidayFollowSources.Manual => makeup.FollowWeekday!.Value.ToString(),
                    HolidayFollowSources.Skip => "skip",
                    _ => "auto",
                };
                <form method="post" asp-page-handler="Override" class="holiday-makeup-row @(makeup.FollowSource == HolidayFollowSources.Unresolved ? "unresolved" : null)">
                    <input type="hidden" name="date" value="@makeup.Date" />
                    <span>@makeup.Date（@dayName）调休上学</span>
                    <select name="follow" aria-label="@makeup.Date 补课安排">
                        <option value="auto" selected="@(selected == "auto")">@autoLabel</option>
                        @for (var weekday = 1; weekday <= 5; weekday++)
                        {
                            <option value="@weekday" selected="@(selected == weekday.ToString())">上@(HolidaysModel.WeekdayNames[weekday])的课</option>
                        }
                        <option value="skip" selected="@(selected == "skip")">不补课</option>
                    </select>
                    <button type="submit" class="secondary">保存</button>
                    @if (makeup.FollowSource == HolidayFollowSources.Unresolved) { <span class="holiday-error">无法自动推算，请手动指定</span> }
                </form>
            }
        </div>
    }
    @if (Model.Overview.StaleOverrideDates.Count > 0)
    {
        <p class="muted">以下手动安排已失效（这些日期已不是调休上学日）：@string.Join("、", Model.Overview.StaleOverrideDates)</p>
    }
</section>
```

- [ ] **Step 5: Navigation, schedule tags and styles**

In `_Layout.cshtml`, immediately after the `/LoginSettings` nav anchor, add:

```cshtml
                        <a class="@NavClass("/Holidays")" aria-current="@CurrentPage("/Holidays")" asp-page="/Holidays" data-search-label="调休 节假日 放假 补课 国庆 春节"><svg class="nav-icon" viewBox="0 0 24 24" aria-hidden="true"><g fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="2"><rect width="18" height="18" x="3" y="4" rx="2"/><path d="M16 2v4M8 2v4M3 10h18M9 16l2 2l4-4"/></g></svg><span>调休</span></a>
```

In `_ScheduleTable.cshtml`, inside the `<th>` for each day, after `<small>@day.Date</small>` add:

```cshtml
@if (day.DayKind == RemoteCI.Shared.Models.ScheduleDayKinds.Holiday) { <em class="schedule-day-tag holiday">放假 · @day.HolidayName</em> } else if (day.DayKind == RemoteCI.Shared.Models.ScheduleDayKinds.Makeup) { <em class="schedule-day-tag makeup">调休 · @day.HolidayName</em> }
```

Append to `server/RemoteCI.Server/wwwroot/app.css`（变量 `--danger`、`--success`、`--muted` 均已在该文件中使用）：

```css
.schedule-day-tag { display: block; font-style: normal; font-size: .75rem; margin-top: .15rem; }
.schedule-day-tag.holiday { color: var(--danger); }
.schedule-day-tag.makeup { color: var(--success); }
.holiday-period + .holiday-period { margin-top: 1rem; }
.holiday-period h3 small { font-weight: normal; margin-left: .5rem; color: var(--muted); }
.holiday-makeup-row { display: flex; flex-wrap: wrap; align-items: center; gap: .5rem; margin: .35rem 0; }
.holiday-makeup-row.unresolved span:first-of-type, .holiday-error { color: var(--danger); }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test server/tests/RemoteCI.Server.Tests -nodeReuse:false --filter "FullyQualifiedName~HolidayPageTests|FullyQualifiedName~ExtensionGroupPages|FullyQualifiedName~StatusCodePage"`
Expected: 全部通过。

- [ ] **Step 7: Commit**

```bash
git add server
git commit -m "feat(webui): 新增调休管理页并在课表上标注放假与调休"
```

---

### Task 7: 插件日历缓存、消息接收与课表标记

**Files:**
- Create: `plugin/RemoteCI.Plugin/Services/HolidayCalendarStore.cs`
- Modify: `plugin/RemoteCI.Plugin/Services/CloudClient.cs`、`plugin/RemoteCI.Plugin/Services/ScheduleCatalog.cs`
- Test: `plugin/tests/RemoteCI.Plugin.Tests/HolidayCalendarStoreTests.cs`；在 `plugin/tests/RemoteCI.Plugin.Tests/SchedulePipelineTests.cs` 中追加用例

**Interfaces:**
- Consumes: Task 1 模型。
- Produces:
  - `interface IHolidayCalendarLookup { HolidayCalendarDay? Find(DateTime date); }`
  - `sealed class HolidayCalendarStore(string path) : IHolidayCalendarLookup`：`HolidayCalendar Current`、`event Action? Changed`、`void Apply(HolidayCalendar calendar)`
  - `static class HolidayCalendarMessage { static bool TryRead(Envelope envelope, out HolidayCalendar calendar); }`
  - `CloudClient.HolidayCalendarReceived`（`event Action<HolidayCalendar>?`）
  - `ScheduleCatalog(IScheduleBackend backend, IHolidayCalendarLookup? holidays = null)`

- [ ] **Step 1: Write the failing tests**

Create `plugin/tests/RemoteCI.Plugin.Tests/HolidayCalendarStoreTests.cs`:

```csharp
using System.Text.Json;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class HolidayCalendarStoreTests
{
    internal static HolidayCalendar National(int followWeekday = 3) => new()
    {
        Enabled = true,
        Days =
        [
            new HolidayCalendarDay { Date = "2026-10-01", Kind = HolidayDayKinds.Off, Name = "国庆节" },
            new HolidayCalendarDay { Date = "2026-10-07", Kind = HolidayDayKinds.Off, Name = "国庆节" },
            new HolidayCalendarDay { Date = "2026-10-10", Kind = HolidayDayKinds.Makeup, Name = "国庆节", FollowWeekday = followWeekday, FollowSource = HolidayFollowSources.Auto },
        ],
    };

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "HolidayCalendar.json");

    [Fact]
    public void Apply_PersistsAndReloads()
    {
        var path = TempPath();
        var store = new HolidayCalendarStore(path);
        var changed = 0;
        store.Changed += () => changed++;

        store.Apply(National());
        var reloaded = new HolidayCalendarStore(path);

        Assert.Equal(1, changed);
        Assert.Equal(HolidayDayKinds.Makeup, reloaded.Find(new DateTime(2026, 10, 10, 8, 0, 0))?.Kind);
        Assert.Null(reloaded.Find(new DateTime(2026, 10, 9)));
    }

    [Fact]
    public void DisabledCalendar_FindsNothing()
    {
        var store = new HolidayCalendarStore(TempPath());
        var calendar = National();
        calendar.Enabled = false;

        store.Apply(calendar);

        Assert.Null(store.Find(new DateTime(2026, 10, 1)));
    }

    [Fact]
    public void CorruptFile_StartsEmpty()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{not json");

        Assert.Empty(new HolidayCalendarStore(path).Current.Days);
    }

    [Fact]
    public void Message_ReadsCalendarFromEnvelope()
    {
        var wire = JsonSerializer.Serialize(Envelope.HolidayCalendar(National()), JsonDefaults.Options);
        var envelope = JsonSerializer.Deserialize<Envelope>(wire, JsonDefaults.Options)!;

        Assert.True(HolidayCalendarMessage.TryRead(envelope, out var calendar));
        Assert.Equal(3, calendar.Days.Single(x => x.Kind == HolidayDayKinds.Makeup).FollowWeekday);
        Assert.False(HolidayCalendarMessage.TryRead(Envelope.AccountSync(new AccountSync()), out _));
    }
}
```

Append to `SchedulePipelineTests`（复用该文件中已有的 `FakeBackend`；如果它的构造方式不同，就按现有用例的写法准备一份含两节课的课表）：

```csharp
    private sealed class FixedLookup(params HolidayCalendarDay[] days) : IHolidayCalendarLookup
    {
        public HolidayCalendarDay? Find(DateTime date) => days.FirstOrDefault(x => x.Date == date.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void BuildDay_MarksHolidayAndMakeupDays()
    {
        var backend = new FakeBackend();   // 按本文件已有用例的方式填充一份含课程的课表
        var plain = new ScheduleCatalog(backend);
        var marked = new ScheduleCatalog(backend, new FixedLookup(
            new HolidayCalendarDay { Date = "2026-10-01", Kind = HolidayDayKinds.Off, Name = "国庆节" },
            new HolidayCalendarDay { Date = "2026-10-10", Kind = HolidayDayKinds.Makeup, Name = "国庆节", FollowWeekday = 3 }));

        var off = marked.BuildDay(new DateTime(2026, 10, 1));
        var makeup = marked.BuildDay(new DateTime(2026, 10, 10));
        var normal = marked.BuildDay(new DateTime(2026, 10, 9));

        Assert.False(off.Enabled);
        Assert.Empty(off.Courses);
        Assert.Equal((ScheduleDayKinds.Holiday, "国庆节"), (off.DayKind, off.HolidayName));
        Assert.NotEqual(plain.BuildDay(new DateTime(2026, 10, 1)).Revision, off.Revision);
        Assert.Equal(ScheduleDayKinds.Makeup, makeup.DayKind);
        Assert.Equal(plain.BuildDay(new DateTime(2026, 10, 10)).Courses.Count, makeup.Courses.Count);
        Assert.Null(normal.DayKind);
        Assert.Equal(plain.BuildDay(new DateTime(2026, 10, 9)).Revision, normal.Revision);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test plugin/tests/RemoteCI.Plugin.Tests -nodeReuse:false --filter "FullyQualifiedName~HolidayCalendarStoreTests|FullyQualifiedName~SchedulePipelineTests"`
Expected: 编译失败。

- [ ] **Step 3: Implement the store and message reader**

Create `plugin/RemoteCI.Plugin/Services/HolidayCalendarStore.cs`:

```csharp
using System.Text.Json;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

public interface IHolidayCalendarLookup
{
    HolidayCalendarDay? Find(DateTime date);
}

/// <summary>服务端最近一次下发的调休日历；落盘缓存，断网或服务端离线时继续按它生效。</summary>
public sealed class HolidayCalendarStore : IHolidayCalendarLookup
{
    private readonly string _path;
    private readonly object _lock = new();
    private HolidayCalendar _current;
    private Dictionary<string, HolidayCalendarDay> _index;

    public HolidayCalendarStore(string path)
    {
        _path = path;
        _current = Load(path);
        _index = BuildIndex(_current);
    }

    public event Action? Changed;

    public HolidayCalendar Current { get { lock (_lock) return _current; } }

    public HolidayCalendarDay? Find(DateTime date)
    {
        lock (_lock) return _index.GetValueOrDefault(date.ToString("yyyy-MM-dd"));
    }

    public void Apply(HolidayCalendar calendar)
    {
        lock (_lock)
        {
            _current = calendar;
            _index = BuildIndex(calendar);
            Persist(calendar);
        }
        Changed?.Invoke();
    }

    private static Dictionary<string, HolidayCalendarDay> BuildIndex(HolidayCalendar calendar) =>
        calendar.Enabled
            ? calendar.Days.GroupBy(x => x.Date).ToDictionary(x => x.Key, x => x.First())
            : [];

    private void Persist(HolidayCalendar calendar)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(calendar, JsonDefaults.Options));
            File.Move(temporary, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存写不进去时只在本次运行内生效，不能因此影响插件其他功能。
        }
    }

    private static HolidayCalendar Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<HolidayCalendar>(File.ReadAllText(path), JsonDefaults.Options) ?? new HolidayCalendar()
                : new HolidayCalendar();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new HolidayCalendar();
        }
    }
}

/// <summary>从信封中取出调休日历；单独成类以便不经网络直接测试。</summary>
public static class HolidayCalendarMessage
{
    public static bool TryRead(Envelope envelope, out HolidayCalendar calendar)
    {
        calendar = null!;
        if (envelope.Type != Protocol.MessageTypeHolidayCalendar || envelope.Payload is null) return false;
        try
        {
            calendar = JsonSerializer.Deserialize<HolidayCalendar>(
                JsonSerializer.Serialize(envelope.Payload), JsonDefaults.Options)!;
            return calendar is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 4: Wire CloudClient and ScheduleCatalog**

In `CloudClient.cs` add next to the other events:

```csharp
    /// <summary>收到服务端下发的调休日历时触发（在接收线程上，订阅方自行切换到 UI 线程）。</summary>
    public event Action<HolidayCalendar>? HolidayCalendarReceived;
```

In `HandleMessageAsync`, right after the `MessageTypeAccountSync` block:

```csharp
        if (HolidayCalendarMessage.TryRead(envelope, out var holidayCalendar))
        {
            HolidayCalendarReceived?.Invoke(holidayCalendar);
            return;
        }
```

In `ScheduleCatalog.cs`, change the primary constructor to `ScheduleCatalog(IScheduleBackend backend, IHolidayCalendarLookup? holidays = null)` and replace `BuildDay`/`ComputeRevision`:

```csharp
    public ScheduleDay BuildDay(DateTime date)
    {
        var day = date.Date;
        var plan = backend.GetClassPlan(day, out var planId);
        var holiday = holidays?.Find(day);
        // 放假日宿主课表开关已被关闭，按规则匹配到的课表并不会真的上，因此不再上报课程，也不允许换课。
        var isOff = holiday?.Kind == HolidayDayKinds.Off;
        var result = new ScheduleDay
        {
            Date = day.ToString("yyyy-MM-dd"),
            ClassPlanName = isOff ? null : plan?.Name,
            Enabled = !isOff && plan is not null,
            Courses = isOff ? [] : plan?.Classes.Select((course, index) => ToCourse(course, index)).ToList() ?? [],
            DayKind = holiday?.Kind switch
            {
                HolidayDayKinds.Off => ScheduleDayKinds.Holiday,
                HolidayDayKinds.Makeup => ScheduleDayKinds.Makeup,
                _ => null,
            },
            HolidayName = holiday?.Name,
        };
        result.Revision = ComputeRevision(result, planId);
        return result;
    }
```

and in `ComputeRevision`, after building `canonical` with the courses, append:

```csharp
        // 只在有标记时参与计算，普通日期的修订号与旧版本保持一致。
        if (day.DayKind is not null)
            canonical.Append("|day:").Append(day.DayKind).Append(':').Append(day.HolidayName);
```

Add `using RemoteCI.Shared.Models;` if missing (it is already imported in `ScheduleCatalog.cs`).

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test plugin/tests/RemoteCI.Plugin.Tests -nodeReuse:false`
Expected: 全部通过（包括原有的 `ScheduleChangeExecutorTests`：放假日 `Enabled=false` 会让换课返回 `ScheduleUnavailable`，这正是期望的行为）。

- [ ] **Step 6: Commit**

```bash
git add plugin
git commit -m "feat(plugin): 缓存调休日历并在上报课表时标注放假与调休"
```

---

### Task 8: HolidayScheduleApplier 核心逻辑

**Files:**
- Create: `plugin/RemoteCI.Plugin/Services/HolidayScheduleApplier.cs`
- Test: `plugin/tests/RemoteCI.Plugin.Tests/HolidayScheduleApplierTests.cs`

**Interfaces:**
- Consumes: Task 1 模型；Task 7 测试中的 `HolidayCalendarStoreTests.National(...)`。
- Produces:
  - `interface IHolidayHostOperations { bool? IsClassPlanEnabled { get; set; } ClassPlan? GetClassPlan(DateTime date, out Guid? planId); Guid? GetOrderedSchedulePlanId(DateTime date); Guid? CreateTempClassPlan(Guid sourcePlanId, DateTime date); void RemoveOrderedSchedule(DateTime date, Guid planId); void SaveProfile(); }`
  - `sealed class HolidayStateFile(string path)`：`IReadOnlyDictionary<string, MakeupRecord> Makeups`、`void Set(string date, MakeupRecord record)`、`void Remove(string date)`、`void Save()`；`sealed record MakeupRecord(Guid PlanId, int FollowWeekday)`
  - `sealed class HolidayScheduleApplier(IHolidayHostOperations host, HolidayStateFile state, ILogger<HolidayScheduleApplier> logger)`：`const int MakeupLookaheadDays = 7`、`bool Apply(HolidayCalendar calendar, DateTime now)`（课表有变化时返回 true）、`static DateTime SourceDate(DateTime makeupDate, int followWeekday)`

- [ ] **Step 1: Write the failing tests**

Create `plugin/tests/RemoteCI.Plugin.Tests/HolidayScheduleApplierTests.cs`:

```csharp
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class HolidayScheduleApplierTests
{
    private sealed class FakeHost : IHolidayHostOperations
    {
        public bool? IsClassPlanEnabled { get; set; } = true;
        public Dictionary<DateTime, Guid> Ordered { get; } = [];
        public Dictionary<DayOfWeek, Guid> PlanIds { get; } = Enum.GetValues<DayOfWeek>()
            .Where(x => x is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            .ToDictionary(x => x, _ => Guid.NewGuid());
        public List<(Guid Source, DateTime Date)> Created { get; } = [];
        public List<DateTime> Removed { get; } = [];
        public int SaveCount { get; private set; }
        public bool ThrowOnSave { get; set; }

        public ClassPlan? GetClassPlan(DateTime date, out Guid? planId)
        {
            planId = PlanIds.TryGetValue(date.DayOfWeek, out var id) ? id : null;
            return planId is null ? null : new ClassPlan { Name = date.DayOfWeek.ToString() };
        }

        public Guid? GetOrderedSchedulePlanId(DateTime date) => Ordered.TryGetValue(date.Date, out var id) ? id : null;

        public Guid? CreateTempClassPlan(Guid sourcePlanId, DateTime date)
        {
            var id = Guid.NewGuid();
            Created.Add((sourcePlanId, date.Date));
            Ordered[date.Date] = id;
            return id;
        }

        public void RemoveOrderedSchedule(DateTime date, Guid planId)
        {
            if (Ordered.TryGetValue(date.Date, out var id) && id == planId) Ordered.Remove(date.Date);
            Removed.Add(date.Date);
        }

        public void SaveProfile()
        {
            if (ThrowOnSave) throw new IOException("disk full");
            SaveCount++;
        }
    }

    private static string StatePath() =>
        Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "HolidayState.json");

    private static HolidayScheduleApplier Create(FakeHost host, string? statePath = null) =>
        new(host, new HolidayStateFile(statePath ?? StatePath()), NullLogger<HolidayScheduleApplier>.Instance);

    private static readonly DateTime Oct1 = new(2026, 10, 1, 7, 30, 0);
    private static readonly DateTime Oct8 = new(2026, 10, 8, 7, 30, 0);

    [Fact]
    public void OffDay_DisablesClassPlanAndRestoresNextSchoolDay()
    {
        var host = new FakeHost();
        var applier = Create(host);

        applier.Apply(HolidayCalendarStoreTests.National(), Oct1);
        Assert.False(host.IsClassPlanEnabled);

        applier.Apply(HolidayCalendarStoreTests.National(), Oct8);
        Assert.True(host.IsClassPlanEnabled);
    }

    [Fact]
    public void OffDay_DoesNotRestoreSwitchTurnedOffByUser()
    {
        var host = new FakeHost { IsClassPlanEnabled = false };
        var applier = Create(host);

        applier.Apply(HolidayCalendarStoreTests.National(), Oct1);
        applier.Apply(HolidayCalendarStoreTests.National(), Oct8);

        Assert.False(host.IsClassPlanEnabled);
    }

    [Fact]
    public void OffDay_ManualReenableSameDayIsRespected()
    {
        var host = new FakeHost();
        var applier = Create(host);
        applier.Apply(HolidayCalendarStoreTests.National(), Oct1);

        host.IsClassPlanEnabled = true;
        applier.Apply(HolidayCalendarStoreTests.National(), Oct1.AddHours(2));

        Assert.True(host.IsClassPlanEnabled);
    }

    [Fact]
    public void OffDay_UnsupportedHostSkipsSwitchButStillCreatesMakeup()
    {
        var host = new FakeHost { IsClassPlanEnabled = null };

        Create(host).Apply(HolidayCalendarStoreTests.National(), new DateTime(2026, 10, 7, 8, 0, 0));

        Assert.Null(host.IsClassPlanEnabled);
        Assert.Single(host.Created);
    }

    [Theory]
    [InlineData("2026-10-10", 3, "2026-10-07")]
    [InlineData("2026-09-20", 2, "2026-09-15")]
    [InlineData("2026-02-28", 1, "2026-02-23")]
    public void SourceDate_UsesSameMondayBasedWeek(string makeup, int weekday, string expected)
    {
        Assert.Equal(DateTime.Parse(expected), HolidayScheduleApplier.SourceDate(DateTime.Parse(makeup), weekday));
    }

    [Fact]
    public void Makeup_CreatesOnce_EvenAcrossInstances()
    {
        var host = new FakeHost();
        var statePath = StatePath();
        var today = new DateTime(2026, 10, 7, 8, 0, 0);

        Create(host, statePath).Apply(HolidayCalendarStoreTests.National(), today);
        Create(host, statePath).Apply(HolidayCalendarStoreTests.National(), today);

        var created = Assert.Single(host.Created);
        Assert.Equal((host.PlanIds[DayOfWeek.Wednesday], new DateTime(2026, 10, 10)), created);
        Assert.Equal(1, host.SaveCount);
    }

    [Fact]
    public void Makeup_LeavesForeignOrderedScheduleAlone()
    {
        var host = new FakeHost();
        var foreign = Guid.NewGuid();
        host.Ordered[new DateTime(2026, 10, 10)] = foreign;

        Create(host).Apply(HolidayCalendarStoreTests.National(), new DateTime(2026, 10, 7));

        Assert.Empty(host.Created);
        Assert.Equal(foreign, host.Ordered[new DateTime(2026, 10, 10)]);
    }

    [Fact]
    public void Makeup_OutsideLookaheadIsNotCreated()
    {
        var host = new FakeHost();

        Create(host).Apply(HolidayCalendarStoreTests.National(), new DateTime(2026, 10, 2));

        Assert.Empty(host.Created);
    }

    [Fact]
    public void Makeup_WeekdayChangeRebuilds()
    {
        var host = new FakeHost();
        var applier = Create(host);
        var today = new DateTime(2026, 10, 7);
        applier.Apply(HolidayCalendarStoreTests.National(followWeekday: 3), today);

        applier.Apply(HolidayCalendarStoreTests.National(followWeekday: 5), today);

        Assert.Equal([new DateTime(2026, 10, 10)], host.Removed);
        Assert.Equal(host.PlanIds[DayOfWeek.Friday], host.Created[^1].Source);
        Assert.Equal(2, host.Created.Count);
    }

    [Fact]
    public void Makeup_SkipRemovesOurPlan()
    {
        var host = new FakeHost();
        var applier = Create(host);
        var today = new DateTime(2026, 10, 7);
        applier.Apply(HolidayCalendarStoreTests.National(), today);
        var skip = HolidayCalendarStoreTests.National();
        skip.Days.Single(x => x.Kind == HolidayDayKinds.Makeup).FollowWeekday = null;

        applier.Apply(skip, today);

        Assert.False(host.Ordered.ContainsKey(new DateTime(2026, 10, 10)));
    }

    [Fact]
    public void DisabledCalendar_RemovesFuturePlansAndRestoresSwitch()
    {
        var host = new FakeHost();
        var applier = Create(host);
        applier.Apply(HolidayCalendarStoreTests.National(), new DateTime(2026, 10, 7));
        Assert.False(host.IsClassPlanEnabled);

        applier.Apply(new HolidayCalendar { Enabled = false }, new DateTime(2026, 10, 7, 9, 0, 0));

        Assert.True(host.IsClassPlanEnabled);
        Assert.Empty(host.Ordered);
    }

    [Fact]
    public void Makeup_SaveFailureRollsBackAndRetriesLater()
    {
        var host = new FakeHost { ThrowOnSave = true };
        var applier = Create(host);
        var today = new DateTime(2026, 10, 7);

        applier.Apply(HolidayCalendarStoreTests.National(), today);
        Assert.Empty(host.Ordered);

        host.ThrowOnSave = false;
        applier.Apply(HolidayCalendarStoreTests.National(), today);
        Assert.Single(host.Ordered);
    }

    [Fact]
    public void PastRecords_AreForgottenWithoutTouchingProfile()
    {
        var host = new FakeHost();
        var statePath = StatePath();
        Create(host, statePath).Apply(HolidayCalendarStoreTests.National(), new DateTime(2026, 10, 7));

        Create(host, statePath).Apply(HolidayCalendarStoreTests.National(), new DateTime(2026, 10, 12));

        Assert.Empty(host.Removed);
        Assert.Empty(new HolidayStateFile(statePath).Makeups);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test plugin/tests/RemoteCI.Plugin.Tests -nodeReuse:false --filter "FullyQualifiedName~HolidayScheduleApplierTests"`
Expected: 编译失败。

- [ ] **Step 3: Implement**

Create `plugin/RemoteCI.Plugin/Services/HolidayScheduleApplier.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>调休所需的宿主能力（ClassIsland 服务含 internal 成员，外部程序集无法实现假对象，故经此防腐层）。</summary>
public interface IHolidayHostOperations
{
    /// <summary>宿主 LessonsService 的“启用课表”运行时开关；宿主不支持时为 null。</summary>
    bool? IsClassPlanEnabled { get; set; }
    ClassPlan? GetClassPlan(DateTime date, out Guid? planId);
    Guid? GetOrderedSchedulePlanId(DateTime date);
    Guid? CreateTempClassPlan(Guid sourcePlanId, DateTime date);
    /// <summary>仅当该日预定课表仍指向 planId 时删除它及对应的临时课表。</summary>
    void RemoveOrderedSchedule(DateTime date, Guid planId);
    void SaveProfile();
}

public sealed record MakeupRecord(Guid PlanId, int FollowWeekday);

/// <summary>插件为调休上学日建立过的临时课表；落盘以便重启后不重复建立，也能在安排变化时撤掉。</summary>
public sealed class HolidayStateFile(string path)
{
    private readonly Dictionary<string, MakeupRecord> _makeups = Load(path);

    public IReadOnlyDictionary<string, MakeupRecord> Makeups => _makeups;
    public void Set(string date, MakeupRecord record) => _makeups[date] = record;
    public void Remove(string date) => _makeups.Remove(date);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new StateDocument { Makeups = _makeups }, JsonDefaults.Options));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去时本次运行内仍按内存状态工作；最坏情况是重启后把同一天的临时课表识别为“他人安排”而不再处理。
        }
    }

    private static Dictionary<string, MakeupRecord> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<StateDocument>(File.ReadAllText(path), JsonDefaults.Options)?.Makeups ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed class StateDocument
    {
        public Dictionary<string, MakeupRecord> Makeups { get; set; } = [];
    }
}

/// <summary>
/// 按调休日历调整 ClassIsland：放假日关闭课表（只恢复自己关掉的），调休上学日建立临时课表（不碰他人安排）。
/// 必须在 UI 线程调用。
/// </summary>
public sealed class HolidayScheduleApplier(
    IHolidayHostOperations host, HolidayStateFile state, ILogger<HolidayScheduleApplier> logger)
{
    public const int MakeupLookaheadDays = 7;
    private bool _disabledByUs;
    private DateTime? _offDayHandled;
    private bool _warnedUnsupported;

    /// <summary>课表内容有变化（建立或撤销了临时课表、切换了开关）时返回 true，调用方据此重新上报课表。</summary>
    public bool Apply(HolidayCalendar calendar, DateTime now)
    {
        var today = now.Date;
        var days = calendar.Enabled
            ? calendar.Days.GroupBy(x => x.Date).ToDictionary(x => x.Key, x => x.First())
            : new Dictionary<string, HolidayCalendarDay>();
        var changed = ApplyOffDay(days, today);
        var stateChanged = CleanupMakeups(days, today, ref changed);
        stateChanged |= CreateMakeups(days, today, ref changed);
        if (stateChanged) state.Save();
        return changed;
    }

    /// <summary>调休上学日所在周（周一为第一天）中对应工作日的日期。</summary>
    public static DateTime SourceDate(DateTime makeupDate, int followWeekday)
    {
        var monday = makeupDate.Date.AddDays(-(((int)makeupDate.DayOfWeek + 6) % 7));
        return monday.AddDays(followWeekday - 1);
    }

    private bool ApplyOffDay(Dictionary<string, HolidayCalendarDay> days, DateTime today)
    {
        var isOff = days.TryGetValue(Key(today), out var day) && day.Kind == HolidayDayKinds.Off;
        if (isOff)
        {
            // 同一天只处理一次：老师当天手动重新打开课表后，插件不再把它关掉。
            if (_offDayHandled == today) return false;
            _offDayHandled = today;
            switch (host.IsClassPlanEnabled)
            {
                case null:
                    if (!_warnedUnsupported) logger.LogWarning("当前 ClassIsland 不支持切换“启用课表”，放假日无法自动关闭课表");
                    _warnedUnsupported = true;
                    return false;
                case true:
                    host.IsClassPlanEnabled = false;
                    _disabledByUs = true;
                    logger.LogInformation("{Date} 为 {Name} 放假日，已关闭课表", Key(today), day!.Name);
                    return true;
                default:
                    return false;
            }
        }

        _offDayHandled = null;
        if (!_disabledByUs) return false;
        _disabledByUs = false;
        if (host.IsClassPlanEnabled != false) return false;
        host.IsClassPlanEnabled = true;
        logger.LogInformation("放假结束，已恢复课表");
        return true;
    }

    private bool CleanupMakeups(Dictionary<string, HolidayCalendarDay> days, DateTime today, ref bool changed)
    {
        var stateChanged = false;
        foreach (var (key, record) in state.Makeups.ToList())
        {
            if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date < today)
            {
                state.Remove(key);
                stateChanged = true;
                continue;
            }
            var desired = days.TryGetValue(key, out var day) && day.Kind == HolidayDayKinds.Makeup ? day.FollowWeekday : null;
            if (desired == record.FollowWeekday) continue;
            if (host.GetOrderedSchedulePlanId(date) == record.PlanId)
            {
                host.RemoveOrderedSchedule(date, record.PlanId);
                host.SaveProfile();
                changed = true;
                logger.LogInformation("{Date} 的调休安排已变化，已撤销之前建立的临时课表", key);
            }
            state.Remove(key);
            stateChanged = true;
        }
        return stateChanged;
    }

    private bool CreateMakeups(Dictionary<string, HolidayCalendarDay> days, DateTime today, ref bool changed)
    {
        var stateChanged = false;
        for (var offset = 0; offset < MakeupLookaheadDays; offset++)
        {
            var date = today.AddDays(offset);
            var key = Key(date);
            if (!days.TryGetValue(key, out var day) || day.Kind != HolidayDayKinds.Makeup || day.FollowWeekday is not { } weekday)
                continue;
            if (state.Makeups.ContainsKey(key) || host.GetOrderedSchedulePlanId(date) is not null)
                continue;
            var source = SourceDate(date, weekday);
            if (host.GetClassPlan(source, out var sourceId) is null || sourceId is null)
            {
                logger.LogWarning("{Date} 调休应上 {Source} 的课，但该日没有可用课表", key, Key(source));
                continue;
            }
            if (host.CreateTempClassPlan(sourceId.Value, date) is not { } overlayId)
            {
                logger.LogWarning("{Date} 无法建立调休临时课表", key);
                continue;
            }
            try
            {
                host.SaveProfile();
            }
            catch (Exception ex)
            {
                host.RemoveOrderedSchedule(date, overlayId);
                logger.LogError(ex, "保存 {Date} 调休临时课表失败，已撤销，稍后重试", key);
                continue;
            }
            state.Set(key, new MakeupRecord(overlayId, weekday));
            stateChanged = true;
            changed = true;
            logger.LogInformation("{Date} 调休上学，已建立临时课表（上 {Source} 的课）", key, Key(source));
        }
        return stateChanged;
    }

    private static string Key(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test plugin/tests/RemoteCI.Plugin.Tests -nodeReuse:false --filter "FullyQualifiedName~HolidayScheduleApplierTests"`
Expected: 全部通过。

- [ ] **Step 5: Commit**

```bash
git add plugin
git commit -m "feat(plugin): 放假日关闭课表并为调休上学日建立临时课表"
```

---

### Task 9: 宿主适配与插件接线

**Files:**
- Create: `plugin/RemoteCI.Plugin/Services/ClassIslandHolidayHost.cs`
- Modify: `plugin/RemoteCI.Plugin/Plugin.cs`、`plugin/RemoteCI.Plugin/Services/RemoteCiService.cs`
- Test: `plugin/tests/RemoteCI.Plugin.Tests/ClassIslandHolidayHostTests.cs`

**Interfaces:**
- Consumes: Task 7 的 `HolidayCalendarStore`、`CloudClient.HolidayCalendarReceived`；Task 8 的 `HolidayScheduleApplier`、`IHolidayHostOperations`、`HolidayStateFile`。
- Produces: `sealed class ClassIslandHolidayHost(ILessonsService lessons, IProfileService profiles) : IHolidayHostOperations`；`static class ClassPlanSwitch { static PropertyInfo? Resolve(object lessons); }`

- [ ] **Step 1: Write the failing tests**

Create `plugin/tests/RemoteCI.Plugin.Tests/ClassIslandHolidayHostTests.cs`:

```csharp
using ClassIsland.Shared.Models.Profile;
using RemoteCI.Plugin;
using RemoteCI.Plugin.Services;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class ClassIslandHolidayHostTests
{
    private sealed class LessonsWithSwitch { public bool IsClassPlanEnabled { get; set; } = true; }
    private sealed class LessonsWithoutSwitch { }

    [Fact]
    public void ClassPlanSwitch_ResolvesWritableBoolProperty()
    {
        var lessons = new LessonsWithSwitch();
        var property = ClassPlanSwitch.Resolve(lessons);

        Assert.NotNull(property);
        property!.SetValue(lessons, false);
        Assert.False(lessons.IsClassPlanEnabled);
        Assert.Null(ClassPlanSwitch.Resolve(new LessonsWithoutSwitch()));
    }

    [Fact]
    public void Profile_ExposesWritableOrderedSchedulesAndClassPlans()
    {
        var profile = new Profile();

        var ordered = HostApiCompat.ReadProperty<IDictionary<DateTime, OrderedSchedule>>(profile, "OrderedSchedules");
        var plans = HostApiCompat.ReadProperty<IDictionary<Guid, ClassPlan>>(profile, "ClassPlans");
        var id = Guid.NewGuid();
        ordered[new DateTime(2026, 10, 10)] = new OrderedSchedule { ClassPlanId = id };
        plans[id] = new ClassPlan { IsOverlay = true };

        Assert.True(ordered.Remove(new DateTime(2026, 10, 10)));
        Assert.True(plans.Remove(id));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test plugin/tests/RemoteCI.Plugin.Tests -nodeReuse:false --filter "FullyQualifiedName~ClassIslandHolidayHostTests"`
Expected: 编译失败（`ClassPlanSwitch` 不存在）。如果第二个用例在实现之后仍然失败（例如 `OrderedSchedules` 不能转换成 `IDictionary<DateTime, OrderedSchedule>`），就按异常信息里的实际类型改用对应接口，并同步修改 Step 3 中的适配器代码。

- [ ] **Step 3: Implement the adapter**

Create `plugin/RemoteCI.Plugin/Services/ClassIslandHolidayHost.cs`:

```csharp
using System.Reflection;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// “启用课表”开关是宿主 LessonsService 的公开运行时属性（不在 ILessonsService 接口上、也不落盘），
/// 因此按名称反射访问；宿主改名或移除时返回 null，放假日处理自动降级。
/// </summary>
public static class ClassPlanSwitch
{
    public static PropertyInfo? Resolve(object lessons) =>
        lessons.GetType().GetProperty("IsClassPlanEnabled", BindingFlags.Instance | BindingFlags.Public) is
            { CanRead: true, CanWrite: true } property && property.PropertyType == typeof(bool)
            ? property
            : null;
}

public sealed class ClassIslandHolidayHost(ILessonsService lessons, IProfileService profiles) : IHolidayHostOperations
{
    private readonly PropertyInfo? _switch = ClassPlanSwitch.Resolve(lessons);

    public bool? IsClassPlanEnabled
    {
        get => _switch?.GetValue(lessons) as bool?;
        set
        {
            if (value is { } enabled) _switch?.SetValue(lessons, enabled);
        }
    }

    public ClassPlan? GetClassPlan(DateTime date, out Guid? planId) => lessons.GetClassPlanByDate(date.Date, out planId);

    public Guid? GetOrderedSchedulePlanId(DateTime date) =>
        OrderedSchedules.TryGetValue(date.Date, out var schedule) ? schedule.ClassPlanId : null;

    public Guid? CreateTempClassPlan(Guid sourcePlanId, DateTime date) =>
        profiles.CreateTempClassPlan(sourcePlanId, enableDateTime: date.Date);

    public void RemoveOrderedSchedule(DateTime date, Guid planId)
    {
        var ordered = OrderedSchedules;
        if (!ordered.TryGetValue(date.Date, out var schedule) || schedule.ClassPlanId != planId) return;
        ordered.Remove(date.Date);
        var plans = HostApiCompat.ReadProperty<IDictionary<Guid, ClassPlan>>(profiles.Profile, "ClassPlans");
        // 只删临时层，绝不删除管理员的源课表。
        if (plans.TryGetValue(planId, out var plan) && plan.IsOverlay) plans.Remove(planId);
    }

    public void SaveProfile() => profiles.SaveProfile();

    private IDictionary<DateTime, OrderedSchedule> OrderedSchedules =>
        HostApiCompat.ReadProperty<IDictionary<DateTime, OrderedSchedule>>(profiles.Profile, "OrderedSchedules");
}
```

- [ ] **Step 4: Register services in Plugin.cs**

In `Plugin.Initialize`, after `services.AddSingleton(new AccountMirror(...));` add:

```csharp
        // 调休：缓存服务端下发的日历；放假日关闭课表、调休上学日建立临时课表。
        var holidayCalendar = new HolidayCalendarStore(Path.Combine(PluginConfigFolder, "HolidayCalendar.json"));
        services.AddSingleton(holidayCalendar);
        services.AddSingleton<IHolidayCalendarLookup>(holidayCalendar);
        services.AddSingleton<IHolidayHostOperations, ClassIslandHolidayHost>();
        services.AddSingleton(sp => new HolidayScheduleApplier(
            sp.GetRequiredService<IHolidayHostOperations>(),
            new HolidayStateFile(Path.Combine(PluginConfigFolder, "HolidayState.json")),
            sp.GetRequiredService<ILogger<HolidayScheduleApplier>>()));
```

（`ScheduleCatalog` 已注册为 singleton，DI 会自动注入第二个构造参数 `IHolidayCalendarLookup`。）

- [ ] **Step 5: Wire triggers in RemoteCiService**

Add constructor parameters `HolidayCalendarStore holidayCalendar, HolidayScheduleApplier holidayApplier` (store them in `_holidayCalendar`, `_holidayApplier` fields), plus fields:

```csharp
    private Timer? _holidayTimer;
    private DateTime _holidayAppliedDate;
```

In `Start()`, after `_cloudClient.ConnectionStatusChanged += OnCloudConnectionStatusChanged;`:

```csharp
        _cloudClient.HolidayCalendarReceived += _holidayCalendar.Apply;
        _holidayCalendar.Changed += RunHolidayApply;
        // 每分钟检查一次本地日期，跨天后（含睡眠唤醒）重新应用；启动时立即应用一次缓存的日历。
        _holidayTimer = new Timer(_ => { if (DateTime.Today != _holidayAppliedDate) RunHolidayApply(); },
            null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
```

In `Stop()`, before `_collector.Stop();`:

```csharp
        _holidayTimer?.Dispose();
        _holidayTimer = null;
        _holidayCalendar.Changed -= RunHolidayApply;
        if (_cloudClient is not null) _cloudClient.HolidayCalendarReceived -= _holidayCalendar.Apply;
```

Add the method:

```csharp
    /// <summary>ClassIsland 课表服务只能在 UI 线程读写；调整后强制重新上报，让 WebUI 立刻看到放假/调休标记。</summary>
    private void RunHolidayApply() => Dispatcher.UIThread.Post(() =>
    {
        try
        {
            _holidayAppliedDate = DateTime.Today;
            _holidayApplier.Apply(_holidayCalendar.Current, DateTime.Now);
            _collector.ForceSchedulePush();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "应用调休日历失败");
        }
    });
```

- [ ] **Step 6: Build and run all plugin tests**

Run: `dotnet build plugin/RemoteCI.Plugin -nodeReuse:false` then `dotnet test plugin/tests/RemoteCI.Plugin.Tests -nodeReuse:false`
Expected: 构建无警告新增，测试全部通过。

- [ ] **Step 7: Commit**

```bash
git add plugin
git commit -m "feat(plugin): 接入 ClassIsland 宿主并在启动、收到日历与跨天时应用调休"
```

---

### Task 10: 协议文档与 Skill

**Files:**
- Create: `skills/remoteci/references/holidays.md`
- Modify: `skills/remoteci/SKILL.md`、`docs/protocol.md`

**Interfaces:**
- Consumes: Task 5 的 API 契约。

- [ ] **Step 1: Write the skill reference**

Create `skills/remoteci/references/holidays.md`:

```markdown
# 调休

RemoteCI 服务端自动从 holiday-cn 获取法定节假日和调休安排，并下发给各教室电脑：

- **放假日**：教室 ClassIsland 自动关闭课表。
- **调休上学日**（原本是周末但要上学）：自动开启一份临时课表，上某个工作日的课。

默认按"同一假期内最后几个放掉的工作日"自动推算补哪天的课，管理员可以逐天改。

## 查询（任何已登录账号）

`GET /api/holidays` 返回：

| 字段 | 含义 |
| --- | --- |
| `enabled` | 调休自动适配是否开启 |
| `status.lastSuccessAt` / `status.lastError` | 上次成功刷新时间 / 最近一次失败原因 |
| `periods[]` | 近期假期：`name`、`offStart`、`offEnd`（放假起止），`makeupDays[]` |
| `periods[].makeupDays[]` | `date`、`autoWeekday`（自动推算）、`followWeekday`（最终生效，1=周一 … 5=周五，null 表示不补课/无法推算）、`followSource`（`auto` / `manual` / `skip` / `unresolved`） |
| `staleOverrideDates` | 已失效的手动安排（那天已不是调休上学日） |

回答用户时说清楚：哪几天放假、哪天调休上学、上周几的课。`followSource` 为 `unresolved` 时提醒管理员手动指定。

## 修改（仅系统管理员）

修改前先向用户复述日期和补课安排，得到确认后再调用。

| 操作 | 请求 |
| --- | --- |
| 改成上周 N 的课 | `PUT /api/admin/holidays/overrides/{yyyy-MM-dd}`，body `{"followWeekday": N}`（N 为 1-5） |
| 这天不补课 | 同上，body `{"followWeekday": null}` |
| 恢复自动推算 | `DELETE /api/admin/holidays/overrides/{yyyy-MM-dd}` |
| 开关 / 换数据源 | `PUT /api/admin/holidays/settings`，body `{"enabled": true, "sourceUrlTemplate": null}`（自定义地址必须是包含 `{year}` 的 https 地址） |
| 立即刷新数据 | `POST /api/admin/holidays/refresh` |

日期不是调休上学日、日期格式不对、`followWeekday` 不在 1-5 时返回 400。修改成功后返回最新的总览，以它为准向用户汇报。
```

- [ ] **Step 2: Link it from SKILL.md**

In `skills/remoteci/SKILL.md`, after the "## 6. 系统管理（系统管理员）" section (before "## 错误码"), add:

```markdown
## 7. 调休与节假日

用户问“哪天放假”“哪天调休上学、上周几的课”，或者管理员要修改调休补课安排、关闭调休适配时，先读 [holidays.md](references/holidays.md)。
```

- [ ] **Step 3: Document the protocol**

In `docs/protocol.md`, add a section describing (follow the file's existing heading style; locate the message-type table with `grep -n "user_notify" docs/protocol.md` and add a row next to it):
- message `holiday_calendar` (server → plugin), payload fields `enabled`, `generatedAt`, `days[].date/kind/name/followWeekday/followSource`, the 60-day window, "sent after the plugin reports capability `schedule.holiday-calendar`, and again whenever content changes";
- `ScheduleDay.dayKind` (`holiday` / `makeup`) and `holidayName`, omitted on normal days; holiday days have `enabled: false` and no courses.

- [ ] **Step 4: Commit**

```bash
git add skills docs/protocol.md
git commit -m "docs: 补充调休协议说明与 Skill 参考"
```

---

### Task 11: AstrBot 插件

**Files（在 AstrBot 插件的独立 worktree 中）:**
- Create: `remoteci/makeup.py`、`tests/test_makeup.py`、`remoteci/skill/references/holidays.md`
- Modify: `remoteci/commands.py`、`main.py`、`remoteci/skill/SKILL.md`

**Interfaces:**
- Consumes: Task 5 的 API；`RemoteCiService._call(binding, method, path, *, params=None, body=None)`、`service.require_binding(user)`、`RemoteCiError`（来自 `remoteci/client.py`）。
- Produces: `format_overview(data: dict) -> str`、`parse_follow(word: str) -> tuple[str, int | None]`、`async list_makeup(service, binding) -> str`、`async set_makeup(service, binding, day: str, follow: str) -> str`、`async run_makeup_command(service, user, args) -> str`；指令 `/rci 调休`；LLM 工具 `remoteci_holidays`、`remoteci_set_makeup`。

- [ ] **Step 1: Create the worktree**

```bash
git -C /d/Files/Codes/Projects/astrbot_plugin_remoteci worktree add ../astrbot_plugin_remoteci-holiday -b feat/holiday-makeup
```

后续步骤都在 `D:\Files\Codes\Projects\astrbot_plugin_remoteci-holiday` 中进行。

- [ ] **Step 2: Write the failing tests**

Create `tests/test_makeup.py`:

```python
import asyncio

import pytest

from remoteci.makeup import format_overview, parse_follow, set_makeup

OVERVIEW = {
    "enabled": True,
    "status": {"lastSuccessAt": "2026-10-08T08:00:00+08:00", "lastError": None},
    "periods": [{
        "name": "国庆节", "offStart": "2026-10-01", "offEnd": "2026-10-07",
        "makeupDays": [
            {"date": "2026-09-20", "autoWeekday": 2, "followWeekday": 2, "followSource": "auto"},
            {"date": "2026-10-10", "autoWeekday": 3, "followWeekday": None, "followSource": "skip"},
        ],
    }],
    "staleOverrideDates": [],
}


class FakeService:
    def __init__(self):
        self.calls = []

    async def _call(self, binding, method, path, *, params=None, body=None):
        self.calls.append((method, path, body))
        return OVERVIEW


def test_format_overview_lists_periods_and_makeup_days():
    text = format_overview(OVERVIEW)
    assert "国庆节：2026-10-01 至 2026-10-07 放假" in text
    assert "2026-09-20（周日）调休上学，上周二的课" in text
    assert "2026-10-10（周六）调休上学，不补课" in text


def test_format_overview_reports_disabled_and_errors():
    text = format_overview({**OVERVIEW, "enabled": False, "status": {"lastError": "网络错误"}})
    assert "已关闭" in text
    assert "网络错误" in text


@pytest.mark.parametrize("word,expected", [
    ("周三", ("weekday", 3)), ("星期五", ("weekday", 5)), ("1", ("weekday", 1)),
    ("不补课", ("skip", None)), ("自动", ("auto", None)),
])
def test_parse_follow(word, expected):
    assert parse_follow(word) == expected


def test_parse_follow_rejects_weekend():
    with pytest.raises(ValueError):
        parse_follow("周六")


def test_set_makeup_calls_put_delete():
    service = FakeService()
    asyncio.run(set_makeup(service, {}, "2026-10-10", "周五"))
    asyncio.run(set_makeup(service, {}, "2026-10-10", "不补课"))
    asyncio.run(set_makeup(service, {}, "2026-10-10", "自动"))
    assert service.calls == [
        ("PUT", "/api/admin/holidays/overrides/2026-10-10", {"followWeekday": 5}),
        ("PUT", "/api/admin/holidays/overrides/2026-10-10", {"followWeekday": None}),
        ("DELETE", "/api/admin/holidays/overrides/2026-10-10", None),
    ]


def test_set_makeup_validates_input_without_calling_server():
    service = FakeService()
    assert "日期格式" in asyncio.run(set_makeup(service, {}, "10-10", "周五"))
    assert "只能是" in asyncio.run(set_makeup(service, {}, "2026-10-10", "周日"))
    assert service.calls == []
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `python -m pytest tests/test_makeup.py -q`
Expected: `ModuleNotFoundError: No module named 'remoteci.makeup'`.

- [ ] **Step 4: Implement makeup.py**

Create `remoteci/makeup.py`:

```python
"""调休补课：查询 RemoteCI 服务端的调休安排，管理员可修改某个调休上学日补哪天的课。

服务端接口：
- GET    /api/holidays
- PUT    /api/admin/holidays/overrides/{date}   {"followWeekday": 1-5 | null}
- DELETE /api/admin/holidays/overrides/{date}   恢复自动推算
"""

from __future__ import annotations

from datetime import date
from typing import TYPE_CHECKING

from .client import RemoteCiError

if TYPE_CHECKING:  # pragma: no cover
    from .service import ChatUser, RemoteCiService

WEEKDAY_NAMES = {1: "周一", 2: "周二", 3: "周三", 4: "周四", 5: "周五"}
DAY_NAMES = "一二三四五六日"
SOURCE_TEXT = {"auto": "自动推算", "manual": "手动指定", "skip": "手动指定", "unresolved": "无法自动推算，需管理员指定"}
WEEKDAY_WORDS = {f"{prefix}{name}": i for i, name in enumerate("一二三四五", 1) for prefix in ("周", "星期", "礼拜")}
WEEKDAY_WORDS.update({str(i): i for i in range(1, 6)})
SKIP_WORDS = {"不补课", "不上课", "跳过", "skip"}
AUTO_WORDS = {"自动", "恢复", "auto"}

MAKEUP_HELP = """调休补课
/rci 调休   近期假期与调休补课安排
/rci 调休 <YYYY-MM-DD> <周一…周五|不补课|自动>   修改补课安排（管理员）"""


def _weekday_label(iso: str) -> str:
    try:
        return "周" + DAY_NAMES[date.fromisoformat(iso).weekday()]
    except ValueError:
        return ""


def format_overview(data: dict) -> str:
    lines = []
    if not data.get("enabled"):
        lines.append("调休自动适配已关闭，教室课表不会随节假日自动调整。")
    periods = data.get("periods") or []
    if not periods:
        lines.append("近期没有法定假期或调休安排。")
    for period in periods:
        head = period.get("name") or "假期"
        if period.get("offStart"):
            head += f"：{period['offStart']} 至 {period['offEnd']} 放假"
        lines.append(head)
        for item in period.get("makeupDays") or []:
            follow = WEEKDAY_NAMES.get(item.get("followWeekday"))
            source = item.get("followSource")
            arrangement = f"上{follow}的课" if follow else ("不补课" if source == "skip" else "未确定补哪天的课")
            lines.append(f"  · {item['date']}（{_weekday_label(item['date'])}）调休上学，{arrangement}"
                         f"（{SOURCE_TEXT.get(source, '')}）")
    if stale := data.get("staleOverrideDates"):
        lines.append("已失效的手动安排：" + "、".join(stale))
    if error := (data.get("status") or {}).get("lastError"):
        lines.append(f"⚠ 最近一次刷新节假日数据失败：{error}")
    return "\n".join(lines)


def parse_follow(word: str) -> tuple[str, int | None]:
    value = (word or "").strip().lower()
    if value in AUTO_WORDS:
        return "auto", None
    if value in SKIP_WORDS:
        return "skip", None
    if value in WEEKDAY_WORDS:
        return "weekday", WEEKDAY_WORDS[value]
    raise ValueError(word)


async def list_makeup(service: "RemoteCiService", binding: dict) -> str:
    return format_overview(await service._call(binding, "GET", "/api/holidays"))


async def set_makeup(service: "RemoteCiService", binding: dict, day: str, follow: str) -> str:
    try:
        iso = date.fromisoformat(day.strip()).isoformat()
    except ValueError:
        return "日期格式应为 YYYY-MM-DD，例如 2026-10-10。"
    try:
        mode, weekday = parse_follow(follow)
    except ValueError:
        return "补课安排只能是 周一…周五、不补课 或 自动。"
    path = f"/api/admin/holidays/overrides/{iso}"
    if mode == "auto":
        data = await service._call(binding, "DELETE", path)
    else:
        data = await service._call(binding, "PUT", path, body={"followWeekday": weekday})
    return "补课安排已更新。\n" + format_overview(data)


async def run_makeup_command(service: "RemoteCiService", user: "ChatUser", args: list[str]) -> str:
    binding = service.require_binding(user)
    try:
        if not args:
            return await list_makeup(service, binding)
        if len(args) != 2:
            return MAKEUP_HELP
        return await set_makeup(service, binding, args[0], args[1])
    except RemoteCiError as ex:
        return f"RemoteCI 请求失败：{ex}"
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `python -m pytest tests/test_makeup.py -q`
Expected: 全部通过。

- [ ] **Step 6: Wire the command and LLM tools**

In `remoteci/commands.py`:
- add `from .makeup import run_makeup_command` next to the swaps import;
- add to `SUBCOMMANDS`: `"makeup": "makeup", "调休": "makeup", "补课": "makeup",`;
- in `HELP`, under "━ 查询 ━" after the `/rci 假期` line add:
  ```
  /rci 调休   近期假期与调休补课安排
  /rci 调休 2026-10-10 周三|不补课|自动   修改补课安排（管理员）
  ```
- in `run_command`, next to the `swap` branch: `if action == "makeup": return await run_makeup_command(service, user, args)`.

In `main.py`, after the `remoteci_swap_decide` tool add:

```python
    @filter.llm_tool(name="remoteci_holidays")
    async def tool_holidays(self, event: AstrMessageEvent):
        """查看近期法定假期与调休安排：哪几天放假（教室课表自动关闭）、哪几天调休上学以及上周几的课。"""
        from .remoteci.makeup import list_makeup

        async def run(user):
            return await list_makeup(self.service, self.service.require_binding(user))
        return await self._tool(event, run)

    @filter.llm_tool(name="remoteci_set_makeup")
    async def tool_set_makeup(self, event: AstrMessageEvent, date: str, follow: str):
        """修改某个调休上学日补哪天的课（需要系统管理员）。修改前先向用户复述日期和安排，得到确认后再调用。

        Args:
            date(string): 调休上学日，格式 YYYY-MM-DD
            follow(string): 周一、周二、周三、周四、周五，或“不补课”，或“自动”（恢复自动推算）
        """
        from .remoteci.makeup import set_makeup

        async def run(user):
            return await set_makeup(self.service, self.service.require_binding(user), date, follow)
        return await self._tool(event, run)
```

Copy the skill reference and link: `cp /d/Files/Codes/Projects/RemoteCI/.claude/worktrees/holiday-makeup/skills/remoteci/references/holidays.md remoteci/skill/references/holidays.md`; then apply the same "## 7. 调休与节假日" paragraph from Task 10 Step 2 to `remoteci/skill/SKILL.md`（只加这一段，不要整份覆盖，避免丢掉主仓库中尚未合并的其他改动）。

- [ ] **Step 7: Run the whole AstrBot test suite**

Run: `python -m pytest -q`
Expected: 全部通过。

- [ ] **Step 8: Commit**

```bash
git add remoteci tests main.py
git commit -m "feat: 新增调休查询与补课安排指令和 LLM 工具"
```

---

### Task 12: 文档站

**Files（在 RemoteCI-Docs 的独立 worktree 中）:**
- Create: `src/guide/holidays.md`
- Modify: `src/guide/features.md`、`src/server/api.md`

- [ ] **Step 1: Create the worktree**

RemoteCI-Docs 主目录里有 codex 未提交的改动，因此在独立 worktree 中修改：

```bash
git -C /d/Files/Codes/Projects/RemoteCI-Docs worktree add ../RemoteCI-Docs-holiday -b feat/holiday-makeup
```

后续步骤都在 `D:\Files\Codes\Projects\RemoteCI-Docs-holiday` 中进行。

- [ ] **Step 2: Write the guide page**

Create `src/guide/holidays.md`:

```markdown
---
title: 调休
icon: calendar-check
order: 4
---

# 调休

RemoteCI 服务端会自动获取国务院公布的法定节假日与调休安排（数据来自开源项目 [holiday-cn](https://github.com/NateScarlet/holiday-cn)），并下发到每台教室电脑：

- **放假日**：ClassIsland 自动关闭课表，不显示课程、不提醒上下课。第二天上学时自动恢复。如果放假当天有老师手动重新打开课表，插件当天不会再关掉；插件也不会去打开别人手动关闭的课表。
- **调休上学日**（原本是周末但要上学）：自动为这一天建立一份临时课表，内容是某个工作日的课。如果这一天已经有人手动安排了课表，插件不会覆盖它。

## 补哪天的课

服务端会自动推算：同一个假期里，调休上学日按时间顺序依次对应这个假期最后几个放掉的工作日。以 2026 年国庆为例，9 月 20 日（周日）上周二的课，10 月 10 日（周六）上周三的课。课表取自调休上学日所在那一周对应工作日的课表，多周轮换按那一周计算。

学校的实际安排不同时，系统管理员可以在 WebUI 左侧导航「调休」页，把每个调休上学日改成上周一至周五的课，或者设为「不补课」。无法自动推算的日期会标红，需要手动指定后才会开课。

## 设置

| 设置 | 说明 |
| --- | --- |
| 启用调休自动适配 | 默认开启。关闭后，插件会撤掉之前建立的临时课表，并恢复它自己关掉的课表开关。 |
| 数据源地址 | 留空时依次使用 jsDelivr 和 GitHub 上的 holiday-cn。也可以填自己维护的同格式文件地址，必须是包含 `{year}` 的 https 地址；填写后只使用这个地址。 |

服务端启动时拉取一次，之后每 12 小时拉取一次，也可以点击「立即刷新」。数据源暂时无法访问时继续使用上次成功获取的数据；教室电脑断网时继续按最后收到的安排生效。

## 版本要求

服务端和插件都需要升级到包含此功能的版本。旧版插件不会收到调休安排，课表保持原样。
```

- [ ] **Step 3: Update features and API pages**

- `src/guide/features.md`：在合适的位置（例如课表相关章节之后）新增"## 调休自动适配"小节，用两三句话说明放假日关闭课表、调休上学日自动开课、管理员可以调整，并链接到 `./holidays.md`。
- `src/server/api.md`：在"### 插件与系统设置"之后新增"### 调休"小节，列出 Task 5 的五个接口（方法、路径、权限、请求体、返回值）。内容与 `skills/remoteci/references/holidays.md` 的两张表保持一致。

- [ ] **Step 4: Build the docs**

Run: `pnpm install --frozen-lockfile && pnpm docs:build`
Expected: 构建成功，没有死链警告。如果 worktree 中没有 `node_modules`，`pnpm install` 会从缓存安装。

- [ ] **Step 5: Commit**

```bash
git add src
git commit -m "docs: 新增调休自动适配说明与接口文档"
```

---

### Task 13: 全量验证与 codex 合并后的收尾（等 codex 提交档案管理后执行）

**Files:**
- Modify: `server/RemoteCI.Server/Services/ConfigurationArchiveService.cs`（快照中加入调休设置与覆盖）
- Regenerate: `server/RemoteCI.Server/Data/Migrations/*_AddHolidayCalendar.*`
- Test: `server/tests/RemoteCI.Server.Tests/RoleAndBackupTests.cs`（追加用例）

- [ ] **Step 1: Full verification before sync**

Run: `dotnet build RemoteCI.slnx -c Release -nodeReuse:false` then `dotnet test RemoteCI.slnx -c Release --no-build -nodeReuse:false`
Expected: 全部通过。如果失败，先排查并修复，再继续。

- [ ] **Step 2: Wait for codex, then sync**

先确认 codex 已经在 main 上提交了档案管理（`git log main --oneline -5` 中能看到 StoredProfiles 相关提交，且主工作区 `git status` 中已没有 `AddStoredProfiles` 未跟踪文件）。在此之前**不要**执行本任务后续步骤，直接向用户报告前面任务的完成情况。

满足条件后，调用 `mcp__ccd_host__sync_with_base_branch`（若不可用则 `git merge main`）同步，按两边的意图解决 `Protocol.cs`、`Program.cs`、`WebSocketHub.cs`、`_Layout.cshtml`、`docs/protocol.md`、`AppDbContext.cs` 中的冲突。

- [ ] **Step 3: Regenerate the migration**

删除本分支的 `*_AddHolidayCalendar.cs`、`*_AddHolidayCalendar.Designer.cs`，把 `AppDbContextModelSnapshot.cs` 恢复成 main 的版本（`git checkout main -- server/RemoteCI.Server/Data/Migrations/AppDbContextModelSnapshot.cs`），再重新执行 Task 3 Step 5 的命令，并同样把 `defaultValue` 改为 `true`。确认新迁移排在档案管理迁移之后，并且只包含调休相关的改动。

- [ ] **Step 4: Include holiday settings in backups (failing test first)**

在 `RoleAndBackupTests.cs` 中仿照现有的备份往返用例新增 `Backup_RestoresHolidaySettingsAndOverrides`：先设置 `enabled=false`、自定义模板、一条覆盖，然后创建备份；把这些值改掉后执行恢复，断言恢复后与备份时一致。运行，确认它失败。

然后在 `ConfigurationArchiveService` 中：
- 快照记录增加可选字段 `HolidaySettingsSnapshot? Holidays = null`（`record HolidaySettingsSnapshot(bool Enabled, string? SourceUrlTemplate, List<HolidayOverrideSnapshot> Overrides)`、`record HolidayOverrideSnapshot(string Date, int? FollowWeekday)`），沿用该文件中其他可选字段的写法，以兼容旧备份；
- 创建备份时从 `SystemMetadata` 和 `HolidayMakeupOverrides` 读取；
- 恢复时，`Holidays` 不为 null 才覆盖（清空覆盖表后写入）；
- 快照版本号按 codex 合并后的最新值加 1，并在 `Validate` 中接受新版本号。

再次运行 `dotnet test server/tests/RemoteCI.Server.Tests -nodeReuse:false --filter "FullyQualifiedName~Backup|FullyQualifiedName~Holiday"`，确认通过。

- [ ] **Step 5: Full verification after sync**

Run: `dotnet build RemoteCI.slnx -c Release -nodeReuse:false` then `dotnet test RemoteCI.slnx -c Release --no-build -nodeReuse:false`
Expected: 全部通过。

- [ ] **Step 6: Commit**

```bash
git add -A server docs
git commit -m "feat(backup): 备份包含调休设置并基于最新 main 重建迁移"
```
