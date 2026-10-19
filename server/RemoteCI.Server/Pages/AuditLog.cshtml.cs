using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>操作记录：系统管理员查看全部下发到教室端的命令，班主任只看自己担任班主任的班级。</summary>
[Authorize]
public sealed class AuditLogModel(UserManager<AppUser> users, CommandAuditQueryService audit, AppDbContext db)
    : WebPageModel(users)
{
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true, Name = "class")] public Guid? ClassFilter { get; set; }
    [BindProperty(SupportsGet = true)] public int? Command { get; set; }
    [BindProperty(SupportsGet = true)] public string? Result { get; set; }
    [BindProperty(SupportsGet = true)] public long? Before { get; set; }

    public CommandAuditPage Records { get; private set; } = new([], null);
    public IReadOnlyList<(Guid Id, string Name)> ClassOptions { get; private set; } = [];
    public bool IsAdmin => CurrentUser.Role == UserRole.Admin;

    public static IReadOnlyList<CommandKind> CommandOptions { get; } = Enum.GetValues<CommandKind>()
        .Where(x => x != CommandKind.TeacherComing)
        .ToList();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        var scope = await audit.GetScopeAsync(CurrentUser, ct);
        if (scope is { Count: 0 }) return RedirectToPage("/Denied");
        ClassOptions = await LoadClassOptionsAsync(scope, ct);
        Records = await audit.QueryAsync(scope, BuildFilter() with { Before = Before }, ct);
        return Page();
    }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        var scope = await audit.GetScopeAsync(CurrentUser, ct);
        if (scope is { Count: 0 }) return RedirectToPage("/Denied");
        var bytes = await audit.ExportAsync(scope, BuildFilter(), ct);
        return File(bytes, OperationsEndpoints.ExcelContentType, $"操作记录-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
    }

    public Dictionary<string, string> ExportRouteValues => RouteValues(handler: "Export");

    /// <summary>下一页链接沿用当前筛选条件，只替换 before 游标。</summary>
    public Dictionary<string, string> RouteValues(long? before = null, string? handler = null) => new Dictionary<string, string?>
    {
        ["from"] = From?.ToString("yyyy-MM-dd"),
        ["to"] = To?.ToString("yyyy-MM-dd"),
        ["class"] = ClassFilter?.ToString(),
        ["command"] = Command?.ToString(),
        ["result"] = Result,
        ["before"] = before?.ToString(),
        ["handler"] = handler,
    }.Where(x => !string.IsNullOrEmpty(x.Value)).ToDictionary(x => x.Key, x => x.Value!);

    private CommandAuditFilter BuildFilter() => new(
        From is { } from ? LocalMidnight(from) : null,
        To is { } to ? LocalMidnight(to.AddDays(1)) : null,
        ClassFilter,
        null,
        Command,
        Result switch { "success" => true, "failed" => false, _ => null });

    /// <summary>日期按服务端本地时区解释，与页面上显示的本地时间一致。</summary>
    private static DateTimeOffset LocalMidnight(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private async Task<IReadOnlyList<(Guid Id, string Name)>> LoadClassOptionsAsync(
        IReadOnlyList<Guid>? scope, CancellationToken ct)
    {
        var query = db.Classrooms.AsNoTracking();
        if (scope is not null)
        {
            var allowed = scope.ToList();
            query = query.Where(x => allowed.Contains(x.Id));
        }
        var rows = await query.OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync(ct);
        return rows.Select(x => (x.Id, x.Name)).ToList();
    }
}
