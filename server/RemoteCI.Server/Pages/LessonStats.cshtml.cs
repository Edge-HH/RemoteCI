using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;

namespace RemoteCI.Server.Pages;

/// <summary>课时统计：按课时台账汇总每位老师的实际课时、代课与被代课，可导出 Excel。</summary>
[Authorize]
public sealed class LessonStatsModel(
    UserManager<AppUser> users,
    LessonStatsService stats,
    ClassAccessService access,
    AppDbContext db) : WebPageModel(users)
{
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true, Name = "class")] public Guid? ClassFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? Teacher { get; set; }
    [BindProperty(SupportsGet = true)] public bool IncludeProfile { get; set; }

    public LessonStatsReport Report { get; private set; } = null!;
    public IReadOnlyList<(Guid Id, string Name)> ClassOptions { get; private set; } = [];
    /// <summary>是否查看全部班级（系统管理员或“查看全部课时统计”）。</summary>
    public bool IsAdmin => _scope is null;
    public string? Error { get; private set; }

    /// <summary>可见班级：null 表示全部（系统管理员）。</summary>
    private IReadOnlyList<Guid>? _scope;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireScopeAsync(ct) is { } denied) return denied;
        var (from, to) = Range();
        try { Report = await stats.GetAsync(_scope, from, to, ClassFilter, Blank(Teacher), IncludeProfile, ct); }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
            Report = new LessonStatsReport(ClassSchedulePeriods.DateKey(from), ClassSchedulePeriods.DateKey(to), IncludeProfile, 0, []);
        }
        return Page();
    }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        if (await RequireScopeAsync(ct) is { } denied) return denied;
        var (from, to) = Range();
        var bytes = await stats.ExportAsync(_scope, from, to, ClassFilter, Blank(Teacher), IncludeProfile, ct);
        return File(bytes, OperationsEndpoints.ExcelContentType, $"课时统计-{from:yyyyMMdd}-{to:yyyyMMdd}.xlsx");
    }

    public Dictionary<string, string> ExportRouteValues => new Dictionary<string, string?>
    {
        ["handler"] = "Export",
        ["from"] = Range().From.ToString("yyyy-MM-dd"),
        ["to"] = Range().To.ToString("yyyy-MM-dd"),
        ["class"] = ClassFilter?.ToString(),
        ["teacher"] = Blank(Teacher),
        ["includeProfile"] = IncludeProfile ? "true" : null,
    }.Where(x => x.Value is not null).ToDictionary(x => x.Key, x => x.Value!);

    public (DateOnly From, DateOnly To) Range() => OperationsEndpoints.LessonStatsRange(From, To);

    private async Task<IActionResult?> RequireScopeAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        _scope = await access.GetPermissionScopeAsync(
            CurrentUser.Id, CurrentUser.Role, UserPermissions.ViewLessonStats, ManagementPermissions.ViewAllLessonStats, ct);
        if (_scope is { Count: 0 }) return RedirectToPage("/Denied");
        var query = db.Classrooms.AsNoTracking();
        if (_scope is not null)
        {
            var allowed = _scope.ToList();
            query = query.Where(x => allowed.Contains(x.Id));
        }
        ClassOptions = (await query.OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync(ct))
            .Select(x => (x.Id, x.Name)).ToList();
        return null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
