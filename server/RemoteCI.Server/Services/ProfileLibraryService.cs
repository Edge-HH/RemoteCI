using System.Text.Json.Nodes;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>档案库唯一读写入口；页面提供的 ID、班级和修订号均在此重新核对。</summary>
public sealed class ProfileLibraryService(AppDbContext db, ClassAccessService access)
{
    public async Task<bool> CanManageClassAsync(AppUser actor, Guid classId, CancellationToken ct = default) =>
        await access.CanAccessAsync(actor.Id, actor.Role, classId, ct) &&
        (actor.Role == UserRole.Admin ||
         await access.IsClassAdminAsync(actor.Id, actor.Role, classId, ct) &&
         (await access.GetEffectivePermissionsAsync(actor.Id, actor.Role, classId, actor.GrantedPermissions, ct))
             .HasFlag(UserPermissions.ManageSchedule));

    public async Task<IReadOnlyList<StoredProfileDto>> ListAsync(AppUser actor, Guid? onlyClass = null, CancellationToken ct = default)
    {
        if (onlyClass is { } classId) await RequireClassAsync(actor, classId, ct);
        else if (actor.Role != UserRole.Admin) throw Denied();
        var query = db.StoredProfiles.AsNoTracking();
        if (onlyClass is not null) query = query.Where(x => x.ClassId == onlyClass);
        var rows = await query.ToListAsync(ct);
        return rows.OrderBy(x => x.Name).Select(ToDto).ToList();
    }

    public async Task<StoredProfileDto> GetAsync(AppUser actor, Guid id, Guid? onlyClass = null, CancellationToken ct = default)
    {
        var row = await FindAuthorizedAsync(actor, id, onlyClass, ct);
        return ToDto(row);
    }

    public static ProfilePreview Preview(string json)
    {
        var root = ProfileDocument.Parse(json);
        return new ProfilePreview(Field(root, "Name") is JsonValue name && name.TryGetValue<string>(out var text) ? text : "新档案",
            Count(root, "TimeLayouts"), Count(root, "ClassPlans"), Count(root, "Subjects"), ProfileDocument.Validate(root));
    }

    public async Task<IReadOnlyList<StoredProfileDto>> SaveAsync(
        AppUser actor, IReadOnlyList<ProfileSaveItem> items, Guid? onlyClass = null, CancellationToken ct = default)
    {
        if (items.Count is < 1 or > 100) throw new ArgumentException("每次请选择 1 至 100 份档案保存。");
        if (items.Where(x => x.Id is not null).GroupBy(x => x.Id).Any(x => x.Count() > 1) ||
            items.Where(x => x.ClassId is not null).GroupBy(x => x.ClassId).Any(x => x.Count() > 1))
            throw new ArgumentException("同一份档案或班级不能重复提交。");

        // 先解析整个批次，任一无效都不进行数据库写入。
        var documents = items.Select(item =>
        {
            var name = item.Name?.Trim() ?? string.Empty;
            if (name.Length is < 1 or > 100) throw new ArgumentException("档案名称须为 1 至 100 个字。");
            var root = ProfileDocument.Parse(item.ProfileJson);
            var nameKey = root.Select(x => x.Key).FirstOrDefault(x => string.Equals(x, "Name", StringComparison.OrdinalIgnoreCase)) ?? "Name";
            root[nameKey] = name;
            var errors = ProfileDocument.Validate(root);
            if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors.Take(10)));
            var json = ProfileDocument.Serialize(root);
            if (Encoding.UTF8.GetByteCount(json) > ProfileDocument.MaxUtf8Bytes)
                throw new ArgumentException("保存后的档案 JSON 不能超过 5 MB。");
            return (Name: name, Json: json);
        }).ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var saved = new List<StoredProfile>();
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (onlyClass is not null && item.ClassId != onlyClass) throw Denied();
                if (item.ClassId is { } classId) await RequireClassAsync(actor, classId, ct);
                else if (actor.Role != UserRole.Admin) throw Denied();

                StoredProfile row;
                if (item.Id is { } id)
                {
                    row = await FindAuthorizedAsync(actor, id, onlyClass, ct);
                    if (row.ClassId != item.ClassId) throw new ArgumentException("不能改变已有档案所属班级。");
                    RequireRevision(row, item.Revision);
                }
                else
                {
                    if (item.Revision != 0) throw Conflict();
                    if (item.ClassId is not null && await db.StoredProfiles.AnyAsync(x => x.ClassId == item.ClassId, ct))
                        throw Conflict();
                    row = new StoredProfile { ClassId = item.ClassId };
                    db.StoredProfiles.Add(row);
                }

                if (item.SourceTemplateId != row.SourceTemplateId)
                {
                    // 班主任可保留来源，但不能借模板 ID 获取其他档案或更改来源关系。
                    if (actor.Role != UserRole.Admin) throw Denied();
                    if (item.SourceTemplateId is { } templateId &&
                        !await db.StoredProfiles.AnyAsync(x => x.Id == templateId && x.ClassId == null, ct))
                        throw new ArgumentException("来源模板不存在。");
                    if (item.ClassId is null && item.SourceTemplateId is not null)
                        throw new ArgumentException("全局模板不能引用来源模板。");
                    row.SourceTemplateId = item.SourceTemplateId;
                }
                row.Name = documents[index].Name;
                row.ProfileJson = documents[index].Json;
                if (item.Id is not null) row.Revision++;
                row.UpdatedAt = DateTimeOffset.UtcNow;
                saved.Add(row);
            }
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { throw Conflict(); }
            await transaction.CommitAsync(ct);
            return saved.Select(ToDto).ToList();
        }
        catch
        {
            // 回滚数据库之外也清除失败批次的跟踪状态，避免同一作用域稍后保存时夹带旧变更。
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<StoredProfileDto> CopyAsync(AppUser actor, ProfileIdRequest input, CancellationToken ct = default)
    {
        if (actor.Role != UserRole.Admin) throw Denied();
        var original = await GetAsync(actor, input.Id, ct: ct);
        if (original.ClassId is not null) throw new ArgumentException("请从全局模板创建模板副本。");
        if (original.Revision != input.Revision) throw Conflict();
        return (await SaveAsync(actor, [new ProfileSaveItem
        {
            Name = string.IsNullOrWhiteSpace(input.Name) ? $"{original.Name} 副本" : input.Name,
            ProfileJson = original.ProfileJson,
        }], ct: ct))[0];
    }

    public async Task DeleteAsync(AppUser actor, ProfileIdRequest input, Guid? onlyClass = null, CancellationToken ct = default)
    {
        var row = await FindAuthorizedAsync(actor, input.Id, onlyClass, ct);
        RequireRevision(row, input.Revision);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (row.ClassId is null)
        {
            // 删除模板保留独立副本，同时递增副本修订号，让旧页面得到冲突提示而不是伪造来源错误。
            var copies = await db.StoredProfiles.Where(x => x.SourceTemplateId == row.Id).ToListAsync(ct);
            foreach (var copy in copies)
            {
                copy.SourceTemplateId = null;
                copy.Revision++;
                copy.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        db.StoredProfiles.Remove(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
        await transaction.CommitAsync(ct);
    }

    private async Task<StoredProfile> FindAuthorizedAsync(AppUser actor, Guid id, Guid? onlyClass, CancellationToken ct)
    {
        var row = await db.StoredProfiles.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("档案不存在。");
        if (onlyClass is not null && row.ClassId != onlyClass) throw Denied();
        if (row.ClassId is { } classId) await RequireClassAsync(actor, classId, ct);
        else if (actor.Role != UserRole.Admin) throw Denied();
        return row;
    }

    private async Task RequireClassAsync(AppUser actor, Guid classId, CancellationToken ct)
    {
        if (!await CanManageClassAsync(actor, classId, ct)) throw Denied();
    }
    private static JsonNode? Field(JsonObject root, string key) => root.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
    private static int Count(JsonObject root, string key) => (Field(root, key) as JsonObject)?.Count ?? 0;
    private static void RequireRevision(StoredProfile row, long revision) { if (row.Revision != revision) throw Conflict(); }
    private static UnauthorizedAccessException Denied() => new("没有管理此班级档案的权限。");
    private static ProfileRevisionException Conflict() => new("档案已被其他操作修改，请重新加载；当前草稿仍保留。");
    public static StoredProfileDto ToDto(StoredProfile row) => new(row.Id, row.Name, row.ClassId, row.SourceTemplateId,
        row.Revision, row.ProfileJson, row.UpdatedAt);
}

public sealed class ProfileRevisionException(string message) : Exception(message);
public sealed record StoredProfileDto(Guid Id, string Name, Guid? ClassId, Guid? SourceTemplateId, long Revision, string ProfileJson, DateTimeOffset UpdatedAt);
public sealed record ProfilePreview(string Name, int TimeLayoutCount, int ClassPlanCount, int SubjectCount, IReadOnlyList<string> Errors);
public sealed class ProfileSaveItem
{
    public Guid? Id { get; set; }
    public Guid? ClassId { get; set; }
    public Guid? SourceTemplateId { get; set; }
    public long Revision { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ProfileJson { get; set; } = string.Empty;
}
public sealed class ProfileIdRequest
{
    public Guid Id { get; set; }
    public long Revision { get; set; }
    public string? Name { get; set; }
}
