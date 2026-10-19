using System.Globalization;
using System.Text.Json.Serialization;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

public enum AutomationTriggerKind
{
    /// <summary>每周固定星期几的某个时刻，按目标班级的教室端本地时间计算。</summary>
    Schedule = 1,
    /// <summary>目标班级的上课、下课或放学事件，可延迟执行。</summary>
    ClassEvent = 2,
    /// <summary>目标班级的教室电脑插件上线后执行。</summary>
    DeviceOnline = 3,
}

public sealed class AutomationTrigger
{
    [JsonPropertyName("kind")] public AutomationTriggerKind Kind { get; set; } = AutomationTriggerKind.Schedule;

    /// <summary>定时触发的星期（1=周一 … 7=周日）。</summary>
    [JsonPropertyName("weekdays")] public List<int> Weekdays { get; set; } = [];

    /// <summary>定时触发的钟点（HH:mm，教室端本地时间）。</summary>
    [JsonPropertyName("time")] public string? Time { get; set; }

    /// <summary>定时触发只在上学日执行：放假日跳过，调休上学日照常执行。</summary>
    [JsonPropertyName("schoolDaysOnly")] public bool SchoolDaysOnly { get; set; }

    /// <summary>课表事件：1 上课、2 下课、3 放学。</summary>
    [JsonPropertyName("event")] public ClassEventKind? Event { get; set; }

    /// <summary>课表事件只匹配该科目（为空匹配全部）。</summary>
    [JsonPropertyName("subject")] public string? Subject { get; set; }

    [JsonPropertyName("delayMinutes")] public int DelayMinutes { get; set; }

    [JsonPropertyName("delaySeconds")] public int DelaySeconds { get; set; }
}

public sealed class AutomationTarget
{
    [JsonPropertyName("classIds")] public List<Guid> ClassIds { get; set; } = [];
    [JsonPropertyName("groupIds")] public List<Guid> GroupIds { get; set; } = [];
}

public enum AutomationActionKind
{
    Notification = 1,
    ClearNotifications = 2,
    MainMenu = 3,
    Volume = 4,
    Power = 5,
    Extension = 6,
    /// <summary>把该班服务端档案中的临时层下发到教室电脑。</summary>
    TempLayers = 7,
}

public sealed class AutomationAction
{
    [JsonPropertyName("kind")] public AutomationActionKind Kind { get; set; }
    [JsonPropertyName("notification")] public NotificationRequest? Notification { get; set; }
    [JsonPropertyName("mainMenuVisible")] public bool? MainMenuVisible { get; set; }
    [JsonPropertyName("volume")] public VolumeControlRequest? Volume { get; set; }
    [JsonPropertyName("powerAction")] public PowerActionKind? PowerAction { get; set; }
    [JsonPropertyName("extensionId")] public string? ExtensionId { get; set; }
    [JsonPropertyName("extensionArgs")] public Dictionary<string, string?>? ExtensionArgs { get; set; }
    [JsonPropertyName("replaceExistingTempLayers")] public bool ReplaceExistingTempLayers { get; set; }
}

/// <summary>REST 与页面共用的规则视图 / 保存请求。</summary>
public sealed class AutomationRuleDto
{
    [JsonPropertyName("id")] public Guid? Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("trigger")] public AutomationTrigger Trigger { get; set; } = new();
    [JsonPropertyName("actions")] public List<AutomationAction> Actions { get; set; } = [];
    [JsonPropertyName("target")] public AutomationTarget Target { get; set; } = new();
    [JsonPropertyName("validFrom")] public string? ValidFrom { get; set; }
    [JsonPropertyName("validTo")] public string? ValidTo { get; set; }
    [JsonPropertyName("createdByName")] public string? CreatedByName { get; set; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset? UpdatedAt { get; set; }
    [JsonPropertyName("lastRunAt")] public DateTimeOffset? LastRunAt { get; set; }
    [JsonPropertyName("lastSuccess")] public bool? LastSuccess { get; set; }
    [JsonPropertyName("lastResult")] public string? LastResult { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

public sealed record AutomationRunView(
    Guid Id,
    Guid RuleId,
    string RuleName,
    Guid ClassId,
    string ClassName,
    string Trigger,
    DateTimeOffset DueAt,
    DateTimeOffset? FinishedAt,
    string Status,
    string StatusLabel,
    string? Message)
{
    public static AutomationRunView From(AutomationRun row) => new(
        row.Id, row.RuleId, row.RuleName, row.ClassId, row.ClassName, row.TriggerText,
        DateTimeOffset.FromUnixTimeMilliseconds(row.DueAtUnixMs),
        row.FinishedAtUnixMs is { } finished ? DateTimeOffset.FromUnixTimeMilliseconds(finished) : null,
        row.Status.ToString(),
        row.Status switch
        {
            AutomationRunStatus.Pending => "等待执行",
            AutomationRunStatus.Running => "执行中",
            AutomationRunStatus.Succeeded => "成功",
            AutomationRunStatus.Failed => "失败",
            AutomationRunStatus.Missed => "已错过",
            _ => row.Status.ToString(),
        },
        row.Message);
}

/// <summary>规则的校验与可读描述。</summary>
public static class AutomationRules
{
    public const int MaxNameLength = 60;
    public const int MaxActions = 5;
    public const int MaxDelayMinutes = 180;
    public const int MaxDelaySeconds = 600;
    public static readonly string[] WeekdayNames = ["", "周一", "周二", "周三", "周四", "周五", "周六", "周日"];

    public static void Validate(AutomationRuleDto rule)
    {
        var name = rule.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > MaxNameLength) throw new ArgumentException($"规则名称须为 1 至 {MaxNameLength} 个字。");
        rule.Name = name;
        ValidateTrigger(rule.Trigger ?? throw new ArgumentException("请设置触发条件。"));
        if (rule.Target is null || rule.Target.ClassIds.Count + rule.Target.GroupIds.Count == 0)
            throw new ArgumentException("请至少选择一个班级或分组。");
        if (rule.Actions is null || rule.Actions.Count is < 1 or > MaxActions)
            throw new ArgumentException($"请设置 1 至 {MaxActions} 个动作。");
        foreach (var action in rule.Actions) ValidateAction(action);
        rule.ValidFrom = NormalizeDate(rule.ValidFrom, "生效开始日期");
        rule.ValidTo = NormalizeDate(rule.ValidTo, "生效结束日期");
        if (rule.ValidFrom is not null && rule.ValidTo is not null && string.CompareOrdinal(rule.ValidFrom, rule.ValidTo) > 0)
            throw new ArgumentException("生效结束日期不能早于开始日期。");
    }

    private static void ValidateTrigger(AutomationTrigger trigger)
    {
        switch (trigger.Kind)
        {
            case AutomationTriggerKind.Schedule:
                trigger.Weekdays = trigger.Weekdays.Distinct().Order().ToList();
                if (trigger.Weekdays.Count == 0 || trigger.Weekdays.Any(x => x is < 1 or > 7))
                    throw new ArgumentException("请选择定时触发的星期。");
                if (!TryParseTime(trigger.Time, out var time)) throw new ArgumentException("定时触发的时间格式应为 HH:mm。");
                trigger.Time = time.ToString("HH:mm", CultureInfo.InvariantCulture);
                break;
            case AutomationTriggerKind.ClassEvent:
                if (trigger.Event is not (ClassEventKind.OnClass or ClassEventKind.OnBreaking or ClassEventKind.OnAfterSchool))
                    throw new ArgumentException("课表事件只能选择上课、下课或放学。");
                if (trigger.DelayMinutes is < 0 or > MaxDelayMinutes)
                    throw new ArgumentException($"延迟须在 0 至 {MaxDelayMinutes} 分钟之间。");
                trigger.Subject = string.IsNullOrWhiteSpace(trigger.Subject) ? null : trigger.Subject.Trim();
                if (trigger.Subject is { Length: > 40 }) throw new ArgumentException("科目名称过长。");
                break;
            case AutomationTriggerKind.DeviceOnline:
                if (trigger.DelaySeconds is < 0 or > MaxDelaySeconds)
                    throw new ArgumentException($"上线后延迟须在 0 至 {MaxDelaySeconds} 秒之间。");
                break;
            default:
                throw new ArgumentException("不支持的触发方式。");
        }
    }

    private static void ValidateAction(AutomationAction action)
    {
        switch (action.Kind)
        {
            case AutomationActionKind.Notification:
                if (NotificationRequest.Validate(action.Notification) is { } error) throw new ArgumentException(error);
                break;
            case AutomationActionKind.ClearNotifications:
            case AutomationActionKind.TempLayers:
                break;
            case AutomationActionKind.MainMenu:
                if (action.MainMenuVisible is null) throw new ArgumentException("请选择显示或隐藏主界面。");
                break;
            case AutomationActionKind.Volume:
                if (action.Volume is null || (action.Volume.Level is null && action.Volume.Muted is null))
                    throw new ArgumentException("请设置音量或静音。");
                if (action.Volume.Level is < 0 or > 100) throw new ArgumentException("音量须在 0 至 100 之间。");
                break;
            case AutomationActionKind.Power:
                if (action.PowerAction is not { } power || !Enum.IsDefined(power)) throw new ArgumentException("请选择电源操作。");
                break;
            case AutomationActionKind.Extension:
                action.ExtensionId = action.ExtensionId?.Trim();
                if (string.IsNullOrEmpty(action.ExtensionId) || action.ExtensionId.Length > ExtensionId.MaxLength)
                    throw new ArgumentException("请填写扩展 Id。");
                break;
            default:
                throw new ArgumentException("不支持的动作。");
        }
    }

    public static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static string? NormalizeDate(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? ClassSchedulePeriods.DateKey(date)
            : throw new ArgumentException($"{label}格式应为 yyyy-MM-dd。");
    }

    public static string DescribeTrigger(AutomationTrigger trigger) => trigger.Kind switch
    {
        AutomationTriggerKind.Schedule =>
            $"{DescribeWeekdays(trigger.Weekdays)} {trigger.Time}{(trigger.SchoolDaysOnly ? "（仅上学日）" : string.Empty)}",
        AutomationTriggerKind.ClassEvent =>
            $"{(trigger.Subject is null ? string.Empty : trigger.Subject)}{EventLabel(trigger.Event)}" +
            (trigger.DelayMinutes > 0 ? $"后 {trigger.DelayMinutes} 分钟" : "时"),
        AutomationTriggerKind.DeviceOnline =>
            trigger.DelaySeconds > 0 ? $"教室电脑上线后 {trigger.DelaySeconds} 秒" : "教室电脑上线时",
        _ => "未知触发",
    };

    public static string EventLabel(ClassEventKind? kind) => kind switch
    {
        ClassEventKind.OnClass => "上课",
        ClassEventKind.OnBreaking => "下课",
        ClassEventKind.OnAfterSchool => "放学",
        _ => "课表事件",
    };

    private static string DescribeWeekdays(IReadOnlyList<int> weekdays)
    {
        if (weekdays.Count == 7) return "每天";
        if (weekdays.SequenceEqual([1, 2, 3, 4, 5])) return "工作日";
        return string.Join("、", weekdays.Select(x => WeekdayNames[Math.Clamp(x, 0, 7)]));
    }

    public static string DescribeAction(AutomationAction action) => action.Kind switch
    {
        AutomationActionKind.Notification => $"发送通知「{action.Notification?.Title}」",
        AutomationActionKind.ClearNotifications => "清除提醒",
        AutomationActionKind.MainMenu => action.MainMenuVisible == true ? "显示主界面" : "隐藏主界面",
        AutomationActionKind.Volume => action.Volume?.Muted == true ? "静音"
            : action.Volume?.Level is { } level ? $"音量设为 {level}" : "取消静音",
        AutomationActionKind.Power => action.PowerAction switch
        {
            PowerActionKind.Shutdown => "关机",
            PowerActionKind.Restart => "重启",
            PowerActionKind.Sleep => "睡眠",
            PowerActionKind.Hibernate => "休眠",
            _ => "电源操作",
        },
        AutomationActionKind.Extension => $"运行扩展 {action.ExtensionId}",
        AutomationActionKind.TempLayers => "下发班级档案临时层",
        _ => "未知动作",
    };

    public static string Describe(AutomationRuleDto rule) =>
        $"{DescribeTrigger(rule.Trigger)}：{string.Join("，", rule.Actions.Select(DescribeAction))}";
}
