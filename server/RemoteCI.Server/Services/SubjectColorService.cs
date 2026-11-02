using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 学科颜色（按账号保存，手机与 WebUI 共用）：只保存用户改过的学科，其余按 <see cref="SubjectColors.Default"/> 分配。
/// 列表包含用户能看到的所有班级课表、“我的日程”与自定义课程里出现的学科，以及曾改过颜色的学科。
/// </summary>
public sealed class SubjectColorService(
    AppDbContext db, IStateStore state, ScheduleViewService views, ClassAccessService access, TeacherBindingService teachers)
{
    private static readonly StringComparer PinyinOrder = CreatePinyinOrder();

    /// <summary>用户改过的颜色，键为去首尾空白的学科名。</summary>
    public async Task<IReadOnlyDictionary<string, string>> OverridesAsync(Guid userId, CancellationToken ct = default) =>
        await db.UserSubjectColors.AsNoTracking().Where(x => x.UserId == userId)
            .ToDictionaryAsync(x => x.SubjectName, x => x.Color, StringComparer.Ordinal, ct);

    /// <summary>生效颜色：用户改过的颜色，否则为默认颜色。</summary>
    public static string Resolve(IReadOnlyDictionary<string, string> overrides, string? subject) =>
        overrides.GetValueOrDefault(SubjectColors.Key(subject)) ?? SubjectColors.Default(subject);

    public async Task<SubjectColorsResponse> ListAsync(UserProfile user, string? boundName, CancellationToken ct = default)
    {
        var overrides = await OverridesAsync(user.Id, ct);
        var names = new HashSet<string>(overrides.Keys, StringComparer.Ordinal);
        var classIds = await access.GetAccessibleClassIdsAsync(user.Id, user.Role, ct);
        var estimated = await views.BuildFromProfilesAsync(classIds, ct: ct);
        foreach (var classId in classIds)
            if ((state.GetLatestSchedule(classId) ?? estimated.GetValueOrDefault(classId)?.Bundle) is { } bundle)
                AddNames(names, bundle.Subjects.Select(x => x.Name).Concat(bundle.Days.SelectMany(x => x.Courses).Select(x => x.Subject)));
        if (boundName is not null)
            foreach (var (taught, _) in await teachers.GetDisplayClassesAsync(boundName, null, ct))
                AddNames(names, taught.Bundle.Days.SelectMany(x => x.Courses)
                    .Where(x => x.Enabled && TeacherBindingService.Matches(boundName, x.Teacher)).Select(x => x.Subject));
        AddNames(names, await db.PersonalCourses.AsNoTracking().Where(x => x.UserId == user.Id).Select(x => x.Subject).ToListAsync(ct));
        return new SubjectColorsResponse
        {
            Palette = [.. SubjectColors.Palette],
            Items = names.Order(PinyinOrder)
                .Select(name => new SubjectColorItem { Name = name, Color = Resolve(overrides, name), Custom = overrides.ContainsKey(name) })
                .ToList(),
        };
    }

    public async Task SetAsync(Guid userId, string? subject, string? color, CancellationToken ct = default)
    {
        var name = ValidName(subject);
        var normalized = SubjectColors.NormalizePaletteColor(color)
            ?? throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "颜色需从色板中选择");
        var row = await db.UserSubjectColors.SingleOrDefaultAsync(x => x.UserId == userId && x.SubjectName == name, ct);
        if (row is null) db.UserSubjectColors.Add(row = new UserSubjectColor { UserId = userId, SubjectName = name });
        row.Color = normalized;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>恢复默认颜色；没有改过时什么也不做。</summary>
    public async Task ResetAsync(Guid userId, string? subject, CancellationToken ct = default)
    {
        var name = ValidName(subject);
        await db.UserSubjectColors.Where(x => x.UserId == userId && x.SubjectName == name).ExecuteDeleteAsync(ct);
    }

    private static string ValidName(string? subject)
    {
        var name = SubjectColors.Key(subject);
        if (name.Length is 0 or > UserSubjectColor.MaxSubjectNameLength)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, $"学科名需为 1–{UserSubjectColor.MaxSubjectNameLength} 字");
        return name;
    }

    private static void AddNames(HashSet<string> names, IEnumerable<string?> subjects)
    {
        foreach (var subject in subjects)
            if (SubjectColors.Key(subject) is { Length: > 0 and <= UserSubjectColor.MaxSubjectNameLength } name && name != "未设置")
                names.Add(name);
    }

    /// <summary>按拼音排序；运行在不含 ICU 的环境（不变全球化模式）时退回序号排序。</summary>
    private static StringComparer CreatePinyinOrder()
    {
        try
        {
            return StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), CompareOptions.None);
        }
        catch (CultureNotFoundException)
        {
            return StringComparer.Ordinal;
        }
    }
}
