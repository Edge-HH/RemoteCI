using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class ScheduleModel(
    UserManager<AppUser> users,
    IStateStore state,
    PeerRegistry peers,
    SchedulePullSettings pullSettings,
    ScheduleSyncService scheduleSync) : WebPageModel(users)
{
    [BindProperty]
    public ScheduleInput Input { get; set; } = new();
    public ScheduleBundle? Bundle { get; private set; }
    public bool PluginOnline => peers.HasPluginFor(CurrentClassId);
    public bool CanPullSchedule => !PluginOnline || peers.PrimaryPluginSupports(CurrentClassId, RemoteCiCapabilities.SchedulePull);
    public bool CanConfigureSchedulePull => ClassPermissions.HasFlag(UserPermissions.ManageSchedule) && CanPullSchedule;
    public bool CanManageSchedule => ClassPermissions.HasFlag(UserPermissions.ManageSchedule) &&
        (!PluginOnline || peers.PrimaryPluginSupports(CurrentClassId, RemoteCiCapabilities.ScheduleChange));
    public ScheduleSyncStatus? CurrentTask => scheduleSync.Current(CurrentClassId);
    [BindProperty]
    public SchedulePullInterval PullInterval { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await RequireAsync() is { } denied) return denied;
        Bundle = state.GetLatestSchedule(CurrentClassId);
        PullInterval = await pullSettings.GetIntervalAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostPullAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.ManageSchedule) is { } classDenied) return classDenied;
        if (PluginOnline && !peers.PrimaryPluginSupports(CurrentClassId, RemoteCiCapabilities.SchedulePull))
        {
            TempData["Error"] = $"{CommandResultCodes.CapabilityUnsupported}：当前班级的插件不支持拉取课表。";
            return RedirectToPage();
        }
        var status = await scheduleSync.StartAndWaitAsync(ScheduleSyncSource.WebUi, CurrentClassId, ct);
        if (status.State == ScheduleSyncTaskState.Completed)
            TempData["Message"] = "已从插件拉取最新课表，并强制覆盖服务端缓存。";
        else
            TempData["Error"] = status.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPullIntervalAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.ManageSchedule) is { } classDenied) return classDenied;
        if (PluginOnline && !peers.PrimaryPluginSupports(CurrentClassId, RemoteCiCapabilities.SchedulePull))
        {
            TempData["Error"] = $"{CommandResultCodes.CapabilityUnsupported}：当前班级的插件不支持拉取课表。";
            return RedirectToPage();
        }
        if (!Enum.IsDefined(PullInterval))
        {
            TempData["Error"] = "请选择有效的自动拉取间隔。";
            return RedirectToPage();
        }
        await pullSettings.SetIntervalAsync(PullInterval, ct);
        TempData["Message"] = PullInterval == SchedulePullInterval.Disabled
            ? "已关闭定时拉取课表。"
            : "自动拉取课表间隔已保存。";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.ManageSchedule) is { } classDenied) return classDenied;
        var sourceDay = state.GetLatestSchedule(CurrentClassId)?.Days.FirstOrDefault(day =>
            day.Enabled &&
            string.Equals(day.Date, Input.Date, StringComparison.Ordinal) &&
            string.Equals(day.Revision, Input.ExpectedRevision, StringComparison.Ordinal));
        var sourceCourse = sourceDay?.Courses.FirstOrDefault(course =>
            course.Enabled && course.Index == Input.SourceIndex);
        if (sourceDay is null || sourceCourse is null)
        {
            TempData["Error"] = "请选择有效的日期和原节次。";
            return RedirectToPage();
        }

        int? targetIndex = null;
        if (Input.Mode == ScheduleChangeMode.Exchange)
        {
            var targetCourse = sourceDay.Courses.FirstOrDefault(course =>
                course.Enabled && course.Index == Input.TargetIndex);
            if (targetCourse is null || targetCourse.Index == sourceCourse.Index)
            {
                TempData["Error"] = "请选择与原节次同一天的其他目标节次。";
                return RedirectToPage();
            }
            targetIndex = targetCourse.Index;
        }
        var result = await peers.SendCommandAndWaitAsync(new CommandMessage
        {
            Command = CommandKind.ChangeSchedule,
            ClassId = CurrentClassId,
            RequestedBy = new UserProfile
            {
                Id = CurrentUser.Id,
                Username = CurrentUser.UserName!,
                DisplayName = CurrentUser.DisplayName,
                Role = CurrentUser.Role,
                GrantedPermissions = CurrentUser.GrantedPermissions,
                Permissions = ClassPermissions,
                Version = CurrentUser.Version,
            },
            ScheduleChange = new ScheduleChangeRequest
            {
                Date = sourceDay.Date,
                Mode = Input.Mode,
                SourceIndex = sourceCourse.Index,
                TargetIndex = targetIndex,
                ReplacementSubjectId = Input.Mode == ScheduleChangeMode.Replace ? Input.ReplacementSubjectId : null,
                ExpectedRevision = sourceDay.Revision,
                Permanent = Input.Permanent,
            },
        }, CurrentClassId, TimeSpan.FromSeconds(15), ct);
        TempData[result.Success ? "Message" : "Error"] = result.Message;
        return RedirectToPage();
    }

    public sealed class ScheduleInput
    {
        public string Date { get; set; } = string.Empty;
        public string ExpectedRevision { get; set; } = string.Empty;
        public int? SourceIndex { get; set; }
        public ScheduleChangeMode Mode { get; set; } = ScheduleChangeMode.Exchange;
        public int? TargetIndex { get; set; }
        public Guid? ReplacementSubjectId { get; set; }
        public bool Permanent { get; set; }
    }
}
