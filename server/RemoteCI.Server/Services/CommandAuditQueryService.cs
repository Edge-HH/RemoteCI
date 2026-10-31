using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

public sealed record CommandAuditFilter(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    Guid? ClassId = null,
    Guid? UserId = null,
    int? Command = null,
    bool? Success = null,
    long? Before = null,
    int Limit = 50);

public sealed record CommandAuditView(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    Guid? ActorUserId,
    string ActorName,
    string Source,
    string SourceLabel,
    string? SourceDetail,
    Guid? ClassId,
    string? ClassName,
    string? DeviceName,
    int Command,
    string CommandLabel,
    string Summary,
    bool? Success,
    string? ResultCode,
    string? ResultMessage);

/// <summary>按时间倒序分页；nextBefore 传回下一页的 before 参数（Unix 毫秒）。</summary>
public sealed record CommandAuditPage(IReadOnlyList<CommandAuditView> Items, long? NextBefore);

/// <summary>
/// 命令审计的查询与导出。系统管理员与拥有“查看全部操作记录”的账号查看全部；其他账号只看自己有“操作记录”权限的班级，
/// 不属于任何班级的记录（例如未分配设备）只在查看全部时可见。
/// </summary>
public sealed class CommandAuditQueryService(AppDbContext db, ClassAccessService access)
{
    public const int MaxPageSize = 200;
    public const int MaxExportRows = 10_000;

    /// <summary>可查看的班级范围：null 表示全部；空列表表示没有查看权限。</summary>
    public Task<IReadOnlyList<Guid>?> GetScopeAsync(AppUser user, CancellationToken ct = default) =>
        access.GetPermissionScopeAsync(user.Id, user.Role, UserPermissions.ViewAuditLog, ManagementPermissions.ViewAllAuditLogs, ct);

    public async Task<CommandAuditPage> QueryAsync(
        IReadOnlyList<Guid>? scope, CommandAuditFilter filter, CancellationToken ct = default)
    {
        var limit = Math.Clamp(filter.Limit, 1, MaxPageSize);
        var rows = await Filter(scope, filter).Take(limit + 1).ToListAsync(ct);
        if (rows.Count <= limit) return new CommandAuditPage(rows.Select(ToView).ToList(), null);
        var boundary = rows[limit].CreatedAtUnixMs;
        rows.RemoveAt(limit);
        // 批量控制会在同一毫秒写入多条记录；游标只有时间，因此把跨页的同毫秒记录整组留给下一页，避免漏掉。
        var kept = rows.Where(x => x.CreatedAtUnixMs != boundary).ToList();
        if (kept.Count > 0) return new CommandAuditPage(kept.Select(ToView).ToList(), boundary + 1);
        // 整页都在同一毫秒：这一毫秒的记录一次全部返回（可能超过 limit），下一页从更早的时间开始。
        var group = await Filter(scope, filter).Where(x => x.CreatedAtUnixMs == boundary).Take(MaxExportRows).ToListAsync(ct);
        var older = await Filter(scope, filter).AnyAsync(x => x.CreatedAtUnixMs < boundary, ct);
        return new CommandAuditPage(group.Select(ToView).ToList(), older ? boundary : null);
    }

    public async Task<byte[]> ExportAsync(IReadOnlyList<Guid>? scope, CommandAuditFilter filter, CancellationToken ct = default)
    {
        var rows = await Filter(scope, filter with { Before = null }).Take(MaxExportRows).ToListAsync(ct);
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("操作记录");
        string[] headers = ["时间", "操作人", "入口", "入口详情", "班级", "设备", "操作", "内容", "结果", "结果说明"];
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        var row = 2;
        foreach (var view in rows.Select(ToView))
        {
            sheet.Cell(row, 1).Value = view.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            sheet.Cell(row, 2).Value = view.ActorName;
            sheet.Cell(row, 3).Value = view.SourceLabel;
            sheet.Cell(row, 4).Value = view.SourceDetail ?? string.Empty;
            sheet.Cell(row, 5).Value = view.ClassName ?? string.Empty;
            sheet.Cell(row, 6).Value = view.DeviceName ?? string.Empty;
            sheet.Cell(row, 7).Value = view.CommandLabel;
            sheet.Cell(row, 8).Value = view.Summary;
            sheet.Cell(row, 9).Value = ResultLabel(view.Success);
            sheet.Cell(row, 10).Value = view.ResultMessage ?? string.Empty;
            row++;
        }
        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, Math.Min(row, 200));
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private IQueryable<CommandAuditEntry> Filter(IReadOnlyList<Guid>? scope, CommandAuditFilter filter)
    {
        var query = db.CommandAuditEntries.AsNoTracking();
        if (scope is not null)
        {
            var allowed = scope.ToList();
            query = query.Where(x => x.ClassId != null && allowed.Contains(x.ClassId.Value));
        }
        if (filter.From is { } from)
        {
            var fromMs = from.ToUnixTimeMilliseconds();
            query = query.Where(x => x.CreatedAtUnixMs >= fromMs);
        }
        if (filter.To is { } to)
        {
            var toMs = to.ToUnixTimeMilliseconds();
            query = query.Where(x => x.CreatedAtUnixMs < toMs);
        }
        if (filter.Before is { } before) query = query.Where(x => x.CreatedAtUnixMs < before);
        if (filter.ClassId is { } classId) query = query.Where(x => x.ClassId == classId);
        if (filter.UserId is { } userId) query = query.Where(x => x.ActorUserId == userId);
        if (filter.Command is { } command) query = query.Where(x => x.Command == command);
        if (filter.Success is { } success)
            query = success ? query.Where(x => x.Success == true) : query.Where(x => x.Success != true);
        return query.OrderByDescending(x => x.CreatedAtUnixMs).ThenByDescending(x => x.Id);
    }

    public static CommandAuditView ToView(CommandAuditEntry row) => new(
        row.Id,
        DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUnixMs),
        row.CompletedAtUnixMs is { } completed ? DateTimeOffset.FromUnixTimeMilliseconds(completed) : null,
        row.ActorUserId,
        row.ActorName,
        row.Source.ToString(),
        SourceLabel(row.Source),
        row.SourceDetail,
        row.ClassId,
        row.ClassName,
        row.DeviceName,
        row.Command,
        CommandAuditSummary.Label((CommandKind)row.Command),
        row.Summary,
        row.Success,
        row.ResultCode,
        row.ResultMessage);

    public static string SourceLabel(CommandAuditSource source) => source switch
    {
        CommandAuditSource.WebUi => "WebUI",
        CommandAuditSource.Mobile => "手机 App",
        CommandAuditSource.Watch => "手表",
        CommandAuditSource.ApiKey => "API Key",
        CommandAuditSource.DeviceRest => "App 接口",
        CommandAuditSource.Automation => "自动化",
        CommandAuditSource.SwapWorkflow => "换课申请",
        CommandAuditSource.DriftCheck => "档案差异检查",
        CommandAuditSource.System => "系统",
        _ => "未知",
    };

    public static string ResultLabel(bool? success) => success switch
    {
        true => "成功",
        false => "失败",
        null => "等待回执",
    };
}
