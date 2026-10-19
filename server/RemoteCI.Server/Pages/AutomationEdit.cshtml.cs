using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>新建或编辑一条自动化规则；最多 3 个动作，按顺序执行。</summary>
[Authorize]
public sealed class AutomationEditModel(
    UserManager<AppUser> users,
    AutomationRuleService rules,
    ClassroomService classrooms) : WebPageModel(users)
{
    public const int ActionSlots = 3;

    [BindProperty(SupportsGet = true)] public Guid? Id { get; set; }
    [BindProperty(SupportsGet = true)] public string? Template { get; set; }
    [BindProperty] public RuleInput Input { get; set; } = new();

    public IReadOnlyList<ClassDetail> Classes { get; private set; } = [];
    public IReadOnlyList<ClassGroupInfo> Groups { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        try
        {
            Input = Id is { } id ? RuleInput.From(await rules.GetAsync(id, ct)) : RuleInput.FromTemplate(Template);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        await LoadOptionsAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (await RequireAdminAsync() is { } denied) return denied;
        try
        {
            var dto = Input.ToDto();
            dto.Id = Id;
            await rules.SaveAsync(CurrentUser, dto, ct);
            TempData["Message"] = $"规则「{dto.Name}」已保存。";
            return RedirectToPage("/Automation");
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or FormatException)
        {
            Error = ex.Message;
            await LoadOptionsAsync(ct);
            return Page();
        }
    }

    private async Task LoadOptionsAsync(CancellationToken ct)
    {
        Classes = await classrooms.ListAsync(ct);
        Groups = await classrooms.ListGroupsAsync(ct);
        while (Input.Actions.Count < ActionSlots) Input.Actions.Add(new ActionInput());
    }

    private async Task<IActionResult?> RequireAdminAsync()
    {
        if (await RequireAsync() is { } denied) return denied;
        return CurrentUser.Role == UserRole.Admin ? null : RedirectToPage("/Denied");
    }

    public sealed class RuleInput
    {
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public AutomationTriggerKind TriggerKind { get; set; } = AutomationTriggerKind.Schedule;
        public List<int> Weekdays { get; set; } = [];
        public string? Time { get; set; } = "17:30";
        public bool SchoolDaysOnly { get; set; } = true;
        public ClassEventKind Event { get; set; } = ClassEventKind.OnAfterSchool;
        public string? Subject { get; set; }
        public int DelayMinutes { get; set; }
        public int DelaySeconds { get; set; } = 30;
        public List<Guid> ClassIds { get; set; } = [];
        public List<Guid> GroupIds { get; set; } = [];
        public string? ValidFrom { get; set; }
        public string? ValidTo { get; set; }
        public List<ActionInput> Actions { get; set; } = [];

        public AutomationRuleDto ToDto() => new()
        {
            Name = Name,
            Enabled = Enabled,
            Trigger = new AutomationTrigger
            {
                Kind = TriggerKind,
                Weekdays = TriggerKind == AutomationTriggerKind.Schedule ? Weekdays : [],
                Time = TriggerKind == AutomationTriggerKind.Schedule ? Time : null,
                SchoolDaysOnly = TriggerKind == AutomationTriggerKind.Schedule && SchoolDaysOnly,
                Event = TriggerKind == AutomationTriggerKind.ClassEvent ? Event : null,
                Subject = TriggerKind == AutomationTriggerKind.ClassEvent ? Subject : null,
                DelayMinutes = TriggerKind == AutomationTriggerKind.ClassEvent ? DelayMinutes : 0,
                DelaySeconds = TriggerKind == AutomationTriggerKind.DeviceOnline ? DelaySeconds : 0,
            },
            Target = new AutomationTarget { ClassIds = ClassIds, GroupIds = GroupIds },
            Actions = Actions.Where(x => x.Kind != 0).Select(x => x.ToAction()).ToList(),
            ValidFrom = ValidFrom,
            ValidTo = ValidTo,
        };

        public static RuleInput From(AutomationRuleDto dto) => new()
        {
            Name = dto.Name,
            Enabled = dto.Enabled,
            TriggerKind = dto.Trigger.Kind,
            Weekdays = dto.Trigger.Weekdays.Count > 0 ? dto.Trigger.Weekdays : [1, 2, 3, 4, 5],
            Time = dto.Trigger.Time ?? "17:30",
            SchoolDaysOnly = dto.Trigger.SchoolDaysOnly,
            Event = dto.Trigger.Event ?? ClassEventKind.OnAfterSchool,
            Subject = dto.Trigger.Subject,
            DelayMinutes = dto.Trigger.DelayMinutes,
            DelaySeconds = dto.Trigger.DelaySeconds,
            ClassIds = dto.Target.ClassIds,
            GroupIds = dto.Target.GroupIds,
            ValidFrom = dto.ValidFrom,
            ValidTo = dto.ValidTo,
            Actions = dto.Actions.Select(ActionInput.From).ToList(),
        };

        /// <summary>页面上的常用模板；目标班级仍需管理员选择。</summary>
        public static RuleInput FromTemplate(string? template) => template switch
        {
            "shutdown" => new RuleInput
            {
                Name = "放学 30 分钟后关机",
                TriggerKind = AutomationTriggerKind.ClassEvent,
                Event = ClassEventKind.OnAfterSchool,
                DelayMinutes = 30,
                Weekdays = [1, 2, 3, 4, 5],
                Actions =
                [
                    new ActionInput { Kind = AutomationActionKind.Notification, Title = "电脑即将关机", Message = "已放学 30 分钟，教室电脑将在 1 分钟后关机，请保存好文件。", DurationSeconds = 20 },
                    new ActionInput { Kind = AutomationActionKind.Power, PowerAction = PowerActionKind.Shutdown },
                ],
            },
            "friday" => new RuleInput
            {
                Name = "周五放学安全提醒",
                TriggerKind = AutomationTriggerKind.Schedule,
                Weekdays = [5],
                Time = "16:30",
                Actions =
                [
                    new ActionInput { Kind = AutomationActionKind.Notification, Title = "周末安全提醒", Message = "周末注意交通安全，不去危险水域游泳，按时完成作业。", DurationSeconds = 30, Speech = true },
                ],
            },
            "noon" => new RuleInput
            {
                Name = "午休降低音量",
                TriggerKind = AutomationTriggerKind.Schedule,
                Weekdays = [1, 2, 3, 4, 5],
                Time = "12:00",
                Actions = [new ActionInput { Kind = AutomationActionKind.Volume, VolumeLevel = 20 }],
            },
            "volume" => new RuleInput
            {
                Name = "开机后恢复音量",
                TriggerKind = AutomationTriggerKind.DeviceOnline,
                DelaySeconds = 30,
                Weekdays = [1, 2, 3, 4, 5],
                Actions = [new ActionInput { Kind = AutomationActionKind.Volume, VolumeLevel = 60 }],
            },
            _ => new RuleInput { Weekdays = [1, 2, 3, 4, 5] },
        };
    }

    public sealed class ActionInput
    {
        public AutomationActionKind Kind { get; set; }
        public string? Title { get; set; }
        public string? Message { get; set; }
        public int DurationSeconds { get; set; } = 10;
        public bool Speech { get; set; }
        public bool Sound { get; set; } = true;
        public bool Effect { get; set; } = true;
        public bool ShowMainMenu { get; set; } = true;
        public int? VolumeLevel { get; set; }
        public bool Mute { get; set; }
        public PowerActionKind PowerAction { get; set; } = PowerActionKind.Shutdown;
        public string? ExtensionId { get; set; }
        /// <summary>扩展参数，每行一个 key=value。</summary>
        public string? ExtensionArgs { get; set; }
        public bool ReplaceTempLayers { get; set; }

        public AutomationAction ToAction() => Kind switch
        {
            AutomationActionKind.Notification => new AutomationAction
            {
                Kind = Kind,
                Notification = new NotificationRequest
                {
                    Title = Title?.Trim() ?? string.Empty,
                    Message = Message?.Trim() ?? string.Empty,
                    DurationSeconds = DurationSeconds,
                    IsSpeechEnabled = Speech,
                    IsNotificationSoundEnabled = Sound,
                    IsNotificationEffectEnabled = Effect,
                },
            },
            AutomationActionKind.MainMenu => new AutomationAction { Kind = Kind, MainMenuVisible = ShowMainMenu },
            AutomationActionKind.Volume => new AutomationAction
            {
                Kind = Kind,
                Volume = Mute ? new VolumeControlRequest { Muted = true }
                    : new VolumeControlRequest { Level = VolumeLevel, Muted = VolumeLevel is null ? null : false },
            },
            AutomationActionKind.Power => new AutomationAction { Kind = Kind, PowerAction = PowerAction },
            AutomationActionKind.Extension => new AutomationAction
            {
                Kind = Kind,
                ExtensionId = ExtensionId,
                ExtensionArgs = ParseArgs(ExtensionArgs),
            },
            AutomationActionKind.TempLayers => new AutomationAction { Kind = Kind, ReplaceExistingTempLayers = ReplaceTempLayers },
            _ => new AutomationAction { Kind = Kind },
        };

        public static ActionInput From(AutomationAction action) => new()
        {
            Kind = action.Kind,
            Title = action.Notification?.Title,
            Message = action.Notification?.Message,
            DurationSeconds = action.Notification?.DurationSeconds is > 0 and var duration ? duration : 10,
            Speech = action.Notification?.IsSpeechEnabled == true,
            Sound = action.Notification?.IsNotificationSoundEnabled ?? true,
            Effect = action.Notification?.IsNotificationEffectEnabled ?? true,
            ShowMainMenu = action.MainMenuVisible ?? true,
            VolumeLevel = action.Volume?.Level,
            Mute = action.Volume?.Muted == true,
            PowerAction = action.PowerAction ?? PowerActionKind.Shutdown,
            ExtensionId = action.ExtensionId,
            ExtensionArgs = action.ExtensionArgs is null ? null
                : string.Join('\n', action.ExtensionArgs.Select(x => $"{x.Key}={x.Value}")),
            ReplaceTempLayers = action.ReplaceExistingTempLayers,
        };

        private static Dictionary<string, string?> ParseArgs(string? text)
        {
            var result = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var line in (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) throw new ArgumentException($"扩展参数“{line}”应写成 key=value。");
                result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
            return result;
        }
    }
}
