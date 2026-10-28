using System.Globalization;
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
    ScheduleSyncService scheduleSync,
    ScheduleViewService scheduleViews,
    ProfileLibraryService profiles) : WebPageModel(users)
{
    [BindProperty]
    public ScheduleInput Input { get; set; } = new();
    public ScheduleBundle? Bundle { get; private set; }
    /// <summary>课表来源；按班级档案推算时只供查看，科目教师与换课需等插件同步实际课表。</summary>
    public ScheduleView? CurrentView { get; private set; }
    public bool FromProfile => CurrentView?.Source == ScheduleViewSource.Profile;
    /// <summary>能否进入本班“档案”页（管理员，或具有本班换课权限的班主任）；不能时不显示档案入口。</summary>
    public bool CanEditClassProfile { get; private set; }
    public bool PluginOnline => peers.HasPluginFor(CurrentClassId);
    // 拉取课表会覆盖服务端缓存，只允许系统管理员或获准的本班班主任使用；其他账号不显示入口。
    public bool CanPullSchedule => ClassSelfService.CanPullSchedule &&
        (!PluginOnline || peers.PrimaryPluginSupports(CurrentClassId, RemoteCiCapabilities.SchedulePull));
    public bool CanManageSchedule => ClassPermissions.HasFlag(UserPermissions.ManageSchedule) &&
        (!PluginOnline || peers.PrimaryPluginSupports(CurrentClassId, RemoteCiCapabilities.ScheduleChange));
    public ScheduleSyncStatus? CurrentTask => scheduleSync.Current(CurrentClassId);
    [BindProperty]
    public SubjectTeacherInput SubjectTeacher { get; set; } = new();

    /// <summary>当前班级是否支持设置科目教师（写入 ClassIsland 档案的 Subject.TeacherName）。</summary>
    public bool CanSetSubjectTeacher => PluginOnline &&
        peers.PrimaryPluginSupports(CurrentClassId, RemoteCiCapabilities.ScheduleSubjectTeacher);

    /// <summary>当前班级课表中出现过的科目及其教师名，供“科目教师”编辑区展示。</summary>
    public IReadOnlyList<SubjectEntry> Subjects => Bundle?.Subjects ?? [];
    public string MobileRevision => CalendarFeedService.Hash(System.Text.Json.JsonSerializer.Serialize(Bundle?.Days, JsonDefaults.Options));
    /// <summary>页头的“申请换课”入口：有老师主动换课权限时显示。</summary>
    public bool CanRequestSwap => Permissions.HasFlag(UserPermissions.RequestScheduleSwap);
    /// <summary>直接换课可选的日期：教室端今天起，至已同步课表的最后一天（至多 30 天，旧版插件只同步 7 天）。</summary>
    public string SwapMinDate { get; private set; } = "";
    public string SwapMaxDate { get; private set; } = "";
    /// <summary>直接换课可选日期的课程，以 JSON 内嵌到页面，由 app.js 按所选日期填充节次。</summary>
    public string SwapDaysJson { get; private set; } = "[]";

    public async Task<IActionResult> OnGetRevisionAsync(CancellationToken ct)
    {
        if (await RequireAsync() is not null) return Unauthorized();
        Bundle = (await scheduleViews.GetAsync(CurrentClassId, ct))?.Bundle;
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(new { revision = MobileRevision });
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await RequireAsync() is { } denied) return denied;
        if (MobileUi.IsMobile(HttpContext) && CurrentUser.RoleDefinitionId == AccountRole.TeacherId && !Request.Query.ContainsKey("class"))
            return RedirectToPage("/Mobile/MySchedule");
        CurrentView = await scheduleViews.GetAsync(CurrentClassId, HttpContext.RequestAborted);
        Bundle = CurrentView?.Bundle;
        CanEditClassProfile = await profiles.CanManageClassAsync(CurrentUser, CurrentClassId, HttpContext.RequestAborted);
        var today = ClassClock.Today(state, CurrentClassId);
        ViewData["ScheduleToday"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (Bundle is not null && !FromProfile) BuildSwapDays(Bundle, today);
        return Page();
    }

    private void BuildSwapDays(ScheduleBundle bundle, DateOnly today)
    {
        var first = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = today.AddDays(ScheduleBundle.HorizonDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var days = bundle.Days
            .Where(day => string.CompareOrdinal(day.Date, first) >= 0 && string.CompareOrdinal(day.Date, end) < 0)
            .OrderBy(day => day.Date, StringComparer.Ordinal)
            .ToList();
        if (days.Count == 0) return;
        SwapMinDate = first;
        SwapMaxDate = days[^1].Date;
        SwapDaysJson = System.Text.Json.JsonSerializer.Serialize(days.Select(day => new
        {
            day.Date,
            day.Revision,
            Courses = day.Enabled
                ? day.Courses.Where(course => course.Enabled).OrderBy(course => course.Index)
                    .Select(course => new { course.Index, course.Label, course.Subject }).ToList()
                : [],
        }), JsonDefaults.Options);
    }

    public async Task<IActionResult> OnPostPullAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!ClassSelfService.CanPullSchedule) return RedirectToPage("/Denied");
        if (PluginOnline && !peers.PrimaryPluginSupports(CurrentClassId, RemoteCiCapabilities.SchedulePull))
        {
            TempData["Error"] = $"{CommandResultCodes.CapabilityUnsupported}：当前班级的插件不支持拉取课表。";
            return RedirectToPage();
        }
        var status = await scheduleSync.StartAndWaitAsync(ScheduleSyncSource.WebUi, CurrentClassId, ct);
        if (status.State == ScheduleSyncTaskState.Completed)
            TempData["Message"] = "已从插件拉取最新课表。";
        else
            TempData["Error"] = status.Message;
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

    /// <summary>管理员为班级科目分配授课教师：下发 SetSubjectTeacher 写入教室端档案，随后课表自动重推。</summary>
    public async Task<IActionResult> OnPostSubjectTeacherAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (RequireClass(UserPermissions.ManageSchedule) is { } classDenied) return classDenied;
        // Razor Page 的 POST 不会执行 OnGetAsync；先读取当前班级课表，
        // 否则 Subjects 仍是空列表，任何合法的科目 Id 都会被误判为不存在。
        Bundle = state.GetLatestSchedule(CurrentClassId);
        if (!CanSetSubjectTeacher)
        {
            TempData["Error"] = $"{CommandResultCodes.CapabilityUnsupported}：当前班级的插件不支持设置科目教师。";
            return RedirectToPage();
        }
        if (Subjects.All(x => x.Id != SubjectTeacher.SubjectId))
        {
            TempData["Error"] = "请选择当前课表中的科目。";
            return RedirectToPage();
        }
        var result = await peers.SendCommandAndWaitAsync(new CommandMessage
        {
            Command = CommandKind.SetSubjectTeacher,
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
            SubjectTeacher = new SubjectTeacherRequest
            {
                SubjectId = SubjectTeacher.SubjectId,
                TeacherName = SubjectTeacher.TeacherName?.Trim(),
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

    public sealed class SubjectTeacherInput
    {
        public Guid SubjectId { get; set; }
        public string? TeacherName { get; set; }
    }
}
