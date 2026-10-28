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
public sealed class MyScheduleModel(
    UserManager<AppUser> users,
    PersonalScheduleService schedule,
    SemesterSettingsService semester,
    LessonStatsService lessonStats) : WebPageModel(users)
{
    /// <summary>本人本月课时（按姓名匹配课时台账）；没有记录时为 null。</summary>
    public TeacherLessonStats? MonthLessons { get; private set; }
    public MyScheduleResponse Schedule { get; private set; } = new();
    public List<PersonalCourseView> Courses { get; private set; } = [];
    public PersonalCourseView? Editing { get; private set; }
    /// <summary>页面打开时弹出课程编辑窗：修改某门课、点了“添加”或保存失败。</summary>
    public bool OpenEditor => Editing is not null || AddRequested || Error is not null;
    private bool AddRequested { get; set; }
    /// <summary>自定义课程列表：单次课程按日期在前，每周课程按周一到周日、开始时间排序。</summary>
    public IEnumerable<PersonalCourseView> SortedCourses => Courses
        .OrderBy(x => x.Date is null ? 1 : 0)
        .ThenBy(x => x.Date, StringComparer.Ordinal)
        .ThenBy(x => x.WeekDay is { } day ? Array.IndexOf(WeekDayOrder, day) : WeekDayOrder.Length)
        .ThenBy(x => x.StartTime, StringComparer.Ordinal)
        .ThenBy(x => x.Subject, StringComparer.Ordinal);
    [BindProperty] public string Subject { get; set; } = "";
    [BindProperty] public string? Location { get; set; }
    [BindProperty] public int WeekDay { get; set; } = 1;
    [BindProperty] public string StartTime { get; set; } = "08:00";
    [BindProperty] public string EndTime { get; set; } = "08:45";
    /// <summary>周次，格式为“第几周/轮换总周数”，0/2 表示每周。</summary>
    [BindProperty] public string Rotation { get; set; } = "0/2";
    [BindProperty] public int? TimeZoneOffsetMinutes { get; set; }
    [BindProperty] public int? ReminderMinutes { get; set; } = 10;
    [BindProperty] public bool NotifyAtStart { get; set; } = true;
    public string? Error { get; private set; }
    public SemesterSettingsView Semester { get; private set; } = new();

    /// <summary>周次选项，与档案课表的 WeekCountDiv / WeekCountDivTotal 对应。</summary>
    public static readonly (string Value, string Label)[] RotationOptions =
    [
        ("0/2", "每周"), ("1/2", "单周"), ("2/2", "双周"),
        ("1/3", "三周轮换 · 第 1 周"), ("2/3", "三周轮换 · 第 2 周"), ("3/3", "三周轮换 · 第 3 周"),
        ("1/4", "四周轮换 · 第 1 周"), ("2/4", "四周轮换 · 第 2 周"), ("3/4", "四周轮换 · 第 3 周"), ("4/4", "四周轮换 · 第 4 周"),
    ];
    public static readonly int[] WeekDayOrder = [1, 2, 3, 4, 5, 6, 0];
    public string MobileRevision => CalendarFeedService.Hash(System.Text.Json.JsonSerializer.Serialize(Schedule.Days, JsonDefaults.Options));

    public async Task<IActionResult> OnGetRevisionAsync(CancellationToken ct)
    {
        if (await RequireAsync() is not null) return Unauthorized();
        await LoadAsync(ct);
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(new { revision = MobileRevision });
    }

    public async Task<IActionResult> OnGetAsync(Guid? edit, bool add, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        AddRequested = add;
        await LoadAsync(ct);
        var (from, to) = OperationsEndpoints.LessonStatsRange(null, null);
        MonthLessons = (await lessonStats.GetAsync(null, from, to, teacher: CurrentUser.DisplayName, ct: ct)).Teachers
            .FirstOrDefault(x => TeacherBindingService.Matches(CurrentUser.DisplayName, x.Teacher));
        Editing = Courses.FirstOrDefault(x => x.Id == edit);
        if (Editing is { } course)
        {
            Subject = course.Subject; Location = course.Location;
            WeekDay = course.WeekDay ?? 1; StartTime = course.StartTime ?? StartTime; EndTime = course.EndTime ?? EndTime;
            Rotation = $"{course.WeekCountDiv}/{(course.WeekCountDiv == 0 ? 2 : course.WeekCountDivTotal)}";
            TimeZoneOffsetMinutes = course.TimeZoneOffsetMinutes;
            ReminderMinutes = course.ReminderMinutes; NotifyAtStart = course.NotifyAtStart;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(Guid? id, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        var rotation = (Rotation ?? "").Split('/');
        if (!ModelState.IsValid || rotation.Length != 2 ||
            !int.TryParse(rotation[0], NumberStyles.None, CultureInfo.InvariantCulture, out var div) ||
            !int.TryParse(rotation[1], NumberStyles.None, CultureInfo.InvariantCulture, out var total))
            Error = "请选择有效的星期、时间和周次";
        else try
        {
            await schedule.SaveAsync(CurrentUser.Id, id, new PersonalCourseRequest
            {
                Subject = Subject, Location = Location ?? "", WeekDay = WeekDay, StartTime = StartTime, EndTime = EndTime,
                WeekCountDiv = div, WeekCountDivTotal = total, TimeZoneOffsetMinutes = TimeZoneOffsetMinutes,
                ReminderMinutes = ReminderMinutes, NotifyAtStart = NotifyAtStart,
            }, ct);
            return RedirectToPage();
        }
        catch (IdentityOperationException ex) { Error = ex.Message; }
        await LoadAsync(ct);
        Editing = Courses.FirstOrDefault(x => x.Id == id);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        try { await schedule.DeleteAsync(CurrentUser.Id, id, ct); }
        catch (IdentityOperationException ex) { Error = ex.Message; await LoadAsync(ct); return Page(); }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetCoursesAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        return new JsonResult(await schedule.ListAsync(CurrentUser.Id, ct));
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Courses = await schedule.ListAsync(CurrentUser.Id, ct);
        Semester = await semester.GetViewAsync(ct);
        Schedule = await schedule.BuildAsync(CurrentUser.Id,
            AccountRole.HasPersonalSchedule(CurrentUser.RoleDefinitionId) ? CurrentUser.DisplayName : null, ct: ct);
    }

    public static string FormatDate(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("MM月dd日 dddd", CultureInfo.GetCultureInfo("zh-CN"))
            : value;
}
