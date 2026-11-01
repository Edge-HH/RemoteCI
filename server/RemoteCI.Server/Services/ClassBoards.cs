using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

public sealed record ClassBoardView(Guid Id, Guid ClassId, string Name, bool ShowTeachers, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt);

/// <summary>新建班牌的结果：链接与二维码只在此时返回一次。</summary>
public sealed record ClassBoardCreated(ClassBoardView Board, string Url, string QrSvg);

public enum BoardLessonState { Done, Current, Upcoming }

public sealed record BoardLesson(string Label, string Subject, string? Teacher, string Time, BoardLessonState State);

/// <summary>班牌一次刷新所需的全部数据，时间均为教室端本地时间。</summary>
public sealed record BoardData(
    string ClassName,
    DateTimeOffset LocalNow,
    string DateText,
    string? HolidayName,
    bool PluginOnline,
    BoardLesson? Current,
    double CurrentProgress,
    BoardLesson? Next,
    string? NextStartsIn,
    IReadOnlyList<BoardLesson> Lessons);

/// <summary>电子班牌的管理（系统管理员或本班班主任）与匿名展示数据。</summary>
public sealed class ClassBoardService(
    AppDbContext db,
    ClassAccessService access,
    ScheduleViewService scheduleViews,
    IStateStore state,
    PeerRegistry peers,
    TimeProvider timeProvider)
{
    public const int MaxBoardsPerClass = 10;
    private static readonly string[] WeekdayNames = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    /// <summary>需要该班的“电子班牌”权限（系统管理员始终拥有）。</summary>
    public async Task<bool> CanManageAsync(AppUser actor, Guid classId, CancellationToken ct = default) =>
        (await access.GetEffectivePermissionsAsync(actor.Id, actor.Role, classId, ct)).HasFlag(UserPermissions.ManageClassBoards) &&
        await db.Classrooms.AnyAsync(x => x.Id == classId, ct);

    public async Task<IReadOnlyList<ClassBoardView>> ListAsync(AppUser actor, Guid classId, CancellationToken ct = default)
    {
        await RequireAsync(actor, classId, ct);
        return (await db.ClassBoards.AsNoTracking().Where(x => x.ClassId == classId).OrderBy(x => x.CreatedAtUnixMs).ToListAsync(ct))
            .Select(ToView).ToList();
    }

    public async Task<ClassBoardCreated> CreateAsync(
        AppUser actor, Guid classId, string? name, bool showTeachers, string baseUrl, CancellationToken ct = default)
    {
        await RequireAsync(actor, classId, ct);
        var trimmed = string.IsNullOrWhiteSpace(name) ? "教室门口班牌" : name.Trim();
        if (trimmed.Length > 40) throw new ArgumentException("班牌名称不能超过 40 个字。");
        if (await db.ClassBoards.CountAsync(x => x.ClassId == classId, ct) >= MaxBoardsPerClass)
            throw new ArgumentException($"每个班级最多 {MaxBoardsPerClass} 个班牌链接，请先撤销不用的链接。");
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var row = new ClassBoard
        {
            Id = Guid.NewGuid(),
            ClassId = classId,
            Name = trimmed,
            TokenHash = CalendarFeedService.Hash(token),
            ShowTeachers = showTeachers,
            CreatedByUserId = actor.Id,
            CreatedAtUnixMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        };
        db.ClassBoards.Add(row);
        await db.SaveChangesAsync(ct);
        var url = $"{baseUrl.TrimEnd('/')}/Board/{token}";
        using var qrData = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var renderer = new SvgQRCode(qrData);
        return new ClassBoardCreated(ToView(row), url, renderer.GetGraphic(4));
    }

    public async Task RevokeAsync(AppUser actor, Guid boardId, CancellationToken ct = default)
    {
        var row = await db.ClassBoards.SingleOrDefaultAsync(x => x.Id == boardId, ct) ?? throw new KeyNotFoundException("班牌链接不存在。");
        await RequireAsync(actor, row.ClassId, ct);
        db.ClassBoards.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>按令牌找到班牌；顺带记录最近访问时间（每分钟最多写一次库）。</summary>
    public async Task<ClassBoard?> ResolveAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return null;
        var hash = CalendarFeedService.Hash(token);
        var row = await db.ClassBoards.Include(x => x.Classroom).SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (row is null) return null;
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (row.LastSeenAtUnixMs is null || now - row.LastSeenAtUnixMs > 60_000)
        {
            row.LastSeenAtUnixMs = now;
            await db.SaveChangesAsync(ct);
        }
        return row;
    }

    public async Task<BoardData> BuildAsync(ClassBoard board, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        var classId = board.ClassId;
        var offset = ClassClock.Offset(state, classId, now);
        var localNow = now.ToOffset(offset);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var day = await scheduleViews.GetAsync(classId, ct) is { } view ? ClassSchedulePeriods.FindDay(view.Bundle, today) : null;
        var lessons = ClassSchedulePeriods.Lessons(day, offset);
        var holiday = day is { Enabled: false } ? day.HolidayName ?? "今天放假" : null;

        BoardLesson Map(LessonSlot slot) => new(
            slot.Course.Label,
            slot.Course.Subject,
            board.ShowTeachers ? slot.Course.Teacher : null,
            $"{slot.StartsAt:HH:mm}–{slot.EndsAt:HH:mm}",
            slot.EndsAt <= now ? BoardLessonState.Done : slot.StartsAt <= now ? BoardLessonState.Current : BoardLessonState.Upcoming);
        var current = lessons.FirstOrDefault(x => x.StartsAt <= now && now < x.EndsAt);
        var next = lessons.FirstOrDefault(x => x.StartsAt > now);
        var progress = current is null ? 0 : Math.Clamp((now - current.StartsAt) / (current.EndsAt - current.StartsAt), 0, 1);
        string? startsIn = next is null ? null : FormatUntil(next.StartsAt - now);
        return new BoardData(
            board.Classroom.Name,
            localNow,
            $"{today:M月d日} {WeekdayNames[(int)today.DayOfWeek]}",
            holiday,
            peers.HasPluginFor(classId),
            current is null ? null : Map(current),
            Math.Round(progress, 3),
            next is null ? null : Map(next),
            startsIn,
            lessons.Select(Map).ToList());
    }

    private static string FormatUntil(TimeSpan span) => span.TotalMinutes < 1 ? "即将开始"
        : span.TotalHours < 1 ? $"{(int)Math.Ceiling(span.TotalMinutes)} 分钟后"
        : $"{(int)span.TotalHours} 小时 {span.Minutes} 分钟后";

    private async Task RequireAsync(AppUser actor, Guid classId, CancellationToken ct)
    {
        if (!await CanManageAsync(actor, classId, ct)) throw new UnauthorizedAccessException("只有系统管理员和本班班主任可以管理班牌链接。");
    }

    private static ClassBoardView ToView(ClassBoard row) => new(
        row.Id, row.ClassId, row.Name, row.ShowTeachers,
        DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUnixMs),
        row.LastSeenAtUnixMs is { } seen ? DateTimeOffset.FromUnixTimeMilliseconds(seen) : null);
}
